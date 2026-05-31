namespace KieshStockExchange.Models;

public enum NotificationSeverity { Info, Success, Warning, Error }

public sealed class Notification
{
    /// <summary>Client-side identity used for toast dismiss timers. Always unique.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Server Messages row id when this notification was persisted server-side.
    /// 0 for local-only toasts (validation errors, optimistic feedback) that are
    /// never persisted and so can't be marked read on the server.
    /// </summary>
    public int MessageId { get; init; }

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string Title { get; init; }
    public required string Message { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;
    public bool IsRead { get; set; }
}
