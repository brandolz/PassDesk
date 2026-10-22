using PassDesk.Browser.Origins;

namespace PassDesk.Browser.Tests;

public class OriginParsingTests
{
    [Theory]
    [InlineData("https://ejemplo.com", "https", "ejemplo.com", 443)]
    [InlineData("https://EJEMPLO.com", "https", "ejemplo.com", 443)]
    [InlineData("https://ejemplo.com:443", "https", "ejemplo.com", 443)]
    [InlineData("https://ejemplo.com:8443", "https", "ejemplo.com", 8443)]
    [InlineData("http://ejemplo.com", "http", "ejemplo.com", 80)]
    [InlineData("https://login.ejemplo.co.uk", "https", "login.ejemplo.co.uk", 443)]
    [InlineData("https://xn--bcher-kva.example", "https", "xn--bcher-kva.example", 443)]
    [InlineData("https://192.168.1.10:9000", "https", "192.168.1.10", 9000)]
    public void Valid_origins_are_normalized(string text, string scheme, string host, int port)
    {
        Assert.True(WebOrigin.TryParse(text, out var origin));
        Assert.Equal(new WebOrigin(scheme, host, port), origin);
    }

    [Theory]
    [InlineData("https://ejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com:443", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com:8443", "https://ejemplo.com:8443")]
    [InlineData("http://ejemplo.com:80", "http://ejemplo.com")]
    public void Serialize_matches_browser_origin_format(string text, string expected)
    {
        Assert.True(WebOrigin.TryParse(text, out var origin));
        Assert.Equal(expected, origin.Serialize());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ejemplo.com")]
    [InlineData("//ejemplo.com")]
    [InlineData("https://")]
    [InlineData("https://ejemplo.com/")]
    [InlineData("https://ejemplo.com/ruta")]
    [InlineData("https://ejemplo.com?x=1")]
    [InlineData("https://ejemplo.com#frag")]
    [InlineData("https://usuario@ejemplo.com")]
    [InlineData("https://ejemplo.com@evil.test")]
    [InlineData("https://usuario:clave@ejemplo.com")]
    [InlineData("https://ejemplo.com.")]
    [InlineData("https://.ejemplo.com")]
    [InlineData("https://ejemplo..com")]
    [InlineData("https://ejemplo.com:")]
    [InlineData("https://ejemplo.com:0")]
    [InlineData("https://ejemplo.com:65536")]
    [InlineData("https://ejemplo.com:08443")]
    [InlineData("https://ejemplo.com:44a")]
    [InlineData("https://ejemplo.com:1:2")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::1]:8443")]
    [InlineData("https://ejemplo.com\\@evil.test")]
    [InlineData("https://ejemplo.com evil.test")]
    [InlineData("https://ejemplo.com\n")]
    [InlineData("https://-ejemplo.com")]
    [InlineData("https://ejemplo-.com")]
    [InlineData("https://ejemplo_.com")]
    [InlineData("https://ejemplo.com%2eevil.test")]
    [InlineData("https://ejémplo.com")]
    [InlineData("https://127.1")]
    [InlineData("https://0x7f.0.0.1")]
    [InlineData("https://300.1.1.1")]
    [InlineData("https://1.2.3")]
    [InlineData("https://01.2.3.4")]
    public void Malformed_origins_are_rejected(string text)
    {
        Assert.False(WebOrigin.TryParse(text, out var origin, out var error));
        Assert.Null(origin);
        Assert.Equal(OriginError.Malformed, error);
    }

    [Theory]
    [InlineData("chrome://settings")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("edge://settings")]
    [InlineData("brave://settings")]
    [InlineData("about://blank")]
    [InlineData("file://host")]
    [InlineData("ftp://ejemplo.com")]
    [InlineData("javascript://ejemplo.com")]
    [InlineData("data://x")]
    [InlineData("view-source://ejemplo.com")]
    public void Browser_internal_and_non_web_origins_are_unsupported(string text)
    {
        Assert.False(WebOrigin.TryParse(text, out _, out var error));
        Assert.Equal(OriginError.UnsupportedScheme, error);
    }

    [Fact]
    public void Excessively_long_origins_are_rejected()
    {
        string text = "https://" + new string('a', WebOrigin.MaxLength) + ".com";
        Assert.False(WebOrigin.TryParse(text, out _));
        string longLabel = "https://" + new string('a', 64) + ".com";
        Assert.False(WebOrigin.TryParse(longLabel, out _));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.False(WebOrigin.TryParse(null, out _));
    }
}

public class OriginMatcherTests
{
    private static WebOrigin Page(string origin)
    {
        Assert.True(WebOrigin.TryParse(origin, out var parsed), origin);
        return parsed;
    }

    private static readonly OriginMatchOptions Exact = new();
    private static readonly OriginMatchOptions WithSubdomains = new() { AllowSubdomains = true };

    [Theory]
    [InlineData("https://ejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com/login")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com/a/b?c=d#e")]
    [InlineData("https://ejemplo.com", "ejemplo.com")]
    [InlineData("https://ejemplo.com", "EJEMPLO.COM")]
    [InlineData("https://ejemplo.com", "https://usuario@ejemplo.com/")]
    [InlineData("https://www.ejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://www.ejemplo.com")]
    [InlineData("https://www.ejemplo.com", "https://www.ejemplo.com")]
    [InlineData("https://login.ejemplo.com", "https://login.ejemplo.com")]
    [InlineData("https://ejemplo.com:8443", "https://ejemplo.com:8443")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com:443")]
    [InlineData("https://ejemplo.com", "http://ejemplo.com")]
    [InlineData("https://ejemplo.co.uk", "https://www.ejemplo.co.uk")]
    [InlineData("https://xn--bcher-kva.example", "https://bücher.example")]
    [InlineData("https://192.168.1.10", "https://192.168.1.10")]
    [InlineData("https://192.168.1.10:9000", "192.168.1.10:9000")]
    // El texto antes de la «@» es el usuario, no el host: lo guardado es para evil.test.
    [InlineData("https://evil.test", "https://ejemplo.com@evil.test/")]
    public void Exact_matches(string page, string stored)
    {
        Assert.Equal(OriginMatchKind.Exact, OriginMatcher.Match(Page(page), stored, Exact));
        Assert.Equal(OriginMatchKind.Exact, OriginMatcher.Match(Page(page), stored, WithSubdomains));
    }

    [Theory]
    // Dominios engañosos: comparten texto, no host.
    [InlineData("https://ejemplo.com.evil.test", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com.evil.test")]
    [InlineData("https://miejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://miejemplo.com")]
    [InlineData("https://ejemplo-com.test", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com.test", "https://ejemplo.com")]
    [InlineData("https://ejemplo.comm", "https://ejemplo.com")]
    [InlineData("https://ejemplo.co", "https://ejemplo.com")]
    [InlineData("https://ejernplo.com", "https://ejemplo.com")]
    [InlineData("https://wwwejemplo.com", "https://ejemplo.com")]
    [InlineData("https://www.www.ejemplo.com", "https://ejemplo.com")]
    // Usuario en la URL guardada: lo que cuenta es el host real.
    [InlineData("https://ejemplo.com", "https://ejemplo.com@evil.test/")]
    // Homógrafos: la «е» cirílica da otro host en punycode.
    [InlineData("https://xn--jemplo-qmf.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://еjemplo.com")]
    // Puertos distintos, esquemas y otros sitios.
    [InlineData("https://ejemplo.com:8443", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com:8443")]
    [InlineData("https://ejemplo.com:9443", "https://ejemplo.com:8443")]
    [InlineData("https://otro.com", "https://ejemplo.com")]
    // Subdominios y padres: no son exactos.
    [InlineData("https://login.ejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://login.ejemplo.com")]
    [InlineData("https://a.ejemplo.com", "https://b.ejemplo.com")]
    // Direcciones IP.
    [InlineData("https://192.168.1.10", "https://192.168.1.11")]
    [InlineData("https://192.168.1.10", "https://192.168.1.10:8443")]
    // URL guardada inservible.
    [InlineData("https://ejemplo.com", "")]
    [InlineData("https://ejemplo.com", "   ")]
    [InlineData("https://ejemplo.com", "javascript:alert(1)")]
    [InlineData("https://ejemplo.com", "ftp://ejemplo.com")]
    [InlineData("https://ejemplo.com", "file:///c:/ejemplo.com")]
    public void Do_not_match_by_default(string page, string stored)
    {
        Assert.Equal(OriginMatchKind.None, OriginMatcher.Match(Page(page), stored, Exact));
    }

    [Theory]
    [InlineData("https://ejemplo.com.evil.test", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://ejemplo.com.evil.test")]
    [InlineData("https://miejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com:8443", "https://login.ejemplo.com")]
    [InlineData("https://login.ejemplo.com:8443", "https://ejemplo.com")]
    [InlineData("https://192.168.1.10", "https://192.168.1.11")]
    [InlineData("https://a.github.io", "https://b.github.io")]
    [InlineData("https://usuario.github.io", "https://github.io")]
    [InlineData("https://github.io", "https://usuario.github.io")]
    [InlineData("https://a.herokuapp.com", "https://b.herokuapp.com")]
    [InlineData("https://a.s3.amazonaws.com", "https://b.s3.amazonaws.com")]
    [InlineData("https://a.co.uk", "https://b.co.uk")]
    [InlineData("https://co.uk", "https://a.co.uk")]
    [InlineData("https://intranet", "https://otro")]
    [InlineData("https://a.localhost", "https://b.localhost")]
    public void Do_not_match_even_with_subdomains_allowed(string page, string stored)
    {
        Assert.Equal(OriginMatchKind.None, OriginMatcher.Match(Page(page), stored, WithSubdomains));
    }

    [Theory]
    [InlineData("https://login.ejemplo.com", "https://ejemplo.com")]
    [InlineData("https://ejemplo.com", "https://login.ejemplo.com")]
    [InlineData("https://a.ejemplo.com", "https://b.ejemplo.com")]
    [InlineData("https://a.b.ejemplo.com", "https://ejemplo.com/login")]
    [InlineData("https://cuenta.ejemplo.co.uk", "https://www.ejemplo.co.uk")]
    [InlineData("https://login.ejemplo.com:8443", "https://ejemplo.com:8443")]
    public void Related_matches_need_the_option(string page, string stored)
    {
        Assert.Equal(OriginMatchKind.None, OriginMatcher.Match(Page(page), stored, Exact));
        Assert.Equal(OriginMatchKind.Related, OriginMatcher.Match(Page(page), stored, WithSubdomains));
    }

    [Theory]
    [InlineData("http://ejemplo.com", "https://ejemplo.com")]
    [InlineData("http://ejemplo.com", "http://ejemplo.com")]
    [InlineData("http://ejemplo.com:80", "ejemplo.com")]
    [InlineData("http://ejemplo.com:8080", "http://ejemplo.com:8080")]
    [InlineData("http://192.168.1.10", "http://192.168.1.10")]
    public void Plain_http_pages_never_match(string page, string stored)
    {
        Assert.Equal(OriginMatchKind.None, OriginMatcher.Match(Page(page), stored, Exact));
        Assert.Equal(OriginMatchKind.None, OriginMatcher.Match(Page(page), stored, WithSubdomains));
    }

    [Fact]
    public void Display_site_drops_www_and_path()
    {
        Assert.Equal("ejemplo.com", OriginMatcher.DisplaySite("https://www.ejemplo.com/login?x=1"));
        Assert.Equal("login.ejemplo.com", OriginMatcher.DisplaySite("login.ejemplo.com"));
        Assert.Equal(string.Empty, OriginMatcher.DisplaySite("javascript:alert(1)"));
        Assert.Equal(string.Empty, OriginMatcher.DisplaySite(null));
    }

    [Fact]
    public void Registrable_domain_handles_second_level_and_shared_hosting_suffixes()
    {
        Assert.True(PublicSuffix.TryGetRegistrableDomain("a.b.ejemplo.co.uk", out string d1));
        Assert.Equal("ejemplo.co.uk", d1);
        Assert.True(PublicSuffix.TryGetRegistrableDomain("www.ejemplo.com", out string d2));
        Assert.Equal("ejemplo.com", d2);
        Assert.True(PublicSuffix.TryGetRegistrableDomain("usuario.github.io", out string d3));
        Assert.Equal("usuario.github.io", d3);
        Assert.True(PublicSuffix.TryGetRegistrableDomain("x.bucket.s3.amazonaws.com", out string d4));
        Assert.Equal("bucket.s3.amazonaws.com", d4);
        Assert.False(PublicSuffix.TryGetRegistrableDomain("com", out _));
        Assert.False(PublicSuffix.TryGetRegistrableDomain("co.uk", out _));
        Assert.False(PublicSuffix.TryGetRegistrableDomain("github.io", out _));
        Assert.False(PublicSuffix.TryGetRegistrableDomain("10.0.0.1", out _));
    }
}
