
namespace KieshStockExchange.Services.UserServices;

public interface ProfileService
{   Task<bool> UpdateProfileAsync(
        string username, string fullname,
        string email, DateTime? birthdate
    );

    Task<bool> ChangePasswordAsync(string oldPassword, string newPassword);

    Task<bool> DeleteAccountAsync();

    bool IsProfileComplete { get; }
}
