using KieshStockExchange.Models;

namespace KieshStockExchange.Services.UserServices.Interfaces;

public interface IAuthService
{
    Task<bool> RegisterAsync(
        string username, string fullname,
        string email, string password, DateTime birthdate
    );
    Task LoginAsync(string username, string password);
    Task LogoutAsync(CancellationToken ct = default);
    bool IsLoggedIn { get; }
    bool IsAdmin { get; }
    User? CurrentUser { get; }
    int CurrentUserId { get; }
}
