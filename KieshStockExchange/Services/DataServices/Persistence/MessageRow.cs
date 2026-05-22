using SQLite;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Messages")]
public class MessageRow
{
    [PrimaryKey, AutoIncrement]
    [Column("MessageId")] public int MessageId { get; set; }

    [Indexed(Name = "IX_Messages_User_Read", Order = 1)]
    [Column("UserId")] public int UserId { get; set; }

    [Column("Kind")] public string Kind { get; set; } = Message.MessageType.Info.ToString();

    [Column("Title")] public string Title { get; set; } = string.Empty;

    [Column("Content")] public string Content { get; set; } = string.Empty;

    [Indexed(Name = "IX_Messages_Created")]
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Indexed(Name = "IX_Messages_User_Read", Order = 2)]
    [Column("ReadAt")] public DateTime? ReadAt { get; set; }
}

public static class MessageMapper
{
    public static Message ToDomain(MessageRow r) => new()
    {
        MessageId = r.MessageId,
        UserId = r.UserId,
        KindString = r.Kind,
        Title = r.Title,
        Content = r.Content,
        CreatedAt = r.CreatedAt,
        ReadAt = r.ReadAt,
    };

    public static MessageRow ToRow(Message m) => new()
    {
        MessageId = m.MessageId,
        UserId = m.UserId,
        Kind = m.KindString,
        Title = m.Title,
        Content = m.Content,
        CreatedAt = m.CreatedAt,
        ReadAt = m.ReadAt,
    };
}
