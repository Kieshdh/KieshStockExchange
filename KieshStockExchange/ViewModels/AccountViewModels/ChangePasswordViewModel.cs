using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class ChangePasswordViewModel : BaseViewModel, IClosablePopupViewModel
{
    private readonly IProfileService _profile;
    private bool _disposed;

    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ChangePasswordViewModel(IProfileService profile)
    {
        Title = "Change Password";
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    [RelayCommand]
    private async Task Save()
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(CurrentPassword) ||
            string.IsNullOrWhiteSpace(NewPassword) ||
            string.IsNullOrWhiteSpace(ConfirmPassword))
        {
            ErrorMessage = "All fields are required.";
            return;
        }

        if (NewPassword.Length < 8)
        {
            ErrorMessage = "New password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "New passwords do not match.";
            return;
        }

        if (CurrentPassword == NewPassword)
        {
            ErrorMessage = "New password must differ from your current password.";
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _profile.ChangePasswordAsync(CurrentPassword, NewPassword).ConfigureAwait(false);
            if (!ok)
            {
                ErrorMessage = "Current password is incorrect.";
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
