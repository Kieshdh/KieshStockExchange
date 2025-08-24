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
}
