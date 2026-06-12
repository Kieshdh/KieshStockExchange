using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using KieshStockExchange.Helpers;
using KieshStockExchange.Server.Data;
using Microsoft.EntityFrameworkCore;
using KieshStockExchange.Server.HealthChecks;
using KieshStockExchange.Server.Services.SeedServices;
using KieshStockExchange.Server.Services.SeedServices.Interfaces;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Server.Services.HostedServices;
using KieshStockExchange.Server.Services.OtherServices;
using KieshStockExchange.Server.Services.RetentionServices;
using KieshStockExchange.Server.Services.Telemetry;
using KieshStockExchange.Server.Services.UserServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Helpers;
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

// R4 §0009 Stage 1: enable the symmetry probe from config (Bots:MatchSymmetryProbe,
// default false). Behaviour-neutral when off; appsettings.json can flip it on.
KieshStockExchange.Services.MarketEngineServices.MatchSymmetryProbe.Configure(builder.Configuration);

// R4 §0009 Stage 2: bot-decision probe (Bots:BotDecisionProbe, default false).
// Attributes the matcher-level sell-skew to the specific upstream surface.
KieshStockExchange.Services.BackgroundServices.Helpers.BotDecisionProbe.Configure(builder.Configuration);

// 7a-3 — Serilog reads its sinks from the "Serilog" config section: console in
// dev, a rolling daily file (logs/server-.log, 7-day retention) everywhere, and
// a Warning-only JSON file in Production (see appsettings.Production.json).
// The extra in-memory sink lifts operator heartbeats onto the TelemetryBus for
// the live BotDashboard panel + web viewer (resolved lazily at host build, so
// the bus singleton below need only be registered before builder.Build()).
builder.Services.AddSingleton<TelemetryBus>();
builder.Services.AddSingleton<TelemetryTicketStore>();
builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Sink(new InMemoryTelemetrySink(sp.GetRequiredService<TelemetryBus>())));

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
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(60);
    // Keep the host alive when a BackgroundService faults; the default (StopHost) turns one
    // background exception into a full process exit. The service's own logging surfaces it.
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

// OpenAPI surface for dev.
builder.Services.AddOpenApi();

// Phase 5 Step 3a — JWT auth. Issuance + middleware land first; [Authorize]
// on existing controllers is deferred to 3c (after client tokens wire up
// in 3b). The middleware reads tokens from the Authorization header on HTTP
// and from the access_token query string on SignalR (default behaviour).
var authSection = builder.Configuration.GetSection("Auth");
var jwtSettings = new JwtSettings();
authSection.Bind(jwtSettings);
if (string.IsNullOrWhiteSpace(jwtSettings.SigningKey))
    throw new InvalidOperationException("Auth:SigningKey is required. Set it in appsettings.Development.json or user-secrets.");
// 7a-4 — refuse to boot in Production with the checked-in dev key. Production
// must supply its own key via the Auth__SigningKey env var (or a secrets store);
// the default config provider already binds that env var into Auth:SigningKey.
const string DevSigningKey = "dev-only-signing-key-rotate-before-deploy-32-bytes-minimum!";
if (builder.Environment.IsProduction() && jwtSettings.SigningKey == DevSigningKey)
    throw new InvalidOperationException(
        "Production must override Auth:SigningKey via the Auth__SigningKey env var or a secrets store.");
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // SignalR hubs read the token from ?access_token=... on the negotiate
        // request because WebSocket upgrades can't carry custom Authorization
        // headers from browsers; the MAUI SignalR client uses AccessTokenProvider.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
// Step 3c — fallback policy: every endpoint requires authentication unless
// explicitly marked [AllowAnonymous]. Auth + login + /healthz are the only
// allow-anon paths. Role gates (admin-only endpoints) come in Phase 7.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// SignalR (Phase 3): one hub at /hubs/market with three group families
// (quotes:{stockId}:{currency}, orders:{userId}, portfolio:{userId}).
builder.Services.AddSignalR();

// Persistence — Postgres via Dapper. AsyncLocal transaction stack + connection
// pooling live inside PgDBService and PostgresConnectionFactory.
builder.Services.AddSingleton<IDbConnectionFactory, PostgresConnectionFactory>();
builder.Services.AddSingleton<IDataBaseService, PgDBService>();

// SeparatorLogger options — engine helpers construct SeparatorLogger<T> inline.
builder.Services.Configure<SeparatorLoggerOptions>(_ => { });

// Phase 3 Step 2: engine state holders + settlement layer move server-side.
// Singletons because OrderRegistry / AccountsCache / OrderBookCache hold in-memory
// state shared across all users for the lifetime of the process, and the matching
// + settlement classes are stateless per call but share these caches.
builder.Services.AddSingleton<IOrderRegistry, OrderRegistry>();
builder.Services.AddSingleton<IAccountsCache, AccountsCache>();
// Step 0g engine internals: matcher, settler, bot decision take IOrderBookEngine
// (mutable book access); admin endpoints take IOrderBookAdmin (validate/rebuild/
// fix); read-side controller + broadcaster take IOrderBookEngine.GetSnapshotAsync.
builder.Services.AddSingleton<IOrderBookEngine, OrderBookEngine>();
builder.Services.AddSingleton<IOrderBookAdmin, OrderBookAdminService>();
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

// Server-side notification generator: persists Messages (humans only) for fills /
// placement results and pushes them to the orders:{userId} group as
// "NotificationReceived". The engine + OrderController feed it.
builder.Services.AddSingleton<IServerNotificationService, ServerNotificationService>();

// Server-side IAuthService is a no-op until Phase 5 adds JWT-derived identity.
// Bots run in BeginSystemScope; admin endpoints will get a real auth path later.
builder.Services.AddSingleton<IAuthService, NoopAuthService>();

// §3.7 Session FX-desk telemetry (conversion count, per-direction volume, spread captured,
// net FX spend). Singleton so it accumulates across the whole session; UserPortfolioService
// feeds it on each convert and the bot loop resets it on Start.
builder.Services.AddSingleton<FxDeskTelemetry>();

// UserPortfolioService moves server-side now that bots (which deposit via this
// service) live here. The pre-Phase-2-Step-6 version is wired — it calls
// _db.RunInTransactionAsync directly instead of the IEngineCommandClient bundles.
builder.Services.AddSingleton<IUserPortfolioService, UserPortfolioService>();

// Engine orchestration — fully wired now that IMarketDataService is available.
builder.Services.AddSingleton<IOrderExecutionService, OrderExecutionService>();
builder.Services.AddSingleton<IOrderEntryService, OrderEntryService>();

// §3.6 P4 bracket coordinator. Lazy<IStopWatcher> breaks the DI cycle
// StopTriggerWatcher → OrderExecutionService → BracketCoordinator → IStopWatcher.
builder.Services.AddSingleton<IBracketCoordinator, BracketCoordinator>();
builder.Services.AddSingleton(sp => new Lazy<IStopWatcher>(sp.GetRequiredService<IStopWatcher>));

// §3.6 P2 stop trigger watcher — one instance serving both the IStopWatcher arm/disarm
// surface (used by OrderEntryService) and the IHostedService quote loop.
builder.Services.AddSingleton<StopTriggerWatcher>();
builder.Services.AddSingleton<IStopWatcher>(sp => sp.GetRequiredService<StopTriggerWatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StopTriggerWatcher>());
// EngineAdminService is registered now that NoopAuthService satisfies its dep.
builder.Services.AddSingleton<IEngineAdminService, EngineAdminService>();

// Phase 3 Step 5: bot loop + helpers move server-side.
builder.Services.AddSingleton<IAiTradeService, AiTradeService>();

// BotDashboard telemetry cache: TTL+single-flight wrapper around the two
// expensive admin reads (last-24h-stats, activity-buckets). Without it,
// every dashboard poll re-scans every transaction in the last 24h on a
// multi-GB DB — visibly slow on cold start.
builder.Services.AddSingleton<BotTelemetryCache>();

// 7b — Excel seed service. The auto-seed-on-empty run is invoked inline after
// app.Build() (below), BEFORE the catalogue/candle warm-up reads the DB. It must
// NOT be a hosted service: those only start at app.Run(), by which point the
// warm-up has already snapshotted an empty DB into the in-memory caches.
builder.Services.AddSingleton<IExcelSeedService, ExcelSeedService>();

// Phase 3 bot-loop host. Starts AiTradeService.StartBotAsync when
// Bots:AutoStart is true. Default is false in appsettings.json so the client
// still owns the bot loop until Step 7 deletes the client side.
builder.Services.AddHostedService<BotLoopHostedService>();

// Wave 8 §3 — database history retention. The service is stateless (runs raw
// batched SQL via IDbConnectionFactory); the hosted service ticks it on a timer
// when Retention:Enabled is true. The admin controller can run it on demand
// regardless of the flag.
builder.Services.AddSingleton<IRetentionService, RetentionService>();
builder.Services.AddHostedService<RetentionHostedService>();

// Warm the BotTelemetryCache shortly after boot so the first dashboard
// poll doesn't pay the cold DB-scan cost.
builder.Services.AddHostedService<BotTelemetryWarmupHostedService>();

// Phase 3 Step 6: subscribe to engine events and push them onto SignalR groups.
builder.Services.AddHostedService<MarketHubBroadcaster>();

// Bridges the TelemetryBus onto the admin-only "telemetry" SignalR group so the
// BotDashboard live panel mirrors the operator heartbeats from the dev console.
builder.Services.AddHostedService<TelemetryBroadcaster>();

// Step 0g-4: on-change order-book snapshot push, throttled to max 1/100ms
// per (stockId, currency) key. Same quotes group the chart already joins;
// the client's OrderBookFeed (0g-6) listens for "OrderBookSnapshot".
builder.Services.AddHostedService<OrderBookBroadcaster>();

// 7a-2 — rate limiting. "orders" caps order/portfolio mutations per authenticated
// user (falling back to client IP for the rare anonymous case) at 60/min; "auth"
// caps login attempts per IP at 10/min to blunt credential stuffing. Reads are
// unlimited. Over-limit requests get 429 immediately (no queue).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("orders", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // GetUserId() reads sub/NameIdentifier (the bearer handler remaps sub);
            // fall back to client IP for the rare anonymous order path.
            partitionKey: httpContext.User.GetUserId()?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// 7a-5 — CORS (origins come from config; empty in dev) and forwarded headers so
// the app sees the real client scheme/IP behind the reverse proxy. KnownProxies
// is cleared because Caddy is the only hop and runs on a non-loopback address.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// 7a-6 — health checks. /healthz/live is a bare liveness probe; /healthz/ready
// additionally confirms the database answers a trivial query.
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

// Last-resort safety net. A background task that faults with no awaiter (the bot
// loop's fire-and-forget work, broadcasters) would otherwise be swallowed by the
// finalizer (TaskScheduler.UnobservedTaskException); a thread that throws would kill
// the process with no server-side trace (AppDomain.UnhandledException). Route both
// through Serilog so a soak's "silent" death always leaves evidence in the log.
var globalLog = app.Services.GetRequiredService<ILogger<Program>>();
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    globalLog.LogError(e.Exception, "Unobserved task exception (no awaiter observed it).");
    e.SetObserved();
};
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    globalLog.LogCritical(e.ExceptionObject as Exception,
        "Unhandled domain exception (terminating={Terminating}).", e.IsTerminating);

// R3 Q5 (patch 0007) — EF tool-independent migration fallback. The runtime uses Dapper
// (PgDBService), not EF; migrations are normally applied via `dotnet ef database update`.
// When that tool isn't available on a host (broken local install, container without the
// EF tool, CI runner) the schema lags behind the code and every bot order trips
// `PgDBService.cs:226`'s "Postgres schema dropped" message. Applying pending migrations at
// startup via the existing design-time factory removes the tool dependency.
//
// Assumption: single-instance deployment. For a multi-instance roll-out, `Database.Migrate()`
// races across replicas — the standard guards (distributed lock, dedicated migration job)
// are out of scope for R3. The `Db:AutoMigrate=false` escape hatch lets operators stage
// migrations manually for that case. Runs BEFORE the seed block so a fresh DB has the
// schema in place before any INSERT is attempted.
if (builder.Configuration.GetValue("Db:AutoMigrate", true))
{
    var migrateLog = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        using var ctx = new KseDbContextFactory().CreateDbContext(args);
        ctx.Database.Migrate();
        migrateLog.LogInformation("Db:AutoMigrate applied any pending EF Core migrations on startup.");
    }
    catch (Exception ex)
    {
        // Don't take the host down on migration failure — log loud and continue. A bad
        // migration in prod must surface via the health-check stream rather than silently
        // failing to boot. Operators can disable AutoMigrate to take the path out of the loop.
        migrateLog.LogCritical(ex, "Db:AutoMigrate failed; continuing without applying migrations. " +
            "Run `dotnet ef database update` manually or set Db:AutoMigrate=false to silence.");
    }
}

// Seed-ordering fix — a fresh DB must be seeded BEFORE the warm-up below reads
// it. The stock cold-load and candle ring priming snapshot the DB into in-memory
// caches; seeding any later (it used to be a hosted service, which only starts at
// app.Run()) left those caches frozen empty on a fresh DB, so every bot order
// failed stock validation until a second boot. Order is now strict: seed → warm → run.
if (builder.Configuration.GetValue("Seed:AutoOnEmptyDb", false))
{
    var seed = app.Services.GetRequiredService<IExcelSeedService>();
    var seedLog = app.Services.GetRequiredService<ILogger<Program>>();
    if (await seed.IsDatabaseEmptyAsync().ConfigureAwait(false))
    {
        seedLog.LogInformation("Seed:AutoOnEmptyDb true and database empty; seeding from embedded workbook.");
        if (!await seed.SeedAllFromEmbeddedAsync().ConfigureAwait(false))
            seedLog.LogWarning("Auto-seed requested but the embedded workbook was not found.");
    }
    else
    {
        seedLog.LogInformation("Seed:AutoOnEmptyDb is on but the database is already populated; skipping.");
    }
}

// Cold-load the stock catalogue before the first request hits OrderValidator —
// otherwise IStockService.TryGetById returns false for every stock and the
// validator rejects every order with "Invalid stock ID". The client app does
// this implicitly via MauiProgram lifecycle hooks; on the server we trigger it
// here once at boot.
await app.Services.GetRequiredService<IStockService>().EnsureLoadedAsync().ConfigureAwait(false);

// §3.6 P4: rebuild the bracket-parent index from DB so brackets self-manage after a restart
// (armed SL legs rehydrate via the stop-watcher cold-load; Attached children stay dormant).
await app.Services.GetRequiredService<IBracketCoordinator>().RehydrateAsync().ConfigureAwait(false);

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

    // Backfill higher-resolution candles by aggregating from the dense 5m
    // source persisted by the long-running bot subscription. Runs BEFORE
    // priming so the prime then reads a DB that includes the aggregates.
    // Without this, charts at 1h/4h/1d only show what the current session has
    // managed to close in real time (e.g. ~5 candles after 5h of uptime at 1h).
    await candles.BackfillUpwardAsync(CurrencyHelper.SupportedCurrencies)
        .ConfigureAwait(false);

    // Prime each (stock, currency, resolution) ring with the most recent 500
    // persisted candles. Without this, the rings start empty and the chart's
    // "last hour" or "last day" requests still fall through to a DB scan
    // until enough buckets close in real time. Priming makes RAM-served
    // chart switches work from minute zero.
    await candles.PrimeRingsAsync(CurrencyHelper.SupportedCurrencies, chartResolutions)
        .ConfigureAwait(false);
}

// 7a-5 — must run before anything that reads the client scheme/IP so downstream
// middleware and rate-limit partitioning see the proxied values.
app.UseForwardedHeaders();

// Before auth: the admin web log viewer (wwwroot/admin/logs.html) is a public
// static asset — its SSE endpoint is the auth-gated part. Placing it after
// UseAuthorization would trip the RequireAuthenticatedUser fallback policy
// (no endpoint matches a static file) and 401 the page.
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// 7a-6 — split liveness from readiness. /healthz/live answers 200 as long as the
// process is up (no checks run); /healthz/ready also confirms the database.
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/healthz/ready").AllowAnonymous();

app.UseCors();

// Auth must come before MapControllers / MapHub so [Authorize] and
// User.FindFirst("sub") work everywhere downstream.
app.UseAuthentication();
app.UseAuthorization();

// 7a-2 — after authentication so the "orders" policy can partition on the
// authenticated user's "sub" claim.
app.UseRateLimiter();

app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");

// Flush Serilog on shutdown so the stop reason (BackgroundService fault, signal,
// StopApplication) lands in the log instead of being lost as the process exits.
app.Lifetime.ApplicationStopping.Register(() =>
    Log.Warning("Application is stopping (ApplicationStopping fired) — flushing logs."));
try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
