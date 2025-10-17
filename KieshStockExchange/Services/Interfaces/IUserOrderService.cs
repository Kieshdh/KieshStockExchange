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
    IReadOnlyList<Order> UserCancelledOrders { get; }
    IReadOnlyList<Order> UserFilledOrders { get; }

    /// <summary>Reloads the user's orders from the back‐end.</summary>
    Task<bool> RefreshOrdersAsync(int? asUserId = null, CancellationToken ct = default);

    /// <summary>Cancels one of the user's open orders.</summary>
    Task<OrderResult> CancelOrderAsync(int orderId, 
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>Places a limit‐buy order.</summary>
    Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int quantity, 
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);

    /// <summary>Places a limit‐sell order.</summary>
    Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int quantity, 
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);

    /// <summary>Places a market‐buy order, capping at maxPrice.</summary>
    Task<OrderResult> PlaceMarketBuyOrderAsync(int stockId, int quantity, 
        decimal maxPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);

    /// <summary>Places a market‐sell order, floor at minPrice.</summary>
    Task<OrderResult> PlaceMarketSellOrderAsync(int stockId, int quantity, 
        decimal minPrice, CurrencyType currency, CancellationToken ct = default, int? asUserId = null);
}
