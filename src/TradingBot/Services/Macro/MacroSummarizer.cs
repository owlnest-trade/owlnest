using System.Globalization;
using System.Text;

namespace TradingBot.Services.Macro;

/// <summary>
/// Converts the current MacroSnapshot into a compact text block that can be prepended to
/// Claude prompts. Kept short to minimise per-call input tokens — top-N markets only, one
/// line each, no fluff. Returns null when the snapshot is empty or stale.
/// </summary>
public static class MacroSummarizer
{
    /// <summary>Build a preamble string. Returns null if the snapshot has no usable data.</summary>
    public static string? BuildPreamble(MacroSnapshot snapshot, int topN = 10, TimeSpan? maxAge = null)
    {
        if (snapshot.Markets is null || snapshot.Markets.Count == 0) return null;

        // Don't pass stale macro to Claude — if it hasn't refreshed in a while, omit entirely.
        var staleAfter = maxAge ?? TimeSpan.FromHours(2);
        if (snapshot.At == DateTimeOffset.MinValue) return null;
        if (DateTimeOffset.UtcNow - snapshot.At > staleAfter) return null;

        var sb = new StringBuilder(512);
        sb.Append("Macro context (prediction-market odds, updated ");
        sb.Append(FormatAge(DateTimeOffset.UtcNow - snapshot.At));
        sb.Append(" ago):\n");

        var picks = snapshot.Markets.Take(topN).ToList();
        foreach (var m in picks)
        {
            var pct = (m.YesPrice * 100).ToString("0", CultureInfo.InvariantCulture);
            var question = m.Question.Length > 110 ? m.Question.Substring(0, 110) + "…" : m.Question;
            sb.Append("- ");
            sb.Append(pct);
            sb.Append("% — ");
            sb.Append(question);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Short rendering used on the dashboard decision feed.</summary>
    public static string? BuildShortSummary(MacroSnapshot snapshot, int topN = 4)
    {
        if (snapshot.Markets is null || snapshot.Markets.Count == 0) return null;
        if (snapshot.At == DateTimeOffset.MinValue) return null;

        var parts = snapshot.Markets.Take(topN).Select(m =>
        {
            var pct = (m.YesPrice * 100).ToString("0", CultureInfo.InvariantCulture);
            // Extract a 2-3 word key phrase from the question — naive but good enough.
            var key = ShortKey(m.Question);
            return $"{key} {pct}%";
        });
        return string.Join(" · ", parts);
    }

    private static string ShortKey(string question)
    {
        // Look for common patterns first.
        var lower = question.ToLowerInvariant();
        if (lower.Contains("fed") || lower.Contains("fomc")) return "Fed";
        if (lower.Contains("recession")) return "Recession";
        if (lower.Contains("cpi") || lower.Contains("inflation")) return "Inflation";
        if (lower.Contains("iran")) return "Iran";
        if (lower.Contains("israel")) return "Israel";
        if (lower.Contains("russia") || lower.Contains("ukraine")) return "Russia/UA";
        if (lower.Contains("china") || lower.Contains("taiwan")) return "China/TW";
        if (lower.Contains("bitcoin") || lower.Contains("btc")) return "BTC";
        if (lower.Contains("oil") || lower.Contains("opec")) return "Oil";
        if (lower.Contains("gold")) return "Gold";
        if (lower.Contains("election") || lower.Contains("trump")) return "Politics";
        // Fallback: first 3 words.
        var words = question.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3);
        return string.Join(" ", words);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }
}
