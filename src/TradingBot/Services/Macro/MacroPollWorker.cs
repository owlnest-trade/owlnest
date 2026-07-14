using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Macro;

/// <summary>
/// Independent background poller for Polymarket. Runs on its own cadence (default every 10 min)
/// so it's completely decoupled from the per-tick trading loop. If the macro pipeline is wedged
/// or misconfigured, trading is unaffected — and vice versa.
/// </summary>
public sealed class MacroPollWorker : BackgroundService
{
    private readonly MacroOptions _opts;
    private readonly IMacroProvider _provider;
    private readonly MacroStore _store;
    private readonly ILogger<MacroPollWorker> _log;

    public MacroPollWorker(
        IOptions<MacroOptions> opts,
        IMacroProvider provider,
        MacroStore store,
        ILogger<MacroPollWorker> log)
    {
        _opts = opts.Value;
        _provider = provider;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("MacroPollWorker disabled by config — exiting.");
            return;
        }

        _log.LogInformation("MacroPollWorker starting (source={Source}, poll every {Sec}s)",
            _provider.SourceName, _opts.PollIntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(60, _opts.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _provider.FetchAsync(stoppingToken);
                _store.Replace(snapshot);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MacroPollWorker tick failed; will retry next interval");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
