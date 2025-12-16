using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderValidator
{
    /// <summary> Returns null when valid; otherwise an OrderResult explaining the failure. </summary>
    OrderResult? ValidateInput(int userId, int stockId, int quantity, decimal price,
        CurrencyType currency, bool buyOrder, bool limitOrder, decimal? slippagePercent);

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
    private readonly IStockService _stock;

    public OrderValidator(IStockService stock) =>
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
    #endregion

    #region Validatation Methods
    public OrderResult? ValidateInput(int userId, int stockId, int quantity, decimal price,
        CurrencyType currency, bool buyOrder, bool limitOrder, decimal? slippagePercent)
    {
        if (userId <= 0)
            return OrderResultFactory.InvalidParams("Invalid user ID.");
        if (stockId <= 0 || !_stock.TryGetById(stockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");
        if (quantity <= 0)
            return OrderResultFactory.InvalidParams("Quantity must be positive.");
        if (!CurrencyHelper.IsSupported(currency))
            return OrderResultFactory.InvalidParams("Unsupported currency.");

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
        if (!_stock.TryGetById(order.StockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");

        // Limit order: Price must be positive and cannot have slippage.
        if (order.IsLimitOrder)
        {
            if (order.Price <= 0m)
                return OrderResultFactory.InvalidParams("Limit price must be positive.");
            if (order.SlippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("Limit order cannot have slippage.");
        }

        // True market: Price must be 0 and no slippage.
        if (order.IsTrueMarketOrder)
        {
            if (order.Price != 0m)
                return OrderResultFactory.InvalidParams("TrueMarket must have Price = 0.");
            if (order.SlippagePercent.HasValue)
                return OrderResultFactory.InvalidParams("TrueMarket cannot have slippage.");
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

        if (newQty.HasValue && newQty.Value <= 0)
            return OrderResultFactory.InvalidParams("New quantity must be positive.");

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
        if (!order.IsOpen) return OrderResultFactory.AlreadyClosed();

        if (!_stock.TryGetById(order.StockId, out _))
            return OrderResultFactory.InvalidParams("Invalid stock ID.");

        if (order.IsInvalid) return OrderResultFactory.InvalidParams("Order is invalid.");
        return null;
    }
    #endregion
}
