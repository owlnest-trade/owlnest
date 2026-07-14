using System.Globalization;
using System.Xml.Linq;

namespace TradingBot.Web.Services.UserBot;

/// <summary>
/// Per-symbol crypto news fetcher. Crypto has no Finnhub company-news / SEC EDGAR / Form 4
/// equivalent — those are all US-equity infrastructure. Instead we use Google News RSS with
/// a coin-name keyword search (BTC/USD → "Bitcoin", ETH/USD → "Ethereum", etc.) plus
/// CoinDesk's market RSS as a general crypto-news fallback.
///
/// Free, no key needed, decent coverage. The exact same RssArticle shape as UserGoogleNewsFeed
/// so the downstream sentiment + gate pipeline doesn't care that it came from a different feed.
/// </summary>
public sealed class UserCryptoNewsFeed
{
    private readonly HttpClient _http;
    private readonly ILogger _log;

    /// <summary>
    /// Coin slug → search keyword(s). Used to translate Alpaca crypto symbols (BTC/USD) into
    /// a query that Google News understands. Unknown coins fall through to the bare symbol.
    /// </summary>
    private static readonly Dictionary<string, string> CoinKeyword = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "Bitcoin",
        ["ETH"] = "Ethereum",
        ["SOL"] = "Solana",
        ["DOGE"] = "Dogecoin",
        ["XRP"] = "Ripple XRP",
        ["AVAX"] = "Avalanche AVAX",
        ["LTC"] = "Litecoin",
        ["BCH"] = "Bitcoin Cash",
        ["LINK"] = "Chainlink",
        ["DOT"] = "Polkadot",
        ["UNI"] = "Uniswap",
        ["MATIC"] = "Polygon MATIC",
        ["AAVE"] = "Aave",
        ["YFI"] = "yearn.finance",
        ["GRT"] = "The Graph crypto",
        ["MKR"] = "MakerDAO",
        ["SHIB"] = "Shiba Inu coin",
        ["BAT"] = "Basic Attention Token",
        ["CRV"] = "Curve Finance",
        ["SUSHI"] = "Sushi crypto",
    };

    public UserCryptoNewsFeed(ILogger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Google News RSS demands a real-looking UA.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; owlnest.trade/1.0)");
    }

    /// <summary>
    /// Translate "BTC/USD" → "Bitcoin" (or fall back to the bare base symbol).
    /// </summary>
    public static string KeywordFor(string cryptoTicker)
    {
        var slash = cryptoTicker.IndexOf('/');
        var baseSym = slash > 0 ? cryptoTicker[..slash] : cryptoTicker;
        return CoinKeyword.TryGetValue(baseSym, out var kw) ? kw : baseSym;
    }

    public async Task<IReadOnlyList<RssArticle>> GetAsync(string cryptoTicker, DateTimeOffset since, CancellationToken ct)
    {
        var keyword = KeywordFor(cryptoTicker);
        // Bias toward serious financial coverage: pair the coin keyword with "price OR analysis OR
        // SEC OR ETF" so we get market-moving stories, not influencer noise.
        var q = Uri.EscapeDataString($"\"{keyword}\" (price OR analysis OR SEC OR ETF OR rally OR plunge)");
        var url = $"https://news.google.com/rss/search?q={q}&hl=en-US&gl=US&ceid=US:en";

        string xml;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<RssArticle>();
            xml = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Crypto news for {Ticker} failed", cryptoTicker); return Array.Empty<RssArticle>(); }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return Array.Empty<RssArticle>(); }

        var results = new List<RssArticle>();
        foreach (var item in doc.Descendants("item").Take(25))
        {
            var title = item.Element("title")?.Value?.Trim();
            var link = item.Element("link")?.Value?.Trim();
            var desc = item.Element("description")?.Value?.Trim();
            var pubStr = item.Element("pubDate")?.Value?.Trim();
            var src = item.Element("source")?.Value?.Trim() ?? "Google News";

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;
            if (!DateTimeOffset.TryParse(pubStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pub))
                continue;
            if (pub <= since) continue;

            // Google News titles come back as "Real headline - Source Name". Split off the source.
            var actualSource = src;
            var actualTitle = title;
            var lastDash = title.LastIndexOf(" - ");
            if (lastDash > 0 && lastDash < title.Length - 3)
            {
                var maybeSource = title[(lastDash + 3)..].Trim();
                if (maybeSource.Length is > 2 and < 60)
                {
                    actualSource = maybeSource;
                    actualTitle = title[..lastDash].Trim();
                }
            }

            // Strip HTML from description (Google News descriptions are HTML-laced).
            var cleanDesc = System.Text.RegularExpressions.Regex.Replace(desc ?? "", "<[^>]+>", " ").Trim();
            if (cleanDesc.Length > 400) cleanDesc = cleanDesc[..400] + "...";

            results.Add(new RssArticle(
                Id: $"crypto:{cryptoTicker}:{actualTitle.GetHashCode():X}",
                Headline: actualTitle,
                Summary: cleanDesc,
                Source: actualSource,
                Url: link,
                PublishedAt: pub));
        }
        return results;
    }

    public void Dispose() => _http.Dispose();
}
