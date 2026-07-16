module.exports = function(T, p) {
  const C = T.C; const N = 1;

// 1 · TITLE
T.titleSlide(p, {
  kicker: "Product explainer · 1 of 7 · start here",
  title: "The System Map",
  subtitle: "A live stock-exchange simulation — ~20,000 trading bots, 50 stocks across 70 USD/EUR order books, a real matching engine, and a desktop client, all sharing one market.",
  footer: "ARCHITECTURE.md   ·   read this first",
  notes: "This is deck 1 of 7. It's the map: the projects, one order's journey, the shared seam, and the conservation invariant. Every other deck zooms into one stage of the pipeline shown on the next slide.",
});

// 2 · MAP (you are here)
T.mapSlide(p, {
  deckNum: N, section: "You are here", zone: T.STAGES,
  title: "One order's journey — the spine of the whole product",
  afterTitle: "After this deck you'll understand",
  after: [
    { t: "the four projects and how they depend on each other", bold: true },
    "one order's path from click to conserved fill",
    "why the client can be a thin skin over the server",
    "the CK = 0 conservation guarantee that never breaks",
  ],
  notes: "The pipeline CLIENT→API→ENTRY→EXEC→MATCH→SETTLE→DB is the backbone. Architecture spans the whole strip; each later deck lights just its zone. Bots feed the same book from below.",
});

// 3 · STAT — one market, four parts
T.contentSlide(p, {
  deckNum: N, section: "What it is", accent: C.up, pipe: T.STAGES,
  title: "One living market, made by twenty thousand bots",
  visual: { kind: "stat", cards: [
    { v: "20k", k: "trading bots", d: "autonomous · ~1s decision loop" },
    { v: "70", k: "order books", d: "50 stocks, cross-listed USD + EUR" },
    { v: "1", k: "shared engine", d: "bots + humans hit the same matcher" },
    { v: "0", k: "conservation error", d: "Σ Δ proved zero before each commit" },
  ]},
  right: { title: "The whole idea", bullets: [
    { t: "The bots supply liquidity and a counterparty.", bold: true },
    "So a human order actually fills against a living market.",
    "All authority lives in one server: engine, bots, API, DB.",
    "The client just renders and sends — it never matches.",
  ]},
  notes: "The bots exist so the market is never empty. A person signs up, gets seed cash, and trades into real bot flow. The realism (correlation, fat tails, Fear/Greed) is what makes the tape believable — that's BOT_MECHANICS.",
});

// 4 · FLOW — projects
T.contentSlide(p, {
  deckNum: N, section: "Projects", accent: C.slateLite,
  title: "Server and client meet only through Shared",
  visual: { kind: "flow", nodes: [
    { t: "MAUI Client", sub: "Views + ViewModels · thin skin", color: C.slate },
    { t: "Shared — Models + interfaces", sub: "the hub both compile against", color: C.upInk },
    { t: "Server — engine · 20k bots · API · host", sub: "the authoritative process", color: C.slate },
  ]},
  right: { title: "Four projects", bullets: [
    { t: "Shared is the hub: Models + service interfaces.", bold: true },
    "Client and Server both depend on Shared — never on each other.",
    "Server owns the engine, the fleet, the API, and all Postgres.",
    "Plus a Migration data-tool and Python bot-seeding in Tools/.",
  ]},
  foot: "Detail: DATA_LAYER.md (persistence) · SERVER_HOST_AND_OPS.md (the host process)",
  notes: "Two things named migration: EF schema migrations (Server/Data/Migrations) vs the SQLite→Postgres data-copy tool (KieshStockExchange.Migration). Keep them apart. The client head targets net9.0-windows; Shared targets net9.0.",
});

// 5 · FLOW — one order end to end (the spine)
T.contentSlide(p, {
  deckNum: N, section: "Lifecycle", accent: C.gold, pipe: T.STAGES,
  title: "One order: click to conserved fill, then everyone sees it",
  visual: { kind: "flow", nodes: [
    { t: "Trade page → OrderController", sub: "IOrderEntryService over HTTP", color: C.slate },
    { t: "Entry → Execution", sub: "reserve funds · batched tx", color: C.slate },
    { t: "Matching → Settlement", sub: "cross the book · Σ Δ = 0", color: C.upInk },
    { t: "MarketHub push", sub: "every client re-renders", color: C.slate },
  ]},
  right: { title: "Click → fill → broadcast", bullets: [
    { t: "The ViewModel calls an interface — the HTTP hop is invisible.", bold: true },
    "Four engine hops, each one job, each a clean handoff.",
    "Settlement proves conservation before every commit.",
    "20k bots take the identical path — one shared matcher.",
  ]},
  foot: "The full worked numeric trace lives in ENGINE_MECHANICS.md §1.5",
  notes: "This is the product's spine. The bot loop submits through the same OrderExecutionService via batch routes, so bot flow and human flow converge at one book. SignalR keeps every screen live after the REST call returns.",
});

// 6 · MONO — the shared seam
T.contentSlide(p, {
  deckNum: N, section: "The seam", accent: C.up,
  title: "The client is thin because the contract is shared",
  visual: { kind: "mono", caption: "ONE CONTRACT, TWO IMPLEMENTATIONS", size: 12.5, lines: [
    { t: "Shared/Models/*   (Order, Fund, Position…)", color: "9FE7C6" },
    { t: "   serialized on server ─┐", color: C.monoInk },
    { t: "   deserialized on client┘  same CLR types", color: C.monoInk },
    { t: "", color: C.monoInk },
    { t: "Shared/.../IOrderEntryService", color: "9FE7C6" },
    { t: "   server → OrderEntryService  (real)", color: C.monoInk },
    { t: "   client → ApiOrderEntryClient (proxy)", color: C.monoInk },
  ]},
  right: { title: "Models + interfaces = the wire", bullets: [
    { t: "Models are the wire DTOs — no hand-kept mirror, no drift.", bold: true },
    "They carry invariants: a bad order can't even be constructed.",
    "Interfaces are one contract; real impl vs HTTP-proxy impl.",
    "That's why the in-process client engine was deleted for free.",
  ]},
  notes: "At the Phase-3 client/server split, the old in-process engine was removed without touching a single ViewModel — only the DI registration changed, because both sides program against the same interface.",
});

// 7 · MONO — conservation
T.contentSlide(p, {
  deckNum: N, section: "The invariant", accent: C.down, pipe: ["SETTLE"],
  title: "Trading moves value — it never creates or destroys it",
  visual: { kind: "mono", caption: "CONSERVATION PROBE · PER BATCH", size: 13, lines: [
    { t: "before every commit:", color: C.monoInk },
    { t: "", color: C.monoInk },
    { t: "  Σ ΔTotalBalance == 0", color: "7FE3AD" },
    { t: "  Σ ΔQuantity     == 0", color: "7FE3AD" },
    { t: "", color: C.monoInk },
    { t: "  one violation → soak FAILS", color: "F5A3A9" },
  ]},
  right: { title: "Σ Δ = 0, always", bullets: [
    { t: "Value moves between accounts — the totals stay fixed.", bold: true },
    "Each settled batch is summed post-apply, pre-commit.",
    "“CK = 0” means that error line never appears — the hard gate.",
    "It's why 20k bots trade for days without the books drifting.",
  ]},
  foot: "Engine-side detail: ENGINE_MECHANICS.md §5",
  notes: "Reservations hold value in flight: Fund.ReservedBalance for buys, Position.ReservedQuantity for sells/shorts. Conservation is checked on money (per currency) and shares (per stock). This gets its full ledger reveal in the ENGINE deck.",
});

// 8 · CLOSING
T.closingSlide(p, {
  takeaways: [
    "One shared order book; bots + humans meet at the same matcher.",
    "Four engine hops turn a click into a conserved, broadcast fill.",
    "Σ Δ = 0 is the invariant that makes days of trading trustworthy.",
  ],
  next: "BOT_MECHANICS — who places the orders, and why price moves",
  notes: "Hand off to deck 2. The natural verbal bridge: we've seen the pipeline; now let's meet the 20,000 traders that keep it full.",
});

};
