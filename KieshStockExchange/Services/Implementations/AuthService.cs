using ExcelDataReader.Log;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public class AuthService : IAuthService
{
    
    private readonly IDataBaseService _db;
    private readonly ILogger<AuthService> _logger;

    public User CurrentUser { get; private set; } = new();
    public bool IsLoggedIn => CurrentUser.UserId != 0;
    public bool IsAdmin => CurrentUser.IsAdmin;

    public AuthService(IDataBaseService db, ILogger<AuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> RegisterAsync(
        string username, string fullname,
        string email, string password, DateTime birthdate
    ) {
        // Check for existing user
        if (await _db.GetUserByUsername(username) != null)
            return false;
        if (await _db.GetUsersAsync().ContinueWith(t =>
             t.Result.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))) 
            return false;

        // Create user
        var user = new User
        {
            Username = username,
            FullName = fullname,
            Email = email,
            PasswordHash = SecurityHelper.HashPassword(password),
            BirthDate = birthdate
        };

        // Check validity
        if (!user.IsValid()) 
            return false;

        // Save user
        await _db.CreateUser(user);

        _logger.LogInformation("New user registered: #{UserId} {Username}", user.UserId, user.Username);

        //CurrentUser = user;
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

    public async Task LogoutAsync()
    {
        CurrentUser = new User();
    }
}
