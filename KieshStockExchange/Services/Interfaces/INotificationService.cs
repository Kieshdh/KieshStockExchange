using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface INotificationService
{
    /// <summary>
    /// Route an OrderResult to a human-friendly notification
    /// (placed, on-book, partial, filled, or failed).
    /// </summary>
    Task NotifyOrderResultAsync(OrderResult result, CancellationToken ct = default);

    /// <summary>
    /// Notify when a resting order receives a (partial) fill later on.
    /// Call this per fill (maker or taker), passing the post-fill Order state and the Transaction tick.
    /// </summary>
    Task NotifyFillAsync(Order orderAfterFill, Transaction fill, CancellationToken ct = default);

    /// <summary>
    /// Generic “push” (UI alert). Internally synchronized by a gate so alerts never overlap.
    /// </summary>
    Task PushNotificationAsync(string title, string message, CancellationToken ct = default);
}
