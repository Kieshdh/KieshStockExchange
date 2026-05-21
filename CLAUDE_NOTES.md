# Upcoming Edits — Backlog

Working notes for upcoming Claude sessions. Items are grouped by theme, not strictly ordered. When picking one up, read the relevant code first per CLAUDE.md and confirm the current state — items may have shifted since this was written.

---

## Current focus — starting implementation

**Active track: Item 2.4 — move the engine + bots to an online server.** See the expanded phased plan in section 2.4 below. Realistic effort with Claude Code helping: ~3–5 weeks of focused work; the long pole is Phase 3 (multi-user concurrency in the engine), which is testing-bound, not typing-bound.

Recommended kickoff: a 2–3 day spike covering Phase 1 + Phase 2 only (shared contracts + HTTP reads, engine still local). That validates project layout, auth approach, and latency feel before committing to the full migration.

Decisions still needed before Phase 0 closes:
- Server-side DB: Postgres (recommended for v2) vs. keep SQLite for the prototype.
- Hosting target: local VM, Azure App Service, something else.
- Auth: JWT bearer (recommended — works cleanly with SignalR) vs. cookies.
- Whether to keep a small SQLite cache on the client for offline reads.

---

## Recommended implementation order

Sequence chosen to (a) finish what's already started, (b) land cheap UX wins, (c) clean up the engine *before* multi-user concurrency exposes its bugs, (d) expand the economy while iteration is still fast and local, (e) migrate online last when the system is feature-complete and bug-light.

**Total realistic timeline with Claude Code helping: ~11–17 weeks** (~3–4 months) of consistent focus. Migration alone is the largest single chunk; Waves 1–6 total roughly 7–11 weeks.

### Wave 1 — Land in-flight work (this week) ✅ DONE
1. Chart MA/EMA + crosshair + price markers ✅ DONE
2. Deposit/Withdraw + FundTransaction ✅ DONE
3. UserPreferences (Theme + BaseCurrency persistence) ✅ DONE

### Wave 2 — Quick UX wins (1–2 weeks) ✅ DONE
4. Reload flicker fix (4.2) — biggest perceived-quality win for the effort ✅ DONE
5. Fund transaction history view (1.1) ✅ DONE
6. Order modify UI (1.4) — `ModifyOrderAsync` plumbing already exists ✅ DONE
7. Market page trending load gap (4.1) ✅ DONE
8. Volume bars overlaid on chart (4.4) — do while in-flight chart code is fresh ✅ DONE

### Wave 3 — Engine + bot fixes (2–3 weeks) — **must finish before Wave 7 Phase 3**
9. Engine audit pass (5.1) — fix buyer balance race + maker-fill OpenOrders lag from `project_market_engine_status.md` ✅ DONE
10. Reduce bot transaction failure rate (2.3) ✅ DONE
11. Better bot starting cash/stock distribution (2.2) ✅ DONE
12. Order book price-range bucketing (4.3) ✅ DONE

### Wave 4 — Economy expansion (2–3 weeks)
13. Expand stock universe + realistic market caps (3.1) ✅ DONE
14. Multi-currency trading (3.2) — engine already keys by `(StockId, CurrencyType)` ✅ DONE
15. Investigate steady price drift — bot economy balance (3.3) ✅ DONE
16. Multi-timescale bot sentiment + rare-event shocks (3.4) ✅ DONE
17. Periodic bot cash injections — nominal-growth driver (3.5) ✅ DONE

### Wave 5 — Watchlist + notifications (1 week)
15. Watchlist (1.3)
16. NotificationService UI surface (1.2) — implement in-process; hub-push transport comes free in migration Phase 4 ✅ DONE

### Wave 6 — Admin + responsive layout (1 week, slottable earlier as a break)
17. Admin tables: column improvements + new FundTransactions and AIUser tables (4.9)
18. Admin sort button restyling (4.8)
19. Admin pagination scales with window height (4.7)
20. Bot activity graph on Bot Dashboard (2.1)
21. Account page proportions (4.6)
22. Responsive layout audit across pages (4.5)

### Wave 7 — Online migration (3–5 weeks)
23. Item 2.4 — see full phased plan in section 2.4. **Don't mix migration with new features** — that's the #1 way these projects slip.

### Why this order
- Wave 1 unblocks Wave 2 mechanically (Fund tx history needs FundTransaction; volume overlay builds on the chart changes).
- Wave 3 is the most important discipline call: a race that's "low probability" with one user is "every minute" with fifty. Fix engine bugs *before* going multi-user.
- Wave 4 is best done locally because the iteration loop is faster — re-seeding the universe takes seconds locally vs. a deploy cycle on the server.
- Wave 5 places notifications before migration deliberately: a working in-process notification system means migration Phase 4 just swaps the transport, not the consumer.
- Wave 6 has no hard dependencies; slot earlier as a change of pace if needed.
- Wave 7 last because by then the system is feature-complete and bug-light — the migration is purely lifting it onto a different runtime, not shipping new behavior at the same time.

---

## Hosting cost (for Wave 7)

Spike (Phases 1+2) costs **$0** — server runs on `localhost` alongside the MAUI client. Real hosting only starts once you commit past the spike.

For single-user + bot fleet (CPU-bound, low bandwidth), realistic monthly cost:

| Option | Cost / mo | Notes |
|---|---|---|
| Home server + Cloudflare Tunnel / Tailscale | $0 | Encrypted public access, no cloud bill |
| Oracle Cloud Free Tier | $0 | 4 ARM vCPUs, 24 GB RAM, forever-free |
| Hetzner CX22 + self-hosted Postgres | ~€5 (~$5.50) | Recommended starting point |
| Fly.io shared CPU + small Postgres | ~$5–10 | Easier deploy story |
| DigitalOcean droplet | ~$12 | Self-host Postgres on same box |
| AWS Lightsail | ~$10–12 | All-in pricing |
| Azure App Service B1 + managed Postgres | ~$25–30 | Zero-ops trade-off |

Hidden costs: domain (~$10–15/year, optional), self-host ops time (~1–2 hrs/month for backups, OS updates, cert renewal). Bandwidth, SSL (Let's Encrypt), and free-tier monitoring are negligible at this scale.

**Recommendation**: spike on localhost ($0), first real deploy on Hetzner (~€5/mo) or Oracle Free Tier ($0). Move to managed only if zero-ops becomes worth the money.

---

## 1. Feature additions (originally requested)

### 1.1 Fund transaction history view ✅ DONE
- `FundTransactionHistoryPage.xaml(.cs)` + `FundTransactionHistoryViewModel.cs`
  back the page; surfaced from `AccountPage` via "Transaction history" button
  under the Funds card. Opens in a 720×600 child window.
- Data path: `IUserPortfolioService.GetFundTransactionsAsync()`.

### 1.2 NotificationService implementation ✅ DONE
- UX: toast overlay (`ToastHostView` + `ToastHostViewModel`, 3 concurrent, 4s auto-dismiss) + persistent in-memory inbox (50-item ring buffer in `NotificationService`).
- Bell + unread badge in `TopNavBarView`; clicking opens `InboxPopup` (CommunityToolkit.Maui `Popup`, taps-outside dismiss + X close).
- Hooked into: order fills (via `NotificationBridgeService` diffing `OrderCacheService.OrdersChanged`), order placements/rejections (`PlaceOrderViewModel`, `ModifyOrderViewModel`), deposits/withdrawals (`DepositWithdrawViewModel`).
- In-memory only — no `Notifications` table. Hub-push transport will swap in during migration Phase 4.
- Follow-on: migrated Account modals (Change Email/Password/Username, Deposit/Withdraw, Convert Currency, Fund Transaction History) from `Application.Current.OpenWindow` to `toolkit:Popup` via `AccountViewModel.ShowAccountPopupAsync<TPopup>`.

### 1.3 Watchlist
- Per-user list of favorited stocks. Likely a star toggle on the Market page rows + a "Watchlist" filter/tab.
- New table `UserWatchlist (UserId, StockId, AddedAt)` with composite PK.
- Add to `IUserPortfolioService` or split into a dedicated `IWatchlistService` (probably the latter — clean separation).
- Filter wired into `MarketPage` pagination and shown as a separate tab.

### 1.4 Order modify UI ✅ DONE
- Plumbing already exists: `OrderExecutionService.ModifyOrderAsync`.
- Need: edit affordance on `OpenOrdersView` rows (price + quantity, not type).
- Validation must mirror entry validation; reuse `OrderValidator` rather than duplicating rules.
- Settlement engine already handles the cancel-and-replace inside; just expose the path through the VM.

---

## 2. Bot / AI improvements

### 2.1 Bot activity graph on Bot Dashboard
- New chart on `BotDashboardPage`: x = time, y = number of currently active bots.
- Sample on a timer (~1s) using `IAiTradeService` active-bot count; ring buffer for the last N minutes.
- Reuse `CandleChartDrawable` patterns? Probably simpler to write a small line-chart drawable since there's no OHLC.
- Pair with the existing Load % / EWMA readouts so trends are visible alongside scaler decisions.

### 2.2 Better starting cash/stock distribution for bots ✅ DONE
- Generator side is correct: `Tools/Person.py::_portfolio` targets ~63–90%
  in stocks (max_cash ≈ 17–58%, min_cash ≈ 6–35%, midpoint allocation to
  shares). Distribution is already in the 65–85% band the planning called for.
- Generator tooling extracted to `Tools/Config.py` (dict-of-dicts STOCKS,
  tunables); `GenerateAIUsers.py` now re-enables the Stocks-sheet write +
  seeds RNG so output is reproducible.
- Action item to actually pick up the new distribution: re-run
  `python Tools/GenerateAIUsers.py` to refresh `AIUserData.xlsx`, then the
  next app startup reseeds Funds/Positions automatically.
- `AiBotDecisionService` has no 50/50 assumption — verified during the
  earlier engine work; reads live cache state, not seed ratios.

### 2.3 Reduce bot transaction failure rate ✅ DONE
- Currently averaging ~1 failed bot transaction per tick.
- Investigate root causes via the improved Warning logs from `AiTradeService.RunLoopAsync`.
- Suspects: race between committed-amount calc and concurrent fills, settlement seller quantity check (item 10 in the engine notes), mid-tick price moves invalidating a chosen limit price.
- Goal is "near zero" failures; failures should be unexpected, not routine.

### 2.4 Move engine + bots to a server (ACTIVE TRACK)

Largest item by far. Goal: UI is faster (no engine work on the local machine) and bots keep trading while the user is offline. The interface-based DI in this repo is the asset that makes the migration tractable — most ViewModels never need to know whether `IMarketDataService` / `IOrderEntryService` / etc. live in-process or behind a network call. Only the implementations registered in `MauiProgram.cs` change.

**Effort with Claude Code: ~3–5 weeks of focused work.** Phases 1, 2, 4, 5 are boilerplate-heavy and benefit a lot from generation. Phase 3 is the long pole — concurrency bugs and manual MAUI testing don't speed up.

#### Phase 0 — Architecture decisions (do first, before any code)
- **Transport**: ASP.NET Core Web API for request/response (orders, login, history) + **SignalR** for live ticks/quotes. Native .NET, integrates with auth.
- **Database**: Postgres for v2; SQLite acceptable for the prototype (single-instance only).
- **Hosting**: dev = `dotnet run` locally, prod TBD (small VM or Azure App Service).
- **Auth**: JWT bearer tokens — stateless, works cleanly with SignalR via `AccessTokenProvider`.
- **Solution shape**: three projects — `KieshStockExchange` (MAUI client), `KieshStockExchange.Server` (ASP.NET Core), `KieshStockExchange.Shared` (Models + DTOs + interfaces both sides reference).

#### Phase 1 — Extract shared contracts (~1–2 days)
- Create `KieshStockExchange.Shared`. Move: all `Models/`, the DTO shapes for the wire (likely 1:1 with models, SQLite attributes split off into a server-only partial), and the interfaces both sides talk to.
- Pure refactor — app still runs as today.

#### Phase 2 — Server skeleton, no engine yet (~3–5 days)
- Move `IDataBaseService` + `LocalDBService` server-side. Client no longer touches the DB directly.
- Build HTTP endpoints mirroring current data calls (`GET /stocks`, `GET /users/{id}/funds`, `GET /orders?status=open`, etc.).
- Client gets `ApiDataBaseService : IDataBaseService` (HTTP calls instead of SQLite). Swap in via `MauiProgram.cs`.
- Engine still runs locally. Order placement will feel laggy from network round-trips — that's expected, fixed in Phase 4.
- **Audit hot paths** (chart load, market list) for any code that called the DB synchronously in tight loops; those need batching now.

**Spike checkpoint:** Phases 1 + 2 are the recommended 2–3 day spike. After this, decide whether to commit to the full plan.

#### Phase 3 — Move engine + bots server-side (~1–2 weeks, long pole)
- Move entirely server-side: all of `MarketEngineServices/` (entry, execution, matching, settlement, caches), `AiTradeService` + `BotScalerService` + `BackgroundServices/Helpers/`, `CandleService`, `PriceSnapshotService`, `TrendingService`, `MarketDataService`, `MarketLookupService`.
- Register the bot service as an ASP.NET Core `IHostedService` so it starts with the server process and runs independently of any client. **This is the line where bots-keep-trading-while-offline becomes true.**
- Client-side: replace these services with thin proxies (`HttpOrderEntryClient` that POSTs to `/orders`; `SignalRMarketDataClient` set up in Phase 4).
- **Concurrency audit — critical here:**
  - Locks in `OrderBookCache` / `OrderCacheService` need to handle real concurrent users, not just bots.
  - The "no buyer balance check in `SettleTradesAsync`" race risk in `project_market_engine_status.md` (Known remaining issues) becomes critical — fix before going multi-user.
  - `RunInTransactionAsync` (savepoints via AsyncLocal) needs to map onto the server DB's transaction model. SQLite→Postgres slightly changes nested-transaction semantics.

#### Phase 4 — Real-time channel via SignalR (~2–4 days)
- Add `MarketHub` server-side. Methods: `Subscribe(stockId, currency)`, `Unsubscribe(stockId, currency)` — map to existing `forUi:true` subscription path.
- Hub uses **groups** keyed by `(stockId, currency)`. On server-side tick, broadcast to that group.
- Replace `MarketDataService.QuoteUpdated` event publication with `hub.Clients.Group(key).SendAsync("QuoteUpdated", ...)`.
- Client `SignalRMarketDataClient` re-raises `QuoteUpdated` the same way `MarketDataService` does today. **ViewModels see no change.**
- Hub callbacks arrive on a non-UI thread; existing `SelectedStockService.OnQuoteUpdated` already self-marshals via `MainThread.BeginInvokeOnMainThread`. Audit other subscribers for the same pattern.

#### Phase 5 — Authentication (~2–3 days)
- Server: install `Microsoft.AspNetCore.Authentication.JwtBearer`. `AuthService.LoginAsync` issues a signed JWT containing `UserId`.
- Client: store the token in MAUI `SecureStorage`. Inject into every HTTP request and the SignalR connection (`AccessTokenProvider` on the hub builder).
- Server middleware reads `User.Identity` from JWT — every endpoint and hub method becomes user-aware automatically.
- `UserSessionService` shrinks: no longer owns "who is the active user?" via a static field — that's the JWT. Still owns local cache of the active user's funds/positions.
- `LoginViewModel` smallest change of any VM: same `await login`, but it's an HTTP call returning a token + opens the hub.

#### Phase 6 — Client cleanup (~3–5 days)
- Remove SQLite-net-pcl from the client project entirely.
- Remove `ExcelImportService` from client (server-side seed work).
- Decide what to cache locally (probably last known holdings + open orders) so the UI doesn't blank during reconnect. A small SQLite cache DB is reasonable here — different role than the old "SQLite is source of truth" model.
- Add reconnection handling: SignalR auto-reconnect + a banner ViewModel that flips an `IsConnected` flag for the UI to dim live data when disconnected.

#### Phase 7 — Operational (ongoing)
- **Schema migrations**: switch from `SQL.txt` + `CreateInvariantTriggers` boot-time setup to EF Core Migrations or a Postgres tool (FluentMigrator, DbUp). One-time-init scripts don't survive deployment cycles.
- Backups + monitoring (file logging at minimum).
- `/version` endpoint; client refuses to start against a server it doesn't match. Saves "old client, new server" debugging hell.
- Rate limiting on order placement endpoints — bots run server-side now, so the only abuse vector is malicious clients.

#### Things that block other backlog items
- 3.2 (Multi-currency) is much easier server-side because the engine already keys order books by `(StockId, CurrencyType)`.
- 1.2 (Notifications) becomes natural to push via the hub once the channel exists.
- 4.1 (Market page trending load gap) becomes a hub-prime-on-connect rather than a timer-only refresh.

---

## 3. Stocks & economy

### 3.1 Expand stock universe + realistic market caps ✅ DONE
- ✅ Expanded 21 → 50 stocks in `Tools/Config.py::STOCKS`, dict keyed by
  StockId in market-cap descending order (largest first).
- ✅ Power-law watchlist sampling implemented in `Tools/Person.py::_portfolio`
  via `weighted_sample_no_replace` (Efraimidis–Spirakis) with weight
  `1/sid**WATCHLIST_WEIGHT_ALPHA`. At α=1.2 (current), top-10 stocks land
  in ~5× more watchlists than bottom-10.
- ✅ Source data path extended: `ExcelImportService.AddStocksFromExcelAsync`
  already reads sheet 0; `AddHoldingsFromExcelAsync` row check now scales
  with `stockCount` instead of hardcoded `* 21`.
- ✅ Runtime weighting: `AiBotDecisionService.ChooseStockId` now uses a
  roulette-wheel `WeightedPick` with `1/sid^0.7` so bigger-cap names trade
  more often during the simulation. Applied on top of the seed bias
  (compounding alpha 0.7 runtime × 1.2 seed); easy to tune.
- ⏭ Excel needs regenerating (`python Tools/GenerateAIUsers.py`) for the
  new universe to land in the running app.

### 3.2 Multi-currency trading ✅ DONE
- Landed: `StockListing` model (per-currency listings per stock) +
  `StockListingSeed` for initial population. `IStockService.GetListings` /
  `IsListedIn` consumed by `SelectedStockService.Set` and the
  `TradingPair` picker on the Trade page.
- `FxRateService` (`Services/MarketDataServices/FxRateService.cs`) +
  `ConvertCurrencyPage` / `ConvertCurrencyViewModel` give users an explicit
  FX path between funds. `IUserPortfolioService.GetFundByCurrency` is the
  canonical lookup for per-currency balances.
- Bots trade every supported currency — `UserSessionService.InitializeBackgroundServicesAsync`
  passes `CurrencyHelper.SupportedCurrencies` to `AiTradeService.Configure`,
  and `AiBotDecisionService.ChooseStockId` filters each bot's watchlist to
  stocks listed in the currency being considered.
- `UserPreferences.BaseCurrency` wired through:
  `AccountViewModel` + `TopNavBarViewModel` subscribe to
  `IUserSessionService.SnapshotChanged` and re-read funds via
  `GetFundByCurrency(session.BaseCurrency)`.
- Verified by 2026-05-19 overnight run: USD + EUR both active, EUR is ~42%
  of reservation-ledger rows (13,358 / 31,868 currency-tagged entries) and
  ~31% of total session wealth ($465M cash + $2.76B shares EUR vs $1.05B +
  $4.27B USD). GBP/JPY/CHF/AUD plumbed through `CurrencyType` /
  `CurrencyHelper` but currently zero listings — flip a listing on if/when
  that universe expansion is wanted.

### 3.3 Investigate steady price drift in bot-driven economy ✅ DONE
- Resolution summary: drift slope reduced from ~9.2%/hr (baseline) to
  ~4.2%/hr after three structural fixes. Residual is stochastic
  accumulation on low-volume stocks (mid-tier cap-weighted names),
  not a code bug.
- Fixes that landed:
  1. **Suspect 3** (commit `d225fae`): `AiBotContext.FundsPercentagePortfolio`
     numerator `AvailableBalance` → `TotalBalance`. Open limit-buy
     reservations no longer push bots toward selling.
  2. **Suspect 2** (commit `e2ab2b2`): `AiBotDecisionService.ComputeOrderPriceAsync`
     limit-order anchor switched from last-trade price to mid-price.
     Removed the structural ratchet where buys filling at the ask kept
     pulling the reference up.
  3. **Suspect 1** (commit `5a0681b`): `AiBotDecisionService.ChooseOrderType`
     symmetrised TrendFollower/MeanReversion momentum magnitudes to
     ±0.175 (was +0.20 vs −0.15). Removed the +0.05 net buy bias per
     unit momentum across the 25/25 strategy split.
- Telemetry harness added in commit `d225fae`: `BotEconomyTelemetry`
  records 60s wealth + drift snapshots, CSV export via Bot Dashboard.
- Original notes (kept for context):
- Symptom: with bots running, stock prices trend upward steadily over a session
  instead of mean-reverting around their seeded levels. Suggests a systemic
  imbalance somewhere in the bot decision / matching / settlement loop.
- Suspect areas to investigate:
  - **Bot cash injection vs. drain**: are bots starting with too much cash
    relative to the float available to trade? Imbalance between buy-side and
    sell-side pressure inflates prices. Check `Tools/Person.py::_portfolio`
    cash/stock split vs. total seeded share supply.
  - **Buy/sell decision bias**: `AiBotDecisionService.ChooseSide` — is the
    probability of buying systematically higher than selling at neutral
    momentum? Even a small asymmetry compounds.
  - **Price reference for limit orders**: `ChooseLimitPrice` may be anchoring
    on the last trade and adding spread — if buyers pay above last and sellers
    ask above last, midprice walks up. Check that the offset is symmetric.
  - **Settlement-side share creation**: confirm no path mints positions
    without a counter-seller (rare but worth auditing — every fill should be
    a zero-sum exchange between two users).
  - **Bot top-up / refill behavior**: any periodic cash injection
    (e.g. via `AiBotStateService` refresh) adds buying power without adding
    shares → net upward pressure.
- Verification approach: run with bots for a fixed window, log total bot
  cash + total bot share value at start vs. end. Total wealth should be
  approximately conserved (modulo fees if any); if it grows, something is
  injecting value.
- Likely fix layer: `Helpers/AiBotDecisionService.cs` for behavioral biases,
  `Tools/Config.py` / `Tools/Person.py` for seed imbalances, settlement
  engine for accounting bugs.

### 3.4 Multi-timescale bot sentiment + rare-event shocks ✅ DONE
- v1 (helper, inert) landed in commit `61a40f0`: `BotSentimentService`
  with AR(1) factors at 24h / 1h / 10m / 1m, per-stock + global, seeded
  from steady-state on session start. Wired through
  `AiTradeService.CheckTimers`.
- v2 (integration) integrates sentiment into `AiBotDecisionService`:
  watchlist-averaged sentiment drives a linear bias on `buyProb`
  (±0.20 max); per-stock raw sentiment crossing ±1 triggers a
  style-dependent forced TrueMarket order with probability
  proportional to overflow (`OverflowGain = 0.5`).
- New `AIUser.ExtremeReactionRandomnessPrc` field (range [0, 0.5],
  skewed toward 0). Default style derived from `AiStrategy`:
  TrendFollower → FOMO; MeanReversion / MarketMaker → Contrarian;
  Scalper → Panic; Random → None. Per-bot randomness picks a uniform
  random style when the roll lands below `RandomnessPrc`.
- Rare-event Poisson shocks were considered and dropped — the
  overflow mechanism already produces realistic extreme-reaction
  behaviour without a separate shock layer.
- Action item to land for the running app: re-run
  `python Tools/GenerateAIUsers.py` to add the new column to
  `AIUserData.xlsx`, then restart the app.
- Original design notes (kept for reference):
- Goal: introduce random sentiment shocks at multiple timescales so the
  market shows organic-looking trends and reversals instead of a flat
  random-walk. Each bot's buy/sell preference is biased by the sum of
  several sentiment factors drifting on different clocks.
- Proposed scales (per-stock, possibly global as a separate factor):
  - **24h** — slow regime drift (bull/bear day)
  - **1h** — session swings
  - **10m** — shorter momentum bursts
  - **1m** — micro noise
- Each factor is a value in `[-1, +1]` (negative → sell-leaning, positive →
  buy-leaning); they sum (or weighted-sum) into a single sentiment scalar
  applied in `AiBotDecisionService.ChooseSide` as a bias on top of the
  existing momentum/cash-share signals.
- Update model: each factor follows a mean-reverting random walk (Ornstein–
  Uhlenbeck style) — bounded, slow drift, occasional reversals. New random
  draw on each factor's tick (24h-factor reseeds once a day, 1h-factor every
  hour, etc.). Implementation can be discrete: re-roll each factor when its
  clock expires, optionally interpolate between rolls.
- Per-bot vs. shared: leaning is **shared market sentiment** (all bots see
  the same 24h/1h/10m/1m factors for a given stock), but each bot mixes it
  with its own RNG so behavior diverges. Optional per-bot personality
  multiplier (`AIUser.SentimentSensitivity` ∈ ~[0.5, 1.5]) so some bots
  follow the herd harder than others.
- Per-stock vs. global: probably both — a global "market mood" factor plus
  per-stock factors. Sector grouping is a stretch goal.
- Likely home: new `BotSentimentService` (singleton, owns the per-stock
  factor state + the timers) consumed by `AiBotDecisionService`. Sentiment
  state lives in memory only; doesn't need persistence.
- Tunables (probably Config.py-side mirrored to a C# const block):
  amplitude per timescale (e.g. 24h ±0.6, 1h ±0.4, 10m ±0.25, 1m ±0.1),
  mean-reversion strength, optional per-bot sensitivity range.
- Watch out for: sentiment compounding with the price-drift issue (3.3) —
  fix 3.3 first or these factors will pile on top of an already biased
  baseline and mask the root cause.
- **Rare-event shocks (disasters / good news)** — a Poisson-process layer
  on top of the OU mean-reverting factors. Per-stock, low frequency
  (mean inter-arrival ~2–6 simulated hours), magnitude ±5–20% sentiment
  jump that decays exponentially over a few hours. Examples: earnings
  beat, recall, fraud headline. Stretch: sector contagion (a shock on
  one stock partially propagates to others in the same sector — would
  need a Sector field on Stock first). The same `BotSentimentService`
  hosts both layers; rare events just add to the current factor sum
  instead of replacing it.

### 3.5 Periodic bot cash injections — nominal-growth driver ✅ DONE
- Landed: `BotCashInjector` (`Services/BackgroundServices/Helpers/BotCashInjector.cs`),
  per-bot `CashInjectionFrequencyPrc` + `CashInjectionAmountPrc` knobs seeded
  inverse to portfolio value, hourly cycle driven from `AiTradeService.CheckTimers`,
  deposits through `IUserPortfolioService.AddFundsAsync` so the reservation
  ledger stays consistent. `BotEconomyTelemetry.RecordInjection` +
  `TotalInjectedThisSession` column in the economy CSV.
- Master `Enabled` switch flipped on 2026-05-19 after the overnight dry run.
  Verified via 2026-05-19 02:54–08:45 UTC CSVs: 6 cycles fired exactly hourly
  (03:54–07:54), per-cycle range $446k → $923k, session total $3.64M.
  Wealth conserved at the cash level (injected cash deployed into shares the
  same hour); reservation ledger had zero negative-balance rows; no failure
  category change beyond the existing rounding-grade InsufficientFunds.
- Original design notes (kept for reference):
- After 3.3 lands, the trading mechanics are symmetric and the bots
  random-walk prices around a flat level (no drift up or down on
  average). That matches a closed market with no inflows. Real
  markets see nominal price rise over time because new money keeps
  flowing in from wages, savings, retained earnings, etc. Adding a
  small, periodic cash deposit to bots replicates that.
- Mechanism: every N minutes (e.g. 10 simulated min), pick a random
  subset of bots and add a small cash amount each. Goal is a controlled
  fleet-wide nominal growth rate — target maybe ~5–10%/yr equivalent
  when annualised.
- Sizing knobs (Config.py-style):
  - `CashInjectionIntervalMinutes` — cadence (default 10 min)
  - `CashInjectionFleetFraction` — fraction of bots that receive a
    deposit per cycle (default ~20%)
  - `CashInjectionAmountPctOfPortfolio` — per-bot deposit as a fraction
    of that bot's current portfolio value (default ~0.05% so a $100k
    bot gets $50 per hit)
- Likely home: new helper `BotCashInjector` (or method on
  `AiBotStateService` if scope stays small), triggered from a new
  timer in `AiTradeService.CheckTimers`. Each deposit goes through the
  engine's fund-add path so the reservation ledger and audits stay
  consistent — no direct mutation of `Fund.TotalBalance`.
- Telemetry: BotEconomyTelemetry already tracks `TotalCash`; with
  injections live it'll grow over time. Useful sanity check that
  injections fire correctly. Could add a separate `InjectedThisSession`
  counter if helpful.
- Interaction with 3.3 verification: do **not** turn this on while
  validating the drift fix — it would mask whether residual drift is
  bug or injection. Add a hard off-switch (`CashInjectionEnabled = false`
  by default) and turn it on only after 3.3 is closed.
- Stretch: instead of equal-weighted random subset, weight selection by
  aggressiveness (aggressive bots earn more, like real income skew) or
  randomise the per-bot amount with a heavy-tailed distribution.

---

## 4. UI / UX polish

### 4.1 Market page: trending stocks initial-load gap ✅ DONE
- For the first few seconds, trending list shows nothing.
- `TrendingService` is timer-only (5s) per the perf overhaul; need an immediate first refresh on subscribe rather than waiting for the next tick.
- Or: prime from cached snapshot on startup.

### 4.2 Reload flicker (numbers disappear briefly) ✅ DONE
- Live update path is replacing the whole VM collection on each refresh, which causes a brief blank frame.
- Switch to incremental updates (update existing items in place) so values blend rather than flash.
- Affects portfolio holdings, market list, possibly admin tables.

### 4.3 Order book: price-range bucketing ✅ DONE
- Currently every price level is its own row → bottom of book is invisible at low zoom.
- Bucket adjacent prices into ranges (look at how Binance / IBKR do it — typically 0.01, 0.10, 1.00 step buckets the user can pick).
- New helper in `OrderBookView` VM: aggregator that takes the raw book + a step size and emits bucketed levels.
- Add a "depth" picker so the user can choose granularity.

### 4.4 TradingView-style volume bars on chart ✅ DONE
- Currently volume is a separate panel; want it overlaid on the price chart with transparency.
- Touches `CandleChartDrawable` — render volume bars first with low alpha + a separate y-axis scaled to the bottom ~20% of the chart area.
- Keep volume axis hidden by default (TradingView-style).

### 4.10 Open orders as price lines on chart (Wave 2 follow-on) ✅ DONE
- The user's open limit orders for the selected stock+currency render as dashed
  horizontal lines on the chart (green for buy, red for sell) with a side+qty
  tag in the right gutter.
- `OpenOrderLine` value type lives in `ChartTypes.cs`; `ChartViewModel` syncs
  the collection from `IOrderCacheService.OrdersChanged`; the drawable's
  `DrawOpenOrderLines` runs before the live-price line.

### 4.11 Drag-to-modify open orders on chart ✅ DONE
- Binance and TradingView both let you grab an open-order line on the chart,
  drag it to a new price, and confirm. We already render the lines (4.10) and
  already have the marker-drag pattern in `CandleChartDrawable.HitMarker` /
  `_dragMode` in `ChartView.xaml.cs`.
- Implementation sketch:
  - Extend the chart drawable with `HitOpenOrderLine(PointF)` returning the
    `OpenOrderLine` under the pointer.
  - Add a new `DragMode.OpenOrder` to ChartView's pointer handler. On drag
    move update a transient overlay price; on release, open the existing
    Modify popup pre-filled with the new price (skip the qty step).
  - Reject drag on market orders.

### 4.5 Responsive layout for window resizing
- Layouts feel cramped on small windows and empty on large ones.
- Audit each `ContentPage` for `Auto` vs `*` row/column distributions.
- Consider `OnIdiom` / `OnPlatform` with breakpoints, or a custom `VisualStateManager` setup.
- Pages most affected: Trade, Portfolio, Market, Admin tables.

### 4.6 Account page proportions
- Currently feels lopsided; rework to more even / square layout.
- Two equal columns is probably right — the right column ("Funds + Preferences") looks lighter than the left ("Identity + Security").
- Add either more content to the right (recent fund tx? base currency display?) or rebalance card heights.

### 4.7 Admin page: more rows when window is bigger
- On large screens the admin tables look empty.
- Pagination size should scale with window height, not a fixed value.
- New computed property in the table VMs: `PageSize = max(20, floor((viewportHeight - chrome) / rowHeight))`.

### 4.8 Admin page: sort button restyling
- Sort buttons clash with the new theme styles.
- Move to shared XAML in `MyStyles.xaml` (or a dedicated `AdminStyles.xaml`); ensure light + dark variants both work.

### 4.9 Admin tables: column improvements + new tables
- Audit each `*TableView.xaml` for missing columns / unhelpful column order.
- Add new admin views for `FundTransactions` and `AIUser` (bots) — both already have models / data.
- Mirror existing pagination / sort patterns.

### 4.13 Modify-buy-above-market matching — verified working (timing-dependent)
- Original concern: `LimitBuy` modified to a price visibly above last-trade
  sometimes ends with `PlacedOnBook` instead of filling.
- Verified working: with a 2-share LimitBuy modified $535→$550 against a maker
  at $540, the engine produced `tradePrice=540` (maker price) and order
  status `Filled`. Engine matching is correct.
- Root cause of the original observation: the diagnostic match log only fires
  on actual fills, so when the bot fleet (~14k–20k active) churns the book
  faster than the "market price" chart updates, a modify can land in a
  millisecond where the best ask is above the modified limit. The order
  correctly rests. The next moment, asks drop and other orders fill —
  creating the false impression that "the same modify should have filled".
- If a similar concern resurfaces with bots paused / scaled down (stable
  book), it's worth adding a "Match: 0 fills, best opposite = X" diagnostic
  log line in the no-match branch so the absence of a fill becomes visible.

### 4.12 Portfolio page: equity + cash values look wrong ✅ DONE
- Root cause confirmed: `PortfolioViewModel.RecomputeSummary` was summing
  `f.AvailableBalance` instead of `f.TotalBalance` for the cash figure,
  so resting limit-buy reservations subtracted from the displayed
  cash + equity. Position side was already correct (`pos.Quantity *
  LivePrice`); only the fund side needed the swap.
- Fix: `PortfolioViewModel.cs:131` — `AvailableBalance` → `TotalBalance`.

---

## 5. Engine / performance

### 5.1 MarketEngine audit pass ✅ DONE
- Walk through `OrderEntryService` → `OrderExecutionService` → `MatchingEngine` → `SettlementEngine` end-to-end again.
- Look for: avoidable allocations on the hot path, lock contention, DB calls inside batch loops.
- Check the "Known remaining issues" in memory:
  - No buyer balance check in `SettleTradesAsync` (race risk).
  - Maker fills don't remove from `OpenOrders` until 1-min refresh.
- Profile under bot fleet load — what's the per-tick CPU profile look like now?

---

## Cross-cutting notes

- **Already in flight (uncommitted):** Chart MA/EMA + crosshair + price markers, DepositWithdrawPage + FundTransaction, UserPreferences. Land these first before starting new work — several items above depend on them.
- **Dependencies to flag:**
  - 1.1 (Fund tx history) blocks on the in-flight FundTransaction work.
  - 3.2 (Multi-currency) depends on `UserPreferences.BaseCurrency` being persisted.
  - 4.4 (Volume overlay) builds on the in-flight chart drawable changes.
  - 2.4 (Engine on server) is a major architectural shift — should be planned independently before any incremental work tries to anticipate it.
- **Testing reminder (CLAUDE.md):** Manual through the running app; no automated tests assumed unless explicitly added.
