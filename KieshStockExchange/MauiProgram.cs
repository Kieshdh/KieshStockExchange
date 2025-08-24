using KieshStockExchange.Services;
using KieshStockExchange.Services.Implementations;
using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using KieshStockExchange.ViewModels.UserViewModels;
using KieshStockExchange.Views.AccountPageViews;
using KieshStockExchange.Views.AdminPageViews;
using KieshStockExchange.Views.MarketPageViews;
using KieshStockExchange.Views.PortfolioPageViews;
using KieshStockExchange.Views.TradePageViews;
using KieshStockExchange.Views.UserViews;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Pages
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<AdminPage>();
        builder.Services.AddTransient<PortfolioPage>();
        builder.Services.AddTransient<MarketPage>();
        builder.Services.AddTransient<TradePage>();
        // Services
        builder.Services.AddSingleton<IDataBaseService, LocalDBService>();
        builder.Services.AddSingleton<IExcelImportService, AddExcelService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IMarketOrderService, MarketOrderService>();
        builder.Services.AddSingleton<IUserOrderService, UserOrderService>();
        builder.Services.AddSingleton<ISelectedStockService, SelectedStockService>();
        builder.Services.AddSingleton<IUserSessionService, UserSessionService>();
        // Viewmodels
        // - User
        builder.Services.AddTransient<RegisterViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        // - Admin
        builder.Services.AddTransient<UserTableViewModel>();
        builder.Services.AddTransient<StockTableViewModel>();
        builder.Services.AddTransient<OrderTableViewModel>();
        builder.Services.AddTransient<TransactionTableViewModel>();
        builder.Services.AddTransient<AdminViewModel>();
        // - Trade
        builder.Services.AddTransient<TradeViewModel>();
        builder.Services.AddTransient<PlaceOrderViewModel>();
        builder.Services.AddTransient<HistoryTableViewModel>();
        builder.Services.AddTransient<OpenOrdersTableViewModel>();
        builder.Services.AddTransient<PositionsTableViewModel>();
        builder.Services.AddTransient<ChartViewModel>();
        builder.Services.AddTransient<OrderBookViewModel>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
