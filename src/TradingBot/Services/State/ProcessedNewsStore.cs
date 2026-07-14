using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.State;

/// <summary>
/// Tracks which news item IDs we've already evaluated, so that a restart (or a duplicate fetch
/// from the news API) doesn't make us re-spend Claude tokens or re-trade on the same headline.
///
/// Backed by a small JSON file. Entries older than the retention window are pruned on load.
/// </summary>
public sealed class ProcessedNewsStore
{
    private const int RetentionDays = 7;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly string _filePath;
    private readonly ILogger<ProcessedNewsStore> _log;
    private readonly object _flushGate = new();

    public ProcessedNewsStore(IOptions<TradingOptions> opts, ILogger<ProcessedNewsStore> log)
    {
        _log = log;
        var dir = opts.Value.StateDirectory;
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "processed-news.json");
        Load();
    }

    public bool TryMarkProcessed(string newsId)
    {
        // Returns true if this is a new ID (caller should proceed). False if we've already seen it.
        var added = _seen.TryAdd(newsId, DateTimeOffset.UtcNow);
        if (added) Flush();
        return added;
    }

    public bool HasSeen(string newsId) => _seen.ContainsKey(newsId);

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(json, JsonOpts);
            if (loaded is null) return;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            foreach (var (id, at) in loaded)
            {
                if (at >= cutoff) _seen[id] = at;
            }
            _log.LogInformation("Loaded {Count} processed-news IDs (after {Days}-day prune)", _seen.Count, RetentionDays);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load processed-news store from {Path} — starting empty", _filePath);
        }
    }

    private void Flush()
    {
        lock (_flushGate)
        {
            try
            {
                var snapshot = _seen.ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to flush processed-news store");
            }
        }
    }
}
