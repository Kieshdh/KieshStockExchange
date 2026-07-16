// build_06_host.js — SERVER_HOST_AND_OPS.md distilled. Deck 6 of 7.
// Story spine: the process boots in a strict order, hosts a fleet of actors,
// is configured by three layered tiers, and runs as three Docker containers
// where one env line is the reversible lever.
module.exports = function (T, p) {
  const { C } = T;
  const N = 6;
  const ZONE = ["API", "ENTRY", "EXEC", "MATCH", "SETTLE", "DB"]; // the whole server-side process

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 6 of 7 · the process + the box",
    title: "The Host and the Box",
    subtitle: "One .NET 9 process hosts the API, the hub, the engine, and a fleet of background actors — run on the prod VM as three Docker containers, tuned by one reversible env line.",
    footer: "SERVER_HOST_AND_OPS.md   ·   the layer underneath the engine + the bots",
    color: C.gold,
    notes: "This deck is the layer underneath ENGINE and BOT_MECHANICS: the DI graph they live in and the container that hosts it. HOST half = §1–§4 (the process): composition root, config, request pipeline, hosted services. OPS half = §5–§8 (the box): Dockerfile, compose, Caddy, deploy/rollback. Persistence is Postgres via hand-written Dapper (PgDBService); EF Core exists only for migrations.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here", zone: ZONE,
    title: "Everything server-side lives inside one hosted process",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "the strict boot sequence that warms the caches", bold: true },
      "the long-lived actors that keep the market alive",
      "how three config tiers merge into one runtime knob",
      "how a change reaches prod — and how to roll it back",
    ],
    notes: "The host spans the whole server-side pipeline (API→DB); only CLIENT lives elsewhere. Program.cs is the composition root — top-level statements read top-to-bottom = boot order. Grep symbols, not line numbers.",
  });

  // 3 · FLOW — boot sequence (the strict order)
  T.contentSlide(p, {
    deckNum: N, section: "Boot sequence", accent: C.gold, pipe: ZONE,
    title: "Boot order is load-bearing, not incidental",
    visual: { kind: "flow", nodes: [
      { t: "Exception nets armed", sub: "unobserved + unhandled → Serilog", color: C.slate },
      { t: "Migrate if Db:AutoMigrate", sub: "EF fallback · failure logged, swallowed", color: C.slate },
      { t: "Seed empty DB", sub: "must precede the warm-up", color: C.upInk },
      { t: "Cold-load stocks · rehydrate brackets", sub: "or every order is rejected", color: C.slate },
      { t: "Candle warm-up → app.Run()", sub: "rings into RAM · loops start", color: C.slate },
    ]},
    right: { title: "Phase C: warm-up before Run()", bullets: [
      { t: "Seed runs before caches snapshot the DB.", bold: true },
      "Stock catalogue load — else \"Invalid stock ID\" everywhere.",
      "Brackets rehydrate so they self-manage after restart.",
      "ShutdownTimeout 60s lets the bot loop flush cleanly.",
    ]},
    foot: "A faulting BackgroundService must not take the host down — HostOptions ignores it",
    notes: "Program.cs three phases: (A) builder.Services registration, (B) builder.Build(), (C) strict pre-Run warm-up. Statics (MatchSymmetryProbe, MidReference, FxRateService) configured before Build() by reading builder.Configuration directly. Seed used to be a hosted service — but hosted services only start at Run(), which froze empty caches; moved into phase C. Candle warm-up subscribes 7 resolutions × currencies, BackfillUpwardAsync then PrimeRingsAsync (last 500 candles/key).",
  });

  // 4 · FLOW — hosted services (merged)
  T.contentSlide(p, {
    deckNum: N, section: "Hosted services", accent: C.up, pipe: ZONE,
    title: "A fleet of actors keeps the market alive",
    visual: { kind: "flow", nodes: [
      { t: "BotLoopHostedService", sub: "starts AiTradeService — the ~20k bots", color: C.upInk },
      { t: "StopTriggerWatcher", sub: "promotes armed stops · circuit breaker", color: C.slate },
      { t: "MarketHub + OrderBook broadcasters", sub: "engine events → SignalR groups", color: C.slate },
      { t: "Telemetry · Retention · warm-up", sub: "dashboard · history prune · cold-start", color: C.slate },
    ]},
    right: { title: "Long-lived, mostly config-gated", bullets: [
      { t: "Bots:AutoStart is the market's on/off switch.", bold: true },
      "Stop watcher cold-loads armed stops — they survive restart.",
      "Broadcasts are fire-and-forget; a slow client never blocks.",
      "Retention prunes the two high-churn tables each tick.",
    ]},
    foot: "All in Services/HostedServices/ · grep AddHostedService in Program.cs",
    notes: "IHostedService.StartAsync fires at app.Run(); BackgroundService loops in ExecuteAsync. StopTriggerWatcher registered 3× (concrete, IStopWatcher, IHostedService) all resolving one singleton; Lazy<IStopWatcher> breaks the DI cycle. OrderBookBroadcaster throttles max 1/100ms per key, BookVersion hash-skips no-ops. MarketHubBroadcaster: QuoteUpdated/CandleClosed → quotes:{stockId}:{currency}, portfolio → portfolio:{userId}. orders:{userId} fed separately by SignalROrderCacheService. RetentionHostedService gated on Retention:Enabled; BotTelemetryWarmup one-shot 2s after boot.",
  });

  // 5 · FLOW — config layering
  T.contentSlide(p, {
    deckNum: N, section: "Config layering", accent: C.slateLite, pipe: ZONE,
    title: "Three tiers merge; the env line wins",
    visual: { kind: "flow", nodes: [
      { t: "appsettings.json", sub: "the base — every default lives here", color: C.slate },
      { t: "appsettings.Production.json", sub: "deep-merge · only leaf keys override", color: C.slate },
      { t: "Section__Key env vars", sub: "highest precedence · __ maps to :", color: C.gold },
    ]},
    right: { title: "Why the top tier matters", bullets: [
      { t: "Bots__Mood__Enabled sets Bots:Mood:Enabled.", bold: true },
      "Trailing __0 becomes an array index.",
      "Live experiment flags live only in the env tier.",
      "Change a prod knob without rebuilding appsettings.",
    ]},
    foot: "GetValue(\"Section:Key\", default) — the code default runs if absent from all three",
    notes: "Standard ASP.NET Core IConfiguration, lowest-to-highest. Double-underscore __ is the config-path separator because ':' isn't legal in a Linux env var name. Prod's appsettings re-asserts Seed:AutoOnEmptyDb, switches Serilog to JSON formatter, carries Bots:* tuning (TradeIntervalMs, Staggering:Slots, DipBuyStrength, Rotator/BankEstimate on) + a Retention window. Cors__AllowedOrigins__0 = Cors:AllowedOrigins[0]. Practical consequence: to change a prod runtime knob you edit the compose env block and up -d server, not appsettings.",
  });

  // 6 · MONO — the live prod env override block
  T.contentSlide(p, {
    deckNum: N, section: "Prod override", accent: C.gold, pipe: ZONE,
    title: "The live experiment flags are just env lines",
    visual: { kind: "mono", caption: "docker-compose.prod.yml · server.environment", size: 11, lines: [
      { t: "server:", color: C.monoInk },
      { t: "  environment:", color: C.monoInk },
      { t: "    Bots__Mood__Enabled: \"true\"", color: "9FE7C6" },
      { t: "    Bots__Mood__TakerCoupling: \"true\"", color: "9FE7C6" },
      { t: "    Bots__Mood__PerStrategy: \"true\"", color: "9FE7C6" },
      { t: "    Bots__ExogShock__Enabled: \"true\"", color: "F5CE7B" },
      { t: "    Bots__ExogShock__GlobalCoFire: \"true\"", color: "F5CE7B" },
      { t: "    Bots__ExogShock__GlobalCoFireNotionalFrac: \"0.1\"", color: "F5CE7B" },
      { t: "", color: C.monoInk },
      { t: "  # roll back = delete the line + up -d server", color: C.muted },
    ]},
    right: { title: "ON in prod, default-off in code", bullets: [
      { t: "Fear/Greed per-strategy reaction — live since 2026-07-14.", bold: true },
      "GlobalCoFire cross-stock correlation @0.10 — council-approved.",
      "No schema, no rebuild — reversible in one restart.",
      "Backups on the box for a full config restore.",
    ]},
    foot: "Each line maps to a Bots:Mood:* / Bots:ExogShock:* key by the __→: rule",
    notes: "This env block carries the flags ON in prod but default-off in appsettings — reversible, no schema, no config rebuild. Fear/Greed Stage A+B: Bots__Mood__ Enabled/TakerCoupling/ConvictionFearBid/PerStrategy/MMWiden all true. GlobalCoFire: Enabled=true, GlobalFraction=0.4, MeanIntervalMinutes=0.5, GlobalCoFire=true, GlobalCoFireFraction=0.15, GlobalCoFireNotionalFrac=0.1, AnchorTracksShock=false. Prod override also un-publishes Postgres (ports: !reset [] — Compose ≥2.24, concatenation means a plain [] wouldn't drop the inherited 5432) and defines the profiled migrate one-shot.",
  });

  // 7 · STAT — request pipeline guardrails
  T.contentSlide(p, {
    deckNum: N, section: "Request pipeline", accent: C.up, pipe: ["API"],
    title: "The pipeline authenticates, limits, and checks health",
    visual: { kind: "stat", cards: [
      { v: "60", k: "orders / min", d: "per-user fixed window · else 429" },
      { v: "10", k: "logins / min", d: "per-IP auth cap" },
      { v: "256", k: "MB body cap", d: "settle-group bundle at peak load" },
      { v: "live", k: "healthcheck", d: "liveness, not readiness — no restart loops" },
    ]},
    right: { title: "Middleware order matters", bullets: [
      { t: "Forwarded headers first — see the real client behind Caddy.", bold: true },
      "JWT required on every endpoint unless AllowAnonymous.",
      "Prod boot throws if the signing key is still the dev key.",
      "SignalR lifts its token from ?access_token= on the WS upgrade.",
    ]},
    foot: "/healthz/live = process up · /healthz/ready = DB reachable · both anonymous",
    notes: "Middleware wired in phase C after warm-up, order commented in-source. JWT: 30s clock skew, Auth:SigningKey mandatory, throws in Production if it equals DevSigningKey const. Fallback authorization policy requires auth on every endpoint. CORS from Cors:AllowedOrigins (only bites a browser client — native MAUI HttpClient sends no Origin). Rate limiter runs after auth so it can read the sub claim; over-limit = immediate 429, reads unlimited. Docker HEALTHCHECK hits /healthz/live deliberately so a slow DB doesn't restart the container. UseStaticFiles before auth so the admin log-viewer page serves publicly.",
  });

  // 8 · FLOW — deploy + rollback (cheapest first)
  T.contentSlide(p, {
    deckNum: N, section: "Deploy + rollback", accent: C.gold, pipe: ZONE,
    title: "A change reaches the box, and unwinds, cheapest first",
    visual: { kind: "flow", nodes: [
      { t: "Deploy", sub: "git pull → up -d --build", color: C.slate },
      { t: "Flip a lever", sub: "edit env line → up -d server", color: C.upInk },
      { t: "Roll back code", sub: "git checkout prev tip → rebuild", color: C.slate },
      { t: "Restore data", sub: "pg_dump backup + pre-change tip", color: C.down },
    ]},
    right: { title: "Three containers, one network", bullets: [
      { t: "postgres + server + caddy, up -d together.", bold: true },
      "Caddy terminates TLS and reverse-proxies to :8080.",
      "Run the migrate one-shot first if the pull added migrations.",
      "Verify: /healthz/ready, /api/version, an order burst hits 429.",
    ]},
    foot: "Config-flag rollback touches no data and needs no rebuild — the standard lever unwind",
    notes: "Deploy from deploy/RUNBOOK.md: git pull && docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build. Rollback cheapest-first: (1) config flag — delete/edit the Bots__… env line + up -d server, no rebuild/no data touch; (2) code — git checkout prev tip + up -d --build server; (3) full/data — restore pg_dump (deploy/backup.sh, cron 02:00 UTC) + checkout pre-change tip + rebuild, required for anything touching DB shape/seed. Dockerfile is two-stage (SDK build / aspnet runtime, non-root appuser uid 1000, curl for healthcheck). migrate one-shot runs dotnet ef from the build stage because the runtime image is EF-free.",
  });

  // 9 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "One .NET 9 process: API, hub, engine, and a fleet of background actors.",
      "Three config tiers merge — the compose env line is the reversible knob.",
      "Deploy and rollback are cheapest-first; a lever unwinds in one restart.",
    ],
    next: "DATA_LAYER — where all of this durably lands: Postgres via Dapper",
    notes: "Hand off to the persistence deck. Verbal bridge: we've seen the process boot, host its actors, and get tuned by env — now let's follow the writes down into Postgres. HOST §1–§4 (process) + OPS §5–§8 (box) both rest on the Dapper data layer.",
  });
};
