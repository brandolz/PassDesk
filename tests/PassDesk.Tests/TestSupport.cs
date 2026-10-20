using System.Text;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Core.Services;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>
/// Datos de prueba. TODO es ficticio: ninguna credencial real se usa en las pruebas.
/// </summary>
internal static class Fake
{
    public const string MasterPassword = "Prueba-Maestra#2026!ficticia";
    public const string OtherMasterPassword = "Otra-Clave_Ficticia$9876";
    public const string Secret = "Ficticia#Clave-2026!";
    public const string Secret2 = "Zz9_FakePass_ñandú€";

    /// <summary>Iteraciones reducidas para que las pruebas sean rápidas (el valor real es 600.000).</summary>
    public const int FastIterations = 10_000;

    public static CredentialEntry Entry(string title, string url, string user, string password, string notes = "", Category? category = null)
    {
        var now = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var entry = new CredentialEntry
        {
            Title = title,
            Url = url,
            Username = user,
            Category = category ?? CategoryClassifier.Classify(title, url),
            CreatedUtc = now,
            ModifiedUtc = now,
            Source = EntrySources.Manual,
        };
        entry.SetPassword(ProtectedSecret.FromString(password));
        entry.SetNotes(ProtectedSecret.FromString(notes));
        return entry;
    }

    public static VaultSession NewSession(out InMemoryVaultStore store, out FakeClock clock, InMemorySecurityStateStore? security = null)
    {
        store = new InMemoryVaultStore();
        clock = new FakeClock();
        return new VaultSession(store, security ?? new InMemorySecurityStateStore(), clock, FastIterations);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static bool ContainsBytes(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>Comprueba que un secreto no aparece ni en UTF-8 ni en UTF-16 dentro de unos bytes.</summary>
    public static bool ContainsText(byte[] haystack, string text) =>
        ContainsBytes(haystack, Encoding.UTF8.GetBytes(text)) || ContainsBytes(haystack, Encoding.Unicode.GetBytes(text));
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Planificador manual: las acciones se ejecutan cuando la prueba lo indica.</summary>
internal sealed class ManualScheduler : IDelayScheduler
{
    private readonly List<Scheduled> _items = [];

    public IReadOnlyList<Scheduled> Items => _items;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var item = new Scheduled(delay, callback);
        _items.Add(item);
        return item;
    }

    public void FireAll()
    {
        foreach (var item in _items.ToList())
        {
            item.Fire();
        }
    }

    internal sealed class Scheduled(TimeSpan delay, Action callback) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;

        public bool Canceled { get; private set; }

        public void Fire()
        {
            if (!Canceled)
            {
                Canceled = true;
                callback();
            }
        }

        public void Dispose() => Canceled = true;
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; private set; }

    public uint Sequence { get; private set; } = 100;

    public bool LastSetWasSensitive { get; private set; }

    public void SetSensitiveText(string text)
    {
        Text = text;
        LastSetWasSensitive = true;
        Sequence++;
    }

    /// <summary>Simula que el usuario copia otra cosa desde otra aplicación.</summary>
    public void UserCopies(string text)
    {
        Text = text;
        LastSetWasSensitive = false;
        Sequence++;
    }

    public uint GetSequenceNumber() => Sequence;

    public void Clear()
    {
        Text = null;
        Sequence++;
    }
}

/// <summary>Portapapeles que simula estar ocupado por otra aplicación al borrar.</summary>
internal sealed class BusyClipboard : IClipboardService
{
    private uint _sequence = 1;

    public string? Text { get; private set; }

    /// <summary>Número de borrados que fallarán antes de funcionar.</summary>
    public int FailingClears { get; set; }

    public int ClearAttempts { get; private set; }

    public void SetSensitiveText(string text)
    {
        Text = text;
        _sequence++;
    }

    public uint GetSequenceNumber() => _sequence;

    public void Clear()
    {
        ClearAttempts++;
        if (FailingClears > 0)
        {
            FailingClears--;
            throw new System.Runtime.InteropServices.ExternalException("Portapapeles ocupado (simulado).");
        }

        Text = null;
        _sequence++;
    }
}

/// <summary>Almacén de la bóveda en memoria que puede fallar al escribir (disco lleno, antivirus…).</summary>
internal sealed class FlakyVaultStore : IVaultStore
{
    public byte[]? Envelope { get; private set; }

    public List<byte[]> RecoveryPoints { get; } = [];

    public int Writes { get; private set; }

    /// <summary>Número de escrituras siguientes que fallarán.</summary>
    public int FailNextWrites { get; set; }

    /// <summary>Escrituras que todavía funcionan antes de que empiecen los fallos de <see cref="FailNextWrites"/>.</summary>
    public int WritesBeforeFailures { get; set; }

    public bool Exists => Envelope is not null;

    public byte[] ReadEnvelope() => (byte[])(Envelope ?? throw new FileNotFoundException()).Clone();

    public void WriteEnvelope(byte[] envelope)
    {
        if (FailNextWrites > 0 && WritesBeforeFailures == 0)
        {
            FailNextWrites--;
            throw new IOException("Fallo de escritura simulado.");
        }

        WritesBeforeFailures = Math.Max(0, WritesBeforeFailures - 1);
        Writes++;
        Envelope = (byte[])envelope.Clone();
    }

    public void CreateRecoveryPoint(string reason)
    {
        if (Envelope is not null)
        {
            RecoveryPoints.Add((byte[])Envelope.Clone());
        }
    }
}

/// <summary>Ejecuta código de interfaz WPF en un hilo STA propio (las pruebas de xUnit usan MTA).</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}

internal sealed class FakePresenceVerifier(PresenceResult result) : IUserPresenceVerifier
{
    public int Calls { get; private set; }

    public Task<bool> IsAvailableAsync() => Task.FromResult(result != PresenceResult.Unavailable);

    public Task<PresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

/// <summary>Directorio temporal que se elimina al terminar la prueba.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PassDeskTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
