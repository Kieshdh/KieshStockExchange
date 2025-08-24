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
    Task<bool> RefreshOrdersAsync();

    /// <summary>Cancels one of the user's open orders.</summary>
    Task<OrderResult> CancelOrderAsync(int orderId);

    /// <summary>Places a limit‐buy order.</summary>
    Task<OrderResult> PlaceLimitBuyOrderAsync(int stockId, int quantity, decimal limitPrice);

    /// <summary>Places a limit‐sell order.</summary>
    Task<OrderResult> PlaceLimitSellOrderAsync(int stockId, int quantity, decimal limitPrice);

    /// <summary>Places a market‐buy order, capping at maxPrice.</summary>
    Task<OrderResult> PlaceMarketBuyOrderAsync(int stockId, int quantity);

    /// <summary>Places a market‐sell order, floor at minPrice.</summary>
    Task<OrderResult> PlaceMarketSellOrderAsync(int stockId, int quantity);
}
