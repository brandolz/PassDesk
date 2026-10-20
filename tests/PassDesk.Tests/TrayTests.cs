using System.Runtime.CompilerServices;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>Icono de la bandeja: qué hace cada opción del menú, cuándo pasa la ventana a la bandeja y qué se bloquea.</summary>
public class TrayTests
{
    [Fact]
    public void Menu_has_fixed_options_in_order_and_open_is_the_default()
    {
        using var session = UnlockedSession();
        var items = new TrayController(new FakeTrayHost(session)).MenuItems;

        Assert.Equal(new[] { "Abrir", "Bloquear ahora", "Búsqueda rápida", "Salir" }, items.Select(i => i.Text));
        Assert.Same(items[0], Assert.Single(items, i => i.IsDefault));
        Assert.Same(items[3], Assert.Single(items, i => i.SeparatorBefore));
    }

    [Fact]
    public void Each_menu_option_does_what_it_says()
    {
        using var session = UnlockedSession();
        var host = new FakeTrayHost(session) { WindowVisible = false };
        var tray = new TrayController(host);

        Item(tray, "Abrir").Invoke();
        Assert.Equal(new[] { "show" }, host.Calls);
        Assert.True(host.WindowVisible);

        Item(tray, "Búsqueda rápida").Invoke();
        Assert.Equal("quick-search", host.Calls[^1]);

        Item(tray, "Bloquear ahora").Invoke();
        Assert.Equal("lock:Manual", host.Calls[^1]);
        Assert.Equal(VaultState.Locked, session.State);

        Assert.False(tray.IsExiting);
        Item(tray, "Salir").Invoke();
        Assert.Equal("shutdown", host.Calls[^1]);
        Assert.True(tray.IsExiting);
    }

    [Fact]
    public void Lock_now_is_disabled_while_the_vault_is_locked()
    {
        using var session = UnlockedSession();
        var tray = new TrayController(new FakeTrayHost(session));
        Assert.True(Item(tray, "Bloquear ahora").Enabled);

        session.Lock(LockReason.Manual);

        Assert.False(Item(tray, "Bloquear ahora").Enabled);
        Assert.All(tray.MenuItems.Where(i => i.Text != "Bloquear ahora"), i => Assert.True(i.Enabled));
    }

    [Fact]
    public void Double_click_or_enter_opens_the_window_and_right_click_shows_the_menu()
    {
        using var session = UnlockedSession();
        var host = new FakeTrayHost(session);
        var tray = new TrayController(host);

        Assert.False(tray.OnIconMessage(NativeMethods.WM_LBUTTONDBLCLK));
        Assert.False(tray.OnIconMessage(NativeMethods.NIN_KEYSELECT));
        Assert.True(tray.OnIconMessage(NativeMethods.WM_CONTEXTMENU));
        Assert.False(tray.OnIconMessage(0x0400)); // NIN_SELECT: un solo clic no abre nada
        Assert.False(tray.OnIconMessage(0x0202)); // WM_LBUTTONUP
        Assert.Equal(new[] { "show", "show" }, host.Calls);
    }

    [Fact]
    public void Closing_the_window_hides_it_in_the_tray_and_applies_lock_on_minimize()
    {
        using var session = UnlockedSession();
        var host = new FakeTrayHost(session);
        var tray = new TrayController(host);

        Assert.True(tray.OnClosing());
        Assert.False(host.WindowVisible);
        Assert.Equal(VaultState.Unlocked, session.State); // por defecto no se bloquea, igual que al minimizar

        host.Settings = new AppSettings { LockOnMinimize = true };
        Assert.True(tray.OnClosing());
        Assert.Equal("lock:Minimized", host.Calls[^1]);
        Assert.Equal(VaultState.Locked, session.State);
    }

    [Fact]
    public void Closing_exits_in_minimize_mode_after_exit_or_without_an_icon()
    {
        using var session = UnlockedSession();
        var host = new FakeTrayHost(session) { Settings = new AppSettings { TrayMode = TrayMode.MinimizeToTray } };
        var tray = new TrayController(host);
        Assert.False(tray.OnClosing()); // cerrar sale de PassDesk, como antes

        host.Settings = new AppSettings();
        host.IsTrayIconShown = false;
        Assert.False(tray.OnClosing()); // sin icono, una ventana oculta quedaría inaccesible

        host.IsTrayIconShown = true;
        tray.Exit();
        Assert.False(tray.OnClosing()); // «Salir»: no se vuelve a esconder en la bandeja

        Assert.DoesNotContain("hide", host.Calls);
    }

    [Fact]
    public void Minimizing_hides_only_in_minimize_mode_and_lock_rules_apply_in_both()
    {
        using var session = UnlockedSession();
        var host = new FakeTrayHost(session);
        var tray = new TrayController(host);

        tray.OnMinimized(); // modo «al cerrar»: se minimiza a la barra de tareas
        Assert.Empty(host.Calls);

        host.Settings = new AppSettings { TrayMode = TrayMode.MinimizeToTray };
        tray.OnMinimized();
        Assert.Equal(new[] { "hide" }, host.Calls);
        Assert.Equal(VaultState.Unlocked, session.State);

        host.Settings = new AppSettings { LockOnMinimize = true };
        tray.OnMinimized();
        Assert.Equal(new[] { "hide", "lock:Minimized" }, host.Calls);
        Assert.Equal(VaultState.Locked, session.State);
    }

    [Fact]
    public void Icon_text_and_menu_never_show_vault_data()
    {
        using var session = UnlockedSession();
        session.AddEntry(Fake.Entry("Banco Ficticio", "https://banco-ficticio.example", "ana.prueba@example.com", Fake.Secret, notes: "Nota ficticia"));
        var tray = new TrayController(new FakeTrayHost(session));

        string[] unlocked = [TrayController.ToolTip, .. tray.MenuItems.Select(i => i.Text)];
        session.Lock(LockReason.Manual);
        string[] locked = [TrayController.ToolTip, .. tray.MenuItems.Select(i => i.Text)];

        Assert.Equal("PassDesk", TrayController.ToolTip);
        Assert.Equal(locked, unlocked);
        foreach (string text in unlocked)
        {
            Assert.DoesNotContain("Ficticio", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ana.prueba", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Fake.Secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Notify_icon_data_has_the_size_windows_expects()
    {
        // NOTIFYICONDATAW completa (Windows Vista o posterior) en 64 bits; con otro tamaño, Shell_NotifyIcon falla.
        Assert.Equal(976, Unsafe.SizeOf<NativeMethods.NOTIFYICONDATAW>());
    }

    [Fact]
    public void Tray_mode_defaults_to_close_round_trips_and_impossible_values_are_normalized()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        Assert.Equal(TrayMode.CloseToTray, store.Load().TrayMode);

        store.Save(new AppSettings { TrayMode = TrayMode.MinimizeToTray });
        Assert.Contains("\"TrayMode\": \"MinimizeToTray\"", File.ReadAllText(store.FilePath));
        Assert.Equal(TrayMode.MinimizeToTray, store.Load().TrayMode);

        // Ajustes de una versión anterior (sin el campo) o editados a mano con un valor que no existe.
        File.WriteAllText(store.FilePath, "{ \"AutoLockMinutes\": 15 }");
        Assert.Equal(TrayMode.CloseToTray, store.Load().TrayMode);
        File.WriteAllText(store.FilePath, "{ \"TrayMode\": 7 }");
        Assert.Equal(TrayMode.CloseToTray, store.Load().TrayMode);
        Assert.Equal(TrayMode.CloseToTray, new AppSettings { TrayMode = (TrayMode)42 }.Normalize().TrayMode);
    }

    private static VaultSession UnlockedSession()
    {
        var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        Assert.Equal(VaultState.Unlocked, session.State);
        return session;
    }

    private static TrayMenuItem Item(TrayController tray, string text) => tray.MenuItems.Single(i => i.Text == text);
}

/// <summary>«Iniciar con Windows» con una clave Run en memoria: ninguna prueba escribe en el registro real.</summary>
public class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\Ana María\AppData\Local\Programs\PassDesk\PassDesk.exe";

    [Fact]
    public void Disabled_by_default_and_enabling_writes_the_quoted_exe_with_the_tray_argument()
    {
        var runKey = new InMemoryRunKey();
        var startup = new StartupRegistration(runKey, Exe);
        Assert.True(startup.IsAvailable);
        Assert.False(startup.IsEnabled);

        Assert.True(startup.TrySetEnabled(true));

        Assert.Equal("\"C:\\Users\\Ana María\\AppData\\Local\\Programs\\PassDesk\\PassDesk.exe\" --tray", runKey.Values["PassDesk"]);
        Assert.Single(runKey.Values);
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void Disabling_removes_only_its_own_entry()
    {
        var runKey = new InMemoryRunKey();
        var startup = new StartupRegistration(runKey, Exe);
        startup.TrySetEnabled(true);
        runKey.Values["OtraApp"] = "\"C:\\Otra\\otra.exe\"";

        Assert.True(startup.TrySetEnabled(false));
        Assert.False(startup.IsEnabled);
        Assert.Equal(new[] { "OtraApp" }, runKey.Values.Keys);

        // Un valor «PassDesk» que apunta a otro programa no se considera propio ni se borra…
        runKey.Values["PassDesk"] = "\"D:\\Otro\\PassDesk.exe\"";
        Assert.False(startup.IsEnabled);
        Assert.True(startup.TrySetEnabled(false));
        Assert.Equal("\"D:\\Otro\\PassDesk.exe\"", runKey.Values["PassDesk"]);

        // …pero activarlo lo sustituye por esta instalación.
        Assert.True(startup.TrySetEnabled(true));
        Assert.True(startup.IsEnabled);
    }

    [Fact]
    public void Only_offered_for_the_installed_copy_with_the_default_data_folder()
    {
        const string install = @"C:\Users\Ana\AppData\Local\Programs\PassDesk";
        Assert.Equal(install + @"\PassDesk.exe", StartupRegistration.InstalledExecutable(install + @"\PassDesk.exe", install + @"\", defaultDataFolder: true));
        Assert.NotNull(StartupRegistration.InstalledExecutable(@"c:\users\ana\appdata\local\programs\passdesk\PassDesk.exe", install, defaultDataFolder: true));
        Assert.Null(StartupRegistration.InstalledExecutable(@"C:\proyectos\PassDesk\dist\PassDesk\PassDesk.exe", install, defaultDataFolder: true));
        Assert.Null(StartupRegistration.InstalledExecutable(install + @"\otra\PassDesk.exe", install, defaultDataFolder: true));
        Assert.Null(StartupRegistration.InstalledExecutable(install + @"\PassDesk.exe", install, defaultDataFolder: false)); // PASSDESK_DATA_DIR
        Assert.Null(StartupRegistration.InstalledExecutable(null, install, defaultDataFolder: true));

        using var temp = new TempDirectory();
        Assert.True(new AppPaths(AppPaths.DefaultRoot).IsDefault);
        Assert.False(new AppPaths(temp.Path).IsDefault);

        var runKey = new InMemoryRunKey();
        var unavailable = new StartupRegistration(runKey, executablePath: null);
        Assert.False(unavailable.IsAvailable);
        Assert.False(unavailable.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => unavailable.TrySetEnabled(true));
        Assert.Empty(runKey.Values);
    }

    [Fact]
    public void A_refused_registry_write_is_reported_and_logged_without_its_message()
    {
        var memory = new MemoryLogSink();
        AppLog.AddSink(memory);
        try
        {
            var runKey = new InMemoryRunKey { Denied = true };
            var startup = new StartupRegistration(runKey, Exe);

            Assert.False(startup.TrySetEnabled(true));

            Assert.False(startup.IsEnabled);
            Assert.Contains(memory.Records, r => r.Event == LogEvent.StartupRegistrationFailed && r.Details.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
            Assert.DoesNotContain("simulado", memory.AllText, StringComparison.Ordinal);
        }
        finally
        {
            AppLog.RemoveSink(memory);
        }
    }

    [Fact]
    public void The_tray_argument_is_recognized_exactly()
    {
        Assert.True(StartupRegistration.IsTrayLaunch(["--tray"]));
        Assert.True(StartupRegistration.IsTrayLaunch(["--TRAY"]));
        Assert.False(StartupRegistration.IsTrayLaunch([]));
        Assert.False(StartupRegistration.IsTrayLaunch(["--trayx"]));
        Assert.False(StartupRegistration.IsTrayLaunch([@"C:\Descargas\Contraseñas.csv"]));
    }
}

internal sealed class FakeTrayHost(VaultSession session) : ITrayHost
{
    public AppSettings Settings { get; set; } = new();

    public bool IsUnlocked => session.State == VaultState.Unlocked;

    public bool IsTrayIconShown { get; set; } = true;

    public bool WindowVisible { get; set; } = true;

    public List<string> Calls { get; } = [];

    public void ShowMainWindow()
    {
        Calls.Add("show");
        WindowVisible = true;
    }

    public void HideMainWindow()
    {
        Calls.Add("hide");
        WindowVisible = false;
    }

    public void ShowQuickSearch() => Calls.Add("quick-search");

    public void LockNow(LockReason reason)
    {
        Calls.Add("lock:" + reason);
        session.Lock(reason);
    }

    public void Shutdown() => Calls.Add("shutdown");
}

/// <summary>Clave Run en memoria (los nombres de valor del registro no distinguen mayúsculas).</summary>
internal sealed class InMemoryRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simula un antivirus o una directiva que impide escribir en la clave.</summary>
    public bool Denied { get; set; }

    public string? GetValue(string name) => Values.GetValueOrDefault(name);

    public void SetValue(string name, string value)
    {
        ThrowIfDenied();
        Values[name] = value;
    }

    public void DeleteValue(string name)
    {
        ThrowIfDenied();
        Values.Remove(name);
    }

    private void ThrowIfDenied()
    {
        if (Denied)
        {
            throw new UnauthorizedAccessException("Acceso denegado (simulado).");
        }
    }
}
