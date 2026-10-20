using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PassDesk.Import;

/// <summary>
/// Reconoce si un archivo es una exportación de contraseñas (de Apple o compatible) mirando solo la
/// línea de cabecera del CSV o los nombres de los archivos dentro del ZIP. Nunca decodifica las filas de datos
/// que entren en el búfer leído, y este se pone a cero en cuanto se ha examinado la cabecera.
/// </summary>
public static class ExportFileProbe
{
    private const int HeaderProbeBytes = 4096;

    public static bool LooksLikePasswordExport(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > ExportFileReader.MaxFileBytes)
            {
                return false;
            }

            string extension = info.Extension.ToLowerInvariant();
            if (extension is not (".csv" or ".zip"))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            return extension == ".zip" ? ZipContainsPasswordCsv(stream) : HeaderIsRecognized(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ZipContainsPasswordCsv(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var csvEntries = archive.Entries
            .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                && !e.FullName.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        if (csvEntries.Any(e => ExportFileReader.LooksLikePasswordFile(e.Name)))
        {
            return true;
        }

        foreach (var entry in csvEntries)
        {
            if (entry.Length is > 0 and <= ExportFileReader.MaxFileBytes)
            {
                using var entryStream = entry.Open();
                if (HeaderIsRecognized(entryStream))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HeaderIsRecognized(Stream stream)
    {
        var buffer = new byte[HeaderProbeBytes];
        try
        {
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ExportFileReader.IsRecognizedHeader(DecodeHeaderLine(buffer.AsSpan(0, read)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Decodifica solo la primera línea (la cabecera). El final se busca en los bytes, así que las filas de datos que
    /// entren en el búfer (con contraseñas) nunca pasan a una cadena, que no se podría borrar.
    /// </summary>
    internal static string DecodeHeaderLine(ReadOnlySpan<byte> data)
    {
        Encoding encoding = Encoding.UTF8;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            encoding = Encoding.Unicode;
            data = data[2..];
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            encoding = Encoding.BigEndianUnicode;
            data = data[2..];
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            data = data[3..];
        }

        int end = 0;
        if (encoding == Encoding.UTF8)
        {
            // En UTF-8, 0x0D y 0x0A nunca forman parte de otro carácter.
            end = data.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0)
            {
                end = data.Length;
            }
        }
        else
        {
            // En UTF-16 sí pueden ser medio carácter (U+0D0A, por ejemplo): se compara cada carácter completo.
            bool bigEndian = encoding == Encoding.BigEndianUnicode;
            while (end + 1 < data.Length && (bigEndian ? data[end] << 8 | data[end + 1] : data[end + 1] << 8 | data[end]) is not ('\r' or '\n'))
            {
                end += 2;
            }
        }

        return encoding.GetString(data[..end]);
    }
}

public sealed class ExportDetectedEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}

/// <summary>
/// Vigila carpetas locales (Escritorio, Descargas, Documentos) y avisa cuando aparece una exportación de
/// contraseñas, para importarla sin tener que buscarla. No importa nada por sí mismo: solo informa de la
/// ruta; la importación sigue requiriendo la confirmación del usuario en la vista previa.
/// </summary>
public sealed class ExportFolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly TimeSpan _settleDelay;
    private bool _disposed;

    public ExportFolderWatcher(IEnumerable<string> folders, TimeSpan? settleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _settleDelay = settleDelay ?? TimeSpan.FromSeconds(1.5);
        Folders = folders
            .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string folder in Folders)
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Filters.Add("*.csv");
            watcher.Filters.Add("*.zip");
            watcher.Created += (_, e) => Schedule(e.FullPath);
            watcher.Changed += (_, e) => Schedule(e.FullPath);
            watcher.Renamed += (_, e) => Schedule(e.FullPath);
            _watchers.Add(watcher);
        }
    }

    /// <summary>Se produce (en un hilo de fondo) al detectar una exportación nueva o modificada.</summary>
    public event EventHandler<ExportDetectedEventArgs>? ExportDetected;

    public IReadOnlyList<string> Folders { get; }

    public bool Enabled
    {
        get => _watchers.Any(w => w.EnableRaisingEvents);
        set
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = value;
            }
        }
    }

    /// <summary>Busca exportaciones recientes que ya estuvieran en las carpetas (por ejemplo, con la app cerrada).</summary>
    public void ScanRecent(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (string folder in Folders)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.csv").Concat(Directory.EnumerateFiles(folder, "*.zip")).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    {
                        Schedule(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Olvida una ruta ya avisada (por ejemplo, tras importarla) para no volver a avisar de ella.</summary>
    public void MarkHandled(string path)
    {
        lock (_gate)
        {
            try
            {
                _reported[Path.GetFullPath(path)] = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MaxValue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
    }

    private void Schedule(string path)
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Cada comprobación libera su propio token al terminar; aquí solo se cancela la anterior.
            if (_pending.Remove(path, out var previous))
            {
                previous.Cancel();
            }

            source = new CancellationTokenSource();
            _pending[path] = source;
        }

        _ = CheckAfterSettlingAsync(path, source);
    }

    private async Task CheckAfterSettlingAsync(string path, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_settleDelay, source.Token).ConfigureAwait(false);

            // Espera a que quien escribe el archivo (navegador, LocalSend…) termine.
            for (int attempt = 0; attempt < 20 && !CanOpen(path); attempt++)
            {
                await Task.Delay(500, source.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, source))
                {
                    _pending.Remove(path);
                }
            }

            source.Dispose();
        }

        DateTime stamp;
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string key = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_disposed || (_reported.TryGetValue(key, out var previous) && previous >= stamp))
            {
                return;
            }
        }

        if (!ExportFileProbe.LooksLikePasswordExport(path))
        {
            return;
        }

        lock (_gate)
        {
            _reported[key] = stamp;
        }

        ExportDetected?.Invoke(this, new ExportDetectedEventArgs(key));
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var source in _pending.Values)
            {
                source.Cancel();
            }

            _pending.Clear();
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }
}
