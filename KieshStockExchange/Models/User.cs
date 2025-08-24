using SQLite;
using System.Text.RegularExpressions;

namespace KieshStockExchange.Models
{
    [Table("Users")]
    public class User : IValidatable
    {
        #region Properties
        [PrimaryKey, AutoIncrement]
        [Column("UserId")] public int UserId { get; set; }

        [Column("Username")] public string Username { get; set; }

        [Column("PasswordHash")] public string PasswordHash { get; set; }

        [Column("Email")] public string Email { get; set; }

        [Column("FullName")] public string FullName { get; set; }

        [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

        [Column("Birthdate")] public DateTime? BirthDate { get; set; }

        [Column("IsAdmin")] public bool IsAdmin { get; set; } = false;
        #endregion

        public User() => CreatedAt = DateTime.UtcNow;

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

        public override string ToString() =>
            $"User #{UserId}: {Username} ({FullName})";

        #region Formatted Properties
        public string BirthDateFormatted =>
            BirthDate?.ToString("dd/MM/yyyy") ?? string.Empty;

        public string CreatedAtFormatted => 
            CreatedAt.ToString("dd/MM/yyyy HH:mm:ss") ?? string.Empty;
        #endregion
    }
}
