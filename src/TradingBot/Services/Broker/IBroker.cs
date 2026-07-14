using TradingBot.Models;

namespace TradingBot.Services.Broker;

public sealed record AccountSnapshot(
    decimal Equity,
    decimal Cash,
    decimal BuyingPower,
    decimal LastEquityAtSessionOpen);

public sealed record PositionSnapshot(
    string Ticker,
    long Quantity,
    decimal AverageEntryPrice,
    decimal MarketValue,
    decimal UnrealizedPnL);

public sealed record OrderSnapshot(
    string OrderId,
    string Ticker,
    string Side,                  // "Buy" | "Sell"
    long RequestedQuantity,
    long FilledQuantity,
    decimal? FilledAvgPrice,
    string Status,                // "new"|"accepted"|"pending_new"|"filled"|"partially_filled"|"canceled"|"rejected"|"expired"|...
    DateTimeOffset SubmittedAt,
    DateTimeOffset? FilledAt);

public interface IBroker
{
    Task<AccountSnapshot> GetAccountAsync(CancellationToken ct);

    /// <summary>Returns null if there is no open position for the ticker.</summary>
    Task<PositionSnapshot?> GetPositionAsync(string ticker, CancellationToken ct);

    /// <summary>Returns all currently-open positions in a single round trip.</summary>
    Task<IReadOnlyList<PositionSnapshot>> ListPositionsAsync(CancellationToken ct);

    /// <summary>Returns the most recent N orders across all statuses (newest first).</summary>
    Task<IReadOnlyList<OrderSnapshot>> ListRecentOrdersAsync(int limit, CancellationToken ct);

    /// <summary>Returns true if the market is currently open for regular-hours trading.</summary>
    Task<bool> IsMarketOpenAsync(CancellationToken ct);

    /// <summary>Returns the latest trade price for sizing. Null if unavailable.</summary>
    Task<decimal?> GetLatestPriceAsync(string ticker, CancellationToken ct);

    /// <summary>
    /// Submit a day-only market order. Returns the broker order id on success, or null on failure
    /// (the implementation already logs the failure).
    /// </summary>
    Task<string?> SubmitMarketOrderAsync(string ticker, TradeSide side, long quantity, CancellationToken ct);
}
