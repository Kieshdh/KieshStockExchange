using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.OtherServices.Interfaces;

namespace KieshStockExchange;

public partial class App : Application
{
    public App(IThemeService themeService)
    {
        InitializeComponent();
        // Apply a random theme on startup, will be overridden by user settings if they exist  
        themeService.ApplyRandomTheme();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}