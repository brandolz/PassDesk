using System.Text;
using System.Text.Json;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Ipc;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

// Banco de pruebas del autorrelleno: bóveda ficticia en memoria + el intermediario y la tubería reales.
//
//   dotnet run --project tools/PassDesk.AutofillSandbox -- [--pipe NOMBRE] [--vault credenciales.json]
//
// Órdenes por la entrada estándar (una por línea):
//   lock | unlock          bloquea o desbloquea la bóveda ficticia
//   confirm yes|no|ask     respuesta a la confirmación (ask: espera a la orden «answer yes|no»)
//   answer yes|no          responde a la confirmación pendiente (modo ask)
//   subdomains on|off      coincidencias por subdominio
//   quit                   termina
// Escribe por la salida estándar eventos sin datos (READY, STATE, AUDIT, CONFIRM) para que una prueba automática los siga.

string pipeName = PipeNames.ForCurrentUser();
string? vaultFile = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pipe")
    {
        pipeName = args[i + 1];
    }
    else if (args[i] == "--vault")
    {
        vaultFile = args[i + 1];
    }
}

var vault = new SandboxVault(vaultFile);
var policy = new AutofillPolicy { RequireConfirmation = true };
var confirmer = new ConsoleConfirmer();
var audit = new ConsoleAudit();
using var broker = new AutofillBroker(vault, () => policy, TimeProvider.System, confirmer, new ConsoleUnlock(vault), audit);
await using var server = new AutofillPipeServer(pipeName, broker, verifier: null, audit);
server.Start();
Console.WriteLine($"READY {pipeName}");
Console.Out.Flush();

while (await Console.In.ReadLineAsync() is { } line)
{
    string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
        continue;
    }

    switch (parts[0])
    {
        case "lock":
            vault.Lock();
            Console.WriteLine("STATE locked");
            break;
        case "unlock":
            vault.Unlock();
            Console.WriteLine("STATE unlocked");
            break;
        case "confirm" when parts.Length == 2:
            confirmer.Mode = parts[1];
            Console.WriteLine($"CONFIRM-MODE {parts[1]}");
            break;
        case "answer" when parts.Length == 2:
            confirmer.Answer(parts[1] == "yes");
            Console.WriteLine($"ANSWER {parts[1]}");
            break;
        case "subdomains" when parts.Length == 2:
            policy = policy with { AllowSubdomains = parts[1] == "on" };
            Console.WriteLine($"SUBDOMAINS {parts[1]}");
            break;
        case "quit":
            return 0;
    }

    Console.Out.Flush();
}

return 0;

internal sealed record SandboxEntry(string Title, string Url, string Username, string Password);

internal sealed class SandboxVault : IAutofillVault
{
    private readonly List<(AutofillEntryInfo Info, string Password)> _entries = [];
    private readonly Lock _gate = new();
    private AutofillVaultState _state = AutofillVaultState.Unlocked;

    public SandboxVault(string? file)
    {
        var entries = file is null
            ? DefaultEntries()
            : JsonSerializer.Deserialize<List<SandboxEntry>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var e in entries)
        {
            _entries.Add((new AutofillEntryInfo(Guid.NewGuid(), e.Title, e.Url, e.Username), e.Password));
        }
    }

    public AutofillVaultState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler? Locked;

    public void Lock()
    {
        lock (_gate)
        {
            _state = AutofillVaultState.Locked;
        }

        Locked?.Invoke(this, EventArgs.Empty);
    }

    public void Unlock()
    {
        lock (_gate)
        {
            _state = AutofillVaultState.Unlocked;
        }
    }

    public IReadOnlyList<AutofillEntryInfo> Snapshot()
    {
        lock (_gate)
        {
            return _state == AutofillVaultState.Unlocked ? [.. _entries.Select(e => e.Info)] : [];
        }
    }

    public AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize)
    {
        lock (_gate)
        {
            if (_state != AutofillVaultState.Unlocked)
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

    // Credenciales FICTICIAS para probar a mano con tools/PassDesk.AutofillSandbox/pagina-de-prueba.
    private static List<SandboxEntry> DefaultEntries() =>
    [
        new("Ejemplo Uno", "https://uno.example.test", "ana@uno.example.test", "Ficticia-Uno#2026"),
        new("Multi (personal)", "https://multi.example.test", "beto@multi.example.test", "Ficticia-Beto#2026"),
        new("Multi (trabajo)", "https://multi.example.test", "carla@multi.example.test", "Ficticia-Carla#2026"),
        new("Con puerto", "https://uno.example.test:8443", "dani@uno.example.test", "Ficticia-Puerto#2026"),
    ];
}

internal sealed class ConsoleAudit : IAutofillAudit
{
    public void Record(AutofillAuditEvent auditEvent, int count = 0)
    {
        Console.WriteLine($"AUDIT {auditEvent} {count}");
        Console.Out.Flush();
    }
}

internal sealed class ConsoleUnlock(SandboxVault vault) : IUnlockRequester
{
    public void RequestUnlock()
    {
        Console.WriteLine($"UNLOCK-REQUESTED state={vault.State}");
        Console.Out.Flush();
    }
}

internal sealed class ConsoleConfirmer : IFillConfirmer
{
    private TaskCompletionSource<bool>? _pending;

    public string Mode { get; set; } = "yes";

    public void Answer(bool answer) => _pending?.TrySetResult(answer);

    public async Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken)
    {
        // Solo datos ficticios de la bóveda de pruebas: sirven para comprobar qué vería el usuario en PassDesk.
        Console.WriteLine($"CONFIRM-SHOWN title={confirmation.Title} user={confirmation.Username} site={confirmation.StoredSite} page={confirmation.PageOrigin} kind={confirmation.Kind} scope={confirmation.Scope}");
        Console.Out.Flush();
        switch (Mode)
        {
            case "no":
                return false;
            case "ask":
                Console.WriteLine("Escribe «answer yes» o «answer no».");
                Console.Out.Flush();
                var pending = _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
                {
                    return await pending.Task.ConfigureAwait(false);
                }
            default:
                return true;
        }
    }
}
