# ULTRAPLAN HANDOFF — finish the advanced/arb order-batching (engine throughput)

**This is the prompt to feed the Ultraplan planner. Deliverable required: a single `git apply`-clean PATCH FILE
that local Claude applies, then implements/soaks autonomously for ~9 h. This is the ONLY ultraplan this round.**
Branch `feature/bot-market-realism-v2`. Companions: `docs/BOT_LOOP_A1_ADVANCED_BATCH_BRIEF.md`,
`docs/PERF_SCALING_PLAN.md`.

---

## 0. What we need back (deliverable contract)
Return **one patch file** (`git apply --check` must pass clean against the branch tip, one shot — if it can't,
the patch is defective; local will NOT hand-fix) that is:
- **Self-contained** (no missing-file refs), **flag-gated default-OFF**, **byte-identical when off**.
- Ships/updates **equivalence + conservation unit tests** for every batched path it touches.
- Touches **nothing in `/Tools`**; no unrelated formatting churn.
- Plus a short apply/soak/bake runbook local Claude follows.

## 1. Council verdict on scope (decided 2026-06-18 — DO NOT exceed)
- **PRIMARY (must): validate + bake the already-coded `BatchArms` + `BracketBatch` flags.** Most of the
  batching is ALREADY WRITTEN but dark/unproven; the night's value is turning proven-conservation-clean dark
  code into baked capacity.
- **STRETCH (only if primary lands clean with hours to spare): batch the arb legs**, behind its OWN flag,
  **default-off, left unbaked** (one night's soak can't bake new sequential code).
- **EXPLICITLY OUT OF SCOPE: the structural decision/commit decoupling (write-behind / group-commit pipeline).**
  It's the real 5–10× but a write-behind durability change that CANNOT be money-conservation-proven in one
  night → near-zero EV tonight / engine-breaking risk. It earns its OWN later ultraplan, with a written design
  first, only after batching is proven exhausted. (Council near-unanimous.)

## 2. System context (self-contained — the planner has no repo access)
.NET 9 stock-exchange simulation, ~20k AI bots, **single-threaded bot loop, 1 s tick**. A `BotScalerService`
holds the active-bot cap so tick-work EWMA ≈ 60 % of the interval; heavier ticks ⇒ fewer active bots ⇒ less
volume. Reads come from an in-memory `AccountsCache`; the ceiling is **per-COMMIT DB round-trips**:
- **Plain orders**: `OrderExecutionService.PlaceAndMatchBatchAsync` — Phase 1 validate, Phase 1.5/1.6 pre-reserve
  sells/buys into the cache (snapshot to `TradeBatchScope` for rollback), Phase 2 ONE tx `InsertAllAsync`,
  Phase 3 per-(stock,currency) group tx in parallel (book lock → `AcquireUserGatesAsync` sorted keys →
  `SettleTradesNoTxAsync` → commit, 40P01/40001 retry), Phase 4 publish ticks. ~0.6 ms/order on prod = cheap.
- **Advanced + arb orders**: each its OWN transaction = expensive (~5 ms/order prod, ~8× the plain path).
**Sacred invariant:** lock order book → per-user gates → DB tx; every reserve emits the same `_ledger.Log*` +
`Take*Reservation` as the per-order path so `ConservationProbe`=0, `CK_Funds`/`CK_Positions`=0, and
`ReservationAuditor` reconciles in tolerance. Determinism: the advanced route is sequential in ascending
`aiUserId` (seed contract). Soaks are NOISE-heavy + local Postgres is docker-commit-latency-skewed (a real win
must show as fewer round-trips / lower ms-PER-ORDER, not just local wall-time).

## 3. Current code state (verify against tree at implementation)
- **A1a arms — SHIPPED, prod-gate met.** `Bots:Advanced:BatchArms` (default off). `OrderExecutionService.
  ArmStopBatchAsync`, `OrderEntryService.ArmStopSellBatchAsync`, `ArmStopBatchEquivalenceTests`. Tonight's A/B:
  adv −42 %/order.
- **A1b short-opens + A1c brackets — CODED, NEVER SOAKED.** `Bots:Advanced:BracketBatch` (default off, "Round 2
  §0005"). `OrderEntryService.PlaceMarketShortBatchAsync`→`_engine.PlaceMarketShortBatchAsync`;
  `OrderEntryService.PlaceBracketBatchAsync`→`_engine.PlaceBracketBatchAsync` (+ `BracketBatchRequest`,
  `ValidateBracketRequestAsync`). Wired in `AiTradeService.SubmitAdvancedAsync` (~:791–819,
  `SubmitBracketBatchAsync`): partitions the tick's advanced cohort into brackets/shorts/rest.
- **Arb legs — NOT batched.** `ArbitrageDecisionService.RunAsync`: per round-trip leg1
  `PlaceTrueMarketBuyOrderAsync` (cheap book) → leg2 `PlaceTrueMarketSellOrderAsync` (filled qty, expensive
  book), each its own engine tx; periodic `ConvertAsync` (FX desk). **leg2 qty depends on leg1 fill** —
  sequential dependency, so legs of one round-trip can't be one flat batch.

## 4. The work
### 4a. PRIMARY — harden + bake BatchArms + BracketBatch
The patch should ADD what's missing to make baking safe (the soak is local Claude's job; the patch supplies the
verification scaffolding + any fixes):
- `MarketShortBatchEquivalenceTests` + `BracketBatchEquivalenceTests` if absent — batched vs per-order produce
  identical Pending/active rows, Position `ReservedQuantity`, per-order reservation ledger tuples, bracket
  parent/SL/TP wiring; partial-failure isolates the bad order; determinism (ascending `aiUserId`) preserved.
- Audit `PlaceBracketBatchAsync` against the **bracket-flip R3 invariants** (shared SL/TP reservation pool;
  `OnParentFillAsync` hook ordering; partial parent fills; the recently-added `FlipQuantity`). Fix any gap found
  (this is the highest-risk surface). If clean, say so + why.
### 4b. STRETCH — arb-leg batching (flag default-off, unbaked)
Batch leg1 (buys) across the arb cohort in one PlaceAndMatchBatch, then leg2 (sells) sized from each leg1 fill
in a second batched pass — 2 batched passes/tick vs 2×N txs, preserving the fill dependency. Own flag
(`Bots:Arbitrage:BatchLegs`?), default off. Note: 5-bot cohort = small absolute win; include only if cheap.

## 5. The 9 h runbook local Claude will follow (Executor's plan)
- **H0–0:45** `git apply --check` → apply → build (server + MAUI client) → full unit suite green (incl. new
  equivalence tests). Any red → bake nothing, report.
- **H0:45–4:00** validate BracketBatch: 2 parallel soaks (max 2 servers) flag-on vs flag-off, ~90 min + a
  90 min confirm on the winner. **Bake gate (must hold BOTH rounds): ConservationProbe=0, CK_Funds/CK_Positions=0,
  ReservationAuditor in tolerance, AND adv ms/order down flag-on.** All hold twice + perf drops → bake
  (default-on, commit, push). Any nonzero CK/conservation even once → leave default-off, capture repro, stop.
- **H4:00–4:30** gate: if baked clean with time left → arb stretch; if shaky → re-soak the batch only.
- **H4:30–8:00** arb-leg batching (only if gate passed): apply that part / implement, ONE soak, keep flagged
  default-off (one soak ≠ bake for new sequential code).
- **H8:00–9:00** write soak comparison + candle CSVs + bake/no-bake rationale into `PERF_SCALING_PLAN.md`.

## 6. Merge/bake gate (non-negotiable)
Flag-gated; `Bots:Advanced:MaxPerTick` stays as the instant fallback. Bake only on conservation/CK/auditor
clean across repeated soaks + ms-per-order drop. `synchronous_commit` bake is SEPARATE (durability — see
PERF_SCALING_PLAN §8 Q2). Note: tonight's docker has `synchronous_commit=off + full_page_writes=off +
commit_delay=50` set for throwaway soaks — local Claude reverts before any durability-sensitive run.

## 7. Open questions for the Ultraplan
1. Is `BracketBatch` safe to bake as-is, or does the R3 bracket-coordinator/SL-TP-pool/FlipQuantity audit find
   a gap needing a fix in the patch?
2. Arb-leg batching: worth it for a 5-bot cohort, or leave per-order and just document?
3. Bake BatchArms + BracketBatch together (one soak) or staged?
4. What equivalence/property tests best gate batched bracket *partial parent fills* (the fragile case)?

## 8. Soak evidence (local Claude is gathering NOW, will append)
- BatchArms+BracketBatch ON vs BatchArms-only A/B running tonight → conservation + adv ms/order results feed
  back into this doc + `PERF_SCALING_PLAN.md §10` before/with the patch apply.
