# Ultraplan — Bot-Loop Performance Program (commit architecture + scaler control-loop)

You are planning a **performance optimization program** for a .NET 9 / PostgreSQL stock-exchange
simulation. A single background thread (`AiTradeService.RunLoopAsync`) runs the whole market every ~1s
and is **commit-bound** (Postgres). It must hold ~20,000 AI bots. **CK=0 (conservation — shares AND cash
are never created/destroyed) is a sacred, non-negotiable hard gate. Determinism (the sim replays from a
seed) is highly valued and every past CK/economic investigation relied on it.** `MaxBotCap = 20000`.

Produce a **phased plan** for the three workstreams below. **Deliverable format (mandatory, uniform):**
- **Order the work C → B → A** (cheapest/safest first; A must not block B or C).
- **Deliver A, B, C as THREE INDEPENDENT patches** (separate commits, no code interdependency) so C can
  flip and B can tune while A is still in design.
- **A is DESIGN-FIRST**: deliver the execution-model design + a phased *seam/skeleton* patch only — do NOT
  produce the full parallel-commit implementation until the design is blessed. **B and C may be
  apply-ready patches.**
- **For each of A/B/C return exactly:** (1) findings, (2) design or diff, (3) the equivalence/determinism
  test, (4) the CK=0 soak plan, (5) a one-line go/no-go.
- Everything lands **default-off / flag-gated / byte-identical when disabled**. **You never flip a default
  or cut over prod — the human owns every flip.** If a fix cannot be byte-identical, split it to its own
  flag, mark it lower priority, and ship the byte-identical parts first.

This is grounded in a completed council sweep — read `docs/BOT_LOOP_OPTIMIZATION_SWEEP.md` for the full
findings and the rejected ideas.

## Verified architecture (all anchors confirmed against the code; re-confirm before editing)

Tick phase order in `AiTradeService.cs` `RunLoopAsync` (~1050–1188):
`CheckTimers → CollectPendingOrdersAsync → SubmitAndApplyBatchAsync (batch matcher) → SubmitAdvancedAsync
→ arbitrage.RunAsync → marketMaker.RunAsync → rotator.RunAsync → jump.RunAsync → bracket.DrainAsync →
RecordTickLatency → scaler.OnTick → auditor.AuditAsync → RunPeriodicMaintenanceAsync`.

- **The batch phase is NOT one commit/tick.** With `Db:GroupCommit:Enabled=false` (default,
  `OrderExecutionService.cs:94` + appsettings), `OrderExecutionService.cs:904–922` commits **one root
  transaction per `(stockId, currency)` group** (via `Task.WhenAll` of `RunGroupWithRecoveryAsync`), gated
  to `Db:MaxConcurrentGroups=24`. `Db:GroupCommit:Enabled=true` routes to `RunGroupCommitShardsAsync`
  (`:1264`) = one root tx per currency, each group a SAVEPOINT.
- **`Db:SynchronousCommit=off` is a PROD RUNTIME KNOB, not a committed default.** It's applied by
  `PostgresConnectionFactory.cs:36-40` from the `Db:SynchronousCommit` config, but the key is **unset in
  every committed appsettings** ⇒ a fresh checkout / local run uses Postgres' default (`on`). It's listed
  in `RESEED_CHECKLIST.md` as a prod deploy step. So: **do NOT assume the committed code path runs with
  sc=off.** The "~4.5× fsync-wait win" is a *deployment* fact. Reason about commit-count levers (B/C/A)
  BOTH ways — the win is larger under sc=on (fsync wait present) and smaller under prod's sc=off.
- **Multi-table writes use `IDataBaseService.RunInTransactionAsync()`** (`:15`) — nested savepoints via an
  **AsyncLocal ambient transaction** (see `GroupCommitCrashTests.cs:39-43`, `UserPortfolioService.cs:652`).
- The scaler (`BotScalerService`): `RecordTickLatency(elapsed from tickStart through tCohorts)` (`:1129`)
  feeds an EWMA (`EwmaAlpha=0.2`); `TargetLoadFraction=0.60`; moves `ActiveBotCap` in `[1, 20000]`;
  `loadFrac = ewma / intervalMs` with `intervalMs = TradeInterval = 1000` (`BotScalerService.cs:69,79`);
  `SampleInterval=2s`. Reconcile + periodic maintenance are measured AFTER `RecordTickLatency` (OFF the EWMA).
- The loop ends with a **fixed** `await Task.Delay(TradeInterval)` (`:1185`) — NO subtraction of elapsed
  work ⇒ true period is `1000 + tickWork`.
- Special cohorts (arbitrage 5 bots [**default ON**], market-maker 12 [off], rotator 200 [off], jump [off])
  are **cap-EXEMPT** (gated only by their own `_*Enabled` flags, independent of `ActiveBotCap`) but their
  cost IS inside `RecordTickLatency` → they load the EWMA that caps the *normal* fleet. A smoke with the
  rotator on measured the `cohorts` bucket ≈ 132 ms/tick (mostly market-order settlement commits).

---

## Workstream A — Per-currency PARALLEL match/settle onto worker connections  ★ the structural bet (DESIGN-FIRST)

**Goal:** break the single-committer ceiling. A single-threaded committer gets ZERO benefit from Postgres
group commit — Postgres amortizes fsync across *concurrent* committers at the WAL level. Explore running
the match/settle of the **USD book set** and the **EUR book set** on separate worker threads/connections,
committing concurrently, joined by a **deterministic per-tick barrier**.

**★ THE HARD PART — currency partitions CASH but NOT SHARES (the real CK constraint; the naive
"per-currency = independent" premise is FALSE, design for this):**
- Cash: a bot holds one `Fund` **per currency**, so a USD shard (USD Funds only) and an EUR shard (EUR
  Funds only) never race on cash. Cash conservation IS independent per currency shard. ✓
- **Shares: `Position` is CURRENCY-AGNOSTIC** (`Position.cs:69-70`). A cross-listed stock (≈20 of 50
  stocks ⇒ ≈40 of 70 books) has ONE `Position` row that BOTH books mutate. A USD-book fill and an EUR-book
  fill on the same (bot, stock) in the same tick would put two shard threads on the same
  `Position.Quantity`/`ReservedQuantity` → race → **share-conservation (CK) violation.** ✗
- **Cross-boundary WRITE PATHS you must design for ALL of (not just Funds):** (1) shared `Position`
  qty/reserved on cross-listed fills; (2) the **DEFAULT-ON arbitrage cohort** — trades both legs of one
  stock across USD+EUR (shared Position) AND FX-rebalances USD Fund ↔ EUR Fund + house account atomically,
  **every tick**; (3) **short collateral** — an EUR-book short takes EUR-`Fund` collateral but stamps the
  shared Position's `ShortCollateralCurrency` (`Position.cs:71`, `TakeShortCollateral`); (4) the FX rate
  service + the house account; (5) ConservationProbe / ReservationAuditor accumulators.

**HARD CONSTRAINT (not a scheduling footnote):** cross-currency / shared-`Position` operations MUST run
**SERIALLY outside the parallel barrier** (before/after it), never concurrently across shards. The parallel
region may only touch state provably owned by one shard this tick. **Do NOT claim "provably independent per
shard."** Treat shared-`Position` + cross-currency contention as THE primary CK risk to design against, and
**quantify the honestly-parallelizable subset** — if only the ~30 single-currency books can go parallel
while ~40 cross-listed books + the arb/FX/house/short paths stay serial, say so and size the real win
accordingly (it may be well under a naive 2×).

**AsyncLocal landmine — name the fix:** `Task.Run` children **capture the parent `ExecutionContext`**, so
the AsyncLocal ambient transaction flows in and one shard's tx leaks into the other. The design must use
`ExecutionContext.SuppressFlow` (or a fresh context/scope per worker) so each shard has its own ambient tx
— "each worker gets its own scope" is otherwise aspirational.

**Also design:** the deterministic barrier + fixed shard-merge order (USD-then-EUR) so results are
replay-identical; per-shard connection sizing; how `RecordTickLatency` is computed across two parallel
shards (max, not sum). **Explicitly OUT OF SCOPE:** sharding *within* a currency (reopens cross-book Fund
contention); and trying to make the arb/FX cohort itself parallel-safe (it can't be — cross-currency ops
stay serial). **Deliverable = design + phased seam patch only** (flag `Db:ParallelShards` or similar,
default-off), NOT the full implementation.

## Workstream B — Scaler control-loop correctness  ★ highest-value non-commit lever (apply-ready)

Three coupled defects (see the sweep doc §B):
1. **Denominator units:** `loadFrac = ewma / 1000ms` but the true period is `1000 + ewma` (fixed
   `Task.Delay`). At target 0.60 the box is only ~37% busy ⇒ the fleet cap is likely far more conservative
   than the hardware warrants, and the dashboard "load" isn't a real duty cycle. Fix options: (a) correct
   the denominator to `intervalMs + ewma` (byte-identical to the sim — only moves the cap, CK-neutral);
   and/or (b) self-correcting delay `Task.Delay(TradeInterval - elapsed)` — this changes `now` spacing ⇒
   which bots are due ⇒ **NOT byte-identical**, so split it to its own flag, lower priority.
   **★ STABILITY HAZARD (flag it):** correcting the denominator immediately cranks the cap UP toward 20k;
   with the fixed delay, if `tickWork` approaches the interval the period balloons and the EWMA chases
   itself. Add an explicit invariant: **keep tick ≤ interval, rate-limit the cap increase, and re-tune +
   soak for no oscillation/hunting before any flip.** Ship the byte-identical (a) first, gated.
2. **Cap-exempt cohorts pollute the EWMA:** `RecordTickLatency` spans through arb+mm+rotator+jump+drain, so
   enabling the 200-bot rotator silently lowers the fleet cap to make room for load exempted from capping.
   The `cohorts` timing bucket isolates this span. Option: feed the scaler only the span it can act on
   (Collect+Batch), accounting cohort cost separately. **Resolve, don't assume:** the counter-view is that
   the scaler *should* account for all wall-time so the tick stays ≤ interval — decide + justify, and keep
   the tick-≤-interval invariant either way.
3. **Stale shared signal:** the rotator reads `_scaler.LastLoadFraction` (refreshed every 2s, and the
   rotator runs before `OnTick` updates it) ⇒ one up-to-2s-stale signal drives two controllers ⇒ phase-lag
   hunting near the threshold. Consider an EWMA-of-load for the rotator's read.

**Do NOT loosen the rotator's scaler-coupling floor** — it is the interlock that prevented the v1 loop-freeze.

## Workstream C — `Db:GroupCommit:Enabled=true` validation + flip readiness  (do FIRST, likely no code)

The lever is **already built + equivalence-tested (`GroupCommitEquivalenceTests`) + crash-tested
(`GroupCommitCrashTests`)**, default-off. Plan: **reconcile against the prior group-commit slice work and
the sc=off decision** (why was it left off after sc=off became the prod knob?), then a measured A/B soak
quantifying round-trip / WAL / gate-wave reduction **under BOTH sc=on and sc=off** (sc=off already took the
fsync-wait prize, so the win is smaller there — measure, don't assume 4.5×). Deliverable: a go/no-go with
the soak numbers + the crash-window durability semantics spelled out. Cheapest of the three; sequence FIRST
as the low-risk warm-up and because it informs A (both concern the commit boundary).

---

## Out of scope / rejected (do not plan these)
- Flipping the other advanced batch levers (`BracketBatch` etc.) beyond BuyStops/ShortOpens — the
  matched-order cost is the match+settle group tx, not entry inserts, so "batch the entry" is spent, and
  BracketBatch carries an F1 interleaving CK risk for zero gain.
- Making the arbitrage / FX-rebalance / house cohort itself parallel-safe or sharded — cross-currency ops
  stay serial (see A's hard constraint).
- Sharding within a currency (phase 1); per-book event-driven liveness for the match phase (285 bots/book,
  no book idle); more config/volume tuning (exhausted); weakening determinism for speed.

## Global gates for every deliverable
CK=0 including SHARE conservation across the shared cross-currency `Position` (hard) · determinism
preserved or the break split to its own flag + explicitly re-validated · `MaxBotCap=20000` + tick ≤
interval respected · default-off / byte-identical when disabled · three independent patches, A design-first
· an equivalence/determinism test + a CK soak plan before any default flip · the human owns every
default-flip and prod cutover.
