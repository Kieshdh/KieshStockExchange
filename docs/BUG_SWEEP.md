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
| A2 | `ObservableCollection` / bound property mutated off the UI thread (SignalR/timer callbacks) → cross-thread crash | VM event handlers for hub/service events | 🟢 high-frequency paths verified marshaled (`OrderBookViewModel.OnFeedSnapshot`, `MarketViewModels.OnQuoteUpdated` both `MainThread`+`_disposed` guarded). 🟡 remaining ~10 subscriber VMs (Portfolio*, OpenOrders, OrderHistory, UserPositions, Account, TopNavBar, Chart) not yet line-audited. |
| A3 | Event subscription without unsubscribe → leak + callback into disposed VM | VMs subscribing to singleton services (`IMarketHubClient`, `IUserSessionService`, `IOrderCacheService`, `IUserPortfolioService`) | 🟢 **(sweep 2)** clean. `ChartViewModel`'s only external-singleton sub (`_orderCache.OrdersChanged`) is unsubscribed in `Dispose`; its other `+=` are to VM-owned collections (self-referential cycles, GC'd with the VM). `NotificationService` 2`+=`/0`-=` is singleton↔singleton (harmless). |
| A4 | Hub lifecycle: invoke on inactive connection; subscriptions not replayed after reconnect/restart | `MarketHubClient` | ✅ fixed (commits 3e852db, 1f513f5): state-guard on all joins, `ReplayGroupsAsync` from both auto-reconnect and manual restart, clear all group sets on disconnect. |
| A5 | Fire-and-forget `_ = …Async()` swallowing failures | client services/VMs | 🔲 not swept |
| A6 | Timers/`IDispatcherTimer` not stopped on disappear/dispose | VMs with polling timers | 🔲 not swept |

## B. SignalR / server real-time

| # | Bug class | Where to look | Status |
|---|---|---|---|
| B1 | JWT expiry mid-session → reconnect with stale token → silent 401 loop | `MarketHubClient` token provider, `TokenStore` | 🔲 not swept |
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
| D2 | Unbounded query (no LIMIT) → memory/DoS | `PgDBService` list queries | ✅ paging clamped (c3a48fd); ✅ **(sweep 2)** `by-stock-range` now server-capped at 50k most-recent rows via an optional `maxRows` param threaded through `IDataBaseService`/`PgDBService`/`ApiDataBaseService`/`TransactionController` — the candle/backfill callers (`CandleService`, `MarketLookupService`) pass null for the full window, so aggregation is unaffected. 🟡 still open: `GetTransactionsByUserId` / `GetOrdersByUserId` have no cap (consumers are per-human, so lower risk). |
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

## Next sweeps (priority order)
1. **A2** — line-audit the remaining ~10 subscriber VM handlers for off-thread mutation. *Top.*
2. **A5 / A6** — fire-and-forget + timer-stop audit in client VMs.
3. **B1** — token-expiry / reconnect behavior.
4. **D2 tail** — `GetTransactionsByUserId` / `GetOrdersByUserId` caps (lower risk).
6. **B1** — token-expiry/reconnect behavior.
