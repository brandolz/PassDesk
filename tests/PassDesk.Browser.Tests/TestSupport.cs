using System.Text;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Tests;

/// <summary>Datos de prueba. TODO es ficticio: no se usa ninguna credencial ni bóveda reales.</summary>
internal static class Fake
{
    public const string Password = "Ficticia#Clave-Autorrelleno-2026";
    public const string Password2 = "Zz9_OtraFicticia_ñandú€";
    public const string User = "ana.ficticia@ejemplo.test";
    public const string User2 = "beto.ficticio@ejemplo.test";

    public static WebOrigin Origin(string text)
    {
        Assert.True(WebOrigin.TryParse(text, out var origin), text);
        return origin;
    }
}

/// <summary>Reloj manual para probar caducidades y límites sin esperar.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000;
    private DateTimeOffset _now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by)
    {
        _ticks += (long)by.TotalMilliseconds;
        _now += by;
    }
}

/// <summary>Bóveda ficticia en memoria que cumple el contrato de <see cref="IAutofillVault"/>.</summary>
internal sealed class FakeVault : IAutofillVault
{
    private readonly object _gate = new();
    private readonly List<(AutofillEntryInfo Info, string Password)> _entries = [];

    public AutofillVaultState State { get; private set; } = AutofillVaultState.Unlocked;

    public event EventHandler? Locked;

    public int ReadCalls { get; private set; }

    public Guid Add(string title, string url, string user, string password)
    {
        var id = Guid.NewGuid();
        _entries.Add((new AutofillEntryInfo(id, title, url, user), password));
        return id;
    }

    public void Lock()
    {
        lock (_gate)
        {
            State = AutofillVaultState.Locked;
        }

        Locked?.Invoke(this, EventArgs.Empty);
    }

    public void Unlock()
    {
        lock (_gate)
        {
            State = AutofillVaultState.Unlocked;
        }
    }

    /// <summary>Bloquea sin avisar con el evento: simula la ventana entre cambiar el estado y notificarlo.</summary>
    public void LockSilently()
    {
        lock (_gate)
        {
            State = AutofillVaultState.Locked;
        }
    }

    public void SetNotCreated() => State = AutofillVaultState.NotCreated;

    public IReadOnlyList<AutofillEntryInfo> Snapshot()
    {
        lock (_gate)
        {
            return State == AutofillVaultState.Unlocked ? [.. _entries.Select(e => e.Info)] : [];
        }
    }

    public AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize)
    {
        lock (_gate)
        {
            ReadCalls++;
            if (State != AutofillVaultState.Unlocked)
            {
                return null;
            }

            var entry = _entries.FirstOrDefault(e => e.Info.Id == id);
            if (entry.Info is null || !authorize(entry.Info))
            {
                return null;
            }

            return new AutofillSecret(
                Encoding.UTF8.GetBytes(entry.Info.Username),
                Encoding.UTF8.GetBytes(entry.Password),
                includeUsername: scope is FillScope.Both or FillScope.Username,
                includePassword: scope is FillScope.Both or FillScope.Password);
        }
    }
}

internal sealed class RecordingAudit : IAutofillAudit
{
    private readonly List<(AutofillAuditEvent Event, int Count)> _events = [];

    public IReadOnlyList<(AutofillAuditEvent Event, int Count)> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Record(AutofillAuditEvent auditEvent, int count = 0)
    {
        lock (_events)
        {
            _events.Add((auditEvent, count));
        }
    }

    public bool Has(AutofillAuditEvent auditEvent) => Events.Any(e => e.Event == auditEvent);

    public string AsText() => string.Join('\n', Events.Select(e => $"{e.Event}:{e.Count}"));
}

internal sealed class RecordingUnlock : IUnlockRequester
{
    public int Requests { get; private set; }

    public void RequestUnlock() => Requests++;
}

/// <summary>Confirmación controlada por la prueba: responde de inmediato o queda pendiente hasta que la prueba decida.</summary>
internal sealed class ScriptedConfirmer : IFillConfirmer
{
    private TaskCompletionSource<bool>? _pending;

    public bool Answer { get; set; } = true;

    public bool HoldAnswer { get; set; }

    public List<FillConfirmation> Shown { get; } = [];

    public TaskCompletionSource Shown_ { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken)
    {
        Shown.Add(confirmation);
        Shown_.TrySetResult();
        if (!HoldAnswer)
        {
            return Task.FromResult(Answer);
        }

        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
        return _pending.Task;
    }

    public void Complete(bool answer) => _pending!.TrySetResult(answer);
}

internal static class Requests
{
    public static AutofillRequest List(string origin) =>
        new(RequestType.List, null, Fake.Origin(origin), null, null, null);

    public static AutofillRequest Fill(string origin, AutofillMatch match, FillScope scope = FillScope.Both) =>
        new(RequestType.Fill, null, Fake.Origin(origin), match.Id, match.Token, scope);

    public static AutofillRequest Of(RequestType type) => new(type, null, null, null, null, null);
}
