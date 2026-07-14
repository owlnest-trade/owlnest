using System.Globalization;
using System.Xml.Linq;

namespace TradingBot.Web.Services.UserBot;

public sealed record RssArticle(string Id, string Headline, string Summary, string Source, string Url, DateTimeOffset PublishedAt);

/// <summary>
/// Per-user Google News RSS fetcher. Free, no key needed — aggregates from Reuters, MarketWatch,
/// Yahoo Finance, Bloomberg headlines (free portion), Seeking Alpha, etc. Catches stories that
/// Finnhub is slow to surface and provides redundancy across aggregators.
/// </summary>
public sealed class UserGoogleNewsFeed
{
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public UserGoogleNewsFeed(ILogger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Google requires a real-looking UA on news.google.com RSS, otherwise returns 403.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; owlnest.trade/1.0)");
    }

    public async Task<IReadOnlyList<RssArticle>> GetAsync(string ticker, DateTimeOffset since, CancellationToken ct)
    {
        // Search for ticker + stock/earnings/analyst as financial-context filter.
        var q = Uri.EscapeDataString($"\"{ticker}\" stock OR earnings OR analyst");
        var url = $"https://news.google.com/rss/search?q={q}&hl=en-US&gl=US&ceid=US:en";

        string xml;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<RssArticle>();
            xml = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Google News for {Ticker} failed", ticker); return Array.Empty<RssArticle>(); }

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

            // Google News titles are formatted "Real headline - Source Name". Split into title + source.
            var actualSource = src;
            var actualTitle = title;
            var lastDash = title.LastIndexOf(" - ");
            if (lastDash > 0 && lastDash < title.Length - 3)
            {
                var maybeSource = title[(lastDash + 3)..].Trim();
                if (maybeSource.Length is > 2 and < 40 && !maybeSource.Contains(' ', StringComparison.Ordinal) ||
                    maybeSource.Length is > 2 and < 60)
                {
                    actualSource = maybeSource;
                    actualTitle = title[..lastDash].Trim();
                }
            }

            // Strip HTML tags from description (Google News descriptions are HTML-laced).
            var cleanDesc = System.Text.RegularExpressions.Regex.Replace(desc ?? "", "<[^>]+>", " ").Trim();
            if (cleanDesc.Length > 400) cleanDesc = cleanDesc[..400] + "...";

            results.Add(new RssArticle(
                Id: $"gnews:{ticker}:{actualTitle.GetHashCode():X}",
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
