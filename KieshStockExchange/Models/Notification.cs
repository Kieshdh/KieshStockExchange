namespace KieshStockExchange.Models;

public enum NotificationSeverity { Info, Success, Warning, Error }

public sealed class Notification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string Title { get; init; }
    public required string Message { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;
    public bool IsRead { get; set; }
}
