using KieshStockExchange.Services.UserServices.Interfaces;

namespace KieshStockExchange.Views.UserViews;

public partial class RegisterPage : ContentPage
{
    public RegisterPage(IAuthService auth)
    {
        InitializeComponent();
        BindingContext = new ViewModels.UserViewModels.RegisterViewModel(Navigation, auth);
    }

    private async void OnSignInClicked(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
