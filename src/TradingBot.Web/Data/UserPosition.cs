namespace TradingBot.Web.Data;

/// <summary>Snapshot of one open position. Refreshed each tick from Alpaca.</summary>
public sealed class UserPosition
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public long Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal MarketValue { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public decimal PeakPrice { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
