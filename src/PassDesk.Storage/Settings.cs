using System.Text.Json;
using System.Text.Json.Serialization;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Search;
using PassDesk.Crypto;

namespace PassDesk.Storage;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Cuándo pasa la ventana principal a la bandeja del sistema (el icono está siempre).</summary>
public enum TrayMode
{
    /// <summary>Al cerrarla (para salir: «Salir» en el menú del icono). Minimizar funciona como siempre.</summary>
    CloseToTray,

    /// <summary>Al minimizarla. Cerrarla sale de PassDesk.</summary>
    MinimizeToTray,
}

/// <summary>Ajustes de la aplicación. No contienen secretos y se guardan en claro.</summary>
public sealed class AppSettings
{
    public static readonly int[] AutoLockChoices = [1, 2, 5, 10, 15, 30, 60];
    public static readonly int[] ClipboardChoices = [10, 20, 30, 45, 60, 90, 120];

    public int AutoLockMinutes { get; set; } = 5;

    public int ClipboardClearSeconds { get; set; } = 30;

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public string QuickSearchHotkey { get; set; } = "Ctrl+Shift+Space";

    public bool QuickSearchEnabled { get; set; } = true;

    public bool ScreenCaptureProtection { get; set; } = true;

    /// <summary>También se aplica al pasar a la bandeja del sistema.</summary>
    public bool LockOnMinimize { get; set; }

    public TrayMode TrayMode { get; set; } = TrayMode.CloseToTray;

    public bool LockOnSessionLock { get; set; } = true;

    /// <summary>
    /// Desactivado por defecto: mientras esté activo, cualquiera que conozca el PIN de Windows puede abrir
    /// PassDesk sin la contraseña maestra (durante unas horas y sin reiniciar la app).
    /// </summary>
    public bool WindowsHelloQuickUnlock { get; set; }

    public SortMode SortMode { get; set; } = SortMode.NameAscending;

    /// <summary>Detecta exportaciones de Apple nuevas en Escritorio, Descargas y Documentos.</summary>
    public bool DetectNewExports { get; set; } = true;

    /// <summary>Borra de forma segura el archivo exportado después de importarlo.</summary>
    public bool DeleteExportAfterImport { get; set; } = true;

    /// <summary>Recuerda actualizar desde Apple si pasan 30 días sin importar.</summary>
    public bool UpdateReminder { get; set; } = true;

    /// <summary>Ajusta valores fuera de rango (por ejemplo, si el archivo se editó a mano).</summary>
    public AppSettings Normalize()
    {
        AutoLockMinutes = Math.Clamp(AutoLockMinutes, 1, 240);
        ClipboardClearSeconds = Math.Clamp(ClipboardClearSeconds, 5, 600);
        if (!Enum.IsDefined(Theme))
        {
            Theme = ThemePreference.System;
        }

        if (!Enum.IsDefined(SortMode))
        {
            SortMode = SortMode.NameAscending;
        }

        if (!Enum.IsDefined(TrayMode))
        {
            TrayMode = TrayMode.CloseToTray;
        }

        if (string.IsNullOrWhiteSpace(QuickSearchHotkey) || QuickSearchHotkey.Length > 64)
        {
            QuickSearchHotkey = "Ctrl+Shift+Space";
        }

        return this;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(JsonFile.WithoutBom(File.ReadAllBytes(FilePath)), Options) ?? new AppSettings();
            AppLog.Info(LogEvent.SettingsLoaded);
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Warn(LogEvent.SettingsInvalid);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(settings.Clone().Normalize(), Options), keepPrevious: false);
        AppLog.Info(LogEvent.SettingsSaved);
    }
}

/// <summary>Estado de la limitación de intentos de desbloqueo (persistido entre reinicios).</summary>
public sealed record UnlockThrottleState(int FailedAttempts, DateTimeOffset? LockedUntilUtc)
{
    public static UnlockThrottleState None { get; } = new(0, null);
}

public interface ISecurityStateStore
{
    /// <summary>Estado guardado, o el inicial si no hay ninguno.</summary>
    /// <exception cref="InvalidDataException">Existe, pero está dañado.</exception>
    UnlockThrottleState Load();

    void Save(UnlockThrottleState state);
}

/// <summary>
/// Contador de intentos fallidos en disco, cifrado con DPAPI (cuenta de Windows actual). Si el archivo está dañado, la
/// sesión aplica la espera máxima en lugar de volver a cero. Límite: quien controle la sesión de Windows puede borrarlo,
/// y sin archivo se vuelve al estado inicial.
/// </summary>
public sealed class FileSecurityStateStore : ISecurityStateStore
{
    public FileSecurityStateStore(string path)
    {
        FilePath = path;
    }

    public string FilePath { get; }

    public UnlockThrottleState Load()
    {
        if (!File.Exists(FilePath))
        {
            return UnlockThrottleState.None;
        }

        byte[] data = File.ReadAllBytes(FilePath);
        try
        {
            // Las versiones anteriores lo guardaban en JSON sin cifrar; aceptarlo no da más margen que borrar el archivo.
            ReadOnlySpan<byte> json = JsonFile.WithoutBom(data);
            if (json is not [(byte)'{', ..])
            {
                json = WindowsDataProtection.Unprotect(data);
            }

            return JsonSerializer.Deserialize<UnlockThrottleState>(json) ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or VaultAccessDeniedException or NotSupportedException)
        {
            throw new InvalidDataException("El estado de seguridad está dañado.");
        }
    }

    public void Save(UnlockThrottleState state) =>
        AtomicFile.Write(FilePath, WindowsDataProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(state)), keepPrevious: false);
}

internal static class JsonFile
{
    /// <summary>Admite archivos guardados con BOM UTF-8 (por ejemplo, editados con el Bloc de notas).</summary>
    public static ReadOnlySpan<byte> WithoutBom(byte[] data) =>
        data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? data.AsSpan(3) : data;
}

public sealed class InMemorySecurityStateStore : ISecurityStateStore
{
    public UnlockThrottleState State { get; set; } = UnlockThrottleState.None;

    public UnlockThrottleState Load() => State;

    public void Save(UnlockThrottleState state) => State = state;
}
