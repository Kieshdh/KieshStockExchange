# Ultraplan prompt — restore 20k-bot throughput (perf regression)

**For the cloud ultraplan. Produce a PHASED implementation plan (with per-phase gates, expected cap gain, and risk), NOT a blind rewrite.
Phase 0 (profiling) is mandatory and gates everything after it.** Council-driven (5/5 + chairman synthesis, 2026-07-05) — the strategic
direction below is settled; your job is the deep code investigation + the concrete plan.

## ★ MEASURED (2026-07-05, local base-config profiling soak, 15 min, recovered machine — use these, not the stale bake numbers)
Ran the current **base config** (prod Stage-1 realism, NO ship/co-fire levers) and mined the existing `BotPhase` instrumentation:
- **Settled cap ~1,700–2,300 active bots** (scaler at target 60% load) — a ~9× regression from the old 20k A/B capability, and this is BEFORE the co-fire/ship levers. ⇒ **the regression is mostly in the BASE realism, not the co-fire.**
- Tick ~525–735 ms, phases (at ~2,300 bots): **`batch` (order submit→match→settle→commit) ~250 ms · `adv` (advanced orders) ~200 ms (18–28 adv/tick) · `arb` ~100 ms · collect ~18 · maint ~30 · check ~0.4.**
- **`adv` is the surprise** — the stop/trailing trigger-scan + arm path is nearly as costly as the entire order/match/commit batch. **`arb` is confirmed FIXED-cost** (~100 ms independent of cap ⇒ eats headroom at every fleet size). `batch` is order-THROUGHPUT-bound (0.6–0.7 round-trips/order — the prior commit-decoupling works; 32–41 commits/sec), NOT book-scan-bound: the resting book is only ~2,800 open limit orders in a fresh soak (92% within 1% of last, 0 stale) — **NOT bloated**. (Still: verify book growth over a LONG-running instance — a fresh soak resets it.)
- ⇒ **Fattest, most gate-able targets: `adv` + `arb` (~300 ms of the ~550 ms tick).** `batch`/decision-cost/persistence are the deeper structural levers. The ultraplan's Phase 0 sub-operation profiler should confirm these at 1k/5k/10k and split `batch` into submit-vs-match-vs-commit.

## ★★ REVIEW-COUNCIL REFINEMENT (2026-07-05, 5/5 + chair — this REPRIORITIZES the phases below using the measured data)
1. **`adv` (advanced orders) is the #1 target, not arb** — the measurement makes it ~2× arb. ~7-11 ms PER arm × 18-28/tick ⇒ a PER-ARM cost (likely a DB read / position re-eval per arm), and the scan walks ALL live stop/trailing arms EVERY tick regardless of price. **THE CAUSAL FIX = a price-threshold GUARD/INDEX: only wake an arm whose trigger price was CROSSED since last tick** ⇒ O(triggered)≈0 in quiet periods, collapsing `adv`. Phase 0 must confirm WHAT the per-arm scan does (DB touch? lock? full re-eval?) — that dictates the fix. **This is Phase 1's #1 item** (ahead of arb).
2. **`arb` is a POLLING loop → make it EVENT-TRIGGERED**, not a blind every-N-ticks gate. ⚠️ The Contrarian's trap: a blind cadence gate lets cross-currency spreads open between fires → bots harvest phantom profit that **CK's balance check does NOT catch** (it checks fund/share conservation, not spread arbitrage). So arb must fire when a spread PRECONDITION exists (cheap check), and any cadence change needs a **multi-currency spread-drift validation** (does not exist in the harness today — add it). NOT a safe blind local change.
3. **Sparse activation is the big perf lever AND a realism change** — cutting `adv`+`arb`+decision-cost is proportional to ACTIVE bots, so wake-schedules could restore 20k WITHOUT the persistence rewrite. BUT heterogeneous wake-horizons change fleet dynamics BOTH ways: they can IMPROVE realism (trader-horizon diversity → larger transient imbalances → attacks the LLN averaging that flattens the market) OR DEGRADE it (desync suppresses the same-tick-cohort autocorrelation herding/momentum/ret_acf depend on). ⇒ design it carefully + gate on a FULL realism-scorecard A/B (not a smoke test).
4. **Persistence-decouple (Phase 3) is DEMOTED to optional/science-project** — if sparse activation + adv/arb fixes reach 20k, the async-commit rewrite (irreversible + CK-crash-window + prod-user-facing risk) isn't needed for the goal; keep it only as the path to 100k / faster-than-realtime replay tooling.
5. **LOCAL-vs-ULTRAPLAN split (what the human's agent may do unattended vs what ultraplan must spec):**
   - SAFE LOCAL (config-only, reversible, measurement): `Bots:Arbitrage:BatchLegs=true` A/B (bake if cap ≥+5%) · `Bots:Advanced:Enabled=false` cap-isolation (measures the adv share; do NOT bake — degrades realism).
   - ULTRAPLAN (touches order-firing semantics / CK / realism): the adv price-guard (bracket lifecycle + settlement-adjacent), arb event-triggering (phantom-spread), sparse activation (realism A/B), persistence-decouple.

## The problem
The engine is a **single-threaded, commit-bound bot loop** (each tick: bots decide → place orders → `MatchingEngine` → `SettlementEngine` →
Postgres commit). A scaler (`BotScalerService`) sizes the ACTIVE fleet (`ActiveBotCap` ≤ `MaxBotCap`=20,000) to hold per-tick load ~60%.
**Regression:** the owner used to run 20k-per-side A/B soaks (40k bots) on this machine; after the realism arc's many features, the full config
now throttles to **~1,000 active bots** (tick ~574 ms). ⚠️ **That 574 ms breakdown — batch/match/commit 272 ms + advanced-orders 121 ms +
arbitrage 145 ms — was measured AT ~1,000 bots, is PHASE wall-clock (not a flamegraph), and will NOT predict the 20k profile.** Treat it as a hint, not a target.

## Architecture + prior perf work (don't re-solve what's done)
- Loop + phase timing live in `KieshStockExchange.Server/Services/BackgroundServices/AiTradeService.cs` (the `BotPhase` log = check/collect/batch/adv/arb/recon/maint).
- Order flow: `OrderEntryService` → `OrderExecutionService` → `MatchingEngine` → `SettlementEngine`; multi-table writes via `IDataBaseService.RunInTransactionAsync()` (nested savepoints).
- Bot decisions: `Services/BackgroundServices/Helpers/AiBotDecisionService.cs` (+ `BotSentimentService`, `FundamentalService`, anchors, homeostasis).
- Arb: `ArbitrageDecisionService`. Advanced orders: `ComputeAdvancedDecisionAsync` + the stop/trailing trigger-scan + arm path.
- **ALREADY DONE (do not redo — measure what's LEFT after them):** commit-decoupling, group-commit (`Db:GroupCommit`, fewer fsyncs), batched advanced-order arming (`Bots:Advanced:BatchArms`), per-currency settlement gates (`Db:PerCurrencyGroupGates`). These raised the cap ~1,870→8,650→13-19k historically.

## Council verdict — the STAGED approach (your plan should follow this shape)
**Phase 0 — PROFILE (mandatory gate; the Contrarian + Executor's non-negotiable).** Build a sub-operation profiler inside the tick loop
(stopwatch per named op, not per phase; log at 100-tick intervals). Capture at **1k / 5k / 10k bots**, plus: (a) **order-book size** at each level
(the Contrarian's prime suspect — months of stops/trailing/arb legs may have grown the in-memory book UNBOUNDED, so `MatchingEngine` scans a huge
book every tick), (b) **a co-fire pulse in isolation** (the ~5,000-bot simultaneous market-order burst every ~30 s = a spike steady-state numbers
miss), (c) **CPU-bound vs round-trip-bound attribution** per op. Deliverable: the real bottleneck at the real target load. *No code changes until this data exists.*

**Phase 1 — cheap, high-confidence wins (data-driven):**
- **Arb cohort** — 145 ms at ~1k bots is a FIXED per-tick cost that doesn't scale with fleet ⇒ eats headroom at every size (Executor's cheapest win).
  Gate it to fire every N ticks and/or batch its DB writes / decouple its book-scans from the hot loop.
- **Advanced-order trigger scan** — if it scans ALL resting orders every tick, index or batch it.
- **Unbounded-book retention** — if Phase 0 confirms book bloat, prune/retire stale resting orders (there's already candle/txn retention infra to mirror).

**Phase 2 — reduce the WORK, not just its speed (First Principles' big-but-contained win): sparse activation.** Most real participants are idle
most of the time, yet the loop runs every active bot's full decision pipeline (multi-timescale rings + slope model + anchors + homeostasis) every
tick. Give each bot a personal wake-schedule / cooldown so only a fraction decide per tick → cut decision load 80-90%. **MUST be validated against
the realism scorecard** (`docs/BOT_MECHANICS.md` §1 + the soak gate-set) via an A/B (dense vs sparse): correlation, damping, ret_acf, CK must hold —
sparse activation changes fleet dynamics, so it is NOT free behaviorally.

**Phase 3 — the structural unlock (highest reward + risk; gate on Phase 0-2 data): decouple simulation state from persistence cadence.** A
simulation's conservation (CK) is a LOGICAL invariant, not a DB durability requirement (Outsider + Expansionist). Run in-memory between ticks and
commit at candle boundaries / async-drain prior-tick writes while the next decision phase runs hot. Unlock (Expansionist): not "back to 20k" but
potentially 100k bots + a **faster-than-realtime research harness** (drive the sim clock faster → a sim-day in ~20 min → A/B experiments go from
overnight to before-lunch).

## ★ Constraints + the blind spots the advisors underweighted (the chairman's additions — HONOR THESE)
1. **CK is sacrosanct + the crash-window is real.** The per-tick commit exists partly for crash-recovery + conservation integrity (group-commit
   already documents a CRASH WINDOW tradeoff). Persistence-decoupling (Phase 3) trades durability for speed — plan it as a **SOAK/RESEARCH-mode
   option first**, keeping prod's durability, because **prod is USER-FACING** (real users see portfolios; a crash losing in-memory state = a visible
   inconsistency). Name the prod-vs-research split explicitly; don't blur them (the advisors did).
2. **Sparse activation must not silently break realism** — gate Phase 2 on the scorecard A/B (above).
3. **Prune is NOT the perf fix** (unanimous) — the ~15 default-off levers cost ~0 when off. Do the prune (`docs/PRUNE_PROPOSAL.md`) as a SEPARATE
   hygiene pass bundled with the reseed; it can precede the refactor as codebase-cleanup scaffolding, but it is not on the perf critical path.
4. **Single-threaded is preferred** — a full multi-threaded rewrite is high-risk; Phase 3's async-drain is the bounded way to overlap CPU + I/O
   without a threading-model rewrite. Only propose more if Phase 0 proves CPU is the wall AND I/O overlap can't fix it.

## Deliverable
A phased plan: for each phase — exact files/functions to touch, the change, the expected cap gain (hypothesis to confirm against Phase 0 data), the
CK/behavioral validation gate, and the rollback. Phase 0 first, always. Keep MaxBotCap=20k and the market behaviorally correct throughout.
