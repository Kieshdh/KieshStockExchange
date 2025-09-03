using KieshStockExchange.Services;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.Implementations;

public class UserSessionService : IUserSessionService
{
    public string UserName { get; } = string.Empty;
    public int UserId { get; } = 0;
    public bool IsAuthenticated { get; } = false;
    public string FullName { get; } = string.Empty;
    public bool KeepLoggedIn { get; } = false;
    public int CurrentStockId { get; } = 0;
}
