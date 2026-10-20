using PassDesk.Core.Text;

namespace PassDesk.Core.Model;

/// <summary>
/// Clasifica credenciales en categorías a partir del dominio y del título, sin conexión a internet.
/// El usuario siempre puede cambiar la categoría manualmente.
/// </summary>
public static class CategoryClassifier
{
    private static readonly Dictionary<string, Category> ServiceLabels = new(StringComparer.Ordinal)
    {
        // Redes sociales y mensajería
        ["facebook"] = Category.Social, ["instagram"] = Category.Social, ["twitter"] = Category.Social,
        ["tiktok"] = Category.Social, ["snapchat"] = Category.Social, ["linkedin"] = Category.Social,
        ["reddit"] = Category.Social, ["pinterest"] = Category.Social, ["tumblr"] = Category.Social,
        ["threads"] = Category.Social, ["whatsapp"] = Category.Social, ["telegram"] = Category.Social,
        ["discord"] = Category.Social, ["bsky"] = Category.Social, ["mastodon"] = Category.Social,
        ["vk"] = Category.Social, ["meta"] = Category.Social, ["messenger"] = Category.Social,
        ["signal"] = Category.Social, ["quora"] = Category.Social, ["flickr"] = Category.Social,
        ["badoo"] = Category.Social, ["tinder"] = Category.Social, ["bumble"] = Category.Social,

        // Correo electrónico
        ["gmail"] = Category.Email, ["google"] = Category.Email, ["outlook"] = Category.Email,
        ["hotmail"] = Category.Email, ["live"] = Category.Email, ["yahoo"] = Category.Email,
        ["proton"] = Category.Email, ["protonmail"] = Category.Email, ["icloud"] = Category.Email,
        ["zoho"] = Category.Email, ["gmx"] = Category.Email, ["aol"] = Category.Email,
        ["tutanota"] = Category.Email, ["tuta"] = Category.Email, ["fastmail"] = Category.Email,
        ["yandex"] = Category.Email, ["mail"] = Category.Email,

        // Videojuegos
        ["steampowered"] = Category.Gaming, ["steamcommunity"] = Category.Gaming, ["steam"] = Category.Gaming,
        ["epicgames"] = Category.Gaming, ["xbox"] = Category.Gaming, ["playstation"] = Category.Gaming,
        ["nintendo"] = Category.Gaming, ["ea"] = Category.Gaming, ["origin"] = Category.Gaming,
        ["ubisoft"] = Category.Gaming, ["ubi"] = Category.Gaming, ["battle"] = Category.Gaming,
        ["blizzard"] = Category.Gaming, ["riotgames"] = Category.Gaming, ["roblox"] = Category.Gaming,
        ["minecraft"] = Category.Gaming, ["mojang"] = Category.Gaming, ["twitch"] = Category.Gaming,
        ["gog"] = Category.Gaming, ["rockstargames"] = Category.Gaming, ["valorant"] = Category.Gaming,
        ["leagueoflegends"] = Category.Gaming, ["supercell"] = Category.Gaming, ["itch"] = Category.Gaming,

        // Bancos y finanzas
        ["paypal"] = Category.Banking, ["bbva"] = Category.Banking, ["santander"] = Category.Banking,
        ["caixabank"] = Category.Banking, ["lacaixa"] = Category.Banking, ["ing"] = Category.Banking,
        ["revolut"] = Category.Banking, ["n26"] = Category.Banking, ["wise"] = Category.Banking,
        ["bancsabadell"] = Category.Banking, ["sabadell"] = Category.Banking, ["bankinter"] = Category.Banking,
        ["unicaja"] = Category.Banking, ["abanca"] = Category.Banking, ["kutxabank"] = Category.Banking,
        ["openbank"] = Category.Banking, ["imaginbank"] = Category.Banking, ["evobanco"] = Category.Banking,
        ["cajamar"] = Category.Banking, ["ibercaja"] = Category.Banking, ["bancolombia"] = Category.Banking,
        ["banamex"] = Category.Banking, ["bancomer"] = Category.Banking, ["banorte"] = Category.Banking,
        ["nubank"] = Category.Banking, ["mercadopago"] = Category.Banking, ["bizum"] = Category.Banking,
        ["visa"] = Category.Banking, ["mastercard"] = Category.Banking, ["americanexpress"] = Category.Banking,
        ["amex"] = Category.Banking, ["chase"] = Category.Banking, ["wellsfargo"] = Category.Banking,
        ["bankofamerica"] = Category.Banking, ["citi"] = Category.Banking, ["hsbc"] = Category.Banking,
        ["barclays"] = Category.Banking, ["coinbase"] = Category.Banking, ["binance"] = Category.Banking,
        ["kraken"] = Category.Banking, ["degiro"] = Category.Banking, ["trade republic"] = Category.Banking,
        ["traderepublic"] = Category.Banking, ["myinvestor"] = Category.Banking, ["indexacapital"] = Category.Banking,

        // Compras
        ["amazon"] = Category.Shopping, ["ebay"] = Category.Shopping, ["aliexpress"] = Category.Shopping,
        ["alibaba"] = Category.Shopping, ["mercadolibre"] = Category.Shopping, ["mercadolivre"] = Category.Shopping,
        ["zara"] = Category.Shopping, ["shein"] = Category.Shopping, ["temu"] = Category.Shopping,
        ["walmart"] = Category.Shopping, ["etsy"] = Category.Shopping, ["elcorteingles"] = Category.Shopping,
        ["pccomponentes"] = Category.Shopping, ["mediamarkt"] = Category.Shopping, ["ikea"] = Category.Shopping,
        ["carrefour"] = Category.Shopping, ["wallapop"] = Category.Shopping, ["vinted"] = Category.Shopping,
        ["decathlon"] = Category.Shopping, ["lidl"] = Category.Shopping, ["primor"] = Category.Shopping,
        ["fnac"] = Category.Shopping, ["asos"] = Category.Shopping, ["zalando"] = Category.Shopping,
        ["nike"] = Category.Shopping, ["adidas"] = Category.Shopping, ["hm"] = Category.Shopping,
        ["mango"] = Category.Shopping, ["bershka"] = Category.Shopping, ["pullandbear"] = Category.Shopping,
        ["glovo"] = Category.Shopping, ["justeat"] = Category.Shopping, ["ubereats"] = Category.Shopping,
        ["bestbuy"] = Category.Shopping, ["target"] = Category.Shopping, ["costco"] = Category.Shopping,
        ["shopify"] = Category.Shopping, ["miravia"] = Category.Shopping, ["leroymerlin"] = Category.Shopping,
    };

    // Dominios cuyo nombre corto es ambiguo y se reconocen completos.
    private static readonly Dictionary<string, Category> FullDomains = new(StringComparer.Ordinal)
    {
        ["x.com"] = Category.Social,
        ["t.me"] = Category.Social,
        ["live.com"] = Category.Email,
        ["battle.net"] = Category.Gaming,
        ["ea.com"] = Category.Gaming,
        ["ing.es"] = Category.Banking,
        ["ing.com"] = Category.Banking,
        ["hm.com"] = Category.Shopping,
        ["wise.com"] = Category.Banking,
    };

    private static readonly (string Keyword, Category Category)[] Keywords =
    [
        ("banco", Category.Banking), ("bank", Category.Banking), ("banca", Category.Banking),
        ("caja", Category.Banking), ("credit", Category.Banking), ("tarjeta", Category.Banking),
        ("correo", Category.Email), ("webmail", Category.Email), ("mail", Category.Email),
        ("shop", Category.Shopping), ("tienda", Category.Shopping), ("store", Category.Shopping),
        ("game", Category.Gaming), ("juego", Category.Gaming),
    ];

    public static Category Classify(string? title, string? url)
    {
        if (DomainUtil.TryGetHost(url) is { } host)
        {
            string registrable = DomainUtil.GetRegistrableDomain(host);
            if (FullDomains.TryGetValue(registrable, out var byDomain))
            {
                return byDomain;
            }

            string label = DomainUtil.GetServiceLabel(host);
            if (label.Length > 1 && ServiceLabels.TryGetValue(label, out var byLabel))
            {
                return byLabel;
            }

            // Subdominios como "mail.empresa.com" o "banca.entidad.es".
            foreach (string part in host.Split('.'))
            {
                if (part.Length > 2 && ServiceLabels.TryGetValue(part, out var bySubdomain))
                {
                    return bySubdomain;
                }
            }

            if (MatchKeyword(registrable) is { } byHostKeyword)
            {
                return byHostKeyword;
            }
        }

        string foldedTitle = TextNormalizer.Fold(title);
        if (foldedTitle.Length > 0)
        {
            foreach (string word in foldedTitle.Split([' ', '.', '-', '_', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > 1 && ServiceLabels.TryGetValue(word, out var byWord))
                {
                    return byWord;
                }
            }

            if (MatchKeyword(foldedTitle) is { } byTitleKeyword)
            {
                return byTitleKeyword;
            }
        }

        return Category.Other;
    }

    private static Category? MatchKeyword(string text)
    {
        foreach (var (keyword, category) in Keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return null;
    }
}
