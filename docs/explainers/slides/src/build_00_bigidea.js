// build_00_bigidea.js — deck 0: the trailer. A promise, not a summary.
// Distilled from docs/explainers/ARCHITECTURE.md (the big idea: price EMERGES).
const { C } = require("./kse_theme.js");
const N = 0;

module.exports = function (T, p) {
  // 1 · TITLE — the claim
  T.titleSlide(p, {
    kicker: "KieshStockExchange · the product in 7 explainers",
    title: "Price is not scripted. It emerges.",
    subtitle: "A live stock-exchange simulation where ~20,000 trading bots make the market — and every price is the output of a real matching engine, not a line of code.",
    footer: "Deck 0 — the trailer   ·   start with ARCHITECTURE.md",
    notes: "This is the opener for the whole set. The single idea: nobody writes the price. A fleet of bots trades continuously across a real matcher, and the tape is whatever those orders produce when they cross. A human logs in and trades into that same living book. The next four slides earn that claim, then hand off to the seven explainers.",
  });

  // 2 · STAT — the numbers that earn respect
  T.contentSlide(p, {
    deckNum: N, section: "The claim, in numbers", accent: C.up, pipe: T.STAGES,
    title: "A real market, not a price animation",
    visual: { kind: "stat", cards: [
      { v: "20k", k: "trading bots", d: "5 strategy cohorts · ~1s decision loop" },
      { v: "70", k: "order books", d: "50 stocks, cross-listed USD + EUR" },
      { v: "1", k: "matching engine", d: "bots + humans cross the same book" },
      { v: "0", k: "conservation error", d: "Σ Δ = 0 proved before every commit" },
    ]},
    right: { title: "Why this is hard to fake", bullets: [
      { t: "Five cohorts supply liquidity and a real counterparty.", bold: true },
      "50 stocks span 70 USD/EUR books — orders, quotes, candles per book.",
      "One matcher: a human order fills against living bot flow.",
      "Every settled batch is proven to conserve money and shares.",
    ]},
    notes: "The cohorts are MarketMaker / TrendFollower / MeanReversion / Scalper / Random, plus small Arbitrage + house cohorts — each sets the market's loop gain. 70 books = 20 stocks dual-listed USD+EUR, 15 USD-only, 15 EUR-only. CK=0 is the HARD invariant: per currency Σ ΔTotalBalance = 0, per stock Σ ΔQuantity = 0, checked live by ConservationProbe before every settle write. One non-zero hit fails a soak. These numbers exist to say: this is a running system, not a chart that plays back.",
  });

  // 3 · MAP — the journey the whole set traces
  T.mapSlide(p, {
    deckNum: N, section: "The journey", zone: T.STAGES,
    title: "Every deck follows one order down this spine",
    afterTitle: "The seven explainers, laid on one pipeline",
    after: [
      { t: "Click to conserved fill: CLIENT → API → ENTRY → EXEC → MATCH → SETTLE → DB", bold: true },
      "Bots feed the same book from below — one shared matcher.",
      "Each explainer lights just its zone of this strip.",
      "Follow the tape from who places an order to where value lands.",
    ],
    notes: "The pipeline CLIENT→API→ENTRY→EXEC→MATCH→SETTLE→DB is the backbone of the entire doc set and slide set. Deck 1 (Architecture) spans the whole strip; each later deck zooms into its stage. This is the 'you are here' motif repeated across every section, so a viewer always knows where the current story sits in an order's life.",
  });

  // 4 · MENU — the seven explainers
  T.contentSlide(p, {
    deckNum: N, section: "The set", accent: C.gold, pipe: T.STAGES,
    title: "Seven explainers, each a zoom into one stage",
    visual: { kind: "stat", cards: [
      { v: "1", k: "Architecture", d: "the system map — start here" },
      { v: "2", k: "Bot Mechanics", d: "who places orders, and why price moves" },
      { v: "3", k: "Engine Mechanics", d: "entry → match → settle · CK = 0" },
      { v: "4", k: "Data · API · Host · Client", d: "storage, wire, ops, and the desktop" },
    ]},
    right: { title: "A guided reading order", bullets: [
      { t: "Architecture is the map — always read it first.", bold: true },
      "Bots explains where order flow comes from and how price is shaped.",
      "Engine explains matching, settlement, and money/share safety.",
      "Data, API, Host and Client cover storage, protocol, and the app.",
    ]},
    foot: "Read top-to-bottom for onboarding; jump by topic once oriented.",
    notes: "The full set is seven core docs, all live: 1 ARCHITECTURE (map), 2 BOT_MECHANICS (the ~20k-bot loop, sentiment/mood/Fear&Greed signals, realism scorecard), 3 ENGINE_MECHANICS (Entry→Execution→Matching→Settlement + the CK=0 proof), 4 DATA_LAYER (EF schema vs runtime Dapper, in-memory caches, retention), 5 API_REFERENCE (REST controllers + SignalR MarketHub contract), 6 SERVER_HOST_AND_OPS (Program.cs composition, hosted loops, Docker/prod), 7 CLIENT_STRUCTURE (the MAUI head: DI, Shell nav, MVVM, hub client). The four cards compress the tail four into one so the menu stays at four tiles; verbally name all seven.",
  });

  // 5 · STATEMENT — the mic drop
  T.statement(p, {
    text: "No line of code sets the price.",
    sub: "It is whatever the order book prints when 20,000 traders cross. →  Deck 1: Architecture",
    notes: "The closing beat of the trailer and the whole thesis. There is no price variable being assigned anywhere — the number on the tape is purely the output of the matching engine as orders cross, at the maker's resting price by price-time priority. That taker-consumes-maker asymmetry is the only reason the mid moves. Hand off to deck 1, Architecture: now let's see the machine that makes it true.",
  });
};
