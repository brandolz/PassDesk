using System.IO.Pipes;
using System.Text.RegularExpressions;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Host;

public sealed record NativeHostOptions
{
    /// <summary>Orígenes de extensión autorizados (<c>chrome-extension://&lt;id&gt;/</c>). Si está vacía, no se atiende a nadie.</summary>
    public IReadOnlyCollection<string> AllowedCallers { get; init; } = [];

    public string PipeName { get; init; } = string.Empty;

    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Espera máxima a la respuesta de PassDesk (incluye la confirmación del usuario).</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Abre la conexión con PassDesk. Por defecto, la tubería local del usuario actual; las pruebas la sustituyen.</summary>
    public Func<string, TimeSpan, CancellationToken, Task<Stream>>? Connector { get; init; }
}

/// <summary>
/// Lógica del conector de Native Messaging. Es un relé estrecho: comprueba que lo lanzó la extensión autorizada, valida
/// el mensaje del navegador, lo reenvía a PassDesk por la tubería local y devuelve la respuesta. No descifra nada, no
/// guarda nada y termina tras responder una vez. Si PassDesk no está abierto, responde <c>unavailable</c>.
/// </summary>
public static partial class NativeHostRunner
{
    public const int ExitOk = 0;
    public const int ExitUnauthorizedCaller = 2;
    public const int ExitBadInput = 3;

    /// <summary>Respuesta máxima que se reenvía al navegador (el límite de Chrome es 1 MiB; una lista de 20 coincidencias ocupa unos 6 KiB).</summary>
    public const int MaxResponseBytes = 64 * 1024;

    [GeneratedRegex("^chrome-extension://[a-p]{32}/$", RegexOptions.CultureInvariant)]
    private static partial Regex CallerPattern();

    /// <summary>Valida y normaliza una lista de orígenes autorizados; los que no tienen la forma exacta se descartan.</summary>
    public static IReadOnlyList<string> ParseAllowedCallers(IEnumerable<string> lines) =>
        [.. lines.Select(l => l.Trim()).Where(l => CallerPattern().IsMatch(l)).Distinct(StringComparer.Ordinal)];

    public static async Task<int> RunAsync(string[] args, Stream input, Stream output, NativeHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        // El navegador pasa como primer argumento el origen de la extensión que lo lanzó. Comparación exacta.
        if (args.Length == 0 || !options.AllowedCallers.Contains(args[0], StringComparer.Ordinal))
        {
            return ExitUnauthorizedCaller;
        }

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(options.ReadTimeout);
        PinnedBuffer? request;
        FrameStatus status;
        try
        {
            (status, request) = await NativeMessageFraming.ReadAsync(input, AutofillRequestParser.MaxRequestBytes, readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExitBadInput;
        }

        using (request)
        {
            if (status == FrameStatus.InvalidLength)
            {
                await WriteErrorAsync(output, ErrorCodes.TooLarge, cancellationToken).ConfigureAwait(false);
                return ExitBadInput;
            }

            if (status != FrameStatus.Ok || request is null)
            {
                return ExitBadInput;
            }

            if (!AutofillRequestParser.TryParse(request.Span, out _, out var error))
            {
                await WriteErrorAsync(output, error == ProtocolError.UnsupportedOrigin ? ErrorCodes.UnsupportedOrigin : ErrorCodes.BadRequest, cancellationToken).ConfigureAwait(false);
                return ExitBadInput;
            }

            Stream pipe;
            try
            {
                var connector = options.Connector ?? ConnectToPipeAsync;
                pipe = await connector(options.PipeName, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await WriteErrorAsync(output, ErrorCodes.Unavailable, cancellationToken).ConfigureAwait(false);
                return ExitOk;
            }

            await using (pipe.ConfigureAwait(false))
            {
                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseTimeout.CancelAfter(options.ResponseTimeout);
                PinnedBuffer? response;
                FrameStatus responseStatus;
                try
                {
                    await NativeMessageFraming.WriteAsync(pipe, request.Memory, responseTimeout.Token).ConfigureAwait(false);
                    (responseStatus, response) = await NativeMessageFraming.ReadAsync(pipe, MaxResponseBytes, responseTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    await WriteErrorAsync(output, ErrorCodes.Unavailable, cancellationToken).ConfigureAwait(false);
                    return ExitOk;
                }

                using (response)
                {
                    if (responseStatus != FrameStatus.Ok || response is null || response.Span[0] != (byte)'{')
                    {
                        await WriteErrorAsync(output, ErrorCodes.Internal, cancellationToken).ConfigureAwait(false);
                        return ExitOk;
                    }

                    await NativeMessageFraming.WriteAsync(output, response.Memory, cancellationToken).ConfigureAwait(false);
                    return ExitOk;
                }
            }
        }
    }

    private static async Task<Stream> ConnectToPipeAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteErrorAsync(Stream output, string code, CancellationToken cancellationToken)
    {
        using var result = new Broker.ErrorResult(code);
        using var payload = result.Serialize();
        await NativeMessageFraming.WriteAsync(output, payload.Memory, cancellationToken).ConfigureAwait(false);
    }
}
