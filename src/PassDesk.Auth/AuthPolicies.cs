using PassDesk.Core.Services;

namespace PassDesk.Auth;

public enum PasswordStrength
{
    TooShort,
    Weak,
    Fair,
    Strong,
    VeryStrong,
}

/// <summary>
/// Política de la contraseña maestra: mínimo 12 caracteres y fortaleza estimada «Fuerte» o superior.
/// La exigencia es alta porque las copias de seguridad no tienen la capa de Windows (DPAPI): quien consiga una
/// puede intentar adivinar la contraseña sin límite de intentos. La estimación es orientativa (longitud ×
/// variedad de caracteres), no una garantía: dos palabras comunes en minúsculas ya no se aceptan.
/// </summary>
public static class MasterPasswordPolicy
{
    public const int MinLength = 12;

    public static PasswordStrength Evaluate(ReadOnlySpan<char> password)
    {
        if (password.Length < MinLength)
        {
            return PasswordStrength.TooShort;
        }

        bool lower = false, upper = false, digit = false, symbol = false, other = false;
        int distinct = 0;
        Span<bool> seen = stackalloc bool[128];
        foreach (char c in password)
        {
            if (c < 128)
            {
                if (!seen[c])
                {
                    seen[c] = true;
                    distinct++;
                }
            }
            else
            {
                distinct++;
            }

            if (char.IsAsciiLetterLower(c)) lower = true;
            else if (char.IsAsciiLetterUpper(c)) upper = true;
            else if (char.IsAsciiDigit(c)) digit = true;
            else if (c < 128) symbol = true;
            else other = true;
        }

        int pool = (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (symbol ? 32 : 0) + (other ? 64 : 0);
        double bits = password.Length * Math.Log2(Math.Max(pool, 2));

        // Penaliza contraseñas con muy pocos caracteres distintos ("aaaaaaaaaaaa", "abababab...").
        if (distinct <= 3)
        {
            bits = Math.Min(bits, 20);
        }
        else if (distinct <= 5)
        {
            bits *= 0.6;
        }

        return bits switch
        {
            < 45 => PasswordStrength.Weak,
            < 60 => PasswordStrength.Fair,
            < 80 => PasswordStrength.Strong,
            _ => PasswordStrength.VeryStrong,
        };
    }

    public static bool IsAcceptable(ReadOnlySpan<char> password) => Evaluate(password) >= PasswordStrength.Strong;
}

/// <summary>
/// Retardos crecientes tras intentos fallidos de desbloqueo. Se aplican también tras reiniciar
/// la aplicación porque el estado se guarda en disco.
/// </summary>
public static class UnlockThrottle
{
    /// <summary>Intentos fallidos a partir de los cuales se aplica la espera máxima.</summary>
    public const int MaxDelayAttempts = 8;

    public static TimeSpan MaxDelay { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan DelayAfter(int failedAttempts) => failedAttempts switch
    {
        <= 3 => TimeSpan.Zero,
        4 => TimeSpan.FromSeconds(5),
        5 => TimeSpan.FromSeconds(15),
        6 => TimeSpan.FromSeconds(30),
        7 => TimeSpan.FromMinutes(1),
        _ => MaxDelay,
    };
}

/// <summary>Registra la última actividad del usuario para el bloqueo automático por inactividad.</summary>
public sealed class InactivityMonitor
{
    private readonly IClock _clock;
    private long _lastActivityTicks;

    public InactivityMonitor(IClock clock, TimeSpan timeout)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Timeout = timeout;
        RecordActivity();
    }

    public TimeSpan Timeout { get; set; }

    public DateTimeOffset LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public void RecordActivity() => Interlocked.Exchange(ref _lastActivityTicks, _clock.UtcNow.UtcTicks);

    public TimeSpan Idle => _clock.UtcNow - LastActivityUtc;

    public bool IsExpired => Timeout > TimeSpan.Zero && Idle >= Timeout;
}

/// <summary>Bloquea la sesión cuando vence el tiempo de inactividad. La UI llama a <see cref="Tick"/> periódicamente.</summary>
public sealed class AutoLockController
{
    private readonly VaultSession _session;
    private readonly InactivityMonitor _monitor;

    public AutoLockController(VaultSession session, InactivityMonitor monitor)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _session.StateChanged += (_, e) =>
        {
            if (e.NewState == VaultState.Unlocked)
            {
                _monitor.RecordActivity();
            }
        };
    }

    public bool Tick()
    {
        _session.ExpireQuickUnlockIfDue();
        if (_session.State == VaultState.Unlocked && _monitor.IsExpired)
        {
            _session.Lock(LockReason.Inactivity);
            return true;
        }

        return false;
    }
}

public enum PresenceResult
{
    Verified,
    Unavailable,
    Canceled,
    Failed,
}

/// <summary>Verificación de presencia del usuario (Windows Hello: rostro, huella o PIN).</summary>
public interface IUserPresenceVerifier
{
    Task<bool> IsAvailableAsync();

    Task<PresenceResult> VerifyAsync(string message, CancellationToken cancellationToken);
}

/// <summary>Verificador usado cuando Windows Hello no está disponible o está desactivado.</summary>
public sealed class UnavailablePresenceVerifier : IUserPresenceVerifier
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public Task<PresenceResult> VerifyAsync(string message, CancellationToken cancellationToken) =>
        Task.FromResult(PresenceResult.Unavailable);
}
