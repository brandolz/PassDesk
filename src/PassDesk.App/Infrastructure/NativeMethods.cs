using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PassDesk.App.Infrastructure;

/// <summary>Llamadas a la API de Windows usadas por la interfaz.</summary>
internal static unsafe partial class NativeMethods
{
    // Ventanas
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    // Atajos globales
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Portapapeles
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    // Tuberías con nombre: identificar el proceso que se conecta al servidor del autorrelleno del navegador.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, int* pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint hMem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    // Carpetas conocidas y aviso en la barra de tareas
    public static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public const uint FLASHW_TRAY = 0x00000002;
    public const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlashWindowEx(in FLASHWINFO pwfi);

    /// <summary>Ruta real de una carpeta conocida (respeta si el usuario la movió de sitio).</summary>
    public static string? GetKnownFolder(Guid folderId)
    {
        if (SHGetKnownFolderPath(folderId, 0, 0, out nint path) != 0 || path == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>Hace parpadear el botón de la barra de tareas hasta que la ventana pase a primer plano.</summary>
    public static void FlashTaskbar(nint hwnd)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(info);
    }

    // Icono en la bandeja del sistema y su menú
    public const int WM_NULL = 0x0000;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_APP = 0x8000;
    public const int NIN_KEYSELECT = 0x0401;
    public const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 0x0002, TPM_RIGHTALIGN = 0x0008, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    public const int SM_MENUDROPALIGNMENT = 40;
    public const int LIM_SMALL = 0;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Identificador con el que el compilador de C# incrusta el icono de la aplicación (ApplicationIcon).</summary>
    public const int ApplicationIconId = 32512;

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, in NOTIFYICONDATAW lpData);

    [LibraryImport("comctl32.dll")]
    public static partial int LoadIconMetric(nint hinst, nint pszName, int lims, out nint phico);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);

    /// <summary>Con TPM_RETURNCMD devuelve el identificador de la opción elegida (0 si se cierra sin elegir).</summary>
    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    // Cierre del proceso
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    /// <summary>
    /// El motor PDF de Windows (Windows.Data.Pdf.dll) guarda un dispositivo Direct3D global que solo libera al
    /// descargarse, ya dentro del cierre del proceso. Con algunos controladores gráficos (comprobado con Intel)
    /// esa liberación espera a hilos que Windows ya ha terminado y el proceso no acaba nunca. Si el visor PDF se
    /// ha usado, se finaliza el proceso directamente. Llamar solo con todo guardado y la bóveda bloqueada.
    /// </summary>
    public static void FinishProcessIfPdfEngineLoaded(int exitCode)
    {
        if (GetModuleHandle("Windows.Data.Pdf.dll") != 0)
        {
            TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
        }
    }
}
