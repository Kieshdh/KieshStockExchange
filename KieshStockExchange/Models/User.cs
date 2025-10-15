using SQLite;
using System.Text.RegularExpressions;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Users")]
public class User : IValidatable
{
    #region Properties
    private int _userId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("UserId")] public int UserId { 
        get => _userId; 
        set {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value < 0 ? 0 : value;
        }
    }

    private string _username = string.Empty;
    [Indexed(Unique = true)]
    [Column("Username")] public string Username { 
        get => _username; 
        set => _username = value.ToLowerInvariant().Trim();
    }

    private string _password = string.Empty;
    [Column("PasswordHash")] public string PasswordHash { 
        get => _password; 
        set => _password = value;
    }

    private string _email = string.Empty;
    [Indexed(Unique = true)]
    [Column("Email")] public string Email { 
        get => _email; 
        set => _email = value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    [Column("FullName")] public string FullName { get; set; } = string.Empty;

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Column("CreatedAt")] public DateTime CreatedAt {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    private DateTime? _birthDate = null;
    [Column("BirthDate")] public DateTime? BirthDate {
        get => _birthDate;
        set
        {
            if (value == null)
            {
                _birthDate = null;
                return;
            }
            if (value.HasValue && value.Value > TimeHelper.NowUtc())
                throw new ArgumentException("BirthDate cannot be in the future.");
            _birthDate = TimeHelper.EnsureUtc(value.Value);
        }
    }

    private bool _isAdmin = false;
    [Column("IsAdmin")] public bool IsAdmin { 
        get => _isAdmin; 
        set => _isAdmin = value;
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => IsValidEmail() && IsValidUsername() 
        && IsValidBirthdate() && IsValidName() && IsValidPassword(PasswordHash);
    
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
        // Username must be alphanumeric and between 5 to 20 characters
        string pattern = @"^[a-zA-Z0-9]{5,20}$";
        return Regex.IsMatch(Username, pattern);
    }

    public bool IsValidPassword(string password) => 
        SecurityHelper.IsValidPassword(password);
    
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
        // Birthdate must be a valid date and at least 18 years old
        return BirthDate.HasValue && BirthDate.Value > DateTime.MinValue && 
               BirthDate.Value <= TimeHelper.NowUtc().AddYears(-18);
    }
    #endregion

    #region String Representations
    public override string ToString() =>
        $"User #{UserId}: {Username} ({FullName})";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string BirthDateDisplay =>  BirthDate?.ToString("dd/MM/yyyy") ?? "N/A";
    #endregion
}
