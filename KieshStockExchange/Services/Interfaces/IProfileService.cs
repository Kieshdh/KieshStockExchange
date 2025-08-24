using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Services;

public interface IProfileService
{   Task<bool> UpdateProfileAsync(
        string username, string fullname,
        string email, DateTime? birthdate
    );
    Task<bool> ChangePasswordAsync(string oldPassword, string newPassword);
    Task<bool> DeleteAccountAsync();
    bool IsProfileComplete { get; }
}
