using System.Text;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Tests;

public class BrokerTests
{
    private readonly FakeVault _vault = new();
    private readonly ManualTime _time = new();
    private readonly RecordingAudit _audit = new();
    private readonly RecordingUnlock _unlock = new();
    private readonly ScriptedConfirmer _confirmer = new();
    private AutofillPolicy _policy = new();

    public BrokerTests()
    {
        _vault.Add("Ejemplo", "https://ejemplo.test", Fake.User, Fake.Password);
    }

    private AutofillBroker NewBroker(bool confirmer = true) =>
        new(_vault, () => _policy, _time, confirmer ? _confirmer : null, _unlock, _audit);

    private static async Task<(AutofillResult Result, string Json)> Run(AutofillBroker broker, AutofillRequest request)
    {
        var result = await broker.HandleAsync(request, CancellationToken.None);
        using var buffer = result.Serialize();
        return (result, Encoding.UTF8.GetString(buffer.Span));
    }

    private static async Task<AutofillMatch> FirstMatch(AutofillBroker broker, string origin)
    {
        var (result, _) = await Run(broker, Requests.List(origin));
        using (result)
        {
            return Assert.IsType<ListResult>(result).Matches[0];
        }
    }

    private static string ErrorOf(AutofillResult result)
    {
        using (result)
        {
            return Assert.IsType<ErrorResult>(result).Code;
        }
    }

    // ---- Estado de la bóveda ----

    [Fact]
    public async Task Status_reports_the_state_and_nothing_else()
    {
        using var broker = NewBroker();
        var (result, json) = await Run(broker, Requests.Of(RequestType.Status));
        Assert.Equal("ready", Assert.IsType<StatusResult>(result).State);
        Assert.DoesNotContain(Fake.User, json);
        _vault.Lock();
        Assert.Equal("locked", Assert.IsType<StatusResult>((await Run(broker, Requests.Of(RequestType.Status))).Result).State);
        _vault.SetNotCreated();
        Assert.Equal("not_created", Assert.IsType<StatusResult>((await Run(broker, Requests.Of(RequestType.Status))).Result).State);
        _policy = new AutofillPolicy { Enabled = false };
        Assert.Equal("disabled", Assert.IsType<StatusResult>((await Run(broker, Requests.Of(RequestType.Status))).Result).State);
    }

    [Fact]
    public async Task Locked_vault_lists_and_fills_nothing()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        _vault.Lock();

        var (listResult, listJson) = await Run(broker, Requests.List("https://ejemplo.test"));
        Assert.Equal(ErrorCodes.Locked, ErrorOf(listResult));
        Assert.DoesNotContain(Fake.User, listJson);

        var (fillResult, fillJson) = await Run(broker, Requests.Fill("https://ejemplo.test", match));
        Assert.Equal(ErrorCodes.Locked, ErrorOf(fillResult));
        Assert.DoesNotContain(Fake.Password, fillJson);
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task Vault_that_does_not_exist_reports_it()
    {
        using var broker = NewBroker();
        _vault.SetNotCreated();
        Assert.Equal(ErrorCodes.NotCreated, ErrorOf((await Run(broker, Requests.List("https://ejemplo.test"))).Result));
    }

    [Fact]
    public async Task Disabled_integration_answers_nothing_useful()
    {
        _policy = new AutofillPolicy { Enabled = false };
        using var broker = NewBroker();
        Assert.Equal(ErrorCodes.Disabled, ErrorOf((await Run(broker, Requests.List("https://ejemplo.test"))).Result));
        Assert.Equal(ErrorCodes.Disabled, ErrorOf((await Run(broker, Requests.Of(RequestType.Unlock))).Result));
    }

    [Fact]
    public async Task Unlock_request_only_shows_the_unlock_screen()
    {
        using var broker = NewBroker();
        _vault.Lock();
        var (result, json) = await Run(broker, Requests.Of(RequestType.Unlock));
        var status = Assert.IsType<StatusResult>(result);
        Assert.Equal("locked", status.State);
        Assert.True(status.UnlockRequested);
        Assert.Equal(1, _unlock.Requests);
        Assert.Equal(AutofillVaultState.Locked, _vault.State);
        Assert.DoesNotContain(Fake.User, json);

        _vault.Unlock();
        var (ready, _) = await Run(broker, Requests.Of(RequestType.Unlock));
        Assert.Equal("ready", Assert.IsType<StatusResult>(ready).State);
        Assert.Equal(1, _unlock.Requests);
    }

    [Fact]
    public async Task Unlock_requests_are_rate_limited()
    {
        using var broker = NewBroker();
        _vault.Lock();
        for (int i = 0; i < 3; i++)
        {
            await Run(broker, Requests.Of(RequestType.Unlock));
        }

        Assert.Equal(ErrorCodes.RateLimited, ErrorOf((await Run(broker, Requests.Of(RequestType.Unlock))).Result));
        Assert.Equal(3, _unlock.Requests);
        _time.Advance(TimeSpan.FromSeconds(61));
        await Run(broker, Requests.Of(RequestType.Unlock));
        Assert.Equal(4, _unlock.Requests);
    }

    // ---- Origen ----

    [Fact]
    public async Task Listing_returns_only_matches_for_the_origin()
    {
        _vault.Add("Otro", "https://otro.test", "x@otro.test", "otra-clave-ficticia");
        _vault.Add("Falso", "https://ejemplo.test.evil.test", "y@evil.test", "clave-falsa-ficticia");
        _vault.Add("Puerto", "https://ejemplo.test:8443", "z@ejemplo.test", "clave-puerto-ficticia");
        using var broker = NewBroker();
        var (result, json) = await Run(broker, Requests.List("https://ejemplo.test"));
        var list = Assert.IsType<ListResult>(result);
        var match = Assert.Single(list.Matches);
        Assert.Equal("Ejemplo", match.Title);
        Assert.Equal(Fake.User, match.Username);
        Assert.Equal("ejemplo.test", match.Site);
        Assert.Equal(OriginMatchKind.Exact, match.Kind);
        foreach (string leaked in new[] { "otro.test", "evil.test", "8443", Fake.Password, "clave" })
        {
            Assert.DoesNotContain(leaked, json);
        }
    }

    [Theory]
    [InlineData("http://ejemplo.test")]
    [InlineData("http://ejemplo.test:80")]
    public async Task Plain_http_pages_get_nothing(string origin)
    {
        using var broker = NewBroker();
        Assert.Equal(ErrorCodes.InsecureOrigin, ErrorOf((await Run(broker, Requests.List(origin))).Result));
        Assert.Equal(0, broker.LiveTokens);
    }

    [Fact]
    public async Task Fill_on_a_plain_http_page_is_refused_even_with_a_valid_token()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        Assert.Equal(ErrorCodes.InsecureOrigin, ErrorOf((await Run(broker, Requests.Fill("http://ejemplo.test", match))).Result));
    }

    [Fact]
    public async Task Subdomains_are_not_offered_unless_enabled_and_are_marked_related()
    {
        _vault.Add("Cuenta", "https://ejemplo.test", "sub@ejemplo.test", "clave-sub-ficticia");
        using var broker = NewBroker();
        var none = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://login.ejemplo.test"))).Result);
        Assert.Empty(none.Matches);

        _policy = new AutofillPolicy { AllowSubdomains = true };
        var related = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://login.ejemplo.test"))).Result);
        Assert.Equal(2, related.Matches.Count);
        Assert.All(related.Matches, m => Assert.Equal(OriginMatchKind.Related, m.Kind));
    }

    [Fact]
    public async Task Exact_matches_come_before_related_ones()
    {
        _policy = new AutofillPolicy { AllowSubdomains = true };
        _vault.Add("AAA relacionada", "https://padre.test", "r@padre.test", "clave-rel-ficticia");
        _vault.Add("ZZZ exacta", "https://login.padre.test", "e@padre.test", "clave-exa-ficticia");
        using var broker = NewBroker();
        var list = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://login.padre.test"))).Result);
        Assert.Equal(["ZZZ exacta", "AAA relacionada"], list.Matches.Select(m => m.Title));
    }

    [Fact]
    public async Task Multiple_accounts_for_the_same_site_are_all_listed_for_the_user_to_choose()
    {
        _vault.Add("Ejemplo (trabajo)", "https://ejemplo.test/login", Fake.User2, Fake.Password2);
        using var broker = NewBroker();
        var list = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
        Assert.Equal(2, list.Matches.Count);
        Assert.Equal(2, list.Matches.Select(m => m.Token).Distinct().Count());
        Assert.Contains(list.Matches, m => m.Username == Fake.User);
        Assert.Contains(list.Matches, m => m.Username == Fake.User2);
    }

    [Fact]
    public async Task Listing_is_capped()
    {
        for (int i = 0; i < 40; i++)
        {
            _vault.Add($"Cuenta {i:00}", "https://ejemplo.test", $"u{i}@ejemplo.test", "clave-ficticia");
        }

        using var broker = NewBroker();
        var list = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
        Assert.Equal(AutofillBroker.MaxMatches, list.Matches.Count);
    }

    // ---- Relleno ----

    [Fact]
    public async Task Fill_returns_the_secret_for_a_listed_credential()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var (result, json) = await Run(broker, Requests.Fill("https://ejemplo.test", match));
        using (result)
        {
            Assert.IsType<SecretResult>(result);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(Fake.Password, doc.RootElement.GetProperty("password").GetString());
        Assert.Equal(Fake.User, doc.RootElement.GetProperty("username").GetString());
        Assert.Single(_confirmer.Shown);
        Assert.Equal("Ejemplo", _confirmer.Shown[0].Title);
        Assert.Equal(Fake.User, _confirmer.Shown[0].Username);
        Assert.Equal("https://ejemplo.test", _confirmer.Shown[0].PageOrigin.Serialize());
    }

    [Theory]
    [InlineData(FillScope.Username)]
    [InlineData(FillScope.Password)]
    public async Task Fill_returns_only_what_was_requested(FillScope scope)
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var (result, json) = await Run(broker, Requests.Fill("https://ejemplo.test", match, scope));
        result.Dispose();
        Assert.Equal(scope == FillScope.Password, json.Contains(Fake.Password, StringComparison.Ordinal));
        Assert.Equal(scope == FillScope.Username, json.Contains(Fake.User, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_works_only_once()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result.Dispose();
        var (again, json) = await Run(broker, Requests.Fill("https://ejemplo.test", match));
        Assert.Equal(ErrorCodes.Expired, ErrorOf(again));
        Assert.DoesNotContain(Fake.Password, json);
    }

    [Fact]
    public async Task A_token_expires()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(ErrorCodes.Expired, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
    }

    [Fact]
    public async Task A_token_cannot_be_reused_for_another_origin_even_one_that_also_matches()
    {
        _policy = new AutofillPolicy { AllowSubdomains = true };
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");

        // Navegación o redirección a otro origen: la ficha nació para ejemplo.test.
        Assert.Equal(ErrorCodes.Denied, ErrorOf((await Run(broker, Requests.Fill("https://login.ejemplo.test", match))).Result));
        // Y se ha inutilizado: ni siquiera sirve ya para el origen original.
        Assert.Equal(ErrorCodes.Expired, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task A_token_cannot_be_used_for_another_credential()
    {
        var other = _vault.Add("Otra", "https://ejemplo.test", Fake.User2, Fake.Password2);
        using var broker = NewBroker();
        var list = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
        var first = list.Matches[0];
        var request = new AutofillRequest(RequestType.Fill, null, Fake.Origin("https://ejemplo.test"), first.Id == other ? list.Matches[1].Id : other, first.Token, FillScope.Both);
        var (result, json) = await Run(broker, request);
        Assert.Equal(ErrorCodes.Denied, ErrorOf(result));
        Assert.DoesNotContain(Fake.Password2, json);
    }

    [Fact]
    public async Task Made_up_tokens_never_work_and_repeated_guesses_lock_fills_out()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var fake = match with { Token = "AAAAAAAAAAAAAAAAAAAAAA" };
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(ErrorCodes.Expired, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", fake))).Result));
        }

        Assert.Equal(ErrorCodes.RateLimited, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", fake))).Result));

        // El bloqueo alcanza también a las fichas buenas, hasta que pasa un minuto.
        Assert.Equal(ErrorCodes.RateLimited, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
        _time.Advance(TimeSpan.FromSeconds(61));
        var fresh = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", fresh))).Result.Dispose();
    }

    [Fact]
    public async Task Turning_subdomains_off_between_list_and_fill_denies_the_fill()
    {
        _vault.Add("Cambiante", "https://cambiante.test", "c@cambiante.test", "clave-cambiante-ficticia");
        _policy = new AutofillPolicy { AllowSubdomains = true };
        using var broker = NewBroker();
        var related = await FirstMatch(broker, "https://sub.cambiante.test");
        Assert.Equal(OriginMatchKind.Related, related.Kind);

        _policy = new AutofillPolicy { AllowSubdomains = false };
        var (result, json) = await Run(broker, Requests.Fill("https://sub.cambiante.test", related));
        Assert.Equal(ErrorCodes.Denied, ErrorOf(result));
        Assert.DoesNotContain("clave-cambiante", json);
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task The_stored_url_is_checked_again_at_the_moment_of_reading()
    {
        // La URL de la cuenta cambia entre mirar y leer (o el almacén se equivoca): la lectura debe rechazarla ella misma.
        var vault = new ChangingUrlVault();
        using var broker = new AutofillBroker(vault, () => new AutofillPolicy { RequireConfirmation = false }, _time, null, _unlock, _audit);
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var (result, json) = await Run(broker, Requests.Fill("https://ejemplo.test", match));
        Assert.Equal(ErrorCodes.NotFound, ErrorOf(result));
        Assert.DoesNotContain(Fake.Password, json);
        Assert.True(vault.ReadAttempted);
    }

    [Fact]
    public async Task Disabling_the_integration_between_list_and_fill_blocks_the_fill()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        _policy = new AutofillPolicy { Enabled = false };
        Assert.Equal(ErrorCodes.Disabled, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
    }

    // ---- Confirmación del usuario y bloqueo durante la solicitud ----

    [Fact]
    public async Task User_cancelling_releases_nothing()
    {
        _confirmer.Answer = false;
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var (result, json) = await Run(broker, Requests.Fill("https://ejemplo.test", match));
        Assert.Equal(ErrorCodes.Denied, ErrorOf(result));
        Assert.DoesNotContain(Fake.Password, json);
        Assert.Equal(0, _vault.ReadCalls);
        Assert.True(_audit.Has(AutofillAuditEvent.Denied));
    }

    [Fact]
    public async Task Confirmation_required_but_unavailable_fails_closed()
    {
        using var broker = NewBroker(confirmer: false);
        var match = await FirstMatch(broker, "https://ejemplo.test");
        Assert.Equal(ErrorCodes.Denied, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task Without_confirmation_option_the_selection_alone_is_enough()
    {
        _policy = new AutofillPolicy { RequireConfirmation = false };
        using var broker = NewBroker(confirmer: false);
        var match = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result.Dispose();
        Assert.Equal(1, _vault.ReadCalls);
    }

    [Fact]
    public async Task Locking_while_the_confirmation_is_pending_releases_nothing()
    {
        _confirmer.HoldAnswer = true;
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var pending = broker.HandleAsync(Requests.Fill("https://ejemplo.test", match), CancellationToken.None);
        await _confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _vault.Lock();
        _confirmer.Complete(true);

        var result = await pending;
        using var buffer = result.Serialize();
        string json = Encoding.UTF8.GetString(buffer.Span);
        Assert.Equal(ErrorCodes.Locked, ErrorOf(result));
        Assert.DoesNotContain(Fake.Password, json);
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task Lock_and_unlock_while_the_confirmation_is_pending_still_releases_nothing()
    {
        _confirmer.HoldAnswer = true;
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var pending = broker.HandleAsync(Requests.Fill("https://ejemplo.test", match), CancellationToken.None);
        await _confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _vault.Lock();
        _vault.Unlock();
        _confirmer.Complete(true);

        Assert.Equal(ErrorCodes.Locked, ErrorOf(await pending));
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task Lock_without_event_is_still_caught_when_the_secret_is_read()
    {
        _confirmer.HoldAnswer = true;
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        var pending = broker.HandleAsync(Requests.Fill("https://ejemplo.test", match), CancellationToken.None);
        await _confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _vault.LockSilently();
        _confirmer.Complete(true);

        Assert.Equal(ErrorCodes.Locked, ErrorOf(await pending));
    }

    [Fact]
    public async Task Locking_invalidates_every_outstanding_token()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        Assert.True(broker.LiveTokens > 0);
        _vault.Lock();
        Assert.Equal(0, broker.LiveTokens);
        _vault.Unlock();
        Assert.Equal(ErrorCodes.Expired, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
    }

    [Fact]
    public async Task A_token_minted_just_before_a_lock_is_dead_after_unlocking()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        _vault.Lock();
        _vault.Unlock();
        // Una ficha de la época anterior que de algún modo siguiera viva tampoco serviría.
        var fresh = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", fresh))).Result.Dispose();
        Assert.Equal(ErrorCodes.Expired, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result));
    }

    [Fact]
    public async Task Confirmation_that_never_answers_times_out_as_denied()
    {
        _confirmer.HoldAnswer = true;
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        using var cts = new CancellationTokenSource();
        var pending = broker.HandleAsync(Requests.Fill("https://ejemplo.test", match), cts.Token);
        await _confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0, _vault.ReadCalls);
    }

    [Fact]
    public async Task A_confirmer_that_ignores_cancellation_cannot_hang_the_broker()
    {
        using var broker = new AutofillBroker(_vault, () => _policy, _time, new DeafConfirmer(), _unlock, _audit);
        var match = await FirstMatch(broker, "https://ejemplo.test");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var pending = broker.HandleAsync(Requests.Fill("https://ejemplo.test", match), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _vault.ReadCalls);

        // Y el relleno queda libre para la siguiente solicitud (no se quedó la puerta cerrada).
        _policy = new AutofillPolicy { RequireConfirmation = false };
        var next = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", next))).Result.Dispose();
        Assert.Equal(1, _vault.ReadCalls);
    }

    [Fact]
    public async Task Only_one_fill_runs_at_a_time()
    {
        _confirmer.HoldAnswer = true;
        _vault.Add("Segunda", "https://ejemplo.test", Fake.User2, Fake.Password2);
        using var broker = NewBroker();
        var list = Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
        var first = broker.HandleAsync(Requests.Fill("https://ejemplo.test", list.Matches[0]), CancellationToken.None);
        await _confirmer.Shown_.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ErrorCodes.Busy, ErrorOf(await broker.HandleAsync(Requests.Fill("https://ejemplo.test", list.Matches[1]), CancellationToken.None)));
        _confirmer.Complete(false);
        Assert.Equal(ErrorCodes.Denied, ErrorOf(await first));
    }

    // ---- Límites de uso ----

    [Fact]
    public async Task Repeated_requests_are_rate_limited_and_recover()
    {
        using var broker = NewBroker();
        for (int i = 0; i < 30; i++)
        {
            Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
        }

        Assert.Equal(ErrorCodes.RateLimited, ErrorOf((await Run(broker, Requests.List("https://ejemplo.test"))).Result));
        Assert.True(_audit.Has(AutofillAuditEvent.RateLimited));
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.IsType<ListResult>((await Run(broker, Requests.List("https://ejemplo.test"))).Result);
    }

    [Fact]
    public async Task Fills_are_rate_limited()
    {
        _policy = new AutofillPolicy { RequireConfirmation = false };
        using var broker = NewBroker(confirmer: false);
        for (int i = 0; i < 10; i++)
        {
            var match = await FirstMatch(broker, "https://ejemplo.test");
            (await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result.Dispose();
            if (i % 2 == 1)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        var last = await FirstMatch(broker, "https://ejemplo.test");
        Assert.Equal(ErrorCodes.RateLimited, ErrorOf((await Run(broker, Requests.Fill("https://ejemplo.test", last))).Result));
    }

    // ---- Auditoría y fallos ----

    [Fact]
    public async Task Audit_contains_events_and_counts_but_no_data()
    {
        using var broker = NewBroker();
        var match = await FirstMatch(broker, "https://ejemplo.test");
        (await Run(broker, Requests.Fill("https://ejemplo.test", match))).Result.Dispose();
        string text = _audit.AsText();
        Assert.Contains("Listed:1", text);
        Assert.Contains("Filled", text);
        foreach (string secret in new[] { Fake.Password, Fake.User, "ejemplo.test", match.Token })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public async Task Unexpected_vault_failures_become_a_generic_error()
    {
        var broker = new AutofillBroker(new ThrowingVault(Fake.Password), () => _policy, _time, _confirmer, _unlock, _audit);
        using (broker)
        {
            var (result, json) = await Run(broker, Requests.List("https://ejemplo.test"));
            Assert.Equal(ErrorCodes.Internal, ErrorOf(result));
            Assert.DoesNotContain(Fake.Password, json);
            Assert.DoesNotContain(Fake.Password, _audit.AsText());
        }
    }

    /// <summary>Confirmador que no hace caso de la cancelación y nunca responde (por ejemplo, una ventana que no llegó a abrirse).</summary>
    private sealed class DeafConfirmer : IFillConfirmer
    {
        public Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken) =>
            new TaskCompletionSource<bool>().Task;
    }

    /// <summary>Muestra una URL que coincide, pero al leer la credencial su URL ya es otra.</summary>
    private sealed class ChangingUrlVault : IAutofillVault
    {
        private readonly Guid _id = Guid.NewGuid();

        public bool ReadAttempted { get; private set; }

        public AutofillVaultState State => AutofillVaultState.Unlocked;

        public event EventHandler? Locked
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AutofillEntryInfo> Snapshot() => [new(_id, "Ejemplo", "https://ejemplo.test", Fake.User)];

        public AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize)
        {
            ReadAttempted = true;
            var current = new AutofillEntryInfo(_id, "Ejemplo", "https://otro-sitio.test", Fake.User);
            return authorize(current)
                ? new AutofillSecret(Encoding.UTF8.GetBytes(Fake.User), Encoding.UTF8.GetBytes(Fake.Password), true, true)
                : null;
        }
    }

    private sealed class ThrowingVault(string secretInMessage) : IAutofillVault
    {
        public AutofillVaultState State => AutofillVaultState.Unlocked;

        public event EventHandler? Locked
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AutofillEntryInfo> Snapshot() => throw new InvalidOperationException($"fallo con {secretInMessage}");

        public AutofillSecret? Read(Guid id, FillScope scope, Func<AutofillEntryInfo, bool> authorize) => throw new InvalidOperationException(secretInMessage);
    }
}
