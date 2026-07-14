namespace TradingBot.Models;

public enum Sentiment
{
    Bearish = -1,
    Neutral = 0,
    Bullish = 1
}

/// <summary>
/// Claude's structured verdict on a single news item.
/// </summary>
public sealed record SentimentResult(
    string Ticker,
    Sentiment Sentiment,
    double Confidence,
    bool IsActionable,
    string Reasoning,
    string Model,
    DateTimeOffset EvaluatedAt);
