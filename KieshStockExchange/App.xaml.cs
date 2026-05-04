using KieshStockExchange.Helpers;
using KieshStockExchange.Services;
using KieshStockExchange.Services.OtherServices;

namespace KieshStockExchange;

public partial class App : Application
{
    public App(IThemeService themeService)
    {
        InitializeComponent();

        // App.xaml hardcodes ExchangeLight at slot 0 of MergedDictionaries.
        // For now, pick a random theme on every startup so we can preview
        // every palette quickly. Swap back to ApplySavedTheme() later to
        // restore "remember the last user choice" behaviour.
        themeService.ApplyRandomTheme();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}