namespace TradingBot.Web.Data;

/// <summary>Hourly equity reading so the dashboard can draw a P&amp;L line chart.</summary>
public sealed class UserEquitySnapshot
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    public decimal Equity { get; set; }
    public decimal Cash { get; set; }
    public decimal BuyingPower { get; set; }
}
