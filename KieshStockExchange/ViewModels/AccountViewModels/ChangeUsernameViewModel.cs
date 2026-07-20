using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using System.Text.RegularExpressions;

namespace KieshStockExchange.ViewModels.AccountViewModels;

public partial class ChangeUsernameViewModel : ModalFormViewModel
{
    private readonly IProfileService _profile;

    [ObservableProperty] private string _newUsername = string.Empty;
    [ObservableProperty] private string _currentPassword = string.Empty;

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

        RequestClose();
    }
}
