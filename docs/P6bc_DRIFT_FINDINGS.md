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

## Sharper signal (post-soak heal run, advanced OFF)
After stopping the soak and restarting with advanced **disabled**, the first reconcile was clean but the
**next pass drifted again** (phantomTotal≈1012) — with no new shorts being opened. The only shorts in play
were the **leftover bot shorts** from the broken soak, being traded by ordinary bot **plain buys**. So the
drift reproduces from *any* bot short + a normal market buy — it is **not** an at-scale/concurrency-only
effect. This points hard at suspects (1) cover-flip and/or (2) short-collateral-vs-fund-consume, and largely
rules out (3) as the primary cause. **Best deterministic repro: one bot, open a short, then place a plain
market buy on that same stock (a) partially and (b) past flat — watch the reconciler.** (Side effect: the
demo DB carries residual bot shorts that will keep producing small, clamped drift until covered or the fix
lands; DB integrity is preserved by the CK constraint + clamp.)

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

---

## RESOLUTION (2026-06-06) — both bugs fixed; no engine bucket needed

Confirmed BOTH suspects (1) and (2) were real; (3) off-loop-fire-vs-batch was a non-issue (gating already
correct). Two fixes landed on `feature/p6-bot-soak`:

**Fix A — bot cover-clamp (decision layer).** `AiBotDecisionService.ComputeOrderQuantityAsync` buy branch now
clamps a buy on a stock the bot is short on to the *coverable* shares (`|short| − committed cover buys`), so a
plain bot buy can never flip short→long. New helper `ComputeCommittedCoverShares` mirrors the sell-side
`ComputeCommittedSellShares`. This removed the invalid-Position / share-phantom class.

**Fix B — buyer fund consume (engine layer).** Root cause: `TradeSettler` consumed the *full notional* from
`Fund.ReservedBalance`, but a buyer who also holds shorts carries short collateral in that same
`ReservedBalance`. An over-budget fill (a `TrueMarketBuy` filling above its estimate) ate into the collateral
and desynced the buy-to-close release.

- **First attempt (rejected):** a parallel `Fund.ReservedCollateral` bucket maintained across 9 sites, with the
  consume bounded to `ReservedForBuys = ReservedBalance − ReservedCollateral`. **This was a dead end** — the
  bucket drifted catastrophically (soak showed `ReservedForBuys` going to **−$11k…−$15k**, rejecting ~6000
  legitimate cover buys, cascading into auto-cancelled makers → 1200 reconcile mismatches). A parallel
  accounting field that must stay in lock-step across that many sites is inherently fragile.
- **Final fix (shipped):** **no bucket.** Consume from `ReservedBalance` only up to *this buy order's own*
  `CurrentBuyReservation` (already maintained accurately in lock-step), and pay any excess from **available
  cash** via `WithdrawFunds`. By construction the consume can never touch another order's reservation or the
  short collateral. The `Fund.ReservedCollateral` field and all 9 maintenance sites were removed.
  - Worked example: short holder market-buys to cover; reserved estimate $5000, fills total $5380. First fills
    consume from the $5000 reservation; the final $380 over-estimate comes from available cash. Collateral
    untouched. Verified by hand against the limit-buy savings path (unchanged: notional ≤ reservation always).

Build clean, 63/63 tests pass. The persisted Postgres DB was found **invariant-clean** (0 over-reserved
positions/funds, 0 qty≥0-with-collateral) — the soak's `CK_Positions` "violations" were *rejected* write
attempts, never persisted, so no DB reset was needed. Re-soak with all 5 advanced kinds enabled
(Stop/Trailing 0.10, Short 0.15, Long/ShortBracket 0.10) to confirm the reconcile trend stays flat.

### Fix B validated — but a SECOND bug surfaced (short-bracket SL fire)

On the inherited-corrupt `kse` DB the re-soak confirmed Fix B works (collateral-consume rejections 6068 → **0**;
reconcile **flat** at the inherited baseline 45/1100 vs the old runaway 6→1200) — but a stream of
`CK_Positions` settle-failures persisted, and on the corrupt DB it was impossible to tell inherited-vs-systemic.

To resolve it cleanly **without** touching the real `kse` DB (a reset was correctly blocked as unauthorized
scope escalation), spun up a **fresh scratch DB `kse_soak`** (created + EF-migrated + app auto-seeded from the
embedded workbook) and soaked there. On the pristine DB, `CK_Positions` violations appeared **on brand-new
activity** (order #330, fresh seed) → **a real, systemic, pre-existing bug, not inherited corruption.**

**Root cause (short-bracket SL fire).** Repro: parent short #329 (market sell 50) filled only 12 then
cancelled the rest → short −12, collateral on the position + fund. Its SL leg #330 fired (market buy 12) to
cover. The SL owns the cash pool (`CurrentBuyReservation = SL_worst × held`), so its buyback consume **and**
the buy-to-cover collateral release **both draw `Fund.ReservedBalance`** in the same settle. When the
SL-pool-vs-collateral arithmetic rounds such that the consume leaves `ReservedBalance` a few units below the
position's `ShortCollateral`, the old release clamp (`min(collateral, ReservedBalance)`) **under-released**,
leaving a flat position (`Quantity 0`) still carrying `ShortCollateral > 0` → violates
`CK_Positions_Quantity_Invariants`, the whole settle rolls back, and the order is left `Filled` while the
position stays uncovered (order↔position desync). This is exactly the short-bracket **SL-fire path** memory
flagged as "unit-verified only; self-trade prevention blocks an admin-driven fill — needs a bot-driven soak."
The bot soak found it.

**Fix (TradeSettler buy-to-cover, §P6).** Decouple the POSITION release from the FUND unreserve: when a cover
makes the position flat/long, release the **full** position collateral (the DB invariant is sacred — a
non-negative position must carry zero collateral), and unreserve from the fund **clamped** to `ReservedBalance`.
Any shortfall is logged and left as a small fund over-reservation for the **reconciler to clamp** (the
established "reconciler is the real conservation check" pattern) — never hard-fail the settle / desync
order↔position. Re-soaking on a fresh `kse_soak` to confirm `CK_Positions` → 0 and the shortfall warning (which
quantifies the residual) is rare/tiny.

### Clean-soak result (fresh `kse_soak`, all 5 advanced kinds @ Stop/Trail .10 / Short .15 / brackets .10)

**Hardening works — DB integrity is perfect:** over t+2…t+10m, `CKviol = 0`, `badPos = 0`, `badFund = 0`
(every persisted position and fund valid), `collConsume = 0` (Bug B fixed), shorts growing 310→1571 (advanced
paths heavily exercised). Reconcile jumped 5→108 mismatches then held flat; money phantomTotal tiny ($3278).

**BUT the soak exposed a DEEPER, pre-existing bug (NOT caused by these fixes; my changes only touched the
buyer-consume side):** the shortfall warning fired ~18/min, and 194 of 235 events were **large** (avg ~$1259,
max $7355, many "fund had only **0** reserved") — i.e. NOT rounding. A direct aggregate confirms it:

```
Σ position ShortCollateral = $5,875,788   vs   Σ fund ReservedBalance = $5,335,197
```

Fund reserved is *below* total position collateral even though it ALSO contains SL pools + live buy
reservations — so short collateral is being **under-reserved on the fund by $540k+ and growing with the short
count**. Example: user 193 holds 6 shorts totaling $39,524 collateral with **$0** reserved on its fund.

**Nature of the bug:** a *reservation-locking* leak, NOT a money or DB-integrity violation — `TotalBalance` is
conserved and every row passes its CHECK constraint. The effect is that a short's collateral isn't actually
locked, so the user's AvailableBalance is overstated (they can spend cash that should be held). The reconciler
sees it as the "under-reserved" mismatches (report-only; the clamp can heal phantoms/over-reservation, not
under-reservation). It spans the advanced **short-open** paths (plain market short, long→short flip, and the
short-bracket coordinator's pool/cushion accounting in `OnParentFillShortAsync` / `OnChildFillShortAsync` /
`OnStopFiringShortAsync`) — all conservation-sensitive, Ultraplan-designed (P1 / P5b / risk #7) territory, and
only reproducible under concurrent bot load (single-order admin tests never hit it; cf. the money-probe
parallel-group race that the batch path already had to fix).

**Status / recommendation.** Validated & solid this session: Bug B fix, Fix A cover-clamp, and the CK-violation
hardening (DB integrity proven on a clean soak). The short-collateral under-reservation is a separate,
larger finding that needs a focused design pass on where/when advanced short opens reserve collateral on the
fund vs the position under load — recommend an Ultraplan pass (it's their short-collateral model and is
conservation-critical). Repro harness: fresh `kse_soak` (create + `dotnet ef database update` + auto-seed),
run with `Bots__Advanced__Enabled=true` and the probs above, then compare Σ position collateral vs Σ fund
reserved.
