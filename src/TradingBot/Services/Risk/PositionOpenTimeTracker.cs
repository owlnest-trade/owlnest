using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Risk;

/// <summary>
/// Persists "when did I open each position" across bot restarts so the time-based exit can fire
/// correctly. Backed by a small JSON file. For positions that already existed before this
/// feature was introduced, we conservatively stamp them as "opened now" on first sight —
/// time-based exits start counting from then, not from the actual original fill.
/// </summary>
public sealed class PositionOpenTimeTracker
{
    private readonly string _filePath;
    private readonly ILogger<PositionOpenTimeTracker> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _openTimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _flushGate = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public PositionOpenTimeTracker(IOptions<ExitOptions> opts, ILogger<PositionOpenTimeTracker> log)
    {
        _log = log;
        _filePath = opts.Value.OpenTimesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        Load();
    }

    /// <summary>Returns the opened-at time for a ticker, or null if not tracked.</summary>
    public DateTimeOffset? Get(string ticker) =>
        _openTimes.TryGetValue(ticker, out var t) ? t : null;

    /// <summary>Record a fresh open. Called by the worker when a buy is successfully submitted.</summary>
    public void RecordOpen(string ticker, DateTimeOffset at)
    {
        _openTimes[ticker] = at;
        Flush();
    }

    /// <summary>Drop a ticker when its position has been fully closed (so a re-open later starts fresh).</summary>
    public void Forget(string ticker)
    {
        if (_openTimes.TryRemove(ticker, out _)) Flush();
    }

    /// <summary>
    /// Walk current positions:
    ///   - any held ticker without an open-time gets stamped "now" (conservative);
    ///   - any tracked ticker no longer in positions gets dropped.
    /// Call once per tick after fetching positions.
    /// </summary>
    public void Reconcile(IReadOnlyList<PositionSnapshot> positions, DateTimeOffset now)
    {
        var held = new HashSet<string>(positions.Select(p => p.Ticker), StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (var p in positions)
        {
            if (!_openTimes.ContainsKey(p.Ticker))
            {
                _openTimes[p.Ticker] = now;
                _log.LogInformation("PositionOpenTimeTracker: first sight of {Ticker}, stamping open-time = now", p.Ticker);
                changed = true;
            }
        }

        foreach (var tracked in _openTimes.Keys.ToList())
        {
            if (!held.Contains(tracked))
            {
                _openTimes.TryRemove(tracked, out _);
                changed = true;
            }
        }

        if (changed) Flush();
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var (k, v) in loaded) _openTimes[k] = v;
            _log.LogInformation("Loaded {N} position open-times", _openTimes.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load position open-times from {Path} — starting empty", _filePath);
        }
    }

    private void Flush()
    {
        lock (_flushGate)
        {
            try
            {
                var snapshot = _openTimes.ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to flush position open-times");
            }
        }
    }
}
