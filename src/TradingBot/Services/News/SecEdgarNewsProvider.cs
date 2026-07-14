using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.News;

/// <summary>
/// Pulls a company's recent SEC filings (8-K material events, 10-Q quarterly, 10-K annual)
/// and surfaces each one as a NewsItem so Claude can reason about it like any other headline.
/// 8-K is the highest-signal source we have: a legally required, primary-document disclosure
/// of material corporate events, filed within 4 business days.
/// </summary>
public sealed class SecEdgarNewsProvider : INewsProvider
{
    private readonly HttpClient _http;
    private readonly CikCache _cikCache;
    private readonly SecEdgarOptions _opts;
    private readonly ILogger<SecEdgarNewsProvider> _log;
    private readonly HashSet<string> _forms;
    private readonly bool _selfDisabled;

    public SecEdgarNewsProvider(
        HttpClient http,
        CikCache cikCache,
        IOptions<SecEdgarOptions> opts,
        ILogger<SecEdgarNewsProvider> log)
    {
        _http = http;
        _cikCache = cikCache;
        _opts = opts.Value;
        _log = log;
        _forms = new HashSet<string>(_opts.Forms, StringComparer.OrdinalIgnoreCase);

        if (!_opts.Enabled)
        {
            _selfDisabled = true;
            _log.LogInformation("SEC EDGAR provider disabled by config");
        }
        else if (string.IsNullOrWhiteSpace(_opts.ContactEmail))
        {
            _selfDisabled = true;
            _log.LogWarning("SEC EDGAR provider disabled — set SecEdgar:ContactEmail in user-secrets (SEC requires a contact in the User-Agent)");
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetRecentNewsAsync(
        string ticker,
        DateTimeOffset since,
        CancellationToken ct)
    {
        if (_selfDisabled) return Array.Empty<NewsItem>();

        var cik = await _cikCache.GetCikAsync(ticker, ct);
        if (cik is null)
        {
            _log.LogDebug("No CIK found for ticker {Ticker}", ticker);
            return Array.Empty<NewsItem>();
        }

        SubmissionsResponse? body;
        try
        {
            body = await _http.GetFromJsonAsync<SubmissionsResponse>($"submissions/CIK{cik}.json", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SEC submissions fetch failed for {Ticker} (CIK {Cik})", ticker, cik);
            return Array.Empty<NewsItem>();
        }

        var recent = body?.Filings?.Recent;
        if (recent is null) return Array.Empty<NewsItem>();

        // Parallel-array shape: index i across all arrays describes one filing.
        var count = recent.AccessionNumber?.Length ?? 0;
        if (count == 0) return Array.Empty<NewsItem>();

        var results = new List<NewsItem>();
        for (int i = 0; i < count; i++)
        {
            var form = recent.Form is { Length: var fl } && i < fl ? recent.Form[i] : null;
            if (string.IsNullOrEmpty(form) || !_forms.Contains(form)) continue;

            // Prefer acceptanceDateTime (precise wall time) over filingDate (date-only).
            DateTimeOffset acceptedAt;
            var acceptedStr = recent.AcceptanceDateTime is { Length: var al } && i < al ? recent.AcceptanceDateTime[i] : null;
            var filingDateStr = recent.FilingDate is { Length: var dl } && i < dl ? recent.FilingDate[i] : null;
            if (!string.IsNullOrEmpty(acceptedStr) && DateTimeOffset.TryParse(acceptedStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedAccepted))
                acceptedAt = parsedAccepted;
            else if (!string.IsNullOrEmpty(filingDateStr) && DateTimeOffset.TryParse(filingDateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedFiling))
                acceptedAt = parsedFiling;
            else
                continue;

            if (acceptedAt <= since) continue;

            var accession = recent.AccessionNumber![i];
            var itemsRaw = recent.Items is { Length: var il } && i < il ? recent.Items[i] : "";
            var primaryDoc = recent.PrimaryDocument is { Length: var pl } && i < pl ? recent.PrimaryDocument[i] : "";
            var docDesc = recent.PrimaryDocDescription is { Length: var ddl } && i < ddl ? recent.PrimaryDocDescription[i] : "";
            var reportDate = recent.ReportDate is { Length: var rdl } && i < rdl ? recent.ReportDate[i] : "";

            var (headline, summary) = BuildHeadlineAndSummary(form!, itemsRaw ?? "", docDesc ?? "", reportDate ?? "");

            results.Add(new NewsItem(
                Id: "sec:" + accession,                                  // namespaced to avoid colliding with Finnhub IDs
                Ticker: ticker.ToUpperInvariant(),
                Headline: headline,
                Summary: summary,
                Source: "SEC EDGAR",
                Url: BuildFilingUrl(cik, accession, primaryDoc ?? ""),
                PublishedAt: acceptedAt));
        }

        _log.LogDebug("SEC returned {Count} fresh filings for {Ticker} since {Since:o}",
            results.Count, ticker, since);

        return results;
    }

    private static (string headline, string summary) BuildHeadlineAndSummary(string form, string itemsRaw, string docDesc, string reportDate)
    {
        if (form.Equals("8-K", StringComparison.OrdinalIgnoreCase))
        {
            // items is a comma-separated list like "2.02,9.01". Expand each to its plain-English meaning.
            var items = itemsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0)
            {
                return ($"[8-K] {(string.IsNullOrEmpty(docDesc) ? "Material event filed" : docDesc)}",
                    "Form 8-K filed with the SEC — content not categorized by item code.");
            }

            var labels = items.Select(code => $"Item {code} — {(Items8K.TryGetValue(code, out var d) ? d : "(unrecognized item)")}").ToArray();
            var headline = "[8-K] " + string.Join("; ", labels.Take(2));     // headline shows up to 2 items
            if (items.Length > 2) headline += $" (+{items.Length - 2} more)";
            var summary = "Form 8-K material-event filing. Items disclosed: " + string.Join(" | ", labels) + ".";
            return (headline, summary);
        }

        if (form.Equals("10-Q", StringComparison.OrdinalIgnoreCase))
        {
            var headline = $"[10-Q] Quarterly report filed" + (string.IsNullOrEmpty(reportDate) ? "" : $" for period ending {reportDate}");
            var summary = "Form 10-Q quarterly financial report filed with the SEC. Contains unaudited quarterly financial statements, management discussion, and updated risk factors.";
            return (headline, summary);
        }

        if (form.Equals("10-K", StringComparison.OrdinalIgnoreCase))
        {
            var headline = $"[10-K] Annual report filed" + (string.IsNullOrEmpty(reportDate) ? "" : $" for period ending {reportDate}");
            var summary = "Form 10-K annual report filed with the SEC. Contains audited annual financial statements, full business description, MD&A, and comprehensive risk factors.";
            return (headline, summary);
        }

        // Fallback for any other configured form types.
        var fbHeadline = $"[{form}] " + (string.IsNullOrEmpty(docDesc) ? "Filed with SEC" : docDesc);
        var fbSummary = $"Form {form} filed with the SEC.";
        return (fbHeadline, fbSummary);
    }

    private static string BuildFilingUrl(string paddedCik, string accession, string primaryDocument)
    {
        // Strip leading zeros from CIK and dashes from accession for the archive URL path.
        var cikInt = long.Parse(paddedCik, CultureInfo.InvariantCulture);
        var accNoDashes = accession.Replace("-", "");
        return string.IsNullOrEmpty(primaryDocument)
            ? $"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={cikInt}"
            : $"https://www.sec.gov/Archives/edgar/data/{cikInt}/{accNoDashes}/{primaryDocument}";
    }

    /// <summary>
    /// Plain-English meaning of the most common 8-K item codes. Source: SEC Form 8-K instructions.
    /// Kept as a static dictionary so we don't pay a startup cost or an extra HTTP fetch.
    /// </summary>
    private static readonly Dictionary<string, string> Items8K = new(StringComparer.Ordinal)
    {
        ["1.01"] = "Entry into a material definitive agreement",
        ["1.02"] = "Termination of a material definitive agreement",
        ["1.03"] = "Bankruptcy or receivership",
        ["1.04"] = "Mine safety — reporting of shutdowns and patterns of violations",
        ["1.05"] = "Material cybersecurity incident",
        ["2.01"] = "Completion of acquisition or disposition of assets",
        ["2.02"] = "Results of operations and financial condition (earnings release)",
        ["2.03"] = "Creation of a direct financial obligation",
        ["2.04"] = "Triggering events that accelerate a direct financial obligation",
        ["2.05"] = "Costs associated with exit or disposal activities",
        ["2.06"] = "Material impairments",
        ["3.01"] = "Notice of delisting or failure to satisfy a continued listing rule",
        ["3.02"] = "Unregistered sales of equity securities",
        ["3.03"] = "Material modification to rights of security holders",
        ["4.01"] = "Changes in registrant's certifying accountant",
        ["4.02"] = "Non-reliance on previously issued financial statements",
        ["5.01"] = "Changes in control of registrant",
        ["5.02"] = "Departure or appointment of directors or principal officers",
        ["5.03"] = "Amendments to articles of incorporation or bylaws",
        ["5.04"] = "Temporary suspension of trading under employee benefit plans",
        ["5.05"] = "Amendments to the registrant's code of ethics",
        ["5.06"] = "Change in shell company status",
        ["5.07"] = "Submission of matters to a vote of security holders",
        ["5.08"] = "Shareholder director nominations",
        ["6.01"] = "ABS informational and computational material",
        ["6.02"] = "Change of servicer or trustee",
        ["6.03"] = "Change in credit enhancement or other external support",
        ["6.04"] = "Failure to make a required distribution",
        ["6.05"] = "Securities act updating disclosure",
        ["7.01"] = "Regulation FD disclosure",
        ["8.01"] = "Other events",
        ["9.01"] = "Financial statements and exhibits",
    };

    // --- Wire types --------------------------------------------------------------------------

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
