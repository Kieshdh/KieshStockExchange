using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Services;

namespace KieshStockExchange.ViewModels.UserViewModels;

public partial class RegisterViewModel : BaseViewModel
{
    // Input properties + validation flags/errors
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _isUsernameInvalid = false;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private bool _isEmailInvalid = false;

    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isPasswordInvalid = false;

    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _isConfirmPasswordInvalid = false;

    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private bool _isFirstNameInvalid = false;

    [ObservableProperty] private string _middleName = string.Empty;
    [ObservableProperty] private bool _isMiddleNameInvalid = false;

    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private bool _isLastNameInvalid = false;

    [ObservableProperty] private string _birthDay = string.Empty;
    [ObservableProperty] private string _birthMonth = string.Empty;
    [ObservableProperty] private string _birthYear = string.Empty;
    [ObservableProperty] private bool _isBirthDateInvalid = false;

    private string FullName => 
        $"{FirstName.Trim()} {MiddleName.Trim()} {LastName.Trim()}";
    private DateTime BirthDate => new DateTime(
        int.Parse(BirthYear), 
        MonthOptions.IndexOf(BirthMonth) + 1, 
        int.Parse(BirthDay)
    );

    public ObservableCollection<string> DayOptions { get; set; }
        = new ObservableCollection<string>();
    public ObservableCollection<string> MonthOptions { get; set; }
        = new ObservableCollection<string>();
    public ObservableCollection<string> YearOptions { get; set; } 
        = new ObservableCollection<string>();

    private readonly IAuthService _authService;
    private readonly INavigation _navigation;

    public RegisterViewModel(INavigation navigation, IAuthService authService)
    {
        Title = "Register";
        _authService = authService; 
        _navigation = navigation;

        InitilizeBirtdateVariables();
    }

    partial void OnUsernameChanged(string value) => ValidateUsername();
    private void ValidateUsername()
    {
        var user = new User { Username = Username };
        IsUsernameInvalid = !user.IsValidUsername();
    }

    partial void OnEmailChanged(string value) => ValidateEmail();
    private void ValidateEmail()
    {
        var user = new User { Email = Email };
        IsEmailInvalid = !user.IsValidEmail();
    }

    partial void OnPasswordChanged(string value) => ValidatePasswords();
    partial void OnConfirmPasswordChanged(string value) => ValidatePasswords();
    private void ValidatePasswords()
    {
        IsPasswordInvalid = (Password?.Length ?? 0) < 8;
        IsConfirmPasswordInvalid = Password != ConfirmPassword;
    }

    partial void OnFirstNameChanged(string value) => ValidateFirstName();
    private void ValidateFirstName()
    {
        var user = new User { FullName = FirstName };
        IsFirstNameInvalid = !user.IsValidName();
    }

    partial void OnLastNameChanged(string value) => ValidateLastName();
    private void ValidateLastName()
    {
        var user = new User { FullName = LastName };
        IsLastNameInvalid = !user.IsValidName();
    }

    private void ValidateFullName()
    {
        ValidateFirstName();
        ValidateLastName();

        if (IsFirstNameInvalid || IsLastNameInvalid)
            return;

        var user = new User { FullName = FullName };
        IsMiddleNameInvalid = !user.IsValidName() && MiddleName.Length > 0;
    }

    partial void OnBirthMonthChanged(string value) => UpdateDayOptions();
    partial void OnBirthYearChanged(string value) => UpdateDayOptions();
    private void UpdateDayOptions()
    {
        int daysInMonth;
        try
        {
            daysInMonth = DateTime.DaysInMonth(
            int.Parse(BirthYear), MonthOptions.IndexOf(BirthMonth) + 1);
        }
        catch { daysInMonth = 31; }

        int CurrentDay;
        try { CurrentDay = int.Parse(BirthDay); }
        catch { CurrentDay = 1; }

        DayOptions.Clear();
        for (int d = 1; d <= daysInMonth; d++)
            DayOptions.Add(d.ToString());
        BirthDay = Math.Min(CurrentDay, daysInMonth).ToString();
    }

    private void ValidateBirthdate()
    {
        IsBirthDateInvalid = !DateTime.TryParse(
            $"{BirthMonth} {BirthDay}, {BirthYear}", out _);
    }

    private bool ValidateAll()
    {
        ValidateUsername(); ValidateFullName();
        ValidateEmail(); ValidatePasswords();; ValidateBirthdate();

        return !(IsUsernameInvalid || IsEmailInvalid || 
            IsPasswordInvalid || IsConfirmPasswordInvalid || 
            IsBirthDateInvalid || IsLastNameInvalid );
    }

    [RelayCommand] private async Task ExecuteRegister()
    {
        // Validate all inputs before proceeding
        if (!ValidateAll())
            return;

        // Check if the user is already registered
        bool IsRegistered = await _authService
            .RegisterAsync(Username, FullName, Email, Password, BirthDate);
        if (!IsRegistered)
        {
            await Shell.Current.DisplayAlert("Error", "Username or email already exists.", "OK");
            return;
        }

        // Go back to login page
        await _navigation.PopAsync();
    }

    private void InitilizeBirtdateVariables()
    {
        // Initialize birthdate pickers
        var today = DateTime.Today;
        BirthDay = today.Day.ToString();
        BirthMonth = today.ToString("MMMM");
        BirthYear = (today.Year - 18).ToString();

        // Initialize options for day, month, and year
        foreach (var m in System.Globalization
                .DateTimeFormatInfo.InvariantInfo.MonthNames.Take(12))
            MonthOptions.Add(m);
        for (int y = 1900; y <= DateTime.Today.Year - 18; y++)
            YearOptions.Add(y.ToString());
        UpdateDayOptions();
    }
}
