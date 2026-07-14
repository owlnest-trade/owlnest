using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Risk;

/// <summary>
/// Per-position high-water mark of price since the position was opened. Used by the trailing
/// stop: peak only ratchets UP, never down. When the price drops by the configured trail
/// percentage below peak, the trailing stop fires.
/// </summary>
public sealed class PositionPeakTracker
{
    private readonly string _filePath;
    private readonly ILogger<PositionPeakTracker> _log;
    private readonly ConcurrentDictionary<string, decimal> _peaks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _flushGate = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public PositionPeakTracker(IOptions<ExitOptions> opts, ILogger<PositionPeakTracker> log)
    {
        _log = log;
        _filePath = opts.Value.PeaksFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        Load();
    }

    /// <summary>Highest price seen for a ticker since its position opened, or null if not tracked.</summary>
    public decimal? GetPeak(string ticker) =>
        _peaks.TryGetValue(ticker, out var p) ? p : null;

    /// <summary>Walk current positions: ratchet each peak UP if today's price exceeded it; drop closed tickers.</summary>
    public void UpdateFromPositions(IReadOnlyList<PositionSnapshot> positions)
    {
        var held = new HashSet<string>(positions.Select(p => p.Ticker), StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (var p in positions)
        {
            if (p.Quantity <= 0) continue;
            var price = p.MarketValue / p.Quantity;
            if (price <= 0m) continue;

            if (_peaks.TryGetValue(p.Ticker, out var existing))
            {
                if (price > existing)
                {
                    _peaks[p.Ticker] = price;
                    changed = true;
                }
            }
            else
            {
                // First sight — seed peak at MAX(currentPrice, avgEntry) so a position
                // already in profit doesn't fire the trail until it tops its current value.
                var seed = Math.Max(price, p.AverageEntryPrice);
                _peaks[p.Ticker] = seed;
                changed = true;
            }
        }

        // Drop peaks for tickers no longer held.
        foreach (var tracked in _peaks.Keys.ToList())
        {
            if (!held.Contains(tracked))
            {
                _peaks.TryRemove(tracked, out _);
                changed = true;
            }
        }

        if (changed) Flush();
    }

    /// <summary>Force-drop a ticker's peak (e.g. after we close the position ourselves).</summary>
    public void Forget(string ticker)
    {
        if (_peaks.TryRemove(ticker, out _)) Flush();
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var (k, v) in loaded) _peaks[k] = v;
            _log.LogInformation("Loaded {N} position peaks", _peaks.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load position peaks from {Path} — starting empty", _filePath);
        }
    }

    private void Flush()
    {
        lock (_flushGate)
        {
            try
            {
                var snapshot = _peaks.ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to flush position peaks");
            }
        }
    }
}
