using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// §3.6 P4 / §F5: long-bracket geometry rules, shared by <c>PlaceBracketAsync</c> (place) and
/// <c>ModifyBracketLegAsync</c> (per-leg edit) so the same invariants apply to both — no rule drift.
/// Returns <c>null</c> when valid, else an <see cref="OrderResult"/> with the validation error.
///
/// Rules (long bracket): stop-loss strictly below the entry reference; each take-profit strictly above
/// entry and strictly above the previous take-profit (in the order given); every take-profit qty &gt; 0;
/// Σ take-profit qty ≤ parent qty. Take-profits are validated in the order supplied — Place passes them
/// in UI order; Modify passes them price-sorted (so a relabelled-but-valid set still passes).
/// </summary>
public static class BracketGeometryValidator
{
    public static OrderResult? Validate(decimal entryRef, decimal? stopPrice,
        IReadOnlyList<(decimal Price, int Quantity)> takeProfits, int parentQuantity, CurrencyType currency)
    {
        if (stopPrice is decimal sp && sp >= entryRef)
            return OrderResultFactory.InvalidParams(
                $"Stop-loss must be below the entry price ({CurrencyHelper.Format(entryRef, currency)}).");

        int tpSum = 0;
        decimal prev = entryRef;
        for (int i = 0; i < takeProfits.Count; i++)
        {
            var (px, qty) = takeProfits[i];
            if (qty <= 0) return OrderResultFactory.InvalidParams("Each take-profit quantity must be positive.");
            if (px <= entryRef)
                return OrderResultFactory.InvalidParams(
                    $"Take-profit #{i + 1} must be above the entry price ({CurrencyHelper.Format(entryRef, currency)}).");
            if (px <= prev && i > 0)
                return OrderResultFactory.InvalidParams("Take-profit prices must strictly increase.");
            prev = px;
            tpSum += qty;
        }
        if (tpSum > parentQuantity)
            return OrderResultFactory.InvalidParams(
                $"Take-profit quantities ({tpSum}) exceed the bracket quantity ({parentQuantity}).");
        return null;
    }
}
