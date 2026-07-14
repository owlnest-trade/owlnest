using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Risk;

/// <summary>
/// Rolling window of actionable (sentiment-cleared) signals per ticker + direction. The worker
/// records every signal that passes the basic Claude bar, then checks if there's a prior signal
/// in the same direction within the window. Requiring 2+ confirmations filters out one-off
/// noise headlines that briefly look bullish/bearish but don't reflect a real catalyst.
/// </summary>
public sealed class ActionableSignalTracker
{
    private readonly EntryOptions _opts;
    private readonly object _gate = new();

    // (ticker, direction) → list of observation times
    private readonly Dictionary<(string Ticker, string Direction), List<DateTimeOffset>> _signals =
        new();

    public ActionableSignalTracker(IOptions<EntryOptions> opts)
    {
        _opts = opts.Value;
    }

    /// <summary>
    /// Record a fresh actionable signal. Returns the total count (including this one) of
    /// matching signals currently in the window.
    /// </summary>
    public int RecordAndCount(string ticker, string direction, DateTimeOffset now)
    {
        var key = (ticker.ToUpperInvariant(), direction);
        var cutoff = now.AddMinutes(-_opts.ConfirmationWindowMinutes);

        lock (_gate)
        {
            if (!_signals.TryGetValue(key, out var list))
            {
                list = new List<DateTimeOffset>(4);
                _signals[key] = list;
            }
            // Prune anything outside the window, then add this one.
            list.RemoveAll(t => t < cutoff);
            list.Add(now);
            return list.Count;
        }
    }

    /// <summary>Count of in-window signals matching the (ticker, direction) without recording a new one.</summary>
    public int CountInWindow(string ticker, string direction, DateTimeOffset now)
    {
        var key = (ticker.ToUpperInvariant(), direction);
        var cutoff = now.AddMinutes(-_opts.ConfirmationWindowMinutes);

        lock (_gate)
        {
            return _signals.TryGetValue(key, out var list)
                ? list.Count(t => t >= cutoff)
                : 0;
        }
    }
}
