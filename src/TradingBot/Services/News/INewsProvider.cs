using TradingBot.Models;

namespace TradingBot.Services.News;

public interface INewsProvider
{
    /// <summary>
    /// Returns news items for the given ticker published after <paramref name="since"/>.
    /// Implementations are responsible for filtering / deduping on the wire side where they can.
    /// </summary>
    Task<IReadOnlyList<NewsItem>> GetRecentNewsAsync(
        string ticker,
        DateTimeOffset since,
        CancellationToken ct);
}
