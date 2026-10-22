using System.Text;
using System.Text.Json;
using PassDesk.App.Infrastructure;
using PassDesk.Auth;
using PassDesk.Browser.Broker;
using PassDesk.Browser.Origins;
using PassDesk.Browser.Protocol;
using PassDesk.Core.Diagnostics;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>
/// Integración del autorrelleno del navegador con la bóveda real de PassDesk (sesión, bloqueo, registro y ajustes). La lógica
/// del protocolo, los orígenes, la tubería y el conector se prueba en PassDesk.Browser.Tests; aquí solo lo que necesita
/// DPAPI y la memoria protegida de Windows. Todo con datos ficticios.
/// </summary>
public class BrowserAutofillTests
{
    private const string Site = "https://ejemplo-autorrelleno.test";
    private const string User = "ana.autorrelleno@ejemplo.test";

    private static VaultSession NewUnlockedSession()
    {
        var session = Fake.NewSession(out _, out _);
        Assert.True(session.CreateVault(Fake.MasterPassword).Succeeded);
        session.AddEntry(Fake.Entry("Ejemplo", Site, User, Fake.Secret));
        session.AddEntry(Fake.Entry("Otro", "https://otro-autorrelleno.test", "otro@ejemplo.test", Fake.Secret2));
        return session;
    }

    private static WebOrigin Origin(string text)
    {
        Assert.True(WebOrigin.TryParse(text, out var origin));
        return origin;
    }

    // ---- SessionAutofillVault ----

    [Fact]
    public void Locked_vault_exposes_nothing()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        Assert.Equal(AutofillVaultState.Unlocked, vault.State);
        var id = Assert.Single(vault.Snapshot(), e => e.Username == User).Id;

        session.Lock(LockReason.Manual);

        Assert.Equal(AutofillVaultState.Locked, vault.State);
        Assert.Empty(vault.Snapshot());
        Assert.Null(vault.Read(id, FillScope.Both, _ => true));
    }

    [Fact]
    public void Vault_that_does_not_exist_is_reported()
    {
        using var session = Fake.NewSession(out _, out _);
        using var vault = new SessionAutofillVault(session);
        Assert.Equal(AutofillVaultState.NotCreated, vault.State);
        Assert.Empty(vault.Snapshot());
    }

    [Fact]
    public void Snapshot_has_metadata_but_never_secrets()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        var snapshot = vault.Snapshot();
        Assert.Equal(2, snapshot.Count);
        string text = JsonSerializer.Serialize(snapshot);
        Assert.Contains(User, text);
        Assert.DoesNotContain(Fake.Secret, text);
        Assert.DoesNotContain(Fake.Secret2, text);
    }

    [Theory]
    [InlineData(FillScope.Both, true, true)]
    [InlineData(FillScope.Username, true, false)]
    [InlineData(FillScope.Password, false, true)]
    public void Read_returns_only_the_requested_parts(FillScope scope, bool user, bool password)
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        var id = vault.Snapshot().Single(e => e.Username == User).Id;
        using var secret = vault.Read(id, scope, _ => true);
        Assert.NotNull(secret);
        Assert.Equal(user, secret.HasUsername);
        Assert.Equal(password, secret.HasPassword);
        if (user)
        {
            Assert.Equal(User, Encoding.UTF8.GetString(secret.Username));
        }

        if (password)
        {
            Assert.Equal(Fake.Secret, Encoding.UTF8.GetString(secret.Password));
        }
    }

    [Fact]
    public void Non_ascii_passwords_round_trip_exactly()
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Acentos", Site, User, Fake.Secret2));
        using var vault = new SessionAutofillVault(session);
        var id = vault.Snapshot().Single().Id;
        using var secret = vault.Read(id, FillScope.Password, _ => true)!;
        Assert.Equal(Fake.Secret2, Encoding.UTF8.GetString(secret.Password));
    }

    [Fact]
    public void Read_needs_the_authorization_callback_to_approve_the_credential()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        var id = vault.Snapshot().Single(e => e.Username == User).Id;
        Assert.Null(vault.Read(id, FillScope.Both, _ => false));
        Assert.Null(vault.Read(Guid.NewGuid(), FillScope.Both, _ => true));
        AutofillEntryInfo? seen = null;
        using var secret = vault.Read(id, FillScope.Both, info =>
        {
            seen = info;
            return true;
        });
        Assert.NotNull(secret);
        Assert.Equal(Site, seen!.Url);
    }

    [Fact]
    public void Locked_event_fires_for_every_way_of_locking_and_not_for_unlocking()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        int locks = 0;
        vault.Locked += (_, _) => locks++;
        foreach (var reason in new[] { LockReason.Manual, LockReason.Inactivity, LockReason.SessionLocked, LockReason.Suspend, LockReason.Minimized, LockReason.Exit })
        {
            Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
            int before = locks;
            session.Lock(reason);
            Assert.Equal(before + 1, locks);
        }

        // Bloquear una bóveda que ya estaba bloqueada no repite el aviso.
        int final = locks;
        session.Lock(LockReason.Manual);
        Assert.Equal(final, locks);
    }

    [Fact]
    public void Disposed_adapter_stops_listening()
    {
        using var session = NewUnlockedSession();
        var vault = new SessionAutofillVault(session);
        int locks = 0;
        vault.Locked += (_, _) => locks++;
        vault.Dispose();
        session.Lock(LockReason.Manual);
        Assert.Equal(0, locks);
    }

    [Fact]
    public async Task A_lock_cannot_slip_in_while_a_secret_is_being_read()
    {
        using var session = NewUnlockedSession();
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var read = Task.Run(() => session.TryRead(entries =>
        {
            reading.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return entries.Count;
        }, out int count) ? count : -1);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        var lockTask = Task.Run(() => session.Lock(LockReason.Manual));
        await Task.Delay(300);
        Assert.False(lockTask.IsCompleted, "el bloqueo espera a que termine la lectura");
        Assert.Equal(VaultState.Unlocked, session.State);

        release.Set();
        Assert.Equal(2, await read);
        await lockTask;
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(session.TryRead(e => e.Count, out int _));
    }

    // ---- Intermediario + bóveda real ----

    [Fact]
    public async Task List_and_fill_work_end_to_end_against_the_real_session()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        using var broker = new AutofillBroker(vault, () => new AutofillPolicy { RequireConfirmation = false });
        using var list = await broker.HandleAsync(new AutofillRequest(RequestType.List, null, Origin(Site), null, null, null), CancellationToken.None);
        var match = Assert.Single(Assert.IsType<ListResult>(list).Matches);
        Assert.Equal(User, match.Username);

        using var fill = await broker.HandleAsync(new AutofillRequest(RequestType.Fill, null, Origin(Site), match.Id, match.Token, FillScope.Both), CancellationToken.None);
        using var json = JsonDocument.Parse(Serialize(fill));
        Assert.Equal(Fake.Secret, json.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Locking_the_real_session_while_the_user_is_confirming_releases_nothing()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        var confirmer = new HoldingConfirmer();
        using var broker = new AutofillBroker(vault, () => new AutofillPolicy(), TimeProvider.System, confirmer);
        using var list = await broker.HandleAsync(new AutofillRequest(RequestType.List, null, Origin(Site), null, null, null), CancellationToken.None);
        var match = Assert.Single(Assert.IsType<ListResult>(list).Matches);

        var pending = broker.HandleAsync(new AutofillRequest(RequestType.Fill, null, Origin(Site), match.Id, match.Token, FillScope.Both), CancellationToken.None);
        await confirmer.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Lock(LockReason.Inactivity);
        confirmer.Answer.SetResult(true);

        using var result = await pending;
        string json = Serialize(result);
        Assert.Equal(ErrorCodes.Locked, Assert.IsType<ErrorResult>(result).Code);
        Assert.DoesNotContain(Fake.Secret, json);
    }

    [Fact]
    public async Task Subdomain_matches_need_the_setting_and_a_different_site_never_matches()
    {
        using var session = NewUnlockedSession();
        using var vault = new SessionAutofillVault(session);
        var policy = new AutofillPolicy();
        using var broker = new AutofillBroker(vault, () => policy);
        async Task<int> Count(string origin)
        {
            using var result = await broker.HandleAsync(new AutofillRequest(RequestType.List, null, Origin(origin), null, null, null), CancellationToken.None);
            return Assert.IsType<ListResult>(result).Matches.Count;
        }

        Assert.Equal(1, await Count(Site));
        Assert.Equal(0, await Count("https://login.ejemplo-autorrelleno.test"));
        policy = new AutofillPolicy { AllowSubdomains = true };
        Assert.Equal(1, await Count("https://login.ejemplo-autorrelleno.test"));
        Assert.Equal(0, await Count("https://ejemplo-autorrelleno.test.evil.test"));
        Assert.Equal(0, await Count("https://nada.test"));
    }

    // ---- Registro, ajustes y comprobaciones de Windows ----

    [Fact]
    public async Task No_secrets_user_names_or_sites_reach_the_log_during_autofill()
    {
        using var temp = new TempDirectory();
        var memory = new MemoryLogSink();
        var file = new FileLogSink(temp.File("logs"));
        AppLog.AddSink(memory);
        AppLog.AddSink(file);
        try
        {
            using var session = NewUnlockedSession();
            using var vault = new SessionAutofillVault(session);
            var audit = new AppLogAutofillAudit();
            using var broker = new AutofillBroker(vault, () => new AutofillPolicy { RequireConfirmation = false }, TimeProvider.System, null, null, audit);
            using var list = await broker.HandleAsync(new AutofillRequest(RequestType.List, null, Origin(Site), null, null, null), CancellationToken.None);
            var match = Assert.IsType<ListResult>(list).Matches[0];
            using (await broker.HandleAsync(new AutofillRequest(RequestType.Fill, null, Origin(Site), match.Id, match.Token, FillScope.Both), CancellationToken.None))
            {
            }

            // Ficha repetida, origen engañoso y bóveda bloqueada: todo denegado, todo sin datos en el registro.
            using (await broker.HandleAsync(new AutofillRequest(RequestType.Fill, null, Origin(Site), match.Id, match.Token, FillScope.Both), CancellationToken.None))
            {
            }

            session.Lock(LockReason.Manual);
            using (await broker.HandleAsync(new AutofillRequest(RequestType.List, null, Origin(Site), null, null, null), CancellationToken.None))
            {
            }

            AppLog.Error(LogEvent.BrowserAutofillFailed, new InvalidOperationException($"fallo con {Fake.Secret} de {User} en {Site}"));
        }
        finally
        {
            AppLog.RemoveSink(memory);
            AppLog.RemoveSink(file);
        }

        foreach (string text in new[] { memory.AllText, File.ReadAllText(file.FilePath) })
        {
            Assert.Contains("BrowserAutofillListed", text);
            Assert.Contains("BrowserAutofillFilled", text);
            Assert.Contains("BrowserAutofillDenied", text);
            foreach (string secret in new[] { Fake.Secret, Fake.Secret2, User, "ejemplo-autorrelleno", "otro-autorrelleno" })
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Fact]
    public void Browser_integration_is_off_by_default_and_asks_for_confirmation()
    {
        var settings = new AppSettings();
        Assert.False(settings.BrowserAutofillEnabled);
        Assert.True(settings.BrowserAutofillConfirm);
        Assert.False(settings.BrowserAutofillSubdomains);
    }

    [Fact]
    public void Settings_from_older_versions_load_with_the_safe_browser_defaults()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("settings.json"), """{ "AutoLockMinutes": 10, "Theme": "Dark" }""");
        var loaded = new SettingsStore(temp.File("settings.json")).Load();
        Assert.Equal(10, loaded.AutoLockMinutes);
        Assert.False(loaded.BrowserAutofillEnabled);
        Assert.True(loaded.BrowserAutofillConfirm);
        Assert.False(loaded.BrowserAutofillSubdomains);
    }

    [Fact]
    public void Browser_settings_persist_and_the_file_has_no_credentials()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        store.Save(new AppSettings { BrowserAutofillEnabled = true, BrowserAutofillConfirm = false, BrowserAutofillSubdomains = true });
        var loaded = store.Load();
        Assert.True(loaded.BrowserAutofillEnabled);
        Assert.False(loaded.BrowserAutofillConfirm);
        Assert.True(loaded.BrowserAutofillSubdomains);
        string text = File.ReadAllText(temp.File("settings.json"));
        Assert.DoesNotContain(Fake.Secret, text);
        Assert.DoesNotContain(User, text);
    }

    [Theory]
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", true)]
    [InlineData(@"c:\users\ANA\appdata\local\programs\passdesk\passdesk.nativehost.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\PassDesk\..\PassDesk\PassDesk.NativeHost.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", true)]
    [InlineData(@"C:\Users\ana\Downloads\PassDesk.NativeHost.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", false)]
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", false)]
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe.evil.exe", @"C:\Users\ana\AppData\Local\Programs\PassDesk\PassDesk.NativeHost.exe", false)]
    [InlineData(null, @"C:\x\PassDesk.NativeHost.exe", false)]
    [InlineData("", @"C:\x\PassDesk.NativeHost.exe", false)]
    [InlineData(@"C:\x\PassDesk.NativeHost.exe", "", false)]
    public void Only_the_installed_host_may_use_the_pipe(string? client, string expected, bool allowed)
    {
        Assert.Equal(allowed, HostProcessVerifier.IsExpectedHost(client, expected));
    }

    private static string Serialize(AutofillResult result)
    {
        using var buffer = result.Serialize();
        return Encoding.UTF8.GetString(buffer.Span);
    }

    private sealed class HoldingConfirmer : IFillConfirmer
    {
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmAsync(FillConfirmation confirmation, CancellationToken cancellationToken)
        {
            Shown.TrySetResult();
            return Answer.Task;
        }
    }
}
