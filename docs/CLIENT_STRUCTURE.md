# CLIENT_STRUCTURE.md — how the KieshStockExchange MAUI client works + its structure

**Product context:** KieshStockExchange is a **stock-exchange simulator**. A fleet of ~20k server-side bots continuously makes the market across 50 stocks (70 USD/EUR cross-listings); a human logs into this Windows desktop app to browse quotes, read charts, and place real orders that match against the bots' book. Everything the user *sees* is this MAUI client; everything that *happens* (matching, settlement, candles, bots) is the server.

Compact reference for the **client head** — the .NET MAUI app users actually see. Companion to `docs/BOT_MECHANICS.md` (the bot fleet) and `docs/ENGINE_MECHANICS.md` (matching/settlement); this one is deliberately **simpler** than those, because since Phase 3 (the client/server split — before it the engine ran in-process) the client does almost no thinking. **Consult + UPDATE this file whenever a client service, page wiring, or nav flow changes** (same commit).

**One sentence:** the client is a **thin presentation shell** — MVVM pages over a named `HttpClient` (REST, on-demand + mutations) and one SignalR hub (live push). No engine, no matching, no candle aggregation, no DB runs in-process; every `Api*`/`SignalR*` service is a network proxy to `KieshStockExchange.Server`.

**Topology at a glance** — every VM sits on exactly two planes:
```
  REST (request/response, on-demand + mutations):
    VM → Api* proxy → HttpClient "KSE.Server" → controller → engine/DB
  SignalR (live push, one connection):
    engine event → *Broadcaster → hub group → MarketHubClient event → SignalR*/Api* proxy → VM
```

**Glossary** (terms used before they're defined below):
- **MVVM / VM** — Model-View-ViewModel; a *VM* is the bindable state+logic object behind a page. **INPC** = `INotifyPropertyChanged`, the binding-refresh signal.
- **DI** — dependency injection; the `MauiProgram` container news up pages/VMs/services and hands them their dependencies.
- **TFM** — target framework moniker (`net9.0-windows10.0.19041.0` = the Windows build target).
- **CTS** — `CancellationTokenSource`. **JWT** — the bearer token proving who the user is. **WAC** — weighted-average cost (P&L basis).
- **listing** — a `(stockId, currency)` pair; one stock is cross-listed in USD and EUR as two listings. **the tape** — the user's transaction/fill history. **proxy / shim** — a client service that only forwards to the server.

**Map:** §1–§9 = **architecture** (how the client is wired: assemblies, DI, Shell nav, MVVM, shared state, the hub, auth, theming, and one worked end-to-end flow). §10–§15 = **surfaces** (the actual screens: Market, Trade + chart + book + ticket, Portfolio, Account, Bot dashboard, Admin). The closing section = the **REST-vs-SignalR rule of thumb** + an **invariants** checklist and an **"add a new page"** recipe. §1–§9 are the "how it's built" half; §10–§15 are the "what the user sees" half — they cross-reference, they don't duplicate. **New to the codebase?** Skim §10–§15 first to see the screens, then read Part A for the wiring behind them.

---

# Part A — Architecture

## 1. Shape of the client — one MAUI head, a thin HTTP+SignalR skin over the server

The MAUI project (`KieshStockExchange`, Windows-primary TFM `net9.0-windows10.0.19041.0`) is a **presentation shell only**. Since Phase 3, the matching engine, bots, candles, and DB all live in `KieshStockExchange.Server`; the client holds **no in-process engine and no direct DB**. *Why the client is thin:* a shared market needs **one authoritative order book and one conservation domain** — N client heads must all observe the *same* matching, so the engine cannot live inside any of them; each head is a read/write observer of the one server. Every client service whose name starts `Api*` or `SignalR*` is a proxy: reads/writes go out over HTTP, live state arrives over one SignalR hub. The old in-process duplicates (`OrderExecutionService`, `LocalDBService`, `AiTradeService`, …) were deleted — if you see a service interface, its client impl is a network shim. Even `IDataBaseService` is now `ApiDataBaseService` (first DI registration, `MauiProgram.cs`), a thin HTTP wrapper: the CLAUDE.md rule "multi-table writes use `RunInTransactionAsync()`" is a **server** concern now — the client never opens a transaction.

Three assemblies:
- **`KieshStockExchange`** — Views (XAML), ViewModels, client Services. This doc.
- **`KieshStockExchange.Shared`** (`net9.0`) — Models (`Stock`, `Order`, `Fund`, `Position`, `LiveQuote`, `Candle`, `PortfolioSnapshot`, `Message`), helpers, enums (`CurrencyType`, `CandleResolution`). Referenced by both client and server, so wire DTOs are literally the same types on both ends.
- **`KieshStockExchange.Server`** — engine, hub, controllers. Out of scope here.

Layering mirrors the folder tree — **by convention only** (no analyzer or architecture test enforces it): `Views/` (XAML + code-behind) → `ViewModels/` → `Services/` (grouped `DataServices`, `MarketDataServices`, `MarketEngineServices`, `PortfolioServices`, `UserServices`, `BackgroundServices`, `OtherServices`, `SignalR`) → HTTP/hub → server. Code-behind is view-only (layout math, `BindingContext` fan-out); all logic lives in a VM or a service.

### 1.1 Build, TFMs & running

**Two TFMs, one solution.** The client head (`KieshStockExchange.csproj`) is a MAUI app targeting **`net9.0-windows10.0.19041.0`** (Windows is the primary target). `KieshStockExchange.Shared` targets plain **`net9.0`** and is pulled into the client via `ProjectReference` at its native TFM — the Windows moniker applies only to the MAUI head, not the shared library. That is why the wire DTOs (`Order`, `Fund`, `Position`, `LiveQuote`, `Candle`, …) are literally the same compiled types on client and server (§1): both reference the one `net9.0` Shared assembly.

**Build the csproj, not the .sln.** Point the SDK at the client project and pass the Windows TFM explicitly — building the solution drags in TFM combinations that don't apply here:
```bash
dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0
dotnet run   --project KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0
```
**It needs a server to talk to.** The client is a thin shell (§1) — with no reachable `Server:BaseUrl` (read once from `Resources/Raw/appsettings.json`, fallback `http://localhost:5000`, §2) every screen shows empty cards. Run/point at a `KieshStockExchange.Server` instance first. That same `Resources/Raw/appsettings.json` is where the client mirrors server-coupled constants like `Candles:HLMinFillSize` (§2) — keep it in sync with the server when those change. *(Commands from the repo `CLAUDE.md`; TFMs from the two csproj files — grep `<TargetFramework` if either moniker looks stale.)*

---

## 2. Composition root — `MauiProgram.CreateMauiApp` (`MauiProgram.cs`)

Everything is wired in one method. Order matters in two places (base-URL-before-DI, and eager resolution at the end).

**Server base URL is resolved *before* DI** — `LoadServerBaseUrl()` (`MauiProgram.cs:265`) synchronously reads `Resources/Raw/appsettings.json` off the `MauiAsset` stream (`Server:BaseUrl`, fallback `http://localhost:5000`). Sync-on-startup is deliberate: the `HttpClient` and `MarketHubClient` registrations below need the URL *now*, and it is read exactly once. Same read also lifts `Candles:HLMinFillSize` into the static `Candle.HLMinFillSize` so the client's in-progress candle bar applies the server's odd-lot wick rule.

**The named HttpClient `"KSE.Server"` is the single outbound pipe** (`MauiProgram.cs:97`). Two `DelegatingHandler`s are chained on it:
1. `AuthHeaderHandler` (outer) — stamps `Authorization: Bearer <token>` from `TokenStore` (`AuthHeaderHandler.cs:20`).
2. `UnauthorizedRedirectHandler` (inner) — logs out + redirects on a `401` (§7).

The order between *these two* is convention, not load-bearing: in a `DelegatingHandler` chain every handler sees both the outbound request and the inbound response, and the bearer is stamped before the wire call regardless of ordering — either arrangement behaves identically here.

Every `Api*` client resolves this client via `IHttpClientFactory.CreateClient("KSE.Server")` — never `new HttpClient()` — so all three (auth, 401-redirect, base address) apply uniformly.

**Lifetime rules** — the split is intentional, not incidental:
- **Singletons** = anything holding shared/live state: `TokenStore`, `IMarketHubClient`, every `Api*`/`SignalR*` proxy, `ISelectedStockService`, `IUserSessionService`, `IWatchlistService`, `IAuthService`, `IOrderCacheService`, `IThemeService`, `ConnectionStatusViewModel`, `ToastHostViewModel`. State that must survive page navigation lives here.
- **Transients** = pages and their VMs (`AddTransient<TradePage>()`, `AddTransient<TradeViewModel>()`, …). A fresh page + VM tree per navigation; the page's `OnDisappearing` disposes the tree (see §9). This is why page VMs must `Dispose()` their handler subscriptions to the long-lived singletons — otherwise every visit leaks a handler onto them.

**`ILogger<>` is overridden** to `SeparatorLogger<>` (`MauiProgram.cs:154`) — a custom category-splitting logger, not the framework default.

**Eager resolution at the end** (`MauiProgram.cs:240`+) forces four singletons to construct so their constructors wire up subscriptions *before* any page exists. Nothing else injects them, so without this they'd never be created:
- `TokenStore.LoadAsync()` — hydrate the in-memory token from `SecureStorage` before the first HTTP call.
- `INotificationService` — ctor subscribes to hub `NotificationReceived` + session `SnapshotChanged`.
- `ConnectionStatusViewModel` — ctor hooks `IMarketHubClient.StateChanged` before the first transition.
- `ApiOrderCacheBridge` — ctor subscribes to hub `OrderUpdated` → triggers `OrderCacheService` refresh.

A global `TaskScheduler.UnobservedTaskException` handler (`MauiProgram.cs:230`) logs faults from the pervasive fire-and-forget `_ = …Async()` pattern (hub joins, background refreshes) that .NET would otherwise swallow.

---

## 3. App startup & Shell navigation (`App.xaml(.cs)`, `AppShell.xaml(.cs)`)

**`App`** (`App.xaml.cs`) takes `IThemeService`, `IAuthService`, `ILogger<App>` by DI. On construct it calls `themeService.ApplyRandomTheme()` (dev convenience — random palette per launch, later overridden by the user's saved theme on login). `CreateWindow` news up `AppShell` and wires `window.Destroying` → a bounded (2 s) `LogoutAsync()` flush so a still-logged-in close emits the matching session-logout line (same pattern as the candle-flush shutdown fix).

**`AppShell`** is a flat `Shell` with `FlyoutBehavior="Disabled"` — there is **no flyout/tab chrome**; navigation is programmatic. `AppShell.xaml` declares eight `ShellContent` routes (Login, Register, Account, Admin, Bots, Portfolio, Market, Trade) via `{DataTemplate …}` (lazy page construction). `AppShell.xaml.cs` *also* `Routing.RegisterRoute(nameof(Page), typeof(Page))` for each — the code-behind routes back the `GoToAsync` string navigations. Initial route is dispatched onto the UI thread: `Dispatcher.Dispatch(async () => await GoToAsync("///LoginPage"))`.

**Navigation conventions** (Shell URI semantics, used verbatim in the codebase):
- `"///LoginPage"` / `$"///{nameof(LoginPage)}"` — **reset** the nav stack to the root route (used for login and 401-redirect; no back-stack to Trade).
- `"//TradePage"` — switch to a top-level route (post-login, `LoginViewModel.ExecuteLogin`).
- All `GoToAsync` calls are marshalled onto the UI thread (`MainThread.InvokeOnMainThreadAsync`) because `GoToAsync` mutates the visual tree and must run on the UI thread — a prior `await` may have resumed on the threadpool. The null-check on `Shell.Current` (`UnauthorizedRedirectHandler.cs:60`) is a separate guard: it covers the *pre-shell* window (a 401 can land before the shell is constructed), when `Shell.Current` is still null.

Pages are DI-resolved (registered transient in `MauiProgram`) and receive their VM through constructor injection.

---

## 4. MVVM wiring — how a View pairs with a ViewModel

The pattern is uniform; `TradePage` (`Views/TradePageViews/TradePage.xaml.cs`) is the reference.

- **Constructor injection of the VM.** `public TradePage(TradeViewModel vm) { InitializeComponent(); BindingContext = vm; }`. The page never news up its VM — DI does, pulling the whole VM graph.
- **Composite VMs.** A page VM owns child VMs as public properties (`TradeViewModel.ChartVm`, `.OrderBookVm`, `.PlacingVm`, `.OpenOrdersVm`, …), each itself DI-injected into the parent's ctor (`TradeViewModels.cs:112`). Sub-views bind to the child VM: code-behind sets `OpenOrdersTab.BindingContext = vm.OpenOrdersVm` for panels that `SegmentedTabView` attaches before `BindingContext` propagates.
- **CommunityToolkit.Mvvm source generators everywhere.** `[ObservableProperty]` on a backing field (`private string _username;`) generates the public `Username` + `PropertyChanged`; `[NotifyPropertyChangedFor(nameof(X))]` chains computed props (see `SelectedStockService`). `[RelayCommand]` on a method generates an `ICommand` (`ToggleSelectedWatchAsync` → `ToggleSelectedWatchCommand`). XAML binds `Command="{Binding ToggleSelectedWatchCommand}"`.
- **`BaseViewModel`** (`ViewModels/OtherViewModels/BaseViewModel.cs`) : `ObservableObject`, adds only `IsBusy` and `Title`. Most VMs derive from it.
- **Lifecycle hooks drive data, not the ctor.** `TradePage.OnAppearing` calls `await _vm.InitializeAsync(1)` (guarded, async-void-safe — a load failure logs, never crashes). `OnDisappearing` calls `_vm.Cleanup()` then `_vm.Dispose()`.
- **Threading discipline is explicit.** VMs receive `PropertyChanged`/events on the threadpool (see §5) and marshal UI writes with `MainThread.BeginInvokeOnMainThread`. Heavy fan-out (a stock switch) is pushed *off* the UI thread on purpose — `TradeViewModel.PickerSelection`'s setter spawns `Task.Run(ApplyPickerSelectionAsync)` because the `SelectedStockService.Set` chain fires PropertyChanged into ~8 subscribers and would freeze the UI thread on rapid picker clicks.

---

## 5. Shared-state services — the singletons a page reads from

These are the client's "session/selected-stock/live-state" layer. They hold the state; VMs subscribe.

**`IUserSessionService` / `UserSessionService`** (`Services/BackgroundServices/`) — the UI-side session snapshot. Wraps an **immutable `SessionSnapshot` record**; every mutator does `_snapshot = _snapshot with { … }` and raises `SnapshotChanged`. Reference assignment is atomic, so reads are lock-free. Carries auth identity (`UserId`, `IsAdmin`, `IsAuthenticated`), preferences (`BaseCurrency`, `DefaultCandleResolution`), and **persisted view state** (`CurrentStockId`, chart zoom/offset/Y-fit, `TablesShowAll`) so leaving and returning to the Trade page restores the last stock and layout. *Why immutable:* a snapshot handed to a subscriber can't be mutated under it mid-read.

**`ISelectedStockService` / `SelectedStockService`** (`Services/MarketDataServices/`) — the *single* "what stock is the trade UI showing" authority; itself an `ObservableObject` with `[ObservableProperty]` `StockId`/`Currency`/`CurrentPrice`/… so VMs bind or subscribe to `PropertyChanged`. `Set(stock, currency)` (`SelectedStockService.cs:101`) is the hot path: gated by a `SemaphoreSlim` with a **last-write-wins sentinel** (`_lastRequested`) so rapid switches serialize and stale sets bail before touching subscriptions. It `Unsubscribe`s the old (stockId,currency) and `Subscribe`s the new on `IMarketDataService`, then writes the observable props. **All awaits use `ConfigureAwait(false)`** — deliberately, so the Subscribe/Unsubscribe/PropertyChanged fan-out runs on the threadpool instead of pinning to the UI thread (the documented cause of a prior trade-page freeze); each INPC subscriber marshals its own UI updates. Live prices arrive via `_market.QuoteUpdated` → `OnQuoteUpdated`, filtered to the active (stockId,currency).

**`StockAwareViewModel`** (`ViewModels/TradeViewModels/StockAwareViewModel.cs`) — the base class that makes "a page gets its data" uniform for stock-scoped VMs (Chart, OrderBook, PlaceOrder, tables). It subscribes to `ISelectedStockService.PropertyChanged` and dispatches to two abstract handlers: `OnStockChangedAsync(stockId, currency, ct)` and `OnPriceUpdatedAsync(…)`. Each fire **cancels and swaps a per-VM `CancellationTokenSource`** under a lock (`FireStockChanged`), so a superseded stock switch cancels its in-flight reload; `OperationCanceledException` on that token is swallowed, any other exception is logged (never torn up into the sync context). `InitializeSelection()` primes both handlers with whatever is already selected. This is the spine of the trade page's reactivity: switch a stock once, and Chart/OrderBook/tables each independently cancel-and-reload against the new selection.

**Other shared singletons:** `IWatchlistService` (watched-stock set + `Changed` event), `IOrderCacheService` (+ `ApiOrderCacheBridge` refreshing it off hub pushes), `IStockService` (in-app stock/listing catalogue cache, `EnsureLoadedAsync`), `IProfileService` (loads persisted prefs on login), `IThemeService` (§8).

---

## 6. SignalR — one hub connection, replayed groups (`Services/SignalR/MarketHubClient.cs`)

`IMarketHubClient` / `MarketHubClient` is the **sole owner of the `HubConnection`** (built `WithAutomaticReconnect`) to `/hubs/market`. Every live-data proxy (`SignalRMarketDataClient`, `SignalRCandleService`, `ApiPortfolioClient`, `ApiOrderCacheBridge`, `ApiOrderBookFeed`) subscribes to its C# events rather than opening its own socket — *one socket means one auth handshake, one reconnect path, and one replay point; N sockets would be N independent failure/replay states to keep coherent.* Registered singleton via a factory (`MauiProgram.cs:107`) so the base URL and `TokenStore` are captured once. This is the client's **live-data plane** — prices, candles, book, order/portfolio invalidations, notifications.

**Auth over WebSocket.** `options.AccessTokenProvider = () => tokens.Current` (`MarketHubClient.cs:51`) — reads the token fresh on every (re)connect so reconnects pick up a rotated token. Server middleware lifts it off `?access_token=` so it survives the WS upgrade.

**Group model** — server hub `KieshStockExchange.Server/Hubs/MarketHub.cs` — three group families the client joins/leaves:

| Group | Join method | Scope | Carries |
|---|---|---|---|
| `quotes:{stockId}:{currency}` | `JoinQuotes` / `JoinCandles` | per visible listing | `QuoteUpdated`, `CandleClosed` |
| `orders:{userId}`, `portfolio:{userId}` | `JoinUserGroups` | logged-in user | `OrderUpdated`, `PortfolioChanged` |
| `telemetry` | `JoinTelemetry` (admin-only) | operator | `OnTelemetryEvent` |

`JoinCandles` does double duty: besides adding the connection to the quotes group, it calls `ICandleService.Subscribe` server-side to **ref-count the per-resolution aggregator** — without it the engine never emits `CandleClosed` for that key and the chart's live bar looks frozen (comment block atop `MarketHub`). `JoinUserGroups` ignores the wire `userId` and derives it from the JWT `sub` claim (the body param is retained only for handshake back-compat).

**Typed pushes re-raised as .NET events** (`MarketHubClient.cs:56`): `QuoteUpdated`, `CandleClosed`, `OrderUpdated` (envelope → userId), `NotificationReceived`, `PortfolioChanged`, `OrderBookSnapshot`. Proxies translate these into their own domain surfaces. On the server side three hosted services originate them:
- `MarketHubBroadcaster` (IHostedService) bridges engine events → groups: `IMarketDataService.QuoteUpdated`, `ICandleService.CandleClosed`, `IUserPortfolioService.SnapshotChanged`. *Note: the `PortfolioChanged` push is a placeholder — it broadcasts to a single `portfolio:0` group, so the client treats it as a bare "go re-fetch" trigger, not a per-user payload (`MarketHubBroadcaster.OnPortfolioChanged`, `ApiPortfolioClient.OnHubPortfolioChanged`).*
- `OrderBookBroadcaster` pushes `OrderBookSnapshot` (own hosted service; the book is *not* part of `MarketHubBroadcaster`).
- `TelemetryBroadcaster` bridges the server `TelemetryBus` onto the `telemetry` group.

**Group tracking + replay** is the resilience core. `JoinQuotes/JoinCandles/JoinUserGroups` record the group in a locked `HashSet` (`_quoteGroups` / `_candleGroups` / `_activeUserId`), then invoke the hub — **but skip the invoke if not `Connected`** (mid-reconnect), because the tracked set is replayed on reconnect anyway. `ReplayGroupsAsync` re-joins every tracked group after *both* the automatic `Reconnected` event and a manual restart via `EnsureConnectedAsync` (a fresh server connection id = empty server-side membership). Each re-join is isolated so one failure doesn't abort the rest. **Why:** a dropped connection would otherwise silently lose all subscriptions and the chart/book/portfolio would go stale with no error.

**Client-side proxies over the hub:**

| Proxy | Interface | Consumes | Provides |
|---|---|---|---|
| `SignalRMarketDataClient` | `IMarketDataService` | `QuoteUpdated` | live `LiveQuote` cache + `Subscribe/Unsubscribe` (ref-counted → `JoinQuotes`) |
| `SignalRCandleService` | `ICandleService` | `CandleClosed` | closed-candle stream + `GetHistoricalCandlesAsync` (HTTP) |
| `ApiOrderBookFeed` | `IOrderBookFeed` | `OrderBookSnapshot` | book cache + `GetSnapshotAsync` (HTTP), `BookVersion`-gated |
| `ApiPortfolioClient` | `IUserPortfolioService` | `PortfolioChanged` | funds/positions snapshot; deposit/withdraw/convert (HTTP) |
| `ApiOrderCacheBridge` | (eager singleton) | `OrderUpdated` | refreshes order cache + transaction tape for the active user |

**`ApiOrderCacheBridge`** is resolved at boot (no VM wires it) and, on an `OrderUpdated` push matching `_auth.CurrentUserId`, refreshes both `IOrderCacheService` and `ITransactionService` — order pushes correlate 1:1 with fills, so a transaction re-pull is the cheapest way to keep history views live without a separate channel.

**Reference-counted subscriptions.** `SignalRMarketDataClient` keeps a `_subRefs` count per (stockId,currency); the hub group is joined on the first subscribe and left on the last unsubscribe — so multiple VMs watching the same stock share one group membership.

**`ConnectionStatusViewModel`** (singleton, eager) subscribes to `StateChanged` and drives the reconnect banner in `TopNavBarView`. It suppresses the "Disconnected" banner pre-login (the hub sits `Disconnected` by design until the first `Connected`) via a `_hasEverConnected` latch.

**Exception — the bot dashboard.** The `telemetry` group exists (`TelemetryBroadcaster`), but `MarketHubClient` exposes no telemetry event and the admin dashboard does **not** consume it — treat that group as available-but-unused on this client. The dashboard polls REST instead (§14).

---

## 7. Auth & token lifecycle (`Services/UserServices/`)

The **server is authoritative** for credentials and registration; the client holds a JWT.

1. **Login** — `LoginViewModel.ExecuteLogin` → `AuthService.LoginAsync` (`AuthService.cs:84`) POSTs `api/auth/login`; on success stores the JWT via `TokenStore.SetAsync`, pulls the full `User` (for VM fields), sets `CurrentUser`, then fire-and-forgets `api/session/login` + `_hub.JoinUserGroupsAsync(userId)`. Back in the VM: `session.SetAuthenticatedUser(...)`, `profile.LoadPreferencesAsync` (restores theme/currency/resolution), `watchlist.RefreshAsync`, then `Shell.Current.GoToAsync("//TradePage")` on the UI thread. (A legacy local-DB password fallback remains but is dead once `[Authorize]` requires the token.)
2. **`TokenStore`** (`TokenStore.cs`) — in-memory `Current` (lock-guarded, sync — it's on the HTTP-handler and hub AccessTokenProvider hot paths) backed by `SecureStorage`. `LoadAsync` on boot, `SetAsync` on login, `Clear` on logout. SecureStorage I/O is best-effort (swallows platform-unavailable).
3. **`AuthHeaderHandler`** stamps the bearer on every `KSE.Server` request; no-op when no token (anonymous endpoints still work).
4. **`UnauthorizedRedirectHandler`** (`UnauthorizedRedirectHandler.cs`) — on a `401` from any **non-`/api/auth/`** path (session expiry, not a bad password), it `LogoutAsync()`es and Shell-redirects to `///LoginPage`. A `CompareExchange` re-entrancy flag stops the logout-notify call (which re-enters the same pipeline and may itself 401) from looping. This is what turns an expired 168 h JWT from "silent empty cards" into an actual re-login prompt.
5. **Logout** — `AuthService.LogoutAsync` (`AuthService.cs:132`) notifies `api/session/logout` and leaves hub user-groups **before** clearing the token, then `TokenStore.Clear()`. *Why this order:* the REST logout-notify needs the bearer on the request; and if the hub has to *reconnect* mid-leave, its `AccessTokenProvider` reads `TokenStore.Current` fresh (§6) — a token already cleared would fail that reconnect. (The already-open WS ride does not re-read the token per invoke; the reconnect window is the case that forces the ordering.)

`IAuthService` exposes `CurrentUser`/`IsLoggedIn`/`IsAdmin`/`CurrentUserId` — the identity source VMs and `ApiPortfolioClient.RefreshAsync` read.

---

## 8. Styling & theming (`Resources/Styles/`, `IThemeService`)

- **Shared resource dictionaries, merged in `App.xaml`** in a fixed order: `Theme.ExchangeLight` (slot 0), then `Colors`, `Styles`, `ShellStyles`, `AdminStyles`, `AuthStyles`, `TradeStyles`, `TradeTableStyles`, `OrderBookStyles`, `ChartStyles`, `SegmentedTabStyles`. Feature-scoped style files keep per-area styling out of the page XAML; pages use `StaticResource` for structure and **`DynamicResource` for theme tokens**.
- **Theming = hot-swap slot 0.** `ThemeService.ApplyTheme` (`ThemeService.cs:55`) news up one of eight compiled-XAML theme partials (`Theme.*.xaml.cs`, e.g. `ExchangeDark`, `MidnightTrader`, `DeepNavyPro`) and **replaces `MergedDictionaries[0]` in place** with `RemoveAt(0)`+`Insert(0, …)`. Because theme colours are referenced as `{DynamicResource}`, live UI re-resolves on swap. It deliberately avoids `Clear()`+re-add: an empty collection makes MAUI re-resolve every active `DynamicResource` against nothing and throw a framework NRE. Choice persists to `Preferences` (`selected_theme`); `ThemeChanged` fires for any listening VM. `App` applies a random theme at startup; login overrides it via `IProfileService`.

---

## 9. How a page actually gets its data — worked flow (Trade page)

Concrete end-to-end, tying §1–§8 together:

1. Shell resolves `TradePage` (transient) from DI, which constructor-injects `TradeViewModel` (transient) and its whole child-VM graph; `BindingContext = vm`.
2. `OnAppearing` → `TradeViewModel.InitializeAsync` (`TradeViewModels.cs:176`): loads trading pairs (`IMarketDataService.GetAllStocksAsync` → `IStockService` cache), restores `_session.CurrentStockId`, and calls `_selected.Set(stock)`.
3. `SelectedStockService.Set` unsubscribes the old and **`SubscribeAsync` the new** on `SignalRMarketDataClient`, which ref-counts and calls `_hub.JoinQuotesAsync` — joining the server group for that (stockId,currency). It then writes its `[ObservableProperty]` fields (`StockId`, `Currency`, `CurrentPrice`), each firing `PropertyChanged` on the threadpool.
4. Every `StockAwareViewModel` child (Chart, OrderBook, …) hears the `StockId` change, cancel-swaps its CTS, and runs `OnStockChangedAsync` — fetching its own slice: `ChartViewModel` joins candle groups + pulls history, `OrderBookViewModel` reads `IOrderBookFeed`, the tables `RefreshAsync` against `Api*` HTTP endpoints.
5. Thereafter it's **push, not poll**: the server broadcasts `QuoteUpdated`/`CandleClosed`/`OrderBookSnapshot`/`PortfolioChanged` over the one hub; `MarketHubClient` re-raises; each proxy updates and raises its own event; the subscribing VM marshals the change to the UI thread. Order actions post through `ApiOrderEntryClient`/`ApiOrderExecutionService` and come back as `OrderUpdated`/`PortfolioChanged` pushes that refresh the order cache and portfolio snapshot.
6. `OnDisappearing` → `vm.Cleanup()` (`_selected.Reset()` — leaves the hub groups) then `vm.Dispose()`, which **cascades `Dispose()` to every child VM** so their handler subscriptions on the long-lived singletons are removed (transient VMs, singleton event sources — the leak this guards against).

## 9.1 The write path — a limit buy round-trip

§9 traces how a page *reads*; every mutation is the mirror image — the client posts and then waits for a push, it never mutates local state optimistically.

1. **Validate** — `PlaceOrderViewModel.PlaceOrderAsync` runs `ValidateInputs` client-side (qty > 0, valid price, trigger on the correct side) — a cheap pre-filter, not the authority.
2. **Dispatch** — routes to the matching method on `IOrderEntryService` (client impl `ApiOrderEntryClient`): here `PlaceLimitBuyOrderAsync`, which POSTs to the order endpoint over `HttpClient "KSE.Server"` (bearer stamped by `AuthHeaderHandler`).
3. **Engine** — the server validates, reserves funds, and runs the book/match/settle path — see `docs/ENGINE_MECHANICS.md` (OrderEntry → Execution → Matching → Settlement). The HTTP call returns a definite per-call result (accepted / rejected).
4. **Push back** — any resulting fill emits `OrderUpdated` (and a placeholder `PortfolioChanged`) on the hub. `ApiOrderCacheBridge` (§6), matching `_auth.CurrentUserId`, refreshes `IOrderCacheService` + `ITransactionService`; `ApiPortfolioClient` re-fetches funds/positions.
5. **UI reflows** — the Trade tables, chart open-order lines / fill markers, and the ticket's asset row all re-sync off those events. **Success/fail toasts are generated server-side** (`ServerNotificationService`) and arrive as a `NotificationReceived` push — the VM raises **no** optimistic local toast (it would double up). After submit the VM also does an immediate portfolio + order-cache refresh so the change shows without waiting for the push.

---

# Part B — Surfaces

The screens the user touches. Each is a page↔VM pair (§4); all of them read from the shared singletons (§5) and the hub (§6). The recurring pattern: **HTTP bootstraps the screen, SignalR keeps it live** (closing section).

### Shared chrome & notifications (`Views/OtherViews/`)

Reused across pages, not a page of their own:
- **`TopNavBarView`** — the persistent header (page title, currency, connection banner driven by `ConnectionStatusViewModel`, §6; inbox affordance). Every content page embeds it.
- **`ToastHostView`** + **`InboxPopup`** — where notifications *land*. `INotificationService` (`NotificationService.cs`, eagerly resolved in §2) is the client sink: its ctor subscribes to hub `NotificationReceived` (live pushes) and session `SnapshotChanged` (on login it **replaces** its 50-item ring with the user's persisted history via `HydrateAsync`, server = source of truth). `NotificationAdded` pops a transient toast in `ToastHostView`; the full newest-first ring is browsed in `InboxPopup`. Notifications are **server-authored** — the client never fabricates one (see §9.1 / §11.3).
- **`SegmentedTabView`** — the reusable tab-strip control (used by the Trade table card §11, the Bot dashboard §14, and the Admin console §15). Note it attaches child content *before* `BindingContext` propagates, which is why hosts set child `BindingContext` explicitly in code-behind (§4).

## 10. Market page

`Views/MarketPageViews/MarketPage.xaml` ↔ `ViewModels/MarketViewModels/MarketViewModels.cs` (`MarketViewModel`). A browse-and-search surface over every listing.

**Data source** — the live `LiveQuote` cache in `IMarketDataService.Quotes`. On appear, `RefreshAsync` calls `SubscribeAllAsync(ccy)` for **both** USD and EUR (so the watchlist card populates across currencies regardless of the active tab), then `Poll()` projects quotes into `MarketRow`s. Rows update **in place** (`MarketRow.UpdateFrom`) rather than being recreated, and the collection is synced with Move/Insert/Remove (`SyncRows`) — never Clear+Add — to avoid `CollectionView` flicker.

**Live update path** — two triggers, deliberately redundant: a push-driven `OnQuoteUpdated` (debounced to one rebuild per UI frame via `_pollPending`) sweeps new rows in sub-second, and a 1 s fallback `IDispatcherTimer` (`PollInterval`) catches stocks that aren't actively trading. Quote subscription + the timer are torn down on page-disappear (`PausePolling`) so 140+ live subscriptions don't storm the UI thread while the user is on the Trade page; the warm row cache and group memberships survive for the next visit.

**Surfaces:**
- **Tab strip** — USD / EUR / Watchlist (`FilterCurrency` + `ShowWatchlistOnly`). Watchlist spans both currencies.
- **All-stocks table** — sortable columns (`MarketSortColumn` Symbol/Name/Change/Volume, arrow indicator in the header text), client-side search over symbol + company name, client-side pagination (`PageSize=20`).
- **"My Watchlist" card** — starred stocks in saved order, its own row cache (`_watchlistRowsById`) so a 5 s membership poll doesn't reshuffle instances. Reorder via `MoveWatchUp/Down` → `IWatchlistService.ReorderAsync`.
- **Top Gainers / Losers / Most-Active** — `TrendingService` (`ITrendingService`), an independent 5 s `PeriodicTimer` that snapshots `LiveQuote` fields into frozen `MoverSnapshot` records off-thread (avoiding wrong-thread `PropertyChanged`), then syncs three top-3 lists on the UI thread. Scoped to the active tab via `Currency` + `WatchlistFilter`, de-duped to one listing per stock. It intentionally does **not** subscribe to `QuoteUpdated` (would cause UI-thread storms).

`TradeAsync` sets `ISelectedStockService` and navigates to `///TradePage`.

## 11. Trade page

`Views/TradePageViews/TradePage.xaml` ↔ `TradeViewModel`. A composite: a symbol/price bar (a `Picker` over `TradingPairs` + a watch-star), then a 70/30 vertical split — chart + order book + order-entry panel on top, a segmented table card below. The chart row's height is pinned imperatively in code-behind (`UpdateTradeLayout`/`UpdatePanelHeights`) because the child panels' inner `ScrollView`s would otherwise inflate the star row (see the `reference_maui_scrollview_starrow_sizing` note). All sub-views share the selected stock via `StockAwareViewModel` (§5), which fans `OnStockChangedAsync` / `OnPriceUpdatedAsync` out from `ISelectedStockService`.

The bottom card is a `SegmentedTabView` with **Open Orders / Order History / Transaction History / Positions**, plus a "current stock only" filter toggle (`CurrentStockOnly`). Those tables are fed by `IOrderCacheService` + `ITransactionService`, which the `ApiOrderCacheBridge` keeps live off `OrderUpdated` (§6).

### 11.1 The chart

`Views/TradePageViews/ChartView.xaml(.cs)` ↔ `ViewModels/TradeViewModels/ChartViewModel.cs` (~1750 lines). The heaviest client component. `ChartView` hosts a `GraphicsView` whose `IDrawable` is `CandleChartDrawable`; the VM owns all state and raises `RedrawRequested` (coalesced to ~60 FPS, `RedrawCoalesceMs=16`). Immutable snapshot records passed VM→drawable live in `Services/MarketDataServices/Helpers/ChartTypes.cs` so a paint mid-mutation sees consistent data. (Tags like `"§F2"` / `"§F7"` below are **in-code comment anchors inside `ChartViewModel.cs`** — grep the file for them, they are not references to another doc.)

**Candle data flow** — `StartStreamingCandles`: HTTP-loads history (`SignalRCandleService.GetHistoricalCandlesAsync` → `GET /api/candles/by-stock-range/...`, sized to `VisibleCount × MaxFactor`), then streams closed candles via `StreamClosedCandles` (SignalR). Stock/resolution switches go through `RestartStreamAsync`, which does an **atomic CTS swap** so aggressive switching doesn't queue HTTP fetches behind a held gate. `_candleBuffer` is kept ascending-`OpenTime` by every mutation path; `LoadOlderAsync` lazy-loads earlier buckets when the user pans within a window of the left edge.

**Live forming bar** — the server streams only *closed* candles, so between closes the last bar is synthesized from the live price tick (`OnPriceUpdatedAsync` → `TrySyncLiveCandle`): it preserves the bucket's Open and extends High/Low/Close, keyed on the floored bucket so the authoritative closed candle replaces it on close (`UpsertCandle`, no duplicates). Heavily guarded — a synthesis failure degrades to a price-line-only redraw.

**Chart types** — `ChartStyle` (`ChartTypes.cs`): Candles, HollowCandles (up-bars outlined), Bars (OHLC ticks), Line, Area, HeikinAshi (smoothed, raw OHLC retained for the crosshair). Persisted per user via `Preferences` (`chart_style`); toolbar button cycles.

**Resolution + viewport** — `ResolutionOptions` 15 s → 1 D (persisted `DefaultCandleResolution`). Viewport is `VisibleCount` + `OffsetFromLatest`; cursor-anchored X-zoom (`ZoomAtCursor` pins the time under the cursor), `GoLive`, `JumpToOldest`. Y-axis: `IsYAutoFit` vs manual range; scale mode Linear / Logarithmic / Percent (`PriceScaleMode`, persisted). The full viewport (`count/offset/YAuto/YMin/YMax`) is snapshotted to the session on dispose and restored once on the next visit (`_pendingRestore`, "§F7").

**Volume** — `VolumeMode` Overlay / Pane / Off (persisted, toolbar cycle).

**Moving averages** — `MaSeries` (default MA20/50/200 SMA, disabled until toggled), each `MaConfig` editable (period/kind/color) in a settings overlay; SMA or EMA via `MovingAverageCalculator`.

**User overlays sourced from user data** (all re-synced on stock/transaction change, rendered by the drawable, clipped to viewport):
- **Open-order lines** (`OpenOrderLines`) — the user's resting limit/stop orders for the selected listing as draggable horizontal lines (green buy / red sell; dashed + STOP/STOP-LIM pill for armed stops; dimmed for dormant bracket children with `IsAttached`). Dragging a line → `BeginModifyOrderAtAsync` → `IOrderEditService` opens the modify ticket. Synced from `IOrderCacheService.OrdersChanged`.
- **Fill markers** (`FillMarkers`) — the user's executed fills as up/down triangles; fills of one order in the same candle bucket are aggregated into a single VWAP arrow. From `ITransactionService`.
- **Trigger markers** (`TriggerMarkers`, "§F2") — fired stop-*limit* triggers as blue arrows at (`ActivatedAt`, `StopPrice`).
- **Position line** (`PositionLine`) — a solid line at weighted-average entry with a live unrealized-P&L tag; the (signed qty, avg) basis is reconstructed from the fill tape (`RefreshPositionBasis`, WAC method incl. short flips) and the P&L re-evaluated on every price tick.

**Pen tray / drawing tools** — `DrawTool` (`ChartTypes.cs`): HLine, Trend, Ray, HRay, Polyline. Drawings are anchored in **(time, price) data space** so they survive pan/zoom, persisted per stock+currency to `Preferences` as JSON (`DrawingObject` + `DrawStyle`; `Color` round-trips via `ColorJsonConverter`). The unified pen panel edits either the **default pen** (nothing selected) or the **selected drawing** (tap-to-select opens the panel in "selected" mode). Style tiles: 10-color palette (2×5), 3 widths, 3 dash kinds (`DashKind`), 3 line endings (`LineEnding` None/End/BothOut), 3 arrow-head shapes (`ArrowHeadStyle`). Tiles are fixed instances whose `Specimen`/`IsSelected` mutate (`RefreshPenTiles`); the live pen preview is a `StylePreviewDrawable`. Placement gestures are handled in `ChartView` code-behind (right-click builds an HLine at cursor price; a tool press places/starts a drawing instead of panning).

**Fear/Greed "Market Mood" sub-pane** — toggle `ShowMoodPane` (persisted `chart_mood_pane`). There is **no stored mood history server-side**, so the VM seeds a back-history from the loaded candles (`SeedMoodFromCandles`: uses each candle's server-stamped `Candle.MarketMood` where present — the composite F&G, correct for the timeframe — else a momentum reconstruction `tanh(k·ln(close/EMA))` as a believable stand-in, *marked in-code as faked*) and then **fills forward** by polling `GET /api/market/mood/{stockId}` every 4 s (`ApiMarketMoodClient.GetMoodAsync` → `MoodPollLoopAsync`, `PeriodicTimer`), accumulating up to 2000 samples on the candle time axis. Returns `null` on any transport fault so the pane stalls rather than throws. The poll restarts on stock change and is a no-op while the pane is off. (This is the client face of `Bots:Mood:*` / `MarketMoodService`; see `docs/BOT_MECHANICS.md` §2.10 for how the score is computed server-side.)

**Order-book depth overlay** — toggle `ShowDepth` (persisted `chart_depth_overlay`). Mirrors the live `IOrderBookFeed.SnapshotChanged` for the selected key into `DepthLevels` (`DepthLevel(price, qty, isBid)`), which the drawable renders as a right-gutter heatmap (green bid / red ask, normalized to the largest level). Cache-first seed on toggle-on (`RefreshDepthLevels`), HTTP-fetched on a cache miss; the level list is reassigned (never mutated in place) so a paint sees a consistent snapshot.

### 11.2 Order book

`OrderBookView` ↔ `OrderBookViewModel`. Fed by `IOrderBookFeed` (`ApiOrderBookFeed`): cache-first on stock change, HTTP fallback (`GET /api/order-book/{stockId}/{currency}`), then live `SnapshotChanged` pushes (out-of-order pushes dropped by `BookVersion`). Snapshots that don't match the currently-selected key are discarded.

Renders `VisibleBuyLevels` / `VisibleSellLevels` (both high→low) trimmed to a user `Depth` (default 10), with **cumulative quantities computed across the full book** so the row nearest the mid reflects total side liquidity, and a per-row `DepthRatio` bar. Rows update in place (`LevelRow.Update`) to avoid `CollectionView` Replace flashes. Best bid/ask, spread, and spread % are derived and colored; a **price-aggregation bucket picker** (`OrderBookDepthAggregator`) auto-scales its step from the mid price and adapts the candidate list until one bucket concentrates ≥25% of side volume. A price-direction arrow/color tracks tick-to-tick change.

### 11.3 Order-entry ticket

`PlaceOrderView` ↔ `PlaceOrderViewModel`. The buy/sell ticket. Side (Buy/Sell) × type (Market / Limit / Trigger) segments, plus a Trigger sub-mode (`TriggerHasLimit` → stop-limit vs stop-market) and a long-only **bracket** builder (optional protective stop-loss and up to 3 take-profit legs; `IsBracket` when either is present). A `MaxQuantity`-normalized 0..1 quantity slider with sticky 0/25/50/75/100% dots (constant `Maximum=1` to avoid a coerce-loop freeze), plus percent buttons.

**Assets shown** — available funds + available/total shares for the active listing, read from `IUserPortfolioService.GetFundByCurrency` / `GetPositionByStockId`; refreshed on `SnapshotChanged`, on `OrdersChanged` (a resting fill changes holdings), and on a 60 s timer. Live order-value preview against the current price.

**Validation & hints** — `ValidateInputs` blocks bad orders client-side before the engine (quantity > 0, valid price, trigger on the correct side of the market); non-blocking hints warn about a marketable limit ("crosses the market — fills immediately"), missing fund in the trading currency ("Convert cash to X first"), and short/flip semantics.

**Submission** — `PlaceOrderAsync` dispatches to the matching `IOrderEntryService` method: `PlaceLimit/SlippageMarket/TrueMarket/StopMarket/StopLimit{Buy,Sell}OrderAsync` and `PlaceBracketAsync`. Slippage guard is a fraction in the UI but passed to the engine as percentage points (`SlippagePrc × 100`). Success/fail notifications are generated **server-side** (`ServerNotificationService`) and rendered via the hub push — the VM raises no optimistic local toast (would double up). After submit it refreshes portfolio + order cache so the tables/overlays update immediately.

## 12. Portfolio page

`PortfolioPageViews/PortfolioPage.xaml` ↔ `PortfolioViewModel`, a shell over six sub-VMs (Currencies, Holdings, OpenOrders, OrderHistory, Transactions, FundsHistory). `RefreshMetrics` runs on `IUserPortfolioService.SnapshotChanged`, `ITransactionService.TransactionsChanged`, and session change.

**KPIs** (base-currency, cross-currency values walked live through `IFxRateService` mid-rates): total equity = cash + market value of holdings (holdings valued off the live `LiveQuote` cache), today's Δ vs session open, all-time realized+unrealized P&L (liquidation-equivalent: sells received + current holdings value − buys paid), position count. An **allocation pie** (`AllocationSlices`, Okabe-Ito palette, top-7 + "Other") from funds + position market values.

## 13. Account / Funds page

`AccountPageViews/AccountPage.xaml` ↔ `AccountViewModel`. Profile (name/email/birth/member-since from `IAuthService.CurrentUser` + `IUserSessionService.Snapshot`), a base-currency picker (`IProfileService.UpdateBaseCurrencyAsync`), and a **Funds card** — base-currency available/reserved balance plus sibling-currency rows (every other non-zero fund).

An **Activity card** computes trades placed, distinct stocks traded, per-currency volume, per-currency **realized P&L** (weighted-average-cost basis walked oldest-first over the user's tape; `PnLIsApproximate` flips on if inventory ever went short since WAC loses meaning), and best/worst stock. Funds/positions come from `IUserPortfolioService`; the tape from `ITransactionService` (server-side filtered to the user). Account actions (change email/password/username, deposit/withdraw, convert, fund history) open as CommunityToolkit `Popup`s and refresh on dismiss. Deposit/withdraw/convert POST to `/api/portfolio/*` via `ApiPortfolioClient` and re-fetch on success.

## 14. Bot dashboard

`AdminPageViews/BotDashboardPage.xaml` ↔ `BotDashboardViewModel`, backed by `ApiBotAdminClient` — a thin wrapper over `GET/POST /api/admin/bots/*` (status, start, stop, failures/clear, scaler, ai-user-ids, activity-samples, last-24h-stats, activity-buckets, strategy-breakdown). This screen is **REST-polled, not SignalR-driven**: a 1 s `IDispatcherTimer` re-fetches `bots/status`; 24 h stats every 30 s, the activity chart every 10 s, the strategy breakdown every 20 s. (The server *does* publish a `telemetry` SignalR group via `TelemetryBroadcaster`, but the client dashboard does not consume it — treat that group as available-but-unused on this client, §6.)

**Surfaces:** live status (running/stopped, loaded vs online bots over cap, tick count, trades, failures, uptime, tick-work EWMA + load fraction), scaler controls (min/max cap, enable), failure diagnostics (recent / by-reason / by-stock, with CSV export), a 60-bucket **activity graph** (Trades / Volume / Active-bots series, 15 m–24 h ranges; drawn by `BotSparklineDrawable`), 24 h aggregate stats, and a **per-strategy breakdown** split into Trader vs House/Liquidity groups (bot share, win %, P&L %, session/range trades, range volume) over 1 h/6 h/24 h/All windows. Last status payload is cached (`_lastStatus`) so getters don't re-issue HTTP and a transient transport fault reuses the prior snapshot.

> **Server-auth caveat (verified).** `AdminBotController` (`api/admin/bots/*`) carries **no `[Authorize]` attribute at all** — it is covered only by the global `FallbackPolicy` (`Program.cs:167`, `RequireAuthenticatedUser`). So the bot start/stop/scaler endpoints are auth-*required* but **not admin-role-gated**, unlike sibling admin controllers (`AdminLogsController.cs:30` = `[Authorize(Roles = "admin")]`). On the *client* this screen is reached only via the admin nav, but any logged-in user could hit those endpoints directly. **Likely a server gap worth flagging to the owner.**

## 15. Admin page (CRUD tables)

`AdminPageViews/AdminPage.xaml` ↔ `AdminViewModel` (`ViewModels/AdminViewModels/`) — a **separate** surface from §14 (both are registered as top-level Shell routes: "Admin" and "Bots"). This is the raw-data admin console: a `SegmentedTabView` of CRUD tables over the domain entities (Users, Stocks, Funds, Positions, Orders, Transactions, …), rendered by the per-entity views under `Views/AdminPageViews/Tables/` with add/edit dialogs under `EditPopups/`. Reads/writes go through the `Api*` proxies to the role-gated admin controllers (`FundController`, `OrderController`, `PositionController`, `TransactionController`, `SeedController`, … — all `[Authorize(Roles = "admin")]`, in contrast to §14's bot controller). Treat it as a maintenance/debug surface, not a trading screen.

---

## Where data comes from — REST vs SignalR

Every ViewModel reads from exactly **two data planes**, and the split is the single most important thing to internalise about this client:

1. **REST** — the named `HttpClient "KSE.Server"` (from `IHttpClientFactory`, §2), for **on-demand loads and all mutations**: history fetches, order placement, deposit/convert, admin start/stop, the mood poll. Auth is the bearer JWT stamped by `AuthHeaderHandler`; a 401 routes through `UnauthorizedRedirectHandler` (§7). JSON options are centralized in `ApiJsonOptions.Default`.
2. **SignalR** — the single `/hubs/market` connection owned by `MarketHubClient` (§6), fanned out to per-feature proxies. This is the **live** plane: prices, candles, book, order/portfolio invalidations, notifications.

*Why two planes:* **push cost scales with data change-rate; poll cost scales with screen-count × interval.** Prices/candles/book change many times a second across many screens, so push (one socket, group-scoped) is far cheaper than everyone polling. REST is kept for the things push can't express: **mutations** need a definite per-call accept/reject result, and **history** needs paging/range params the fire-and-forget group model has no room for.

**Rule of thumb across the whole codebase: HTTP bootstraps a screen (or seeds a cache), SignalR keeps it live.** Most feeds are cache-first — read the last snapshot synchronously, HTTP-fetch on a miss, and let the next push overwrite. Two deliberate exceptions, both explained by the rule: the **Market page** (§10) adds a 1 s polling fallback so idle (non-trading) stocks still tick when no push is coming, and the **Bot dashboard** (§14) is pure REST-poll because it never joins the telemetry group.

**REST proxy → route → caller** (the REST plane; the SignalR plane's proxy table is §6):

| Proxy | Base route(s) | Used by |
|---|---|---|
| `ApiOrderEntryClient` / `ApiOrderExecutionService` | order placement/modify/cancel | ticket §11.3, chart drag §11.1 |
| `SignalRCandleService` | `GET /api/candles/by-stock-range/...` | chart history §11.1 |
| `ApiOrderBookFeed` | `GET /api/order-book/{stockId}/{currency}` | order book §11.2 (cache-miss fallback) |
| `ApiPortfolioClient` | `/api/portfolio/*` (deposit/withdraw/convert) | portfolio §12, account §13, ticket assets |
| `ApiMarketMoodClient` | `GET /api/market/mood/{stockId}` | chart mood pane §11.1 (4 s poll) |
| `AuthService` | `api/auth/login`, `api/session/{login,logout}` | auth §7 |
| `ApiBotAdminClient` | `GET/POST /api/admin/bots/*` | bot dashboard §14 (poll) |

## Invariants — the "never do X" rules, collected

These are enforced by convention + code review, not the compiler. Break one and you get a subtle leak, freeze, flicker, or torn read:

| Invariant | Why | Where |
|---|---|---|
| Never `new HttpClient()` — resolve `"KSE.Server"` via `IHttpClientFactory` | otherwise auth / 401-redirect / base-address handlers don't apply | §2 |
| Transient VMs **must** `Dispose()` their subscriptions to singletons | singleton event sources outlive the page; a missed unsub leaks a handler per visit | §2, §9 step 6 |
| All `GoToAsync` on the UI thread | it mutates the visual tree; off-thread corrupts/throws | §3 |
| Theme swap is `RemoveAt(0)`+`Insert(0,…)`, **never** `Clear()` | an empty dictionary makes MAUI re-resolve every live `DynamicResource` against nothing → framework NRE | §8 |
| Sync observable collections **in place** (Move/Insert/Remove/`UpdateFrom`), never Clear+Add | Clear+Add flickers `CollectionView` and drops row identity | §10, §11.2 |
| Exactly one `HubConnection`, owned by `MarketHubClient` | one auth / reconnect / replay state instead of N | §6 |
| `_candleBuffer` stays ascending-`OpenTime` on every mutation path | pan/zoom/aggregation math and the live-bar upsert assume it | §11.1 |
| Overlay/depth level lists are **reassigned**, not mutated in place | a paint mid-mutation must see a consistent snapshot | §11.1 |
| No optimistic local toasts — notifications are server-authored | the server also pushes one → double toast | §9.1, §11.3 |

## Recipe — adding a new page or feed

1. **Register** the page + its VM as `AddTransient<…>()` in `MauiProgram.CreateMauiApp` (never news them up; DI injects the VM into the page ctor, §4).
2. **Route it**: add a `ShellContent` (with a `{DataTemplate}`) in `AppShell.xaml` **and** a matching `Routing.RegisterRoute(nameof(Page), typeof(Page))` in `AppShell.xaml.cs` (§3).
3. **If stock-scoped**, derive the VM from `StockAwareViewModel` and implement `OnStockChangedAsync` / `OnPriceUpdatedAsync` — you get cancel-and-swap CTS reactivity for free (§5).
4. **Pick a plane** per the rule of thumb: REST for on-demand loads/mutations, the hub (via an existing or new `SignalR*`/`Api*` proxy) for live push; cache-first where possible (§6, closing rule).
5. **Wire lifecycle**: load data from `OnAppearing`→`InitializeAsync` (not the ctor), and in `OnDisappearing`→`Cleanup()`+`Dispose()` unsubscribe every handler you attached to a singleton (invariant above).

---

*Verification notes: `file:line` references above were read from source (they will drift on unrelated edits to the cited file — prefer the accompanying symbol name as the durable anchor). Flagged as present-but-transitional in their own code comments rather than presented as final design: the `LoginViewModel` legacy local-DB password fallback (`AuthService.cs:116`), and `ApiPortfolioClient`'s shared `portfolio:0` broadcast group — the client treats `PortfolioChanged` as a bare "re-fetch" trigger, not a per-user payload (`ApiPortfolioClient.cs:55`). Server-side authorization was inspected this pass: `AdminBotController` (`api/admin/bots/*`) has no `[Authorize]` and rides only the global `RequireAuthenticatedUser` fallback (`Program.cs:167`), so it is auth-required but not admin-role-gated — a probable server gap (§14), unlike the role-gated sibling admin controllers.*
