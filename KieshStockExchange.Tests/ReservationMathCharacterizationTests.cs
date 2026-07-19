using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// Characterization tests for the server's authoritative reservation arithmetic
/// (<see cref="ReservationMath"/>, reachable via the Server csproj's InternalsVisibleTo). These PIN
/// the CURRENT output of every buy-reservation / short-collateral method against by-hand values —
/// they are a behavior fence for the settlement math, not a spec. Expected cash figures follow
/// <see cref="CurrencyHelper.Notional"/> = RoundMoney(price*qty) at the currency's decimals (USD = 2).
/// Order kinds are built from the three orthogonal dimensions (Side/Entry/Stop) that DERIVE OrderType,
/// matching the StopOrderModelTests convention.
/// </summary>
public class ReservationMathCharacterizationTests
{
    // ---- fixtures: one builder per order kind ReservationMath branches on ----

    private static Order LimitBuy(decimal price = 2.50m, int qty = 4, int filled = 1) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price, AmountFilled = filled,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order TrueMarketBuy(decimal budget = 500m, int qty = 10) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = 0m, BuyBudget = budget,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    // An armed (Pending) buy-stop-market: promotes to a TrueMarketBuy, so it reserves the flat budget.
    private static Order StopMarketBuy(decimal budget = 550m, int qty = 5) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = 0m, StopPrice = 110m, BuyBudget = budget,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
    };

    // An armed (Pending) buy-stop-limit: promotes to a LimitBuy, so it reserves at its limit Price.
    private static Order StopLimitBuy(decimal price = 105m, int qty = 4) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price, StopPrice = 104m, AmountFilled = 0,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
    };

    // A slippage-capped market buy: reserves at the upper-bound PriceWithSlippage.
    private static Order SlippageBuy(decimal anchor = 100m, decimal pct = 1.5m, int qty = 4) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = anchor, SlippagePercent = pct,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order LimitSell(decimal price = 50m, int qty = 10) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price,
        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    [Fact] // sanity: fixtures build the intended derived OrderType and are model-valid.
    public void Fixtures_derive_the_expected_order_types()
    {
        Assert.Equal(Order.Types.LimitBuy, LimitBuy().OrderType);
        Assert.Equal(Order.Types.TrueMarketBuy, TrueMarketBuy().OrderType);
        Assert.Equal(Order.Types.StopMarketBuy, StopMarketBuy().OrderType);
        Assert.Equal(Order.Types.StopLimitBuy, StopLimitBuy().OrderType);
        Assert.Equal(Order.Types.SlippageMarketBuy, SlippageBuy().OrderType);
        Assert.Equal(Order.Types.LimitSell, LimitSell().OrderType);
        Assert.True(LimitBuy().IsValid());
        Assert.True(TrueMarketBuy().IsValid());
        Assert.True(StopMarketBuy().IsValid());
        Assert.True(StopLimitBuy().IsValid());
        Assert.True(SlippageBuy().IsValid());
        Assert.True(LimitSell().IsValid());
    }

    // ---- IsTrueMarketBuy / IsBudgetBuy classification ----

    [Fact]
    public void IsTrueMarketBuy_true_only_for_true_market_buy()
    {
        Assert.True(ReservationMath.IsTrueMarketBuy(TrueMarketBuy()));
        Assert.False(ReservationMath.IsTrueMarketBuy(StopMarketBuy())); // still StopMarketBuy while armed
        Assert.False(ReservationMath.IsTrueMarketBuy(LimitBuy()));
        Assert.False(ReservationMath.IsTrueMarketBuy(StopLimitBuy()));
        Assert.False(ReservationMath.IsTrueMarketBuy(SlippageBuy()));
        Assert.False(ReservationMath.IsTrueMarketBuy(LimitSell()));
    }

    [Fact]
    public void IsBudgetBuy_true_for_true_market_and_stop_market_buys()
    {
        Assert.True(ReservationMath.IsBudgetBuy(TrueMarketBuy()));
        Assert.True(ReservationMath.IsBudgetBuy(StopMarketBuy()));
        Assert.False(ReservationMath.IsBudgetBuy(LimitBuy()));
        Assert.False(ReservationMath.IsBudgetBuy(StopLimitBuy()));
        Assert.False(ReservationMath.IsBudgetBuy(SlippageBuy()));
        Assert.False(ReservationMath.IsBudgetBuy(LimitSell()));
    }

    // ---- LimitBuy: per-unit = Price; Initial/Remaining = Notional(Price, qty) ----

    [Fact]
    public void LimitBuy_reservation_is_price_times_quantity()
    {
        var o = LimitBuy(price: 2.50m, qty: 4, filled: 1); // remaining = 3
        Assert.Equal(2.50m, ReservationMath.ReservationPerUnit(o));
        Assert.Equal(10.00m, ReservationMath.InitialBuyReservation(o));   // 2.50 * 4
        Assert.Equal(7.50m, ReservationMath.RemainingBuyReservation(o));  // 2.50 * 3
    }

    [Fact]
    public void LimitBuy_projected_uses_new_qty_and_price_over_remaining_after_fill()
    {
        var o = LimitBuy(price: 2.50m, qty: 4, filled: 1); // AmountFilled = 1
        // newQty 6, newPrice 3.00 → remainingQty = 6 - 1 = 5 → 3.00 * 5 = 15.00
        Assert.Equal(15.00m, ReservationMath.ProjectedBuyReservation(o, newQty: 6, newPrice: 3.00m));
    }

    [Fact]
    public void LimitBuy_projected_null_args_keeps_current_and_equals_remaining()
    {
        var o = LimitBuy(price: 2.50m, qty: 4, filled: 1);
        // null/null → perUnit 2.50, remainingQty = 4 - 1 = 3 → 7.50, same as RemainingBuyReservation.
        Assert.Equal(7.50m, ReservationMath.ProjectedBuyReservation(o, newQty: null, newPrice: null));
        Assert.Equal(ReservationMath.RemainingBuyReservation(o),
                     ReservationMath.ProjectedBuyReservation(o, null, null));
    }

    [Fact]
    public void LimitBuy_projected_zero_remaining_returns_zero()
    {
        var o = LimitBuy(price: 2.50m, qty: 4, filled: 1);
        // newQty = AmountFilled = 1 → remainingQty = max(0, 1 - 1) = 0 → 0.
        Assert.Equal(0m, ReservationMath.ProjectedBuyReservation(o, newQty: 1, newPrice: null));
    }

    [Fact]
    public void LimitBuy_rounding_case_rounds_notional_away_from_zero()
    {
        // 2.505 * 3 = 7.515 → RoundMoney(USD, AwayFromZero) = 7.52.
        var o = LimitBuy(price: 2.505m, qty: 3, filled: 0);
        Assert.Equal(2.505m, ReservationMath.ReservationPerUnit(o));
        Assert.Equal(7.52m, ReservationMath.InitialBuyReservation(o));
        Assert.Equal(7.52m, ReservationMath.RemainingBuyReservation(o));
    }

    // ---- TrueMarketBuy: flat BuyBudget; per-unit 0; Projected = BuyBudget ----

    [Fact]
    public void TrueMarketBuy_reserves_flat_budget()
    {
        var o = TrueMarketBuy(budget: 500m, qty: 10);
        Assert.Equal(0m, ReservationMath.ReservationPerUnit(o));         // budget buys reserve per-fill from budget
        Assert.Equal(500m, ReservationMath.InitialBuyReservation(o));
        Assert.Equal(500m, ReservationMath.RemainingBuyReservation(o));
        // IsTrueMarketBuy guard → budget is not modifiable, args ignored.
        Assert.Equal(500m, ReservationMath.ProjectedBuyReservation(o, newQty: 99, newPrice: 12.34m));
    }

    // ---- StopMarketBuy: budget for Initial/Remaining, but Projected guards on IsTrueMarketBuy ----

    [Fact]
    public void StopMarketBuy_initial_and_remaining_are_the_budget()
    {
        var o = StopMarketBuy(budget: 550m, qty: 5);
        Assert.Equal(0m, ReservationMath.ReservationPerUnit(o));
        Assert.Equal(550m, ReservationMath.InitialBuyReservation(o));   // IsBudgetBuy path
        Assert.Equal(550m, ReservationMath.RemainingBuyReservation(o));
    }

    [Fact]
    public void StopMarketBuy_projected_returns_zero_asymmetry()
    {
        var o = StopMarketBuy(budget: 550m, qty: 5);
        // characterization: ProjectedBuyReservation guards on IsTrueMarketBuy (NOT IsBudgetBuy). A
        // StopMarketBuy is a budget buy but not a true-market buy, and its limit/slippage branches
        // don't apply, so it falls through to `return 0m` — asymmetric vs Initial/Remaining (= 550).
        // Pinned as-is; DO NOT change the code.
        Assert.Equal(0m, ReservationMath.ProjectedBuyReservation(o, newQty: null, newPrice: null));
    }

    // ---- StopLimitBuy: special-cased to reserve at its limit Price (armed, AmountFilled = 0) ----

    [Fact]
    public void StopLimitBuy_reserves_at_its_limit_price()
    {
        var o = StopLimitBuy(price: 105m, qty: 4); // armed, RemainingQuantity = 4
        Assert.Equal(105m, ReservationMath.ReservationPerUnit(o));       // special-cased like a LimitBuy
        Assert.Equal(420m, ReservationMath.InitialBuyReservation(o));    // 105 * 4
        Assert.Equal(420m, ReservationMath.RemainingBuyReservation(o));  // 105 * 4 (nothing filled)
    }

    [Fact]
    public void StopLimitBuy_projected_special_cases_new_price_and_qty()
    {
        var o = StopLimitBuy(price: 105m, qty: 4); // AmountFilled = 0
        // newQty 6, newPrice 110 → remainingQty = 6 - 0 = 6 → 110 * 6 = 660.
        Assert.Equal(660m, ReservationMath.ProjectedBuyReservation(o, newQty: 6, newPrice: 110m));
    }

    // ---- Slippage buy: per-unit = PriceWithSlippage ----

    [Fact]
    public void SlippageBuy_reserves_at_price_with_slippage()
    {
        var o = SlippageBuy(anchor: 100m, pct: 1.5m, qty: 4);
        // PriceWithSlippage = 100 * (1 + 1.5/100) = 101.50.
        Assert.Equal(101.50m, o.PriceWithSlippage);
        Assert.Equal(101.50m, ReservationMath.ReservationPerUnit(o));
        Assert.Equal(406.00m, ReservationMath.InitialBuyReservation(o));   // 101.50 * 4
        Assert.Equal(406.00m, ReservationMath.RemainingBuyReservation(o)); // 101.50 * 4
        // Projected keeps the slippage upper bound (not modified), qty defaulted → same 406.00.
        Assert.Equal(406.00m, ReservationMath.ProjectedBuyReservation(o, newQty: null, newPrice: null));
    }

    // ---- Non-buy (sell): all buy-reservation methods return 0 ----

    [Fact]
    public void NonBuy_sell_returns_zero_for_all_buy_reservation_methods()
    {
        var o = LimitSell(price: 50m, qty: 10);
        Assert.Equal(0m, ReservationMath.ReservationPerUnit(o));
        Assert.Equal(0m, ReservationMath.InitialBuyReservation(o));
        Assert.Equal(0m, ReservationMath.RemainingBuyReservation(o));
        Assert.Equal(0m, ReservationMath.ProjectedBuyReservation(o, newQty: 5, newPrice: 60m));
    }

    // ---- Short collateral ----

    [Theory]
    [InlineData(10, 50.00, 500.00)]  // 50.00 * 10
    [InlineData(3, 2.505, 7.52)]     // 2.505 * 3 = 7.515 → RoundMoney = 7.52 (rounding case)
    public void ShortCollateralForFill_is_notional_of_fill_price_times_qty(
        int qty, decimal fillPrice, decimal expected)
        => Assert.Equal(expected, ReservationMath.ShortCollateralForFill(qty, fillPrice, CurrencyType.USD));

    [Fact]
    public void ShortCollateralForResting_is_notional_at_the_orders_limit_price()
    {
        var o = LimitSell(price: 50m, qty: 10);
        // Held at the order's own limit Price for the uncovered short remainder (4 shares): 50 * 4 = 200.
        Assert.Equal(200m, ReservationMath.ShortCollateralForResting(o, shortQty: 4));
    }
}
