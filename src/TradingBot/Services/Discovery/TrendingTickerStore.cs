namespace TradingBot.Services.Discovery;

public sealed record TrendingTicker(string Ticker, string Reason, string Sentiment);

public sealed record TrendingSnapshot(DateTimeOffset At, IReadOnlyList<TrendingTicker> Tickers);

/// <summary>Singleton snapshot of the latest Grok trending list. Atomic reference swap on update.</summary>
public sealed class TrendingTickerStore
{
    private TrendingSnapshot _latest = new(DateTimeOffset.MinValue, Array.Empty<TrendingTicker>());

    public TrendingSnapshot Latest => _latest;

    public void Replace(TrendingSnapshot snap) => _latest = snap;
}
