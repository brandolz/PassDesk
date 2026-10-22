using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Services;
using PassDesk.Import;
using PassDesk.Import.Sources;
using PassDesk.Storage;

namespace PassDesk.App;

/// <summary>
/// Raíz de composición: crea y conecta los módulos (almacenamiento, autenticación, cifrado,
/// importación) con los servicios de la plataforma Windows.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly DispatcherTimer _autoLockTimer;
    private readonly Dispatcher _dispatcher;
    private nint _clipboardOwner;
    private ExportFolderWatcher? _exportWatcher;

    private AppServices(AppPaths paths, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Paths = paths;
        SettingsStore = new SettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        Session = new VaultSession(
            new DpapiVaultStore(paths.VaultFile, paths.RecoveryDirectory),
            new FileSecurityStateStore(paths.SecurityStateFile),
            SystemClock.Instance)
        {
            QuickUnlockEnabled = Settings.WindowsHelloQuickUnlock,
        };
        Documents = new SecureDocumentService(Session, new DpapiDocumentStore(paths.DocumentsDirectory), SystemClock.Instance);
        Inactivity = new InactivityMonitor(SystemClock.Instance, TimeSpan.FromMinutes(Settings.AutoLockMinutes));
        AutoLock = new AutoLockController(Session, Inactivity);
        Clipboard = new ClipboardGuard(
            new WindowsClipboardService(() => _clipboardOwner),
            new DispatcherDelayScheduler(dispatcher),
            () => TimeSpan.FromSeconds(Settings.ClipboardClearSeconds));
        Presence = new WindowsHelloVerifier(() => ActiveWindowHandle());
        Sources = CredentialSourceRegistry.CreateDefault();
        Theme = new ThemeService();
        Startup = new StartupRegistration(
            new RegistryRunKey(),
            StartupRegistration.InstalledExecutable(Environment.ProcessPath, StartupRegistration.DefaultInstallDirectory, paths.IsDefault));

        // Bloqueo por inactividad: se comprueba cada pocos segundos.
        _autoLockTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => AutoLock.Tick(), dispatcher);
        _autoLockTimer.Start();
        InputManager.Current.PreProcessInput += OnPreProcessInput;

        // Bloqueo al bloquear Windows (Win+L) o al suspender el equipo.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Al bloquear, se borra del portapapeles lo que haya copiado PassDesk.
        Session.StateChanged += (_, e) =>
        {
            if (e.NewState == VaultState.Locked)
            {
                dispatcher.BeginInvoke(() => SafeClearClipboard());
            }
        };

        ConfigureExportWatcher();
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    public VaultSession Session { get; }

    /// <summary>Documentos cifrados (DNI, pasaporte, PDF, fotos…).</summary>
    public SecureDocumentService Documents { get; }

    public InactivityMonitor Inactivity { get; }

    public AutoLockController AutoLock { get; }

    public ClipboardGuard Clipboard { get; }

    public IUserPresenceVerifier Presence { get; }

    public CredentialSourceRegistry Sources { get; }

    internal ThemeService Theme { get; }

    /// <summary>«Iniciar con Windows» (entrada en HKCU\…\Run).</summary>
    internal StartupRegistration Startup { get; }

    internal HotkeyService? Hotkeys { get; private set; }

    public event EventHandler? SettingsChanged;

    public event EventHandler? QuickSearchRequested;

    /// <summary>Se produce (en el hilo de la interfaz) al detectar una exportación de contraseñas nueva.</summary>
    public event EventHandler<ExportDetectedEventArgs>? ExportDetected;

    /// <summary>Exportación detectada que todavía no se ha importado ni descartado.</summary>
    public string? PendingExportPath { get; private set; }

    /// <summary>Marca una exportación como atendida (importada, descartada o eliminada) para no volver a avisar.</summary>
    public void MarkExportHandled(string path)
    {
        _exportWatcher?.MarkHandled(path);
        if (string.Equals(PendingExportPath, path, StringComparison.OrdinalIgnoreCase))
        {
            PendingExportPath = null;
        }
    }

    public void DismissPendingExport()
    {
        if (PendingExportPath is { } path)
        {
            MarkExportHandled(path);
        }
    }

    /// <summary>
    /// Vigila Escritorio, Descargas y Documentos para detectar exportaciones de Apple recién guardadas
    /// (desde iCloud para Windows, LocalSend, el navegador…). Solo se examinan cabeceras, nunca datos.
    /// </summary>
    private void ConfigureExportWatcher()
    {
        if (Settings.DetectNewExports && _exportWatcher is null)
        {
            string?[] folders =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                NativeMethods.GetKnownFolder(NativeMethods.FolderIdDownloads),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ];
            _exportWatcher = new ExportFolderWatcher(folders.OfType<string>());
            _exportWatcher.ExportDetected += (_, e) => _dispatcher.BeginInvoke(() =>
            {
                PendingExportPath = e.Path;
                ExportDetected?.Invoke(this, e);
            });
            _exportWatcher.Enabled = true;
            _exportWatcher.ScanRecent(TimeSpan.FromHours(24));
        }
        else if (!Settings.DetectNewExports && _exportWatcher is not null)
        {
            _exportWatcher.Dispose();
            _exportWatcher = null;
            PendingExportPath = null;
        }
    }

    /// <summary>Integración con navegadores (extensión + conector). Solo escucha si está activada en Ajustes.</summary>
    internal AutofillService Autofill { get; private set; } = null!;

    public static AppServices Create(AppPaths paths, Dispatcher dispatcher)
    {
        var services = new AppServices(paths, dispatcher);
        services.Autofill = new AutofillService(services, dispatcher);
        services.Autofill.Apply(services.Settings);
        return services;
    }

    /// <summary>Se llama cuando la ventana principal ya tiene identificador (HWND).</summary>
    internal void AttachMainWindow(Window window)
    {
        _clipboardOwner = new WindowInteropHelper(window).Handle;
        Hotkeys = new HotkeyService(_clipboardOwner);
        Hotkeys.Pressed += (_, _) => QuickSearchRequested?.Invoke(this, EventArgs.Empty);
        RegisterHotkey();
    }

    /// <summary>Registra el atajo global configurado. Devuelve false si no se pudo.</summary>
    internal bool RegisterHotkey()
    {
        if (Hotkeys is null)
        {
            return false;
        }

        Hotkeys.Unregister();
        return Settings.QuickSearchEnabled
            && HotkeyGesture.TryParse(Settings.QuickSearchHotkey, out var gesture)
            && Hotkeys.Register(gesture);
    }

    public void UpdateSettings(AppSettings settings)
    {
        var previous = Settings;
        Settings = settings.Clone().Normalize();
        SettingsStore.Save(Settings);
        Inactivity.Timeout = TimeSpan.FromMinutes(Settings.AutoLockMinutes);
        Session.QuickUnlockEnabled = Settings.WindowsHelloQuickUnlock;
        if (previous.Theme != Settings.Theme)
        {
            Theme.Apply(Settings.Theme);
        }

        if (previous.QuickSearchEnabled != Settings.QuickSearchEnabled || previous.QuickSearchHotkey != Settings.QuickSearchHotkey)
        {
            RegisterHotkey();
        }

        if (previous.DetectNewExports != Settings.DetectNewExports)
        {
            ConfigureExportWatcher();
        }

        if (previous.BrowserAutofillEnabled != Settings.BrowserAutofillEnabled)
        {
            Autofill.Apply(Settings);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LockNow(LockReason reason)
    {
        Session.Lock(reason);
        SafeClearClipboard();
    }

    public void SafeClearClipboard()
    {
        try
        {
            Clipboard.ClearIfOwned();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            AppLog.Warn(LogEvent.ClipboardFailed);
        }
    }

    public void Dispose()
    {
        _autoLockTimer.Stop();
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        // Al salir ya no habrá reintentos programados: si el portapapeles está ocupado, se insiste un poco.
        try
        {
            Clipboard.ClearIfOwnedNow(attempts: 5, pause: TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            AppLog.Warn(LogEvent.ClipboardFailed);
        }

        Autofill?.Dispose();
        Clipboard.Dispose();
        Hotkeys?.Dispose();
        _exportWatcher?.Dispose();
        Session.Dispose();
    }

    private static nint ActiveWindowHandle()
    {
        var window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        return window is null ? 0 : new WindowInteropHelper(window).Handle;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs or MouseEventArgs)
        {
            Inactivity.RecordActivity();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect))
        {
            return;
        }

        if (Settings.LockOnSessionLock)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => LockNow(LockReason.SessionLocked));
        }
        else
        {
            // Aunque no se bloquee PassDesk, tras bloquear Windows ya no vale Windows Hello: hará falta la contraseña.
            Session.ForgetQuickUnlockKey();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => LockNow(LockReason.Suspend));
        }
    }
}
