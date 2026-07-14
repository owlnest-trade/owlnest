using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

public sealed record WatchlistEntry(string Ticker, DateTimeOffset PromotedAt, DateTimeOffset ExpiresAt, int BuzzAtPromotion);

/// <summary>
/// Time-bounded set of tickers the bot should additionally scan beyond the fixed universe.
/// Tickers enter via Promote(), expire automatically after TtlHours, and the size is capped.
/// Re-promoting an existing ticker extends its TTL (so persistent buzz keeps it on the list).
/// </summary>
public sealed class WatchlistManager
{
    private readonly DiscoveryOptions _opts;
    private readonly ILogger<WatchlistManager> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchlistEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public WatchlistManager(IOptions<DiscoveryOptions> opts, ILogger<WatchlistManager> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Try to add (or extend) tickers from buzz results. Tickers already in <paramref name="excludeTickers"/>
    /// (typically the fixed universe) are skipped so we don't double-track them.
    /// </summary>
    public void PromoteMany(IEnumerable<(string Ticker, int Score)> buzzy, IReadOnlyCollection<string> excludeTickers)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(_opts.WatchlistTtlHours);
        var excluded = new HashSet<string>(excludeTickers, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var (ticker, score) in buzzy)
            {
                if (excluded.Contains(ticker)) continue;

                if (_entries.TryGetValue(ticker, out var existing))
                {
                    // Extend TTL on continued buzz; keep the original PromotedAt.
                    _entries[ticker] = existing with { ExpiresAt = expires, BuzzAtPromotion = Math.Max(existing.BuzzAtPromotion, score) };
                    continue;
                }

                if (_entries.Count >= _opts.MaxWatchlistSize)
                {
                    // At cap — only displace the lowest-score entry if this one is stronger.
                    var weakest = _entries.Values.OrderBy(e => e.BuzzAtPromotion).First();
                    if (score <= weakest.BuzzAtPromotion) continue;
                    _entries.Remove(weakest.Ticker);
                    _log.LogInformation("Watchlist full — dropping {Old} (score {OldScore}) for {New} (score {NewScore})",
                        weakest.Ticker, weakest.BuzzAtPromotion, ticker, score);
                }

                _entries[ticker] = new WatchlistEntry(ticker, now, expires, score);
                _log.LogInformation("Promoted {Ticker} to dynamic watchlist (buzz={Score}, expires {Expires:u})",
                    ticker, score, expires);
            }
        }
    }

    /// <summary>Drop entries whose TTL has elapsed.</summary>
    public void Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            var stale = _entries.Values.Where(e => e.ExpiresAt <= now).ToList();
            foreach (var e in stale)
            {
                _entries.Remove(e.Ticker);
                _log.LogInformation("Watchlist expired {Ticker} (was promoted {PromoAgo:hh\\:mm} ago)",
                    e.Ticker, now - e.PromotedAt);
            }
        }
    }

    /// <summary>Current dynamic watchlist tickers.</summary>
    public IReadOnlyList<string> ActiveTickers()
    {
        lock (_gate) { return _entries.Keys.ToList(); }
    }

    /// <summary>Full entries (for the dashboard).</summary>
    public IReadOnlyList<WatchlistEntry> ActiveEntries()
    {
        lock (_gate) { return _entries.Values.OrderByDescending(e => e.BuzzAtPromotion).ToList(); }
    }
}
