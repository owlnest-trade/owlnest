namespace TradingBot.Web.Data;

/// <summary>One submitted order — buy or sell — recorded the moment the bot sent it to Alpaca.</summary>
public sealed class UserOrder
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string OrderId { get; set; } = "";          // Alpaca order id
    public string Ticker { get; set; } = "";
    public string Side { get; set; } = "";              // "Buy" | "Sell"
    public long Quantity { get; set; }
    public string Status { get; set; } = "new";         // alpaca order status lowercased
    public decimal? FilledAvgPrice { get; set; }
    public long FilledQuantity { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public DateTimeOffset? FilledAtUtc { get; set; }
    public string Reason { get; set; } = "";            // why the bot placed it (entry sentiment, exit trigger, etc.)

    /// <summary>
    /// Market price at the moment the bot decided to submit this order — the value we used for
    /// sizing the qty/notional. Compare against <see cref="FilledAvgPrice"/> to see slippage.
    /// </summary>
    public decimal? PriceAtSubmitUsd { get; set; }
}
