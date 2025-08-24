using KieshStockExchange.Services;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.Implementations;

public class UserSessionService : IUserSessionService
{
    public string UserName { get; }
    public int UserId { get; }
    public bool IsAuthenticated { get; }
    public bool IsAdmin { get; }
    public string FullName { get; }
    public bool KeepLoggedIn { get; }
    public int CurrentStockId { get; }
}
