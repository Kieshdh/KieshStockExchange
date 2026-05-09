using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.UserServices.Interfaces;

public interface IProfileService
{
    Task<bool> ChangePasswordAsync(string currentPassword, string newPassword);
    Task<bool> ChangeEmailAsync(string newEmail, string currentPassword);
    Task<bool> ChangeUsernameAsync(string newUsername, string currentPassword);

    /// <summary>Update base currency in session AND persist to the user's preferences row.</summary>
    Task UpdateBaseCurrencyAsync(CurrencyType newCurrency);

    /// <summary>Apply a theme key via <see cref="OtherServices.Interfaces.IThemeService"/> and persist.</summary>
    Task UpdateThemeAsync(string themeKey);

    /// <summary>Update default candle resolution in session and persist.</summary>
    Task UpdateDefaultCandleResolutionAsync(CandleResolution resolution);

    /// <summary>
    /// Load the user's persisted preferences from the database and apply them to the
    /// session + theme service. Falls back to canonical defaults when the row is missing
    /// or any DB call fails (does not throw).
    /// </summary>
    Task LoadPreferencesAsync(int userId, CancellationToken ct = default);
}
