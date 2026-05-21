using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.OtherServices.Interfaces;

public interface INotificationService
{
    /// <summary> Raised when a new Notification is appended to the ring buffer. </summary>
    event EventHandler<Notification>? NotificationAdded;

    /// <summary> Snapshot of the most recent notifications (newest first). </summary>
    IReadOnlyList<Notification> Recent { get; }

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
    /// Push a custom notification with caller-specified severity.
    /// </summary>
    Task PushNotificationAsync(string title, string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        CancellationToken ct = default);

    /// <summary> Clear the inbox ring buffer. </summary>
    void Clear();
}
