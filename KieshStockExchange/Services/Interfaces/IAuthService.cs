using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IAuthService
{
    Task<bool> RegisterAsync(
        string username, string fullname,
        string email, string password, DateTime birthdate
    );
    Task LoginAsync(string username, string password);
    Task LogoutAsync();
    bool IsLoggedIn { get; }
    User CurrentUser { get; }
}
