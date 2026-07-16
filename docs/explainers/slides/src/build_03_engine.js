module.exports = function (T, p) {
  const { C } = T;
  const N = 3;

  // 1 · TITLE
  T.titleSlide(p, {
    kicker: "Product explainer · 3 of 7 · the engine",
    title: "Once an Order Is Placed",
    subtitle: "The path from a click or a bot decision to a durable, conserved fill — validate, reserve, cross the book, settle, and prove that value never appeared or vanished.",
    footer: "ENGINE_MECHANICS.md   ·   the four layers, one conserved unit",
    color: C.up,
    notes: "Deck 3 of 7. Where deck 1 mapped the whole product and deck 2 covered WHO places orders and why, this deck covers WHAT happens to one order after it's placed. The spine of the section is a single worked limit-buy trace (ENGINE_MECHANICS §1.5): reserve 253.00, print at the maker's 25.25, release the 0.50 over-reservation, prove Σ Δ = 0. Everything else is machinery around that. Engine code lives under KieshStockExchange.Server/Services/MarketEngineServices/, matcher+settlement helpers in its Helpers/ and Settlement/ subfolders; models (Order/Fund/Position) in KieshStockExchange.Shared/Models/.",
  });

  // 2 · MAP (you are here)
  T.mapSlide(p, {
    deckNum: N, section: "You are here",
    zone: ["ENTRY", "EXEC", "MATCH", "SETTLE"],
    title: "This deck owns the four engine hops",
    afterTitle: "After this deck you'll understand",
    after: [
      { t: "the four layers each order passes through", bold: true },
      "how a fill prints at the resting maker's price",
      "the reserve → consume → prove conservation cycle",
      "why 20k bots fit in one ~1-second tick",
    ],
    notes: "The lit zone is ENTRY→EXEC→MATCH→SETTLE — the middle of the pipeline. CLIENT/API (deck 1's split) feed it; DB (deck's data layer) is where settlement lands. Order Entry = the single public write-surface; Execution = sequencing + transaction boundaries; Matching = the in-memory cross; Settlement = where money and shares actually move and where CK=0 is proven.",
  });

  // 3 · FLOW — the four layers (the spine)
  T.contentSlide(p, {
    deckNum: N, section: "The flow", accent: C.gold,
    pipe: ["ENTRY", "EXEC", "MATCH", "SETTLE"],
    title: "One order, four layers, one job each",
    visual: { kind: "flow", nodes: [
      { t: "Order Entry", sub: "validate · build · gate · delegate", color: C.slate },
      { t: "Execution", sub: "sequence + transaction boundaries", color: C.slate },
      { t: "Matching", sub: "cross the book, in memory only", color: C.slate },
      { t: "Settlement", sub: "move value · persist · prove Σ Δ = 0", color: C.upInk },
    ]},
    right: { title: "Each hands a clean artifact on", bullets: [
      { t: "Entry decides if an order is well-formed and permitted.", bold: true },
      "Execution decides what runs inside which commit.",
      "Matching produces fills but never writes the DB.",
      "Settlement is where money and shares truly move.",
    ]},
    foot: "Every human click and every bot decision travels this same four-hop pipeline",
    notes: "OrderEntryService → OrderExecutionService → MatchingEngine → SettlementEngine. Entry never touches the book and never reserves. Execution does not match and does not move money — it sequences the two that do, and owns the tx boundaries and lock order. The matcher is a pure in-memory function over one (stockId, currency) book. Settlement brackets matching on both sides: its reserve half (OrderSettler) runs pre-match, its consume half (TradeSettler) runs post-match.",
  });

  // 4 · ENTRY — the front door
  T.contentSlide(p, {
    deckNum: N, section: "Order Entry", accent: C.up, pipe: ["ENTRY"],
    title: "Entry is the thin, permission-checked front door",
    visual: { kind: "flow", nodes: [
      { t: "Shape-check the raw inputs", sub: "userId, stock listed in currency, qty", color: C.slate },
      { t: "Build a valid Order", sub: "3 enum dims · invariants in the model", color: C.upInk },
      { t: "Gate ownership on edits", sub: "'Order not found' hides others' orders", color: C.slate },
      { t: "Delegate to the engine", sub: "no book, no reserve here", color: C.slate },
    ]},
    right: { title: "Well-formed and permitted", bullets: [
      { t: "Order type = 3 orthogonal enums, not a flat string.", bold: true },
      "A malformed order can't even be constructed silently.",
      "Two-stage validation: loose params, then the built order.",
      "Same validated Order flows per-order and batched.",
    ]},
    foot: "OrderEntryService · IOrderValidator · Shared/Models/Order.cs",
    notes: "Order.Side / Order.Entry / Order.Stop are authoritative; the legacy string vocabulary (LimitBuy, TrueMarketSell) survives only as a read-only OrderType projection for logs. Immutable identity fields (OrderId/UserId/StockId) throw if reassigned; setter invariants reject negative price, out-of-range slippage. ValidateInput checks raw params (no BuyBudget ceiling — the real limit is enforced downstream by the AvailableBalance check), ValidateNew re-checks the constructed object. Ownership is gated here because the engine cancels by orderId and can't tell whose order it is; VerifyOwnershipAsync returns a uniform 'Order not found.' so it never reveals someone else's order exists.",
  });

  // 5 · EXEC — the sacrosanct lock order
  T.contentSlide(p, {
    deckNum: N, section: "Execution", accent: C.slateLite, pipe: ["EXEC"],
    title: "Every path takes locks in one fixed order",
    visual: { kind: "flow", nodes: [
      { t: "Book lock — outermost", sub: "per (stockId, currency); held across settle", color: C.slate },
      { t: "Per-user gates — inner", sub: "funds before positions, always sorted", color: C.slateLite },
      { t: "DB transaction — innermost", sub: "opened only after the match is done", color: C.slate },
    ]},
    right: { title: "Deadlock-free by construction", bullets: [
      { t: "Nothing may reorder these three — it is sacrosanct.", bold: true },
      "Book lock spans match AND settle so rollback is safe.",
      "Sorted gates mean two groups never AB/BA-deadlock.",
      "Execution sequences; it never matches or moves money.",
    ]},
    foot: "OrderExecutionService · book → per-user gates → DB tx",
    notes: "§3.1. The book lock is held across match and settle and never released between: MatchingEngine.Match mutates the book in memory, and RollbackMatch on a settlement failure assumes the book hasn't moved since. Per-user gates guard each user's shared Fund/Position so two parallel groups settling the same user can't interleave a non-atomic balance write (the historical P2 money-conservation race). Keys sorted inside AcquireUserGatesAsync (funds before positions). Single-order path commits two txs per order (reserve+insert, then match+settle) — fine for humans, ruinous at 20k-bot rate, which is the entire reason the batch path exists.",
  });

  // 6 · MATCH — prints at the maker's price
  T.contentSlide(p, {
    deckNum: N, section: "Matching", accent: C.up, pipe: ["MATCH"],
    title: "Every fill prints at the resting maker's price",
    visual: { kind: "mono", caption: "ORDER BOOK · (stockId, USD)", size: 12.5, lines: [
      { t: "ASK  25.27   x40", color: "F5A3A9" },
      { t: "ASK  25.25   x10   <- best (maker)", color: "F5A3A9" },
      { t: "------------------------------", color: C.monoInk },
      { t: "BID  25.20   x60", color: "7FE3AD" },
      { t: "", color: C.monoInk },
      { t: "taker: BUY 10 @ 25.30", color: C.gold },
      { t: "  25.25 <= 25.30  -> crosses", color: C.monoInk },
      { t: "  Transaction(qty=10, price=25.25)", color: "9FE7C6" },
    ]},
    right: { title: "Price-time priority", bullets: [
      { t: "The resting order sets the price; the aggressor pays the touch.", bold: true },
      "The matcher walks the book, best price first.",
      "Only open limit orders ever rest in the book.",
      "It emits fills in memory — it never writes the DB.",
    ]},
    foot: "OrderBook · MatchingEngine.Match · one Transaction per maker consumed",
    notes: "The book is two price-ordered maps (buy inverted-comparer highest-bid-first, sell ascending lowest-ask-first), each price level a LinkedList time-queue = price-time priority. The matcher is handed a taker + a book and walks the opposite side; taker.UserId excludes self-orders (no self-trades). A taker bigger than the touch sweeps multiple levels, printing one Transaction per maker at progressively worse prices. Trade price = maker.Price. A crossing limit acts as a taker for its marketable portion then rests its remainder. MidReference (Bots:BounceReference, shipped 'mid') stamps a bounce-free reference for the candle close.",
  });

  // 7 · HERO 1 — RESERVE
  T.contentSlide(p, {
    deckNum: N, section: "Worked trace · 1 of 3", accent: C.gold, pipe: ["SETTLE"],
    title: "Reserve fences the cash before any match",
    visual: { kind: "mono", caption: "RESERVE · SETTLEMENT PRE-MATCH", size: 13, lines: [
      { t: "limit buy  10 @ 25.30", color: C.gold },
      { t: "", color: C.monoInk },
      { t: "reserve = RoundMoney(25.30 x 10)", color: C.monoInk },
      { t: "        = 253.00", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "Fund.ReservedBalance += 253.00", color: "9FE7C6" },
      { t: "Fund.TotalBalance      unchanged", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "order.CurrentBuyReservation = 253.00", color: C.monoInk },
    ]},
    right: { title: "Held from placement", bullets: [
      { t: "Cash moves available → reserved; the total never changes.", bold: true },
      "An unfilled order's cash can't be double-spent.",
      "Per-order field and cache aggregate stay in lock-step.",
      "The order row is inserted and gets its real OrderId.",
    ]},
    foot: "OrderSettler.SettleAsync · Fund.ReserveFunds · reserve at place, consume at fill",
    notes: "Step 2 of the §1.5 trace. Settlement's OrderSettler reserves against the DB-backed accounts cache and inserts the row, invoked by OrderExecutionService before the match. RoundMoney(25.30×10)=253.00. Fund.ReservedBalance += 253.00 with TotalBalance unchanged, mirrored onto the order via TakeBuyReservation. The lock-step invariant: Order.CurrentBuyReservation (source of truth) and Fund.ReservedBalance (aggregate) are mutated in the same critical section, always equal. Subsequent same-account orders immediately see the reduced AvailableBalance.",
  });

  // 8 · HERO 2 — PRINT + CONSUME
  T.contentSlide(p, {
    deckNum: N, section: "Worked trace · 2 of 3", accent: C.gold, pipe: ["MATCH", "SETTLE"],
    title: "It fills at 25.25 and releases the 0.50 surplus",
    visual: { kind: "mono", caption: "MATCH + CONSUME", size: 12.5, lines: [
      { t: "prints at the MAKER's price -> 25.25", color: C.gold },
      { t: "notional = RoundMoney(10 x 25.25) = 252.50", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "ConsumeReservedFunds(252.50)", color: "F5A3A9" },
      { t: "  Reserved and Total drop together", color: C.monoInk },
      { t: "", color: C.monoInk },
      { t: "over-reserved by (25.30-25.25)x10 = 0.50", color: C.monoInk },
      { t: "UnreserveFunds(0.50)  -> back to available", color: "9FE7C6" },
      { t: "buyer Qty += 10   seller Total += 252.50", color: C.monoInk },
    ]},
    right: { title: "Consume, then refund the savings", bullets: [
      { t: "The buyer pays from its OWN reservation, not blindly.", bold: true },
      "It reserved at 25.30 but fills at 25.25.",
      "The 0.50 savings is unreserved back to available.",
      "Finer price decimals never leak cash out of the hold.",
    ]},
    foot: "TradeSettler.SettleNoTxAsync · savings = (perUnitReserved − price) × qty",
    notes: "Steps 3–4 of the trace. The matcher crosses (25.25 ≤ 25.30) and prints at the maker's price 25.25. notional = RoundMoney(10×25.25) = 252.50. The buyer pays from its own reservation: reservedPortion = min(notional, CurrentBuyReservation) via ConsumeReservedFunds, which drops Reserved AND Total together. The clamp to the order's own reservation is deliberate — a buyer also holding short collateral keeps it in the same ReservedBalance, so a blind consume would eat that collateral. Over-reservation savings = (25.30−25.25)×10 = 0.50, UnreserveFunds'd back to available. Seller: TotalBalance += 252.50 (straight to total, no reservation on the credit side), Quantity −= 10.",
  });

  // 9 · HERO 3 — PROVE
  T.contentSlide(p, {
    deckNum: N, section: "Worked trace · 3 of 3", accent: C.down, pipe: ["SETTLE"],
    title: "The probe proves the batch conserved value",
    visual: { kind: "mono", caption: "CONSERVATION PROBE · BEFORE COMMIT", size: 13.5, lines: [
      { t: "buyer  ΔTotal = -252.50", color: "F5A3A9" },
      { t: "seller ΔTotal = +252.50", color: "7FE3AD" },
      { t: "               ---------", color: C.monoInk },
      { t: "          Σ  =   0.00", color: "F5B942" },
      { t: "", color: C.monoInk },
      { t: "buyer  ΔQty = +10", color: "7FE3AD" },
      { t: "seller ΔQty = -10", color: "F5A3A9" },
      { t: "          Σ  =   0", color: "F5B942" },
      { t: "", color: C.monoInk },
      { t: "commit.", color: C.gold },
    ]},
    right: { title: "Σ Δ = 0, or the soak fails", bullets: [
      { t: "Money sums to zero per currency; shares per stock.", bold: true },
      "Checked after the apply-pass, before the DB write.",
      "A single non-zero net logs an error and trips the gate.",
      "Only then does the transaction commit.",
    ]},
    foot: "ConservationProbe.Check · the batch created or destroyed neither money nor shares",
    notes: "Step 5 of the trace. ConservationProbe sums per-currency ΔTotalBalance and per-stock ΔQuantity against pre-batch snapshots: buyer ΔTotal −252.50 + seller +252.50 = 0; buyer ΔQty +10 + seller −10 = 0. Modulo a sub-cent tolerance (CurrencyHelper.IsEffectivelyZero). Any non-zero net is a LogError naming the currency, the net, and the first trade — the historical 'ConservationProbe negative-delta' signature. Soaks grep for it; a single hit fails the gate. 'CK = 0' means that error line never appears. Then commit.",
  });

  // 10 · STATEMENT — the crescendo
  T.statement(p, {
    text: "Value moved between two accounts. The market's totals never budged.",
    sub: "Σ Δ = 0 — proven before every commit, under 20k-bot load, for days.",
    notes: "The emotional peak of the section, right after the trace resolves to zero. This is the engine's one HARD invariant (BOT_MECHANICS §1 scorecard). It's why the books never drift no matter how long the fleet trades. The next slide generalizes: two guards enforce it — the cross-row probe and the per-row Postgres CHECK constraints.",
  });

  // 11 · CK machinery — two guards
  T.contentSlide(p, {
    deckNum: N, section: "Conservation", accent: C.down, pipe: ["SETTLE", "DB"],
    title: "Two guards, one invariant that never breaks",
    visual: { kind: "mono", caption: "CROSS-ROW PROBE + PER-ROW CHECK", size: 12.5, lines: [
      { t: "cross-row:  ConservationProbe", color: "9FE7C6" },
      { t: "  Σ ΔTotal == 0   per currency", color: C.monoInk },
      { t: "  Σ ΔQty   == 0   per stock", color: C.monoInk },
      { t: "  non-zero -> LogError, soak fails", color: "F5A3A9" },
      { t: "", color: C.monoInk },
      { t: "per-row:  Postgres CHECK", color: "9FE7C6" },
      { t: "  Total >= 0 · Reserved <= Total", color: C.monoInk },
      { t: "  a row can't commit illegal", color: "F5A3A9" },
    ]},
    right: { title: "Reports, and enforces", bullets: [
      { t: "Conservation is guaranteed by construction — the probe reports it.", bold: true },
      "A committed non-zero net means a logic bug shipped.",
      "The DB CHECK constraints are the hard per-row backstop.",
      "A row can be legal yet the batch still leak — hence both.",
    ]},
    foot: "ConservationProbe (cross-row) · CK_Funds / CK_Positions CHECK constraints (per-row)",
    notes: "§5.4. The probe REPORTS, it does not enforce — conservation is guaranteed by construction (the reserve model plus coupled buyer/seller deltas mean a non-zero net is an impossible-by-design logic bug, not a recoverable data condition). Committing-with-a-loud-error keeps the offending batch on disk for post-mortem and trips the gate, rather than hiding the root cause in a silent rollback. The DB backstop: CK_Funds_Balance_Invariants (Total≥0 ∧ Reserved≥0 ∧ Reserved≤Total) and CK_Positions_Quantity_Invariants are Postgres CHECKs enforcing per-row legality; the probe enforces cross-row conservation. Distinct: a per-row-legal batch can still leak. The Q7 pre-write scan DOES reject a legal-but-CHECK-violating row via the snapshot-rollback path.",
  });

  // 12 · BATCH — commit-bound throughput
  T.contentSlide(p, {
    deckNum: N, section: "Throughput", accent: C.slateLite, pipe: ["EXEC"],
    title: "Batching fits a 20k-bot tick under one second",
    visual: { kind: "stat", cards: [
      { v: "1", k: "insert commit", d: "the whole tick, not one per order" },
      { v: "N → 1", k: "settle commits", d: "one per book group, run in parallel" },
      { v: "20k", k: "bots per tick", d: "one PlaceAndMatchBatchAsync call" },
    ]},
    right: { title: "The engine is commit-bound", bullets: [
      { t: "The cost is the fsync on commit, not the CPU.", bold: true },
      "Reserve and match in memory against the cache.",
      "Touch the disk in as few, as-parallel commits as safe.",
      "Bots take the identical path — one shared matcher.",
    ]},
    foot: "PlaceAndMatchBatchAsync · reserve-in-cache → bulk-insert → per-group settle",
    notes: "§3.3/§3.6. Round-2 perf profiling found steady-state cost is the commit, not the matcher/book ops. Single-order path = one commit per order = thousands of fsyncs/sec at 20k-bot rate, which the disk can't sustain. Batch collapses this: Phase 2 = one InsertAllAsync commit for the whole tick's orders (matcher needs real OrderIds first); Phase 3 = one settle tx per (stockId, currency) group, run in parallel under _groupGate (Db:MaxConcurrentGroups, default 24), Postgres MVCC lets disjoint writers commit independently. Cross-group atomicity is intentionally sacrificed — each group is already atomic and the caller reads OrderResult per order. Optional group-commit coalesces a currency's groups into one fsync (§6.3, default off).",
  });

  // 13 · CROSS-CUTTING — atomic writes + the house
  T.contentSlide(p, {
    deckNum: N, section: "The machinery under it", accent: C.gold, pipe: ["SETTLE", "DB"],
    title: "Multi-table writes commit atomically or not at all",
    visual: { kind: "flow", nodes: [
      { t: "RunInTransactionAsync", sub: "up to 5 tables, one atomic commit", color: C.slate },
      { t: "AsyncLocal ambient tx", sub: "deep Dapper calls auto-enroll", color: C.upInk },
      { t: "Nested savepoints", sub: "one group rolls back, others commit", color: C.slate },
    ]},
    right: { title: "Atomicity as a scope, not a signature", bullets: [
      { t: "One bot trade can mutate five tables — they land together.", bold: true },
      "No transaction parameter threaded through any method.",
      "Savepoints give per-group rollback inside one root tx.",
      "The FX spread on conversions funds a pure-profit house.",
    ]},
    foot: "PgDBService · Postgres via hand-written Dapper · FxRateService bounded AR(1) walk",
    notes: "§6. RunInTransactionAsync wraps every multi-table settlement (Orders, Transactions, Positions, Funds, FundTransactions). The plumbing is an AsyncLocal<TxScope> ambient: any code inside the delegate, however deep, enrolls in the same physical connection+transaction with no tx param threaded through the ~40 hand-written Dapper methods. BeginTransactionAsync is re-entrant — when ambient is already set it issues SAVEPOINT and returns a non-root handle (RELEASE / ROLLBACK TO SAVEPOINT), so an inner group unwinds without aborting the outer tx; only the root does a real COMMIT = one fsync. FX: FxRateService is a mean-reverting bounded AR(1) walk (EUR→USD base 1.08); its ConvertSpread (~0.2% round-trip) is both the arbitrage-coupling floor and the house's revenue — the arb cohort's rebalances pay the spread into the house.",
  });

  // 14 · CLOSING
  T.closingSlide(p, {
    takeaways: [
      "Four layers: entry validates, execution sequences, matching crosses, settlement moves value.",
      "A fill prints at the maker's price; the buyer pays its own reservation and refunds the surplus.",
      "The probe proves Σ Δ = 0 before every commit — the invariant that never breaks.",
    ],
    next: "DATA_LAYER — where conserved fills become durable Postgres rows",
    notes: "Hand off to the data-layer deck. Verbal bridge: we've watched one order become a conserved fill in memory; now let's follow it onto disk — the Dapper region methods, the AccountsCache, and the retention model that keeps days of 20k-bot trading queryable.",
  });
};
