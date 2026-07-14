namespace TradingBot.Web.Data;

/// <summary>
/// Persistent record of every time a ticker was added to the dynamic watchlist — by buzz
/// discovery (Finnhub firehose + mentions threshold) or by Grok trending. Captures the price
/// at the moment of promotion so you can compare against the eventual buy / fill price and
/// answer "did discovery work? did the price move after we noticed?".
///
/// One row per NEW promotion. Re-promotions (extending TTL on a ticker that was already on
/// the watchlist) do NOT create a new row.
/// </summary>
public sealed class UserWatchlistEvent
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }

    public string Ticker { get; set; } = "";
    /// <summary>"Buzz" (Finnhub firehose mentions) or "Grok" (X-trending poll).</summary>
    public string Source { get; set; } = "";
    public int BuzzScore { get; set; }
    public string? Reason { get; set; }

    /// <summary>Market price at the moment of promotion. Null if the price fetch failed.</summary>
    public decimal? PriceUsd { get; set; }
}
