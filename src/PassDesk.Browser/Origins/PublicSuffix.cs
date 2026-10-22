namespace PassDesk.Browser.Origins;

/// <summary>
/// Dominio registrable aproximado, sin red ni la Public Suffix List completa. Se usa solo para las coincidencias
/// «relacionadas» (subdominios), que están desactivadas por defecto. Además de los sufijos de segundo nivel
/// habituales, incluye plataformas de alojamiento compartido (github.io, herokuapp.com…): en ellas cada subdominio
/// es de un usuario distinto, así que <c>a.github.io</c> y <c>b.github.io</c> nunca se consideran del mismo sitio.
/// </summary>
internal static class PublicSuffix
{
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        // Sufijos de segundo nivel de países.
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk", "nhs.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "id.au",
        "co.nz", "org.nz", "net.nz", "govt.nz", "ac.nz", "co.za", "org.za",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "co.kr", "or.kr", "ne.kr", "go.kr",
        "co.in", "net.in", "org.in", "gen.in", "firm.in", "ind.in", "co.id", "co.il", "org.il", "ac.il", "co.th",
        "com.mx", "gob.mx", "org.mx", "edu.mx",
        "com.ar", "gob.ar", "org.ar", "com.br", "gov.br", "org.br", "net.br",
        "com.co", "gov.co", "org.co", "com.pe", "gob.pe", "com.ve", "com.ec", "gob.ec",
        "com.uy", "com.py", "com.bo", "com.gt", "com.do", "com.sv", "com.hn", "com.ni",
        "com.pa", "com.cu", "com.pr", "cl.cl",
        "com.es", "nom.es", "org.es", "gob.es", "edu.es",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "com.hk", "org.hk", "com.tw", "org.tw", "com.sg",
        "com.my", "com.ph", "com.tr", "com.sa", "com.eg", "com.ng", "com.pk", "com.vn", "com.ua", "com.pl",
        "co.at", "or.at",

        // Alojamiento compartido y servicios que reparten subdominios entre usuarios distintos.
        "github.io", "githubusercontent.com", "gitlab.io", "bitbucket.io", "blogspot.com", "herokuapp.com",
        "herokudns.com", "vercel.app", "now.sh", "netlify.app", "pages.dev", "workers.dev", "web.app",
        "firebaseapp.com", "appspot.com", "googleusercontent.com", "azurewebsites.net", "azurestaticapps.net",
        "cloudfront.net", "amazonaws.com", "s3.amazonaws.com", "elasticbeanstalk.com", "onrender.com", "fly.dev",
        "railway.app", "repl.co", "replit.app", "glitch.me", "myshopify.com", "wixsite.com", "weebly.com",
        "wordpress.com", "tumblr.com", "ngrok.io", "ngrok-free.app", "trycloudflare.com", "duckdns.org",
        "no-ip.org", "dyndns.org", "ddns.net", "hopto.org", "surge.sh", "000webhostapp.com", "localhost.run",
    };

    /// <summary>
    /// Dominio registrable de un host DNS (por ejemplo, «a.b.ejemplo.co.uk» → «ejemplo.co.uk»). Devuelve false para
    /// IP, nombres de una sola etiqueta y hosts que ya son un sufijo público.
    /// </summary>
    public static bool TryGetRegistrableDomain(string host, out string registrable)
    {
        registrable = string.Empty;
        if (WebOrigin.IsIpv4(host))
        {
            return false;
        }

        string[] labels = host.Split('.');
        if (labels.Length < 2 || Suffixes.Contains(host))
        {
            return false;
        }

        // Sufijo público más largo conocido; si no hay ninguno, la última etiqueta (TLD).
        int suffixLabels = 1;
        for (int count = Math.Min(labels.Length - 1, 3); count >= 2; count--)
        {
            if (Suffixes.Contains(string.Join('.', labels[^count..])))
            {
                suffixLabels = count;
                break;
            }
        }

        if (labels.Length <= suffixLabels)
        {
            return false;
        }

        registrable = string.Join('.', labels[^(suffixLabels + 1)..]);
        return true;
    }
}
