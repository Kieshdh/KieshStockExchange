using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// Characterization tests for the server's <see cref="OrderValidator"/> (public, constructed with a
/// mocked <see cref="IStockService"/> exactly like ArbBatchLegsEquivalenceTests). These PIN the CURRENT
/// accept/reject behaviour and the EXACT rejection message text of <c>ValidateInput</c> (loose params)
/// and <c>ValidateNew</c> (a built <see cref="Order"/>) — a behaviour fence, not a spec. DO NOT change
/// the code to make a surprising case "nicer"; the surprises are pinned deliberately.
///
/// Contract: both methods return <c>OrderResult?</c> — <c>null</c> == ACCEPT (valid); a non-null
/// result carries <see cref="OrderStatus.InvalidParameters"/> and the human message in ErrorMessage.
/// Order kinds are built from the three orthogonal dimensions (Side/Entry/Stop), matching the
/// ReservationMath/StopOrderModel test convention. Message literals are copied verbatim from
/// OrderValidator/OrderResultFactory; the "max quantity" line is rebuilt with the same
/// <c>{1_000_000:N0}</c> interpolation so it matches regardless of the test host's culture.
/// </summary>
public class OrderValidatorCharacterizationTests
{
    private const CurrencyType USD = CurrencyType.USD;

    // The exact max-quantity message, rebuilt with the same interpolation the code uses
    // ($"...{MaxOrderQuantity:N0}." with MaxOrderQuantity = 1_000_000) so it stays culture-safe.
    private static readonly string MaxQtyMessage = $"Quantity exceeds the maximum of {1_000_000:N0}.";

    // known: TryGetById returns this (a valid stock exists). listed: IsListedIn returns this.
    private static OrderValidator MakeValidator(bool known = true, bool listed = true)
    {
        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(known);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(listed);
        return new OrderValidator(stocks.Object);
    }

    // ---- assertion helpers: null == accept; non-null == reject with InvalidParameters ----

    private static void AssertAccepted(OrderResult? result) => Assert.Null(result);

    private static void AssertRejected(OrderResult? result, string expectedMessage)
    {
        Assert.NotNull(result);
        Assert.Equal(OrderStatus.InvalidParameters, result!.Status);
        Assert.Equal(expectedMessage, result.ErrorMessage);
    }

    // ---- Order fixtures for ValidateNew (three-dimension builders) ----

    private static Order LimitBuy(decimal price = 2.50m, int qty = 4) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order LimitSell(decimal price = 50m, int qty = 10) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price,
        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order TrueMarketBuy(decimal budget = 500m, int qty = 10) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = 0m, BuyBudget = budget,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order TrueMarketSell(int qty = 10) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = 0m,
        Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    private static Order SlippageBuy(decimal anchor = 100m, decimal pct = 1.5m, int qty = 4) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = anchor, SlippagePercent = pct,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Status = Order.Statuses.Open,
    };

    // Armed buy-stop-market: true-market stop that promotes to a TrueMarketBuy.
    private static Order StopMarketBuy(decimal budget = 550m, int qty = 5) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = 0m, StopPrice = 110m, BuyBudget = budget,
        Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
    };

    // Armed buy-stop-limit: promotes to a LimitBuy.
    private static Order StopLimitBuy(decimal price = 105m, int qty = 4) => new()
    {
        UserId = 1, StockId = 1, Quantity = qty, Price = price, StopPrice = 104m,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
    };

    // =====================================================================================
    //  ValidateInput — HAPPY PATHS (accept == null)
    // =====================================================================================

    [Fact]
    public void ValidateInput_LimitBuy_valid_is_accepted()
    {
        var v = MakeValidator();
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true));
    }

    [Fact]
    public void ValidateInput_LimitSell_valid_is_accepted()
    {
        var v = MakeValidator();
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 10, price: 50m,
            currency: USD, buyOrder: false, limitOrder: true));
    }

    [Fact]
    public void ValidateInput_TrueMarketBuy_price0_with_budget_is_accepted()
    {
        var v = MakeValidator();
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 10, price: 0m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: null, buyBudget: 500m));
    }

    [Fact]
    public void ValidateInput_TrueMarketSell_price0_no_budget_is_accepted()
    {
        var v = MakeValidator();
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 10, price: 0m,
            currency: USD, buyOrder: false, limitOrder: false));
    }

    [Fact]
    public void ValidateInput_SlippageMarketBuy_valid_is_accepted()
    {
        var v = MakeValidator();
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 100m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: 1.5m));
    }

    // =====================================================================================
    //  ValidateInput — ONE-RULE VIOLATIONS (exact message per rule, in short-circuit order)
    // =====================================================================================

    [Fact]
    public void ValidateInput_userId_not_positive_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 0, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Invalid user ID.");
    }

    [Fact]
    public void ValidateInput_stockId_not_positive_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 0, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Invalid stock ID.");
    }

    [Fact]
    public void ValidateInput_unknown_stock_is_rejected()
    {
        // stockId > 0 but the catalog does not know it (TryGetById → false).
        var v = MakeValidator(known: false);
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Invalid stock ID.");
    }

    [Fact]
    public void ValidateInput_quantity_not_positive_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 0, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Quantity must be positive.");
    }

    [Fact]
    public void ValidateInput_quantity_over_max_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 1_000_001, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), MaxQtyMessage);
    }

    [Fact]
    public void ValidateInput_notional_overflow_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 1000, price: decimal.MaxValue,
            currency: USD, buyOrder: true, limitOrder: true), "Price is too large.");
    }

    [Fact]
    public void ValidateInput_unsupported_currency_is_rejected()
    {
        // Only reachable with an out-of-range cast — every declared CurrencyType is supported.
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: (CurrencyType)999, buyOrder: true, limitOrder: true), "Unsupported currency.");
    }

    [Fact]
    public void ValidateInput_stock_not_listed_in_currency_is_rejected()
    {
        var v = MakeValidator(listed: false);
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Stock 1 is not listed in USD.");
    }

    [Fact]
    public void ValidateInput_limit_price_not_positive_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 0m,
            currency: USD, buyOrder: true, limitOrder: true), "Limit price must be positive.");
    }

    [Fact]
    public void ValidateInput_limit_with_slippage_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true, slippagePercent: 1.0m),
            "Limit order cannot have slippage.");
    }

    [Fact]
    public void ValidateInput_truemarket_nonzero_price_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 5m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: null, buyBudget: 100m),
            "TrueMarket must have Price = 0.");
    }

    [Fact]
    public void ValidateInput_truemarket_buy_missing_budget_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 0m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: null, buyBudget: null),
            "BuyBudget is required for TrueMarket BUY orders and must be > 0.");
    }

    [Fact]
    public void ValidateInput_slippage_anchor_price_not_positive_is_rejected()
    {
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 0m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: 1.5m),
            "Slippage anchor price must be positive.");
    }

    [Fact]
    public void ValidateInput_slippage_percent_out_of_range_is_rejected()
    {
        // characterization: ValidateInput takes slippagePercent as a raw decimal? param — it is NOT
        // clamped by the Order.SlippagePercent setter, so a >100 value REACHES this range check here.
        // (The equivalent ValidateNew rule is unreachable — the setter throws first. See the
        // ValidateNew slippage-range skip note below.)
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 100m,
            currency: USD, buyOrder: true, limitOrder: false, slippagePercent: 150m),
            "Slippage percent must be between 0 and 100%.");
    }

    // ---- ValidateInput short-circuit ORDER (two rules violated at once) ----

    [Fact]
    public void ValidateInput_userId_beats_stockId_when_both_invalid()
    {
        // userId check runs before the stock check → "Invalid user ID." wins.
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 0, stockId: 0, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true), "Invalid user ID.");
    }

    [Fact]
    public void ValidateInput_quantity_beats_limit_price_when_both_invalid()
    {
        // Quantity check (#3) runs before the limit-price check (#8) → quantity message wins.
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 0, price: 0m,
            currency: USD, buyOrder: true, limitOrder: true), "Quantity must be positive.");
    }

    // =====================================================================================
    //  ValidateNew — HAPPY PATHS (accept == null)
    // =====================================================================================

    [Fact]
    public void ValidateNew_LimitBuy_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(LimitBuy()));

    [Fact]
    public void ValidateNew_LimitSell_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(LimitSell()));

    [Fact]
    public void ValidateNew_TrueMarketBuy_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(TrueMarketBuy()));

    [Fact]
    public void ValidateNew_SlippageMarketBuy_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(SlippageBuy()));

    [Fact]
    public void ValidateNew_StopMarketBuy_armed_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(StopMarketBuy()));

    [Fact]
    public void ValidateNew_StopLimitBuy_armed_valid_is_accepted() => AssertAccepted(MakeValidator().ValidateNew(StopLimitBuy()));

    // =====================================================================================
    //  ValidateNew — ONE-RULE VIOLATIONS
    // =====================================================================================

    [Fact]
    public void ValidateNew_null_order_is_rejected()
        => AssertRejected(MakeValidator().ValidateNew(null!), "Order is null.");

    [Fact]
    public void ValidateNew_quantity_not_positive_is_rejected()
    {
        var o = LimitBuy(); o.Quantity = 0;
        AssertRejected(MakeValidator().ValidateNew(o), "Quantity must be positive.");
    }

    [Fact]
    public void ValidateNew_quantity_over_max_is_rejected()
    {
        var o = LimitBuy(); o.Quantity = 1_000_001;
        AssertRejected(MakeValidator().ValidateNew(o), MaxQtyMessage);
    }

    [Fact]
    public void ValidateNew_notional_overflow_is_rejected()
    {
        var o = LimitBuy(qty: 1000); o.Price = decimal.MaxValue;
        AssertRejected(MakeValidator().ValidateNew(o), "Price is too large.");
    }

    [Fact]
    public void ValidateNew_unknown_stock_is_rejected()
        => AssertRejected(MakeValidator(known: false).ValidateNew(LimitBuy()), "Invalid stock ID.");

    [Fact]
    public void ValidateNew_stock_not_listed_in_currency_is_rejected()
        => AssertRejected(MakeValidator(listed: false).ValidateNew(LimitBuy()), "Stock 1 is not listed in USD.");

    [Fact]
    public void ValidateNew_limit_price_not_positive_is_rejected()
    {
        var o = LimitBuy(); o.Price = 0m;
        AssertRejected(MakeValidator().ValidateNew(o), "Limit price must be positive.");
    }

    [Fact]
    public void ValidateNew_limit_with_slippage_is_rejected()
    {
        var o = LimitBuy(); o.SlippagePercent = 1.5m;
        AssertRejected(MakeValidator().ValidateNew(o), "Limit order cannot have slippage.");
    }

    [Fact]
    public void ValidateNew_limit_buy_with_budget_is_rejected()
    {
        // characterization: ValidateNew rejects a LIMIT buy carrying a BuyBudget. ValidateInput has
        // NO equivalent check (it ignores buyBudget on the limit path) — see the divergence test below.
        var o = LimitBuy(); o.BuyBudget = 100m;
        AssertRejected(MakeValidator().ValidateNew(o), "Limit buy order cannot have BuyBudget.");
    }

    [Fact]
    public void ValidateNew_truemarket_nonzero_price_is_rejected()
    {
        var o = TrueMarketBuy(); o.Price = 5m;
        AssertRejected(MakeValidator().ValidateNew(o), "TrueMarket must have Price = 0.");
    }

    [Fact]
    public void ValidateNew_truemarket_with_slippage_is_rejected()
    {
        // Market entry with a slippage % is classified IsSlippageOrder, NOT IsTrueMarketOrder, so the
        // "TrueMarket cannot have slippage." line at OrderValidator.cs:137 is effectively unreachable
        // for a well-formed market order. Pinned instead: the slippage-anchor rule fires (Price 0).
        var o = TrueMarketBuy(); o.BuyBudget = null; o.SlippagePercent = 1.5m; // Price still 0
        AssertRejected(MakeValidator().ValidateNew(o), "Slippage anchor price must be positive.");
    }

    [Fact]
    public void ValidateNew_truemarket_buy_missing_budget_is_rejected()
    {
        var o = TrueMarketBuy(); o.BuyBudget = null;
        AssertRejected(MakeValidator().ValidateNew(o),
            "BuyBudget is required for TrueMarket BUY orders and must be > 0.");
    }

    [Fact]
    public void ValidateNew_truemarket_sell_with_budget_is_rejected()
    {
        // characterization: ValidateNew rejects a TrueMarket SELL carrying a budget. ValidateInput does
        // NOT (its budget check is gated on buyOrder only) — see the divergence test below.
        var o = TrueMarketSell(); o.BuyBudget = 100m;
        AssertRejected(MakeValidator().ValidateNew(o), "Sell TrueMarket orders cannot have BuyBudget.");
    }

    [Fact]
    public void ValidateNew_slippage_anchor_price_not_positive_is_rejected()
    {
        var o = SlippageBuy(); o.Price = 0m; // slippage set, anchor 0
        AssertRejected(MakeValidator().ValidateNew(o), "Slippage anchor price must be positive.");
    }

    [Fact]
    public void ValidateNew_slippage_market_with_budget_is_rejected()
    {
        var o = SlippageBuy(); o.BuyBudget = 100m;
        AssertRejected(MakeValidator().ValidateNew(o), "Slippage market order cannot have BuyBudget.");
    }

    // ---- ValidateNew stop-order rules ----

    [Fact]
    public void ValidateNew_stop_without_positive_stop_price_is_rejected()
    {
        var o = StopMarketBuy(); o.StopPrice = null;
        AssertRejected(MakeValidator().ValidateNew(o), "Stop order requires a positive stop price.");
    }

    [Fact]
    public void ValidateNew_trailing_stop_must_be_market_only()
    {
        // A stop-LIMIT with Stop = Trailing → "Trailing stops are market-only." (checked before the
        // stop-limit price/slippage rules).
        var o = StopLimitBuy(); o.Stop = StopKind.Trailing;
        AssertRejected(MakeValidator().ValidateNew(o), "Trailing stops are market-only.");
    }

    [Fact]
    public void ValidateNew_stop_limit_with_slippage_is_rejected()
    {
        var o = StopLimitBuy(); o.SlippagePercent = 1.5m;
        AssertRejected(MakeValidator().ValidateNew(o), "Stop-limit order cannot have slippage.");
    }

    // ---- ValidateNew short-circuit ORDER (two rules violated at once) ----

    [Fact]
    public void ValidateNew_quantity_beats_notional_when_both_invalid()
    {
        // Quantity check (#2) runs before the notional-overflow check (#4).
        var o = LimitBuy(qty: 1000); o.Quantity = 0; o.Price = decimal.MaxValue;
        AssertRejected(MakeValidator().ValidateNew(o), "Quantity must be positive.");
    }

    [Fact]
    public void ValidateNew_limit_slippage_beats_buybudget_when_both_present()
    {
        // On the limit path the slippage rule (line 126) runs before the BuyBudget rule (line 128).
        var o = LimitBuy(); o.SlippagePercent = 1.5m; o.BuyBudget = 100m;
        AssertRejected(MakeValidator().ValidateNew(o), "Limit order cannot have slippage.");
    }

    // =====================================================================================
    //  DIVERGENCES — the same logical input, pinned to show ValidateInput vs ValidateNew disagree
    // =====================================================================================

    [Fact]
    public void Divergence_limit_buy_with_budget_input_accepts_new_rejects()
    {
        var v = MakeValidator();
        // characterization: ValidateInput ACCEPTS a limit buy that carries a budget (it never checks
        // buyBudget on the limit path); ValidateNew REJECTS the equivalent order.
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: USD, buyOrder: true, limitOrder: true, slippagePercent: null, buyBudget: 100m));

        var o = LimitBuy(); o.BuyBudget = 100m;
        AssertRejected(v.ValidateNew(o), "Limit buy order cannot have BuyBudget.");
    }

    [Fact]
    public void Divergence_truemarket_sell_with_budget_input_accepts_new_rejects()
    {
        var v = MakeValidator();
        // characterization: ValidateInput ACCEPTS a TrueMarket SELL with a budget (its budget guard is
        // gated on buyOrder only); ValidateNew REJECTS the equivalent order.
        AssertAccepted(v.ValidateInput(userId: 1, stockId: 1, quantity: 10, price: 0m,
            currency: USD, buyOrder: false, limitOrder: false, slippagePercent: null, buyBudget: 100m));

        var o = TrueMarketSell(); o.BuyBudget = 100m;
        AssertRejected(v.ValidateNew(o), "Sell TrueMarket orders cannot have BuyBudget.");
    }

    [Fact]
    public void Divergence_unsupported_currency_only_input_checks_it()
    {
        // characterization: ValidateInput rejects an unsupported currency; ValidateNew has NO
        // currency-support check at all (an Order can only hold a declared CurrencyType, so the guard
        // was never added there). Both paths otherwise agree.
        var v = MakeValidator();
        AssertRejected(v.ValidateInput(userId: 1, stockId: 1, quantity: 4, price: 2.50m,
            currency: (CurrencyType)999, buyOrder: true, limitOrder: true), "Unsupported currency.");
    }
}
