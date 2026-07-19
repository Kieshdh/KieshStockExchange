using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Text.RegularExpressions;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class ChangeEmailViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IProfileService _profile;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _newEmail = string.Empty;
    [ObservableProperty] private string _currentPassword = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ChangeEmailViewModel(IProfileService profile)
    {
        Title = "Change Email";
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    [RelayCommand]
    private async Task Save()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(NewEmail) || string.IsNullOrWhiteSpace(CurrentPassword))
        {
            ErrorMessage = "All fields are required.";
            return;
        }

        if (!Regex.IsMatch(NewEmail.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            ErrorMessage = "Please enter a valid email address.";
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _profile.ChangeEmailAsync(NewEmail, CurrentPassword).ConfigureAwait(false);
            if (!ok)
            {
                ErrorMessage = "Incorrect password or that email is already in use.";
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
