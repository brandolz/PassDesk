using System.Security.Cryptography;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Core.Services;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Auth;

public sealed record DocumentAddResult(int Added, int Duplicates, int TooLarge, int Failed, IReadOnlyList<string> AddedPaths);

/// <summary>
/// Documentos cifrados de la bóveda (DNI, pasaporte, PDF, fotos…).
/// <list type="bullet">
/// <item>Cada documento tiene su propia clave AES-256 aleatoria, guardada dentro de la bóveda.</item>
/// <item>El contenido cifrado se guarda aparte, con la capa DPAPI de la cuenta de Windows.</item>
/// <item>Para mostrarlo se descifra en memoria fijada que se pone a cero; nunca se escribe sin cifrar en
/// disco, salvo al exportarlo por decisión del usuario.</item>
/// </list>
/// </summary>
public sealed class SecureDocumentService
{
    private readonly VaultSession _session;
    private readonly IDocumentStore _store;
    private readonly IClock _clock;

    public SecureDocumentService(VaultSession session, IDocumentStore store, IClock clock)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IReadOnlyList<SecureDocument> Documents => _session.Documents;

    /// <summary>Cifra y guarda los archivos indicados. Los originales no se tocan.</summary>
    public DocumentAddResult AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int added = 0, duplicates = 0, tooLarge = 0, failed = 0;
        var addedPaths = new List<string>();
        var knownHashes = new HashSet<string>(_session.Documents.Select(d => d.Sha256), StringComparer.Ordinal);

        foreach (string path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    failed++;
                    continue;
                }

                if (info.Length > DocumentCipher.MaxDocumentBytes)
                {
                    tooLarge++;
                    continue;
                }

                using var content = ReadFile(path, (int)info.Length);
                string hash = Convert.ToHexString(SHA256.HashData(content.Span));
                if (!knownHashes.Add(hash))
                {
                    duplicates++;
                    continue;
                }

                var now = _clock.UtcNow;
                var document = new SecureDocument
                {
                    Title = Path.GetFileNameWithoutExtension(info.Name),
                    FileName = info.Name,
                    Size = content.Length,
                    Sha256 = hash,
                    CreatedUtc = now,
                    ModifiedUtc = now,
                };
                document.SetKey(DocumentCipher.NewKey());
                _store.Write(document.Id, DocumentCipher.Seal(document.Key, document.Id, content.Span));

                try
                {
                    _session.Mutate(vault => vault.Documents.Add(document));
                }
                catch
                {
                    _store.Delete(document.Id);
                    document.Dispose();
                    throw;
                }

                added++;
                addedPaths.Add(info.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                AppLog.Error(LogEvent.DocumentAddFailed, ex);
                failed++;
            }
        }

        AppLog.Info(
            LogEvent.DocumentsAdded,
            new LogMetric("added", added),
            new LogMetric("duplicates", duplicates),
            new LogMetric("tooLarge", tooLarge),
            new LogMetric("failed", failed));
        return new DocumentAddResult(added, duplicates, tooLarge, failed, addedPaths);
    }

    /// <summary>Descifra un documento en memoria. El llamador debe liberar el resultado cuanto antes.</summary>
    public SensitiveBuffer Open(Guid id)
    {
        var document = Find(id);
        return DocumentCipher.Open(document.Key, id, _store.Read(id));
    }

    /// <summary>Guarda una copia descifrada donde indique el usuario (acción explícita).</summary>
    public void Export(Guid id, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        using var content = Open(id);
        using (var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
        {
            stream.Write(content.Span);
        }

        AppLog.Info(LogEvent.DocumentExported);
    }

    public void Rename(Guid id, string title)
    {
        string trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return;
        }

        _session.Mutate(vault =>
        {
            var document = vault.Documents.FirstOrDefault(d => d.Id == id);
            if (document is not null)
            {
                document.Title = trimmed.Length > 200 ? trimmed[..200] : trimmed;
                document.ModifiedUtc = _clock.UtcNow;
            }
        });
    }

    /// <summary>Elimina el documento de la bóveda y su archivo cifrado.</summary>
    public bool Delete(Guid id)
    {
        bool removed = _session.Mutate(vault =>
        {
            var document = vault.Documents.FirstOrDefault(d => d.Id == id);
            if (document is null)
            {
                return false;
            }

            vault.Documents.Remove(document);
            document.Dispose();
            return true;
        });

        if (removed)
        {
            _store.Delete(id);
            AppLog.Info(LogEvent.DocumentDeleted);
        }

        return removed;
    }

    /// <summary>Documentos cifrados (sin la capa DPAPI) para incluirlos en una copia de seguridad.</summary>
    public Dictionary<Guid, byte[]> CollectForBackup()
    {
        var blobs = new Dictionary<Guid, byte[]>();
        foreach (var document in _session.Documents)
        {
            blobs[document.Id] = _store.Read(document.Id);
        }

        return blobs;
    }

    /// <summary>
    /// Restaura los documentos de una copia de seguridad ya abierta con su contraseña. Se verifica cada
    /// documento (integridad y huella) y se omiten los que ya existen. Devuelve cuántos se han restaurado.
    /// </summary>
    public int RestoreFromBackup(VaultDocument backup, IReadOnlyDictionary<Guid, byte[]> blobs)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(blobs);
        var current = _session.Documents;
        var knownHashes = new HashSet<string>(current.Select(d => d.Sha256), StringComparer.Ordinal);
        var knownIds = new HashSet<Guid>(current.Select(d => d.Id));
        int restored = 0;

        foreach (var source in backup.Documents)
        {
            if (knownHashes.Contains(source.Sha256) || !blobs.TryGetValue(source.Id, out var blob))
            {
                continue;
            }

            try
            {
                using var content = DocumentCipher.Open(source.Key, source.Id, blob);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(content.Span)), source.Sha256, StringComparison.Ordinal))
                {
                    continue;
                }

                // Si el identificador ya existe (caso muy improbable), se vuelve a cifrar con uno nuevo.
                var document = new SecureDocument
                {
                    Id = knownIds.Contains(source.Id) ? Guid.NewGuid() : source.Id,
                    Title = source.Title,
                    FileName = source.FileName,
                    Size = source.Size,
                    Sha256 = source.Sha256,
                    CreatedUtc = source.CreatedUtc,
                    ModifiedUtc = source.ModifiedUtc,
                };
                if (document.Id == source.Id)
                {
                    document.SetKey(source.Key.Clone());
                    _store.Write(document.Id, blob);
                }
                else
                {
                    document.SetKey(DocumentCipher.NewKey());
                    _store.Write(document.Id, DocumentCipher.Seal(document.Key, document.Id, content.Span));
                }

                try
                {
                    _session.Mutate(vault => vault.Documents.Add(document));
                }
                catch
                {
                    _store.Delete(document.Id);
                    document.Dispose();
                    throw;
                }

                knownHashes.Add(document.Sha256);
                knownIds.Add(document.Id);
                restored++;
            }
            catch (VaultFormatException)
            {
                // Documento dañado en la copia: se omite.
            }
        }

        AppLog.Info(LogEvent.DocumentsRestored, new LogMetric("restored", restored));
        return restored;
    }

    private SecureDocument Find(Guid id) =>
        _session.Documents.FirstOrDefault(d => d.Id == id) ?? throw new KeyNotFoundException("El documento ya no existe.");

    private static SensitiveBuffer ReadFile(string path, int length)
    {
        var buffer = new SensitiveBuffer(length);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
            int read = stream.ReadAtLeast(buffer.Span, length, throwOnEndOfStream: true);
            if (read != length || stream.ReadByte() != -1)
            {
                throw new IOException("El archivo cambió mientras se leía.");
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
