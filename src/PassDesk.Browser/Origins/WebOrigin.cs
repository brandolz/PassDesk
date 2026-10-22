using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace PassDesk.Browser.Origins;

public enum OriginError
{
    None,
    Malformed,

    /// <summary>Esquema distinto de http y https (chrome://, chrome-extension://, about:, file:, data:…).</summary>
    UnsupportedScheme,
}

/// <summary>
/// Origen de una página (esquema, host y puerto), normalizado y validado de forma estricta. Solo se admiten http y
/// https, hosts ASCII (los internacionales llegan en punycode, como los serializa el navegador), sin usuario, ruta,
/// consulta ni fragmento. Un origen que no cumple se rechaza: nunca se «arregla».
/// </summary>
public sealed record WebOrigin(string Scheme, string Host, int Port)
{
    public const int MaxLength = 300;

    public bool IsSecure => Scheme == "https";

    public bool IsIpAddress => IsIpv4(Host);

    /// <summary>Forma canónica, igual que <c>URL.origin</c> en el navegador (el puerto por defecto se omite).</summary>
    public string Serialize() => Port == DefaultPort(Scheme)
        ? $"{Scheme}://{Host}"
        : string.Create(CultureInfo.InvariantCulture, $"{Scheme}://{Host}:{Port}");

    public override string ToString() => Serialize();

    public static bool TryParse(string? text, [NotNullWhen(true)] out WebOrigin? origin) =>
        TryParse(text, out origin, out _);

    public static bool TryParse(string? text, [NotNullWhen(true)] out WebOrigin? origin, out OriginError error)
    {
        origin = null;
        error = OriginError.Malformed;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        string scheme = text[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            if (IsSchemeToken(scheme))
            {
                error = OriginError.UnsupportedScheme;
            }

            return false;
        }

        string authority = text[(schemeEnd + 3)..];
        string hostPart = authority;
        int port = DefaultPort(scheme);
        int colon = authority.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            hostPart = authority[..colon];
            string portText = authority[(colon + 1)..];
            if (!TryParsePort(portText, out port))
            {
                return false;
            }
        }

        if (!TryNormalizeHost(hostPart, out string host))
        {
            return false;
        }

        origin = new WebOrigin(scheme, host, port);
        error = OriginError.None;
        return true;
    }

    /// <summary>
    /// Interpreta la URL guardada en una credencial. Es más permisiva que <see cref="TryParse(string?, out WebOrigin?)"/>
    /// (admite URL sin esquema, con ruta o con usuario), pero el host resultante pasa las mismas comprobaciones.
    /// </summary>
    public static bool TryParseStoredUrl(string? url, [NotNullWhen(true)] out StoredSite? site)
    {
        site = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
        {
            return false;
        }

        string candidate = url.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            return false;
        }

        string idnHost;
        try
        {
            idnHost = uri.IdnHost;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (!TryNormalizeHost(idnHost.TrimEnd('.'), out string host))
        {
            return false;
        }

        site = new StoredSite(uri.Scheme, host, uri.IsDefaultPort ? null : uri.Port);
        return true;
    }

    internal static int DefaultPort(string scheme) => scheme == "https" ? 443 : 80;

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (text.Length is 0 or > 5 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }

    /// <summary>Host DNS en ASCII (minúsculas) o IPv4 canónica. Sin punto final, sin IPv6 y sin formas ambiguas.</summary>
    internal static bool TryNormalizeHost(string text, out string host)
    {
        host = string.Empty;
        if (text.Length is 0 or > 253)
        {
            return false;
        }

        string lower = text.ToLowerInvariant();
        string[] labels = lower.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        // Una IPv4 solo se acepta en su forma canónica (a.b.c.d): «127.1» o «0x7f.1» son ambiguas.
        if (char.IsAsciiDigit(labels[^1][0]))
        {
            if (!IsIpv4(lower))
            {
                return false;
            }
        }

        host = lower;
        return true;
    }

    internal static bool IsIpv4(string host)
    {
        string[] parts = host.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            foreach (char c in part)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            if (int.Parse(part, CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return IPAddress.TryParse(host, out _);
    }

    private static bool IsSchemeToken(string scheme)
    {
        if (scheme.Length is 0 or > 32 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Sitio de una credencial guardada: host ASCII normalizado y puerto explícito (null si es el del esquema).</summary>
public sealed record StoredSite(string Scheme, string Host, int? ExplicitPort);
