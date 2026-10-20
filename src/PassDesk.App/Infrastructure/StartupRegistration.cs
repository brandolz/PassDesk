using Microsoft.Win32;
using PassDesk.Core.Diagnostics;

namespace PassDesk.App.Infrastructure;

/// <summary>Valores de la clave Run del registro. Interfaz mínima para que las pruebas no toquen el registro real.</summary>
internal interface IRunKey
{
    string? GetValue(string name);

    void SetValue(string name, string value);

    void DeleteValue(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run: lo que Windows abre al iniciar sesión con esta cuenta.</summary>
internal sealed class RegistryRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// «Iniciar con Windows» (desactivado por defecto): el valor «PassDesk» de la clave Run abre el PassDesk instalado en la
/// bandeja y bloqueado. La única fuente de verdad es el registro; en los ajustes no se guarda nada.
/// </summary>
internal sealed class StartupRegistration(IRunKey runKey, string? executablePath)
{
    public const string ValueName = "PassDesk";

    /// <summary>Argumento de la entrada: arranca sin ventana, solo con el icono de la bandeja.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>Carpeta en la que instala scripts\install.ps1 por defecto.</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PassDesk");

    /// <summary>Solo se ofrece en la copia instalada y con la carpeta de datos por defecto.</summary>
    public bool IsAvailable => executablePath is not null;

    public string? Command => executablePath is null ? null : $"\"{executablePath}\" {TrayArgument}";

    public bool IsEnabled => Command is { } command && string.Equals(runKey.GetValue(ValueName), command, StringComparison.OrdinalIgnoreCase);

    public static bool IsTrayLaunch(IEnumerable<string> args) => args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ejecutable para la entrada: el actual, solo si es la copia instalada y usa la carpeta de datos por defecto. Así, una
    /// compilación de prueba o un perfil con PASSDESK_DATA_DIR (que al iniciar sesión no se abriría) nunca tocan la entrada.
    /// </summary>
    public static string? InstalledExecutable(string? processPath, string installDirectory, bool defaultDataFolder)
    {
        if (!defaultDataFolder || string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        string exe = Path.GetFullPath(processPath);
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        return string.Equals(Path.GetDirectoryName(exe), folder, StringComparison.OrdinalIgnoreCase) ? exe : null;
    }

    /// <summary>Activa o desactiva la entrada. Devuelve false si el registro no lo permite (antivirus, directivas…).</summary>
    public bool TrySetEnabled(bool enabled)
    {
        string command = Command ?? throw new InvalidOperationException("«Iniciar con Windows» solo está disponible en la copia instalada.");
        try
        {
            if (enabled)
            {
                runKey.SetValue(ValueName, command);
            }
            else if (IsEnabled)
            {
                // Solo se borra la entrada propia: un valor «PassDesk» que apunte a otro programa no se toca.
                runKey.DeleteValue(ValueName);
            }

            AppLog.Info(LogEvent.StartupRegistrationChanged, new LogMetric("enabled", enabled ? 1 : 0));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Error(LogEvent.StartupRegistrationFailed, ex);
            return false;
        }
    }
}
