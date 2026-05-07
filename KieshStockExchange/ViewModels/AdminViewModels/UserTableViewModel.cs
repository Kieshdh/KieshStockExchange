using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class UserTableViewModel : BaseTableViewModel<UserTableObject>
{
    [ObservableProperty] private string _idFilter = string.Empty;

    partial void OnIdFilterChanged(string value)
    {
        CurrentFilter = string.IsNullOrWhiteSpace(value) ? null : value;
        _ = ApplyViewChange();
    }

    public UserTableViewModel(IDataBaseService dbService, ILogger<UserTableViewModel> logger)
        : base(dbService, logger)
    {
        Title = "Users";
        SortKey = "CreatedAt";
        SortDesc = true;
    }

    protected override async Task<(IReadOnlyList<UserTableObject> Items, int Total)> LoadPageAsync(
        int skip, int take, string? sortKey, bool desc, string? filter, CancellationToken ct)
    {
        var (users, total) = await _db.GetUsersPageAsync(skip, take, sortKey ?? "CreatedAt", desc, filter, ct);
        var rows = users.Select(u => new UserTableObject(_db, u)).ToList();
        return (rows, total);
    }
}

public partial class UserTableObject : ObservableObject
{
    #region Bindable properties
    [ObservableProperty] private bool _isEditing = false;
    [ObservableProperty] private bool _isViewing = true;
    [ObservableProperty] private string _editText = "Edit";
    [ObservableProperty] private string _birthDateText = string.Empty;

    public User User { get; private set; }
    #endregion

    #region Other properties and Constructor
    public IAsyncRelayCommand ChangeEditCommand { get; }

    private readonly IDataBaseService _db;

    public UserTableObject( IDataBaseService db, User user)
    {
        _db = db;
        ChangeEditCommand = new AsyncRelayCommand(ChangeEdit);

        User = user;
        SetBirthDateText();
    }
    #endregion

    #region Methods
    private async Task ChangeEdit()
    {
        if (IsViewing)
        {
            EditText = "Save";
            IsViewing = false;
            IsEditing = true;
            return;
        }

        var saved = await SaveAsync();
        if (!saved)
        {
            await ResetAsync();
            SetBirthDateText();
            return;
        }

        EditText = "Edit";
        IsViewing = true;
        IsEditing = false;
        SetBirthDateText();
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            if (!VerifyUser()) return false;
            if (User.IsInvalid)
            {
                Debug.WriteLine($"User #{User.UserId} is invalid.");
                return false;
            }
            await _db.UpsertUser(User);
            Debug.WriteLine($"User #{User.UserId} saved successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving user #{User.UserId}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ResetAsync()
    {
        try
        {
            var user = await _db.GetUserById(User.UserId);
            if (user == null)
            {
                Debug.WriteLine($"User #{User.UserId} no longer exists.");
                return false;
            }
            User = user;
            OnPropertyChanged(nameof(User));
            Debug.WriteLine($"User #{User.UserId} reverted successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error resetting user #{User.UserId}: {ex.Message}");
            return false;
        }
    }

    private void SetBirthDateText()
    {
        BirthDateText = User.BirthDate.HasValue
            ? User.BirthDate.Value.ToString("dd/MM/yyyy")
            : string.Empty;
    }

    private bool VerifyUser()
    {
        if (string.IsNullOrWhiteSpace(BirthDateText) || string.IsNullOrWhiteSpace(User.Email) ||
            string.IsNullOrWhiteSpace(User.Username) || string.IsNullOrWhiteSpace(User.FullName))
        {
            Debug.WriteLine("One or more required fields are empty.");
            return false;
        }

        if (!DateTime.TryParseExact(BirthDateText, "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            Debug.WriteLine("Invalid birth date format.");
            return false;
        }
        User.BirthDate = TimeHelper.EnsureUtc(parsedDate);
        if (!User.IsValidBirthdate())
        {
            Debug.WriteLine("Birth date is not valid (must be at least 18 years old and not in the future).");
            return false;
        }

        if (!User.IsValidEmail())
        {
            Debug.WriteLine("Invalid email format.");
            return false;
        }

        if (!User.IsValidUsername())
        {
            Debug.WriteLine("Invalid username format (must be alphanumeric and 5-20 characters).");
            return false;
        }

        if (!User.IsValidName())
        {
            Debug.WriteLine("Invalid full name format (3-100 characters, letters and some punctuation).");
            return false;
        }

        return true;
    }
    #endregion
}
