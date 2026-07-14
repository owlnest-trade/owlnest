namespace TradingBot.Services.Macro;

/// <summary>
/// Singleton holding the latest Polymarket macro snapshot. Atomic swap on update so HTTP reads
/// never observe a partially-updated list.
/// </summary>
public sealed class MacroStore
{
    private MacroSnapshot _latest = new(DateTimeOffset.MinValue, Array.Empty<MacroMarket>());

    public MacroSnapshot Latest => _latest;

    public void Replace(MacroSnapshot snapshot)
    {
        // Reference assignment is atomic on all .NET-supported architectures — no lock needed.
        _latest = snapshot;
    }
}
