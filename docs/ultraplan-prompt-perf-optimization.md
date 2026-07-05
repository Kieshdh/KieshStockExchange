# Ultraplan prompt — restore 20k-bot throughput (perf regression)

**For the cloud ultraplan. Produce a PHASED implementation plan (with per-phase gates, expected cap gain, and risk), NOT a blind rewrite.
Phase 0 (profiling) is mandatory and gates everything after it.** Council-driven (5/5 + chairman synthesis, 2026-07-05) — the strategic
direction below is settled; your job is the deep code investigation + the concrete plan.

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
