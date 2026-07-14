using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.Shared;

/// <summary>One SEC filing reduced to a news-like shape consumed by UserBotInstance.</summary>
public sealed record SecFiling(
    string Id,           // "sec:0001193125-26-123456"
    string Ticker,
    string Form,         // "8-K", "10-Q", "10-K"
    string Headline,     // human-readable summary
    string Summary,
    string Url,
    DateTimeOffset AcceptedAt);

/// <summary>
/// Shared per-ticker SEC filings fetcher. Each ticker is hit at most once per TTL (5 min) no matter
/// how many users are watching it — so 20 users all tracking AAPL = 1 SEC request per 5 min, not 20.
/// This matters because SEC's per-IP rate limit (10 req/sec) is shared across all users on the same
/// App Service instance.
/// </summary>
public sealed class SecFilingsFeed
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> SupportedForms =
        new(new[] { "8-K", "10-Q", "10-K" }, StringComparer.OrdinalIgnoreCase);

    private readonly SecCikCache _cikCache;
    private readonly ILogger<SecFilingsFeed> _log;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SecFilingsFeed(SecCikCache cikCache, ILogger<SecFilingsFeed> log)
    {
        _cikCache = cikCache;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), BaseAddress = new Uri("https://data.sec.gov/") };
        // User-Agent gets overwritten per-call with the requesting user's email.
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Return filings for <paramref name="ticker"/> filed after <paramref name="since"/>.
    /// Uses a 5-minute cache so multiple users watching the same ticker share one SEC request.</summary>
    public async Task<IReadOnlyList<SecFiling>> GetFilingsAsync(
        string ticker, DateTimeOffset since, string contactEmail,
        bool include8K, bool include10Q, bool include10K,
        CancellationToken ct)
    {
        var key = ticker.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var entry) && now - entry.FetchedAt < CacheTtl)
        {
            // Hit — just filter by `since` and form flags
            return Filter(entry.All, since, include8K, include10Q, include10K);
        }

        var fresh = await FetchAsync(key, contactEmail, ct);
        _cache[key] = new CacheEntry(now, fresh);
        return Filter(fresh, since, include8K, include10Q, include10K);
    }

    private static IReadOnlyList<SecFiling> Filter(
        IReadOnlyList<SecFiling> all, DateTimeOffset since,
        bool i8K, bool i10Q, bool i10K)
    {
        return all.Where(f => f.AcceptedAt > since)
                  .Where(f =>
                      (f.Form.Equals("8-K", StringComparison.OrdinalIgnoreCase)  && i8K)  ||
                      (f.Form.Equals("10-Q", StringComparison.OrdinalIgnoreCase) && i10Q) ||
                      (f.Form.Equals("10-K", StringComparison.OrdinalIgnoreCase) && i10K))
                  .ToList();
    }

    private async Task<IReadOnlyList<SecFiling>> FetchAsync(string ticker, string contactEmail, CancellationToken ct)
    {
        var cik = await _cikCache.GetCikAsync(ticker, contactEmail, ct);
        if (cik is null) return Array.Empty<SecFiling>();

        SubmissionsResponse? body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"submissions/CIK{cik}.json");
            // SEC requires this header on every request.
            var ua = string.IsNullOrWhiteSpace(contactEmail) ? "owlnest.trade@example.com" : contactEmail;
            req.Headers.UserAgent.ParseAdd($"owlnest.trade/1.0 ({ua})");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<SecFiling>();
            body = await resp.Content.ReadFromJsonAsync<SubmissionsResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "SEC fetch failed for {Ticker}", ticker);
            return Array.Empty<SecFiling>();
        }

        var r = body?.Filings?.Recent;
        if (r?.AccessionNumber is null) return Array.Empty<SecFiling>();

        var result = new List<SecFiling>();
        for (int i = 0; i < r.AccessionNumber.Length; i++)
        {
            var form = i < (r.Form?.Length ?? 0) ? r.Form![i] : null;
            if (string.IsNullOrEmpty(form) || !SupportedForms.Contains(form)) continue;

            var acceptedStr = i < (r.AcceptanceDateTime?.Length ?? 0) ? r.AcceptanceDateTime![i] : null;
            var filingStr = i < (r.FilingDate?.Length ?? 0) ? r.FilingDate![i] : null;
            DateTimeOffset accepted;
            if (!string.IsNullOrEmpty(acceptedStr) && DateTimeOffset.TryParse(acceptedStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var a))
                accepted = a;
            else if (!string.IsNullOrEmpty(filingStr) && DateTimeOffset.TryParse(filingStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f))
                accepted = f;
            else continue;

            var accession = r.AccessionNumber[i];
            var items = i < (r.Items?.Length ?? 0) ? r.Items![i] : "";
            var primaryDoc = i < (r.PrimaryDocument?.Length ?? 0) ? r.PrimaryDocument![i] : "";
            var docDesc = i < (r.PrimaryDocDescription?.Length ?? 0) ? r.PrimaryDocDescription![i] : "";
            var reportDate = i < (r.ReportDate?.Length ?? 0) ? r.ReportDate![i] : "";

            var (head, summary) = BuildHeadline(form!, items ?? "", docDesc ?? "", reportDate ?? "");
            result.Add(new SecFiling(
                Id: "sec:" + accession,
                Ticker: ticker,
                Form: form!,
                Headline: head,
                Summary: summary,
                Url: BuildUrl(cik, accession, primaryDoc ?? ""),
                AcceptedAt: accepted));
        }
        return result;
    }

    private static (string head, string summary) BuildHeadline(string form, string itemsRaw, string docDesc, string reportDate)
    {
        if (form.Equals("8-K", StringComparison.OrdinalIgnoreCase))
        {
            var items = itemsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0)
                return ($"[8-K] {(string.IsNullOrEmpty(docDesc) ? "Material event filed" : docDesc)}",
                    "Form 8-K material-event filing.");
            var labels = items.Select(c => $"Item {c} — {(Items8K.TryGetValue(c, out var d) ? d : "(unrecognized)")}").ToArray();
            var head = "[8-K] " + string.Join("; ", labels.Take(2)) + (items.Length > 2 ? $" (+{items.Length - 2} more)" : "");
            return (head, "Form 8-K material-event filing. Items: " + string.Join(" | ", labels));
        }
        if (form.Equals("10-Q", StringComparison.OrdinalIgnoreCase))
            return ($"[10-Q] Quarterly report" + (string.IsNullOrEmpty(reportDate) ? "" : $" for period ending {reportDate}"),
                    "Form 10-Q quarterly financial report.");
        if (form.Equals("10-K", StringComparison.OrdinalIgnoreCase))
            return ($"[10-K] Annual report" + (string.IsNullOrEmpty(reportDate) ? "" : $" for period ending {reportDate}"),
                    "Form 10-K annual financial report.");
        return ($"[{form}] {(string.IsNullOrEmpty(docDesc) ? "Filed with SEC" : docDesc)}", $"Form {form} filed.");
    }

    private static string BuildUrl(string paddedCik, string accession, string primaryDoc)
    {
        var cikInt = long.Parse(paddedCik, CultureInfo.InvariantCulture);
        var accNoDash = accession.Replace("-", "");
        return string.IsNullOrEmpty(primaryDoc)
            ? $"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={cikInt}"
            : $"https://www.sec.gov/Archives/edgar/data/{cikInt}/{accNoDash}/{primaryDoc}";
    }

    private static readonly Dictionary<string, string> Items8K = new(StringComparer.Ordinal)
    {
        ["1.01"] = "Entry into a material definitive agreement",
        ["1.02"] = "Termination of a material definitive agreement",
        ["1.03"] = "Bankruptcy or receivership",
        ["1.05"] = "Material cybersecurity incident",
        ["2.01"] = "Completion of acquisition or disposition of assets",
        ["2.02"] = "Results of operations (earnings release)",
        ["2.03"] = "Creation of a direct financial obligation",
        ["2.04"] = "Triggering events that accelerate a direct financial obligation",
        ["2.05"] = "Costs of exit or disposal",
        ["2.06"] = "Material impairments",
        ["3.01"] = "Notice of delisting",
        ["3.02"] = "Unregistered sales of equity",
        ["4.02"] = "Non-reliance on prior financial statements",
        ["5.01"] = "Change in control",
        ["5.02"] = "Departure / appointment of directors or officers",
        ["7.01"] = "Regulation FD disclosure",
        ["8.01"] = "Other events",
        ["9.01"] = "Financial statements and exhibits",
    };

    private sealed record CacheEntry(DateTimeOffset FetchedAt, IReadOnlyList<SecFiling> All);

    private sealed class SubmissionsResponse
    {
        [JsonPropertyName("filings")] public FilingsBlock? Filings { get; set; }
    }
    private sealed class FilingsBlock
    {
        [JsonPropertyName("recent")] public RecentFilings? Recent { get; set; }
    }
    private sealed class RecentFilings
    {
        [JsonPropertyName("accessionNumber")] public string[]? AccessionNumber { get; set; }
        [JsonPropertyName("filingDate")] public string[]? FilingDate { get; set; }
        [JsonPropertyName("reportDate")] public string[]? ReportDate { get; set; }
        [JsonPropertyName("acceptanceDateTime")] public string[]? AcceptanceDateTime { get; set; }
        [JsonPropertyName("form")] public string[]? Form { get; set; }
        [JsonPropertyName("primaryDocument")] public string[]? PrimaryDocument { get; set; }
        [JsonPropertyName("primaryDocDescription")] public string[]? PrimaryDocDescription { get; set; }
        [JsonPropertyName("items")] public string[]? Items { get; set; }
    }
}
