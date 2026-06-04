using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderValidator
{
    /// <summary> Returns null when valid; otherwise an OrderResult explaining the failure. </summary>
    OrderResult? ValidateInput(int userId, int stockId, int quantity, decimal price, CurrencyType currency, 
        bool buyOrder, bool limitOrder, decimal? slippagePercent = null, decimal? buyBudget = null);

    /// <summary> Returns null when valid; otherwise an OrderResult explaining the failure. </summary>
    OrderResult? ValidateNew(Order order);

    /// <summary> Returns null when valid; otherwise an OrderResult explaining the failure. </summary>
    OrderResult? ValidateModify(Order order, int? newQty, decimal? newPrice);

    /// <summary> Returns null when valid; otherwise an OrderResult explaining the failure. </summary>
    OrderResult? ValidateCancel(Order order);
}

public sealed class OrderValidator : IOrderValidator
{
    #region Services and Constructor

    // Quantity ceiling — reject obviously-bad inputs early so reservation math
    // (Quantity × Price) never produces an unhelpful OverflowException stack trace.
    // Decimal max (~7.9e28) is far above any realistic value; the cap exists for
    // user-error protection, not arithmetic safety. Tune up if the project ever
    // needs genuinely larger orders.
    //
    // Intentionally NO BuyBudget ceiling: the legitimate value is "whatever the
    // user actually has", which the AvailableBalance check in SettleOrderAsync
    // enforces with a clean "Insufficient funds" message. A hardcoded ceiling
    // here would falsely reject high-balance users.
    private const int MaxOrderQuantity = 1_000_000;

    private readonly IStockService _stock;

    public OrderValidator(IStockService stock) =>
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
    #endregion

    #region Validation Methods
    public OrderResult? ValidateInput(int userId, int stockId, int quantity, decimal price, CurrencyType currency,
        bool buyOrder, bool limitOrder, decimal? slippagePercent = null, decimal? buyBudget = null)
    {
        if (userId <= 0)
            return OrderResultFactory.InvalidParams("Invalid user ID.");
        if (stockId <= 0 || !_stock.TryGetById(stockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");
        if (quantity <= 0)
            return OrderResultFactory.InvalidParams("Quantity must be positive.");
        if (quantity > MaxOrderQuantity)
            return OrderResultFactory.InvalidParams($"Quantity exceeds the maximum of {MaxOrderQuantity:N0}.");
        if (NotionalOverflows(price, quantity))
            return OrderResultFactory.InvalidParams("Price is too large.");
        if (!CurrencyHelper.IsSupported(currency))
            return OrderResultFactory.InvalidParams("Unsupported currency.");
        // Reject phantom (StockId, Currency) books.
        if (!_stock.IsListedIn(stockId, currency))
            return OrderResultFactory.InvalidParams(
                $"Stock {stockId} is not listed in {currency}.");

        // Limit order: must have positive price and no slippage.
        if (limitOrder)
        {
            if (price <= 0m)
                return OrderResultFactory.InvalidParams("Limit price must be positive.");
            if (slippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("Limit order cannot have slippage.");
        }
        else
        {
            // TrueMarket: price=0 and no slippage
            if (!slippagePercent.HasValue)
            {
                if (price != 0m)
                    return OrderResultFactory.InvalidParams("TrueMarket must have Price = 0.");
                if (buyOrder)
                {
                    // For TrueMarket buy orders with a budget, ensure budget is positive
                    if (!buyBudget.HasValue || buyBudget.Value <= 0m)
                        return OrderResultFactory.InvalidParams("BuyBudget is required for TrueMarket BUY orders and must be > 0.");
                }
            }
            else // SlippageMarket: needs anchor price and slippage %
            {
                if (price <= 0m)
                    return OrderResultFactory.InvalidParams("Slippage anchor price must be positive.");
                if (slippagePercent.Value < 0m || slippagePercent.Value > 100m)
                    return OrderResultFactory.InvalidParams("Slippage percent must be between 0 and 100%.");
            }
        }
        return null;
    }

    public OrderResult? ValidateNew(Order order)
    {
        if (order is null) return OrderResultFactory.InvalidParams("Order is null.");
        if (order.Quantity <= 0) return OrderResultFactory.InvalidParams("Quantity must be positive.");
        if (order.Quantity > MaxOrderQuantity)
            return OrderResultFactory.InvalidParams($"Quantity exceeds the maximum of {MaxOrderQuantity:N0}.");
        if (NotionalOverflows(order.Price, order.Quantity))
            return OrderResultFactory.InvalidParams("Price is too large.");
        if (!_stock.TryGetById(order.StockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");
        // Reject phantom (StockId, Currency) books.
        if (!_stock.IsListedIn(order.StockId, order.CurrencyType))
            return OrderResultFactory.InvalidParams(
                $"Stock {order.StockId} is not listed in {order.CurrencyType}.");

        // Limit order: Price must be positive and cannot have slippage.
        if (order.IsLimitOrder)
        {
            if (order.Price <= 0m)
                return OrderResultFactory.InvalidParams("Limit price must be positive.");
            if (order.SlippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("Limit order cannot have slippage.");
            if (order.BuyBudget.HasValue)
                return OrderResultFactory.InvalidParams("Limit buy order cannot have BuyBudget.");
        }

        // True market: Price must be 0 and no slippage.
        if (order.IsTrueMarketOrder)
        {
            if (order.Price != 0m)
                return OrderResultFactory.InvalidParams("TrueMarket must have Price = 0.");
            if (order.SlippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("TrueMarket cannot have slippage.");
            if (order.IsBuyOrder && (!order.BuyBudget.HasValue || order.BuyBudget.Value <= 0m))
                return OrderResultFactory.InvalidParams("BuyBudget is required for TrueMarket BUY orders and must be > 0.");
            if (order.IsSellOrder && order.BuyBudget.HasValue)
                return OrderResultFactory.InvalidParams("Sell TrueMarket orders cannot have BuyBudget.");
        }

        // Slippage market: needs anchor price and slippage %
        if (order.IsSlippageOrder)
        {
            if (!order.SlippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("Slippage percent is required.");
            if (order.SlippagePercent.Value < 0m)
                return OrderResultFactory.InvalidParams("Slippage percent cannot be negative.");
            if (order.SlippagePercent.Value > 100m)
                return OrderResultFactory.InvalidParams("Slippage percent cannot exceed 100%.");
            if (order.Price <= 0m)
                return OrderResultFactory.InvalidParams("Slippage anchor price must be positive.");
            if (order.BuyBudget.HasValue)
                return OrderResultFactory.InvalidParams("Slippage market order cannot have BuyBudget.");
        }

        // §3.6 stop orders, validated like their promotion target. StopLimit → limit
        // (positive Price, no slippage). StopMarket → market: either a slippage-capped market
        // (SlippagePercent set + a positive anchor Price) or a true market (Price 0). Buys
        // fund an uncapped market from BuyBudget; sells/limits/capped markets carry none. All
        // need a positive StopPrice. Direction sanity is enforced at the arm entry point.
        if (order.IsStopOrder)
        {
            if (!order.StopPrice.HasValue || order.StopPrice.Value <= 0m)
                return OrderResultFactory.InvalidParams("Stop order requires a positive stop price.");
            if (NotionalOverflows(order.StopPrice.Value, order.Quantity))
                return OrderResultFactory.InvalidParams("Stop price is too large.");

            if (order.IsStopLimitOrder)
            {
                if (order.SlippagePercent.HasValue)
                    return OrderResultFactory.InvalidParams("Stop-limit order cannot have slippage.");
                if (order.Price <= 0m)
                    return OrderResultFactory.InvalidParams("Stop-limit requires a positive limit price.");
                if (order.BuyBudget.HasValue)
                    return OrderResultFactory.InvalidParams("Stop-limit order cannot have BuyBudget.");
            }
            else // StopMarket
            {
                if (order.SlippagePercent.HasValue)
                {
                    // Slippage-capped stop-market: needs a positive anchor Price and a 0–100% cap.
                    if (order.Price <= 0m)
                        return OrderResultFactory.InvalidParams("Capped StopMarket requires a positive anchor price.");
                    if (order.SlippagePercent.Value < 0m || order.SlippagePercent.Value > 100m)
                        return OrderResultFactory.InvalidParams("Slippage percent must be between 0 and 100%.");
                    if (order.BuyBudget.HasValue)
                        return OrderResultFactory.InvalidParams("Capped StopMarket order cannot have BuyBudget.");
                }
                else // true (uncapped) market stop
                {
                    if (order.Price != 0m)
                        return OrderResultFactory.InvalidParams("StopMarket must have Price = 0.");
                    if (order.IsBuyOrder && (!order.BuyBudget.HasValue || order.BuyBudget.Value <= 0m))
                        return OrderResultFactory.InvalidParams("BuyBudget is required for StopMarket BUY orders and must be > 0.");
                    if (order.IsSellOrder && order.BuyBudget.HasValue)
                        return OrderResultFactory.InvalidParams("StopMarket SELL orders cannot have BuyBudget.");
                }
            }
        }

        if (order.IsInvalid) return OrderResultFactory.InvalidParams("Order is invalid.");
        return null;
    }

    public OrderResult? ValidateModify(Order order, int? newQty, decimal? newPrice)
    {
        if (order is null) return OrderResultFactory.InvalidParams("Order not found.");
        if (!order.IsOpen) return OrderResultFactory.AlreadyClosed();

        if (!_stock.TryGetById(order.StockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");

        if (newQty.HasValue)
        {
            if (newQty.Value <= 0)
                return OrderResultFactory.InvalidParams("New quantity must be positive.");
            if (newQty.Value > MaxOrderQuantity)
                return OrderResultFactory.InvalidParams($"Quantity exceeds the maximum of {MaxOrderQuantity:N0}.");
            if (!order.IsLimitOrder)
                return OrderResultFactory.InvalidParams("Cannot modify quantity for non-limit orders.");
        }

        if (newPrice.HasValue)
        {
            if (order.IsLimitOrder)
            {
                if (newPrice.Value <= 0m)
                    return OrderResultFactory.InvalidParams("New price must be positive.");
            }
            else
            {
                // Disallow price modification for non-limit orders to preserve order semantics.
                return OrderResultFactory.InvalidParams("Cannot modify price for non-limit orders.");
            }
        }

        if (order.IsInvalid) return OrderResultFactory.InvalidParams("Order is invalid.");

        return null;
    }

    public OrderResult? ValidateCancel(Order order)
    {
        if (order is null) return OrderResultFactory.InvalidParams("Order not found.");
        // An armed (Pending) stop is cancellable too — the user can pull it before it triggers.
        if (!order.IsOpen && !order.IsArmed) return OrderResultFactory.AlreadyClosed();

        if (!_stock.TryGetById(order.StockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");

        if (order.IsInvalid) return OrderResultFactory.InvalidParams("Order is invalid.");
        return null;
    }

    // True when the reservation multiply (Notional = price × quantity) would overflow
    // decimal and surface as a 500. quantity is capped above; the ×2 headroom absorbs a
    // slippage order's doubled effective price (PriceWithSlippage at 100% slippage).
    private static bool NotionalOverflows(decimal price, int quantity)
        => quantity > 0 && Math.Abs(price) > decimal.MaxValue / (quantity * 2);
    #endregion
}
