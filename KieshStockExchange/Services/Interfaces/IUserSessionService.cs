
namespace KieshStockExchange.Services;

public interface IUserSessionService
{
    string UserName { get; }
    int UserId { get; }
    bool IsAuthenticated { get; }
    string FullName { get; }
    bool KeepLoggedIn { get; }
    int CurrentStockId { get; }
}
