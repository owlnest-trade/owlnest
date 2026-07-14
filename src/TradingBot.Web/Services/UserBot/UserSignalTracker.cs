namespace TradingBot.Web.Services.UserBot;

/// <summary>
/// Per-user rolling window of cleared signals per (ticker, direction). Used by the confirmation
/// gate: require N same-direction signals within the window before approving a buy. Filters out
/// one-off noise headlines that briefly look bullish but don't reflect a real catalyst.
/// </summary>
public sealed class UserSignalTracker
{
    private readonly int _windowMinutes;
    private readonly object _gate = new();
    private readonly Dictionary<(string Ticker, string Direction), List<DateTimeOffset>> _signals = new();

    public UserSignalTracker(int windowMinutes) { _windowMinutes = Math.Max(1, windowMinutes); }

    /// <summary>Record a fresh signal and return the count in-window (including this one).</summary>
    public int RecordAndCount(string ticker, string direction, DateTimeOffset now)
    {
        var key = (ticker.ToUpperInvariant(), direction.ToLowerInvariant());
        var cutoff = now.AddMinutes(-_windowMinutes);
        lock (_gate)
        {
            if (!_signals.TryGetValue(key, out var list))
            {
                list = new List<DateTimeOffset>(4);
                _signals[key] = list;
            }
            list.RemoveAll(t => t < cutoff);
            list.Add(now);
            return list.Count;
        }
    }
}
