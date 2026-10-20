using PassDesk.Core.Diagnostics;

namespace PassDesk.Import.Sources;

public enum SourceAvailability
{
    Available,
    Unavailable,
}

public sealed record SourceStatus(SourceAvailability Availability, string Explanation);

public sealed record CredentialSourceRequest(string? FilePath);

public sealed class SyncSourceUnavailableException(string message) : Exception(message);

/// <summary>
/// Punto de extensión para orígenes de credenciales (opción C de la arquitectura).
/// Una futura integración oficial de Apple solo tendría que implementar esta interfaz y
/// registrarse en <see cref="CredentialSourceRegistry"/>; el resto de la aplicación (vista previa,
/// duplicados, cifrado y guardado) se reutiliza sin cambios.
/// </summary>
public interface ICredentialSource
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Indica si la fuente podría sincronizar sin intervención del usuario.</summary>
    bool SupportsAutomaticSync { get; }

    SourceStatus GetStatus();

    Task<ParseResult> FetchAsync(CredentialSourceRequest request, CancellationToken cancellationToken);
}

/// <summary>Importación desde los archivos de exportación oficiales de Apple (CSV o ZIP).</summary>
public sealed class AppleExportFileSource : ICredentialSource
{
    public string Id => "apple-export";

    public string DisplayName => "Exportación de Contraseñas de Apple (CSV o ZIP)";

    public bool SupportsAutomaticSync => false;

    public SourceStatus GetStatus() => new(
        SourceAvailability.Available,
        "Importación manual a partir de la exportación oficial de Apple (iPhone, iCloud para Windows o Mac).");

    public Task<ParseResult> FetchAsync(CredentialSourceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new ImportFileException("No se ha seleccionado ningún archivo.");
        }

        return Task.Run(() => ExportFileReader.ReadFile(request.FilePath), cancellationToken);
    }
}

/// <summary>
/// Sincronización directa con iCloud o el iPhone. Se registra para que la arquitectura la contemple,
/// pero no está disponible: Apple no publica ninguna API que permita a aplicaciones de terceros leer
/// las contraseñas de iCloud o del iPhone, y PassDesk no accede a datos internos de otras aplicaciones.
/// </summary>
public sealed class ICloudDirectSyncSource : ICredentialSource
{
    public const string Explanation =
        "Apple no ofrece una API pública para que otras aplicaciones lean las contraseñas de iCloud o del iPhone. " +
        "La app oficial «Contraseñas de iCloud» para Windows sí sincroniza, pero no comparte los datos con otras aplicaciones. " +
        "Por eso PassDesk usa la importación manual de la exportación oficial.";

    public string Id => "icloud-direct";

    public string DisplayName => "Sincronización directa con iCloud / iPhone";

    public bool SupportsAutomaticSync => true;

    public SourceStatus GetStatus() => new(SourceAvailability.Unavailable, Explanation);

    public Task<ParseResult> FetchAsync(CredentialSourceRequest request, CancellationToken cancellationToken)
    {
        AppLog.Warn(LogEvent.SyncSourceUnavailable);
        throw new SyncSourceUnavailableException(Explanation);
    }
}

public sealed class CredentialSourceRegistry
{
    private readonly List<ICredentialSource> _sources;

    public CredentialSourceRegistry(IEnumerable<ICredentialSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
    }

    public static CredentialSourceRegistry CreateDefault() => new([new AppleExportFileSource(), new ICloudDirectSyncSource()]);

    public IReadOnlyList<ICredentialSource> Sources => _sources;

    public ICredentialSource Get(string id) =>
        _sources.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("Fuente de credenciales desconocida.");

    /// <summary>Primera fuente con sincronización automática disponible, o null si no hay ninguna.</summary>
    public ICredentialSource? FindAutomaticSyncSource() =>
        _sources.FirstOrDefault(s => s.SupportsAutomaticSync && s.GetStatus().Availability == SourceAvailability.Available);
}
