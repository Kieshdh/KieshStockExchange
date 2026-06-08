using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// §3.6 P4 / §F5 / §P5b: bracket geometry rules, shared by <c>PlaceBracketAsync</c> (place) and
/// <c>ModifyBracketLegAsync</c> (per-leg edit) so the same invariants apply to both — no rule drift.
/// Returns <c>null</c> when valid, else an <see cref="OrderResult"/> with the validation error.
///
/// A bracket protects a position with an opposite-side stop-loss + take-profit legs, so the geometry
/// is mirrored by side:
/// - <b>Long</b> (buy entry → sell legs): stop-loss strictly <b>below</b> entry; take-profits strictly
///   <b>above</b> entry and strictly <b>ascending</b>.
/// - <b>Short</b> (sell entry → buy-to-close legs): stop-loss strictly <b>above</b> entry; take-profits
///   strictly <b>below</b> entry and strictly <b>descending</b>.
/// Both: every take-profit qty &gt; 0; Σ take-profit qty ≤ parent qty. Take-profits are validated in the
/// order supplied — Place passes UI order; Modify passes them sorted toward-market-first.
/// </summary>
public static class BracketGeometryValidator
{
    public static OrderResult? Validate(decimal entryRef, decimal? stopPrice,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, int parentQuantity, CurrencyType currency,
        bool isShort = false)
    {
        string fmt = CurrencyHelper.Format(entryRef, currency);

        if (stopPrice is decimal sp)
        {
            if (!isShort && sp >= entryRef)
                return OrderResultFactory.InvalidParams($"Stop-loss must be below the entry price ({fmt}).");
            if (isShort && sp <= entryRef)
                return OrderResultFactory.InvalidParams($"Stop-loss must be above the entry price ({fmt}).");
        }

        int tpSum = 0;
        decimal prev = entryRef;
        for (int i = 0; i < takeProfits.Count; i++)
        {
            var (px, qty) = takeProfits[i];
            if (qty <= 0) return OrderResultFactory.InvalidParams("Each take-profit quantity must be positive.");
            if (!isShort)
            {
                if (px <= entryRef)
                    return OrderResultFactory.InvalidParams($"Take-profit #{i + 1} must be above the entry price ({fmt}).");
                if (i > 0 && px <= prev)
                    return OrderResultFactory.InvalidParams("Take-profit prices must strictly increase.");
            }
            else
            {
                if (px >= entryRef)
                    return OrderResultFactory.InvalidParams($"Take-profit #{i + 1} must be below the entry price ({fmt}).");
                if (i > 0 && px >= prev)
                    return OrderResultFactory.InvalidParams("Take-profit prices must strictly decrease.");
            }
            prev = px;
            tpSum += qty;
        }
        if (tpSum > parentQuantity)
            return OrderResultFactory.InvalidParams(
                $"Take-profit quantities ({tpSum}) exceed the bracket quantity ({parentQuantity}).");
        return null;
    }
}
