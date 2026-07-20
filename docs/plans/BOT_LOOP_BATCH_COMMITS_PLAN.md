# Bot-loop throughput — batch the per-order commits (advanced + arbitrage)

Status: PLAN for Ultraplan to harden, then implement (await approval). Self-contained — Ultraplan works
from this + the repo on `master`. Source investigation: [[project_bot_loop_perf]] + `logs/PERF_INVESTIGATION_NOTES.md`.

---

## 1. Problem (measured, not guessed)

The bot loop is single-threaded with a 1s `TradeInterval`. `BotScalerService` holds the active-bot cap so
the tick-work EWMA ≈ 60% of the interval (~600ms). So **the active-bot ceiling is `0.6 × interval ÷
per-tick-cost`** — heavier ticks ⇒ fewer active bots (observed: ~250 active of 20k, down from 10k+).

Per-phase profiling (added this session: `Bots:PhaseTimingSeconds`, `AiTradeService.LogPhaseTiming`) shows
where the tick goes, local, ~250 active:

| phase | ms/tick | why |
|---|---|---|
| **adv** (stops/brackets/shorts) | **160–440** | each placement is its OWN `RunInTransactionAsync` → insert + reserve + **commit**, run sequentially. ~40ms/order. |
| batch (plain orders) | 60–140 | `PlaceAndMatchBatchAsync` does bulk insert/update for MANY orders in ONE transaction → ONE commit. ~5ms/order. |
| arb (5-bot cohort) | 50–155 | same shape: 2 true-market legs + a convert, each its own tx/commit. |
| collect (ALL decisions) | 8–22 | cheap — NOT the bottleneck. |
| check / recon | spikes / ~0 | sentiment Tick cheap; prune(30s)/reload(60s) spike the EWMA. |

**Root cause = per-order COMMITS.** Funds/positions are already served from the in-memory `AccountsCache`
(`_accounts.GetFund`/`GetPosition`), so there is no cacheable-read win — the cost is the commit round-trip,
paid N times/tick on the advanced + arb paths vs once for the whole plain batch.

## 2. Goal & success metric

Cut `adv` (and `arb`) from hundreds of ms to tens of ms by **collapsing N commits/tick → ~1**, so the
scaler supports far more active bots at the same 600ms budget.

- **Primary:** active-bot cap rises substantially (target: 250 → 600+; ideally back toward 10k as load allows)
  at the same `TickWorkMsEwma` budget. Verify via the `BotPhase`/`Scaler` log lines.
- **Invariants (must stay green):** `ConservationProbe` = 0, `CK_Funds`/`CK_Positions` = 0, `ReservationAuditor`
  phantomTotal within tolerance, 63/63 unit tests, no change to fill outcomes (same orders, same matches).
- **No behavior change** to the market (this is a plumbing change, not a tuning change).

## 3. Where the per-order commits live (grounded)

- `AiTradeService.SubmitAdvancedAsync` (~`:554`): sorts advanced decisions by AiUserId, then **sequentially**
  `await`s one entry-route call per order:
  - `IOrderEntryService.PlaceStopMarketSellOrderAsync` / `PlaceTrailingStopSellOrderAsync`
    → `OrderEntryService.ArmStopOrderAsync`/`ArmTrailingStopAsync` (`:221`,`:282`) → `_engine.ArmStopAsync`
    (own tx: reserve + persist `Pending`) + `_stopWatcher.Arm`.
  - `PlaceTrueMarketSellOrderAsync` (short open) → `PlaceOrderAsync` → engine place+match (own tx).
  - `PlaceBracketAsync` (`OrderEntryService.cs:327`) → `BracketCoordinator` (own multi-leg tx).
- `ArbitrageDecisionService.RunAsync`: per bot `TryFlattenAsync` (market sell), `TryRoundTripAsync` (2 true-
  market legs), `MaybeRebalanceAsync` → `UserPortfolioService.ConvertAsync` (own tx). Each is its own commit.
- Plain batch for contrast: `AiTradeService.SubmitAndApplyBatchAsync` → `_marketOrders.PlaceAndMatchBatchAsync`
  (groups per book, one transaction with `InsertAllAsync`/`UpdateAllAsync`).
- Enabler: `IDataBaseService.RunInTransactionAsync` **supports nested savepoints via AsyncLocal** (per CLAUDE.md).
- Invariants to preserve (engine comments in `OrderExecutionService.cs`): ordering is **book → gates → tx**
  ("book lock stays outermost"); `AcquireUserGatesAsync` sorts keys (no AB/BA deadlock); advanced today runs
  "outside the matcher's locked region, each call owns its own gates."

## 4. Proposed approaches (Ultraplan to pick/harden)

### Option B — single outer transaction per tick (RECOMMENDED first step, lowest-risk-per-gain)
Wrap the tick's advanced placements (and separately the arb pass) in ONE `RunInTransactionAsync`. The inner
per-order transactions become **savepoints**, so there's a single COMMIT at the end instead of N.
- Leverages existing nested-savepoint support → minimal new code.
- **Per-order savepoint isolation:** an inner failure must roll back only that order (to its savepoint), not the
  whole tick — confirm `RunInTransactionAsync` nesting gives this (it should: savepoint per nested scope).
- **Open questions for Ultraplan:**
  1. Gate/lock lifetime: the outer tx spans all placements; do per-user gates / book locks get held across the
     whole advanced batch? The loop is single-threaded (no cross-thread contention), but verify nothing inside
     (e.g. a bracket-parent market fill) deadlocks or violates book→gates→tx when nested.
  2. `ConservationProbe` runs per settlement (per `PlaceAndMatch`); confirm it still validates each settle inside
     the outer tx (it should — it checks the batch delta at settle time, not at commit).
  3. Market-fill advanced (short open, bracket parent) do matching; ensure matching-within-outer-tx is sound and
     the watcher arm (`_stopWatcher.Arm`) still happens only on committed success (move arms to after commit, or
     make them idempotent on rollback).

### Option A — true batched arm path (bigger, larger ceiling)
Mirror the plain matcher: collect all advanced arms for the tick, bulk-insert the `Pending` orders + bulk-apply
reservations in one transaction, then register all with the watcher. Market-fill advanced (short opens, bracket
parents) route through the existing plain batch matcher instead of one-at-a-time. More invasive (the arm/bracket
paths aren't batch-aware) but removes the sequential structure entirely.

### Option C — Postgres commit tuning (orthogonal, combine with B/A)
The commit round-trip is the floor. `synchronous_commit=off` (or `local`) for the bot write path, or group-commit
/ WAL tuning, cuts per-commit cost across EVERY phase. Bot data is reconstructable/disposable, so relaxed
durability is acceptable for it (human writes could stay synchronous if separable). Low-effort experiment,
potentially the largest single gain. Measure first via the dry knob before committing to it.

### Option D — parallelism / pipelining (largest, most invasive)
Single-threaded loop: overlap collect (CPU) with batch (DB), and place independent per-book legs in parallel
(respecting per-book locks). Biggest redesign; only if A/B/C don't reach the target.

## 5. Suggested sequence
1. **Option C experiment** (free measurement): flip `synchronous_commit=off` on a soak DB, re-profile — quantify
   the commit-floor gain before changing code.
2. **Option B** for advanced + arb (the structural win that doesn't need C).
3. Re-profile (`BotPhase` lines), confirm the cap rises + invariants green over a multi-hour soak.
4. Only if needed: Option A, then D.

## 6. Validation
- Re-run with `Bots:PhaseTimingSeconds` on; compare `adv`/`arb` ms/tick and the settled active-bot cap vs the
  baseline in `logs/PERF_INVESTIGATION_NOTES.md`.
- Multi-hour soak: `ConservationProbe`=0, `CK_`=0, reservation reconcile within tolerance, 63/63 tests, identical
  fill behavior. The harness: `scripts/kse-balance-soak.ps1`.
