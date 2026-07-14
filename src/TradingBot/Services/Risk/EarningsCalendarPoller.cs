using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Risk;

/// <summary>
/// Independent background poller for the earnings calendar. Refreshes every 12 hours over the
/// full universe + any currently-held positions (some held tickers may not be in the static
/// universe if they came in via discovery).
/// </summary>
public sealed class EarningsCalendarPoller : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);
    private readonly EarningsCalendar _calendar;
    private readonly IServiceScopeFactory _scopes;
    private readonly TradingOptions _trading;
    private readonly EntryOptions _entry;
    private readonly ILogger<EarningsCalendarPoller> _log;

    public EarningsCalendarPoller(
        EarningsCalendar calendar,
        IServiceScopeFactory scopes,
        IOptions<TradingOptions> trading,
        IOptions<EntryOptions> entry,
        ILogger<EarningsCalendarPoller> log)
    {
        _calendar = calendar;
        _scopes = scopes;
        _trading = trading.Value;
        _entry = entry.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_entry.EarningsBlackoutEnabled)
        {
            _log.LogInformation("EarningsCalendarPoller disabled (EarningsBlackoutEnabled = false) — exiting.");
            return;
        }

        _log.LogInformation("EarningsCalendarPoller starting (refresh every {Hours}h)", RefreshInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tickers = new HashSet<string>(_trading.Universe, StringComparer.OrdinalIgnoreCase);
                // Also include held positions in case Discovery picked them up.
                using (var scope = _scopes.CreateScope())
                {
                    var broker = scope.ServiceProvider.GetRequiredService<IBroker>();
                    var positions = await broker.ListPositionsAsync(stoppingToken);
                    foreach (var p in positions) tickers.Add(p.Ticker);
                }
                await _calendar.RefreshAsync(tickers, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "EarningsCalendarPoller refresh failed; will retry next interval");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
