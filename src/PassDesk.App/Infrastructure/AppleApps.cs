using System.Diagnostics;

namespace PassDesk.App.Infrastructure;

/// <summary>Acceso a la app oficial «Contraseñas de iCloud» (iCloud para Windows, Microsoft Store).</summary>
internal static class AppleApps
{
    private const string PackageFamily = "AppleInc.iCloud_nzyj5cx40ttqa";
    private const string PasswordsAppId = PackageFamily + "!iCloudPasswords";

    public static bool IsICloudInstalled =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", PackageFamily));

    /// <summary>Abre Contraseñas de iCloud para exportar. PassDesk no interactúa con ella ni lee sus datos.</summary>
    public static bool TryOpenICloudPasswords()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + PasswordsAppId) { UseShellExecute = false });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
