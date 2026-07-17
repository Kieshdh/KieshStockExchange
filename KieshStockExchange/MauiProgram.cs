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

namespace KieshStockExchange;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
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

        // Step 3b — JWT token plumbing. TokenStore caches in memory + persists
        // via SecureStorage; AuthHeaderHandler stamps Authorization: Bearer on
        // every KSE.Server HTTP call when a token is present.
        // §B1 (BUG_SWEEP): UnauthorizedRedirectHandler watches for 401 on protected
        // calls and bounces the user back to LoginPage with state cleared. Added
        // inner of AuthHeaderHandler so the auth header is already stamped before
        // the network call (and so we see the response 401 to act on).
        builder.Services.AddSingleton<TokenStore>();
        builder.Services.AddTransient<AuthHeaderHandler>();
        builder.Services.AddTransient<UnauthorizedRedirectHandler>();

        builder.Services
            .AddHttpClient("KSE.Server", c => c.BaseAddress = new Uri(serverBaseUrl))
            .AddHttpMessageHandler<AuthHeaderHandler>()
            .AddHttpMessageHandler<UnauthorizedRedirectHandler>();

        // Phase 3 finish — single shared SignalR connection to /hubs/market.
        // Every live-state proxy (market data, candles, portfolio, order cache
        // bridge) subscribes through this. Factory captures serverBaseUrl so
        // we don't read appsettings.json a second time. AccessTokenProvider
        // reads from TokenStore so reconnects pick up the latest token.
        builder.Services.AddSingleton<IMarketHubClient>(sp =>
            new MarketHubClient(new Uri(serverBaseUrl),
                sp.GetRequiredService<TokenStore>(),
                sp.GetRequiredService<ILogger<MarketHubClient>>()));

        // Services
        builder.Services.AddSingleton<IDataBaseService, ApiDataBaseService>();
        // UP-STORE — per-user chart drawings: server-backed store fronted by the local Preferences cache.
        builder.Services.AddSingleton<IDrawingStore, CachedDrawingStore>();
        // IEngineCommandClient / EngineCommandClient deleted in Step 0e — the
        // 4 engine bundle endpoints were already removed in Phase 3 Step 6,
        // and the 2 portfolio bundles now route through ApiPortfolioClient.
        builder.Services.AddSingleton<IAuthService, AuthService>();
        // Phase 3 finish — market data + candles + FX + lookups: thin proxies
        // backed by HTTP + SignalR. The in-process duplicates are dead-code
        // and get deleted in Step 0e.
        builder.Services.AddSingleton<IMarketLookupService, ApiMarketLookupClient>();
        builder.Services.AddSingleton<IMarketDataService, SignalRMarketDataClient>();
        builder.Services.AddSingleton<IFxRateService, ApiFxRateClient>();
        builder.Services.AddSingleton<ICandleService, SignalRCandleService>();
        // §market-mood: HTTP read of the bots' ground-truth Fear/Greed field for the chart sub-pane.
        builder.Services.AddSingleton<IMarketMoodService, ApiMarketMoodClient>();
        builder.Services.AddSingleton<IUserPortfolioService, ApiPortfolioClient>();
        builder.Services.AddSingleton<IOrderCacheService, OrderCacheService>();
        builder.Services.AddSingleton<ISelectedStockService, SelectedStockService>();
        builder.Services.AddSingleton<IWatchlistService, WatchlistService>();
        builder.Services.AddSingleton<IUserSessionService, UserSessionService>();
        builder.Services.AddSingleton<ITrendingService, TrendingService>();
        builder.Services.AddSingleton<IStockService, StockService>();
        builder.Services.AddSingleton<ITransactionService, TransactionService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        // Alert tool (non-chart half): evaluates live quotes against user-armed price alerts and
        // fires INotificationService on a crossing. Singleton so alerts survive page navigation.
        builder.Services.AddSingleton<IPriceAlertService, PriceAlertService>();
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
        // Step 0g-6a — HTTP + SignalR-backed order-book feed. 0g-6b swings
        // the chart VM onto it; until then no one reads it.
        builder.Services.AddSingleton<IOrderBookFeed, ApiOrderBookFeed>();
        // Service helpers
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(SeparatorLogger<>));
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
        builder.Services.AddTransient<FundTransactionTableViewModel>();
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
        builder.Services.AddTransient<PortfolioCurrenciesViewModel>();
        builder.Services.AddTransient<PortfolioHoldingsViewModel>();
        builder.Services.AddTransient<PortfolioOpenOrdersViewModel>();
        builder.Services.AddTransient<PortfolioOrderHistoryViewModel>();
        builder.Services.AddTransient<PortfolioTransactionViewModel>();
        builder.Services.AddTransient<PortfolioFundsHistoryViewModel>();
        // - Trade
        builder.Services.AddTransient<MarketViewModel>();
        builder.Services.AddTransient<TradeViewModel>();
        builder.Services.AddTransient<PlaceOrderViewModel>();
        builder.Services.AddTransient<TransactionHistoryViewModel>();
        builder.Services.AddTransient<OpenOrdersViewModel>();
        builder.Services.AddTransient<UserPositionsViewModel>();
        builder.Services.AddTransient<ChartDrawingViewModel>();
        builder.Services.AddTransient<ChartViewModel>();
        builder.Services.AddTransient<OrderBookViewModel>();
        builder.Services.AddTransient<OrderHistoryViewModel>();
        // - Other
        builder.Services.AddTransient<TopNavBarViewModel>();
        builder.Services.AddSingleton<ToastHostViewModel>();
        // Phase 6c — singleton so every navbar VM shares the same connection
        // state. Eager-resolved at app end so its StateChanged subscription
        // is live before the first hub event.
        builder.Services.AddSingleton<ConnectionStatusViewModel>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        // HttpClient pipelines log Start/Sending/Received at Information per request.
        // That floods the debug output once bots are placing orders — bump to Warning
        // so only retries and failures surface.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        var app = builder.Build();

        // Last-resort net for the client's fire-and-forget work (the `_ = …Async()`
        // refreshes / hub joins below and across the VMs). .NET swallows an unobserved
        // task fault by default, so a failed background refresh would vanish without a
        // trace — log it here instead.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[UnobservedTaskException] {e.Exception}");
            e.SetObserved();
        };

        // Step 3b — populate the in-memory token cache from SecureStorage
        // before any HTTP call goes out. Synchronous-but-fire-and-forget is
        // fine because anonymous endpoints (login + healthz) don't need a
        // token; the first protected call happens after user login anyway.
        _ = app.Services.GetRequiredService<TokenStore>().LoadAsync();

        // Eagerly resolve the notification service so its ctor subscribes to the hub's
        // NotificationReceived push and the session's SnapshotChanged (login hydrate)
        // before any inbox VM is constructed. Replaces the old client-side
        // NotificationBridgeService, which is gone now that the server generates fills.
        _ = app.Services.GetRequiredService<INotificationService>();

        // Eagerly resolve the price-alert service so its ctor subscribes to
        // IMarketDataService.QuoteUpdated immediately — otherwise an alert armed before any
        // page resolves it (indirectly, via injection) could miss the very next quote.
        _ = app.Services.GetRequiredService<IPriceAlertService>();

        // Phase 6c — connection banner singleton. Eager-resolve so its ctor
        // hooks IMarketHubClient.StateChanged before the first transition.
        _ = app.Services.GetRequiredService<ConnectionStatusViewModel>();

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

            // §filtered-tape H/L: the client builds the live in-progress bar via the shared
            // CandleAggregator, so it must apply the same odd-lot-analog rule the server uses for
            // stored candles — else the forming bar's wicks disagree with history. Optional key;
            // absent ⇒ 0 ⇒ off (today's behaviour).
            if (doc.RootElement.TryGetProperty("Candles", out var candles) &&
                candles.TryGetProperty("HLMinFillSize", out var minFill) &&
                minFill.TryGetInt32(out var mf))
                KieshStockExchange.Models.Candle.HLMinFillSize = mf;

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
