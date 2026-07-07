# Bot-Loop + Rotator Optimization Sweep (2026-07-07)

Council-driven optimization pass on the rotator cohort + the AI bot tick loop. Method: a verified
per-phase cost map (Explore agent) → a 5-advisor council (First-Principles, Contrarian, Executor,
Outsider, Expansionist) reasoning over it. Safe/byte-identical wins shipped autonomously this session;
structural levers queued for tomorrow's ultraplan. **CK=0 sacred; MaxBotCap=20k; any perf-lever
default flip-ON is Kiesh-gated (never autonomous).**

---

## KEY CORRECTION TO THE MENTAL MODEL (First-Principles, load-bearing)

The batch phase is **NOT** "~1 commit/tick." With `Db:GroupCommit:Enabled=false` (the default),
`OrderExecutionService.cs:904-922` commits **one root tx per `(stockId, currency)` group** — dozens of
commits/round-trips per busy tick, run in waves gated to `Db:MaxConcurrentGroups=24`. GroupCommit
collapses that to O(#currencies). This is the center of gravity for a commit-bound loop.

Honest asterisk that discounts every commit-count lever: `synchronous_commit=off` is already held ON, so
the per-commit **fsync WAIT** is already gone. What remains per commit is the BEGIN/COMMIT round-trip +
WAL generation + the 24-wide gate serialization. Commit-count reductions are still real (fewer
round-trips, less WAL, fewer gate waves) but smaller than a sync-commit-on world would show.

---

## SHIPPED THIS SESSION (autonomous — byte-identical or pure default-off instrumentation)

All validated: build clean + 430/430 tests (CK/conservation gate green). Nothing baked-ON, no behavior
change to the default sim.

1. **Rotator: hoist bot-independent gap/velocity out of the per-bot loop** (`RotatorDecisionService.cs`).
   The estimate gap + velocity are identical across firing bots (only the tiny `idio` term varies), so
   they're now resolved ONCE per book instead of `fireCount×`. Ranking is `Score(gap,dir,idio,global)`
   verbatim ⇒ identical picks. Byte-identical.

2. **Rotator: reuse one scratch `scored` buffer across firing bots** (single loop thread ⇒ no aliasing)
   instead of a fresh `List` allocation per firing bot per book. Byte-identical, cuts GC.

3. **Arb: filter-the-cohort-first, then sort** (`ArbitrageDecisionService.cs:97`). Was
   `AiUsersByAiUserId.Values.OrderBy(id)` = materialize + O(20k log 20k) sort **every tick** just to skip
   ~19.8k non-arb bots. Arb runs every tick by default ⇒ this was a *live* waste. Now filters to the ~5
   cohort then sorts. Byte-identical (same set, same ascending-id order). **The clean win of the sweep.**

4. **MM: filter-the-cohort-first, then sort** (`MarketMakerDecisionService.cs:64`). Identical fix; MM is
   default-off so it future-proofs the cohort rather than a live win. Byte-identical.

5. **Phase-timing `cohorts` bucket** (`AiTradeService.cs`). Rotator/MM/jump/bracket-drain ran *inside*
   tick latency (so the scaler saw them) but were **invisible in the `BotPhase` profiling line**. Now a
   named bucket ⇒ "did enabling the rotator/MM slow the loop?" is directly measurable — the monitor Kiesh
   asked for. Near-zero when all cohorts off. (Opt-in via `Bots:PhaseTimingSeconds>0`, default off.)

6. **Cache `Bots:ReconcileClamp` at startup** (`AiTradeService.cs`) — was the only periodic config walk
   left in the loop; now a `readonly` field, consistent with every other startup-cached Bots flag.

> The rotator refactor already done in the prior session (filter-first cohort selection) is exactly what
> arb + MM were still missing — #3/#4 apply the rotator's already-blessed pattern to its two siblings.

---

## ULTRAPLAN QUEUE (structural — Kiesh-gated, for tomorrow)

Ranked by payoff-to-risk. None of these is shippable autonomously; each is a flip-ON, a control-loop
change, or an architecture change.

### A. Per-currency PARALLEL match/settle onto worker connections  ★ the structural bet
**Expansionist #1.** The crux the whole perf arc circled: a *single-threaded* committer gets **zero**
benefit from Postgres group commit — Postgres amortizes fsync across *concurrent* committers at the WAL
level. sc=off won ~4.5× by removing the fsync *wait* from the one serialized committer; concurrent shards
recover the *amortization* structurally, potentially **with sc=ON (durability retained)**. Currency is
the CK-safe partition: a bot holds one `Fund` per currency, so the USD shard never touches EUR funds →
conservation stays independent and provable per shard. Per-currency alone ≈ 1.5–2× (USD dominates);
architecture generalizes toward 4–8× if sub-currency account-contention is later solved. Hard risks:
cross-shard determinism (needs a deterministic per-tick barrier + fixed shard-merge order),
`RunInTransactionAsync`'s AsyncLocal savepoint model under concurrency, per-shard scaler accounting, prod
connection topology. **This is the beachhead** for B (pipeline the shards) and the realism-cohort scaling.

### B. Scaler control-loop cluster  ★ highest-value NON-commit lever (+ directly serves the monitoring ask)
Three coupled findings (First-Principles #4, Outsider #2/#3):
- **Denominator "units" question (Outsider — most surprising finding):** the loop ends with a *fixed*
  `Task.Delay(TradeInterval)` (1000 ms) with no elapsed subtraction, so the true period is `1000 + ewma`.
  But the scaler computes `loadFrac = ewma / 1000`. At its `TargetLoadFraction=0.60`, the box is actually
  busy only ~`0.6/1.6 ≈ 37%` of wall time → **the fleet cap may be far more conservative than the
  hardware warrants** (i.e. bot count left on the table). "60% load" on the dashboard ≠ real duty cycle.
- **Cap-exempt cohorts pollute the EWMA:** `RecordTickLatency` spans through arb+mm+**rotator**+jump+drain,
  so enabling the 200-bot rotator silently **lowers the fleet cap** to make room for load we explicitly
  exempted from capping. This is the exact "do the new cohorts slow the fleet?" risk. (Design tension:
  the counter-view is that the scaler *correctly* accounts for all wall-time so the tick stays ≤ interval
  — so this is a design question for the council, not a clear bug. The new `cohorts` timing bucket now
  makes the magnitude measurable before deciding.)
- **Stale shared signal:** the rotator reads `_scaler.LastLoadFraction` (refreshed every 2s `SampleInterval`,
  and *before* `OnTick` updates it) → one up-to-2s-stale signal drives two controllers (fleet cap +
  rotator valve) → phase-lag hunting near the threshold.

Fix direction: feed the scaler the span it can actually act on (Collect+Batch), account cohort cost
separately (the new bucket isolates it), optionally correct the duty-cycle denominator. Behavior-changing
(moves the cap trajectory) ⇒ re-tune + soak. **Do NOT loosen the rotator's scaler-coupling floor for
"correlation under load"** — that floor is the v1-freeze interlock (Contrarian).

### C. `Db:GroupCommit:Enabled=true` flip  (Kiesh-gated FLIP + soak, NOT a fresh design)
First-Principles #1. Collapses batch commits O(groups)→O(#currencies); already built + equivalence-tested
(`GroupCommitEquivalenceTests`) + crash-tested (`GroupCommitCrashTests`), default-off. **Caveat: reconcile
against the prior group-commit slice work (tasks #158–160) and the sc=off decision** — sc=off was the
chosen fsync lever; group-commit may have been left off deliberately or found marginal once sc=off landed.
Size the win by measured round-trip/WAL/gate-wave reduction in a soak, not an assumed 4.5×.

### D. Batch the remaining advanced classes  (Kiesh-gated FLIPs, one-at-a-time-behind-a-soak)
`BatchBuyStops` (test already written — `ArmStopBuyBatchEquivalenceTests.cs`, untracked in git status),
then `BatchShortOpens`. Removes *serial* per-order round-trips from `SubmitAdvancedAsync`. **NOT
`BracketBatch`** — the matched-order cost is the match+settle *group tx*, not entry inserts, so "batch the
entry" is spent, and it carries the bracket-flip F1 interleaving CK risk for zero gain (Contrarian: REJECT).

### E. Write-behind / async settlement pipeline
Expansionist #2. In-memory book authoritative; a separate committer batches fills across *ticks*.
Deepest attack on commit-boundedness but a **durability-model change** (crash-recovery story must be
designed + blessed first). Follow-on to A (pipeline the shards).

### F. Due-index / next-decision-time wheel for the 20k Collect scan
Outsider #4. `CollectPendingOrdersAsync` scans all 20k every tick even though stagger + cohorts make
most ineligible. **Determinism-hard:** the burst/quiet/activity RNG bookkeeping runs for *every* bot each
tick as a seeded-stream contract; a due-index that skips iteration reorders/omits draws → not
byte-identical. Needs a designed RNG-stream refactor. Lower priority — pure CPU, almost certainly <10%,
and commit-side (A/C/D) comes first.

### G. Cohort passes through the group-commit shard machinery / rotator QuickSelect ranking
First-Principles #5 + Outsider #6. Only matters once the rotator cohort grows past ~200 and fires
unthrottled — its 2 passes/ccy + per-bot full-board sort become the cohort's dominant cost. Defer until
the cohort is actually scaled.

### DEAD-END (don't spend ultraplan budget)
- **Per-book event-driven liveness scheduler for matching** — 20k bots / 70 books ≈ 285 bots/book; no
  book is ever idle per tick, so a liveness gate on the match phase saves nothing (Expansionist #5).
- **More config/volume tuning** — the arc has conclusively exhausted this ("config can't beat physics").
- **Turning MM/rotator ON for throughput** — cap-exempt but they *load the EWMA* → net cap LOSS, not a
  gain. Enabling them is a realism/prod decision with its own soak gate, never a perf rider.

---

## REJECTED (Contrarian, council-endorsed)
- Flip the spent batch levers (BracketBatch etc.) — F1 CK hazard for zero throughput. REFUSE.
- Loosen scaler-coupling so the rotator "works under load" — it's the v1-freeze stability interlock, not
  a perf knob. (The rotator being weakest-under-load *is* a real flaw, but it's a **realism-design**
  question for the realism track, not something a perf sweep should quietly fix by removing the interlock.)
- Weaken per-order determinism for speed — sells the forensic replay tool every CK investigation relied on,
  to save microseconds on a default-off cohort. REFUSE.
- Micro-opt `RankWeightedPick`/`Score` — runs a few-hundred times/tick; pure noise in any A/B.

## The trap the council is most likely to fall into (Contrarian)
Measuring the wrong thing and banking noise: the rotator fires ~4 bots under load, so any A/B toggling a
rotator micro-opt shows a delta smaller than the documented run-to-run soak variance. The real fleet-side
cost is the commit phase (A/C/D) and the multi-pass 20k scans (fixed by #3/#4 this session), not the
shiny rotator everyone was pointed at. **The rotator was never the patient.**
