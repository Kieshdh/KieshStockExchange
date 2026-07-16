// build_05_api.js — Section 5: the server's HTTP + realtime surface.
// Source: docs/explainers/API_REFERENCE.md. Structure copied from build_01_arch.js.
module.exports = function (T, p) {
  const { C } = T;
  const N = 5;
  const ZONE = ["CLIENT", "API"];

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 5 of 7 · the network boundary",
    title: "The API Surface",
    subtitle: "Every wire a client or ops tool can touch: 28 REST controllers and one SignalR hub. The client is a thin proxy — it never runs the engine, so this surface IS the API.",
    footer: "API_REFERENCE.md   ·   the server's HTTP + realtime edge",
    color: C.upInk,
    notes: "Deck 5 of 7. The server is an ASP.NET Core app (KieshStockExchange.Server): 28 MVC controllers under Controllers/ plus Hubs/MarketHub.cs. Every controller routes off an explicit [Route(\"api/...\")] — no global prefix. Because the client never runs the engine, this HTTP + WebSocket edge is the entire contract. Trading/portfolio DTOs live in Shared/Services/MarketEngineServices/; auth DTOs are inline in AuthController.cs.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here", zone: ZONE,
    title: "This deck is the client-facing edge of the server",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "how 28 controllers group into five purposes", bold: true },
      "the one JWT fallback plus admin + ownership gates",
      "why REST bootstraps a screen and SignalR keeps it live",
      "what a MarketHub push actually carries on the wire",
    ],
    notes: "We sit at CLIENT→API: the boundary the deck-6 client consumes and the deck-3 engine hides behind. Everything past API (ENTRY→EXEC→MATCH→SETTLE→DB) is the engine — this deck stops at the door.",
  });

  // 3 · STAT — the surface at a glance
  T.contentSlide(p, {
    deckNum: N, section: "The surface", accent: C.up, pipe: ZONE,
    title: "One HTTP edge, one realtime hub, one auth default",
    visual: { kind: "stat", cards: [
      { v: "28", k: "REST controllers", d: "explicit [Route(\"api/...\")] each" },
      { v: "1", k: "SignalR hub", d: "MarketHub at /hubs/market" },
      { v: "JWT", k: "default on everything", d: "auth unless [AllowAnonymous]" },
      { v: "60", k: "orders / min / user", d: "auth 10/min/IP · reads free" },
    ]},
    right: { title: "The whole edge", bullets: [
      { t: "The client is a proxy — this surface is the only API.", bold: true },
      "REST for request/response; one hub for live push.",
      "A global fallback policy locks down every endpoint.",
      "Only auth + a couple of health probes are anonymous.",
    ]},
    notes: "Bearer token issued by POST /api/auth/login or /register (LoginResponse carries it). Rate limits from Program.cs AddRateLimiter, over-limit → 429 no queue: \"auth\" 10/min per IP on login/register; \"orders\" 60/min per user on place/modify/cancel + deposit/withdraw/convert. Reads unlimited. Health: /healthz/live and /healthz/ready are [AllowAnonymous].",
  });

  // 4 · FLOW — controllers by purpose
  T.contentSlide(p, {
    deckNum: N, section: "Controllers", accent: C.slateLite, pipe: ZONE,
    title: "Twenty-eight controllers fall into five jobs",
    visual: { kind: "flow", nodes: [
      { t: "Doorway", sub: "auth · session · version", color: C.upInk },
      { t: "Trading + portfolio", sub: "orders · brackets · deposit/convert", color: C.slate },
      { t: "Per-user reads", sub: "funds · positions · trades · inbox", color: C.slate },
      { t: "Market + reference data", sub: "book · candles · quotes · mood", color: C.slate },
      { t: "Admin + ops", sub: "seed · retention · bot fleet · shutdown", color: C.slate },
    ]},
    right: { title: "Grouped by what they do", bullets: [
      { t: "Doorway is the only anonymous group — it mints tokens.", bold: true },
      "Trading writes go through the engine, never raw CRUD.",
      "Market data reads are open to any logged-in client.",
      "Admin controls the fleet, seed, retention, and shutdown.",
    ]},
    foot: "Full controller → route → verb → auth table: API_REFERENCE.md §2",
    notes: "Doorway: AuthController (Anon, mints JWT), SessionController, VersionController. Trading: OrderController (place/place-bracket/modify/cancel, Owner + orders limit) maps the (Stop,Entry,Side) triple to an IOrderEntryService.Place*Async; EngineController portfolio deposit-withdraw/convert-internal. Per-user reads (Owner-gated via CanAccessUser): Fund/Position/Transaction/FundTransaction/Message by-user. Market data (JWT reads): OrderBook, Candle, Stock(+Listing/Price), FxRate, MarketLookup, MarketMood, AIUser. Admin/ops: AdminController, AdminBotController, AdminLogsController, SeedController, RetentionController, ServerController.",
  });

  // 5 · AUTH — the three layers
  T.contentSlide(p, {
    deckNum: N, section: "Auth model", accent: C.gold, pipe: ZONE,
    title: "One fallback, one role, then per-endpoint ownership",
    visual: { kind: "flow", nodes: [
      { t: "Global fallback policy", sub: "RequireAuthenticatedUser — valid JWT or 401", color: C.upInk },
      { t: "Admin role gate", sub: "[Authorize(Roles=\"admin\")] on ops routes", color: C.slate },
      { t: "Per-user ownership", sub: "route userId == token sub, else 403", color: C.gold },
    ]},
    right: { title: "Three layers, in order", bullets: [
      { t: "Default is authenticated: no anonymous access anywhere.", bold: true },
      "Admin routes add a lowercase \"admin\" role claim check.",
      "\"My data\" routes match the route id to the JWT sub.",
      "Trading writes also require req.UserId == the caller.",
    ]},
    foot: "SignalR can't send a header — the hub lifts the token off ?access_token=",
    notes: "Program.cs: options.FallbackPolicy = RequireAuthenticatedUser() — every endpoint needs a JWT unless [AllowAnonymous]. Admin gate class-wide on ServerController/SeedController/RetentionController and per-action on cross-user reads; role claim issued lowercase by JwtTokenService. Ownership via ClaimsExtensions.GetUserId()/CanAccessUser(userId) (true if caller==userId OR admin). JWT validated in AddJwtBearer (issuer/audience/lifetime/HMAC key; refuses to boot in Production with the dev key). JwtBearerEvents.OnMessageReceived lifts ?access_token= for /hubs paths.",
  });

  // 6 · FLOW — REST bootstraps, SignalR keeps live
  T.contentSlide(p, {
    deckNum: N, section: "The pattern", accent: C.up, pipe: ZONE,
    title: "REST bootstraps a screen; SignalR keeps it live",
    visual: { kind: "flow", nodes: [
      { t: "GET a snapshot over REST", sub: "book · candle history · orders · portfolio", color: C.slate },
      { t: "Join the matching hub group", sub: "quotes / orders / portfolio", color: C.slate },
      { t: "Receive push events", sub: "thereafter it's push, not poll", color: C.upInk },
      { t: "Re-fetch on a light trigger", sub: "cache invalidated → REST for truth", color: C.slate },
    ]},
    right: { title: "Fetch once, then listen", bullets: [
      { t: "A screen loads its initial state with one HTTP call.", bold: true },
      "Then it joins a hub group and stops polling.",
      "OrderUpdated / PortfolioChanged carry no payload.",
      "They just say \"go refresh\" — REST stays the source of truth.",
    ]},
    foot: "Mirrors the client's REST-vs-SignalR split from the server side (API_REFERENCE.md §4)",
    notes: "Three IHostedService broadcasters bridge in-process engine events onto hub groups; MarketHub itself only owns subscribe/unsubscribe. Bootstrap→live pairings: QuoteUpdated ← latest-price; CandleClosed ← candles/by-stock-range; OrderBookSnapshot ← order-book (≤1 push/100ms); OrderUpdated ← orders/by-user; PortfolioChanged ← funds+positions/by-user. The payload-light events deliberately invalidate a cache so the client re-fetches authoritative state over REST rather than trusting a pushed body.",
  });

  // 7 · MONO — a MarketHub event
  T.contentSlide(p, {
    deckNum: N, section: "MarketHub", accent: C.up, pipe: ZONE,
    title: "Most pushes are a bare 'go refresh' trigger",
    visual: { kind: "mono", caption: "SERVER → CLIENT · group orders:{userId}", size: 12.5, lines: [
      { t: "// engine settles a fill, then broadcasts:", color: "8FA6C4" },
      { t: "OrderUpdated  { UserId: 4021 }", color: "9FE7C6" },
      { t: "", color: C.monoInk },
      { t: "// client's reaction — NOT the order itself:", color: "8FA6C4" },
      { t: "GET /api/orders/by-user/4021   ← re-fetch", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "// compare: a payload event", color: "8FA6C4" },
      { t: "OrderBookSnapshot { Bids[], Asks[],", color: C.monoInk },
      { t: "  LastUpdatedUtc, BookVersion }", color: C.monoInk },
    ]},
    right: { title: "Two shapes of push", bullets: [
      { t: "OrderUpdated is just an envelope — a userId, no order.", bold: true },
      "The client answers it with a REST re-fetch.",
      "OrderBookSnapshot ships real depth plus a version.",
      "BookVersion is monotonic — drop any stale push.",
    ]},
    foot: "JoinCandles also ref-counts the aggregator — skip it and the live bar looks frozen",
    notes: "Client→server hub methods: JoinQuotes/JoinCandles/JoinUserGroups/JoinTelemetry (+Leave). JoinUserGroups ignores the wire userId — the server derives id from the JWT sub (param kept for pre-JWT back-compat). JoinTelemetry is admin-only via an in-method Context.User.IsInRole(\"admin\") check that throws HubException. JoinCandles calls ICandleService.Subscribe, ref-counting the server aggregator; without it the engine never emits CandleClosed for that key. OrderBookSnapshot: StockId, Currency, Bids/Asks (DepthLevel = Price/Quantity/OrderCount), LastUpdatedUtc, BookVersion (drop stale). Hub is [Authorize] — valid JWT required to connect.",
  });

  // 8 · MONO — auth flags (warning)
  T.contentSlide(p, {
    deckNum: N, section: "Auth flags", accent: C.down, pipe: ZONE,
    title: "A few admin routes ride only the fallback",
    visual: { kind: "mono", caption: "WEAKER THAN THEY LOOK · §5", size: 12.5, lines: [
      { t: "POST api/admin/drop-recreate", color: "F5A3A9" },
      { t: "   any JWT → wipes the whole schema", color: C.monoInk },
      { t: "POST api/admin/bots/stop", color: "F5A3A9" },
      { t: "   any JWT → halts market liquidity", color: C.monoInk },
      { t: "DELETE api/users/{id}", color: "F5A3A9" },
      { t: "   any JWT → deletes any account", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "// no [Authorize(Roles=\"admin\")], no ownership", color: "8FA6C4" },
    ]},
    right: { title: "Known, deferred, documented", bullets: [
      { t: "These carry no admin gate — only the JWT fallback.", bold: true },
      "Reference-data writes are likewise open to any token.",
      "In-code notes read as \"role-gating lands in Phase 7\".",
      "It's a trusted-client simulation — flagged, not exploited.",
    ]},
    foot: "Register is anonymous + self-service, so any user can obtain a token — confirm intent",
    notes: "§5 ranked by blast radius: (1) AdminController api/admin/* — drop-recreate/reset/insert-all/update-all, no admin gate; (2) AdminBotController — start/stop/scaler + telemetry CSV leaks; (3) UserController — full CRUD, no gate no ownership; (4) MessageController class-level CRUD — GetAll returns everyone's inbox; (5) UserPreferences/UserWatchlist — no ownership check on by-user; (6) reference-data write CRUD (Stock/StockPrice/StockListing/Candle/AIUser) JWT-only; (7) SessionController stale 'anyone can hit' comment. Missing gates are verified against controller attributes; intent (deliberate deferral vs hole) is inferred — sibling ops controllers ARE admin-gated, so contrast is real. Confirm before treating as a bug.",
  });

  // 9 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "28 controllers + one hub are the entire client-facing API.",
      "One JWT fallback, an admin role, and per-endpoint ownership.",
      "REST bootstraps the screen; SignalR pushes 'go refresh'.",
    ],
    next: "CLIENT_STRUCTURE — the MAUI app that consumes this surface",
    notes: "Hand off to deck 6. Verbal bridge: we've mapped every wire the server exposes; next we meet the thin MAUI client that fetches over these REST routes and stays live on this hub — the consumer side of the same REST-vs-SignalR split.",
  });
};
