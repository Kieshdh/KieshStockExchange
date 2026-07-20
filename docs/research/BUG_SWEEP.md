# Bug-sweep register

A living checklist of common bug classes for this stack (.NET MAUI client + ASP.NET Core
server + SignalR + Postgres/Dapper + real-time bot-driven trading sim) and where they tend
to live in this repo. Each sweep updates status. **Sweep 1 ran 2026-06-02.**

Status legend: ✅ fixed · 🟢 audited-clean · 🟡 partially checked / open spots · 🔲 not yet swept

---

## A. MAUI client — async / threading / lifecycle

| # | Bug class | Where to look | Status (sweep 1) |
|---|---|---|---|
| A1 | `async void` handlers without try/catch → exception escapes to sync context → app crash | View code-behind `OnAppearing`/tap/nav handlers | ✅ all 5 `OnAppearing` + 3 nav/close handlers guarded (commit 52207ce). AdminPage resize handler already guarded. |
| A2 | `ObservableCollection` / bound property mutated off the UI thread (SignalR/timer callbacks) → cross-thread crash | VM event handlers for hub/service events | 🟢 **(sweep 2)** clean — all subscriber VM handlers marshal. Hot paths (`OrderBookViewModel`, `MarketViewModels`) guard `MainThread`+`_disposed`; Portfolio*/TopNavBar/PlaceOrder/Chart marshal directly (`MainThread.BeginInvokeOnMainThread`/`_dispatcher.Dispatch`); OpenOrders/OrderHistory/UserPositions go through `TradeTableViewModelBase.PostUpdateFromCache` which marshals. Event sources (`ApiPortfolioClient`, `ApiOrderCacheBridge`) raise on thread-pool, so per-consumer marshaling is the (correctly-followed) contract. |
| A3 | Event subscription without unsubscribe → leak + callback into disposed VM | VMs subscribing to singleton services (`IMarketHubClient`, `IUserSessionService`, `IOrderCacheService`, `IUserPortfolioService`) | 🟢 **(sweep 2)** clean. `ChartViewModel`'s only external-singleton sub (`_orderCache.OrdersChanged`) is unsubscribed in `Dispose`; its other `+=` are to VM-owned collections (self-referential cycles, GC'd with the VM). `NotificationService` 2`+=`/0`-=` is singleton↔singleton (harmless). |
| A4 | Hub lifecycle: invoke on inactive connection; subscriptions not replayed after reconnect/restart | `MarketHubClient` | ✅ fixed (commits 3e852db, 1f513f5): state-guard on all joins, `ReplayGroupsAsync` from both auto-reconnect and manual restart, clear all group sets on disconnect. |
| A5 | Fire-and-forget `_ = …Async()` swallowing failures | client services/VMs | ✅ **(sweep 2)** ~30 sites; hub-related ones already use `…SafelyAsync`. Rather than wrap each, added a client `TaskScheduler.UnobservedTaskException` logger in `MauiProgram` (commit b2f15d1) so a swallowed background fault leaves a trace. |
| A6 | Timers/`IDispatcherTimer` not stopped on disappear/dispose | VMs with polling timers | 🟢 **(sweep 2)** clean — `MarketViewModels._pollTimer` (PausePolling), `BotDashboardViewModel._timer` (StopPolling/Dispose), `PlaceOrderViewModel._assetsTimer` (Dispose), `TrendingService`/`ApiFxRateClient` PeriodicTimers (Dispose/`using`) all stopped+disposed on teardown. |

## B. SignalR / server real-time

| # | Bug class | Where to look | Status |
|---|---|---|---|
| B1 | JWT expiry mid-session → reconnect with stale token → silent 401 loop | `MarketHubClient` token provider, `TokenStore` | ✅ **(2026-06-11, commit 0854a31)** `UnauthorizedRedirectHandler` added inner of `AuthHeaderHandler` on the KSE.Server pipeline. Any 401 on a non-auth endpoint clears the session via `IAuthService.LogoutAsync` and `Shell.GoToAsync("///LoginPage")` — same convention as AppShell/AccountViewModel. Re-entrancy CAS-guarded so the logout-side notify call can't loop. Wrong-password 401s on `/api/auth/*` still flow through normally for `LoginViewModel` to render inline. (Refresh-token feature itself is still Phase-6 territory — the user is now auto-prompted to re-login instead of staring at empty cards.) |
| B2 | Server broadcast fan-out blocking the engine tick | `MarketHubBroadcaster`, `TelemetryBroadcaster` | 🟢 both fire-and-forget with `ContinueWith` fault logging |

## C. Server concurrency / async

| # | Bug class | Where to look | Status |
|---|---|---|---|
| C1 | sync-over-async (`.Result`/`.Wait()`/`GetResult()`) → thread starvation/deadlock | grep across server | 🟢 **(sweep 2)** all clear. `StockService` `.Result` is post-`WhenAll`; `OrderBookBroadcaster:146` is a deliberate commented sync-in-ticker (server, no sync ctx, try/catch, warm snapshots); `MauiProgram:252` is one-time startup config load before any UI sync context. No deadlock risk. |
| C2 | Shared mutable cache without locking under the bot fleet | engine caches, `AccountsCache`, order books | 🟢 covered by conservation/reservation probes — silent through the sweep-1 load soak (item 41). |
| C3 | Escaped background-loop exception vanishes | hosted services, bot loop | ✅ global net added (`Program.cs` TaskScheduler/AppDomain handlers, commit fa153df). |

## D. Database / Dapper / Postgres

| # | Bug class | Where to look | Status |
|---|---|---|---|
| D1 | SQL injection via string-interpolated identifiers | `PgDBService.*` dynamic ORDER BY / WHERE | 🟢 `sortKey` whitelisted everywhere; filters parameterized. |
| D2 | Unbounded query (no LIMIT) → memory/DoS | `PgDBService` list queries | ✅ paging clamped (c3a48fd); ✅ **(sweep 2)** `by-stock-range` now server-capped at 50k most-recent rows via an optional `maxRows` param threaded through `IDataBaseService`/`PgDBService`/`ApiDataBaseService`/`TransactionController` — the candle/backfill callers (`CandleService`, `MarketLookupService`) pass null for the full window, so aggregation is unaffected. 🟢 **(sweep 2)** `GetTransactionsByUserId`/`GetOrdersByUserId` reviewed — **deliberately not capped**: `GetOrdersByUserId` feeds `OrderCacheService` which needs the full open-order set (a cap would drop old open orders from the cache), and the admin UI views other users via the already-clamped *paged* endpoints. Self-consumers are human-bounded; residual risk is only a hand-crafted admin API call against a bot id (trusted). |
| D3 | Decimal overflow / money rounding | `OrderValidator`, `CurrencyHelper`, settlement | ✅ notional-overflow guard (commit c3a48fd); rounding via `CurrencyHelper`. |
| D4 | Connection/transaction leaks | `PgDBService` `OpenAsync`/`RunInTransactionAsync` | 🟢 `await using` scope pattern. 🔲 deeper audit not done. |

## E. Auth / security

| # | Bug class | Where to look | Status |
|---|---|---|---|
| E1 | IDOR — read/write another user's data by id | all controllers | ✅ write-side (cancel/modify/batch) + read-side (orders/funds/positions/transactions/fund-tx) closed (commits 5a50ffe, 8280534). |
| E2 | Engine-bypassing raw CRUD exposed to clients | controller POST/PUT/DELETE | ✅ admin-gated. |
| E3 | Token in URL/logs | SSE log viewer | ✅ single-use ticket flow (earlier). |
| E4 | Mass assignment binding raw entities from body | controller `[FromBody] <Entity>` | 🟡 raw-entity binds now admin-only; review remaining DTO binds. |

## F. Domain / financial correctness

| # | Bug class | Where to look | Status |
|---|---|---|---|
| F1 | Reservation/settlement races, negative balances | engine settlement, probes | 🟢 probes silent through load soak (item 41). |
| F2 | Order validation gaps (negative/zero/overflow/unlisted) | `OrderValidator` | ✅ fuzzed clean (item 40). |

---

## Status after sweep 2
Sweeps 1–2 covered every high/medium bug class for this stack. Real bugs found were all
fixed + deployed (async-void crash class, hub lifecycle, write+read IDOR, paging clamp,
notional overflow, server+client global exception nets, unbounded `by-stock-range`). The
rest audited clean (UI-thread marshaling, subscription leaks, sync-over-async, timers).

### Remaining (not bugs — feature work or deferred deep-dives)
1. **B1** — add a 401 `DelegatingHandler` → re-login (real **feature**, ties into Phase-6 refresh tokens).
2. **D4 / E4** — deeper connection/transaction-leak audit + remaining DTO-bind review (low priority; `await using` + admin-gated raw binds already in place).
3. **C** server concurrency under *new* load shapes — re-run the conservation/reservation soak after any engine change.

## G. Ops / deploy (found during connection-debug, sweep 2)

| # | Bug class | Where | Status |
|---|---|---|---|
| G1 | Docker HEALTHCHECK uses a tool absent from the image → permanent "unhealthy" → autoheal restart loop | `KieshStockExchange.Server/Dockerfile` | ✅ **fixed** (commit 1431e93). `wget` (not in aspnet image) → `curl` (installed) + `--start-period=120s` for the slow candle-backfill boot. Was RestartCount=55, ~every 1-2 min; now healthy & stable. Manifested to users as reconnect-every-30s + 502s + "server slow". |
| G2 | Healthcheck has no start-period vs a ~60-90s boot → flagged unhealthy mid-startup | same | ✅ fixed (start-period=120s). |
| G3 | Single-VPS capacity (4 vCPU / 8 GB) for the 20k-bot sim | infra | 🟢 fine once stable — after the loop was fixed the fleet ramped to the full 20k bots at ~40k trades/min / $52M, tick EWMA ~380ms. Capacity was never the issue. |
| G4 | **Silent host-stop restart loop** — a background fault during the slow cold-start tick tripped .NET's default `BackgroundServiceExceptionBehavior.StopHost`, exiting the whole process (~exit 0) every ~2 min; `restart: unless-stopped` brought it back, so the bot fleet never ramped (stuck ~75 bots, 16–34s ticks) and SignalR clients reconnected every ~30s. Shutdown reason was invisible (Serilog not flushed on exit). | `Program.cs`, `AiTradeService.RunLoopAsync` | ✅ **fixed** (commit ba464a8). `BackgroundServiceExceptionBehavior.Ignore` + whole-tick try/catch in the bot loop + `Log.CloseAndFlush()` on shutdown + healthcheck → `/healthz/live`. Result: RestartCount 0, healthy, full 20k fleet, ~40k trades/min. This (not the wget healthcheck G1) was the dominant cause of the reconnect-every-30s + low-volume reports. Candle-backfill-every-boot (`CandleService.BackfillUpwardAsync`, ~8.8k candles/60d) is now harmless (runs once per rare restart, not every 2 min) — optional future: make it incremental. |
