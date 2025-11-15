using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Messages")]
public class Message : IValidatable
{
    #region Constants
    public enum MessageType
    {
        Info,
        Warning,
        Error,
        Fill,
        PasswordReset
    }
    #endregion

    #region Properties
    private int _messageId = 0; 
    [PrimaryKey, AutoIncrement]
    [Column("MessageId")] public int MessageId
    {
        get => _messageId;
        set {
            if (_messageId != 0 && value != _messageId) throw new InvalidOperationException("MessageId is immutable once set.");
            _messageId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    [Indexed(Name = "IX_Messages_User_Read", Order = 1)]
    [Column("UserId")] public int UserId
    {
        get => _userId;
        set {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    [Ignore] public MessageType Kind { get; set; } = MessageType.Info;
    [Column("Kind")] public string KindString
    {
        get => Kind.ToString();
        set {
            var v = (value ?? string.Empty).Trim();
            if (Enum.TryParse<MessageType>(v, ignoreCase: true, out var parsedType))
                Kind = parsedType;
            else throw new ArgumentException($"Invalid message type: {value}");
        }
    }

    private string _title = string.Empty;
    [Column("Title")] public string Title
    {
        get => _title;
        set => _title = value?.Trim() ?? string.Empty;
    }

    private string _content = string.Empty;
    [Column("Content")] public string Content
    {
        get => _content;
        set => _content = value?.Trim() ?? string.Empty;
    }

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Indexed(Name = "IX_Messages_Created")]
    [Column("CreatedAt")] public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    private DateTime? _readAt;
    [Indexed(Name = "IX_Messages_User_Read", Order = 2)]
    [Column("ReadAt")] public DateTime? ReadAt
    {
        get => _readAt;
        set {
            if (_readAt.HasValue) throw new InvalidOperationException("ReadAt is immutable once set.");
            if (value.HasValue) _readAt = TimeHelper.EnsureUtc(value.Value);
        }
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && !string.IsNullOrWhiteSpace(Title) 
        && !string.IsNullOrWhiteSpace(Content) && IsValidTimeStamps();

    public bool IsInvalid => !IsValid();

    public bool IsValidTimeStamps() =>
        CreatedAt > DateTime.MinValue && CreatedAt <= TimeHelper.NowUtc() &&
        (!ReadAt.HasValue || (ReadAt.Value >= CreatedAt && ReadAt.Value <= TimeHelper.NowUtc()));

    #endregion

    #region String Representations
    public override string ToString() => $"Message #{MessageId} {Title}: {Content}";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    [Ignore] public string ReadAtDisplay => ReadAt.HasValue ? ReadAt.Value.ToLocalTime().ToString("MM-dd HH:mm") : "Unread";
    #endregion

    #region Helpers
    [Ignore] public bool IsRead => ReadAt.HasValue;
    [Ignore] public bool IsUnread => !IsRead;

    public void MarkAsRead()
    {
        if (IsRead) throw new InvalidOperationException("Message is already marked as read.");
        ReadAt = TimeHelper.NowUtc();
    }
    #endregion
}
