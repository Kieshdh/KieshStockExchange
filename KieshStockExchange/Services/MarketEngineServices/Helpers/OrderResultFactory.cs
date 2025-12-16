using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

public static class OrderResultFactory
{
    public static OrderResult NotAuthenticated() => new()
    {
        Status = OrderStatus.NotAuthenticated,
        ErrorMessage = "User not authenticated."
    };

    public static OrderResult NotAuthorized(string msg) => new()
    {
        Status = OrderStatus.NotAuthorized,
        ErrorMessage = msg
    };

    public static OrderResult InvalidParams(string msg) => new()
    {
        Status = OrderStatus.InvalidParameters,
        ErrorMessage = msg
    };

    public static OrderResult OperationFailed(string? msg = null) => new()
    {
        Status = OrderStatus.OperationFailed,
        ErrorMessage = msg ?? "An unexpected error occurred."
    };

    public static OrderResult NoLiquidity(Order order, List<Transaction> txs) => new()
    {
        PlacedOrder = order,
        FillTransactions = txs,
        Status = OrderStatus.NoLiquidity,
        ErrorMessage = order.IsBuyOrder
            ? "Not enough sell-side liquidity at or below your max price."
            : "Not enough buy-side liquidity at or above your min price."
    };

    public static OrderResult AlreadyClosed() => new()
    {
        Status = OrderStatus.AlreadyClosed,
        ErrorMessage = "Order already closed."
    };

    public static OrderResult Cancelled(Order order) => new()
    {
        PlacedOrder = order,
        Status = OrderStatus.Success,
        SuccessMessage = "Order successfully cancelled."
    };

    public static OrderResult Success(Order order, List<Transaction> txs)
    {
        var status = order.IsOpen
            ? txs.Count > 0 ? OrderStatus.PartialFill : OrderStatus.PlacedOnBook
            : order.RemainingQuantity > 0 ? OrderStatus.PartialFill : OrderStatus.Filled;

        return new OrderResult
        {
            PlacedOrder = order,
            FillTransactions = txs,
            Status = status,
            SuccessMessage = order.IsOpen
                ? txs.Count > 0 ? "Order partially filled." : "Order placed on book."
                : order.RemainingQuantity > 0 ? "Order partially filled." : "Order fully filled."
        };
    }

    public static OrderResult Modified(Order order, List<Transaction> txs)
    {
        var status = order.IsOpen
            ? txs.Count > 0 ? OrderStatus.PartialFill : OrderStatus.PlacedOnBook
            : order.RemainingQuantity > 0 ? OrderStatus.PartialFill : OrderStatus.Filled;

        return new OrderResult
        {
            PlacedOrder = order,
            FillTransactions = txs,
            Status = status,
            SuccessMessage = order.IsOpen
                ? txs.Count > 0 ? "Order partially filled after modification."
                                 : "Order modified and placed on book."
                : order.RemainingQuantity > 0 ? "Order partially filled after modification."
                                               : "Order fully filled after modification."
        };
    }
}