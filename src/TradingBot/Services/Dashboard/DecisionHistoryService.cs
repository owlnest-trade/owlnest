using System.Collections.Concurrent;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Dashboard;

/// <summary>
/// In-memory store of recent activity, read by the web dashboard. Two things:
///   - A ring buffer of the last N evaluated news items + their outcomes.
///   - Latest snapshots (account, positions, tick timing) so the header card stays fresh.
///
/// Thread-safe: many writes from the worker, many concurrent reads from HTTP requests.
/// </summary>
public sealed class DecisionHistoryService
{
    private const int Capacity = 500;

    private readonly LinkedList<DecisionRecord> _ring = new();
    private readonly object _ringGate = new();

    private AccountSnapshot? _latestAccount;
    private IReadOnlyList<PositionSnapshot> _latestPositions = Array.Empty<PositionSnapshot>();
    private DateTimeOffset? _lastTickAt;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _marketOpen;
    private int _tradesToday;

    public void Record(DecisionRecord r)
    {
        lock (_ringGate)
        {
            _ring.AddFirst(r);
            while (_ring.Count > Capacity) _ring.RemoveLast();
        }
    }

    public void RecordTick(AccountSnapshot account, bool marketOpen, int tradesToday)
    {
        _latestAccount = account;
        _marketOpen = marketOpen;
        _tradesToday = tradesToday;
        _lastTickAt = DateTimeOffset.UtcNow;
    }

    public void RecordPositions(IReadOnlyList<PositionSnapshot> positions)
    {
        _latestPositions = positions;
    }

    public IReadOnlyList<DecisionRecord> Recent(int limit)
    {
        lock (_ringGate)
        {
            return _ring.Take(limit).ToList();
        }
    }

    public AccountSnapshot? LatestAccount => _latestAccount;
    public IReadOnlyList<PositionSnapshot> LatestPositions => _latestPositions;
    public DateTimeOffset? LastTickAt => _lastTickAt;
    public DateTimeOffset StartedAt => _startedAt;
    public bool MarketOpen => _marketOpen;
    public int TradesToday => _tradesToday;
}
