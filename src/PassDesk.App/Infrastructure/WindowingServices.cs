using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Storage;

namespace PassDesk.App.Infrastructure;

/// <summary>Tema claro/oscuro: tema Fluent de WPF (Windows 11) + paleta propia de la aplicación.</summary>
internal sealed class ThemeService
{
    private bool _listening;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        IsDark = preference == ThemePreference.Dark || (preference == ThemePreference.System && SystemUsesDarkTheme());

        var app = Application.Current;
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PassDesk;component/Themes/Palette.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Source?.OriginalString.Contains("Themes/Palette.", StringComparison.Ordinal) == true)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            dictionaries[index] = palette;
        }
        else
        {
            dictionaries.Add(palette);
        }

        ApplySystemAccent(app.Resources, IsDark);

        foreach (Window window in app.Windows)
        {
            WindowSecurity.ApplyTitleBar(window, IsDark);
        }

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                // Cambios de tema o de color de énfasis en Configuración › Personalización.
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                {
                    app.Dispatcher.BeginInvoke(() => Apply(Preference));
                }
            };
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Usa el color de énfasis de Windows (el mismo que aplican los controles Fluent) con las
    /// variantes que emplean las apps nativas: Dark1 en tema claro y Light2 en tema oscuro.
    /// </summary>
    private static void ApplySystemAccent(ResourceDictionary resources, bool dark)
    {
        Color accent = dark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1;
        Color text = dark ? SystemColors.AccentColorLight3 : SystemColors.AccentColorDark2;
        double luminance = ((0.299 * accent.R) + (0.587 * accent.G) + (0.114 * accent.B)) / 255;

        resources["Brush.Accent"] = Frozen(accent);
        resources["Brush.AccentHover"] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
        resources["Brush.AccentPressed"] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        resources["Brush.OnAccent"] = Frozen(luminance > 0.55 ? Colors.Black : Colors.White);
        resources["Brush.AccentText"] = Frozen(text);
        resources["Brush.AccentSubtle"] = Frozen(Color.FromArgb(dark ? (byte)0x38 : (byte)0x26, accent.R, accent.G, accent.B));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}

/// <summary>Medidas de protección por ventana: exclusión de capturas y barra de título acorde al tema.</summary>
internal static class WindowSecurity
{
    public static void Protect(Window window, AppServices services, bool roundCorners = false)
    {
        void Apply()
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
            {
                return;
            }

            ApplyCaptureProtection(hwnd, services.Settings.ScreenCaptureProtection);
            ApplyTitleBar(window, services.Theme.IsDark);
            if (roundCorners)
            {
                ApplyRoundCorners(hwnd);
            }
        }

        void OnSettingsChanged(object? sender, EventArgs e) => Apply();

        if (new WindowInteropHelper(window).Handle != 0)
        {
            Apply();
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply();
        }

        services.SettingsChanged += OnSettingsChanged;
        window.Closed += (_, _) => services.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>
    /// Excluye la ventana de capturas de pantalla, grabaciones y pantalla compartida
    /// (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+). La ventana aparece vacía en la captura.
    /// </summary>
    public static void ApplyCaptureProtection(nint hwnd, bool enabled)
    {
        if (NativeMethods.SetWindowDisplayAffinity(hwnd, enabled ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE))
        {
            return;
        }

        if (enabled && NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_MONITOR))
        {
            return;
        }

        AppLog.Warn(LogEvent.CaptureProtectionFailed);
    }

    public static unsafe void ApplyTitleBar(Window window, bool dark)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        int value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &value, sizeof(int));
    }

    public static unsafe void ApplyRoundCorners(nint hwnd)
    {
        int value = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &value, sizeof(int));
    }
}

/// <summary>Garantiza una sola instancia por perfil de datos; una segunda ejecución activa la existente.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    public event EventHandler? ActivationRequested;

    public static SingleInstance? TryAcquire(string dataRoot, out string activationEventName)
    {
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant())))[..16];
        activationEventName = $@"Local\PassDesk.Activate.{suffix}";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\PassDesk.Instance.{suffix}", out bool created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }

        var instance = new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName));
        instance._wait = ThreadPool.RegisterWaitForSingleObject(
            instance._activate,
            (_, _) => instance.ActivationRequested?.Invoke(instance, EventArgs.Empty),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return instance;
    }

    public static void SignalExisting(string activationEventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(activationEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
    }
}

/// <summary>Nombres e iconos (Segoe Fluent Icons) de las categorías.</summary>
internal static class CategoryInfo
{
    public const string AllGlyph = "";

    public static string Name(Category category) => category switch
    {
        Category.Social => "Redes sociales",
        Category.Email => "Correo electrónico",
        Category.Gaming => "Videojuegos",
        Category.Banking => "Bancos y finanzas",
        Category.Shopping => "Compras",
        _ => "Otras",
    };

    public static string Glyph(Category category) => category switch
    {
        Category.Social => "",
        Category.Email => "",
        Category.Gaming => "",
        Category.Banking => "",
        Category.Shopping => "",
        _ => "",
    };
}

/// <summary>
/// Iconos de servicio sin conexión: inicial del servicio sobre su color de marca (si se conoce) o un
/// color estable derivado del dominio. No se descargan favicons para no revelar a terceros qué
/// cuentas tiene el usuario.
/// </summary>
internal static class Monogram
{
    private static readonly Dictionary<string, Color> BrandColors = new(StringComparer.Ordinal)
    {
        ["google"] = Rgb(0x4285F4), ["gmail"] = Rgb(0xEA4335), ["youtube"] = Rgb(0xFF0000),
        ["facebook"] = Rgb(0x1877F2), ["instagram"] = Rgb(0xE1306C), ["whatsapp"] = Rgb(0x25D366),
        ["twitter"] = Rgb(0x1D9BF0), ["x"] = Rgb(0x14171A), ["tiktok"] = Rgb(0x111111),
        ["linkedin"] = Rgb(0x0A66C2), ["reddit"] = Rgb(0xFF4500), ["discord"] = Rgb(0x5865F2),
        ["telegram"] = Rgb(0x26A5E4), ["pinterest"] = Rgb(0xE60023), ["snapchat"] = Rgb(0xF7D100),
        ["microsoft"] = Rgb(0x0078D4), ["outlook"] = Rgb(0x0078D4), ["live"] = Rgb(0x0078D4),
        ["hotmail"] = Rgb(0x0078D4), ["office"] = Rgb(0xD83B01), ["xbox"] = Rgb(0x107C10),
        ["apple"] = Rgb(0x555555), ["icloud"] = Rgb(0x3693F3), ["yahoo"] = Rgb(0x6001D2),
        ["proton"] = Rgb(0x6D4AFF), ["amazon"] = Rgb(0xFF9900), ["ebay"] = Rgb(0xE53238),
        ["aliexpress"] = Rgb(0xE62E04), ["netflix"] = Rgb(0xE50914), ["spotify"] = Rgb(0x1DB954),
        ["twitch"] = Rgb(0x9146FF), ["steampowered"] = Rgb(0x1B2838), ["steam"] = Rgb(0x1B2838),
        ["playstation"] = Rgb(0x003791), ["nintendo"] = Rgb(0xE60012), ["epicgames"] = Rgb(0x313131),
        ["paypal"] = Rgb(0x003087), ["bbva"] = Rgb(0x004481), ["santander"] = Rgb(0xEC0000),
        ["caixabank"] = Rgb(0x007EAE), ["ing"] = Rgb(0xFF6200), ["revolut"] = Rgb(0x191C1F),
        ["github"] = Rgb(0x24292F), ["dropbox"] = Rgb(0x0061FF), ["adobe"] = Rgb(0xFA0F00),
        ["zara"] = Rgb(0x111111), ["shein"] = Rgb(0x222222), ["airbnb"] = Rgb(0xFF5A5F),
        ["uber"] = Rgb(0x111111), ["booking"] = Rgb(0x003580), ["wallapop"] = Rgb(0x13C1AC),
    };

    private static readonly Color[] Palette =
    [
        Rgb(0x0F6CBD), Rgb(0x8764B8), Rgb(0x038387), Rgb(0xCA5010), Rgb(0x498205), Rgb(0xC239B3),
        Rgb(0xE3008C), Rgb(0x00838F), Rgb(0x986F0B), Rgb(0x4F6BED), Rgb(0xC50F1F), Rgb(0x107C10),
    ];

    private static readonly Dictionary<Color, SolidColorBrush> Cache = [];

    public static (Brush Background, Brush Foreground, string Letter) For(string title, string host)
    {
        string label = host.Length > 0 ? Core.Text.DomainUtil.GetServiceLabel(host) : string.Empty;
        Color color = BrandColors.TryGetValue(label, out var brand)
            ? brand
            : Palette[(int)(StableHash(label.Length > 0 ? label : title.ToLowerInvariant()) % (uint)Palette.Length)];

        double luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255;
        var foreground = luminance > 0.62 ? Brushes.Black : Brushes.White;
        return (GetBrush(color), foreground, Letter(title, label));
    }

    private static string Letter(string title, string label)
    {
        foreach (char c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpper(c, Formatting.Spanish).ToString();
            }
        }

        return label.Length > 0 ? char.ToUpperInvariant(label[0]).ToString() : "?";
    }

    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    private static SolidColorBrush GetBrush(Color color)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                Cache[color] = brush;
            }

            return brush;
        }
    }

    private static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
