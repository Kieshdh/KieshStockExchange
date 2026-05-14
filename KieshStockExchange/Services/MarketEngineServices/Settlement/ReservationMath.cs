using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Pure reservation arithmetic for buy orders. </summary>
public static class ReservationMath
{
    internal static bool IsTrueMarketBuy(Order o) => o.OrderType == Order.Types.TrueMarketBuy;

    internal static decimal Round(decimal amount, CurrencyType ccy)
        => CurrencyHelper.RoundMoney(amount, ccy);

    /// <summary>
    /// Per-unit reservation for a buy order. Returns 0 for non-buys and TrueMarketBuy
    /// (which reserves a flat <see cref="Order.BuyBudget"/> rather than per-unit).
    /// LimitBuy reserves at <see cref="Order.Price"/>; SlippageMarketBuy reserves at the
    /// upper-bound <see cref="Order.PriceWithSlippage"/>.
    /// </summary>
    internal static decimal ReservationPerUnit(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (o.IsLimitOrder) return o.Price;
        if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue) return o.PriceWithSlippage.Value;
        return 0m; // TrueMarketBuy: per-fill reserves directly from BuyBudget
    }

    /// <summary>
    /// Up-front reservation amount for a freshly-placed buy order. For limit and slippage
    /// orders this is per-unit × Quantity; for TrueMarketBuy it's the full BuyBudget.
    /// </summary>
    internal static decimal InitialBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return Round(ReservationPerUnit(o) * o.Quantity, o.CurrencyType);
    }

    /// <summary>
    /// Reservation still held against the unfilled portion of a buy order. For limit /
    /// slippage orders that's per-unit × RemainingQuantity. For TrueMarketBuy it's the
    /// remaining <see cref="Order.BuyBudget"/> (which the apply-pass decrements per fill).
    /// </summary>
    internal static decimal RemainingBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return Round(ReservationPerUnit(o) * o.RemainingQuantity, o.CurrencyType);
    }

    /// <summary>
    /// What <see cref="RemainingBuyReservation"/> would return if the order's price
    /// and/or quantity were changed to the supplied values. Used when sizing the
    /// reservation delta for a modify before mutating the order. Caller passes nulls
    /// for fields that are not changing.
    /// </summary>
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
        return Round(perUnit * remainingQty, o.CurrencyType);
    }
}
