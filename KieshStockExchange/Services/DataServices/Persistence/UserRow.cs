using SQLite;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Users")]
public class UserRow
{
    [PrimaryKey, AutoIncrement]
    [Column("UserId")] public int UserId { get; set; }

    [Indexed(Unique = true)]
    [Column("Username")] public string Username { get; set; } = string.Empty;

    [Column("PasswordHash")] public string PasswordHash { get; set; } = string.Empty;

    [Indexed(Unique = true)]
    [Column("Email")] public string Email { get; set; } = string.Empty;

    [Column("FullName")] public string FullName { get; set; } = string.Empty;

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Column("BirthDate")] public DateTime? BirthDate { get; set; }

    [Column("IsAdmin")] public bool IsAdmin { get; set; }
}

public static class UserMapper
{
    public static User ToDomain(UserRow r) => new()
    {
        UserId = r.UserId,
        Username = r.Username,
        PasswordHash = r.PasswordHash,
        Email = r.Email,
        FullName = r.FullName,
        CreatedAt = r.CreatedAt,
        BirthDate = r.BirthDate,
        IsAdmin = r.IsAdmin,
    };

    public static UserRow ToRow(User u) => new()
    {
        UserId = u.UserId,
        Username = u.Username,
        PasswordHash = u.PasswordHash,
        Email = u.Email,
        FullName = u.FullName,
        CreatedAt = u.CreatedAt,
        BirthDate = u.BirthDate,
        IsAdmin = u.IsAdmin,
    };
}
