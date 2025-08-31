using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services;

namespace KieshStockExchange.Services.Implementations;

public class AuthService : IAuthService
{
    
    private readonly IDataBaseService _db;
    public User? CurrentUser { get; private set; }
    public bool IsLoggedIn => CurrentUser != null;
    public bool IsAdmin => CurrentUser?.IsAdmin ?? false;

    public AuthService(IDataBaseService db)
    {
        _db = db;
    }

    public async Task<bool> RegisterAsync(
        string username, string fullname,
        string email, string password, DateTime birthdate
    ) {
        var user = new User
        {
            Username = username,
            FullName = fullname,
            Email = email,
            PasswordHash = SecurityHelper.HashPassword(password),
            BirthDate = birthdate
        };
        // Check for existing user
        if (await _db.GetUserByUsername(user.Username) != null)
            return false;

        // Check validity
        if (!user.IsValid()) 
            return false;

        // Save user
        await _db.CreateUser(user);
        return true;
    }

    public async Task LoginAsync(string username, string password)
    {
        var user = await _db.GetUserByUsername(username);
        if (user == null) 
            return;

        var hash = SecurityHelper.HashPassword(password);
        if (user.PasswordHash != hash) 
            return;

        CurrentUser = user;
    }

    public Task LogoutAsync()
    {
        CurrentUser = null;
        return Task.CompletedTask;
    }
}
