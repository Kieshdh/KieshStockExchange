using System;
using KieshStockExchange.Services;

namespace KieshStockExchange.Views.UserViews;

public partial class RegisterPage : ContentPage
{
	public RegisterPage(IAuthService auth)
    {
		InitializeComponent();
        BindingContext = new ViewModels.UserViewModels.RegisterViewModel(Navigation, auth);
    }
}