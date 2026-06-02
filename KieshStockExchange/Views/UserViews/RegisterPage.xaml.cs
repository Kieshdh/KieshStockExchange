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
        // async void — a failed navigation must not crash the app.
        try { await Shell.Current.GoToAsync(".."); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RegisterPage.OnSignInClicked nav failed: {ex}"); }
    }
}
