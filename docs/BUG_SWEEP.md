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
| A3 | Event subscription without unsubscribe → leak + callback into disposed VM | VMs subscribing to singleton services (`IMarketHubClient`, `IUserSessionService`, `IOrderCacheService`, `IUserPortfolioService`) | 🟢 sub/unsub balanced across subscribers. 🟡 `ChartViewModel` shows 4 `+=` / 2 `-=` — verify. `NotificationService` 2`+=`/0`-=` is singleton↔singleton (harmless). |
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
| C1 | sync-over-async (`.Result`/`.Wait()`/`GetResult()`) → thread starvation/deadlock | grep across server | 🟢 `StockService` `.Result` is post-`WhenAll` (safe). 🟡 `MauiProgram.cs:252` + `OrderBookBroadcaster.cs:146` `GetResult()` — review (server has no sync ctx, so starvation not deadlock). |
| C2 | Shared mutable cache without locking under the bot fleet | engine caches, `AccountsCache`, order books | 🟢 covered by conservation/reservation probes — silent through the sweep-1 load soak (item 41). |
| C3 | Escaped background-loop exception vanishes | hosted services, bot loop | ✅ global net added (`Program.cs` TaskScheduler/AppDomain handlers, commit fa153df). |

## D. Database / Dapper / Postgres

| # | Bug class | Where to look | Status |
|---|---|---|---|
| D1 | SQL injection via string-interpolated identifiers | `PgDBService.*` dynamic ORDER BY / WHERE | 🟢 `sortKey` whitelisted everywhere; filters parameterized. |
| D2 | Unbounded query (no LIMIT) → memory/DoS | `PgDBService` list queries | ✅ paging clamped (commit c3a48fd). 🟡 **OPEN, top priority:** `GetTransactionsByStockIdAndTimeRange` (`/transactions/by-stock-range`) takes from/to with no row cap — a wide window streams the whole tape. **Constraint found:** the same DB method is reused internally by `CandleService:495/559` + `MarketLookupService:95` for candle building over full windows, so a blanket data-layer `LIMIT` would corrupt candle aggregation. **Fix plan:** add an optional `int? maxRows = null` param (mirror `GetTransactionsSinceTime`) → most-recent `ORDER BY Timestamp DESC LIMIT n` only when set; controller passes a cap, internal candle callers pass none. Touches `IDataBaseService` + `PgDBService` + `ApiDataBaseService` + `TransactionController`. Also re-check `GetTransactionsByUserId`, `GetOrdersByUserId` (no cap; consumers are per-human so lower risk). |
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
1. **D2** — cap the unbounded transaction/user-scoped list queries (server, deployable). *Top.*
2. **A2** — line-audit the remaining ~10 subscriber VM handlers for off-thread mutation.
3. **A3** — confirm `ChartViewModel` unsubscribes everything it subscribes.
4. **A5 / A6** — fire-and-forget + timer-stop audit in client VMs.
5. **C1** — review the two `GetResult()` sites.
6. **B1** — token-expiry/reconnect behavior.
