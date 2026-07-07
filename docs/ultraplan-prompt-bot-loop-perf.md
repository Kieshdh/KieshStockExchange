# Ultraplan — Bot-Loop Performance Program (commit architecture + scaler control-loop)

You are planning a **performance optimization program** for a .NET 9 / PostgreSQL stock-exchange
simulation. A single background thread (`AiTradeService.RunLoopAsync`) runs the whole market every ~1s
and is **commit-bound** (Postgres). It must hold ~20,000 AI bots. **CK=0 (conservation — shares and cash
are never created/destroyed) is a sacred, non-negotiable hard gate. Determinism (the sim replays from a
seed) is highly valued and every past CK/economic investigation relied on it.** `MaxBotCap = 20000`.

Produce a **phased implementation plan + patch** for the three workstreams below (A, B, C). Each lands
**default-off / behind a flag / byte-identical when disabled** and is validated by (i) an equivalence or
determinism unit test, (ii) a CK=0 soak, before any default flip (default flips are the human's call,
never yours). Sequence them and call out dependencies. This is grounded in a completed council sweep —
read `docs/BOT_LOOP_OPTIMIZATION_SWEEP.md` for the full findings and the rejected ideas.

## Verified architecture (trust these anchors; confirm before editing)

Tick phase order in `AiTradeService.cs` `RunLoopAsync` (~1061–1168):
`CheckTimers → CollectPendingOrdersAsync → SubmitAndApplyBatchAsync (batch matcher) → SubmitAdvancedAsync
→ arbitrage.RunAsync → marketMaker.RunAsync → rotator.RunAsync → jump.RunAsync → bracket.DrainAsync →
RecordTickLatency → scaler.OnTick → auditor.AuditAsync → RunPeriodicMaintenanceAsync`.

- **The batch phase is NOT one commit/tick.** With `Db:GroupCommit:Enabled=false` (default),
  `OrderExecutionService.cs:904–922` commits **one root transaction per `(stockId, currency)` group**,
  run in waves gated to `Db:MaxConcurrentGroups=24`. `Db:GroupCommit:Enabled=true` collapses that to one
  root tx per currency (each group a SAVEPOINT) via `RunGroupCommitShardsAsync`.
- `synchronous_commit=off` is already held ON (`Db:SynchronousCommit`, the decisive ~4.5× fsync-wait win).
  So remaining per-commit cost = BEGIN/COMMIT round-trip + WAL generation + the 24-wide gate serialization.
- **Multi-table writes use `IDataBaseService.RunInTransactionAsync()`** — nested savepoints via an
  **AsyncLocal** ambient transaction. This AsyncLocal model is the crux constraint for any concurrency.
- The scaler (`BotScalerService`): `RecordTickLatency(elapsed from tickStart through tCohorts)` feeds an
  EWMA (α≈0.15–0.2); target load 0.60; moves `ActiveBotCap` in [1, 20000] proportionally with a cooldown.
  Reconcile + periodic maintenance are measured AFTER `RecordTickLatency` (correctly OFF the EWMA).
- The loop ends with a **fixed** `await Task.Delay(TradeInterval)` (1000 ms) — NO subtraction of elapsed
  work. So the true period is `1000 + tickWork`.
- Special cohorts (arbitrage 5 bots [default on], market-maker 12 [off], rotator 200 [off], jump [off])
  are **cap-EXEMPT** (they ignore `ActiveBotCap`) but their cost IS inside `RecordTickLatency` → they load
  the EWMA that caps the *normal* fleet. A new opt-in `cohorts` bucket in the `BotPhase` line now measures
  them; a smoke with the rotator on showed cohorts ≈ 132 ms/tick (mostly market-order settlement commits).

---

## Workstream A — Per-currency PARALLEL match/settle onto worker connections  ★ the structural bet

**Goal:** break the single-committer ceiling. A single-threaded committer gets ZERO benefit from Postgres
group commit — Postgres amortizes fsync across *concurrent* committers at the WAL level. Run the
match/settle of the **USD book set** and the **EUR book set** on separate worker threads, each with its
own DB connection, committing concurrently, joined by a **deterministic per-tick barrier** before the loop
advances. This may reclaim much of the throughput while allowing `synchronous_commit=ON` (durability).

**Why currency is the CK-safe partition:** a bot holds one `Fund` per currency but trades many stocks. The
USD shard only ever mutates USD funds; the EUR shard only EUR funds ⇒ no cross-shard account race ⇒
conservation stays independent and provable per shard. **Sharding *within* a currency reopens cross-book
fund contention — explicitly OUT OF SCOPE for phase 1; design the seam so it *could* extend, but do not
attempt it now.**

**Hard problems to solve in the plan:**
- The `RunInTransactionAsync` AsyncLocal ambient-transaction model under two concurrent loop workers —
  each worker needs its own ambient tx / connection scope with no shared AsyncLocal bleed.
- A deterministic barrier + fixed shard-merge order so results are replay-identical (shard results merged
  in a fixed USD-then-EUR order; each shard internally deterministic).
- Assigning the special cohorts (arb/mm/rotator/jump) and the scaler accounting to shards deterministically.
- Connection-pool sizing; how `RecordTickLatency` is computed across two parallel shards (max? sum?).

**Deliverables:** design doc of the execution model; a phased patch (phase 1 = 2 currency shards, barrier,
per-connection tx, keep sc=on); a **CK=0 long soak** plan + a determinism/equivalence test proving the
2-shard result equals the serial result on a fixed fixture. Flag-gated (`Db:ParallelShards` or similar),
default-off, byte-identical when off. This is the beachhead for a future write-behind pipeline.

## Workstream B — Scaler control-loop correctness  ★ highest-value non-commit lever

Three coupled defects (see the sweep doc §B):
1. **Denominator units:** `loadFrac = ewma / 1000ms` but the true period is `1000 + ewma` (fixed
   `Task.Delay`). At target 0.60 the box is only ~37% busy ⇒ the fleet cap is likely far more conservative
   than the hardware warrants (bot count left on the table), and the dashboard "load" is not a real duty
   cycle. Options: (a) correct the denominator to `intervalMs + ewma`; and/or (b) self-correcting delay
   `Task.Delay(TradeInterval - elapsed)` (note: (b) changes `now` spacing ⇒ changes which bots are due ⇒
   NOT byte-identical — treat carefully).
2. **Cap-exempt cohorts pollute the EWMA:** `RecordTickLatency` spans through arb+mm+rotator+jump+drain, so
   enabling the 200-bot rotator silently lowers the fleet cap to make room for load exempted from capping.
   The new `cohorts` timing bucket isolates this span. Option: feed the scaler only the span it can act on
   (Collect+Batch), accounting cohort cost separately. **Design tension to resolve, not assume:** the
   counter-view is that the scaler *correctly* accounts for all wall-time so the tick stays ≤ interval —
   decide which is right and justify it.
3. **Stale shared signal:** the rotator reads `_scaler.LastLoadFraction` (refreshed every 2s `SampleInterval`,
   before `OnTick` updates it) ⇒ one up-to-2s-stale signal drives two controllers (fleet cap + rotator
   valve) ⇒ phase-lag hunting near the threshold.

**Constraint:** these change the cap trajectory (already non-deterministic, CK-neutral) but must be
**re-tuned + soak-validated for stability (no oscillation/hunting)** before default. **Do NOT loosen the
rotator's scaler-coupling floor** — it is the interlock that prevented the v1 loop-freeze.

## Workstream C — `Db:GroupCommit:Enabled=true` validation + flip readiness

The lever is **already built + equivalence-tested (`GroupCommitEquivalenceTests`) + crash-tested
(`GroupCommitCrashTests`)**, default-off. Plan: **reconcile against the prior group-commit slice work and
the sc=off decision** (why was it left off after sc=off landed?), then a measured A/B soak quantifying
round-trip / WAL / gate-wave reduction (NOT an assumed 4.5× — sc=off already took the fsync-wait prize).
Deliverable: a go/no-go recommendation with the soak numbers + the crash-window durability semantics
spelled out. This is the cheapest of the three and may not need code — but sequence it FIRST as the
low-risk warm-up + because it informs A (both concern the commit boundary).

---

## Out of scope / rejected (do not plan these)
- Flipping the other advanced batch levers (`BracketBatch` etc.) beyond BuyStops/ShortOpens — the
  matched-order cost is the match+settle group tx, not entry inserts, so "batch the entry" is spent, and
  BracketBatch carries an F1 interleaving CK risk for zero gain.
- Per-book event-driven liveness for the match phase — 20k bots / 70 books ≈ 285 bots/book; no book is
  idle, so it saves nothing on matching.
- More config/volume tuning — the arc has exhausted it.
- Weakening determinism for speed.

## Global gates for every deliverable
CK=0 (hard) · determinism preserved or the break explicitly justified + re-validated · `MaxBotCap=20000`
respected · default-off / byte-identical when disabled · an equivalence/determinism test + a CK soak plan
before any default flip · the human owns every default-flip and prod cutover.
