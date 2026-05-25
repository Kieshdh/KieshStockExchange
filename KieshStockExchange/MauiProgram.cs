using CommunityToolkit.Maui;
using Microsoft.Maui.LifecycleEvents;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.AccountViewModels;
using KieshStockExchange.ViewModels.AdminViewModels;
using KieshStockExchange.ViewModels.AdminViewModels.EditPopups;
using KieshStockExchange.ViewModels.AdminViewModels.Tables;
using KieshStockExchange.ViewModels.PortfolioViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using KieshStockExchange.ViewModels.UserViewModels;
using KieshStockExchange.ViewModels.MarketViewModels;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.Views.AccountPageViews;
using KieshStockExchange.Views.AdminPageViews;
using KieshStockExchange.Views.AdminPageViews.EditPopups;
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
            })
#if WINDOWS
            .ConfigureLifecycleEvents(events =>
            {
                events.AddWindows(windows => windows.OnWindowCreated(window =>
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                    if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                        presenter.Maximize();
                }));
            })
#endif
            ;

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
        // Phase 2: Server BaseUrl loaded from Resources/Raw/appsettings.json. Synchronous read
        // off the MauiAsset stream so the HttpClient registration below has the URL ready.
        var serverBaseUrl = LoadServerBaseUrl();

        builder.Services.AddHttpClient("KSE.Server", c => c.BaseAddress = new Uri(serverBaseUrl));

        // Phase 3 finish — single shared SignalR connection to /hubs/market.
        // Every live-state proxy (market data, candles, portfolio, order cache
        // bridge) subscribes through this. Factory captures serverBaseUrl so
        // we don't read appsettings.json a second time.
        builder.Services.AddSingleton<IMarketHubClient>(sp =>
            new MarketHubClient(new Uri(serverBaseUrl), sp.GetRequiredService<ILogger<MarketHubClient>>()));

        // Services
        builder.Services.AddSingleton<IDataBaseService, ApiDataBaseService>();
        // IOrderRegistry stays for now — the in-process OrderBookCache (kept
        // until Step 0g) still calls _registry.GetOrAdd when bulk-loading the
        // book. Step 0g's ApiOrderBookFeed will drop both.
        builder.Services.AddSingleton<IOrderRegistry, OrderRegistry>();
        // IEngineCommandClient / EngineCommandClient deleted in Step 0e — the
        // 4 engine bundle endpoints were already removed in Phase 3 Step 6,
        // and the 2 portfolio bundles now route through ApiPortfolioClient.
        builder.Services.AddSingleton<IExcelImportService, ExcelImportService>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        // Phase 3 finish — market data + candles + FX + lookups: thin proxies
        // backed by HTTP + SignalR. The in-process duplicates are dead-code
        // and get deleted in Step 0e.
        builder.Services.AddSingleton<IMarketLookupService, ApiMarketLookupClient>();
        builder.Services.AddSingleton<IMarketDataService, SignalRMarketDataClient>();
        builder.Services.AddSingleton<IFxRateService, ApiFxRateClient>();
        builder.Services.AddSingleton<ICandleService, SignalRCandleService>();
        builder.Services.AddSingleton<IUserPortfolioService, ApiPortfolioClient>();
        builder.Services.AddSingleton<IOrderCacheService, OrderCacheService>();
        builder.Services.AddSingleton<ISelectedStockService, SelectedStockService>();
        builder.Services.AddSingleton<IWatchlistService, WatchlistService>();
        builder.Services.AddSingleton<IUserSessionService, UserSessionService>();
        builder.Services.AddSingleton<ITrendingService, TrendingService>();
        builder.Services.AddSingleton<IStockService, StockService>();
        builder.Services.AddSingleton<ITransactionService, TransactionService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<NotificationBridgeService>();
        // Phase 3 Step 7b.1: ApiBotAdminClient is the dashboard's HTTP-backed
        // replacement for the dead in-process AiTradeService.
        builder.Services.AddSingleton<ApiBotAdminClient>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IProfileService, ProfileService>();
        builder.Services.AddSingleton<IOrderEditService, OrderEditService>();
        // Order entry/execution route to the server's in-process engine via HTTP
        // (Phase 3 Step 7a). The client's IOrderEntryService / IOrderExecutionService
        // impls are HTTP proxies; the in-process classes were deleted in Step 7b.3.
        builder.Services.AddSingleton<IOrderExecutionService, ApiOrderExecutionService>();
        builder.Services.AddSingleton<IOrderEntryService, ApiOrderEntryClient>();
        // Phase 3 finish — SignalR -> OrderCacheService.RefreshAsync glue.
        // Eagerly resolved below so its ctor wires up the subscription.
        builder.Services.AddSingleton<ApiOrderCacheBridge>();
        // Service helpers
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(SeparatorLogger<>));
        // OrderBookCache stays for now — Step 0g reworks it (engine/admin/UI
        // split + live SignalR push). Until then SelectedStockService still
        // reads a one-shot snapshot via this in-process class, which reads
        // from IDataBaseService HTTP under the hood.
        builder.Services.AddSingleton<IOrderBookCache, OrderBookCache>();
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
        builder.Services.AddTransient<UserEditViewModel>();
        builder.Services.AddTransient<UserEditPopup>();
        builder.Services.AddTransient<StockTableViewModel>();
        builder.Services.AddTransient<StockEditViewModel>();
        builder.Services.AddTransient<StockEditPopup>();
        builder.Services.AddTransient<OrderTableViewModel>();
        builder.Services.AddTransient<TransactionTableViewModel>();
        builder.Services.AddTransient<PositionTableViewModel>();
        builder.Services.AddTransient<PositionEditViewModel>();
        builder.Services.AddTransient<PositionEditPopup>();
        builder.Services.AddTransient<FundTableViewModel>();
        builder.Services.AddTransient<FundAdjustViewModel>();
        builder.Services.AddTransient<FundAdjustPopup>();
        builder.Services.AddTransient<BotDashboardViewModel>();
        builder.Services.AddTransient<UserDetailsViewModel>();
        builder.Services.AddTransient<OrderDetailsViewModel>();
        builder.Services.AddTransient<OrderDetailsPopup>();
        builder.Services.AddTransient<TransactionDetailsViewModel>();
        builder.Services.AddTransient<TransactionDetailsPopup>();
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
        // HttpClient pipelines log Start/Sending/Received at Information per request.
        // That floods the debug output once bots are placing orders — bump to Warning
        // so only retries and failures surface.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        var app = builder.Build();

        // Eagerly resolve the bridge so its ctor wires up the OrdersChanged
        // subscription — DI would otherwise defer construction until something
        // injects it, which nothing does (it's a side-effect-only service).
        _ = app.Services.GetRequiredService<NotificationBridgeService>();

        // Phase 3 finish — same pattern: ApiOrderCacheBridge subscribes to
        // SignalR "OrderUpdated" in its ctor and triggers OrderCacheService
        // refresh. Nothing injects it, so force creation here.
        _ = app.Services.GetRequiredService<ApiOrderCacheBridge>();

        return app;
    }

    /// <summary>
    /// Read the server base URL from Resources/Raw/appsettings.json. Sync — happens once at
    /// startup before DI resolution. Returns the localhost fallback if the asset is missing
    /// or malformed so a misconfigured client still boots and surfaces the error on first call.
    /// </summary>
    private static string LoadServerBaseUrl()
    {
        const string fallback = "http://localhost:5000";
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").GetAwaiter().GetResult();
            using var reader = new StreamReader(stream);
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            if (doc.RootElement.TryGetProperty("Server", out var server) &&
                server.TryGetProperty("BaseUrl", out var url) &&
                url.GetString() is { Length: > 0 } s)
                return s;
        }
        catch
        {
            // Fall through to the default — surfaces as a connection error on first DB call,
            // which is clearer than a config-parse crash at startup.
        }
        return fallback;
    }
}
