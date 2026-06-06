# P6b/P6c — conservation drift under load. Findings for Ultraplan refinement.

**Status:** P6b (flat shorts + long brackets) and P6c (short brackets) are IMPLEMENTED + build clean + 63/63
unit tests, but a live soak with all advanced kinds enabled at scale **broke conservation**. P6a
(stops/trailing) is unaffected (soaked clean, committed). P6b/c code is committed on `feature/p6-bot-soak`
**behind default-off flags** (inert unless `Bots:Advanced:*Prob` are set), pending this refinement.

## Drift signature (soak: StopProb/TrailingProb 0.05, ShortProb 0.06, LongBracketProb 0.06, ShortBracketProb 0.10)
- Reconcile clean at t=0, then growing: 0 → 23 → 45 mismatches; phantomTotal 0 → 2.1k → 33k over 3 passes.
- **Position (share) phantom:** e.g. `user=112 stock=1 Δ=2; offender #...(Filled,qty=2)` — a Filled order
  left a share reservation. (P6a never produced any position phantom.)
- **`CK_Positions_Quantity_Invariants` violation** in `SettleTradesAsync → UpdatePositionsBatchAsync` — a
  batch trade tried to persist an invalid Position (ReservedQuantity > max(Quantity,0), or a share reservation
  on a short). The DB constraint rejected the write (DB integrity preserved; cache drifted).
- **"Reservation drift on buyer N: Reserved=$0.00, Amount=$X"** in batch settlement — a buyer fill called
  `ConsumeReservedFunds(notional)` with the fund holding **no** reservation; the engine auto-cancelled the
  drifted maker(s).
- Cancelled-order fund phantoms with large amounts (e.g. `#...(Cancelled, amt=13627)`).

All failures are in the **batch** path (`PlaceAndMatchBatchAsync` / `SettleTradesAsync`), triggered once bots
**hold shorts** — which the single-short admin tests never combined with concurrent market buys / batch flow.

## Candidate mechanisms (for Ultraplan to confirm + design the fix)
1. **Missing bot cover-clamp (clearly under-specified in my impl).** The §3 design said bot closes "clamp to
   |short|", but P6b only added short *opens* (`ShortOpen`/`ShortBracket`); the **plain buy path**
   (`AiBotDecisionService.ComputeOrderQuantityAsync` buy branch + `ChooseStockId`) is **unchanged and
   short-unaware**. So a bot that's short on stock X can later place a plain market BUY on X that **covers and
   flips short→long** (the buy-side mirror of risk #7), unclamped. The cover-flip is the prime suspect for the
   invalid-Position / share-phantom. Fix: make the bot's plain buy clamp to `|short|` on a stock it's short on
   (never flip), mirroring the existing sell-side `:219–236` clamp.
2. **Short collateral vs market-buy fund consume (engine-level, needs review).** A bot's short collateral sits
   in `Fund.ReservedBalance`. `TradeSettler:183 ConsumeReservedFunds(notional)` is **fund-aggregate, not
   bucket-aware** (per-order consume is clamped at `:200`, but the fund consume is the full notional). Confirm
   a plain/bracket buy can't draw down the short-collateral bucket (the "Reserved=$0.00" drift suggests a
   reservation vanished before settle — possibly collateral consumed by an unrelated buy, or a double-release).
3. **Off-loop fire × batch race at short scale.** P6b/c create far more armed stops + short positions; a
   bracket SL/TP firing off-loop (watcher/coordinator) concurrent with the next tick's batch on the **same bot
   user's** shared Fund/Position could race (the class the money-probe fix addressed for the batch path, but
   the entry/fire paths may not be fully covered for shorts). Confirm gate coverage across the entry-phase /
   watcher / coordinator vs the batch for a shorting user.

## What's solid vs what needs work
- **Solid (committed, clean):** P6a (protective stop/trailing on longs) — zero phantoms; the two-phase
  submission route + determinism + entry-route plumbing are proven.
- **Needs refinement (committed off-by-default):** P6b/c. The decision/submission scaffolding is in and
  correct in isolation; the **bot-short-at-scale interaction** with the plain buy path + batch settlement
  breaks conservation. Likely a combination of (1) the missing cover-clamp and (2)/(3) an engine-level
  short-collateral/concurrency interaction.

## Recommendation
Ultraplan to design: (a) the bot cover-clamp (decision layer, definitely needed), and (b) confirm whether the
short-collateral-vs-buy-consume and off-loop-fire-vs-batch interactions need an engine-level fix or are fully
handled — with a deterministic repro (a single bot: open short, then market-buy the same stock partially and
past flat; observe reconcile). Then I re-soak. P6a stands as the shipped first phase.
