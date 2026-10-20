using System.Globalization;
using PassDesk.Core.Search;
using PassDesk.Core.Text;
using PassDesk.Crypto;

namespace PassDesk.Core.Model;

public enum DocumentKind
{
    Image,
    Pdf,
    Text,
    Other,
}

/// <summary>
/// Documento guardado en la bóveda (DNI, pasaporte, PDF, fotos…). Aquí solo hay metadatos y la clave
/// del documento (protegida en memoria); el contenido cifrado vive en un archivo aparte.
/// </summary>
public sealed class SecureDocument : IDisposable
{
    private ProtectedSecret _key = ProtectedSecret.Empty;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    /// <summary>Nombre original del archivo (determina el tipo y el nombre al exportar).</summary>
    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Huella SHA-256 del contenido, para detectar documentos repetidos.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Clave AES-256 del documento.</summary>
    public ProtectedSecret Key => _key;

    public DocumentKind Kind => DocumentKinds.FromFileName(FileName);

    public void SetKey(ProtectedSecret key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!ReferenceEquals(_key, key))
        {
            _key.Dispose();
            _key = key;
        }
    }

    public void Dispose() => _key.Dispose();

    public override string ToString() => $"SecureDocument({Id})";
}

public static class DocumentKinds
{
    public static DocumentKind FromFileName(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".webp" => DocumentKind.Image,
            ".pdf" => DocumentKind.Pdf,
            ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" => DocumentKind.Text,
            _ => DocumentKind.Other,
        };

    public static string Describe(DocumentKind kind) => kind switch
    {
        DocumentKind.Image => "Imagen",
        DocumentKind.Pdf => "PDF",
        DocumentKind.Text => "Texto",
        _ => "Archivo",
    };

    public static string FormatSize(long bytes)
    {
        var culture = CultureInfo.GetCultureInfo("es-ES");
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => (bytes / 1024d).ToString("0.#", culture) + " KB",
            _ => (bytes / (1024d * 1024d)).ToString("0.#", culture) + " MB",
        };
    }
}

/// <summary>Búsqueda de documentos por título o nombre de archivo (sin distinguir mayúsculas ni tildes).</summary>
public static class DocumentSearch
{
    public static List<SecureDocument> Apply(IEnumerable<SecureDocument> documents, string? query, SortMode sort)
    {
        ArgumentNullException.ThrowIfNull(documents);
        string[] terms = TextNormalizer.Terms(query);
        var matches = documents
            .Where(d =>
            {
                string haystack = TextNormalizer.Fold(d.Title) + "\n" + TextNormalizer.Fold(d.FileName);
                return terms.All(t => haystack.Contains(t, StringComparison.Ordinal));
            })
            .ToList();

        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        matches.Sort((a, b) => sort switch
        {
            SortMode.NameDescending => comparer.Compare(b.Title, a.Title),
            SortMode.ModifiedNewest => b.ModifiedUtc.CompareTo(a.ModifiedUtc),
            SortMode.ModifiedOldest => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            _ => comparer.Compare(a.Title, b.Title),
        });
        return matches;
    }
}
