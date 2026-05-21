using CommunityToolkit.Maui;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.Implementations;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.AccountViewModels;
using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.ViewModels.PortfolioViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using KieshStockExchange.ViewModels.UserViewModels;
using KieshStockExchange.ViewModels.MarketViewModels;
using KieshStockExchange.ViewModels.OtherViewModels;
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
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Pages
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<ChangePasswordPage>();
        builder.Services.AddTransient<ChangeEmailPage>();
        builder.Services.AddTransient<ChangeUsernamePage>();
        builder.Services.AddTransient<DepositWithdrawPage>();
        builder.Services.AddTransient<ConvertCurrencyPage>();
        builder.Services.AddTransient<FundTransactionHistoryPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<AdminPage>();
        builder.Services.AddTransient<BotDashboardPage>();
        builder.Services.AddTransient<PortfolioPage>();
        builder.Services.AddTransient<MarketPage>();
        builder.Services.AddTransient<TradePage>();
        // Services
        builder.Services.AddSingleton<IDataBaseService, LocalDBService>();
        builder.Services.AddSingleton<IOrderRegistry, OrderRegistry>();
        builder.Services.AddSingleton<IExcelImportService, ExcelImportService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<IMarketLookupService, MarketLookupService>();
        builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
        builder.Services.AddSingleton<IFxRateService, FxRateService>();
        builder.Services.AddSingleton<IOrderCacheService, OrderCacheService>();
        builder.Services.AddSingleton<ISelectedStockService, SelectedStockService>();
        builder.Services.AddSingleton<IUserPortfolioService, UserPortfolioService>();
        builder.Services.AddSingleton<IWatchlistService, WatchlistService>();
        builder.Services.AddSingleton<IUserSessionService, UserSessionService>();
        builder.Services.AddSingleton<ITrendingService, TrendingService>();
        builder.Services.AddSingleton<IPriceSnapshotService, PriceSnapshotService>();
        builder.Services.AddSingleton<ICandleService, CandleService>();
        builder.Services.AddSingleton<IStockService, StockService>();
        builder.Services.AddSingleton<ITransactionService, TransactionService>();
        builder.Services.AddSingleton<IAccountsCache, AccountsCache>();
        builder.Services.AddSingleton<IReservationLedger, ReservationLedger>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<NotificationBridgeService>();
        builder.Services.AddSingleton<IAiTradeService, AiTradeService>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IProfileService, ProfileService>();
        builder.Services.AddSingleton<IOrderEditService, OrderEditService>();
        // Market engine
        builder.Services.AddSingleton<IOrderValidator, OrderValidator>();
        builder.Services.AddSingleton<ISettlementEngine, SettlementEngine>();
        builder.Services.AddSingleton<IOrderExecutionService, OrderExecutionService>();
        builder.Services.AddSingleton<IOrderEntryService, OrderEntryService>();
        builder.Services.AddSingleton<IEngineAdminService, EngineAdminService>();
        // Service helpers
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(SeparatorLogger<>));
        builder.Services.AddSingleton<IOrderBookCache, OrderBookCache>();
        builder.Services.AddSingleton<IMatchingEngine, MatchingEngine>();
        // Viewmodels
        // - User
        builder.Services.AddTransient<RegisterViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<AccountViewModel>();
        builder.Services.AddTransient<ChangePasswordViewModel>();
        builder.Services.AddTransient<ChangeEmailViewModel>();
        builder.Services.AddTransient<ChangeUsernameViewModel>();
        builder.Services.AddTransient<DepositWithdrawViewModel>();
        builder.Services.AddTransient<ConvertCurrencyViewModel>();
        builder.Services.AddTransient<FundTransactionHistoryViewModel>();
        builder.Services.AddTransient<ModifyOrderViewModel>();
        // - Admin
        builder.Services.AddTransient<UserTableViewModel>();
        builder.Services.AddTransient<StockTableViewModel>();
        builder.Services.AddTransient<OrderTableViewModel>();
        builder.Services.AddTransient<TransactionTableViewModel>();
        builder.Services.AddTransient<PositionTableViewModel>();
        builder.Services.AddTransient<FundTableViewModel>();
        builder.Services.AddTransient<BotDashboardViewModel>();
        builder.Services.AddTransient<AdminViewModel>();
        // - Portfolio
        builder.Services.AddTransient<PortfolioViewModel>();
        builder.Services.AddTransient<PortfolioHoldingsViewModel>();
        builder.Services.AddTransient<PortfolioOpenOrdersViewModel>();
        builder.Services.AddTransient<PortfolioOrderHistoryViewModel>();
        builder.Services.AddTransient<PortfolioTransactionViewModel>();
        // - Trade
        builder.Services.AddTransient<MarketViewModel>();
        builder.Services.AddTransient<TradeViewModel>();
        builder.Services.AddTransient<PlaceOrderViewModel>();
        builder.Services.AddTransient<TransactionHistoryViewModel>();
        builder.Services.AddTransient<OpenOrdersViewModel>();
        builder.Services.AddTransient<UserPositionsViewModel>();
        builder.Services.AddTransient<ChartViewModel>();
        builder.Services.AddTransient<OrderBookViewModel>();
        builder.Services.AddTransient<OrderHistoryViewModel>();
        // - Other
        builder.Services.AddTransient<TopNavBarViewModel>();
        builder.Services.AddSingleton<ToastHostViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Eagerly resolve the bridge so its ctor wires up the OrdersChanged
        // subscription — DI would otherwise defer construction until something
        // injects it, which nothing does (it's a side-effect-only service).
        _ = app.Services.GetRequiredService<NotificationBridgeService>();

        return app;
    }
}
