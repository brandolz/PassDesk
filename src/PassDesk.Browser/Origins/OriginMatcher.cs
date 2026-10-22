namespace PassDesk.Browser.Origins;

public enum OriginMatchKind
{
    None = 0,

    /// <summary>Mismo host (con «www.» equivalente), mismo puerto y página HTTPS.</summary>
    Exact,

    /// <summary>Mismo dominio registrable pero otro host (subdominio). Solo si <see cref="OriginMatchOptions.AllowSubdomains"/>.</summary>
    Related,
}

public sealed record OriginMatchOptions
{
    /// <summary>Permite credenciales del mismo dominio registrable en otro subdominio. Desactivado por defecto.</summary>
    public bool AllowSubdomains { get; init; }
}

/// <summary>
/// Decide si una credencial guardada puede rellenarse en una página. Compara el origen completo con la URL guardada
/// por hosts, puertos y esquemas enteros: dos nombres nunca son equivalentes por compartir una cadena de texto
/// («ejemplo.com.evil.test» no coincide con «ejemplo.com», ni «miejemplo.com» con «ejemplo.com»).
/// </summary>
public static class OriginMatcher
{
    public static OriginMatchKind Match(WebOrigin page, string? storedUrl, OriginMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Nunca se rellena en páginas sin cifrar.
        if (!page.IsSecure)
        {
            return OriginMatchKind.None;
        }

        if (!WebOrigin.TryParseStoredUrl(storedUrl, out var site))
        {
            return OriginMatchKind.None;
        }

        // Un puerto explícito en la URL guardada debe coincidir; sin puerto, la página debe usar el de HTTPS. Una URL
        // guardada con http:// se admite para la misma dirección en https (mejora de seguridad, no al revés).
        if (page.Port != (site.ExplicitPort ?? 443))
        {
            return OriginMatchKind.None;
        }

        if (page.IsIpAddress || WebOrigin.IsIpv4(site.Host))
        {
            return page.Host == site.Host ? OriginMatchKind.Exact : OriginMatchKind.None;
        }

        if (string.Equals(StripWww(page.Host), StripWww(site.Host), StringComparison.Ordinal))
        {
            return OriginMatchKind.Exact;
        }

        if (options?.AllowSubdomains == true
            && PublicSuffix.TryGetRegistrableDomain(page.Host, out string pageDomain)
            && PublicSuffix.TryGetRegistrableDomain(site.Host, out string siteDomain)
            && string.Equals(pageDomain, siteDomain, StringComparison.Ordinal))
        {
            return OriginMatchKind.Related;
        }

        return OriginMatchKind.None;
    }

    /// <summary>Host de la URL guardada sin «www.», para mostrar al usuario dónde se guardó la cuenta.</summary>
    public static string DisplaySite(string? storedUrl) =>
        WebOrigin.TryParseStoredUrl(storedUrl, out var site) ? StripWww(site.Host) : string.Empty;

    private static string StripWww(string host)
    {
        // Solo el «www.» inicial, y solo si queda un nombre con al menos dos etiquetas.
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.IndexOf('.', 4) > 0)
        {
            return host[4..];
        }

        return host;
    }
}
