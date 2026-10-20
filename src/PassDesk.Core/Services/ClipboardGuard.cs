using System.Runtime.InteropServices;
using PassDesk.Core.Diagnostics;

namespace PassDesk.Core.Services;

public enum ClipboardItemKind
{
    Username,
    Password,
    VerificationCode,
    GeneratedPassword,
    Note,
    DocumentText,
}

/// <summary>
/// Copia credenciales al portapapeles y las borra automáticamente tras un intervalo configurable.
/// <para>
/// Para no borrar contenido ajeno, recuerda el número de secuencia del portapapeles tras copiar:
/// si al vencer el plazo el número ha cambiado (el usuario copió otra cosa), no toca el portapapeles.
/// </para>
/// <para>
/// Si otra aplicación tiene el portapapeles abierto cuando toca borrarlo, PassDesk conserva la propiedad y
/// lo reintenta (y de nuevo al bloquear y al salir): nunca da por borrado algo que sigue copiado.
/// </para>
/// </summary>
public sealed class ClipboardGuard : IDisposable
{
    /// <summary>Espera entre reintentos cuando el portapapeles está ocupado.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Reintentos programados como máximo; después solo se reintenta al bloquear o salir.</summary>
    public const int MaxScheduledRetries = 30;

    private readonly IClipboardService _clipboard;
    private readonly IDelayScheduler _scheduler;
    private readonly Func<TimeSpan> _clearAfter;
    private uint? _ownedSequence;
    private IDisposable? _pending;
    private int _retries;

    public ClipboardGuard(IClipboardService clipboard, IDelayScheduler scheduler, Func<TimeSpan> clearAfter)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _clearAfter = clearAfter ?? throw new ArgumentNullException(nameof(clearAfter));
    }

    /// <summary>Se produce cuando el portapapeles se ha borrado automáticamente.</summary>
    public event EventHandler? Cleared;

    public bool OwnsClipboard => _ownedSequence.HasValue;

    /// <summary>Copia el texto y programa su borrado. Devuelve el intervalo de borrado.</summary>
    public TimeSpan Copy(string text, ClipboardItemKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        CancelPending();
        _retries = 0;

        _clipboard.SetSensitiveText(text);
        _ownedSequence = _clipboard.GetSequenceNumber();

        var delay = _clearAfter();
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }

        _pending = _scheduler.Schedule(delay, OnTimerElapsed);
        AppLog.Info(LogEvent.ClipboardCopied, new LogMetric("kind", (long)kind), new LogMetric("clearSeconds", (long)delay.TotalSeconds));
        return delay;
    }

    /// <summary>
    /// Borra el portapapeles solo si sigue conteniendo lo que copió PassDesk. Se invoca al vencer el plazo,
    /// al bloquear y al salir. Si el portapapeles está ocupado, devuelve false, conserva la propiedad y
    /// programa un reintento.
    /// </summary>
    public bool ClearIfOwned()
    {
        CancelPending();
        if (_ownedSequence is not { } owned)
        {
            return false;
        }

        if (_clipboard.GetSequenceNumber() != owned)
        {
            _ownedSequence = null;
            _retries = 0;
            AppLog.Info(LogEvent.ClipboardClearSkipped);
            return false;
        }

        try
        {
            _clipboard.Clear();
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            // Otra aplicación tiene el portapapeles abierto: se conserva la propiedad para volver a intentarlo.
            AppLog.Warn(LogEvent.ClipboardFailed, new LogMetric("retry", _retries));
            ScheduleRetry();
            return false;
        }

        _ownedSequence = null;
        _retries = 0;
        AppLog.Info(LogEvent.ClipboardCleared);
        return true;
    }

    /// <summary>
    /// Para el cierre de la aplicación, cuando ya no habrá reintentos programados: lo intenta varias veces
    /// seguidas con una pausa corta entre intentos.
    /// </summary>
    public bool ClearIfOwnedNow(int attempts, TimeSpan pause)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (ClearIfOwned())
            {
                CancelPending();
                return true;
            }

            if (!OwnsClipboard)
            {
                break;
            }

            if (pause > TimeSpan.Zero)
            {
                Thread.Sleep(pause);
            }
        }

        CancelPending();
        return false;
    }

    public void Dispose() => CancelPending();

    private void OnTimerElapsed()
    {
        if (ClearIfOwned())
        {
            Cleared?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ScheduleRetry()
    {
        if (_retries >= MaxScheduledRetries)
        {
            return;
        }

        _retries++;
        _pending = _scheduler.Schedule(RetryDelay, OnTimerElapsed);
    }

    private void CancelPending()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
