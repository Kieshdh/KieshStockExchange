using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

 public interface IUserOrderService
{
    /// <summary>
    /// In‐memory cache of all user orders; call RefreshOrdersAsync before reading.
    /// </summary>
    List<Order> UserAllOrders { get; }
    IReadOnlyList<Order> UserOpenOrders { get; }
    IReadOnlyList<Order> UserClosedOrders { get; }
    IReadOnlyList<Order> UserCancelledOrders { get; }
    IReadOnlyList<Order> UserFilledOrders { get; }

    event EventHandler? OrdersChanged;

    /// <summary>Reloads the user's orders from the back‐end.</summary>
    Task<bool> RefreshOrdersAsync(int? asUserId = null, CancellationToken ct = default);

    /// <summary>Cancels one of the user's open orders.</summary>
    Task<OrderResult> CancelOrderAsync(int orderId, 
        int? asUserId = null, CancellationToken ct = default);

    /// <summary> Modifies an existing open order's quantity and/or price. </summary>
    Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, int? asUserId = null, CancellationToken ct = default);

    /// <summary>Places a limit‐buy order.</summary>
    Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int quantity, 
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);

    /// <summary>Places a limit‐sell order.</summary>
    Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int quantity, 
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);

    /// <summary>Places a market‐buy order with true market pricing.</summary>
    Task<OrderResult> PlaceTrueMarketBuyAsync(int stockId, int quantity, 
        CurrencyType currency, int? asUserId = null, CancellationToken ct = default);

    /// <summary>Places a market‐sell order with true market pricing.</summary>
    Task<OrderResult> PlaceTrueMarketSellAsync(int stockId, int quantity, 
        CurrencyType currency, int? asUserId = null, CancellationToken ct = default);

    /// <summary>Places a market‐buy order with slippage protection.</summary>
    Task<OrderResult> PlaceSlippageMarketBuyAsync(int stockId, int quantity, decimal anchorPrice, 
        decimal slippagePercent, CurrencyType currency, int? asUserId = null, CancellationToken ct = default);

    /// <summary>Places a market‐sell order with slippage protection.</summary>
    Task<OrderResult> PlaceSlippageMarketSellAsync(int stockId, int quantity, decimal anchorPrice,
         decimal slippagePercent, CurrencyType currency, int? asUserId = null, CancellationToken ct = default);

}
