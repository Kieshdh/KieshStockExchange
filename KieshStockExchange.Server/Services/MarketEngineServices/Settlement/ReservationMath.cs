using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Pure reservation arithmetic for buy orders. </summary>
public static class ReservationMath
{
    internal static bool IsTrueMarketBuy(Order o) => o.OrderType == Order.Types.TrueMarketBuy;

    // §3.6 P2: budget-funded buys reserve a flat BuyBudget rather than price×qty. A
    // StopMarketBuy promotes to TrueMarketBuy, so while armed it reserves the same way.
    internal static bool IsBudgetBuy(Order o) =>
        o.OrderType is Order.Types.TrueMarketBuy or Order.Types.StopMarketBuy;

    /// <summary> Per-unit reservation. 0 for non-buys and budget buys (flat budget). </summary>
    internal static decimal ReservationPerUnit(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        // StopLimitBuy reserves at its limit Price, like a LimitBuy (it promotes to one).
        if (o.IsLimitOrder || o.OrderType == Order.Types.StopLimitBuy) return o.Price;
        if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue) return o.PriceWithSlippage.Value;
        return 0m; // budget buys: per-fill reserves directly from BuyBudget
    }

    /// <summary> Place-time reservation: per-unit × Quantity, or full BuyBudget for budget buys. </summary>
    internal static decimal InitialBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsBudgetBuy(o)) return o.BuyBudget ?? 0m;
        return CurrencyHelper.Notional(ReservationPerUnit(o), o.Quantity, o.CurrencyType);
    }

    /// <summary> Reservation still held against the unfilled portion. </summary>
    internal static decimal RemainingBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsBudgetBuy(o)) return o.BuyBudget ?? 0m;
        return CurrencyHelper.Notional(ReservationPerUnit(o), o.RemainingQuantity, o.CurrencyType);
    }

    /// <summary>
    /// Cash collateral for a short-opening fill of <paramref name="qty"/> shares at
    /// <paramref name="fillPrice"/>. §3.6 P1 reserves collateral at fill time (not place
    /// time) so it always equals the proceeds credited on the same fill — buying power is
    /// unchanged at open and conservation holds. Mirror of <see cref="InitialBuyReservation"/>
    /// for the short side.
    /// </summary>
    internal static decimal ShortCollateralForFill(int qty, decimal fillPrice, CurrencyType ccy)
        => CurrencyHelper.Notional(fillPrice, qty, ccy);

    /// <summary> RemainingBuyReservation with hypothetical new price/qty. Pass null to keep current. </summary>
    internal static decimal ProjectedBuyReservation(Order o, int? newQty, decimal? newPrice)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m; // budget is not modifiable

        decimal perUnit;
        if (o.IsLimitOrder)
            perUnit = newPrice ?? o.Price;
        else if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue)
            perUnit = o.PriceWithSlippage.Value; // slippage upper bound isn't modified
        else
            return 0m;

        var qty = newQty ?? o.Quantity;
        var remainingQty = Math.Max(0, qty - o.AmountFilled);
        if (remainingQty == 0) return 0m;
        return CurrencyHelper.Notional(perUnit, remainingQty, o.CurrencyType);
    }
}
