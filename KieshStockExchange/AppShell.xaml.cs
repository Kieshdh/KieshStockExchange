using KieshStockExchange.Views.AccountPageViews;
using KieshStockExchange.Views.AdminPageViews;
using KieshStockExchange.Views.MarketPageViews;
using KieshStockExchange.Views.PortfolioPageViews;
using KieshStockExchange.Views.TradePageViews;
using KieshStockExchange.Views.UserViews;

namespace KieshStockExchange;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Registering routes
        Routing.RegisterRoute(nameof(LoginPage), typeof(LoginPage));
        Routing.RegisterRoute(nameof(RegisterPage), typeof(RegisterPage));
        Routing.RegisterRoute(nameof(AccountPage), typeof(AccountPage));
        Routing.RegisterRoute(nameof(AdminPage), typeof(AdminPage));
        Routing.RegisterRoute(nameof(PortfolioPage), typeof(PortfolioPage));
        Routing.RegisterRoute(nameof(MarketPage), typeof(MarketPage));
        Routing.RegisterRoute(nameof(TradePage), typeof(TradePage));

        // Set the initial page to LoginPage
        Dispatcher.Dispatch(async () => await GoToAsync("///LoginPage"));
    }
}
