using System.Security.Cryptography;
using System.Text;

namespace PassDesk.Browser.Ipc;

/// <summary>Nombre de la tubería local. Depende de la carpeta de datos, igual que la instancia única, para que un perfil de pruebas no se mezcle con el real.</summary>
public static class PipeNames
{
    /// <summary>Variable de entorno que cambia la carpeta de datos (la misma que usa PassDesk).</summary>
    public const string DataDirVariable = "PASSDESK_DATA_DIR";

    public static string ForDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string full = Path.GetFullPath(dataRoot);
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
        return $"PassDesk.Autofill.{suffix}";
    }

    /// <summary>Carpeta de datos que PassDesk usaría en este equipo para el usuario actual (misma lógica que <c>AppPaths.CreateDefault</c>).</summary>
    public static string CurrentDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(DataDirVariable);
        return !string.IsNullOrWhiteSpace(overrideRoot) && Path.IsPathFullyQualified(overrideRoot)
            ? overrideRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PassDesk");
    }

    public static string ForCurrentUser() => ForDataRoot(CurrentDataRoot());
}
