using System.IO.Enumeration;
using PassDesk.Core.Diagnostics;
using PassDesk.Crypto;

namespace PassDesk.Storage;

/// <summary>Almacén del sobre cifrado de la bóveda.</summary>
public interface IVaultStore
{
    bool Exists { get; }

    /// <summary>Devuelve el sobre AES-GCM (ya sin la capa local de DPAPI).</summary>
    byte[] ReadEnvelope();

    void WriteEnvelope(byte[] envelope);

    /// <summary>
    /// Guarda una copia de la bóveda tal como está ahora (igual de cifrada) antes de un cambio masivo, como una
    /// importación o una restauración, para poder deshacerlo.
    /// </summary>
    void CreateRecoveryPoint(string reason);
}

/// <summary>
/// Bóveda local en disco. Añade una segunda capa de cifrado con DPAPI (ámbito usuario actual)
/// sobre el sobre AES-256-GCM protegido por la contraseña maestra:
/// copiar el archivo a otro equipo o a otra cuenta de Windows no permite ni siquiera intentar
/// adivinar la contraseña maestra sin las credenciales de esa cuenta.
/// </summary>
/// <remarks>Formato: "PDLV" | versión (1) | protección (1 = DPAPI CurrentUser) | blob DPAPI.</remarks>
public sealed class DpapiVaultStore : IVaultStore
{
    private const int HeaderLength = 6;
    private const byte Version = 1;
    private const byte ProtectionDpapiCurrentUser = 1;
    private const long MaxFileBytes = 128L * 1024 * 1024;

    /// <summary>Puntos de recuperación que se conservan (los más antiguos se eliminan).</summary>
    public const int MaxRecoveryPoints = 10;

    /// <summary>Etiqueta de la copia guardada antes de pasar la bóveda de PBKDF2 a Argon2id («vault-…-antesdeargon2id.pdv»).</summary>
    public const string PreArgon2idLabel = "antesdeargon2id";

    private const string RecoveryPointPattern = "vault-*.pdv";

    public DpapiVaultStore(string path, string? recoveryDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        RecoveryDirectory = recoveryDirectory
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "recuperacion");
    }

    private static ReadOnlySpan<byte> Magic => "PDLV"u8;

    public string FilePath { get; }

    public string RecoveryDirectory { get; }

    public bool Exists => File.Exists(FilePath);

    public byte[] ReadEnvelope()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("No existe la bóveda.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new VaultFormatException("El archivo de la bóveda es demasiado grande.");
        }

        byte[] bytes = File.ReadAllBytes(FilePath);
        if (bytes.Length <= HeaderLength
            || !bytes.AsSpan(0, 4).SequenceEqual(Magic)
            || bytes[4] != Version
            || bytes[5] != ProtectionDpapiCurrentUser)
        {
            throw new VaultFormatException("El archivo no tiene el formato de bóveda local de PassDesk.");
        }

        return WindowsDataProtection.Unprotect(bytes[HeaderLength..]);
    }

    public void WriteEnvelope(byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        byte[] blob = WindowsDataProtection.Protect(envelope);
        var file = new byte[HeaderLength + blob.Length];
        Magic.CopyTo(file);
        file[4] = Version;
        file[5] = ProtectionDpapiCurrentUser;
        blob.CopyTo(file, HeaderLength);
        AtomicFile.Write(FilePath, file, keepPrevious: true);
    }

    /// <summary>
    /// Copia el archivo actual a «recuperacion\vault-AAAAMMDD-HHMMSS-motivo.pdv». Mantiene la misma protección
    /// (DPAPI + contraseña maestra) y se puede restaurar desde Ajustes. Conserva solo los más recientes.
    /// </summary>
    public void CreateRecoveryPoint(string reason)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        Directory.CreateDirectory(RecoveryDirectory);
        string label = string.Concat((reason ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(24));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string target = Path.Combine(RecoveryDirectory, $"vault-{stamp}-{(label.Length > 0 ? label : "cambio")}.pdv");
        for (int suffix = 2; File.Exists(target); suffix++)
        {
            target = Path.Combine(RecoveryDirectory, $"vault-{stamp}-{(label.Length > 0 ? label : "cambio")}-{suffix}.pdv");
        }

        File.Copy(FilePath, target);
        File.SetCreationTimeUtc(target, DateTime.UtcNow);

        // Se ordena por la fecha de creación de la copia (no por el nombre: varias en el mismo segundo llevan sufijo).
        foreach (string old in Directory.GetFiles(RecoveryDirectory, RecoveryPointPattern)
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(MaxRecoveryPoints))
        {
            File.Delete(old);
        }
    }

    /// <summary>Puntos de recuperación que hay ahora (rutas completas). Cada uno se abre con la contraseña que había al guardarlo.</summary>
    public string[] ListRecoveryPoints() =>
        Directory.Exists(RecoveryDirectory) ? Directory.GetFiles(RecoveryDirectory, RecoveryPointPattern) : [];

    /// <summary>La copia previa a Argon2id (en PBKDF2): al ofrecer borrar puntos de recuperación se pregunta por ella aparte.</summary>
    public static bool IsPreArgon2idPoint(string path) =>
        Path.GetFileName(path).Contains("-" + PreArgon2idLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Borra (sin pasar por la Papelera) los puntos de recuperación indicados y devuelve los que no se pudieron borrar
    /// (en uso, sin permiso…). Solo admite «vault-*.pdv» de <see cref="RecoveryDirectory"/>: si alguna ruta es otra
    /// cosa, no borra nada.
    /// </summary>
    public IReadOnlyList<string> DeleteRecoveryPoints(IEnumerable<string> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecoveryDirectory));
        string[] targets = [.. points.Select(Path.GetFullPath)];
        if (targets.Any(p => !string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)
                             || !FileSystemName.MatchesSimpleExpression(RecoveryPointPattern, Path.GetFileName(p))))
        {
            throw new ArgumentException("Solo se pueden borrar puntos de recuperación.", nameof(points));
        }

        var failed = new List<string>();
        foreach (string target in targets)
        {
            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(target);
            }
        }

        AppLog.Info(LogEvent.RecoveryPointsDeleted, new LogMetric("deleted", targets.Length - failed.Count), new LogMetric("failed", failed.Count));
        return failed;
    }
}

/// <summary>Almacén en memoria para pruebas.</summary>
public sealed class InMemoryVaultStore : IVaultStore
{
    public byte[]? Envelope { get; set; }

    public int WriteCount { get; private set; }

    public List<byte[]> RecoveryPoints { get; } = [];

    public bool Exists => Envelope is not null;

    public byte[] ReadEnvelope() => (byte[])(Envelope ?? throw new FileNotFoundException("No existe la bóveda.")).Clone();

    public void WriteEnvelope(byte[] envelope)
    {
        Envelope = (byte[])envelope.Clone();
        WriteCount++;
    }

    public void CreateRecoveryPoint(string reason)
    {
        if (Envelope is not null)
        {
            RecoveryPoints.Add((byte[])Envelope.Clone());
        }
    }
}

/// <summary>Almacén de los documentos cifrados (un archivo por documento).</summary>
public interface IDocumentStore
{
    bool Exists(Guid id);

    /// <summary>Devuelve el documento cifrado con AES-256-GCM (ya sin la capa local de DPAPI).</summary>
    byte[] Read(Guid id);

    void Write(Guid id, byte[] blob);

    void Delete(Guid id);
}

/// <summary>
/// Documentos en disco (%LOCALAPPDATA%\PassDesk\docs). Cada archivo contiene el documento cifrado con su
/// propia clave AES-256-GCM y, encima, la capa DPAPI de la cuenta de Windows, igual que la bóveda.
/// </summary>
/// <remarks>Formato: "PDDL" | versión (1) | protección (1 = DPAPI CurrentUser) | blob DPAPI.</remarks>
public sealed class DpapiDocumentStore : IDocumentStore
{
    private const int HeaderLength = 6;
    private const byte Version = 1;
    private const byte ProtectionDpapiCurrentUser = 1;
    private const long MaxFileBytes = 128L * 1024 * 1024;

    public DpapiDocumentStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder);
    }

    private static ReadOnlySpan<byte> Magic => "PDDL"u8;

    public string Folder { get; }

    public string PathFor(Guid id) => Path.Combine(Folder, id.ToString("N") + ".pdd");

    public bool Exists(Guid id) => File.Exists(PathFor(id));

    public byte[] Read(Guid id)
    {
        string path = PathFor(id);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("No se encuentra el documento cifrado.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new VaultFormatException("El documento cifrado es demasiado grande.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length <= HeaderLength
            || !bytes.AsSpan(0, 4).SequenceEqual(Magic)
            || bytes[4] != Version
            || bytes[5] != ProtectionDpapiCurrentUser)
        {
            throw new VaultFormatException("El documento cifrado no tiene un formato válido.");
        }

        return WindowsDataProtection.Unprotect(bytes[HeaderLength..]);
    }

    public void Write(Guid id, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        byte[] protectedBlob = WindowsDataProtection.Protect(blob);
        var file = new byte[HeaderLength + protectedBlob.Length];
        Magic.CopyTo(file);
        file[4] = Version;
        file[5] = ProtectionDpapiCurrentUser;
        protectedBlob.CopyTo(file, HeaderLength);
        AtomicFile.Write(PathFor(id), file, keepPrevious: false);
    }

    public void Delete(Guid id)
    {
        string path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Almacén de documentos en memoria para pruebas.</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    public Dictionary<Guid, byte[]> Blobs { get; } = [];

    public bool Exists(Guid id) => Blobs.ContainsKey(id);

    public byte[] Read(Guid id) => Blobs.TryGetValue(id, out var blob) ? (byte[])blob.Clone() : throw new FileNotFoundException();

    public void Write(Guid id, byte[] blob) => Blobs[id] = (byte[])blob.Clone();

    public void Delete(Guid id) => Blobs.Remove(id);
}

/// <summary>Contenido de una copia de seguridad: el sobre de la bóveda y los documentos cifrados.</summary>
public sealed record BackupContents(byte[] Envelope, IReadOnlyDictionary<Guid, byte[]> Documents);

/// <summary>
/// Copia de seguridad portátil: el mismo sobre AES-256-GCM protegido por la contraseña maestra,
/// pero sin la capa DPAPI, para poder restaurarla en otro equipo o cuenta de Windows. Desde la versión 2
/// incluye también los documentos (cifrados con sus claves, que viajan dentro del sobre).
/// </summary>
/// <remarks>
/// Versión 1: "PDBK" | 1 | sobre.
/// Versión 2: "PDBK" | 2 | longitud del sobre (int32) | sobre | nº de documentos (int32) |
/// por documento: identificador (16) | longitud (int32) | documento cifrado.
/// </remarks>
public static class BackupFile
{
    public const string Extension = ".pdbackup";
    private const byte VersionWithoutDocuments = 1;
    private const byte VersionWithDocuments = 2;
    private const int HeaderLength = 5;
    private const long MaxFileBytes = 1024L * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "PDBK"u8;

    public static byte[] Wrap(ReadOnlySpan<byte> envelope) => Wrap(envelope, new Dictionary<Guid, byte[]>());

    public static byte[] Wrap(ReadOnlySpan<byte> envelope, IReadOnlyDictionary<Guid, byte[]> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        long total = HeaderLength + 4L + envelope.Length + 4 + documents.Values.Sum(d => 16L + 4 + d.Length);
        if (total > MaxFileBytes)
        {
            throw new InvalidOperationException("La copia de seguridad supera el tamaño máximo admitido (1 GB).");
        }

        using var stream = new MemoryStream((int)total);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(VersionWithDocuments);
            writer.Write(envelope.Length);
            writer.Write(envelope);
            writer.Write(documents.Count);
            foreach (var (id, blob) in documents)
            {
                writer.Write(id.ToByteArray());
                writer.Write(blob.Length);
                writer.Write(blob);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Devuelve solo el sobre de la bóveda (compatibilidad).</summary>
    public static byte[] Unwrap(ReadOnlySpan<byte> file) => UnwrapContents(file).Envelope;

    public static BackupContents UnwrapContents(ReadOnlySpan<byte> file)
    {
        if (file.Length <= HeaderLength || !file[..4].SequenceEqual(Magic))
        {
            throw new VaultFormatException("El archivo no es una copia de seguridad de PassDesk.");
        }

        if (file[4] == VersionWithoutDocuments)
        {
            return new BackupContents(file[HeaderLength..].ToArray(), new Dictionary<Guid, byte[]>());
        }

        if (file[4] != VersionWithDocuments)
        {
            throw new VaultFormatException("Versión de copia de seguridad no compatible.");
        }

        try
        {
            int offset = HeaderLength;
            byte[] envelope = ReadBlock(file, ref offset);
            int count = ReadInt32(file, ref offset);
            if (count < 0 || count > 100_000)
            {
                throw new VaultFormatException("La copia de seguridad está dañada.");
            }

            var documents = new Dictionary<Guid, byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                if (offset + 16 > file.Length)
                {
                    throw new VaultFormatException("La copia de seguridad está incompleta.");
                }

                var id = new Guid(file.Slice(offset, 16));
                offset += 16;
                documents[id] = ReadBlock(file, ref offset);
            }

            return new BackupContents(envelope, documents);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new VaultFormatException("La copia de seguridad está dañada o incompleta.");
        }
    }

    public static BackupContents ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
        {
            throw new VaultFormatException("El archivo es demasiado grande para ser una copia de PassDesk.");
        }

        return UnwrapContents(File.ReadAllBytes(path));
    }

    public static void WriteFile(string path, ReadOnlySpan<byte> envelope, IReadOnlyDictionary<Guid, byte[]> documents) =>
        AtomicFile.Write(path, Wrap(envelope, documents), keepPrevious: false);

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            throw new VaultFormatException("La copia de seguridad está incompleta.");
        }

        int value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static byte[] ReadBlock(ReadOnlySpan<byte> data, ref int offset)
    {
        int length = ReadInt32(data, ref offset);
        if (length < 0 || offset + length > data.Length)
        {
            throw new VaultFormatException("La copia de seguridad está dañada o incompleta.");
        }

        byte[] block = data.Slice(offset, length).ToArray();
        offset += length;
        return block;
    }
}

/// <summary>Escritura atómica: archivo temporal + reemplazo, para no dejar una bóveda a medias.</summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> content, bool keepPrevious)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Ruta no válida.", nameof(path));
        Directory.CreateDirectory(directory);
        string temp = fullPath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        if (!File.Exists(fullPath))
        {
            File.Move(temp, fullPath);
            return;
        }

        string? backup = keepPrevious ? fullPath + ".bak" : null;
        try
        {
            File.Replace(temp, fullPath, backup, ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            // Algunos sistemas de archivos no admiten ReplaceFile; alternativa equivalente.
            if (backup is not null)
            {
                File.Copy(fullPath, backup, overwrite: true);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
    }
}
