using SQLite;
using System.Text.RegularExpressions;

namespace KieshStockExchange.Models;

[Table("Users")]
public class User : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("UserId")] public int UserId { get; set; } = 0;

    [Column("Username")] public string Username { get; set; } = string.Empty;

    [Column("PasswordHash")] public string PasswordHash { get; set; } = string.Empty;

    [Column("Email")] public string Email { get; set; } = string.Empty;

    [Column("FullName")] public string FullName { get; set; } = string.Empty;

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("Birthdate")] public DateTime? BirthDate { get; set; } = null;

    [Column("IsAdmin")] public bool IsAdmin { get; set; } = false;
    #endregion

    #region IValidatable Implementation
    public bool IsValid() =>
        IsValidEmail() && IsValidUsername() && IsValidBirthdate() &&
        IsValidPassword(PasswordHash) && IsValidName();
    
    public bool IsValidEmail()
    {
        if (string.IsNullOrWhiteSpace(Email))
            return false;

        string pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
        return Regex.IsMatch(Email, pattern, RegexOptions.IgnoreCase);
    }

    public bool IsValidUsername()
    {
        if (string.IsNullOrWhiteSpace(Username))
            return false;
        // Username must be alphanumeric and between 3 to 20 characters
        string pattern = @"^[a-zA-Z0-9]{5,20}$";
        return Regex.IsMatch(Username, pattern);
    }
    
    public bool IsValidPassword(string password)
    {
        // Password must be at least 8 characters long
        return !string.IsNullOrWhiteSpace(password) && password.Length >= 8;
    }
    
    public bool IsValidName()
    {
        // Name must not be empty and can contain letters, spaces, and some punctuation
        if (string.IsNullOrWhiteSpace(FullName))
            return false;
        string pattern = @"^[a-zA-Z\s.,'-]{3,100}$";
        return Regex.IsMatch(FullName, pattern);
    }

    public bool IsValidBirthdate()
    {
        // Birthdate must be a valid date and not in the future
        return BirthDate.HasValue && BirthDate.Value <= DateTime.UtcNow;
    }
    #endregion

    #region String Representations
    public override string ToString() =>
        $"User #{UserId}: {Username} ({FullName})";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string BirthDateDisplay =>  BirthDate?.ToString("dd/MM/yyyy") ?? "N/A";
    #endregion
}
