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

    public User? CurrentUser { get; private set; } = null;
    public bool IsLoggedIn => CurrentUser != null;
    public bool IsAdmin => IsLoggedIn && CurrentUser!.IsAdmin;

    public AuthService(IDataBaseService db, ILogger<AuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> RegisterAsync(string username, string fullname,
        string email, string password, DateTime birthdate) 
    {
        // Check for existing user
        if (await _db.GetUserByUsername(username) != null)
            return false;
        if (await _db.GetUsersAsync().ContinueWith(t =>
             t.Result.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))) 
            return false;

        // Check valid password
        if (!SecurityHelper.IsValidPassword(password))
            return false;

        // Create user
        var user = new User
        {
            Username = username,
            FullName = fullname,
            Email = email,
            PasswordHash = SecurityHelper.HashPassword(password),
            BirthDate = birthdate,
            IsAdmin = true, // Humans always admin
        };

        // Check validity
        if (!user.IsValid()) 
            return false;

        // Save user
        await _db.CreateUser(user);

        _logger.LogInformation("New user registered: #{UserId} {Username}", user.UserId, user.Username);

        return true;
    }

    public async Task LoginAsync(string username, string password)
    {
        // Check if already logged in
        if (IsLoggedIn) return;
        // Basic checks
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) 
            return;
        // Get the user
        var user = await _db.GetUserByUsername(username);
        if (user == null) 
            return;
        // Verify password
        if (!SecurityHelper.VerifyPassword(password, user.PasswordHash))
            return;
        // Set current user
        CurrentUser = user;

        _logger.LogInformation("User logged in: #{UserId} {Username}", user.UserId, user.Username);
    }

    public async Task LogoutAsync()
    {
        CurrentUser = null;
        await Task.CompletedTask;
    }
}
