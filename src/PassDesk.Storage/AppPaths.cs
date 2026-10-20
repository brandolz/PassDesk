using System.Text;
using PassDesk.Core.Diagnostics;

namespace PassDesk.Storage;

/// <summary>
/// Rutas de datos locales. Por defecto %LOCALAPPDATA%\PassDesk, una carpeta que Windows solo
/// permite leer a la cuenta del usuario (y a administradores/SYSTEM) y que no se sincroniza con
/// OneDrive ni se mueve con los perfiles itinerantes.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// Ruta por defecto. La variable de entorno PASSDESK_DATA_DIR permite usar otra carpeta
    /// (perfil de pruebas con datos ficticios o instalación portátil).
    /// </summary>
    public static AppPaths CreateDefault()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("PASSDESK_DATA_DIR");
        return !string.IsNullOrWhiteSpace(overrideRoot) && Path.IsPathFullyQualified(overrideRoot)
            ? new AppPaths(overrideRoot)
            : new AppPaths(DefaultRoot);
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PassDesk");

    public string Root { get; }

    /// <summary>Es la carpeta de datos normal (no un perfil de pruebas ni una instalación portátil).</summary>
    public bool IsDefault => string.Equals(Root, Path.GetFullPath(DefaultRoot), StringComparison.OrdinalIgnoreCase);

    public string VaultFile => Path.Combine(Root, "vault.pdv");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SecurityStateFile => Path.Combine(Root, "security.json");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string DocumentsDirectory => Path.Combine(Root, "docs");

    /// <summary>Puntos de recuperación automáticos (copias cifradas de la bóveda antes de importar o restaurar).</summary>
    public string RecoveryDirectory => Path.Combine(Root, "recuperacion");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DocumentsDirectory);
    }
}

/// <summary>Registro rotativo en disco (máximo ~1 MB entre los dos archivos). Sin secretos por diseño.</summary>
public sealed class FileLogSink : ILogSink
{
    private const long MaxBytes = 512 * 1024;
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly string _previousPath;

    public FileLogSink(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "passdesk.log");
        _previousPath = Path.Combine(directory, "passdesk.1.log");
    }

    public string FilePath => _path;

    public void Write(in LogRecord record)
    {
        string line = record.Format() + Environment.NewLine;
        lock (_gate)
        {
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(_path, _previousPath, overwrite: true);
            }

            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }
}
