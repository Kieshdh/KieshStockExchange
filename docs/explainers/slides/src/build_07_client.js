// build_07_client.js — Section 7: the MAUI client (CLIENT_STRUCTURE.md)
// Story spine: the client is a thin skin — HTTP bootstraps a screen, SignalR keeps it live —
// then a tour of the screens the user actually touches.
module.exports = function (T, p) {
  const { C } = T;
  const N = 7;
  const CLIENT = ["CLIENT"];

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 7 of 7 · the screen the user sees",
    title: "The Desktop Client",
    subtitle: "A .NET MAUI Windows app — a thin presentation shell. It renders quotes, charts, and tickets; it never matches. Every live number arrives from the one server.",
    footer: "CLIENT_STRUCTURE.md   ·   the head over the engine",
    color: C.slateLite,
    notes: "Deck 7, the last one. Everything the user SEES is this MAUI client; everything that HAPPENS (matching, settlement, candles, bots) is the server. Since the Phase-3 client/server split the client does almost no thinking — before it, the engine ran in-process. This deck is deliberately simpler than the engine/bot decks. Companion to BOT_MECHANICS.md and ENGINE_MECHANICS.md.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here", zone: CLIENT,
    title: "The client is the front of the pipeline — CLIENT only",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "why the client holds no engine, no DB, no matcher", bold: true },
      "the two data planes: REST to bootstrap, SignalR to stay live",
      "how a page is wired — MVVM, DI, Shell nav, one hub",
      "the screens: Market, Trade, Portfolio, the mood pane",
    ],
    notes: "This deck lights only the CLIENT stage. Everything to the right (API→ENTRY→EXEC→MATCH→SETTLE→DB) lives in KieshStockExchange.Server and was covered in decks 1-6. The client is a read/write observer of the one authoritative server — N heads must all observe the same book, so the engine cannot live inside any of them.",
  });

  // 3 · STAT — what the client is
  T.contentSlide(p, {
    deckNum: N, section: "The shape", accent: C.slateLite, pipe: CLIENT,
    title: "A thin skin: no engine, no database, no matching",
    visual: { kind: "stat", cards: [
      { v: "3", k: "assemblies", d: "Client · Shared · Server" },
      { v: "2", k: "data planes", d: "REST out · one SignalR hub in" },
      { v: "0", k: "in-process engine", d: "every Api*/SignalR* is a proxy" },
      { v: "8", k: "Shell routes", d: "Login…Trade, no flyout chrome" },
    ]},
    right: { title: "Presentation shell only", bullets: [
      { t: "Client = Views (XAML) + ViewModels + proxy services.", bold: true },
      "Shared = Models + interfaces, the same types on both ends.",
      "Reads go out over HTTP; live state arrives over one hub.",
      "Even IDataBaseService is now an HTTP wrapper — no local DB.",
    ]},
    foot: "TFMs: client head net9.0-windows; Shared net9.0 — wire DTOs are literally the same compiled types",
    notes: "Three assemblies: KieshStockExchange (this doc), KieshStockExchange.Shared (net9.0 — Stock, Order, Fund, Position, LiveQuote, Candle, PortfolioSnapshot; referenced by both ends so DTOs are identical types), KieshStockExchange.Server (out of scope). The old in-process duplicates — OrderExecutionService, LocalDBService, AiTradeService — were deleted at Phase 3. Layering (Views→ViewModels→Services→HTTP/hub) is by CONVENTION only, no analyzer enforces it. Client needs a reachable Server:BaseUrl (Resources/Raw/appsettings.json, fallback http://localhost:5000) or every screen shows empty cards.",
  });

  // 4 · FLOW — MVVM / DI / Shell wiring
  T.contentSlide(p, {
    deckNum: N, section: "How a page is wired", accent: C.up, pipe: CLIENT,
    title: "DI builds a page; Shell navigates to it programmatically",
    visual: { kind: "flow", nodes: [
      { t: "MauiProgram.CreateMauiApp", sub: "one composition root · all DI", color: C.slate },
      { t: "Shell route → transient Page + VM", sub: "ctor-injects the whole VM graph", color: C.slate },
      { t: "OnAppearing → InitializeAsync", sub: "loads data, not the constructor", color: C.upInk },
      { t: "OnDisappearing → Cleanup + Dispose", sub: "cascades to child VMs", color: C.slate },
    ]},
    right: { title: "MVVM, uniform everywhere", bullets: [
      { t: "Pages + VMs are transient; shared-state services are singletons.", bold: true },
      "Composite VMs own child VMs (chart, book, ticket) as properties.",
      "CommunityToolkit source-gens [ObservableProperty] / [RelayCommand].",
      "Transient VMs must Dispose singleton subscriptions — or leak.",
    ]},
    foot: "AppShell is flat, FlyoutBehavior=Disabled; all GoToAsync marshalled onto the UI thread",
    notes: "MauiProgram wires everything in one method; order matters twice (base-URL-before-DI, and eager resolution at the end forcing 4 singletons to construct so their ctors subscribe before any page exists: TokenStore, INotificationService, ConnectionStatusViewModel, ApiOrderCacheBridge). Singletons = anything holding shared/live state. Transients = pages + VMs, fresh per navigation. TradePage(TradeViewModel vm) — page never news up its VM. StockAwareViewModel base gives cancel-and-swap CTS reactivity: switch a stock once and Chart/OrderBook/tables each independently cancel-and-reload. GoToAsync mutates the visual tree so must run on the UI thread. Invariant: a missed unsubscribe leaks a handler onto the singleton every visit.",
  });

  // 5 · FLOW — the single SignalR hub
  T.contentSlide(p, {
    deckNum: N, section: "The live plane", accent: C.gold, pipe: CLIENT,
    title: "One hub connection carries everything live",
    visual: { kind: "flow", nodes: [
      { t: "engine event", sub: "quote · candle · order · portfolio", color: C.slate },
      { t: "server *Broadcaster → hub group", sub: "quotes: · orders: · portfolio:", color: C.slate },
      { t: "MarketHubClient re-raises as .NET event", sub: "sole owner of the HubConnection", color: C.upInk },
      { t: "SignalR*/Api* proxy → ViewModel", sub: "VM marshals to the UI thread", color: C.slate },
    ]},
    right: { title: "One socket, replayed groups", bullets: [
      { t: "Exactly one HubConnection to /hubs/market, auto-reconnect.", bold: true },
      "Group families: per-listing quotes, per-user orders, telemetry.",
      "Tracked groups are replayed on every reconnect — no stale UI.",
      "Reference-counted joins: many VMs on one stock share one group.",
    ]},
    foot: "Auth over WebSocket via AccessTokenProvider, read fresh each (re)connect so rotated tokens survive",
    notes: "IMarketHubClient/MarketHubClient owns the sole HubConnection — one socket = one auth handshake, one reconnect path, one replay point. Every live proxy (SignalRMarketDataClient, SignalRCandleService, ApiPortfolioClient, ApiOrderBookFeed, ApiOrderCacheBridge) subscribes to its C# events, not its own socket. Three server hosted services originate pushes: MarketHubBroadcaster (quotes/candles/portfolio), OrderBookBroadcaster (book snapshots), TelemetryBroadcaster (admin). PortfolioChanged is a placeholder broadcast to portfolio:0 — client treats it as a bare 're-fetch' trigger. ReplayGroupsAsync re-joins every tracked group after both Reconnected and manual restart. Telemetry group exists but this client doesn't consume it (bot dashboard polls REST instead).",
  });

  // 6 · STATEMENT — the rule of thumb
  T.statement(p, {
    text: "HTTP bootstraps a screen. SignalR keeps it live.",
    sub: "Push cost scales with change-rate; poll cost scales with screens × interval — so prices push, mutations POST.",
    notes: "The single most important thing to internalise about this client. REST (named HttpClient 'KSE.Server' via IHttpClientFactory) = on-demand loads + ALL mutations: history fetches, order placement, deposit/convert, admin start/stop, the mood poll — it needs a definite per-call accept/reject and paging params. SignalR = the live plane: prices, candles, book, order/portfolio invalidations, notifications. Most feeds are cache-first: read the last snapshot synchronously, HTTP-fetch on a miss, let the next push overwrite. Two deliberate exceptions: the Market page adds a 1s poll fallback for idle stocks, and the Bot dashboard is pure REST-poll (never joins telemetry).",
  });

  // 7 · FLOW — Market page
  T.contentSlide(p, {
    deckNum: N, section: "Screen · Market", accent: C.up, pipe: CLIENT,
    title: "Market is a live browse-and-search over every listing",
    visual: { kind: "flow", nodes: [
      { t: "SubscribeAllAsync — USD + EUR", sub: "warm the LiveQuote cache", color: C.slate },
      { t: "Poll() → MarketRow (in place)", sub: "Move/Insert/Remove, never Clear+Add", color: C.slate },
      { t: "push OnQuoteUpdated + 1s fallback", sub: "sub-second sweep · idle stocks still tick", color: C.upInk },
    ]},
    right: { title: "What's on the page", bullets: [
      { t: "Tabs: USD / EUR / Watchlist (spans both currencies).", bold: true },
      "Sortable, searchable, paginated all-stocks table.",
      "Starred watchlist card with drag-reorder, own row cache.",
      "Top Gainers / Losers / Most-Active, off-thread snapshots.",
    ]},
    foot: "Rows update in place to avoid CollectionView flicker; polling torn down on page-leave",
    notes: "MarketViewModel over IMarketDataService.Quotes. Subscribes both currencies so the watchlist populates regardless of active tab. Two redundant triggers: push-driven OnQuoteUpdated (debounced to one rebuild per UI frame via _pollPending) and a 1s IDispatcherTimer fallback for non-trading stocks. TrendingService is an independent 5s PeriodicTimer snapshotting LiveQuote into frozen MoverSnapshot records off-thread (avoids wrong-thread PropertyChanged), de-duped to one listing per stock; it intentionally does NOT subscribe to QuoteUpdated (UI-thread storms). PausePolling on disappear so 140+ subscriptions don't storm the UI thread on the Trade page. TradeAsync sets ISelectedStockService and navigates to ///TradePage.",
  });

  // 8 · STAT — the Trade page payload
  T.contentSlide(p, {
    deckNum: N, section: "Screen · Trade", accent: C.gold, pipe: CLIENT,
    title: "Trade is the dense screen — one composite page",
    visual: { kind: "stat", cards: [
      { v: "6", k: "chart types", d: "candles · line · Heikin-Ashi · …" },
      { v: "5", k: "drawing tools", d: "persisted in (time, price) space" },
      { v: "4", k: "table tabs", d: "orders · history · tape · positions" },
      { v: "70/30", k: "layout split", d: "chart+book+ticket / table card" },
    ]},
    right: { title: "Three panels, one selected stock", bullets: [
      { t: "Symbol/price bar over a Picker, plus a watch-star.", bold: true },
      "Chart + order book + order-entry ticket up top.",
      "Segmented table card below keeps orders and fills live.",
      "All panels share the stock via StockAwareViewModel.",
    ]},
    foot: "Chart-row height pinned in code-behind so inner ScrollViews don't inflate the star row",
    notes: "TradePage ↔ TradeViewModel, a composite owning ChartVm/OrderBookVm/PlacingVm/OpenOrdersVm. 70/30 vertical split. Chart row height pinned imperatively (UpdateTradeLayout/UpdatePanelHeights) — the reference_maui_scrollview_starrow_sizing gotcha. Bottom card = SegmentedTabView with Open Orders / Order History / Transaction History / Positions + a 'current stock only' filter, fed by IOrderCacheService + ITransactionService kept live by ApiOrderCacheBridge off OrderUpdated pushes. PickerSelection setter spawns Task.Run so the ~8-subscriber SelectedStockService.Set fan-out doesn't freeze the UI thread on rapid clicks.",
  });

  // 9 · FLOW — the three Trade panels
  T.contentSlide(p, {
    deckNum: N, section: "Screen · Trade panels", accent: C.slateLite, pipe: CLIENT,
    title: "Chart, book, and ticket each fetch their own slice",
    visual: { kind: "flow", nodes: [
      { t: "Chart — history + live forming bar", sub: "HTTP candles, SignalR closes, synth tick", color: C.slate },
      { t: "Order book — cache-first depth", sub: "SnapshotChanged, BookVersion-gated", color: C.slate },
      { t: "Ticket — validate then POST", sub: "IOrderEntryService · no local mutation", color: C.upInk },
    ]},
    right: { title: "Read live, write and wait", bullets: [
      { t: "Chart overlays user data: order lines, fills, position line.", bold: true },
      "Book renders cumulative depth, auto-bucketed price steps.",
      "Ticket: side × type, bracket builder, quantity slider.",
      "Submit POSTs, then waits for the fill push — never optimistic.",
    ]},
    foot: "Client validation is a cheap pre-filter; the server is the authority and authors every toast",
    notes: "Chart (ChartViewModel ~1750 lines, the heaviest component): GraphicsView + CandleChartDrawable, immutable snapshot records VM→drawable, RedrawRequested coalesced ~60fps. HTTP-loads history then streams closed candles; the live forming bar is synthesized from the price tick (TrySyncLiveCandle) since the server streams only closed candles. Overlays from user data: open-order lines (draggable → modify ticket), fill markers (VWAP-aggregated), trigger markers, position line (WAC basis, live P&L). Book (ApiOrderBookFeed): cache-first, HTTP fallback GET /api/order-book/{stockId}/{currency}, out-of-order pushes dropped by BookVersion; OrderBookDepthAggregator auto-scales the bucket until one holds ≥25% of side volume. Ticket (PlaceOrderViewModel): ValidateInputs client-side (qty>0, valid price, trigger side) is a pre-filter not the authority; dispatches to PlaceLimit/SlippageMarket/TrueMarket/StopMarket/StopLimit/Bracket; success/fail toasts server-authored via NotificationReceived — no optimistic local toast (would double up).",
  });

  // 10 · MONO — Fear/Greed mood pane
  T.contentSlide(p, {
    deckNum: N, section: "Screen · Market Mood", accent: C.gold, pipe: CLIENT,
    title: "The Fear/Greed pane seeds history, then polls forward",
    visual: { kind: "mono", caption: "MARKET MOOD · CHART SUB-PANE", size: 12.5, lines: [
      { t: "// no server-side mood history exists", color: "8FA3C0" },
      { t: "seed  <- Candle.MarketMood  (F&G, if stamped)", color: "9FE7C6" },
      { t: "      else tanh(k*ln(close/EMA))  // faked", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "loop  every 4s  (PeriodicTimer):", color: C.monoInk },
      { t: "  GET /api/market/mood/{stockId}", color: "F5B942" },
      { t: "  -> accumulate up to 2000 samples", color: C.monoInk },
      { t: "  null on fault -> pane stalls, never throws", color: "F5A3A9" },
    ]},
    right: { title: "A believable mood line", bullets: [
      { t: "Toggle ShowMoodPane; poll restarts on stock change.", bold: true },
      "Back-history seeded from loaded candles' stamped mood.",
      "Missing stamps fall back to a momentum stand-in, marked fake.",
      "The client face of the server's MarketMoodService.",
    ]},
    foot: "See BOT_MECHANICS.md §2.10 for how the composite Fear/Greed score is computed server-side",
    notes: "The mood pane is the client face of Bots:Mood:* / MarketMoodService. There is NO stored mood history server-side, so SeedMoodFromCandles builds a back-history: it uses each candle's server-stamped Candle.MarketMood where present (the composite F&G, correct for the timeframe), else a momentum reconstruction tanh(k·ln(close/EMA)) as a believable stand-in that is explicitly marked in-code as faked. Then it fills forward via ApiMarketMoodClient.GetMoodAsync → MoodPollLoopAsync, GET /api/market/mood/{stockId} every 4s, accumulating up to 2000 samples on the candle time axis. Returns null on any transport fault so the pane stalls rather than throws. No-op while the pane is off. This is one of the REST-poll exceptions to the push rule — the mood is low-frequency and there's no group for it.",
  });

  // 11 · FLOW — Portfolio page
  T.contentSlide(p, {
    deckNum: N, section: "Screen · Portfolio", accent: C.up, pipe: CLIENT,
    title: "Portfolio values holdings live in one base currency",
    visual: { kind: "flow", nodes: [
      { t: "funds + positions snapshot", sub: "IUserPortfolioService", color: C.slate },
      { t: "value via live quotes + FX mid-rates", sub: "walk cross-currency to base", color: C.slate },
      { t: "RefreshMetrics on every change", sub: "SnapshotChanged · tape · session", color: C.upInk },
    ]},
    right: { title: "KPIs at a glance", bullets: [
      { t: "Total equity = cash + market value of holdings.", bold: true },
      "Today's Δ vs session open; all-time realized + unrealized P&L.",
      "Allocation pie: top-7 positions plus Other.",
      "Six sub-VMs: currencies, holdings, orders, history, tape, funds.",
    ]},
    foot: "Account page mirrors this: profile, base-currency picker, funds card, WAC realized-P&L activity",
    notes: "PortfolioViewModel is a shell over six sub-VMs (Currencies, Holdings, OpenOrders, OrderHistory, Transactions, FundsHistory). RefreshMetrics runs on IUserPortfolioService.SnapshotChanged, ITransactionService.TransactionsChanged, and session change. KPIs are base-currency, cross-currency values walked live through IFxRateService mid-rates; holdings valued off the live LiveQuote cache. All-time P&L is liquidation-equivalent (sells received + current holdings value − buys paid). Allocation pie uses Okabe-Ito palette, top-7 + Other. Account/Funds page (§13) is the sibling: profile from IAuthService.CurrentUser, base-currency picker, Funds card, and an Activity card with WAC realized P&L (PnLIsApproximate flips if inventory ever went short). Deposit/withdraw/convert POST to /api/portfolio/* via ApiPortfolioClient. Two admin surfaces exist too — Bot dashboard (REST-polled) and the CRUD Admin console.",
  });

  // 12 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "The client is a thin skin — no engine, no DB, no matching lives in it.",
      "Two planes: REST bootstraps a screen, one SignalR hub keeps it live.",
      "Market, Trade, Portfolio and the mood pane all read the same shared server.",
    ],
    next: "That's the whole product — click to conserved fill, and back to the screen",
    notes: "Final deck of the seven. The verbal close: we opened deck 1 with one order's journey through the pipeline; this deck showed the front of that pipeline — the screen the user actually touches. It reads live over one hub and writes over HTTP, but it never decides anything; all authority — matching, settlement, conservation, the 20k bots — lives in the server we toured in decks 1-6.",
  });
};
