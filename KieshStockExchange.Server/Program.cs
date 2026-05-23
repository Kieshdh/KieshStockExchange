using System.Text.Json.Serialization;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Server.Services.HostedServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
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
// OrderExecutionService, OrderEntryService, EngineAdminService land in Step 4
// alongside IMarketDataService (their construction depends on it).

// Phase 3 bot-loop host. Stub until Step 5 wires it to IAiTradeService.
builder.Services.AddHostedService<BotLoopHostedService>();

var app = builder.Build();

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
