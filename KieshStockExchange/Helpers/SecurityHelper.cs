using System.Security.Cryptography;
using System.Text;

namespace KieshStockExchange.Helpers;

public static class SecurityHelper
{
    public static string HashPassword(string password)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        var hashedInput = HashPassword(password);
        return hashedInput == hash;
    }

    public static bool IsValidPassword(string password)
    {
        // Password must be at least 8 characters long
        return !string.IsNullOrWhiteSpace(password) && password.Length >= 8;
    }
}
