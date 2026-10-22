using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Broker;

/// <summary>
/// Decide qué se entrega al navegador. Es la única puerta de acceso a las credenciales para el autorrelleno:
/// <list type="bullet">
/// <item>Solo se lista lo que coincide con el origen de la página (HTTPS, host y puerto), nunca la bóveda entera.</item>
/// <item>El relleno exige la ficha de selección que se entregó al listar: una credencial, un origen, un solo uso.</item>
/// <item>El origen se vuelve a comprobar contra la URL guardada en la bóveda, en el momento de entregar.</item>
/// <item>Con la bóveda bloqueada, o si se bloquea mientras se espera la confirmación, no se entrega nada.</item>
/// <item>Solicitudes limitadas por tiempo y de una en una para el relleno.</item>
/// </list>
/// </summary>
public sealed class AutofillBroker : IDisposable
{
    public const int MaxMatches = 20;

    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly IAutofillVault _vault;
    private readonly Func<AutofillPolicy> _policy;
    private readonly TimeProvider _time;
    private readonly IFillConfirmer? _confirmer;
    private readonly IUnlockRequester? _unlock;
    private readonly IAutofillAudit? _audit;
    private readonly SelectionTokens _tokens;
    private readonly SemaphoreSlim _fillGate = new(1, 1);
    private readonly SlidingWindowLimiter _all;
    private readonly SlidingWindowLimiter _status;
    private readonly SlidingWindowLimiter _list;
    private readonly SlidingWindowLimiter _fill;
    private readonly SlidingWindowLimiter _unlockLimiter;
    private readonly SlidingWindowLimiter _invalidTokens;
    private long _epoch;
    private long _fillBlockedUntil;

    public AutofillBroker(
        IAutofillVault vault,
        Func<AutofillPolicy> policy,
        TimeProvider? time = null,
        IFillConfirmer? confirmer = null,
        IUnlockRequester? unlock = null,
        IAutofillAudit? audit = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _time = time ?? TimeProvider.System;
        _confirmer = confirmer;
        _unlock = unlock;
        _audit = audit;
        _tokens = new SelectionTokens(_time, TokenLifetime, capacity: 256);
        _all = new SlidingWindowLimiter(120, Minute, _time);
        _status = new SlidingWindowLimiter(60, Minute, _time);
        _list = new SlidingWindowLimiter(30, Minute, _time);
        _fill = new SlidingWindowLimiter(10, Minute, _time);
        _unlockLimiter = new SlidingWindowLimiter(3, Minute, _time);
        _invalidTokens = new SlidingWindowLimiter(5, Minute, _time);
        _vault.Locked += OnVaultLocked;
    }

    /// <summary>Fichas vivas (para pruebas y diagnóstico).</summary>
    internal int LiveTokens => _tokens.Count;

    public async Task<AutofillResult> HandleAsync(AutofillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (!_all.TryAcquire() || !LimiterFor(request.Type).TryAcquire())
            {
                _audit?.Record(AutofillAuditEvent.RateLimited);
                return Error(request, ErrorCodes.RateLimited);
            }

            var policy = _policy();
            if (!policy.Enabled)
            {
                return request.Type == RequestType.Status ? new StatusResult("disabled") { RequestId = request.RequestId } : Error(request, ErrorCodes.Disabled);
            }

            return request.Type switch
            {
                RequestType.Status => Status(request),
                RequestType.Unlock => Unlock(request),
                RequestType.List => List(request, policy),
                RequestType.Fill => await FillAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Error(request, ErrorCodes.BadRequest),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Solo se registra que falló; el mensaje de la excepción podría contener datos de la solicitud.
            _audit?.Record(AutofillAuditEvent.Failed);
            return Error(request, ErrorCodes.Internal);
        }
    }

    public void Dispose()
    {
        _vault.Locked -= OnVaultLocked;
        _tokens.Clear();
        _fillGate.Dispose();
    }

    private SlidingWindowLimiter LimiterFor(RequestType type) => type switch
    {
        RequestType.Status => _status,
        RequestType.List => _list,
        RequestType.Fill => _fill,
        _ => _unlockLimiter,
    };

    private void OnVaultLocked(object? sender, EventArgs e)
    {
        // Primero el contador, después las fichas: una solicitud en curso ve el cambio de época y se rechaza.
        Interlocked.Increment(ref _epoch);
        _tokens.Clear();
    }

    private AutofillResult Status(AutofillRequest request) =>
        new StatusResult(StateName(_vault.State)) { RequestId = request.RequestId };

    private AutofillResult Unlock(AutofillRequest request)
    {
        var state = _vault.State;
        if (state != AutofillVaultState.Locked)
        {
            return new StatusResult(StateName(state)) { RequestId = request.RequestId };
        }

        // Pedir el desbloqueo solo muestra la pantalla de PassDesk; el usuario se autentica con la contraseña maestra o
        // Windows Hello como siempre. Nada de lo que llegue por aquí puede desbloquear la bóveda.
        _unlock?.RequestUnlock();
        _audit?.Record(AutofillAuditEvent.UnlockRequested);
        return new StatusResult("locked", unlockRequested: _unlock is not null) { RequestId = request.RequestId };
    }

    private AutofillResult List(AutofillRequest request, AutofillPolicy policy)
    {
        var origin = request.Origin!;
        if (!origin.IsSecure)
        {
            _audit?.Record(AutofillAuditEvent.Denied);
            return Error(request, ErrorCodes.InsecureOrigin);
        }

        var state = _vault.State;
        if (state != AutofillVaultState.Unlocked)
        {
            return Error(request, state == AutofillVaultState.NotCreated ? ErrorCodes.NotCreated : ErrorCodes.Locked);
        }

        // La época se lee antes que la instantánea: si se bloquea entre medias, las fichas nacen ya caducadas.
        long epoch = Interlocked.Read(ref _epoch);
        var options = new OriginMatchOptions { AllowSubdomains = policy.AllowSubdomains };
        var found = new List<(AutofillEntryInfo Entry, OriginMatchKind Kind)>();
        foreach (var entry in _vault.Snapshot())
        {
            var kind = OriginMatcher.Match(origin, entry.Url, options);
            if (kind != OriginMatchKind.None)
            {
                found.Add((entry, kind));
            }
        }

        var matches = found
            .OrderBy(f => f.Kind)
            .ThenBy(f => f.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(f => f.Entry.Username, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxMatches)
            .Select(f => new AutofillMatch(
                f.Entry.Id,
                f.Entry.Title,
                f.Entry.Username,
                OriginMatcher.DisplaySite(f.Entry.Url),
                f.Kind,
                _tokens.Mint(f.Entry.Id, origin, f.Kind, epoch)))
            .ToList();
        _audit?.Record(AutofillAuditEvent.Listed, matches.Count);
        return new ListResult(origin, matches) { RequestId = request.RequestId };
    }

    private async Task<AutofillResult> FillAsync(AutofillRequest request, CancellationToken cancellationToken)
    {
        var origin = request.Origin!;
        if (!origin.IsSecure)
        {
            _audit?.Record(AutofillAuditEvent.Denied);
            return Error(request, ErrorCodes.InsecureOrigin);
        }

        if (_time.GetTimestamp() < Interlocked.Read(ref _fillBlockedUntil))
        {
            _audit?.Record(AutofillAuditEvent.RateLimited);
            return Error(request, ErrorCodes.RateLimited);
        }

        // Bloqueada: se avisa antes de mirar la ficha (al bloquear desaparecen todas) para que la extensión pida el desbloqueo.
        if (_vault.State != AutofillVaultState.Unlocked)
        {
            return Error(request, ErrorCodes.Locked);
        }

        long epoch = Interlocked.Read(ref _epoch);
        var grant = _tokens.Consume(request.Token!);
        if (grant is null)
        {
            return InvalidAttempt(request, ErrorCodes.Expired);
        }

        if (grant.CredentialId != request.CredentialId || grant.Origin != origin.Serialize() || grant.Epoch != epoch)
        {
            return InvalidAttempt(request, ErrorCodes.Denied);
        }

        if (!_fillGate.Wait(0, CancellationToken.None))
        {
            return Error(request, ErrorCodes.Busy);
        }

        try
        {
            var policy = _policy();
            var options = new OriginMatchOptions { AllowSubdomains = policy.AllowSubdomains };
            var info = _vault.Snapshot().FirstOrDefault(e => e.Id == request.CredentialId);
            if (info is null)
            {
                return Error(request, ErrorCodes.NotFound);
            }

            var kind = OriginMatcher.Match(origin, info.Url, options);
            if (kind == OriginMatchKind.None)
            {
                _audit?.Record(AutofillAuditEvent.Denied);
                return Error(request, ErrorCodes.Denied);
            }

            if (policy.RequireConfirmation)
            {
                // Sin forma de preguntar al usuario no se entrega nada.
                if (_confirmer is null || !await ConfirmAsync(info, origin, kind, request.Scope!.Value, cancellationToken).ConfigureAwait(false))
                {
                    _audit?.Record(AutofillAuditEvent.Denied);
                    return Error(request, ErrorCodes.Denied);
                }

                // El usuario pudo tardar: si mientras tanto se bloqueó la bóveda (aunque se haya vuelto a desbloquear), no vale.
                if (Interlocked.Read(ref _epoch) != epoch || _vault.State != AutofillVaultState.Unlocked)
                {
                    return Error(request, ErrorCodes.Locked);
                }

                policy = _policy();
                if (!policy.Enabled)
                {
                    return Error(request, ErrorCodes.Disabled);
                }

                options = new OriginMatchOptions { AllowSubdomains = policy.AllowSubdomains };
            }

            var secret = _vault.Read(
                request.CredentialId!.Value,
                request.Scope!.Value,
                entry => OriginMatcher.Match(origin, entry.Url, options) != OriginMatchKind.None);
            if (secret is null)
            {
                return Error(request, _vault.State == AutofillVaultState.Unlocked ? ErrorCodes.NotFound : ErrorCodes.Locked);
            }

            _audit?.Record(AutofillAuditEvent.Filled);
            return new SecretResult(secret) { RequestId = request.RequestId };
        }
        finally
        {
            _fillGate.Release();
        }
    }

    private async Task<bool> ConfirmAsync(AutofillEntryInfo info, WebOrigin origin, OriginMatchKind kind, FillScope scope, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConfirmationTimeout);
        try
        {
            var confirmation = new FillConfirmation(info.Title, info.Username, OriginMatcher.DisplaySite(info.Url), origin, kind, scope);
            // WaitAsync: si el confirmador no atiende la cancelación (o su ventana no llega a abrirse), la espera termina igual.
            return await _confirmer!.ConfirmAsync(confirmation, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Tiempo de espera agotado: equivale a cancelar.
            return false;
        }
    }

    /// <summary>Una ficha desconocida, caducada o usada para otra cosa: tras varias en un minuto se bloquean los rellenos.</summary>
    private AutofillResult InvalidAttempt(AutofillRequest request, string code)
    {
        _audit?.Record(AutofillAuditEvent.Denied);
        if (!_invalidTokens.TryAcquire())
        {
            Interlocked.Exchange(ref _fillBlockedUntil, _time.GetTimestamp() + (long)(Minute.TotalSeconds * _time.TimestampFrequency));
            _audit?.Record(AutofillAuditEvent.RateLimited);
            return Error(request, ErrorCodes.RateLimited);
        }

        return Error(request, code);
    }

    private static ErrorResult Error(AutofillRequest request, string code) => new(code) { RequestId = request.RequestId };

    private static string StateName(AutofillVaultState state) => state switch
    {
        AutofillVaultState.NotCreated => "not_created",
        AutofillVaultState.Locked => "locked",
        _ => "ready",
    };
}
