# ULTRAPLAN HANDOFF — per-currency engine sharding (+ bot staggering)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE + apply→build→test→soak→bake
runbook that local Claude executes. Branch `feature/bot-market-realism-v2`.** Target = **PROD capacity** (local
docker is commit-latency-skewed ~10×; judge by ms/round-trips-per-order, not local wall-time). Builds on
`docs/PERF_SCALING_PLAN.md` (esp. §13–14), `docs/BOT_LOOP_A1_ADVANCED_BATCH_BRIEF.md`, [[project_bot_loop_perf]].

## Why this round (the finding that points here)
The bot loop is single-threaded; the ceiling is **per-COMMIT DB round-trips**. The 2026-06-18 batch round proved
**"batch the advanced ENTRY route" is SPENT**: BatchArms (arms = reserve+insert, NO match) was the win (−42%,
baked); BracketBatch/short-opens/arb showed **zero** gain because **matched-order cost is the per-`(stock,
currency)` MATCH+SETTLE group transaction**, not entry-inserts. So the remaining throughput lever is to
**parallelize / reduce those group-txs** — and per-currency sharding does that AND independently fixes the thin
EUR books (a separate user pain point: the FX desk drains EUR→USD).

## Scope (user-approved; two slices, staggering first)
### Slice 1 (recommended FIRST — cheaper, safer): bot staggering
Phase-offset each bot's act schedule (act every N seconds, offset by bot id) instead of all-bots-every-tick.
Likely **extends the existing per-bot `Lateness`** field. Effect: ~N-fold cut in per-tick work (the scaler then
allows far more loaded bots), MORE realistic flow cadence, and lets more bots cover EUR names. Flag-gated,
default-off, byte-identical when off. Lower risk (no concurrency/conservation model change) → bank this first.
CAUTION: preserve the seed-determinism contract (ascending-aiUserId processing) and don't perturb the tuned
realism (ret_acf) — the stagger must be a deterministic function of bot id + tick, not RNG.

### Slice 2 (the structural win): per-currency engine sharding
Run a **separate engine instance + DB connection per currency** (USD, EUR) so their books + match/settle
group-txs run in PARALLEL on independent connections, and EUR fill-rate scales independently of USD.
**THE HARD PART = cross-currency atomicity/conservation:** two flows span currencies and must NOT break
ConservationProbe / CK_Funds / CK_Positions / ReservationAuditor:
- **FX desk** (`UserPortfolioService.ConvertAsync`, the house account) moves value USD↔EUR.
- **Arbitrage cohort** (`ArbitrageDecisionService`) trades the SAME currency-agnostic `Position` across the USD
  and EUR books (buy cheap book / sell rich book) — legs land on different shards.
The Ultraplan must design how cross-shard operations stay conserved (options: a coordinator/2-phase path for
cross-currency txs; keep FX+arb on a shared serialized path while sharding only the single-currency flow; or a
global conservation reconciler). This is the crux — get it wrong and money isn't conserved.

## OUT OF SCOPE
- Decision/commit decoupling (write-behind) — the other big lever; separate future ultraplan.
- EUR seed-bot rebalancing — a `Tools/` seed task (tracked separately; user also chose it).

## Hard constraints / invariants (non-negotiable)
- Conservation is sacred: ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in tolerance.
- Lock order book → per-user gates (`AcquireUserGatesAsync`, sorted keys) → DB tx (no AB/BA deadlock). Sharding
  changes the locking domain — re-establish the ordering per shard + for cross-shard ops.
- Every change flag-gated, default-OFF, byte-identical when off. `Bots:Advanced:MaxPerTick` stays the fallback.
- Determinism: per-shard processing must keep the seed-reproducibility contract.
- Win must show as ms/round-trips-per-order on PROD; local docker is skewed.

## Deliverable contract
ONE patch (`git apply --check` clean, one shot), self-contained, flag-gated default-off, ships equivalence +
conservation tests (sharded vs single-engine produce identical rows/reservations/ledger; cross-currency FX+arb
conserved), touches nothing in `/Tools`, no formatting churn. Plus the apply→build→test→soak→bake runbook
(stagger slice first; soak each slice flag-on/off parallel A/B; bake only conservation-clean + measured win).

## Open questions for the Ultraplan
1. Cross-currency conservation design — coordinator/2-phase vs shared-serialized FX+arb vs global reconciler?
2. Is staggering enough alone (cheap N-fold) to defer sharding, or are both needed for the volume target?
3. Sharding granularity — per-currency (2 shards) only, or per-(currency) + parallel book groups (already exists
   in `PlaceAndMatchBatchAsync` Phase 3 via `_groupGate`)? Does the existing per-group parallelism already give
   most of the win, making full sharding redundant?  ← worth checking first; may shrink the scope.
4. How does sharding interact with the scaler (one scaler vs per-shard)?

## Soak evidence to feed in (local Claude gathers)
Stagger A/B (on/off): per-tick load cut + cap lift + conservation. Sharding A/B: cross-currency conservation +
per-currency cap + EUR fill-rate. Both on the baked realism config.
