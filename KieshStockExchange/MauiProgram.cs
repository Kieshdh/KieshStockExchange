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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace KieshStockExchange;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        Batteries_V2.Init(); // Ensures the bundled e_sqlite3 is loaded everywhere
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
        builder.Services.AddSingleton<IExcelImportService, ExcelImportService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IMarketOrderService, MarketOrderService>();
        builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
        builder.Services.AddSingleton<IUserOrderService, UserOrderService>();
        builder.Services.AddSingleton<ISelectedStockService, SelectedStockService>();
        builder.Services.AddSingleton<IUserPortfolioService, UserPortfolioService>();
        builder.Services.AddSingleton<IUserSessionService, UserSessionService>();
        builder.Services.AddSingleton<ITrendingService, TrendingService>();
        builder.Services.AddSingleton<IPriceSnapshotService, PriceSnapshotService>();
        builder.Services.AddSingleton<ICandleService, CandleService>();
        builder.Services.AddSingleton<IStockService, StockService>();
        builder.Services.AddSingleton<ITransactionService, TransactionService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        //builder.Services.AddSingleton(_ => Application.Current!.Dispatcher);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(SeparatorLogger<>));
        // Viewmodels
        // - User
        builder.Services.AddTransient<RegisterViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        // - Admin
        builder.Services.AddTransient<UserTableViewModel>();
        builder.Services.AddTransient<StockTableViewModel>();
        builder.Services.AddTransient<OrderTableViewModel>();
        builder.Services.AddTransient<TransactionTableViewModel>();
        builder.Services.AddTransient<PositionTableViewModel>();
        builder.Services.AddTransient<FundTableViewModel>();
        builder.Services.AddTransient<AdminViewModel>();
        // - Trade
        builder.Services.AddTransient<TradeViewModel>();
        builder.Services.AddTransient<PlaceOrderViewModel>();
        builder.Services.AddTransient<TransactionHistoryViewModel>();
        builder.Services.AddTransient<OpenOrdersViewModel>();
        builder.Services.AddTransient<UserPositionsViewModel>();
        builder.Services.AddTransient<ChartViewModel>();
        builder.Services.AddTransient<OrderBookViewModel>();
        builder.Services.AddTransient<OrderHistoryViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
