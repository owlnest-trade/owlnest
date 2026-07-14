using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Rolling time-windowed mention counter. The worker feeds market-wide news articles in;
/// the worker (and dashboard) read back which tickers have crossed the buzz threshold.
/// </summary>
public sealed class BuzzTracker
{
    private readonly DiscoveryOptions _opts;
    private readonly object _gate = new();

    // ticker -> list of mention timestamps (only ones inside the window after Prune)
    private readonly Dictionary<string, List<DateTimeOffset>> _mentions =
        new(StringComparer.OrdinalIgnoreCase);

    // Article IDs we've already counted, to avoid double-counting if the same item is fetched twice.
    private readonly HashSet<string> _ingestedIds = new(StringComparer.Ordinal);

    public BuzzTracker(IOptions<DiscoveryOptions> opts)
    {
        _opts = opts.Value;
    }

    /// <summary>Add an article's ticker mentions to the rolling window.</summary>
    public void Ingest(MarketNewsItem item)
    {
        // Timestamp by when we OBSERVE the mention, not by article PublishedAt. The window then
        // measures "discovery activity in the last N minutes" which is what we want — an article
        // published yesterday but still being recirculated today is still fresh buzz to us.
        var observedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_ingestedIds.Add(item.Id)) return;
            foreach (var t in item.Tickers)
            {
                if (!_mentions.TryGetValue(t, out var list))
                {
                    list = new List<DateTimeOffset>(8);
                    _mentions[t] = list;
                }
                list.Add(observedAt);
            }
        }
    }

    /// <summary>Drop mentions older than the configured window.</summary>
    public void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-_opts.BuzzWindowMinutes);
        lock (_gate)
        {
            // Walk a snapshot of keys so we can remove empty buckets safely.
            foreach (var key in _mentions.Keys.ToList())
            {
                var list = _mentions[key];
                list.RemoveAll(ts => ts < cutoff);
                if (list.Count == 0) _mentions.Remove(key);
            }

            // Don't let the ingested-IDs set grow unbounded — Finnhub IDs are stable so capping is fine.
            if (_ingestedIds.Count > 5000)
            {
                // Cheap heuristic: drop the whole set. Worst case is a few re-counts the next cycle.
                _ingestedIds.Clear();
            }
        }
    }

    /// <summary>
    /// Returns tickers whose mention count (within the window) meets or exceeds the threshold,
    /// ordered by buzz desc.
    /// </summary>
    public IReadOnlyList<(string Ticker, int Score)> GetBuzzyTickers()
    {
        lock (_gate)
        {
            return _mentions
                .Where(kv => kv.Value.Count >= _opts.BuzzThreshold)
                .Select(kv => (kv.Key, kv.Value.Count))
                .OrderByDescending(x => x.Count)
                .ToList();
        }
    }

    /// <summary>Read-only view of the entire buzz map (for the dashboard). Top N by mention count.</summary>
    public IReadOnlyList<(string Ticker, int Score)> Snapshot(int top)
    {
        lock (_gate)
        {
            return _mentions
                .Select(kv => (kv.Key, kv.Value.Count))
                .OrderByDescending(x => x.Count)
                .Take(top)
                .ToList();
        }
    }
}
