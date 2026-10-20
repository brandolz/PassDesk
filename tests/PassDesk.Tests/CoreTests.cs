using System.Text;
using PassDesk.Core.Model;
using PassDesk.Core.Search;
using PassDesk.Core.Security;
using PassDesk.Core.Services;
using PassDesk.Core.Text;

namespace PassDesk.Tests;

public class SearchTests : IDisposable
{
    private readonly List<CredentialEntry> _entries =
    [
        Fake.Entry("Facebook", "https://www.facebook.com", "perfil.prueba@example.com", Fake.Secret),
        Fake.Entry("Correo de Trabajo", "https://outlook.office.com", "ana.garcia@empresa-ficticia.example", "x"),
        Fake.Entry("Banco Ficticio", "https://banca.banco-ficticio.example", "12345678Z", "y"),
        Fake.Entry("Cafetería Ñandú", "https://cafeteria-nandu.example", "cliente_ficticio", "z"),
        Fake.Entry("Amazon", "https://www.amazon.es", "comprador@example.com", "w"),
    ];

    public SearchTests()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            _entries[i].ModifiedUtc = new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
        {
            entry.Dispose();
        }
    }

    [Theory]
    [InlineData("facebook", "Facebook")]          // servicio
    [InlineData("outlook.office", "Correo de Trabajo")] // dominio
    [InlineData("ana.garcia", "Correo de Trabajo")]     // usuario / correo
    [InlineData("12345678", "Banco Ficticio")]          // usuario numérico
    [InlineData("AMAZON.ES", "Amazon")]                 // sin distinguir mayúsculas
    [InlineData("cafeteria nandu", "Cafetería Ñandú")]  // sin tildes, varios términos
    [InlineData("ñandú", "Cafetería Ñandú")]
    public void Finds_by_service_domain_and_username(string query, string expectedTitle)
    {
        var results = CredentialSearch.Apply(_entries, query, null, SortMode.NameAscending);
        Assert.Equal(expectedTitle, Assert.Single(results).Title);
    }

    [Fact]
    public void Never_searches_inside_passwords()
    {
        Assert.Empty(CredentialSearch.Apply(_entries, Fake.Secret, null, SortMode.NameAscending));
    }

    [Fact]
    public void All_terms_must_match()
    {
        Assert.Empty(CredentialSearch.Apply(_entries, "facebook amazon", null, SortMode.NameAscending));
    }

    [Fact]
    public void Filters_by_category_and_sorts()
    {
        var shopping = CredentialSearch.Apply(_entries, null, Category.Shopping, SortMode.NameAscending);
        Assert.Equal("Amazon", Assert.Single(shopping).Title);

        var byName = CredentialSearch.Apply(_entries, "", null, SortMode.NameAscending).Select(e => e.Title).ToList();
        Assert.Equal(new[] { "Amazon", "Banco Ficticio", "Cafetería Ñandú", "Correo de Trabajo", "Facebook" }, byName);

        var newest = CredentialSearch.Apply(_entries, null, null, SortMode.ModifiedNewest);
        Assert.Equal("Amazon", newest[0].Title);
        Assert.Equal("Facebook", newest[^1].Title);
    }

    [Fact]
    public void Prefix_matches_rank_first()
    {
        using var other = Fake.Entry("Mi cuenta de amazon", "https://otro.example", "u", "p");
        var results = CredentialSearch.Apply([.. _entries, other], "amazon", null, SortMode.NameAscending);
        Assert.Equal("Amazon", results[0].Title);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_reflects_edits()
    {
        _entries[0].Title = "Red social renombrada";
        Assert.Single(CredentialSearch.Apply(_entries, "renombrada", null, SortMode.NameAscending));
    }
}

public class ClassificationTests
{
    [Theory]
    [InlineData("Facebook", "https://m.facebook.com", Category.Social)]
    [InlineData("X", "https://x.com/login", Category.Social)]
    [InlineData("Gmail", "https://accounts.google.com", Category.Email)]
    [InlineData("Webmail", "https://mail.empresa-ficticia.example", Category.Email)]
    [InlineData("Steam", "https://store.steampowered.com", Category.Gaming)]
    [InlineData("PlayStation", "https://my.account.sony.com", Category.Gaming)]
    [InlineData("BBVA", "https://www.bbva.es", Category.Banking)]
    [InlineData("Banco Santander", "", Category.Banking)]
    [InlineData("Amazon", "https://www.amazon.co.uk", Category.Shopping)]
    [InlineData("Tienda Ficticia", "", Category.Shopping)]
    [InlineData("Router de casa", "http://192.168.1.1", Category.Other)]
    public void Classifies_common_services(string title, string url, Category expected)
    {
        Assert.Equal(expected, CategoryClassifier.Classify(title, url));
    }

    [Theory]
    [InlineData("https://www.Example.com/login?x=1", "example.com")]
    [InlineData("mail.google.com", "mail.google.com")]
    [InlineData("", "")]
    [InlineData("no es una url", "")]
    public void Extracts_display_host(string url, string expected)
    {
        Assert.Equal(expected, DomainUtil.GetDisplayHost(url));
    }

    [Theory]
    [InlineData("mail.google.com", "google.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("tienda.example.com.mx", "example.com.mx")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    public void Computes_registrable_domain(string host, string expected)
    {
        Assert.Equal(expected, DomainUtil.GetRegistrableDomain(host));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("example.com/login", true)]
    [InlineData("http://192.168.1.1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("", false)]
    public void Only_http_and_https_links_can_be_opened(string url, bool allowed)
    {
        Assert.Equal(allowed, DomainUtil.TryCreateBrowsableUri(url, out _));
    }

    [Fact]
    public void Duplicate_key_ignores_www_path_and_case()
    {
        Assert.Equal(
            CredentialEntry.BuildDuplicateKey("A", "https://www.Example.com/login", "Usuario@Example.com"),
            CredentialEntry.BuildDuplicateKey("B", "example.com", "usuario@example.com"));
    }
}

public class GeneratorTests
{
    [Fact]
    public void Generates_requested_length_with_every_selected_group()
    {
        var options = new PasswordGeneratorOptions(Length: 24, Uppercase: true, Digits: true, Symbols: true);
        for (int i = 0; i < 200; i++)
        {
            string password = PasswordGenerator.Generate(options);
            Assert.Equal(24, password.Length);
            Assert.Contains(password, char.IsAsciiLetterLower);
            Assert.Contains(password, char.IsAsciiLetterUpper);
            Assert.Contains(password, char.IsAsciiDigit);
            Assert.Contains(password, c => !char.IsAsciiLetterOrDigit(c));
        }
    }

    [Fact]
    public void Respects_disabled_groups_and_ambiguous_characters()
    {
        var options = new PasswordGeneratorOptions(Length: 64, Uppercase: false, Digits: false, Symbols: false, ExcludeAmbiguous: true);
        for (int i = 0; i < 100; i++)
        {
            string password = PasswordGenerator.Generate(options);
            Assert.All(password, c => Assert.True(char.IsAsciiLetterLower(c)));
            Assert.DoesNotContain('l', password);
            Assert.DoesNotContain('o', password);
        }
    }

    [Fact]
    public void Generated_passwords_are_unique()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(set.Add(PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 16))));
        }
    }

    [Fact]
    public void Rejects_invalid_length_and_estimates_entropy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 500)));
        Assert.InRange(PasswordGenerator.EstimateEntropyBits(new PasswordGeneratorOptions(Length: 20)), 120, 135);
    }
}

public class TotpTests
{
    // Vectores de prueba oficiales del RFC 6238 (apéndice B), con 8 dígitos.
    [Theory]
    [InlineData(59L, "94287082", TotpAlgorithm.Sha1)]
    [InlineData(1111111109L, "07081804", TotpAlgorithm.Sha1)]
    [InlineData(1234567890L, "89005924", TotpAlgorithm.Sha1)]
    [InlineData(20000000000L, "65353130", TotpAlgorithm.Sha1)]
    [InlineData(59L, "46119246", TotpAlgorithm.Sha256)]
    [InlineData(59L, "90693936", TotpAlgorithm.Sha512)]
    public void Matches_rfc6238_test_vectors(long unixTime, string expected, TotpAlgorithm algorithm)
    {
        string seed = algorithm switch
        {
            TotpAlgorithm.Sha256 => "12345678901234567890123456789012",
            TotpAlgorithm.Sha512 => "1234567890123456789012345678901234567890123456789012345678901234",
            _ => "12345678901234567890",
        };
        using var totp = TotpGenerator.FromRawKey(Encoding.ASCII.GetBytes(seed), 8, 30, algorithm);
        Assert.Equal(expected, totp.ComputeCode(DateTimeOffset.FromUnixTimeSeconds(unixTime)));
    }

    [Fact]
    public void Parses_otpauth_uri_from_apple_export()
    {
        // "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ" = Base32 de "12345678901234567890".
        using var uri = PassDesk.Crypto.ProtectedSecret.FromString("otpauth://totp/Ejemplo:usuario?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Ejemplo&digits=8&period=30&algorithm=SHA1");
        using var totp = TotpGenerator.TryCreate(uri);

        Assert.NotNull(totp);
        Assert.Equal(8, totp.Digits);
        Assert.Equal("94287082", totp.ComputeCode(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(1, totp.SecondsRemaining(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal("TotpGenerator([redacted])", totp.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("otpauth://hotp/x?secret=GEZDGNBV&counter=1")]
    [InlineData("otpauth://totp/x?issuer=sin-secreto")]
    [InlineData("otpauth://totp/x?secret=!!!")]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&digits=12")]
    [InlineData("https://example.com")]
    public void Invalid_uris_produce_no_generator(string value)
    {
        using var uri = PassDesk.Crypto.ProtectedSecret.FromString(value);
        Assert.Null(TotpGenerator.TryCreate(uri));
    }
}

public class ClipboardTests
{
    [Fact]
    public void Copies_as_sensitive_and_clears_after_interval()
    {
        var clipboard = new FakeClipboard();
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(30));
        int clearedEvents = 0;
        guard.Cleared += (_, _) => clearedEvents++;

        var delay = guard.Copy(Fake.Secret, ClipboardItemKind.Password);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        Assert.Equal(Fake.Secret, clipboard.Text);
        Assert.True(clipboard.LastSetWasSensitive);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(scheduler.Items).Delay);

        scheduler.FireAll();
        Assert.Null(clipboard.Text);
        Assert.Equal(1, clearedEvents);
        Assert.False(guard.OwnsClipboard);
    }

    [Fact]
    public void Does_not_clear_content_copied_later_by_the_user()
    {
        var clipboard = new FakeClipboard();
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(20));

        guard.Copy(Fake.Secret, ClipboardItemKind.Password);
        clipboard.UserCopies("texto del usuario");
        scheduler.FireAll();

        Assert.Equal("texto del usuario", clipboard.Text);
    }

    [Fact]
    public void New_copy_replaces_pending_timer_and_lock_clears_immediately()
    {
        var clipboard = new FakeClipboard();
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(45));

        guard.Copy("usuario-ficticio", ClipboardItemKind.Username);
        guard.Copy(Fake.Secret, ClipboardItemKind.Password);

        Assert.True(scheduler.Items[0].Canceled);
        Assert.False(scheduler.Items[1].Canceled);

        Assert.True(guard.ClearIfOwned()); // lo que hace la app al bloquear o salir
        Assert.Null(clipboard.Text);
        Assert.False(guard.ClearIfOwned());
    }

    [Fact]
    public void Busy_clipboard_keeps_ownership_and_retries_until_cleared()
    {
        var clipboard = new BusyClipboard { FailingClears = 2 };
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(30));
        int clearedEvents = 0;
        guard.Cleared += (_, _) => clearedEvents++;

        guard.Copy(Fake.Secret, ClipboardItemKind.Password);
        scheduler.FireAll(); // vence el plazo: el portapapeles está ocupado

        Assert.Equal(Fake.Secret, clipboard.Text);
        Assert.True(guard.OwnsClipboard);
        Assert.Equal(ClipboardGuard.RetryDelay, scheduler.Items[^1].Delay);

        scheduler.FireAll(); // primer reintento: sigue ocupado
        Assert.True(guard.OwnsClipboard);
        scheduler.FireAll(); // segundo reintento: ya se puede

        Assert.Null(clipboard.Text);
        Assert.False(guard.OwnsClipboard);
        Assert.Equal(1, clearedEvents);
    }

    [Fact]
    public void Lock_after_a_failed_clear_still_clears()
    {
        var clipboard = new BusyClipboard { FailingClears = 1 };
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(30));

        guard.Copy(Fake.Secret, ClipboardItemKind.Password);
        scheduler.FireAll(); // falla una vez

        Assert.True(guard.ClearIfOwned()); // al bloquear la bóveda
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public void Exit_insists_while_clipboard_is_busy()
    {
        var clipboard = new BusyClipboard { FailingClears = 3 };
        using var guard = new ClipboardGuard(clipboard, new ManualScheduler(), () => TimeSpan.FromSeconds(30));

        guard.Copy(Fake.Secret, ClipboardItemKind.Password);

        Assert.True(guard.ClearIfOwnedNow(attempts: 5, pause: TimeSpan.Zero));
        Assert.Null(clipboard.Text);
        Assert.Equal(4, clipboard.ClearAttempts);
    }

    [Fact]
    public void Busy_clipboard_does_not_clear_content_copied_later_by_the_user()
    {
        var clipboard = new BusyClipboard { FailingClears = 1 };
        var scheduler = new ManualScheduler();
        using var guard = new ClipboardGuard(clipboard, scheduler, () => TimeSpan.FromSeconds(30));

        guard.Copy(Fake.Secret, ClipboardItemKind.Password);
        scheduler.FireAll(); // falla
        clipboard.SetSensitiveText("copiado después por el usuario"); // otra aplicación copia algo
        scheduler.FireAll(); // el reintento ve que el contenido ya no es de PassDesk

        Assert.Equal("copiado después por el usuario", clipboard.Text);
        Assert.False(guard.OwnsClipboard);
    }
}
