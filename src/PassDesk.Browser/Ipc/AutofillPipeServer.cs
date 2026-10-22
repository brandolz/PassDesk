using System.IO.Pipes;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Ipc;

/// <summary>Comprueba quién se ha conectado a la tubería (en Windows, que sea el conector de PassDesk y no otro programa).</summary>
public interface IPipeClientVerifier
{
    bool Verify(NamedPipeServerStream pipe);
}

public sealed record PipeServerLimits
{
    public int MaxConcurrentConnections { get; init; } = 4;

    public TimeSpan RequestReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tiempo máximo para atender una solicitud (incluye la confirmación del usuario en PassDesk).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Tiempo máximo para entregar la respuesta a un cliente que no la lee.</summary>
    public TimeSpan ResponseWriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Servidor de la tubería local con nombre. No es un puerto de red: solo el usuario actual puede abrirla
/// (<see cref="PipeOptions.CurrentUserOnly"/>) y no admite clientes remotos. Cada conexión atiende una solicitud:
/// lee un mensaje (con límite de tamaño y de tiempo), lo valida, lo pasa al intermediario y responde.
/// </summary>
public sealed class AutofillPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly AutofillBroker _broker;
    private readonly IPipeClientVerifier? _verifier;
    private readonly IAutofillAudit? _audit;
    private readonly PipeServerLimits _limits;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private Task? _acceptLoop;

    public AutofillPipeServer(string pipeName, AutofillBroker broker, IPipeClientVerifier? verifier = null, IAutofillAudit? audit = null, PipeServerLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _verifier = verifier;
        _audit = audit;
        _limits = limits ?? new PipeServerLimits();
        _slots = new SemaphoreSlim(_limits.MaxConcurrentConnections, _limits.MaxConcurrentConnections);
    }

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            return;
        }

        // Se crea la primera instancia aquí para que la tubería exista (y un nombre ocupado falle) al volver de Start.
        var first = CreateServer();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(first, _stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancelación normal, o un fallo del bucle que ya se registró: al cerrar no hay nada más que hacer.
            }
        }

        _stop.Dispose();
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: _limits.MaxConcurrentConnections + 2,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                server = await NextServerAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var connected = server;
            server = await NextServerAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleConnectionAsync(connected, cancellationToken), CancellationToken.None);
        }

        await server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<NamedPipeServerStream> NextServerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return CreateServer();
            }
            catch (IOException)
            {
                // Demasiadas instancias abiertas: se espera a que se libere alguna.
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            if (!await _slots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                using var busy = new ErrorResult(ErrorCodes.Busy);
                await TryWriteAsync(pipe, busy, serverToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await ServeAsync(pipe, serverToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // El cliente se fue o se cerró el servidor: no hay nada que responder.
            }
            catch (Exception)
            {
                _audit?.Record(AutofillAuditEvent.Failed);
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        if (_verifier is not null && !_verifier.Verify(pipe))
        {
            _audit?.Record(AutofillAuditEvent.ClientRejected);
            using var rejected = new ErrorResult(ErrorCodes.UnauthorizedCaller);
            await TryWriteAsync(pipe, rejected, serverToken).ConfigureAwait(false);
            return;
        }

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        readTimeout.CancelAfter(_limits.RequestReadTimeout);
        PinnedBuffer? frame;
        FrameStatus status;
        try
        {
            (status, frame) = await NativeMessageFraming.ReadAsync(pipe, AutofillRequestParser.MaxRequestBytes, readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
        {
            // Un cliente lento o parado no retiene la conexión.
            _audit?.Record(AutofillAuditEvent.BadRequest);
            return;
        }

        using (frame)
        {
            if (status != FrameStatus.Ok || frame is null)
            {
                if (status == FrameStatus.InvalidLength)
                {
                    _audit?.Record(AutofillAuditEvent.BadRequest);
                    using var tooLarge = new ErrorResult(ErrorCodes.TooLarge);
                    await TryWriteAsync(pipe, tooLarge, serverToken).ConfigureAwait(false);
                }

                return;
            }

            if (!AutofillRequestParser.TryParse(frame.Span, out var request, out var error))
            {
                _audit?.Record(AutofillAuditEvent.BadRequest);
                using var bad = new ErrorResult(error == ProtocolError.UnsupportedOrigin ? ErrorCodes.UnsupportedOrigin : ErrorCodes.BadRequest);
                await TryWriteAsync(pipe, bad, serverToken).ConfigureAwait(false);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            timeout.CancelAfter(_limits.RequestTimeout);
            AutofillResult result;
            try
            {
                result = await _broker.HandleAsync(request!, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
            {
                result = new ErrorResult(ErrorCodes.Denied) { RequestId = request!.RequestId };
            }

            using (result)
            {
                await TryWriteAsync(pipe, result, serverToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TryWriteAsync(NamedPipeServerStream pipe, AutofillResult result, CancellationToken cancellationToken)
    {
        using var payload = result.Serialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_limits.ResponseWriteTimeout);
        try
        {
            await NativeMessageFraming.WriteAsync(pipe, payload.Memory, timeout.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // El cliente cerró antes de leer: el secreto se descarta al liberar el búfer.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Un cliente que no lee no retiene la conexión.
        }
    }
}
