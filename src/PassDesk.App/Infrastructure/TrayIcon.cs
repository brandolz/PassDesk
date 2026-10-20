using System.Windows.Interop;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Storage;

namespace PassDesk.App.Infrastructure;

/// <summary>Una opción del menú del icono de la bandeja.</summary>
internal sealed record TrayMenuItem(string Text, Action Invoke, bool Enabled = true, bool IsDefault = false, bool SeparatorBefore = false);

/// <summary>Lo que la bandeja necesita de la ventana principal y de la aplicación.</summary>
internal interface ITrayHost
{
    AppSettings Settings { get; }

    bool IsUnlocked { get; }

    /// <summary>El icono está en la bandeja. Sin él, ocultar la ventana la dejaría inaccesible.</summary>
    bool IsTrayIconShown { get; }

    void ShowMainWindow();

    void HideMainWindow();

    void ShowQuickSearch();

    void LockNow(LockReason reason);

    /// <summary>Salida normal de la aplicación (App.OnExit).</summary>
    void Shutdown();
}

/// <summary>
/// Qué hace cada opción del menú del icono de la bandeja y cuándo pasa la ventana principal a la bandeja. Está separado
/// del icono de Windows (<see cref="TrayIcon"/>) para poder probarlo. Los textos son fijos: nunca muestran datos de la
/// bóveda.
/// </summary>
internal sealed class TrayController(ITrayHost host)
{
    public const string ToolTip = "PassDesk";

    /// <summary>Se ha elegido «Salir»: el cierre de la ventana ya no se intercepta.</summary>
    public bool IsExiting { get; private set; }

    public IReadOnlyList<TrayMenuItem> MenuItems =>
    [
        new("Abrir", Open, IsDefault: true),
        new("Bloquear ahora", () => host.LockNow(LockReason.Manual), Enabled: host.IsUnlocked),
        new("Búsqueda rápida", host.ShowQuickSearch),
        new("Salir", Exit, SeparatorBefore: true),
    ];

    public void Open() => host.ShowMainWindow();

    public void Exit()
    {
        IsExiting = true;
        host.Shutdown();
    }

    /// <summary>Aviso del icono (NOTIFYICON_VERSION_4). Devuelve true si hay que mostrar el menú.</summary>
    public bool OnIconMessage(int message)
    {
        switch (message)
        {
            case NativeMethods.WM_CONTEXTMENU: // clic derecho, Mayús+F10 o la tecla de menú
                return true;
            case NativeMethods.WM_LBUTTONDBLCLK:
            case NativeMethods.NIN_KEYSELECT: // Intro o Espacio con el icono seleccionado
                Open();
                return false;
            default:
                return false;
        }
    }

    /// <summary>Al cerrar la ventana principal. Devuelve true si se cancela el cierre porque pasa a la bandeja.</summary>
    public bool OnClosing()
    {
        // Al cerrar la sesión de Windows, WPF sale aunque se cancele el cierre.
        if (IsExiting || !host.IsTrayIconShown || host.Settings.TrayMode != TrayMode.CloseToTray)
        {
            return false;
        }

        host.HideMainWindow();
        ApplyLockRules();
        return true;
    }

    /// <summary>Al minimizar: la ventana pasa a la bandeja si así se ha elegido, y se aplican las reglas de bloqueo.</summary>
    public void OnMinimized()
    {
        if (host.IsTrayIconShown && host.Settings.TrayMode == TrayMode.MinimizeToTray)
        {
            host.HideMainWindow();
        }

        ApplyLockRules();
    }

    private void ApplyLockRules()
    {
        if (host.Settings.LockOnMinimize)
        {
            host.LockNow(LockReason.Minimized);
        }
    }
}

/// <summary>
/// Icono de PassDesk en el área de notificación (Shell_NotifyIcon, versión 4) con el menú nativo de Windows. Lo que hace
/// cada opción está en <see cref="TrayController"/>.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const int CallbackMessage = NativeMethods.WM_APP + 1;
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly TrayController _tray;
    private readonly HwndSource _window;
    private readonly nint _icon;

    public TrayIcon(TrayController tray)
    {
        _tray = tray;

        // Ventana oculta propia: recibe los avisos del icono y es la dueña del menú, así abrirlo no activa la ventana principal.
        _window = new HwndSource(new HwndSourceParameters("PassDesk.Tray") { WindowStyle = 0, ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW });
        _window.AddHook(WndProc);
        if (NativeMethods.LoadIconMetric(NativeMethods.GetModuleHandle(null), NativeMethods.ApplicationIconId, NativeMethods.LIM_SMALL, out _icon) != 0)
        {
            AppLog.Warn(LogEvent.TrayIconFailed);
        }

        Add();
    }

    /// <summary>El icono está en la bandeja. Puede faltar si el Explorador aún no ha arrancado; se añade cuando lo haga.</summary>
    public bool IsShown { get; private set; }

    public void Dispose()
    {
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, Data());
        IsShown = false;
        NativeMethods.DestroyIcon(_icon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private NativeMethods.NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = 1,
    };

    private void Add()
    {
        var data = Data();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        TrayController.ToolTip.AsSpan().CopyTo(new Span<char>(data.szTip, 127));

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, data); // por si sigue ahí de antes
        IsShown = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, data) && NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, data);
        if (!IsShown)
        {
            AppLog.Warn(LogEvent.TrayIconFailed);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;

            // Versión 4: LOWORD(lParam) es el evento y wParam, la posición en pantalla (x, y).
            if (_tray.OnIconMessage((ushort)lParam))
            {
                ShowMenu((short)wParam, (short)(wParam >> 16));
            }
        }
        else if (TaskbarCreated != 0 && msg == (int)TaskbarCreated)
        {
            // El Explorador se ha reiniciado (o acaba de arrancar al iniciar sesión): el icono hay que volver a añadirlo.
            Add();
        }

        return 0;
    }

    private void ShowMenu(int x, int y)
    {
        var items = _tray.MenuItems;
        nint menu = NativeMethods.CreatePopupMenu();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].SeparatorBefore)
                {
                    NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
                }

                uint state = items[i].Enabled ? NativeMethods.MF_STRING : NativeMethods.MF_STRING | NativeMethods.MF_GRAYED;
                NativeMethods.AppendMenu(menu, state, (nuint)(i + 1), items[i].Text);
                if (items[i].IsDefault)
                {
                    NativeMethods.SetMenuDefaultItem(menu, (uint)(i + 1), 0); // en negrita: es lo que hace el doble clic
                }
            }

            // Sin primer plano, el menú no se cierra al hacer clic fuera de él.
            NativeMethods.SetForegroundWindow(_window.Handle);
            uint flags = NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTBUTTON
                | (NativeMethods.GetSystemMetrics(NativeMethods.SM_MENUDROPALIGNMENT) != 0 ? NativeMethods.TPM_RIGHTALIGN : 0);
            int command = NativeMethods.TrackPopupMenuEx(menu, flags, x, y, _window.Handle, 0);
            NativeMethods.PostMessage(_window.Handle, NativeMethods.WM_NULL, 0, 0);
            if (command > 0)
            {
                items[command - 1].Invoke();
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }
}
