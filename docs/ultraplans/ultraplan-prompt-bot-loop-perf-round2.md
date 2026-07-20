# Ultraplan — Bot-Loop Performance, ROUND 2 (post-validation)

Round 1 (`docs/ultraplan-prompt-bot-loop-perf.md`) proposed three workstreams; the patch was applied,
validated (build clean + 439/439 tests, committed `04b7d40` + `8aa1e12`, all default-off byte-identical),
and **soak-tested locally**. This round carries the results — one workstream is refuted, one is proven
safe, one is directionally right but mis-calibrated — and defines the real remaining work. Same repo, same
sacred gates: **CK=0 (shares AND cash), determinism, `MaxBotCap=20000`, everything default-off, the human
owns every default-flip and prod cutover.**

## What the round-1 validation FOUND (evidence committed; don't re-derive)

- **★ Workstream A (per-currency parallel match/settle) = REFUTED. Do NOT build it.** The A measurement
  gate (concurrent-committer high-water mark bracketing `_tx.CommitAsync`, on the `BotPhase` line) read
  **~13–17 max concurrent committers** under load. The default batch path already fans out per-`(stock,
  currency)` group via `Task.WhenAll` gated at `Db:MaxConcurrentGroups=24`, so Postgres already amortizes
  fsync ~13–17-wide. Per-currency sharding gives only 2-way ⇒ **strictly worse**. The "single-threaded
  committer starves group commit" premise is false. (Prod runs `synchronous_commit=off`, removing the
  fsync wait entirely ⇒ doubly moot.) The shared cross-currency `Position` is already mutated across those
  parallel group tasks today and stays CK-clean. **Drop A entirely.**

- **★ Workstream C = group-commit is SHARE-CONSERVATION SAFE for the shared cross-currency Position**,
  proven at the fill level under partial shard failure (`GroupCommitSharedPositionFillTests`, committed
  `8aa1e12`): EUR-shard fsync-death → durable store shows only the committed USD leg, buyer-gained ==
  seller-lost across both currencies, crashed taker recovered, cache released. The contrarian's shared-
  Position CK worry does NOT bite. **Caveat:** the fake is single-threaded, so it proves durability/
  rollback conservation, not the in-memory concurrent-mutation race — but that race already happens in the
  default 13–17-wide parallel group path in every CK-clean prod soak, so it's empirically safe.

- **★ Workstream B (scaler duty-cycle correction) = the insight is RIGHT but the calibration OVER-CORRECTS.**
  A/B soak (30m, rotator on both arms, CK=0 both, 0 errors):
  - CONTROL (default scaler): cap settled ~**1575**, tick ~615–800 ms, ~57 trades/s, drift +0.6%. Note
    `cohorts 186–204 ms` (the rotator's settlement cost sitting IN the EWMA, depressing the cap — the exact
    pollution `ActionableSpanSizing` targets).
  - TREATMENT (`DutyCycleDenominator` + `ActionableSpanSizing`): cap raced to **20000 (max) and PINNED**
    there (no oscillation/hunting), but the **tick ballooned to ~4000 ms = 4× the 1 s interval** (batch
    alone ~2.6 s at 20k bots), ~**392 trades/s (~7×)**, CK=0, drift +3% (bounded).
  - Root cause: `TickGuardFraction=0.95` is inert — with `fullDuty = ewma/(interval+ewma)` it only binds at
    ewma ≥ ~19 s; and `TargetLoadFraction=0.60` on the corrected denominator implies ewma≈1500 ms (tick
    1.5× interval) even at "target." So nothing stops the cap pinning at max with a 4× tick.
  - **The real truth this exposed: 20k bots CANNOT be processed in a 1 s tick** (batch ≈ 2.6 s at 20k). The
    cap that keeps tick ≤ interval is between ~1575 and 20000, not either extreme. `MaxBotCap=20000` is
    aspirational for this box at a 1 s cadence.

- **★ NEW dominant cost (rotator OFF, cohorts 0.00): the ADVANCED-ORDER phase ~250–310 ms/tick is the
  single largest phase**, bigger than batch (~215–280 ms), at ~40 adv/tick. With the rotator ON it adds a
  further ~200 ms `cohorts`. So the levers that actually raise the sustainable-at-1s cap are the
  **advanced-order route** and the **batch matcher per-order cost** — not sharding.

## ROUND-2 WORKSTREAMS (what to actually patch)

### R2-1 — Recalibrate the scaler to a STABLE equilibrium (primary; small, high-value)
The duty-cycle correction works but has no sane setpoint. Make the corrected control law target a stable
cap where **tick ≤ (a configurable multiple of) the interval**, instead of pinning at max.
- Fix the `TickGuardFraction` semantics so the guard binds when the tick reaches the interval: to enforce
  tick ≤ interval you need it to bind at ewma ≥ interval, i.e. `fullDuty ≥ 0.5` — so the *code-default*
  guard for the corrected path should be ~0.5, not 0.95. Verify the guard actually caps growth (the soak
  showed it didn't).
- Reconcile `TargetLoadFraction` with the corrected denominator: 0.60 targets tick ≈ 1.5× interval. Either
  target ≤ 0.5 on the corrected denominator, or add an explicit `MaxTickMultiple`/target-tick knob so the
  operator chooses the tick-cadence ceiling directly.
- **Try config-only first** (sweep `TickGuardFraction`≈0.5 and the target) in a soak; only add a code knob
  if the current levers can't express "settle where actionable-work ≈ interval."
- **Deliverable:** a soak showing the treatment cap settling at a stable value (NOT pinned at 20000) with
  tick ≤ chosen×interval, CK=0, no oscillation — plus the cap it lands on vs control's ~1575.
- **★ PRODUCT DECISION FOR KIESH (surface, don't decide):** a bigger cap at a coarser tick is a *legitimate
  win* — the treatment did ~7× the trades. Choice: (a) keep a 1 s tick → moderate cap increase (~2–5k?);
  (b) accept a 2–4 s tick for ~7× throughput / all-20k-active; (c) somewhere between. R2-1 should make
  BOTH reachable by config and report the trade curve.

### R2-2 — Advanced-order + batch per-tick cost (the NEW real lever)
The adv phase (~250–310 ms) is the top per-tick cost and the batch matcher is ~2.6 s at 20k — these set the
sustainable-at-1s cap. Investigate + reduce:
- Why does ~40 adv/tick cost ~250–310 ms? `Bots:Advanced:BatchBuyStops` and `BatchShortOpens` are already
  built default-off (prior commits) — measure their effect on the adv phase (a soak/A-B), and find any
  advanced class still on the per-order commit path that could batch.
- Is there per-order overhead in the batch matcher (allocations, redundant reads) that lowers the
  sustainable cap? A profiling pass at a fixed cap (e.g. 3000) to find the per-order hot spots.
- **Deliverable:** the adv-phase reduction (flag-gated) + the sustainable-at-1s cap it buys.

### R2-3 — GroupCommit throughput measurement (soak, likely no code)
C proved group-commit is *safe*; the open question is whether it *helps* given the default already commits
13–17-wide. Run `Db:GroupCommit:Enabled` off vs on (both under sc=on AND sc=off if possible) and measure
round-trips/order, commits/sec, trades/sec, tick. If it doesn't beat the 13–17-wide default, mark it a
prune candidate; if it does, it's a Kiesh-gated flip. No new code expected — a measurement + verdict.

## Out of scope / settled
- Workstream A (dropped). Sharding within a currency. Per-book liveness for matching (285 bots/book).
  Weakening determinism. `BracketBatch` (F1 CK risk, zero gain).

## Global gates
CK=0 (shares + cash) · determinism preserved or the break split to its own flag + re-validated ·
`MaxBotCap=20000` and the **tick ≤ (chosen)×interval invariant** respected · default-off / byte-identical
when disabled · a test + a CK soak before any default flip · the human owns every flip, the tick-cadence
product decision, and prod cutover.
