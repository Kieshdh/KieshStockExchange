# API_REFERENCE.md — the server's HTTP + realtime surface

Compact reference for **every network boundary a client (or ops tool) can hit** on `KieshStockExchange.Server`: the REST controllers, their auth requirements, the trading/auth request+response shapes, and the one SignalR hub. Companion to `docs/explainers/ENGINE_MECHANICS.md` (what happens to an order *after* it's placed), `docs/explainers/BOT_MECHANICS.md` (who places orders), and `docs/explainers/CLIENT_STRUCTURE.md` (the MAUI client that consumes this surface — its REST-vs-SignalR map is mirrored here from the **server** side, §4).

**What this is.** A .NET (`KieshStockExchange.Server`) ASP.NET Core app: 28 MVC controllers under `KieshStockExchange.Server/Controllers/` + one SignalR hub (`Hubs/MarketHub.cs`). The client is a thin proxy — it never runs the engine, so this surface *is* the API. Every controller routes off an explicit `[Route("api/...")]`; there is no global route prefix. Request/response DTOs for trading + portfolio live in `KieshStockExchange.Shared/Services/MarketEngineServices/` (`CommandDtos/OrderRequests.cs`, `CommandDtos/EngineCommands.cs`, `OrderResult.cs`, `OrderBookSnapshot.cs`); auth DTOs are declared inline in `AuthController.cs`.

**Reading the references.** File/symbol references are concrete. **Line numbers rot with every unrelated edit above them — grep the symbol (route string, method name, record name), not the line.** Where auth looks notable (an endpoint that a competent reader would *expect* to be admin-only but isn't), it's flagged inline and collected in §5.

**Map:**
- **§1 — Auth model.** The one fallback policy, the admin role gate, ownership checks, rate limits.
- **§2 — Controllers.** Grouped table: every controller → routes → verb → auth → purpose.
- **§3 — Key DTO shapes.** Trading + auth request/response records.
- **§4 — SignalR.** MarketHub groups, client→server methods, server→client events, and which REST endpoint each event pairs with.
- **§5 — Auth flags.** Endpoints whose auth is weaker than a reader would assume.

---

## 1. AUTH MODEL — one fallback policy, one role, per-endpoint ownership

Three layers, applied in this order (`Program.cs` — grep `AddAuthorization`, `UseAuthentication`, `UseRateLimiter`):

1. **Global fallback policy** — `Program.cs` `options.FallbackPolicy = …RequireAuthenticatedUser()`. **Every endpoint requires a valid JWT bearer token unless it is explicitly `[AllowAnonymous]`.** This is the default auth for anything with no `[Authorize(Roles=…)]` of its own — most controllers rely on it. "JWT" in the tables below means exactly this: authenticated, *any* logged-in user, no further check.
2. **Admin role gate** — `[Authorize(Roles = "admin")]` on a class or action. The role claim is issued **lowercase `"admin"`** by `JwtTokenService` (`Services/UserServices/`), and the gate string matches exactly. Applied class-wide on `ServerController`, `SeedController`, `RetentionController`, and per-action on the cross-user/raw-CRUD reads of `OrderController`/`FundController`/`PositionController`/`TransactionController`/`FundTransactionController`.
3. **Per-user ownership** — for "read/write *my* data" endpoints, the controller compares the route's `userId` to the token's `sub` claim. Two helpers in `Services/UserServices/ClaimsExtensions.cs`: `GetUserId()` (parses the `sub`/`NameIdentifier` claim → `int?`) and `CanAccessUser(userId)` (true if `caller == userId` **or** caller is admin). A failed check returns `403 Forbid`. Trading writes additionally require `req.UserId == caller` (a caller can only act as itself).

**JWT plumbing** — `Program.cs` `AddJwtBearer`. Validates issuer/audience/lifetime/signing-key (HMAC symmetric, `Auth:SigningKey`; the server **refuses to boot in Production** with the checked-in dev key). SignalR can't send an `Authorization` header over the WS upgrade, so `JwtBearerEvents.OnMessageReceived` lifts the token off `?access_token=` for any path under `/hubs`.

**Token issue** — `POST /api/auth/login` and `/register` return a `LoginResponse` with the bearer token (§3). The client stores it and sends `Authorization: Bearer <token>` on REST and `?access_token=<token>` on the hub.

**Rate limits** (`Program.cs` `AddRateLimiter`, over-limit → `429`, no queue):
- **`"auth"`** — `[EnableRateLimiting("auth")]` on login/register. 10/min **per client IP**. Blunts credential stuffing.
- **`"orders"`** — on order placement/modify/cancel + portfolio deposit/withdraw/convert. 60/min **per authenticated user** (falls back to IP for the rare anonymous case).
- Reads are unlimited.

**Health + static** — `/healthz/live` (200 if the process is up, no checks) and `/healthz/ready` (also probes the DB) are `[AllowAnonymous]` (`Program.cs` `MapHealthChecks`). `app.UseStaticFiles()` serves `wwwroot/` (the admin log-viewer page) *before* auth so the static asset isn't 401'd by the fallback policy — only its SSE data endpoint is gated (§4 / `AdminLogsController`).

---

## 2. CONTROLLERS — routes, verbs, auth, purpose

Auth column: **Anon** = `[AllowAnonymous]`; **JWT** = any authenticated user (global fallback, no further check); **Owner** = JWT + `CanAccessUser`/`sub`-match ownership check; **Admin** = `[Authorize(Roles="admin")]`. "orders"/"auth" = rate-limit policy applied.

### 2.1 Auth + session + version (the doorway)

| Controller / route | Endpoints | Auth | Purpose |
|---|---|---|---|
| `AuthController` — `api/auth` | `POST login`, `POST register` | **Anon** (+`auth` limit) | Username/password → JWT; self-service trader registration (creates non-admin user, seeds `Users:SeedBalanceUsd` USD, returns auto-login token). `IsAdmin` is server-forced false. |
| `SessionController` — `api/session` | `POST login`, `POST logout` | **JWT** ⚠ | Fire-and-forget "who's connected" server-log breadcrumbs. No-op side effects. (Class comment claims "anyone can hit these" — **stale**; the global fallback now requires a JWT. §5.) |
| `VersionController` — `api/version` | `GET` | **Anon** | Build/version/uptime/environment probe for clients + ops tooling. |

### 2.2 Trading + portfolio (the engine's write surface)

| Controller / route | Endpoints | Auth | Purpose |
|---|---|---|---|
| `OrderController` — `api/orders` | `POST place`, `POST place-bracket`, `POST {id}/modify`, `POST {id}/modify-stop`, `POST {id}/modify-leg`, `POST {id}/cancel`, `POST cancel-batch` | **Owner** (+`orders` limit) | Engine-driven order entry. `place` maps the `(Stop,Entry,Side)` combination to the matching `IOrderEntryService.Place*Async` method (§3). Every mutation enforces `req.UserId == caller`; `cancel-batch` re-checks every id belongs to the caller (max 500). |
| `OrderController` — `api/orders` | `GET by-user/{userId}` | **Owner** | A user's own orders (admin may read anyone's). |
| `OrderController` — `api/orders` | `GET` (all), `GET page`, `GET {id}`, `POST by-ids`, `GET by-stock/{id}`, `GET open-limit/{id}/{ccy}`, `POST open-for-users`, `POST`/`PUT`/`DELETE {id}` | **Admin** | Cross-user admin tables + raw CRUD that bypasses the engine (no match/reserve) — must never be client-reachable. |
| `EngineController` — `api` | `POST portfolio/deposit-withdraw`, `POST portfolio/convert-internal` | **Owner** (+`orders` limit) | Cash deposit/withdraw + internal FX conversion, self-only. Delegates to in-process `IUserPortfolioService`. (Bots top up via the service directly, never this route.) |

### 2.3 Per-user portfolio reads (Owner-gated)

| Controller / route | Endpoints | Auth | Purpose |
|---|---|---|---|
| `FundController` — `api/funds` | `GET by-user/{userId}`, `GET by-user-currency/{userId}/{ccy}` | **Owner** | A user's cash balances. |
| `FundController` — `api/funds` | `GET` (all), `GET user-ids-page`, `GET page`, `GET {id}`, `POST for-users`, `POST`/`PUT`/`PUT upsert`/`DELETE {id}` | **Admin** | Cross-user admin tables + raw CRUD. |
| `PositionController` — `api/positions` | `GET by-user/{userId}`, `GET by-user-stock/{userId}/{stockId}` | **Owner** | A user's share holdings. |
| `PositionController` — `api/positions` | `GET` (all), `GET page/{stockId}`, `GET {id}`, `POST for-users`, `POST`/`PUT`/`PUT upsert`/`DELETE {id}` | **Admin** | Cross-user admin tables + raw CRUD. |
| `TransactionController` — `api/transactions` | `GET by-user/{userId}` | **Owner** | A user's own trades. |
| `TransactionController` — `api/transactions` | `GET by-order/{id}`, `GET by-stock-range/{id}/{ccy}`, `GET since`, `GET latest/{id}/{ccy}`, `GET latest-before/{id}/{ccy}` | **JWT** | Public market-data trade tape (any authenticated client). `by-stock-range` caps rows at 50 000 server-side. |
| `TransactionController` — `api/transactions` | `GET` (all), `GET page`, `GET {id}`, `POST`/`PUT`/`DELETE {id}` | **Admin** | Cross-user tables + raw CRUD. |
| `FundTransactionController` — `api/fund-transactions` | `GET by-user/{userId}` | **Owner** | A user's deposit/withdraw/convert audit history. |
| `FundTransactionController` — `api/fund-transactions` | `GET page`, `POST` | **Admin** | Cross-user table + raw insert (bypasses the deposit/withdraw flow). |
| `MessageController` — `api/messages` | `GET {id}`, `GET by-user/{userId}`, `GET unread-count/{userId}`, `POST {id}/mark-read`, `POST users/{userId}/mark-all-read` | **Owner** | Notification inbox (Kind=Fill etc.) — per-row caller ownership checks. |
| `MessageController` — `api/messages` | `GET` (all), `POST`, `PUT`, `DELETE {id}` | **JWT** ⚠ | Admin/server-internal surface **not yet role-gated** — `GetAll` returns *everyone's* messages to any authenticated caller (§5). |

### 2.4 Market data + reference data (mostly JWT reads; writes notable)

| Controller / route | Endpoints | Auth | Purpose |
|---|---|---|---|
| `OrderBookController` — `api/order-book` | `GET {stockId}/{ccy}` | **JWT** | Live depth snapshot (`OrderBookSnapshot`, §3). Bootstraps the book; live updates arrive over SignalR. |
| `CandleController` — `api/candles` | `GET` (all), `GET {id}`, `GET by-stock/{id}/{ccy}`, `GET by-stock-range/{id}/{ccy}` | **JWT** | Candle history (range routes through the hot-ring candle service, replay on miss). |
| `CandleController` — `api/candles` | `POST`, `PUT`, `PUT upsert`, `POST upsert-batch`, `DELETE {id}` | **JWT** ⚠ | Raw candle CRUD — write-open to any authenticated user (§5). |
| `StockController` — `api/stocks` | `GET` (all), `GET {id}`, `GET {id}/exists` | **JWT** | Stock catalog reads. |
| `StockController` — `api/stocks` | `POST`, `PUT`, `PUT upsert`, `DELETE {id}` | **JWT** ⚠ | Catalog write CRUD — write-open (§5). |
| `StockListingController` — `api/stock-listings` | `GET` (all), `GET by-stock/{id}` | **JWT** | `(stock,currency)` listing reads. |
| `StockListingController` — `api/stock-listings` | `POST` | **JWT** ⚠ | Listing create — write-open (§5). |
| `StockPriceController` — `api/stock-prices` | `GET` (all + `{id}`, `by-stock`, `latest`, `latest-before`, `by-stock-range`) | **JWT** | Persisted price-point reads. |
| `StockPriceController` — `api/stock-prices` | `POST`, `PUT`, `DELETE {id}` | **JWT** ⚠ | Price-point write CRUD — write-open (§5). |
| `FxRateController` — `api/fx-rates` | `GET` | **JWT** | Live FX mid-rate snapshot for every supported currency pair (server runs the AR(1) walk; client mirrors). |
| `MarketLookupController` — `api/market-lookup` | `GET latest-price/{id}/{ccy}`, `GET price-at/{id}/{ccy}`, `GET historical-ticks/{id}/{ccy}`, `GET fallback-price/{id}/{ccy}` | **JWT** | Server-side price-resolution fallback chain (live→last tx→StockPrice→seed) so the client avoids multi-round-trips. |
| `MarketMoodController` — `api/market/mood` | `GET`, `GET {stockId}` | **JWT** | Fear/Greed gauge (0..100) from the bots' ground-truth sentiment field. "Public (non-admin)" in the sense of *any authenticated user* — still behind the fallback policy. |
| `AIUserController` — `api/ai-users` | `GET` (all + `{id}`, `by-user/{userId}`) | **JWT** | Bot profile reads. |
| `AIUserController` — `api/ai-users` | `POST`, `PUT`, `PUT upsert`, `DELETE {id}` | **JWT** ⚠ | Bot profile write CRUD — write-open (§5). |
| `UserController` — `api/users` | `GET` (all), `GET page`, `GET {id}`, `GET by-username/{u}`, `POST by-ids`, `GET {id}/exists`, `POST`, `PUT`, `PUT upsert`, `DELETE {id}`, `DELETE {id}/by-id` | **JWT** ⚠⚠ | **Full user CRUD with no admin gate and no ownership check** — any authenticated user can list, read, create, update, or delete *any* user (§5). |
| `UserPreferencesController` — `api/user-preferences` | `GET by-user/{userId}`, `PUT upsert` | **JWT** ⚠ | UI prefs — **no ownership check** (§5). |
| `UserWatchlistController` — `api/user-watchlist` | `GET by-user/{userId}`, `PUT upsert`, `DELETE {userId}/{stockId}`, `POST users/{userId}/replace` | **JWT** ⚠ | Watchlist — **no ownership check** (§5). `replace` wraps DELETE+N-INSERT in a server tx. |

### 2.5 Admin + ops

| Controller / route | Endpoints | Auth | Purpose |
|---|---|---|---|
| `AdminController` — `api/admin` | `POST drop-recreate`, `POST insert-all/{entity}` ×14, `POST update-all/{entity}` ×14, `POST reset/{entity}` ×14 | **JWT** ⚠⚠ | Bulk passthrough to `IDataBaseService` (`InsertAll`/`UpdateAll`/`ResetTable`) + **`drop-recreate` (drop & recreate the whole schema)** — one action per persisted entity. **No admin gate** — global fallback only (§5). |
| `AdminBotController` — `api/admin/bots` | `GET status`, `POST start`, `POST stop`, `POST scaler`, `POST failures/clear`, `GET failures.csv`/`reservation-ledger.csv`/`economy.csv`/`sentiment.csv`, `GET counts`, `GET ai-user-ids`, `GET activity-samples`, `GET last-24h-stats`, `GET activity-buckets`, `GET strategy-breakdown` | **JWT** ⚠⚠ | Bot-fleet lifecycle + telemetry for the BotDashboard. **No admin gate** — `start`/`stop`/`scaler` (fleet control) reachable by any authenticated user (§5). |
| `AdminLogsController` — `api/admin/logs` | `POST ticket` | **Admin** | Mints a single-use 30 s ticket for the log SSE stream (the JWT never rides a URL). |
| `AdminLogsController` — `api/admin/logs` | `GET stream` | **Anon (ticket)** | SSE telemetry stream — `[AllowAnonymous]` but gated by consuming the admin-minted ticket from `?ticket=` (EventSource can't set headers). Effectively admin-gated via the ticket. |
| `SeedController` — `api/admin/seed/excel` | `POST full`, `POST {kind}`, `POST from-embedded` | **Admin** | Seed the DB from an uploaded (or embedded) AIUserData workbook. |
| `RetentionController` — `api/admin/retention` | `GET preview`, `POST run` | **Admin** | Dry-run / on-demand DB retention prune. |
| `ServerController` — `api/server` | `POST shutdown` | **Admin** | Graceful shutdown via `IHostApplicationLifetime.StopApplication()` (runs every hosted service's `StopAsync`). |

---

## 3. KEY DTO SHAPES — trading + auth

Records live in `KieshStockExchange.Shared/Services/MarketEngineServices/CommandDtos/` unless noted. All are `System.Text.Json` (web defaults). Enums (`OrderSide`, `EntryType`, `StopKind`, `CurrencyType`) are in `KieshStockExchange.Shared`.

### 3.1 Auth (`AuthController.cs`, inline records)

```
LoginRequest    (string Username, string Password)
RegisterRequest (string Username, string Password, string Email, string FullName, DateTime BirthDate)
LoginResponse   (string Token, DateTime ExpiresUtc, int UserId, string Username, bool IsAdmin)
```
`login` 401s with the same opaque `invalid_credentials` for unknown-user and wrong-password alike (no username enumeration). `register` 400s on a weak password (`SecurityHelper.IsValidPassword`, min 8 chars) / invalid user (5–20 alphanumeric username, valid email, 18+), 409 on a taken username.

### 3.2 Order placement (`OrderRequests.cs`)

`PlaceOrderRequest` is the whole order surface — the `(Side, Entry, Stop)` triple plus optional value fields; `OrderController.Place` switches on that triple to pick the engine method (limit/market/slippage-market/stop-market/stop-limit/trailing).

```
PlaceOrderRequest(
    int UserId, int StockId, int Quantity,
    OrderSide Side,        // Buy | Sell
    EntryType Entry,       // Limit | Market
    StopKind  Stop,        // None | Stop | Trailing
    CurrencyType Currency,
    decimal? Price,        // limit price, or slippage anchor on a capped market
    decimal? SlippagePct,  // set = slippage-capped market entry
    decimal? BuyBudget,    // budget for an uncapped market buy
    decimal? StopPrice = null,     // trigger level when Stop != None
    decimal? TrailOffset = null,   // trailing offset (absolute or 0–100%)
    bool?    TrailIsPercent = null)
```

`PlaceBracketRequest` — buy/sell entry + optional protective stop-loss + up to 3 take-profit legs (OCO-grouped, armed as the parent fills). `StopPrice == null` ⇒ TP-only bracket; `StopLimitPrice` set ⇒ stop-limit SL else stop-market. `Side` picks long (default) vs short.

```
BracketLeg(decimal Price, int Quantity)
PlaceBracketRequest(
    int UserId, int StockId, int Quantity, EntryType Entry, CurrencyType Currency,
    decimal? Price, decimal? BuyBudget,
    decimal? StopPrice, decimal? StopLimitPrice, decimal? StopSlippagePct,
    IReadOnlyList<BracketLeg> TakeProfits, OrderSide Side = OrderSide.Buy)
```

Modify/cancel bodies:
```
ModifyOrderRequest      (int UserId, int? Quantity, decimal? Price)
ModifyStopRequest       (int UserId, int? Quantity, decimal? StopPrice, decimal? LimitPrice)
ModifyBracketLegRequest (int UserId, decimal Price, int Quantity)     // one SL or TP leg
CancelBatchRequest      (IReadOnlyList<int> OrderIds)                  // max 500, all caller-owned
```
The `*BatchRequest` records (`BracketBatchRequest`, `MarketShortBatchRequest`, `TrueMarketBuy/SellBatchRequest`) are **bot-fleet internal** — collected by `SubmitAdvancedAsync`, not exposed as controller routes.

### 3.3 Order result (`OrderResult.cs`) — the response to every place/modify/cancel

```
OrderResult {
    OrderStatus Status;                 // enum below
    IReadOnlyList<Transaction> FillTransactions;   // fills; empty if it rested
    int      TotalFilledQuantity;       // derived from fills
    decimal  AverageFillPrice;          // weighted avg; 0 if nothing filled
    Order?   PlacedOrder;               // qty adjusted down by fills
    int      RemainingQuantity;         // still resting
    int?     NewOrderId;                // DB id if a resting order was created
    string   SuccessMessage / ErrorMessage;
    bool     PlacedSuccessfully;        // Success | PartialFill | Filled | PlacedOnBook
}
OrderStatus = Success | NotAuthenticated | InvalidParameters | PriceTooLow | PriceTooHigh
            | InsufficientStocks | InsufficientFunds | NoMarketPrice | NoLiquidity
            | AlreadyClosed | NotAuthorized | OperationFailed | PartialFill | Filled | PlacedOnBook
```

### 3.4 Portfolio (`EngineCommands.cs`) — the `api/portfolio/*` bodies

```
DepositWithdrawCommand (int UserId, CurrencyType Currency, decimal Amount,
                        string Kind,   // "Deposit" | "Withdrawal"
                        string? Note)  // → ActionResult<bool>
ConvertInternalCommand (int UserId, CurrencyType FromCurrency, CurrencyType ToCurrency,
                        decimal Amount, decimal ConvertedAmount,  // client passes the bid-rate-derived amount
                        string? OutNote, string? InNote)          // → ActionResult<bool>
```
(The other records in `EngineCommands.cs` — `Settle*Command`, `PlaceOrdersBatch*` — are in-process engine bundles, **not** HTTP DTOs.)

### 3.5 Order book (`OrderBookSnapshot.cs`) — `GET api/order-book` + the `OrderBookSnapshot` push

```
OrderBookSnapshot(int StockId, CurrencyType Currency,
                  IReadOnlyList<DepthLevel> Bids,   // price desc, best bid first
                  IReadOnlyList<DepthLevel> Asks,   // price asc, best ask first
                  DateTime LastUpdatedUtc, long BookVersion)   // BookVersion = monotonic; drop stale pushes
DepthLevel(decimal Price, int Quantity, int OrderCount)   // readonly struct
```

Admin JSON payloads (`AdminBotController.cs`, inline records): `BotStatusResponse`, `BotScalerSettings`, `BotStrategyBreakdown`/`BotStrategyRow`, `BotLast24hStats`, `BotActivityBuckets`, `BotRingCounts`. Mood (`MarketMoodController.cs`): `MarketMoodResponse(double Global, IReadOnlyDictionary<int,double> Stocks)`, `StockMoodResponse(double Mood)`.

---

## 4. SIGNALR — one hub, three group families (`Hubs/MarketHub.cs`, mapped at `/hubs/market`)

`[Authorize]` on the hub class → a **valid JWT is required to connect** (lifted off `?access_token=` at the WS upgrade). Server-side pushes originate from three `IHostedService` broadcasters that bridge in-process engine events onto hub groups; the hub file itself only owns the subscribe/unsubscribe surface. Group-name helpers are `static` on `MarketHub` (`GroupNameQuotes`/`Orders`/`Portfolio`, `GroupNameTelemetry`).

### 4.1 Client → server (hub methods the client invokes)

| Method | Group joined/left | Notes |
|---|---|---|
| `JoinQuotes` / `LeaveQuotes(stockId, currency)` | `quotes:{stockId}:{currency}` | Per visible listing. |
| `JoinCandles` / `LeaveCandles(stockId, currency, resolution)` | (same quotes group) | **Also ref-counts the server-side candle aggregator** via `ICandleService.Subscribe` — without it the engine never emits `CandleClosed` for that key and the live bar looks frozen. |
| `JoinUserGroups` / `LeaveUserGroups(userId)` | `orders:{userId}` + `portfolio:{userId}` | The wire `userId` is **ignored** — the server derives the id from the JWT `sub` claim (param kept only for pre-JWT handshake back-compat). A mismatched body id is silently re-mapped to the claim. |
| `JoinTelemetry` / `LeaveTelemetry` | `telemetry` | **Admin-only** — `Context.User.IsInRole("admin")` check throws `HubException` otherwise. This role check is *inside the method*, not on the class (the class `[Authorize]` only proves authenticated). |

### 4.2 Server → client (broadcast events) and their REST pairing

| Event | Group | Originates in | Pairs with REST | Payload |
|---|---|---|---|---|
| `QuoteUpdated` | `quotes:{id}:{ccy}` | `MarketHubBroadcaster` ← `IMarketDataService.QuoteUpdated` | `GET api/market-lookup/latest-price`, `GET api/stock-prices/latest` (bootstrap) | live quote |
| `CandleClosed` | `quotes:{id}:{ccy}` | `MarketHubBroadcaster` ← `ICandleService.CandleClosed` | `GET api/candles/by-stock-range` (history) | closed `Candle` |
| `OrderBookSnapshot` | `quotes:{id}:{ccy}` | `OrderBookBroadcaster` (own hosted service; ≤1 push/100 ms per key) | `GET api/order-book/{id}/{ccy}` (cache-miss fallback) | `OrderBookSnapshot` (§3.5) |
| `OrderUpdated` | `orders:{userId}` | `SignalROrderCacheService.NotifyOrdersMutated` (engine settle/cancel/modify) | `GET api/orders/by-user/{userId}` (re-fetch on receipt) | `{ UserId }` envelope — a bare "go refresh" trigger, no order payload |
| `NotificationReceived` | `orders:{userId}` | `ServerNotificationService` (fed by the engine + `OrderController`) | `GET api/messages/by-user/{userId}` | persisted `Message` |
| `PortfolioChanged` | `portfolio:{userId}` | `MarketHubBroadcaster` ← `IUserPortfolioService.SnapshotChanged` | `GET api/funds/by-user`, `GET api/positions/by-user` | snapshot — **treated as a bare refresh trigger** by the client (see `CLIENT_STRUCTURE.md` §6: the push currently targets a placeholder `portfolio:0` group) |
| `OnTelemetryEvent` | `telemetry` | `TelemetryBroadcaster` | (admin dashboard polls REST instead) | telemetry event — **group exists but the client doesn't consume it** (`CLIENT_STRUCTURE.md` §6) |

**The pattern** (mirrors `CLIENT_STRUCTURE.md`'s REST-vs-SignalR split from the server side): **REST bootstraps a screen, SignalR keeps it live.** A client fetches an initial snapshot over HTTP (order book, candle history, orders, portfolio) then joins the matching hub group; thereafter it's push, not poll. `OrderUpdated`/`PortfolioChanged` are deliberately payload-light — they invalidate a cache and the client re-fetches the authoritative state over REST.

---

## 5. AUTH FLAGS — endpoints weaker than they look

Ranked by blast radius. All of these are reachable by **any authenticated (non-admin) user** because they carry no `[Authorize(Roles="admin")]` and no ownership check — only the global `RequireAuthenticatedUser` fallback.

1. **`AdminController` (`api/admin/*`) — no admin gate.** `POST drop-recreate` drops and recreates the entire schema; `reset/{entity}` truncates any table; `insert-all`/`update-all` bulk-write any entity. The class relies solely on the fallback policy. A single non-admin token can wipe the database. *(Contrast: sibling admin controllers `ServerController`/`SeedController`/`RetentionController` *are* `[Authorize(Roles="admin")]`.)*
2. **`AdminBotController` (`api/admin/bots/*`) — no admin gate.** `POST start`/`stop`/`scaler` control the bot fleet; the CSV exports leak bot economy/sentiment/ledger telemetry. Any authenticated user can stop the market's liquidity or dump telemetry.
3. **`UserController` (`api/users/*`) — no admin gate, no ownership.** Full CRUD over *every* user: list all, read by id/username, create, update, `DELETE {id}`. Any token can enumerate or delete other accounts.
4. **`MessageController` `GET`/`POST`/`PUT`/`DELETE` (the non-`by-user` routes) — JWT only.** `GetAll` returns every user's inbox; `Create`/`Update`/`Delete` are ungated. The per-user routes *are* ownership-checked; these class-level CRUD routes are flagged in-code as "role-gating lands with the Phase-7 admin policy" — not yet done.
5. **`UserPreferencesController` + `UserWatchlistController` — no ownership check.** Unlike `Fund`/`Position`/`Transaction` by-user reads (which call `CanAccessUser`), these `by-user/{userId}` + `upsert`/`replace`/`delete` routes never compare `userId` to the caller's claim — one user can read or overwrite another's prefs/watchlist.
6. **Reference-data write CRUD is write-open** — `POST`/`PUT`/`DELETE` on `StockController`, `StockPriceController`, `StockListingController`, `CandleController`, `AIUserController` are JWT-only. A non-admin can mutate the catalog, price history, and candles. (Reads on these are legitimately open market data.)
7. **`SessionController` stale comment** — the class comment says "No security claim — anyone can hit these endpoints," but that predates the fallback policy; the endpoints now require a JWT. Low impact (they only write log lines), but the comment misleads.

*Verified vs inferred:* the missing gates themselves are **verified against the controller attributes** — items 1–6 carry no `[Authorize(Roles=…)]` and no `CanAccessUser` call; only the fallback `RequireAuthenticatedUser` applies. What's *inferred* is intent: they read as deliberate deferrals ("Role-gating lands with the Phase-7 admin policy", `MessageController.cs`) rather than exploited holes — this is a simulation with a trusted client. Whether it matters depends on who can obtain a token (any successful `POST api/auth/register`, which is anonymous and self-service). Flagged per the brief; confirm intent before treating as a bug.
