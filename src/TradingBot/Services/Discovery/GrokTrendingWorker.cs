using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Independent background poller for Grok's X/news trending list. Pushes returned tickers
/// directly into WatchlistManager so the main trading loop scans them like any other
/// dynamic-watchlist candidate (news → sentiment → risk → trade).
/// </summary>
public sealed class GrokTrendingWorker : BackgroundService
{
    private readonly GrokOptions _opts;
    private readonly TradingOptions _trading;
    private readonly GrokTrendingProvider _provider;
    private readonly TrendingTickerStore _store;
    private readonly WatchlistManager _watchlist;
    private readonly ILogger<GrokTrendingWorker> _log;

    public GrokTrendingWorker(
        IOptions<GrokOptions> opts,
        IOptions<TradingOptions> trading,
        GrokTrendingProvider provider,
        TrendingTickerStore store,
        WatchlistManager watchlist,
        ILogger<GrokTrendingWorker> log)
    {
        _opts = opts.Value;
        _trading = trading.Value;
        _provider = provider;
        _store = store;
        _watchlist = watchlist;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("GrokTrendingWorker disabled by config — exiting.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
        {
            _log.LogWarning("GrokTrendingWorker enabled but Grok:ApiKey is missing — exiting. Set via dotnet user-secrets.");
            return;
        }

        _log.LogInformation("GrokTrendingWorker starting (model={Model}, poll every {Sec}s)",
            _opts.Model, _opts.PollIntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(60, _opts.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = await _provider.FetchAsync(stoppingToken);
                _store.Replace(snap);

                // Promote each trending ticker to the dynamic watchlist with a synthetic buzz score.
                // We pass score = 100 so they always win against news-driven buzz when the cap is full.
                if (snap.Tickers.Count > 0)
                {
                    var promoted = snap.Tickers.Select(t => (Ticker: t.Ticker, Score: 100));
                    _watchlist.PromoteMany(promoted, _trading.Universe);
                    _log.LogInformation("Grok promoted {N} trending tickers to watchlist", snap.Tickers.Count);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GrokTrendingWorker tick failed; will retry next interval");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
