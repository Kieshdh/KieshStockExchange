using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Text.RegularExpressions;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class ChangeUsernameViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IProfileService _profile;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private string _currentPassword = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ChangeUsernameViewModel(IProfileService profile)
    {
        Title = "Change Username";
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    [RelayCommand]
    private async Task Save()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(CurrentPassword))
        {
            ErrorMessage = "All fields are required.";
            return;
        }

        if (!Regex.IsMatch(NewUsername.Trim(), @"^[a-zA-Z0-9]{5,20}$"))
        {
            ErrorMessage = "Username must be 5-20 letters or digits.";
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _profile.ChangeUsernameAsync(NewUsername, CurrentPassword).ConfigureAwait(false);
            if (!ok)
            {
                ErrorMessage = "Incorrect password or that username is already taken.";
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // Drop handler refs so the closed popup can be collected; no external subscriptions.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRequested = null;
    }
}
