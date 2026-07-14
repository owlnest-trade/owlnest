using Alpaca.Markets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.Broker;

public sealed class AlpacaBroker : IBroker, IAsyncDisposable
{
    private readonly IAlpacaTradingClient _trading;
    private readonly IAlpacaDataClient _data;
    private readonly ILogger<AlpacaBroker> _log;
    private readonly bool _paper;

    public AlpacaBroker(IOptions<AlpacaOptions> opts, ILogger<AlpacaBroker> log)
    {
        var o = opts.Value;
        if (string.IsNullOrWhiteSpace(o.KeyId) || string.IsNullOrWhiteSpace(o.SecretKey))
        {
            throw new InvalidOperationException(
                "Alpaca credentials are missing. Run `dotnet user-secrets set Alpaca:KeyId <key>` and `Alpaca:SecretKey <secret>`.");
        }

        _log = log;
        _paper = o.UsePaperTrading;
        var creds = new SecretKey(o.KeyId, o.SecretKey);
        var env = _paper ? Alpaca.Markets.Environments.Paper : Alpaca.Markets.Environments.Live;

        _trading = env.GetAlpacaTradingClient(creds);
        _data = env.GetAlpacaDataClient(creds);

        _log.LogInformation("AlpacaBroker initialized ({Mode})", _paper ? "PAPER" : "LIVE");
    }

    public async Task<AccountSnapshot> GetAccountAsync(CancellationToken ct)
    {
        var a = await _trading.GetAccountAsync(ct);
        return new AccountSnapshot(
            Equity: a.Equity ?? 0m,
            Cash: a.TradableCash,
            BuyingPower: a.BuyingPower ?? 0m,
            LastEquityAtSessionOpen: a.LastEquity);
    }

    public async Task<PositionSnapshot?> GetPositionAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var p = await _trading.GetPositionAsync(ticker, ct);
            return new PositionSnapshot(
                Ticker: p.Symbol,
                Quantity: (long)p.IntegerQuantity,
                AverageEntryPrice: p.AverageEntryPrice,
                MarketValue: p.MarketValue ?? 0m,
                UnrealizedPnL: p.UnrealizedProfitLoss ?? 0m);
        }
        catch (RestClientErrorException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PositionSnapshot>> ListPositionsAsync(CancellationToken ct)
    {
        try
        {
            var positions = await _trading.ListPositionsAsync(ct);
            return positions.Select(p => new PositionSnapshot(
                Ticker: p.Symbol,
                Quantity: (long)p.IntegerQuantity,
                AverageEntryPrice: p.AverageEntryPrice,
                MarketValue: p.MarketValue ?? 0m,
                UnrealizedPnL: p.UnrealizedProfitLoss ?? 0m)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListPositionsAsync failed");
            return Array.Empty<PositionSnapshot>();
        }
    }

    public async Task<IReadOnlyList<OrderSnapshot>> ListRecentOrdersAsync(int limit, CancellationToken ct)
    {
        try
        {
            // Pull all-status orders sorted desc by submission time.
            var req = new ListOrdersRequest
            {
                OrderStatusFilter = OrderStatusFilter.All,
                LimitOrderNumber = Math.Max(1, Math.Min(limit, 200)),
                OrderListSorting = SortDirection.Descending,
            };
            var orders = await _trading.ListOrdersAsync(req, ct);
            return orders.Select(o => new OrderSnapshot(
                OrderId: o.OrderId.ToString(),
                Ticker: o.Symbol,
                Side: o.OrderSide == OrderSide.Buy ? "Buy" : "Sell",
                RequestedQuantity: (long)(o.IntegerQuantity),
                FilledQuantity: (long)(o.IntegerFilledQuantity),
                FilledAvgPrice: o.AverageFillPrice,
                Status: o.OrderStatus.ToString().ToLowerInvariant(),
                SubmittedAt: o.SubmittedAtUtc ?? o.CreatedAtUtc ?? DateTime.UtcNow,
                FilledAt: o.FilledAtUtc)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListRecentOrdersAsync failed");
            return Array.Empty<OrderSnapshot>();
        }
    }

    public async Task<bool> IsMarketOpenAsync(CancellationToken ct)
    {
        var clock = await _trading.GetClockAsync(ct);
        return clock.IsOpen;
    }

    public async Task<decimal?> GetLatestPriceAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var trade = await _data.GetLatestTradeAsync(new LatestMarketDataRequest(ticker), ct);
            return trade.Price;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetLatestPriceAsync failed for {Ticker}", ticker);
            return null;
        }
    }

    public async Task<string?> SubmitMarketOrderAsync(string ticker, TradeSide side, long quantity, CancellationToken ct)
    {
        if (quantity <= 0)
        {
            _log.LogWarning("Refusing to submit zero/negative qty order for {Ticker}", ticker);
            return null;
        }

        try
        {
            var order = side == TradeSide.Buy
                ? MarketOrder.Buy(ticker, quantity)
                : MarketOrder.Sell(ticker, quantity);
            var submitted = await _trading.PostOrderAsync(order.WithDuration(TimeInForce.Day), ct);
            _log.LogInformation("Submitted {Side} {Qty} {Ticker} (order {OrderId})",
                side, quantity, ticker, submitted.OrderId);
            return submitted.OrderId.ToString();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PostOrderAsync failed: {Side} {Qty} {Ticker}", side, quantity, ticker);
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _trading.Dispose();
        _data.Dispose();
        return ValueTask.CompletedTask;
    }
}
