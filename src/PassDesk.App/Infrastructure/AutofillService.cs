using System.Diagnostics;
using System.IO.Pipes;
using System.Windows.Threading;
using PassDesk.App.Views;
using PassDesk.Auth;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Ipc;
using PassDesk.Core.Diagnostics;
using PassDesk.Storage;

namespace PassDesk.App.Infrastructure;

/// <summary>
/// Integración con navegadores: arranca y para el servidor de la tubería local según el ajuste y conecta el intermediario con
/// la sesión de la bóveda, el bloqueo y la confirmación del usuario. Con el ajuste desactivado (por defecto) no hay tubería
/// ni nada escuchando.
/// </summary>
internal sealed class AutofillService : IDisposable
{
    public const string HostFileName = "PassDesk.NativeHost.exe";

    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private SessionAutofillVault? _vault;
    private AutofillBroker? _broker;
    private AutofillPipeServer? _server;

    public AutofillService(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
    }

    public bool IsRunning => _server is not null;

    /// <summary>El navegador pide que PassDesk muestre su pantalla de desbloqueo (en el hilo de la interfaz).</summary>
    public event EventHandler? UnlockRequested;

    /// <summary>Ruta del conector que debe haber junto a PassDesk.exe; solo ese programa puede usar la tubería.</summary>
    public static string ExpectedHostPath() =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, HostFileName);

    public void Apply(AppSettings settings)
    {
        if (settings.BrowserAutofillEnabled)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    private AutofillPolicy CurrentPolicy()
    {
        var settings = _services.Settings;
        return new AutofillPolicy
        {
            Enabled = settings.BrowserAutofillEnabled,
            AllowSubdomains = settings.BrowserAutofillSubdomains,
            RequireConfirmation = settings.BrowserAutofillConfirm,
        };
    }

    private void Start()
    {
        if (_server is not null)
        {
            return;
        }

        try
        {
            var audit = new AppLogAutofillAudit();
            _vault = new SessionAutofillVault(_services.Session);
            _broker = new AutofillBroker(
                _vault,
                CurrentPolicy,
                TimeProvider.System,
                new WindowsFillConfirmer(_services, _dispatcher),
                new DispatcherUnlockRequester(this, _dispatcher),
                audit);
            _server = new AutofillPipeServer(
                PipeNames.ForDataRoot(_services.Paths.Root),
                _broker,
                new HostProcessVerifier(ExpectedHostPath()),
                audit);
            _server.Start();
            AppLog.Info(LogEvent.BrowserAutofillStarted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error(LogEvent.BrowserAutofillStartFailed, ex);
            Stop();
        }
    }

    private void Stop()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            // Sin esperar en el hilo de la interfaz: el cierre del servidor no depende de ella, pero así nunca la bloquea.
            Task.Run(async () => await server.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(3));
            AppLog.Info(LogEvent.BrowserAutofillStopped);
        }

        _broker?.Dispose();
        _broker = null;
        _vault?.Dispose();
        _vault = null;
    }

    private void RaiseUnlockRequested() => UnlockRequested?.Invoke(this, EventArgs.Empty);

    private sealed class DispatcherUnlockRequester(AutofillService owner, Dispatcher dispatcher) : IUnlockRequester
    {
        public void RequestUnlock() => dispatcher.BeginInvoke(owner.RaiseUnlockRequested);
    }
}

/// <summary>Auditoría del autorrelleno en el registro de PassDesk: solo tipo de evento y contador, nunca sitios ni cuentas.</summary>
internal sealed class AppLogAutofillAudit : IAutofillAudit
{
    public void Record(AutofillAuditEvent auditEvent, int count = 0)
    {
        var logEvent = auditEvent switch
        {
            AutofillAuditEvent.Listed => LogEvent.BrowserAutofillListed,
            AutofillAuditEvent.Filled => LogEvent.BrowserAutofillFilled,
            AutofillAuditEvent.Denied => LogEvent.BrowserAutofillDenied,
            AutofillAuditEvent.RateLimited => LogEvent.BrowserAutofillRateLimited,
            AutofillAuditEvent.UnlockRequested => LogEvent.BrowserAutofillUnlockRequested,
            AutofillAuditEvent.BadRequest => LogEvent.BrowserAutofillBadRequest,
            AutofillAuditEvent.ClientRejected => LogEvent.BrowserAutofillClientRejected,
            _ => LogEvent.BrowserAutofillFailed,
        };
        if (auditEvent == AutofillAuditEvent.Listed)
        {
            AppLog.Info(logEvent, new LogMetric("matches", count));
        }
        else if (auditEvent is AutofillAuditEvent.Filled or AutofillAuditEvent.UnlockRequested)
        {
            AppLog.Info(logEvent);
        }
        else
        {
            AppLog.Warn(logEvent);
        }
    }
}

/// <summary>
/// Solo el conector instalado junto a PassDesk puede hablar con la tubería. Es una defensa adicional a la del propio canal
/// (que ya es solo del usuario actual): si no se puede identificar al cliente, se rechaza.
/// </summary>
internal sealed class HostProcessVerifier(string expectedPath) : IPipeClientVerifier
{
    public bool Verify(NamedPipeServerStream pipe)
    {
        try
        {
            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
            {
                return false;
            }

            using var process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            return IsExpectedHost(path, expectedPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsExpectedHost(string? candidatePath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Muestra la confirmación de PassDesk (ventana nativa, en el hilo de la interfaz) y devuelve la decisión del usuario.</summary>
internal sealed class WindowsFillConfirmer(AppServices services, Dispatcher dispatcher) : IFillConfirmer
{
    public Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(() =>
        {
            AutofillConfirmWindow window;
            try
            {
                window = new AutofillConfirmWindow(services, confirmation);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Error(LogEvent.BrowserAutofillFailed, ex);
                completion.TrySetResult(false);
                return;
            }

            CancellationTokenRegistration registration = default;
            window.Closed += (_, _) =>
            {
                registration.Dispose();
                completion.TrySetResult(window.Allowed);
            };

            // Tiempo agotado o solicitud cancelada: la ventana se cierra sin autorizar.
            registration = cancellationToken.Register(() => dispatcher.BeginInvoke(window.Close));
            window.Show();
            window.Activate();
        });
        return completion.Task;
    }
}
