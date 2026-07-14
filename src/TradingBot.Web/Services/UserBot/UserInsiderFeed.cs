using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.UserBot;

public sealed record InsiderTxn(
    string Id,
    string Ticker,
    string Name,
    long Share,        // negative = sale, positive = buy
    decimal Price,
    DateTimeOffset At,
    string Code);      // "P" purchase, "S" sale, "A" award, etc.

/// <summary>
/// Per-user Finnhub insider-transactions feed. Way easier than parsing SEC Form 4 XML — Finnhub
/// pre-parses everything (insider name, share count, price, buy/sell code) into clean JSON.
/// Same Finnhub key + quota the user already uses for news.
///
/// Returns transactions reduced to news-like "Article" headlines so the rest of the trading
/// loop (LLM sentiment + risk + order) doesn't need to know they came from a different endpoint.
/// </summary>
public sealed class UserInsiderFeed
{
    private readonly HttpClient _http;
    private readonly string _finnhubKey;
    private readonly ILogger _log;

    public UserInsiderFeed(HttpClient http, string finnhubKey, ILogger log)
    { _http = http; _finnhubKey = finnhubKey; _log = log; }

    public async Task<IReadOnlyList<InsiderTxn>> GetAsync(string ticker, DateTimeOffset since, CancellationToken ct)
    {
        var to = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var from = since.UtcDateTime.Date.AddDays(-1).ToString("yyyy-MM-dd");
        var url = $"stock/insider-transactions?symbol={Uri.EscapeDataString(ticker)}&from={from}&to={to}&token={Uri.EscapeDataString(_finnhubKey)}";

        Response? body;
        try { body = await _http.GetFromJsonAsync<Response>(url, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Insider feed for {Ticker} failed", ticker); return Array.Empty<InsiderTxn>(); }

        var rows = body?.Data ?? Array.Empty<Row>();
        var result = new List<InsiderTxn>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.TransactionDate)) continue;
            if (!DateTime.TryParse(r.TransactionDate, out var d)) continue;
            var at = new DateTimeOffset(d, TimeSpan.Zero);
            if (at <= since) continue;

            // Build a stable ID — Finnhub doesn't give one, so combine fields.
            var id = $"finsider:{ticker}:{r.TransactionDate}:{r.Name}:{r.Change}:{r.TransactionPrice}";
            result.Add(new InsiderTxn(
                Id: id,
                Ticker: ticker.ToUpperInvariant(),
                Name: r.Name ?? "(unknown)",
                Share: r.Change,
                Price: r.TransactionPrice ?? 0m,
                At: at,
                Code: r.TransactionCode ?? "?"));
        }
        return result;
    }

    /// <summary>Turn a transaction into a human-readable headline + summary.</summary>
    public static (string headline, string summary) FormatHeadline(InsiderTxn t)
    {
        var dir = t.Share > 0 ? "BOUGHT" : "SOLD";
        var qty = Math.Abs(t.Share).ToString("N0");
        var notional = (decimal)Math.Abs(t.Share) * t.Price;
        var code = t.Code switch
        {
            "P" => "open-market purchase",
            "S" => "open-market sale",
            "A" => "stock award/grant",
            "D" => "disposition to issuer",
            "M" => "exercise of options",
            "F" => "shares withheld for tax",
            "G" => "gift",
            _ => $"code {t.Code}"
        };
        var headline = $"[Insider] {t.Name} {dir} {qty} shares of {t.Ticker} @ ${t.Price:F2}";
        var summary = $"Form 4 insider transaction: {t.Name} {dir} {qty} shares ({code}) at ${t.Price:F2}, " +
                      $"total notional ≈ ${notional:N0}. Open-market purchases by officers/directors are typically " +
                      $"considered a bullish conviction signal; routine sales (especially planned 10b5-1 sales) less so.";
        return (headline, summary);
    }

    private sealed class Response
    {
        [JsonPropertyName("data")] public Row[]? Data { get; set; }
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    }
    private sealed class Row
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("share")] public long Share { get; set; }
        [JsonPropertyName("change")] public long Change { get; set; }
        [JsonPropertyName("filingDate")] public string? FilingDate { get; set; }
        [JsonPropertyName("transactionDate")] public string? TransactionDate { get; set; }
        [JsonPropertyName("transactionPrice")] public decimal? TransactionPrice { get; set; }
        [JsonPropertyName("transactionCode")] public string? TransactionCode { get; set; }
    }
}
