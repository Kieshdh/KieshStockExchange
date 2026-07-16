// build_02_bot.js — Section 2: BOT_MECHANICS — who places the orders, and why price moves.
// Crown-jewel section: derive price emergence from first principles, then the machinery that shapes it.
module.exports = function (T, p) {
  const { C } = T;
  const N = 2;
  const PIPE = ["ENTRY"];

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 2 of 7 · the crown jewel",
    title: "Why the Price Moves",
    subtitle: "Twenty thousand bots share one book — but independent bets cancel to a flat line. Every realism mechanism exists to break that symmetry and make an emergent price look alive.",
    footer: "BOT_MECHANICS.md   ·   price is engineered, not scripted",
    notes: "This is deck 2 of 7 and the heart of the product. Price is NOT scripted — it emerges from a real order book as ~20k bots submit real limit/market orders through the same matcher a human uses. The doc's §0 is the kernel: with 20k independent draws the imbalance FRACTION goes to zero (law of large numbers), so the tape would be dead. Everything else — inertia, herding, shared sentiment, co-fire, Fear/Greed — exists to correlate those draws so imbalance scales with N instead of √N. Keep that frame; nothing here makes sense without it.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here", zone: PIPE,
    title: "The bots supply every order that enters the book",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "why 20k independent bots would flatline the tape", bold: true },
      "the taker-vs-limit asymmetry that lets price move at all",
      "the 1-second loop, the strategy cohorts, and Fear/Greed",
      "how cross-stock correlation is engineered, not simulated",
    ],
    notes: "This section lives at ENTRY: the bots are the source of order flow that everything downstream (EXEC→MATCH→SETTLE) processes. The bot loop submits through the very same OrderExecutionService a human order uses. Code lives in KieshStockExchange.Server/Services/BackgroundServices/ and its Helpers/.",
  });

  // 3 · FLOW — the LLN flatline (§0)
  T.contentSlide(p, {
    deckNum: N, section: "First principles", accent: C.down,
    title: "Twenty thousand independent bets draw a flat line",
    visual: { kind: "flow", nodes: [
      { t: "20k bots, each a ±1 side bet", sub: "independent buy/sell draws", color: C.slate },
      { t: "Net imbalance ≈ √N", sub: "the bets nearly cancel", color: C.slate },
      { t: "Imbalance fraction ≈ 1/√N → 0", sub: "law of large numbers", color: C.down },
      { t: "A dead, arithmetic tape", sub: "no realistic movement", color: C.slate },
    ]},
    right: { title: "The core problem", bullets: [
      { t: "Independent bets average out — the market flatlines.", bold: true },
      "Tuning one bot's buy-probability changes almost nothing.",
      "Net imbalance grows with √N, not with N.",
      "Price barely moves — unless the draws are correlated.",
    ]},
    foot: "BOT_MECHANICS.md §0 — the kernel every other lever hangs off",
    notes: "LLN: N independent ±1 bets net to order √N, so the imbalance FRACTION is ~1/√N ≈ 0 for N=20k. This is why a per-bot buyProb tilt is nearly inert on realized price — and why 'correlating the draws' is the entire game. Correlate a fraction of bots (shared side) and net imbalance scales with N·(correlated fraction) instead of √N. That single fact is why a hold-time or herding lever moves a PRICE metric.",
  });

  // 4 · STATEMENT — the thesis (the one allowed layout break)
  T.statement(p, {
    text: "Realism here is not simulated. It is engineered correlation.",
    sub: "Correlate the draws and imbalance scales with N, not √N.",
    notes: "The single sentence the whole section turns on. Independence is the enemy; the mechanisms in §2 of the doc (inertia, herding, shared sentiment rings, co-fire, Fear/Greed coupling) are all ways to inject correlation into otherwise-independent decisions. This is the pivot from 'the problem' (previous slide) to 'the machinery' (rest of the deck).",
  });

  // 5 · FLOW — taker vs maker asymmetry (§0 step 2-3)
  T.contentSlide(p, {
    deckNum: N, section: "The mechanism", accent: C.up, pipe: PIPE,
    title: "Only taker flow actually moves the price",
    visual: { kind: "flow", nodes: [
      { t: "Resting limit order", sub: "adds depth at one fixed level", color: C.slate },
      { t: "Opposing book absorbs it", sub: "the mid does not move", color: C.slate },
      { t: "Marketable taker order", sub: "crosses the spread, eats depth", color: C.upInk },
      { t: "Prints through levels → mid moves", sub: "impact ≈ direction × taker-ness × size", color: C.upInk },
    ]},
    right: { title: "Two order types, one asymmetry", bullets: [
      { t: "A resting limit is swallowed by the opposing book.", bold: true },
      "A taker consumes depth, executes deeper — the mid moves.",
      "Shared sentiment lands as limits, so it caps correlation ~0.08.",
      "Every realism lever must become taker flow to matter.",
    ]},
    foot: "Anchors damp the tilt · the book absorbs the limit · only the taker moves the mid",
    notes: "A taker (marketable/market order) crosses the spread and CONSUMES resting depth → moves the mid. A maker (resting limit) ADDS depth and waits → does not move the mid. This asymmetry is the doc's central mechanism. A buyProb tilt that produces mostly resting limits is 'book-absorbed' — it changes queue depth, not price. That's why shared sentiment alone caps cross-stock correlation ~0.08: the tilt lands as limits and gets swallowed. Anchors (value/recent/fundamental) are a SEPARATE upstream damper on the tilt — not what absorbs the limit; the book is.",
  });

  // 6 · FLOW — one signal, three routings (§0 step 4)
  T.contentSlide(p, {
    deckNum: N, section: "The master lever", accent: C.gold,
    title: "One conviction signal, routed three ways",
    visual: { kind: "flow", nodes: [
      { t: "Per-stock momentum taker", sub: "→ random-walk returns (ret_acf → 0)", color: C.slate },
      { t: "Shared taker burst", sub: "→ cross-stock correlation", color: C.slate },
      { t: "Down-skewed global shock", sub: "→ fat left tails, crashes", color: C.down },
    ]},
    right: { title: "Three realism fixes, one idea", bullets: [
      { t: "Feed one directional signal to taker flow three ways.", bold: true },
      "Per-stock momentum makes trends stick within a stock.",
      "A synchronized burst couples separate stocks together.",
      "A rare global shock delivers correlated elevator-down crashes.",
    ]},
    notes: "§0 step 4: feed a directional signal to taker flow three ways and get three realism fixes from one mechanism. Per-stock momentum → ret_acf toward 0 (§2.2/2.4). A SHARED taker burst → cross-stock correlation (§2.7 co-fire, §2.10 mood coupling). A down-skewed GLOBAL taker shock → fat left tails (§2.7 shock/jumps). Targets: ret_acf −0.1 (structural ceiling ~−0.5), pairwise corr aspirational ≥0.2 (bot-lever ceiling ~0.13 factorR²), kurtosis ≥4. The gold accent marks this as the through-line lever, not a warning.",
  });

  // 7 · FLOW — the tick loop (§3)
  T.contentSlide(p, {
    deckNum: N, section: "The loop", accent: C.gold, pipe: PIPE,
    title: "One loop ticks the whole market each second",
    visual: { kind: "flow", nodes: [
      { t: "Advance the world", sub: "sentiment · regime · news · mood", color: C.slate },
      { t: "Collect decisions", sub: "a staggered slice of the fleet", color: C.slate },
      { t: "Batch submit → match → settle", sub: "one grouped transaction", color: C.upInk },
      { t: "Fills feed back", sub: "caches · mood · load scaler", color: C.slate },
    ]},
    right: { title: "A single-threaded heartbeat", bullets: [
      { t: "One thread, no locks — the phase order is load-bearing.", bold: true },
      "World advances first, then bots read it, then orders leave.",
      "Staggering: each tick sees only ~1/N of the fleet.",
      "A load scaler tunes how many bots the box can carry.",
    ]},
    foot: "AiTradeService.RunLoopAsync — 14 phases per tick, then await Task.Delay",
    notes: "The whole bot market is one single-threaded loop (AiTradeService.RunLoopAsync). All state — context, accounts cache, sentiment/mood/activity/fundamental services — is mutated only here with NO locks; single-threaded is a hard invariant. Per tick, 14 phases in strict order: CheckTimers (advance external state before any bot reads it) → Collect (gate each bot: enabled, burst, quiet, activity, stagger, interval, trade-prob draw, decide) → batch submit/match/settle → advanced route → the cap-exempt cohorts (arb/MM/rotator/conviction/jumps) → RecordTickLatency → scaler → reconcile → maintenance. TradeIntervalMs default 0 ⇒ 1s (prod may run ~250ms). BotScalerService moves ActiveBotCap toward a 0.60 target load so the box self-tunes online-bot count.",
  });

  // 8 · FLOW — one bot's decision (§4)
  T.contentSlide(p, {
    deckNum: N, section: "One bot", accent: C.up, pipe: PIPE,
    title: "Each bot turns mood into a single order",
    visual: { kind: "flow", nodes: [
      { t: "buyProb = base + directional + anchors", sub: "homeostasis · momentum · sentiment", color: C.slate },
      { t: "Pick stock, size, limit tier", sub: "fat-tail size draw · Close/Mid/Far", color: C.slate },
      { t: "Taker or limit?", sub: "conviction crosses the spread", color: C.slate },
      { t: "Price-band veto", sub: "no order crosses the cap", color: C.down },
    ]},
    right: { title: "From signal to a real order", bullets: [
      { t: "Direction is a probability summed from independent tilts.", bold: true },
      "Anchors pull toward fair value; caps bound runaway.",
      "Aggression turns conviction into spread-crossing takers.",
      "Every lever off collapses to the original additive line.",
    ]},
    notes: "AiBotDecisionService.ComputeOrderAsync: given (ctx, user, currency) produce one Order or null. Order of resolution: co-fire branch first (above any RNG), then chaser branch (draw-free substitution into a news shock), then ChooseOrderType builds buyProb = homeostatic base (BuyBias + cash-reserve shift) + directional (SentimentDynamics slope model or legacy momentum+sentiment, + herding tilt) + anchors (value/recent/fundamental). Then ChooseStockId, size (power-law + rare block), limit tier × DecisionDistanceMult × composition seam, and the hard IsOverBand price-band veto. effectiveUseMarket decides taker vs limit separately: base UseMarketProb × MarketProbMult + AggressionBoost×|directional| + reflexive mood coupling. Every v2 lever off ⇒ byte-identical to the original additive line. Bots read the SMOOTHED price so they don't counter-trade their own 1-min impact.",
  });

  // 9 · STAT — strategy cohorts (§5)
  T.contentSlide(p, {
    deckNum: N, section: "The population", accent: C.slateLite, pipe: PIPE,
    title: "Nine strategies: amplifiers, dampers, and houses",
    visual: { kind: "stat", cards: [
      { v: "~½", k: "momentum amplifiers", d: "trend + scalper chase the move" },
      { v: "5", k: "in-fleet strategies", d: "MM · trend · reversion · random · scalper" },
      { v: "4", k: "dedicated cohorts", d: "arbitrage · MM-house · rotator · conviction" },
      { v: "0", k: "RNG in cohorts", d: "pure hashes · sells-before-buys · CK-safe" },
    ]},
    right: { title: "A diverse population by design", bullets: [
      { t: "Amplifiers chase trends; dampers fade them.", bold: true },
      "Houses supply liquidity, arbitrage, and the panic bid.",
      "Cohorts run outside the loop's load-scaled span.",
      "Every cohort routes through the same conserved engine.",
    ]},
    foot: "Strategy is seeded per bot (Tools/Config.py STRATEGY_WEIGHTS) — cohorts 5-8 never come from the general draw",
    notes: "enum AiStrategy { MarketMaker=0, TrendFollower=1, MeanReversion=2, Random=3, Scalper=4, Arbitrage=5, MarketMakerHouse=6, Rotator=7, Conviction=8 }. In-fleet (normal path): MM posts two-sided quotes; TrendFollower amplifies momentum; MeanReversion damps; Random is homeostatic noise; Scalper is fast-slope conviction + extra taker. Dedicated RunAsync passes (phases 5-8, master-gated): Arbitrage couples USD/EUR books and funds the house; MarketMakerHouse quotes continuously and widens in fear; Rotator chases the price-vs-bank-estimate gap (turnover-bounded ~100%-win rebalancer); Conviction = realistic discretionary risk-takers (win-rate ≪100%) that add the panic fear-bid. All cohorts iterate ascending AiUserId, consume no RNG, run two batch passes (SELLS-before-BUYS) ⇒ Σ buys ≤ available cash ⇒ CK-safe.",
  });

  // 10 · MONO — Fear/Greed gauge (§2.10)
  T.contentSlide(p, {
    deckNum: N, section: "The regime", accent: C.up,
    title: "Fear and Greed become a bounded taker lever",
    visual: { kind: "mono", caption: "COMPOSITE MOODSCORE · 0-100 GAUGE", size: 12.5, lines: [
      { t: "mood = 50 + 50·tanh(", color: C.monoInk },
      { t: "    WMom·momZ + WBreadth·(2b−1)", color: "9FE7C6" },
      { t: "  − WVol·volZ + WFlow·flowZ + WSent·s )", color: "9FE7C6" },
      { t: "", color: C.monoInk },
      { t: "greed/fear → taker INTENSITY, not direction", color: C.gold },
      { t: "lagged 5-min · bounded · latch = 0", color: C.monoInk },
    ]},
    right: { title: "One emotional axis, fed back", bullets: [
      { t: "Global mood is a 0-100 gauge shown on every chart.", bold: true },
      "Lagged mood scales taker share — intensity, never direction.",
      "In fear, market-makers widen; conviction bots buy the panic.",
      "Bounded and lagged, so it can never latch into a spiral.",
    ]},
    foot: "MarketMoodService — sentiment = slow direction, activity = clustering, F&G = regime intensity + correlation",
    notes: "§2.10 MarketMoodService. Per stock mood = 50 + 50·tanh(WMom·momZ + WBreadth·(2b−1) − WVol·volZ + WFlow·flowZ + WSent·sentiment); momZ = trend-vs-anchor ln(price/EMA) ÷ POOLED cross-sectional σ (pooled not own-σ — in a mean-reverting sim own-σ would misread a rising stock as fear). Global mood = cross-stock mean, at three horizon bands (Fast/Mid/Slow). Reflexive taker coupling: lagged 5-min global mood scales each bot's taker share (asymmetric-V — greed AND fear both raise activity), bounded by a cap and JointTakerCapMult, with a kill-switch. MMWiden = in fear the MM cohort widens spread and shrinks size (elevator-down amplifier); Conviction fear-bid = smart money buys panic (the absorber that keeps fear controlled). latch=0 (mood not pegged <30/>70) is the gate that proves the absorber holds. Base appsettings ships every flag false (byte-identical); prod enables via docker env. LIVE + healthy on prod.",
  });

  // 11 · FLOW — co-fire correlation (§2.7)
  T.contentSlide(p, {
    deckNum: N, section: "Correlation", accent: C.gold, pipe: PIPE,
    title: "One pulse fires the same trade everywhere",
    visual: { kind: "flow", nodes: [
      { t: "A market-wide pulse ticks", sub: "a shared clock, all stocks", color: C.slate },
      { t: "Co-fire cohort selected", sub: "hash-spread across the board", color: C.slate },
      { t: "One same-sign taker, same tick", sub: "synchronized directional burst", color: C.upInk },
      { t: "Cross-stock correlation ~doubles", sub: "+0.037 factorR² at 10 min", color: C.upInk },
    ]},
    right: { title: "Shared flow, not shared sentiment", bullets: [
      { t: "Shared sentiment is book-absorbed; shared flow is not.", bold: true },
      "A synchronized taker burst couples the whole market.",
      "Validated live: ~doubles 5-10 min correlation on prod.",
      "The cost is real — crashes get deeper and correlated.",
    ]},
    foot: "ExogShock GlobalCoFire — council-approved, live on prod, reversible env override",
    notes: "§2.7 co-fire = the banked correlation lever. On a market-wide pulse, a cohort fires ONE same-sign marketable order same-tick across hash-spread stocks (GlobalCoFireNotionalFrac caps notional; resolved FIRST in ComputeOrderAsync, above any RNG, so off is byte-identical). Why it works where shared sentiment failed: sentiment lands as resting limits and is book-absorbed (caps corr ~0.08), but a SIMULTANEOUS shared taker burst prints through levels. Validated: +0.037 factorR² at 10 min across two clean 2h A/Bs, ~doubling 5-10min cross-stock correlation. Cost = deeper CORRELATED crashes (the arc's intended character trade-off). Live on prod since 2026-07-16 at GlobalCoFireNotionalFrac 0.10 / GlobalCoFireFraction 0.15, council-approved and reversible. Cross-stock corr is judged on prod over days, not a 45m soak.",
  });

  // 12 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "Independent bots flatline; realism is engineered correlation.",
      "Only taker flow moves the mid — so every lever becomes taker flow.",
      "One 1-second loop, nine strategies, a Fear/Greed regime — all CK-safe.",
    ],
    next: "ENGINE_MECHANICS — how one order matches and settles",
    notes: "Hand off to deck 3. Verbal bridge: we've seen WHO places the orders and WHY the emergent price moves; now follow one of those orders into the engine that matches and settles it, and see the conservation invariant proved. CK=0 always: every bot and cohort routes through the same reserve→match→settle engine — that's the seam into the ENGINE deck.",
  });
};
