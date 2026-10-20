using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Threading;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Services;
using PassDesk.Crypto;
using Windows.Security.Credentials.UI;

namespace PassDesk.App.Infrastructure;

/// <summary>
/// Portapapeles de Windows mediante la API Win32. Además del texto, publica los formatos que
/// Windows reconoce para excluir el contenido del historial del portapapeles (Win+V) y de la
/// sincronización en la nube entre dispositivos.
/// </summary>
internal sealed class WindowsClipboardService(Func<nint> ownerWindow) : IClipboardService
{
    private static readonly uint ExcludeFromMonitoring = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = NativeMethods.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public void SetSensitiveText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Open();
        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            SetUnicodeText(text);
            SetDword(ExcludeFromMonitoring, 0);
            SetDword(CanIncludeInHistory, 0);
            SetDword(CanUploadToCloud, 0);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public uint GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public void Clear()
    {
        Open();
        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private void Open()
    {
        // Otra aplicación puede tener el portapapeles abierto unos milisegundos.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(ownerWindow()))
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new ExternalException("El portapapeles está en uso por otra aplicación.");
    }

    private static unsafe void SetUnicodeText(string text)
    {
        nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
        nint handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, bytes);
        if (handle == 0)
        {
            throw new OutOfMemoryException();
        }

        nint target = NativeMethods.GlobalLock(handle);
        if (target == 0)
        {
            NativeMethods.GlobalFree(handle);
            throw new OutOfMemoryException();
        }

        try
        {
            fixed (char* source = text)
            {
                Buffer.MemoryCopy(source, (void*)target, (long)bytes, text.Length * sizeof(char));
            }

            ((char*)target)[text.Length] = '\0';
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, handle) == 0)
        {
            NativeMethods.GlobalFree(handle);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe void SetDword(uint format, uint value)
    {
        if (format == 0)
        {
            return;
        }

        nint handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, sizeof(uint));
        if (handle == 0)
        {
            return;
        }

        nint target = NativeMethods.GlobalLock(handle);
        if (target != 0)
        {
            *(uint*)target = value;
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(format, handle) == 0)
        {
            NativeMethods.GlobalFree(handle);
        }
    }
}

/// <summary>Planificador basado en DispatcherTimer: las acciones se ejecutan en el hilo de la interfaz.</summary>
internal sealed class DispatcherDelayScheduler(Dispatcher dispatcher) : IDelayScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        var handle = new TimerHandle(timer);
        timer.Tick += (_, _) =>
        {
            handle.Dispose();
            callback();
        };
        timer.Start();
        return handle;
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>Windows Hello (rostro, huella o PIN) mediante la API oficial UserConsentVerifier.</summary>
internal sealed class WindowsHelloVerifier(Func<nint> ownerWindow) : IUserPresenceVerifier
{
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<PresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(ownerWindow(), message)
                .AsTask(cancellationToken);
            return result switch
            {
                UserConsentVerificationResult.Verified => PresenceResult.Verified,
                UserConsentVerificationResult.Canceled => PresenceResult.Canceled,
                UserConsentVerificationResult.DeviceNotPresent
                    or UserConsentVerificationResult.NotConfiguredForUser
                    or UserConsentVerificationResult.DisabledByPolicy => PresenceResult.Unavailable,
                _ => PresenceResult.Failed,
            };
        }
        catch (OperationCanceledException)
        {
            return PresenceResult.Canceled;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Error(LogEvent.WindowsHelloResult, ex);
            return PresenceResult.Unavailable;
        }
    }
}

/// <summary>Convierte el contenido de un PasswordBox en caracteres fijados en memoria sin crear cadenas.</summary>
internal static class SecureStringHelper
{
    public static SensitiveChars ToSensitiveChars(SecureString secure)
    {
        ArgumentNullException.ThrowIfNull(secure);
        var result = new SensitiveChars(secure.Length);
        nint native = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            unsafe
            {
                new ReadOnlySpan<char>((void*)native, secure.Length).CopyTo(result.Span);
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(native);
        }

        return result;
    }
}
