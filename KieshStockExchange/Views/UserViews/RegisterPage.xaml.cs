using System;
using KieshStockExchange.Services;

namespace KieshStockExchange.Views.UserViews;

public partial class RegisterPage : ContentPage
{
	public RegisterPage()
    {
		InitializeComponent();
        var authService = Application.Current.Handler
            .MauiContext.Services.GetRequiredService<IAuthService>();
        BindingContext = new ViewModels.UserViewModels.RegisterViewModel(Navigation, authService);
    }
}