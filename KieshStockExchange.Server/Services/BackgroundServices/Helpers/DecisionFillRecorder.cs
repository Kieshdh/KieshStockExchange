using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Shared per-bot fill-telemetry recorder for the decision services (Conviction / Rotator / Arbitrage):
/// stamps each fill transaction onto the owning <see cref="AIUser"/> via <see cref="AIUser.RecordTrade"/>.
/// Pure telemetry — no Fund/Position/reservation mutation, no RNG. Distinct from
/// <c>BotActivityService.RecordFill</c> (the activity-clustering signal).
/// </summary>
internal static class DecisionFillRecorder
{
    internal static void RecordFills(AIUser user, OrderResult result)
    {
        if (result.FillTransactions.Count == 0) return;
        for (int i = 0; i < result.FillTransactions.Count; i++)
            user.RecordTrade(result.FillTransactions[i]);
    }
}
