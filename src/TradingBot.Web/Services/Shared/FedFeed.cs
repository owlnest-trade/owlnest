using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace TradingBot.Web.Services.Shared;

public sealed record FedEvent(string Title, string Url, DateTimeOffset PublishedAt, string Source);

/// <summary>
/// Shared singleton — polls federalreserve.gov RSS feeds every hour. The latest few events get
/// injected into the macro prompt summary alongside Manifold odds, so the LLM knows about recent
/// Fed actions (rate decisions, speeches by voting members, monetary policy statements) when
/// reasoning about rate-sensitive trades.
/// </summary>
public sealed class FedFeed : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    // Three Fed RSS feeds — combined into one event stream.
    private static readonly (string Url, string Label)[] Feeds = new[]
    {
        ("https://www.federalreserve.gov/feeds/press_monetary.xml", "Fed Monetary"),
        ("https://www.federalreserve.gov/feeds/press_all.xml",       "Fed Press"),
        ("https://www.federalreserve.gov/feeds/speeches.xml",        "Fed Speeches"),
    };

    private readonly HttpClient _http;
    private readonly ILogger<FedFeed> _log;
    private IReadOnlyList<FedEvent> _events = Array.Empty<FedEvent>();
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

    public FedFeed(ILogger<FedFeed> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("owlnest.trade/1.0 (+https://owlnest.trade)");
    }

    public IReadOnlyList<FedEvent> Recent(int n = 5) =>
        _events.Take(n).ToList();

    /// <summary>One-line summary suitable for prompt injection. Empty if no events in last 14 days.</summary>
    public string PromptSummary()
    {
        if (_events.Count == 0) return "";
        var fortnight = DateTimeOffset.UtcNow.AddDays(-14);
        var recent = _events.Where(e => e.PublishedAt >= fortnight).Take(3).ToList();
        if (recent.Count == 0) return "";
        var sb = new StringBuilder("Recent Fed activity: ");
        for (int i = 0; i < recent.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            var daysAgo = (int)(DateTimeOffset.UtcNow - recent[i].PublishedAt).TotalDays;
            sb.Append('"').Append(recent[i].Title.Trim()).Append("\" (").Append(daysAgo).Append("d ago)");
        }
        sb.Append('.');
        return sb.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Fed RSS poll failed"); }
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var all = new List<FedEvent>();
        foreach (var (url, label) in Feeds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var xml = await resp.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(xml);

                // Standard RSS 2.0 shape
                foreach (var item in doc.Descendants("item").Take(20))
                {
                    var title = item.Element("title")?.Value?.Trim();
                    var link = item.Element("link")?.Value?.Trim();
                    var pubDateStr = item.Element("pubDate")?.Value?.Trim();
                    if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;
                    if (!DateTimeOffset.TryParse(pubDateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pub))
                        pub = DateTimeOffset.UtcNow;
                    all.Add(new FedEvent(title, link, pub, label));
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Fed feed {Url} parse failed", url); }
        }

        _events = all.OrderByDescending(e => e.PublishedAt).Take(30).ToList();
        _lastUpdate = DateTimeOffset.UtcNow;
        _log.LogInformation("Fed RSS: refreshed {N} events across {F} feeds", _events.Count, Feeds.Length);
    }

    public override void Dispose() { _http.Dispose(); base.Dispose(); }
}
