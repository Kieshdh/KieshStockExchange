using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Builds standard <see cref="OrderResult"/> instances for the order pipeline so callers
/// don't repeat status/message wiring at every failure or success site.
/// </summary>
public static class OrderResultFactory
{
    #region Failure Results
    /// <summary> User session was not authenticated when the order was placed. </summary>
    public static OrderResult NotAuthenticated() => new()
    {
        Status = OrderStatus.NotAuthenticated,
        ErrorMessage = "User not authenticated."
    };

    /// <summary> User is authenticated but lacks the role/ownership needed for this action. </summary>
    public static OrderResult NotAuthorized(string msg) => new()
    {
        Status = OrderStatus.NotAuthorized,
        ErrorMessage = msg
    };

    /// <summary> Input parameters failed validation (price, quantity, currency, etc.). </summary>
    public static OrderResult InvalidParams(string msg) => new()
    {
        Status = OrderStatus.InvalidParameters,
        ErrorMessage = msg
    };

    /// <summary> Buyer's available balance is below the order's required notional. </summary>
    public static OrderResult InsufficientFunds(string msg) => new()
    {
        Status = OrderStatus.InsufficientFunds,
        ErrorMessage = msg
    };

    /// <summary> Seller's available shares are below the order's quantity. </summary>
    public static OrderResult InsufficientStocks(string msg) => new()
    {
        Status = OrderStatus.InsufficientStocks,
        ErrorMessage = msg
    };

    /// <summary> Generic catch-all for unexpected failures during order processing. </summary>
    public static OrderResult OperationFailed(string? msg = null) => new()
    {
        Status = OrderStatus.OperationFailed,
        ErrorMessage = msg ?? "An unexpected error occurred."
    };

    /// <summary>
    /// Market or limit order could not be filled because no resting orders satisfied the
    /// taker's price constraint. Any partial fills are still returned for accounting.
    /// </summary>
    public static OrderResult NoLiquidity(Order order, List<Transaction> txs) => new()
    {
        PlacedOrder = order,
        FillTransactions = txs,
        Status = OrderStatus.NoLiquidity,
        ErrorMessage = order.IsBuyOrder
            ? "Not enough sell-side liquidity at or below your max price."
            : "Not enough buy-side liquidity at or above your min price."
    };

    /// <summary> Modify/cancel was attempted on an order that is no longer open. </summary>
    public static OrderResult AlreadyClosed() => new()
    {
        Status = OrderStatus.AlreadyClosed,
        ErrorMessage = "Order already closed."
    };
    #endregion

    #region Success Results
    /// <summary> Cancellation succeeded; returns the now-cancelled order for the UI. </summary>
    public static OrderResult Cancelled(Order order) => new()
    {
        PlacedOrder = order,
        Status = OrderStatus.Success,
        SuccessMessage = "Order successfully cancelled."
    };

    /// <summary>
    /// Order placement succeeded. Status and message are derived from how much filled
    /// and whether the remainder is now resting on the book.
    /// </summary>
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

    /// <summary>
    /// Modification succeeded. Same status logic as <see cref="Success"/> but with messages
    /// that mention the modification.
    /// </summary>
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
    #endregion
}
