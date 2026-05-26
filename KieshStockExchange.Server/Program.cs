using System.Text.Json.Serialization;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Server.Services.HostedServices;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Options;
using SQLitePCL;

// Make the bundled e_sqlite3 native library loadable on every TFM the server runs on.
// Mirrors what MauiProgram does on the client side.
Batteries_V2.Init();

var builder = WebApplication.CreateBuilder(args);

// Lift Kestrel's request body cap above the 30MB default. The bulk passthrough
// endpoints (insert-all / update-all) are chunked client-side at 2000 items per
// call, but TradeSettler's SettleTradeGroup bundle can carry every order touched
// by a tick group plus the trades and balances — at peak bot load that approaches
// the limit. 256MB gives plenty of headroom without inviting unbounded payloads.
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = 256L * 1024 * 1024;
});

// Controllers + System.Text.Json options. JsonStringEnumConverter serialises
// CurrencyType/CandleResolution and other Shared enums as their string names so
// the wire payload survives enum reordering across client/server versions.
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Bump shutdown timeout above the default 30s. Bot loop's clean stop needs to
// finish the in-flight tick + flush ringbuffers; under load the longest path
// is the AiTradeService dispose + ReservationLedger CSV flush. 60s is plenty.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

// OpenAPI surface for dev. No auth wiring yet — Phase 5 handles JWT.
builder.Services.AddOpenApi();

// SignalR (Phase 3): one hub at /hubs/market with three group families
// (quotes:{stockId}:{currency}, orders:{userId}, portfolio:{userId}).
builder.Services.AddSignalR();

// Persistence — owns the SQLite connection for the entire server process. Singleton:
// DBService maintains AsyncLocal transaction stacks + a writer-serialising semaphore,
// both of which need a single instance to function correctly.
builder.Services.AddSingleton<IDataBaseService, DBService>();

// SeparatorLogger options — engine helpers construct SeparatorLogger<T> inline.
builder.Services.Configure<SeparatorLoggerOptions>(_ => { });

// Phase 3 Step 2: engine state holders + settlement layer move server-side.
// Singletons because OrderRegistry / AccountsCache / OrderBookCache hold in-memory
// state shared across all users for the lifetime of the process, and the matching
// + settlement classes are stateless per call but share these caches.
builder.Services.AddSingleton<IOrderRegistry, OrderRegistry>();
builder.Services.AddSingleton<IAccountsCache, AccountsCache>();
builder.Services.AddSingleton<IOrderBookCache, OrderBookCache>();
builder.Services.AddSingleton<IReservationLedger, ReservationLedger>();
builder.Services.AddSingleton<IMatchingEngine, MatchingEngine>();
builder.Services.AddSingleton<IOrderValidator, OrderValidator>();
builder.Services.AddSingleton<ISettlementEngine, SettlementEngine>();

// Phase 3 Step 4: market data + candles + FX + lookups move server-side.
// IDispatcher gets a no-op impl — server has no UI thread to marshal to.
builder.Services.AddSingleton<IDispatcher, NoopDispatcher>();
builder.Services.AddSingleton<IStockService, StockService>();
builder.Services.AddSingleton<IMarketLookupService, MarketLookupService>();
builder.Services.AddSingleton<ICandleService, CandleService>();
builder.Services.AddSingleton<IMarketDataService, MarketDataService>();
builder.Services.AddSingleton<IFxRateService, FxRateService>();

// IOrderCacheService is the engine's UI-notify hook. Client keeps its INPC impl;
// server uses SignalROrderCacheService which forwards NotifyOrdersMutated
// to each user's orders:{userId} SignalR group.
builder.Services.AddSingleton<IOrderCacheService, SignalROrderCacheService>();

// Server-side IAuthService is a no-op until Phase 5 adds JWT-derived identity.
// Bots run in BeginSystemScope; admin endpoints will get a real auth path later.
builder.Services.AddSingleton<IAuthService, NoopAuthService>();

// UserPortfolioService moves server-side now that bots (which deposit via this
// service) live here. The pre-Phase-2-Step-6 version is wired — it calls
// _db.RunInTransactionAsync directly instead of the IEngineCommandClient bundles.
builder.Services.AddSingleton<IUserPortfolioService, UserPortfolioService>();

// Engine orchestration — fully wired now that IMarketDataService is available.
builder.Services.AddSingleton<IOrderExecutionService, OrderExecutionService>();
builder.Services.AddSingleton<IOrderEntryService, OrderEntryService>();
// EngineAdminService is registered now that NoopAuthService satisfies its dep.
builder.Services.AddSingleton<IEngineAdminService, EngineAdminService>();

// Phase 3 Step 5: bot loop + helpers move server-side.
builder.Services.AddSingleton<IAiTradeService, AiTradeService>();

// Phase 3 bot-loop host. Starts AiTradeService.StartBotAsync when
// Bots:AutoStart is true. Default is false in appsettings.json so the client
// still owns the bot loop until Step 7 deletes the client side.
builder.Services.AddHostedService<BotLoopHostedService>();

// Phase 3 Step 6: subscribe to engine events and push them onto SignalR groups.
builder.Services.AddHostedService<MarketHubBroadcaster>();

var app = builder.Build();

// Cold-load the stock catalogue before the first request hits OrderValidator —
// otherwise IStockService.TryGetById returns false for every stock and the
// validator rejects every order with "Invalid stock ID". The client app does
// this implicitly via MauiProgram lifecycle hooks; on the server we trigger it
// here once at boot.
await app.Services.GetRequiredService<IStockService>().EnsureLoadedAsync().ConfigureAwait(false);

// Boot-warm the chart's 7 candle resolutions across every (stock, supported
// currency). The CandleService then aggregates every bot tick for these keys,
// and the per-key 500-candle hot ring (CandleService._recent) fills up so
// chart switches serve from RAM instead of a DB range scan. The keys for
// resolutions other than the bot-loop default also stay warm without needing
// the chart to be open.
{
    var candles = app.Services.GetRequiredService<ICandleService>();
    var chartResolutions = new[]
    {
        CandleResolution.FifteenSeconds,
        CandleResolution.OneMinute,
        CandleResolution.FiveMinutes,
        CandleResolution.FifteenMinutes,
        CandleResolution.OneHour,
        CandleResolution.FourHours,
        CandleResolution.OneDay,
    };
    foreach (var ccy in CurrencyHelper.SupportedCurrencies)
        foreach (var res in chartResolutions)
            await candles.SubscribeAllAsync(ccy, res).ConfigureAwait(false);

    // Prime each (stock, currency, resolution) ring with the most recent 500
    // persisted candles. Without this, the rings start empty and the chart's
    // "last hour" or "last day" requests still fall through to a DB scan
    // until enough buckets close in real time. Priming makes RAM-served
    // chart switches work from minute zero.
    await candles.PrimeRingsAsync(CurrencyHelper.SupportedCurrencies, chartResolutions)
        .ConfigureAwait(false);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Cheap liveness probe; client uses it during startup to fail fast if the server
// isn't reachable instead of waiting for the first DB call to time out.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");

app.Run();
