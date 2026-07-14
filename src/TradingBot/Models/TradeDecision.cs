namespace TradingBot.Models;

public enum TradeSide
{
    Buy,
    Sell
}

/// <summary>
/// What the bot has decided to do after combining sentiment with risk gates.
/// Approved=false means we logged a decision but did NOT submit an order; Reason explains why.
/// </summary>
public sealed record TradeDecision(
    string Ticker,
    TradeSide Side,
    int Quantity,
    bool Approved,
    string Reason,
    SentimentResult Sentiment,
    DateTimeOffset DecidedAt);
