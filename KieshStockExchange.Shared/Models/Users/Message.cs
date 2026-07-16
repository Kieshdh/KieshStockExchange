using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Message : IValidatable
{
    public enum MessageType
    {
        Info,
        Warning,
        Error,
        Fill,
        PasswordReset
    }

    private int _messageId = 0;
    public int MessageId
    {
        get => _messageId;
        set
        {
            if (_messageId != 0 && value != _messageId) throw new InvalidOperationException("MessageId is immutable once set.");
            _messageId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    public MessageType Kind { get; set; } = MessageType.Info;
    public string KindString
    {
        get => Kind.ToString();
        set
        {
            var v = (value ?? string.Empty).Trim();
            if (Enum.TryParse<MessageType>(v, ignoreCase: true, out var parsedType))
                Kind = parsedType;
            else throw new ArgumentException($"Invalid message type: {value}");
        }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => _title = value?.Trim() ?? string.Empty;
    }

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set => _content = value?.Trim() ?? string.Empty;
    }

    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    private DateTime? _readAt;
    public DateTime? ReadAt
    {
        get => _readAt;
        set
        {
            if (_readAt.HasValue) throw new InvalidOperationException("ReadAt is immutable once set.");
            if (value.HasValue) _readAt = TimeHelper.EnsureUtc(value.Value);
        }
    }

    public bool IsValid() => UserId > 0 && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Content) && IsValidTimeStamps();

    public bool IsInvalid => !IsValid();

    public bool IsValidTimeStamps() =>
        CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc() &&
        (!ReadAt.HasValue || (ReadAt.Value >= CreatedAt && ReadAt.Value <= TimeHelper.NowUtc()));

    public override string ToString() => $"Message #{MessageId} {Title}: {Content}";

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string ReadAtDisplay => ReadAt.HasValue ? ReadAt.Value.ToLocalTime().ToString("MM-dd HH:mm") : "Unread";

    public bool IsRead => ReadAt.HasValue;
    public bool IsUnread => !IsRead;

    public void MarkAsRead()
    {
        if (IsRead) throw new InvalidOperationException("Message is already marked as read.");
        ReadAt = TimeHelper.NowUtc();
    }
}
