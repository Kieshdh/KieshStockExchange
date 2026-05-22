using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class UserEditViewModel : BaseViewModel
{
    #region Fields, events and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<UserEditViewModel> _logger;

    private User? _original;

    public event EventHandler? CloseRequested;
    public event EventHandler? Saved;
    #endregion

    #region Bound state
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _fullName = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _birthDateText = string.Empty;
    [ObservableProperty] private bool _isAdmin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    #endregion

    public UserEditViewModel(IDataBaseService db, ILogger<UserEditViewModel> logger)
    {
        Title = "Edit user";
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Initialize and commands
    public void Initialize(User user)
    {
        _original = user ?? throw new ArgumentNullException(nameof(user));
        UserId = user.UserId;
        Username = user.Username;
        FullName = user.FullName;
        Email = user.Email;
        IsAdmin = user.IsAdmin;
        BirthDateText = user.BirthDate?.ToLocalTime().ToString("dd/MM/yyyy") ?? string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_original is null) return;
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(FullName)
            || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(BirthDateText))
        {
            ErrorMessage = "All fields are required.";
            return;
        }

        if (!DateTime.TryParseExact(BirthDateText, "dd/MM/yyyy", null,
                System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            ErrorMessage = "Birthdate must be in dd/MM/yyyy format.";
            return;
        }

        // Stage edits on a clone; original stays untouched on validation failure.
        var draft = new User
        {
            UserId = _original.UserId,
            PasswordHash = _original.PasswordHash,
            CreatedAt = _original.CreatedAt,
            Username = Username.Trim(),
            FullName = FullName.Trim(),
            Email = Email.Trim(),
            BirthDate = TimeHelper.EnsureUtc(parsedDate),
            IsAdmin = IsAdmin,
        };

        if (!draft.IsValidUsername())  { ErrorMessage = "Username must be alphanumeric and 5–20 characters."; return; }
        if (!draft.IsValidName())      { ErrorMessage = "Full name is invalid (3–100 letters, spaces and . , ' -)."; return; }
        if (!draft.IsValidEmail())     { ErrorMessage = "Email format is invalid."; return; }
        if (!draft.IsValidBirthdate()) { ErrorMessage = "User must be at least 18 years old."; return; }

        // Friendlier than letting the unique-index throw on save.
        if (!string.Equals(draft.Username, _original.Username, StringComparison.OrdinalIgnoreCase))
        {
            var existing = await _db.GetUserByUsername(draft.Username).ConfigureAwait(false);
            if (existing is not null && existing.UserId != draft.UserId)
            {
                ErrorMessage = $"Username '{draft.Username}' is already taken.";
                return;
            }
        }

        IsBusy = true;
        try
        {
            await _db.UpsertUser(draft).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserEditViewModel: save failed for user #{UserId}", draft.UserId);
            ErrorMessage = "Save failed. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Saved?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
    #endregion
}
