using System.Globalization;
using System.Text;

namespace PassDesk.Core.Diagnostics;

public enum LogLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>Eventos registrables. El registro solo admite estos códigos y contadores numéricos.</summary>
public enum LogEvent
{
    AppStarted,
    AppExiting,
    VaultCreated,
    VaultUnlocked,
    VaultUnlockFailed,
    VaultUnlockThrottled,
    VaultQuickUnlock,
    VaultLocked,
    VaultSaved,
    VaultLoadFailed,
    VaultSaveFailed,
    MasterPasswordChanged,
    EntryAdded,
    EntryUpdated,
    EntryDeleted,
    ImportParsed,
    ImportRejected,
    ImportApplied,
    ImportCanceled,
    SecureDeleteCompleted,
    SecureDeleteFailed,
    ClipboardCopied,
    ClipboardCleared,
    ClipboardClearSkipped,
    ClipboardFailed,
    BackupExported,
    BackupRestoreOpened,
    BackupFailed,
    SettingsLoaded,
    SettingsSaved,
    SettingsInvalid,
    HotkeyRegistered,
    HotkeyRegistrationFailed,
    WindowsHelloResult,
    CaptureProtectionFailed,
    SyncSourceUnavailable,
    DocumentsAdded,
    DocumentAddFailed,
    DocumentExported,
    DocumentDeleted,
    DocumentsRestored,
    DocumentPreviewFailed,
    UnhandledException,
    VaultKdfUpgraded,
    VaultKdfUpgradeFailed,
    VaultChangeRolledBack,
    RecoveryPointCreated,
    QuickUnlockExpired,
    SecureDeleteRefused,
    SecurityStateInvalid,
    CryptoUnavailable,
    RecoveryPointsDeleted,
    TrayIconFailed,
    StartupRegistrationChanged,
    StartupRegistrationFailed,
    BrowserAutofillStarted,
    BrowserAutofillStopped,
    BrowserAutofillStartFailed,
    BrowserAutofillListed,
    BrowserAutofillFilled,
    BrowserAutofillDenied,
    BrowserAutofillRateLimited,
    BrowserAutofillUnlockRequested,
    BrowserAutofillBadRequest,
    BrowserAutofillClientRejected,
    BrowserAutofillFailed,
}

/// <summary>Contador numérico asociado a un evento (por ejemplo, número de registros importados).</summary>
public readonly record struct LogMetric(string Name, long Value);

public readonly record struct LogRecord(DateTimeOffset Timestamp, LogLevel Level, LogEvent Event, string Details)
{
    public string Format() =>
        string.Create(CultureInfo.InvariantCulture, $"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} {Level.ToString().ToUpperInvariant()} {Event}{(Details.Length > 0 ? " " + Details : string.Empty)}");
}

public interface ILogSink
{
    void Write(in LogRecord record);
}

/// <summary>
/// Registro de diagnóstico sin secretos por diseño: solo acepta un código de evento, contadores
/// numéricos y, para errores, el tipo de excepción y su HRESULT. Nunca se registran mensajes de
/// excepción (podrían contener fragmentos de archivos o datos del usuario), nombres de cuentas,
/// URL, usuarios ni contraseñas.
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();
    private static ILogSink[] _sinks = [];

    public static void AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (Gate)
        {
            _sinks = [.. _sinks, sink];
        }
    }

    public static void RemoveSink(ILogSink sink)
    {
        lock (Gate)
        {
            _sinks = _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray();
        }
    }

    public static void Info(LogEvent logEvent, params ReadOnlySpan<LogMetric> metrics) =>
        Write(LogLevel.Information, logEvent, null, metrics);

    public static void Warn(LogEvent logEvent, params ReadOnlySpan<LogMetric> metrics) =>
        Write(LogLevel.Warning, logEvent, null, metrics);

    public static void Error(LogEvent logEvent, Exception? exception = null, params ReadOnlySpan<LogMetric> metrics) =>
        Write(LogLevel.Error, logEvent, exception, metrics);

    private static void Write(LogLevel level, LogEvent logEvent, Exception? exception, ReadOnlySpan<LogMetric> metrics)
    {
        var sinks = _sinks;
        if (sinks.Length == 0)
        {
            return;
        }

        var details = new StringBuilder();
        foreach (var metric in metrics)
        {
            if (details.Length > 0)
            {
                details.Append(' ');
            }

            details.Append(SanitizeName(metric.Name)).Append('=').Append(metric.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (exception is not null)
        {
            if (details.Length > 0)
            {
                details.Append(' ');
            }

            // Solo tipo y HRESULT: el mensaje de la excepción puede contener datos sensibles.
            details.Append("exception=").Append(exception.GetType().FullName)
                .Append(" hresult=0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }

        var record = new LogRecord(DateTimeOffset.UtcNow, level, logEvent, details.ToString());
        foreach (var sink in sinks)
        {
            try
            {
                sink.Write(record);
            }
            catch (IOException)
            {
                // El registro nunca debe interrumpir la aplicación.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Los nombres de contador solo pueden contener letras ASCII, dígitos y guiones bajos.</summary>
    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "value" : builder.ToString();
    }
}

/// <summary>Sumidero en memoria, útil para pruebas y diagnósticos.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly Lock _gate = new();
    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    public string AllText => string.Join(Environment.NewLine, Records.Select(r => r.Format()));

    public void Write(in LogRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }
    }
}
