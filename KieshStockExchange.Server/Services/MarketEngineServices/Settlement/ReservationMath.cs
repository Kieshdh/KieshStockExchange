using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Pure reservation arithmetic for buy orders. </summary>
public static class ReservationMath
{
    internal static bool IsTrueMarketBuy(Order o) => o.OrderType == Order.Types.TrueMarketBuy;

    /// <summary> Per-unit reservation. 0 for non-buys and TrueMarketBuy (flat budget). </summary>
    internal static decimal ReservationPerUnit(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (o.IsLimitOrder) return o.Price;
        if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue) return o.PriceWithSlippage.Value;
        return 0m; // TrueMarketBuy: per-fill reserves directly from BuyBudget
    }

    /// <summary> Place-time reservation: per-unit × Quantity, or full BuyBudget for TrueMarketBuy. </summary>
    internal static decimal InitialBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return CurrencyHelper.Notional(ReservationPerUnit(o), o.Quantity, o.CurrencyType);
    }

    /// <summary> Reservation still held against the unfilled portion. </summary>
    internal static decimal RemainingBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return CurrencyHelper.Notional(ReservationPerUnit(o), o.RemainingQuantity, o.CurrencyType);
    }

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
