namespace KieshStockExchange.ViewModels.AdminViewModels;

/// <summary>
/// §dashboard: one immutable display row of the bot-types breakdown table. Rebuilt each refresh (a handful of
/// rows, so no in-place mutation needed). Snapshot columns (bots / win% / P&amp;L%) are "now" state; the trades /
/// per-bot / volume columns reflect the selected flow range (or the session totals for the "All" range).
/// </summary>
public sealed class StrategyBreakdownRowVm
{
    public int Strategy { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    // Composition bar: 0..1 relative to the largest cohort, so the biggest strategy fills the bar.
    public double ShareFraction { get; init; }
    public string ShareText { get; init; } = "—";     // absolute % of the fleet, e.g. "34%"
    public string BotCountText { get; init; } = "—";

    public string WinRateText { get; init; } = "—";    // fraction of the cohort above its seed baseline
    public string PnlText { get; init; } = "—";        // cohort portfolio Δ vs seed

    public string TradesText { get; init; } = "—";     // range trades, or session trades for "All"
    public string PerBotText { get; init; } = "—";     // trades ÷ bots
    public string VolumeText { get; init; } = "—";     // range volume (blank for the "All" range)
}
