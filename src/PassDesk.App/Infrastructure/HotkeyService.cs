using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using PassDesk.Core.Diagnostics;

namespace PassDesk.App.Infrastructure;

/// <summary>Combinación de teclas como "Ctrl+Shift+Space".</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                case "mayús":
                case "mayus":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key is not null || !Enum.TryParse(raw, ignoreCase: true, out Key parsed) || parsed == Key.None)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        // Exige al menos un modificador para no secuestrar teclas normales.
        if (key is null || modifiers == ModifierKeys.None)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }

    public string ToDisplayString() => ToString()
        .Replace("Shift", "Mayús", StringComparison.Ordinal)
        .Replace("Space", "Espacio", StringComparison.Ordinal)
        .Replace("+", " + ", StringComparison.Ordinal);
}

/// <summary>Atajo de teclado global (RegisterHotKey) para abrir la búsqueda rápida desde cualquier aplicación.</summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5044;
    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyService(nint windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle) ?? throw new InvalidOperationException("Ventana no válida.");
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public HotkeyGesture? Current { get; private set; }

    /// <summary>Registra el atajo. Devuelve false si otra aplicación ya lo usa o no es válido.</summary>
    public bool Register(HotkeyGesture gesture)
    {
        Unregister();
        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= NativeMethods.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        _registered = vk != 0 && NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk);
        if (_registered)
        {
            Current = gesture;
            AppLog.Info(LogEvent.HotkeyRegistered);
        }
        else
        {
            Current = null;
            AppLog.Warn(LogEvent.HotkeyRegistrationFailed);
        }

        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
            Current = null;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }
}

internal static class Formatting
{
    public static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public static string Date(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("d MMM yyyy, HH:mm", Spanish) : "—";

    public static string Count(int count, string singular, string plural) =>
        count.ToString("N0", Spanish) + " " + (count == 1 ? singular : plural);
}
