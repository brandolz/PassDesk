using System.IO.Compression;
using System.Text;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Core.Text;
using PassDesk.Crypto;
using PassDesk.Import.Csv;

namespace PassDesk.Import;

public enum ImportColumn
{
    Title,
    Url,
    Username,
    Password,
    Notes,
    OtpAuth,
}

public enum ParseIssueKind
{
    /// <summary>La fila no tiene contraseña (por ejemplo, entradas solo con llave de acceso).</summary>
    MissingPassword,

    /// <summary>La fila no tiene título, URL ni usuario.</summary>
    MissingIdentity,

    /// <summary>La fila tiene más columnas que la cabecera (formato dañado).</summary>
    ColumnCountMismatch,
}

/// <summary>Incidencia de una fila. Solo guarda el número de línea, nunca su contenido.</summary>
public sealed record ParseIssue(int Line, ParseIssueKind Kind);

/// <summary>Error de importación con un mensaje apto para el usuario y sin datos del archivo.</summary>
public sealed class ImportFileException : Exception
{
    public ImportFileException(string message)
        : base(message)
    {
    }

    public ImportFileException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Credencial leída de un archivo, todavía sin guardar. Sus secretos están protegidos en memoria.</summary>
public sealed class ParsedCredential : IDisposable
{
    public required int Line { get; init; }

    public required string Title { get; init; }

    public required string Url { get; init; }

    public required string Username { get; init; }

    public required ProtectedSecret Password { get; init; }

    public ProtectedSecret Notes { get; init; } = ProtectedSecret.Empty;

    public ProtectedSecret OtpAuth { get; init; } = ProtectedSecret.Empty;

    /// <summary>Metadatos opcionales (presentes al restaurar una copia de seguridad de PassDesk).</summary>
    public Category? Category { get; init; }

    public bool IsCategoryManual { get; init; }

    public DateTimeOffset? CreatedUtc { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }

    public string DisplayHost => DomainUtil.GetDisplayHost(Url);

    public string DuplicateKey => CredentialEntry.BuildDuplicateKey(Title, Url, Username);

    public void Dispose()
    {
        Password.Dispose();
        Notes.Dispose();
        OtpAuth.Dispose();
    }

    public override string ToString() => $"ParsedCredential(line {Line})";
}

/// <summary>
/// Identidad del archivo leído: tamaño y huella SHA-256 de su contenido exacto. Antes de borrar el archivo se
/// comprueba que sigue siendo el mismo, para no destruir otra exportación que lo haya sustituido.
/// </summary>
public sealed record FileFingerprint(long Length, string Sha256)
{
    public static FileFingerprint Of(ReadOnlySpan<byte> content) =>
        new(content.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)));
}

/// <summary>Resultado de leer un archivo de exportación. Liberarlo borra los secretos de memoria.</summary>
public sealed class ParseResult : IDisposable
{
    public required string FormatName { get; init; }

    /// <summary>Identidad del archivo leído (null si el resultado no procede de un archivo).</summary>
    public FileFingerprint? SourceFingerprint { get; internal set; }

    public required IReadOnlyList<ParsedCredential> Items { get; init; }

    public required IReadOnlyList<ParseIssue> Issues { get; init; }

    public IReadOnlyList<ImportColumn> Columns { get; init; } = [];

    /// <summary>Indica que el origen guardaba las contraseñas sin cifrar (CSV/ZIP de Apple).</summary>
    public bool SourceWasPlaintext { get; init; }

    public bool SourceHadVerificationCodes => Items.Any(i => !i.OtpAuth.IsEmpty);

    public void Dispose()
    {
        foreach (var item in Items)
        {
            item.Dispose();
        }
    }

    /// <summary>Convierte credenciales existentes (por ejemplo, de una copia de seguridad) en un resultado importable.</summary>
    public static ParseResult FromEntries(IEnumerable<CredentialEntry> entries, string formatName)
    {
        var items = new List<ParsedCredential>();
        int index = 0;
        foreach (var entry in entries)
        {
            index++;
            items.Add(new ParsedCredential
            {
                Line = index,
                Title = entry.Title,
                Url = entry.Url,
                Username = entry.Username,
                Password = entry.Password.Clone(),
                Notes = entry.Notes.Clone(),
                OtpAuth = entry.OtpAuth.Clone(),
                Category = entry.Category,
                IsCategoryManual = entry.IsCategoryManual,
                CreatedUtc = entry.CreatedUtc,
                ModifiedUtc = entry.ModifiedUtc,
            });
        }

        return new ParseResult { FormatName = formatName, Items = items, Issues = [], SourceWasPlaintext = false };
    }
}

/// <summary>
/// Lee las exportaciones oficiales de Apple:
/// <list type="bullet">
/// <item>iPhone/iPad (iOS 18.2+): Ajustes › Apps › Safari › Exportar › Contraseñas → archivo ZIP o CSV.</item>
/// <item>Contraseñas de iCloud para Windows: Exportar contraseñas → CSV.</item>
/// <item>App Contraseñas del Mac: Archivo › Exportar todas las contraseñas → CSV.</item>
/// </list>
/// Las columnas se reconocen por nombre (Title/URL/Username/Password/Notes/OTPAuth de Apple,
/// name/url/username/password de navegadores) y se admite coma, punto y coma o tabulador.
/// El archivo de origen se abre en modo solo lectura y nunca se modifica.
/// </summary>
public static class ExportFileReader
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxRows = 50_000;
    private const int MaxTitleLength = 512;
    private const int MaxUrlLength = 2048;
    private const int MaxUsernameLength = 512;

    private static readonly Dictionary<string, ImportColumn> HeaderAliases = new(StringComparer.Ordinal)
    {
        ["title"] = ImportColumn.Title, ["name"] = ImportColumn.Title, ["nombre"] = ImportColumn.Title,
        ["titulo"] = ImportColumn.Title, ["site name"] = ImportColumn.Title,
        ["url"] = ImportColumn.Url, ["website"] = ImportColumn.Url, ["web site"] = ImportColumn.Url,
        ["login_uri"] = ImportColumn.Url, ["uri"] = ImportColumn.Url, ["sitio web"] = ImportColumn.Url,
        ["site"] = ImportColumn.Url, ["web"] = ImportColumn.Url,
        ["username"] = ImportColumn.Username, ["user name"] = ImportColumn.Username, ["user"] = ImportColumn.Username,
        ["login"] = ImportColumn.Username, ["login_username"] = ImportColumn.Username, ["email"] = ImportColumn.Username,
        ["e-mail"] = ImportColumn.Username, ["usuario"] = ImportColumn.Username, ["nombre de usuario"] = ImportColumn.Username,
        ["account"] = ImportColumn.Username, ["cuenta"] = ImportColumn.Username,
        ["password"] = ImportColumn.Password, ["login_password"] = ImportColumn.Password,
        ["contrasena"] = ImportColumn.Password, ["clave"] = ImportColumn.Password, ["pass"] = ImportColumn.Password,
        ["notes"] = ImportColumn.Notes, ["note"] = ImportColumn.Notes, ["notas"] = ImportColumn.Notes,
        ["nota"] = ImportColumn.Notes, ["comments"] = ImportColumn.Notes, ["extra"] = ImportColumn.Notes,
        ["otpauth"] = ImportColumn.OtpAuth, ["otp"] = ImportColumn.OtpAuth, ["totp"] = ImportColumn.OtpAuth,
        ["login_totp"] = ImportColumn.OtpAuth,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Lee un archivo CSV o ZIP seleccionado por el usuario.</summary>
    public static ParseResult ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ImportFileException("La ruta del archivo no es válida.", ex);
        }

        if (!info.Exists)
        {
            throw new ImportFileException("No se encuentra el archivo seleccionado.");
        }

        if (info.Length == 0)
        {
            throw new ImportFileException("El archivo está vacío.");
        }

        if (info.Length > MaxFileBytes)
        {
            throw new ImportFileException("El archivo supera el tamaño máximo admitido (50 MB).");
        }

        byte[] data = SecureMemory.AllocatePinned((int)info.Length);
        try
        {
            int read;
            try
            {
                // Sin búfer interno (bufferSize 0) para no dejar copias adicionales del contenido.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
                read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ImportFileException("No se puede leer el archivo. Comprueba que no esté abierto en otra aplicación y que tengas permiso.", ex);
            }

            var result = IsZip(data.AsSpan(0, read))
                ? ReadZip(data, read)
                : ParseCsv(data.AsSpan(0, read), "Archivo CSV");
            result.SourceFingerprint = FileFingerprint.Of(data.AsSpan(0, read));
            AppLog.Info(LogEvent.ImportParsed, new LogMetric("items", result.Items.Count), new LogMetric("issues", result.Issues.Count));
            return result;
        }
        catch (ImportFileException)
        {
            AppLog.Warn(LogEvent.ImportRejected);
            throw;
        }
        finally
        {
            SecureMemory.Zero(data);
        }
    }

    /// <summary>Analiza el contenido de un CSV en bytes (UTF-8, UTF-16 o Latin-1).</summary>
    public static ParseResult ParseCsv(ReadOnlySpan<byte> data, string sourceLabel)
    {
        if (data.IsEmpty)
        {
            throw new ImportFileException("El archivo está vacío.");
        }

        Encoding encoding;
        int preamble = 0;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            encoding = StrictUtf8;
            preamble = 3;
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            encoding = Encoding.Unicode;
            preamble = 2;
        }
        else if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            encoding = Encoding.BigEndianUnicode;
            preamble = 2;
        }
        else
        {
            encoding = StrictUtf8;
        }

        var body = data[preamble..];
        int charCount;
        try
        {
            charCount = encoding.GetCharCount(body);
        }
        catch (DecoderFallbackException)
        {
            // CSV guardado con otra codificación (por ejemplo, tras abrirlo en Excel).
            encoding = Encoding.Latin1;
            charCount = encoding.GetCharCount(body);
        }

        using var chars = new SensitiveChars(charCount);
        int written = encoding.GetChars(body, chars.Span);
        return ParseText(chars.Span[..written], sourceLabel);
    }

    /// <summary>Analiza texto CSV ya decodificado.</summary>
    public static ParseResult ParseText(ReadOnlySpan<char> text, string sourceLabel)
    {
        if (text.IsEmpty || text.IsWhiteSpace())
        {
            throw new ImportFileException("El archivo está vacío.");
        }

        if (text[..Math.Min(text.Length, 4096)].Contains('\0'))
        {
            throw new ImportFileException("El archivo no es un CSV de texto. Selecciona la exportación de contraseñas (.csv o .zip).");
        }

        char delimiter = CsvParser.DetectDelimiter(text);
        CsvTable table;
        try
        {
            table = CsvParser.Parse(text, delimiter, MaxRows + 1);
        }
        catch (CsvFormatException ex)
        {
            throw new ImportFileException($"El archivo CSV no es válido (línea {ex.Line}): {ex.Message}", ex);
        }

        if (table.Rows.Count == 0)
        {
            throw new ImportFileException("El archivo está vacío.");
        }

        var header = table.Rows[0];
        var map = new Dictionary<ImportColumn, int>();
        for (int c = 0; c < header.Length; c++)
        {
            string name = TextNormalizer.Fold(CsvParser.GetString(text, header[c])).Trim();
            if (HeaderAliases.TryGetValue(name, out var column) && !map.ContainsKey(column))
            {
                map[column] = c;
            }
        }

        if (!HasRequiredColumns(map.Keys))
        {
            throw new ImportFileException(
                "No se reconocen las columnas del archivo. Se esperaba una exportación de Contraseñas de Apple " +
                "(Title, URL, Username, Password, Notes, OTPAuth) o un CSV con las columnas name, url, username y password.");
        }

        if (table.Rows.Count - 1 > MaxRows)
        {
            throw new ImportFileException($"El archivo tiene demasiadas filas (máximo {MaxRows:N0}).");
        }

        var items = new List<ParsedCredential>();
        var issues = new List<ParseIssue>();
        try
        {
            for (int r = 1; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                int line = table.RowLines[r];
                if (CsvParser.IsBlankRow(text, row))
                {
                    continue;
                }

                if (row.Length > header.Length)
                {
                    issues.Add(new ParseIssue(line, ParseIssueKind.ColumnCountMismatch));
                    continue;
                }

                int passwordIndex = map[ImportColumn.Password];
                if (passwordIndex >= row.Length || row[passwordIndex].Length == 0)
                {
                    issues.Add(new ParseIssue(line, ParseIssueKind.MissingPassword));
                    continue;
                }

                string title = Limit(GetText(text, row, map, ImportColumn.Title), MaxTitleLength);
                string url = Limit(GetText(text, row, map, ImportColumn.Url), MaxUrlLength);
                string username = Limit(GetText(text, row, map, ImportColumn.Username), MaxUsernameLength);
                if (title.Length == 0 && url.Length == 0 && username.Length == 0)
                {
                    issues.Add(new ParseIssue(line, ParseIssueKind.MissingIdentity));
                    continue;
                }

                if (title.Length == 0)
                {
                    string host = DomainUtil.GetDisplayHost(url);
                    title = host.Length > 0 ? host : username;
                }

                items.Add(new ParsedCredential
                {
                    Line = line,
                    Title = title,
                    Url = url,
                    Username = username,
                    Password = CsvParser.GetSecret(text, row[passwordIndex]),
                    Notes = GetSecret(text, row, map, ImportColumn.Notes),
                    OtpAuth = GetSecret(text, row, map, ImportColumn.OtpAuth),
                });
            }
        }
        catch
        {
            foreach (var item in items)
            {
                item.Dispose();
            }

            throw;
        }

        return new ParseResult
        {
            FormatName = DescribeFormat(map, sourceLabel),
            Items = items,
            Issues = issues,
            Columns = [.. map.Keys.Order()],
            SourceWasPlaintext = true,
        };
    }

    /// <summary>
    /// Indica si una línea de cabecera corresponde a una exportación de contraseñas reconocible.
    /// Solo examina nombres de columna, nunca datos.
    /// </summary>
    public static bool IsRecognizedHeader(string? headerLine)
    {
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            return false;
        }

        string line = headerLine.TrimStart('﻿');
        char delimiter = CsvParser.DetectDelimiter(line);
        var columns = new HashSet<ImportColumn>();
        foreach (string raw in line.Split(delimiter))
        {
            string name = TextNormalizer.Fold(raw.Trim().Trim('"')).Trim();
            if (HeaderAliases.TryGetValue(name, out var column))
            {
                columns.Add(column);
            }
        }

        return HasRequiredColumns(columns);
    }

    private static bool HasRequiredColumns(IEnumerable<ImportColumn> columns)
    {
        var set = columns as ICollection<ImportColumn> ?? [.. columns];
        return set.Contains(ImportColumn.Password)
            && (set.Contains(ImportColumn.Url) || set.Contains(ImportColumn.Title) || set.Contains(ImportColumn.Username));
    }

    private static string GetText(ReadOnlySpan<char> text, CsvField[] row, Dictionary<ImportColumn, int> map, ImportColumn column) =>
        map.TryGetValue(column, out int index) && index < row.Length ? CsvParser.GetString(text, row[index]).Trim() : string.Empty;

    private static ProtectedSecret GetSecret(ReadOnlySpan<char> text, CsvField[] row, Dictionary<ImportColumn, int> map, ImportColumn column) =>
        map.TryGetValue(column, out int index) && index < row.Length && row[index].Length > 0
            ? CsvParser.GetSecret(text, row[index])
            : ProtectedSecret.Empty;

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string DescribeFormat(Dictionary<ImportColumn, int> map, string sourceLabel)
    {
        string format = map.ContainsKey(ImportColumn.OtpAuth) && map.ContainsKey(ImportColumn.Title)
            ? "exportación de Contraseñas de Apple"
            : map.ContainsKey(ImportColumn.Title) && map.ContainsKey(ImportColumn.Url) && map.ContainsKey(ImportColumn.Username)
                ? "exportación CSV (iCloud para Windows o navegador)"
                : "CSV genérico";
        return $"{sourceLabel} · {format}";
    }

    private static bool IsZip(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;

    private static ParseResult ReadZip(byte[] data, int length)
    {
        List<ZipArchiveEntry> candidates;
        using var memory = new MemoryStream(data, 0, length, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(memory, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new ImportFileException("El archivo ZIP está dañado o no es válido.", ex);
        }

        using (archive)
        {
            candidates = archive.Entries
                .Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    && !e.FullName.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => LooksLikePasswordFile(e.Name))
                .Take(10)
                .ToList();

            if (candidates.Count == 0)
            {
                throw new ImportFileException("El archivo ZIP no contiene ningún archivo CSV de contraseñas.");
            }

            ImportFileException? lastError = null;
            foreach (var entry in candidates)
            {
                if (entry.Length <= 0 || entry.Length > MaxFileBytes)
                {
                    lastError = new ImportFileException("El CSV dentro del ZIP está vacío o es demasiado grande.");
                    continue;
                }

                byte[] buffer = SecureMemory.AllocatePinned((int)entry.Length);
                try
                {
                    int total;
                    using (var stream = entry.Open())
                    {
                        total = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                        if (stream.ReadByte() != -1)
                        {
                            // El tamaño declarado no coincide con el real: posible ZIP manipulado.
                            throw new ImportFileException("El archivo ZIP no es coherente y no se importará.");
                        }
                    }

                    string label = LooksLikePasswordFile(entry.Name) ? "Archivo ZIP de Apple" : "Archivo ZIP";
                    return ParseCsv(buffer.AsSpan(0, total), label);
                }
                catch (ImportFileException ex)
                {
                    lastError = ex;
                }
                catch (InvalidDataException ex)
                {
                    lastError = new ImportFileException("El archivo ZIP está dañado o no es válido.", ex);
                }
                finally
                {
                    SecureMemory.Zero(buffer);
                }
            }

            throw lastError ?? new ImportFileException("El archivo ZIP no contiene ningún archivo CSV de contraseñas.");
        }
    }

    internal static bool LooksLikePasswordFile(string name)
    {
        string folded = TextNormalizer.Fold(name);
        return folded.Contains("password", StringComparison.Ordinal)
            || folded.Contains("contrasen", StringComparison.Ordinal)
            || folded.Contains("passwort", StringComparison.Ordinal)
            || folded.Contains("keychain", StringComparison.Ordinal);
    }
}
