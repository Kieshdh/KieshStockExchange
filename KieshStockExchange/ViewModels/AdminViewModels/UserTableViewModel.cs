using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using System.Diagnostics;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.AdminViewModels;

public partial class UserTableViewModel : BaseTableViewModel<UserTableObject>
{
    public UserTableViewModel(IDataBaseService dbService) : base(dbService)
    {
        Title = "Users"; // from BaseViewModel
    }

    protected override async Task<List<UserTableObject>> LoadItemsAsync()
    {
        IsBusy = true;
        try
        {
            var rows = new List<UserTableObject>();

            // Fetch all data
            var users = await _db.GetUsersAsync();

            // Convert to table objects
            foreach (var user in users)
                rows.Add(new UserTableObject(_db, user));

            // Sort by most recent first
            rows = rows.OrderByDescending(r => r.User.CreatedAt).ToList();
            Debug.WriteLine($"The User table has {rows.Count} users.");
            return rows;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading users: {ex.Message}");
            return new List<UserTableObject>();
        }
        finally { IsBusy = false; }
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

        // Attempt to save changes
        var saved = await SaveAsync();
        if (!saved) // If save failed, revert changes
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
            // Validate user data
            if (!VerifyUser()) return false;
            // Extra safety check, should not happen
            if (User.IsInvalid) 
            {
                Debug.WriteLine($"User #{User.UserId} is invalid.");
                return false;
            }

            // Save to database
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
        // Verify Empty text
        if (string.IsNullOrWhiteSpace(BirthDateText) || string.IsNullOrWhiteSpace(User.Email) ||
            string.IsNullOrWhiteSpace(User.Username) || string.IsNullOrWhiteSpace(User.FullName))
        {
            Debug.WriteLine("One or more required fields are empty.");
            return false;
        }

        // Verify birth date
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

        // Verify email
        if (!User.IsValidEmail())
        {
            Debug.WriteLine("Invalid email format.");
            return false;
        }

        // Verify username
        if (!User.IsValidUsername())
        {
            Debug.WriteLine("Invalid username format (must be alphanumeric and 5-20 characters).");
            return false;
        }

        // Verify full name
        if (!User.IsValidName())
        {
            Debug.WriteLine("Invalid full name format (3-100 characters, letters and some punctuation).");
            return false;
        }

        // All checks passed
        return true;
    }
    #endregion
}
