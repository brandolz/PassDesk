namespace PassDesk.Core.Text;

/// <summary>Utilidades para extraer y normalizar dominios a partir de URL guardadas.</summary>
public static class DomainUtil
{
    // Sufijos públicos de segundo nivel habituales. No pretende ser la lista completa de
    // la Public Suffix List; basta para agrupar y reconocer servicios comunes sin red.
    private static readonly HashSet<string> SecondLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "co.nz", "org.nz", "co.za", "co.jp", "ne.jp", "or.jp", "co.kr", "or.kr",
        "co.in", "net.in", "org.in", "co.id", "co.il", "co.th",
        "com.mx", "gob.mx", "org.mx", "edu.mx",
        "com.ar", "gob.ar", "org.ar", "com.br", "gov.br", "org.br", "net.br",
        "com.co", "gov.co", "org.co", "com.pe", "gob.pe", "com.ve", "com.ec", "gob.ec",
        "com.uy", "com.py", "com.bo", "com.gt", "com.do", "com.sv", "com.hn", "com.ni",
        "com.pa", "com.cu", "com.pr", "cl.cl",
        "com.es", "nom.es", "org.es", "gob.es", "edu.es",
        "com.cn", "com.hk", "com.tw", "com.sg", "com.my", "com.ph", "com.tr", "com.sa",
        "com.eg", "com.ng", "com.pk", "com.vn", "com.ua", "com.pl", "co.at", "or.at",
    };

    /// <summary>Obtiene el host en minúsculas de una URL (admite URL sin esquema).</summary>
    public static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        string host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return host.Length == 0 ? null : host;
    }

    public static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4 ? host[4..] : host;

    /// <summary>Host sin "www." para mostrar o agrupar (cadena vacía si no hay URL válida).</summary>
    public static string GetDisplayHost(string? url) => TryGetHost(url) is { } host ? StripWww(host) : string.Empty;

    /// <summary>Dominio registrable aproximado: "mail.google.com" → "google.com", "www.bbc.co.uk" → "bbc.co.uk".</summary>
    public static string GetRegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host) || System.Net.IPAddress.TryParse(host, out _))
        {
            return host;
        }

        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }

        string lastTwo = labels[^2] + "." + labels[^1];
        int take = SecondLevelSuffixes.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>Etiqueta principal del servicio: "www.amazon.es" → "amazon".</summary>
    public static string GetServiceLabel(string host)
    {
        string registrable = GetRegistrableDomain(host);
        int dot = registrable.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? registrable[..dot] : registrable;
    }

    /// <summary>
    /// Devuelve una URI navegable solo si el esquema es http o https. Evita lanzar esquemas
    /// arbitrarios (file:, javascript:, protocolos de terceros) a partir de datos importados.
    /// </summary>
    public static bool TryCreateBrowsableUri(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}
