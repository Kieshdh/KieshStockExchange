using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.UserServices.Interfaces;

public interface IProfileService
{
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword);
    Task<bool> ChangeEmailAsync(string newEmail, string currentPassword);
    Task<bool> ChangeUsernameAsync(string newUsername, string currentPassword);
    Task UpdateBaseCurrencyAsync(CurrencyType newCurrency);
}
