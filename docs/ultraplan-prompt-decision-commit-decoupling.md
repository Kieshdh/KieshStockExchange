# ULTRAPLAN HANDOFF — decision/commit decoupling (group-commit write-behind)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE + apply→build→test→soak→bake
runbook that local Claude executes. Branch `feature/bot-market-realism-v2`.** Target = **PROD capacity** (local
docker is commit-latency-skewed ~10×; judge by ms / round-trips-per-order and commits/sec, NOT local wall-time).
Builds on `docs/PERF_SCALING_PLAN.md` (esp. §2, §7.2, §13–15), the per-currency-sharding round
(`docs/ultraplan-prompt-per-currency-sharding.md`), [[project_perf_scaling_round]], [[project_bot_loop_perf]].

## Why this round (the finding that points here)
The bot loop is single-threaded and **per-COMMIT-round-trip bound** (`PERF_SCALING_PLAN` §2). Three rounds have
now narrowed the lever to ONE thing:
- **§7.1 "batch the advanced ENTRY route" is SPENT.** BatchArms (arms = reserve+insert, NO match) won −42% and is
  baked; brackets/short-opens/arb entry-batching gave **zero** gain because **the cost of a matched order is the
  per-`(stock,currency)` MATCH+SETTLE group transaction**, not the entry insert (§13).
- **The sharding/staggering round** (the one currently soaking) *parallelizes* and *load-cuts* those group-txs but
  does **not reduce the number of fsync round-trips** — each group still commits its own transaction.
- ⇒ The only remaining structural lever is **coalescing the group-tx COMMITS themselves**: many orders / many
  groups → **far fewer fsyncs**. That is decision/commit decoupling (§7.2) — the council's projected **5–10×** and
  the real ceiling-mover, because it attacks `commits/sec × orders/commit` directly.

## What the engine does today (ground truth — read before designing)
- `AiTradeService` collects a tick's pending orders, then `OrderExecutionService.PlaceAndMatchBatchAsync` groups
  them per `(stockId, currency)` and (Phase 3) runs each group's match+settle as **its own DB transaction**
  (`RunGroupTxAsync`), gated by `_groupGate` (Npgsql-pool guard; this round adds an optional inner per-currency
  gate). Plain orders within a tick already share work, but **each group still does its own commit/fsync**.
- **The in-memory `AccountsCache` is already the read source of truth** for funds/positions; the DB write is the
  durable record, not the live read path. This is the key enabler — settlement mutates the cache, and the DB
  persist is what we want to make write-behind.
- Lock order is sacred: **order book lock → per-user gates (`AcquireUserGatesAsync`, sorted keys) → DB tx.**
- Conservation invariants (the bake gate): ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in
  tolerance. `Bots:Advanced:MaxPerTick` is the standing fallback.

## ⚠️ GATE 0 — empirical kill-check BEFORE any engine surgery (council-mandated, do first)
The council (2026-06-18, §below) flagged that this whole round may be solving an already-deleted cost. Settle it
empirically in ~1 hour before touching the engine:
1. **fsync microbenchmark** — standalone harness (throwaway console or skippable `[Fact]`): ONE Npgsql connection,
   N tiny INSERTs into a scratch table, compare (a) N separate `BEGIN/COMMIT` round-trips vs (b) N statements
   pipelined into ONE COMMIT. Measure **commits/sec on the actual docker Postgres**. **Repeat both with
   `synchronous_commit=off`.** If group-commit doesn't coalesce fsyncs at the driver level here, OR sync_commit=off
   already flattens the gap, the premise is dead — stop and report. One hour spent, not a sprint.
2. **Land the metric first (its own byte-identical PR):** there is NO `commits/sec` metric today, and on the
   10×-skewed docker box the soak CANNOT adjudicate the win by wall-time. Add **commits/sec + mean/p99
   round-trips-per-order + equilibrium bot-cap** to the BotPhase telemetry (or a probe), validated byte-identical,
   BEFORE the write-behind soak.

Only proceed to the slices if Gate 0 shows fsync-coalescing is real AND material under the prod-bound durability
mode. Reframe the goal from "coalesce fsyncs" to **"get the durable write off the single decision thread"** —
post-sync_commit=off the binding constraint is likely one thread doing N synchronous round-trips in series
(CPU/lock serialization), not fsync latency.

## Scope (two slices; the SAFE one first — mirror the staggering-first cadence)
> **Council caveat (read before trusting "Slice 1 = safe"):** today `RunGroupTxAsync` OWNS the transaction
> (open→match→settle→commit under `_groupGate`). Coalescing means tick-scope owns the commit → you must split
> "produce write-set" from "commit write-set," thread a shared tx through every settle path, and define **N-group
> rollback semantics**. Wrapping N group-txs in one commit ALSO changes failure atomicity (one poison group rolls
> back the whole tick's fills) — that is NOT byte-identical when ON. Slice 1 carries ~70% of Slice 2's risk. If the
> microbench says fsync is the cost, prefer a **per-currency SHARDED writer** (composes with the sharding round) +
> a parallel read-only decision stage over a single global writer (a single writer just relocates the serial
> bottleneck).
### Slice 1 (recommended FIRST — lower risk): group-commit pipeline (coalesced fsync, cache unchanged)
Keep matching + settlement **exactly as today** (synchronous, against the in-memory cache, same lock order), but
**decouple the DB persist from the fsync**: drain each tick's per-group writes into a **single pipelined Npgsql
connection** that issues **one group-commit (N groups / one fsync)** instead of one commit per `(stock,currency)`
group. The cache is already authoritative for reads, so within a tick nothing observes the difference. This is the
"write-behind the *durability*, not the *logic*" version — smallest blast radius, and it directly cuts
`commits/sec`. Flag-gated `Db:GroupCommit:{Enabled:false, MaxBatch:N}`, default-off, byte-identical when off.
CRUX for Slice 1: **crash atomicity, and ConservationProbe is BLIND to it.** The probe runs against the in-memory
cache, which reads conserved (0) even when the durable DB is short by the un-fsynced window — so the existing gate
cannot see the exact hazard this round introduces. The crash-window contract is **design artifact #1**: state
precisely what is lost on crash (e.g. the last write-behind batch / last tick), and ship a **crash-injection test
that kills the process between cache-mutate and fsync, then reconciles the DB ALONE (not the cache)** and asserts
the surviving durable state is internally conserved. A coalesced commit must also be all-or-nothing across the
groups it batches; document the enlarged crash blast-radius (group #1..#N lost together, not just the last).

### Slice 2 (the structural version): intent-channel decision/commit decoupling
Split the tick into a **parallel read-only decision stage** (bots read the cache + book snapshots, emit order
*intents* into an in-memory channel — no writes, no locks held) and a **single dedicated writer** that drains the
channel, runs match+settle, and **group-commits**. Decouples CPU-bound decision from fsync stalls so the loop
stops blocking per-commit. Bigger change: it moves where locks/gates are taken and changes the durability model to
true write-behind. Flag-gated default-off. Only pursue if Slice 1's coalesced-fsync win is insufficient alone.
CRUX for Slice 2: **determinism + conservation under a producer/consumer split** — the seed-reproducibility
contract (ascending-aiUserId processing, no RNG perturbation) must survive the parallel decision stage, and the
intent→apply ordering must be deterministic (order the channel by aiUserId/seed, NOT arrival). Make the writer
**per-currency/book sharded** (composes with the sharding round), not one global writer.

**THE HIDDEN PRIZE (Expansionist) — build the intent channel as an event-sourced log, not a transient queue.** Once
order intents are a durable, ordered stream you get, nearly for free: (1) **deterministic session replay** —
record a session once, replay the exact tape against a tweaked config and diff the candles; this collapses the
noisy, non-reproducible realism-soak loop that 19 experiments fought (potentially ~5× realism iteration velocity)
and is independently valuable; (2) **replay-based crash recovery** that is STRONGER than per-commit durability
(re-run the log against a snapshot); (3) a **control plane** (live admin throttles, per-cohort injection,
kill-switches = intents you admit/drop) and an **analytics tap** (stream the log to a replica so soak metrics stop
competing with the hot path); (4) the doorway off the single thread entirely (shard decision workers across cores
→ processes). If Slice 2 is built, build the log — the volume goal is downstream of it.

## PRE-CHECK (could shrink scope — do this first)
1. **Measure how many fsyncs/tick the sharding round already removed.** If per-currency parallelism + staggering
   already pushed the prod cap near the volume target, Slice 1 alone (coalesced fsync) may be the whole win and
   Slice 2 is deferrable. Pull the latest cap/commits-per-sec from the sharding-round soak before designing.
2. **Confirm the cache-is-read-truth assumption** in `AccountsCache` / `SettlementEngine` — Slice 1's safety rests
   on the DB write being off the live read path. If any read path hits the DB directly, that path must be handled.
3. **Check Npgsql pipelining/batching support** actually coalesces fsyncs the way assumed (one round-trip, one
   WAL flush for the batch) vs. just pipelining statements — this determines whether Slice 1 delivers.

## OUT OF SCOPE
- Per-currency engine sharding / bot staggering — the PRECEDING round (separate, may already be baked).
- Postgres `synchronous_commit=off` bake — a prod durability **decision** (PERF_SCALING_PLAN §8 Q2), already
  approved for prod deploy; orthogonal to this code change (it cheapens each fsync; group-commit cuts their count —
  they stack).
- EUR seed-bot rebalancing — a `Tools/` task tracked separately.

## Hard constraints / invariants (non-negotiable)
- **Conservation is sacred:** ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in tolerance —
  including the crash-window definition (what write-behind can lose must still leave a conserved state).
- Lock order unchanged: order book → per-user gates (sorted keys) → DB tx. Any new writer/channel must preserve it.
- Every change flag-gated, default-OFF, **byte-identical when off**. `Bots:Advanced:MaxPerTick` stays the fallback.
- **Determinism:** seed-reproducibility (ascending-aiUserId, no RNG perturbation, no wall-clock in decisions).
- Win must show as **commits/sec ↓ + ms/round-trips-per-order ↓ + equilibrium cap ↑** on PROD; local docker is
  commit-latency-skewed — judge by the transferable metrics, not local wall-time.

## Deliverable contract
ONE patch (`git apply --check` clean, one shot), self-contained, flag-gated default-off, ships:
- **equivalence tests** (group-commit ON vs OFF produce identical persisted rows + reservation-ledger tuples),
- **conservation/crash tests** (a simulated crash mid-write-behind leaves a conserved DB+cache state),
touches nothing in `/Tools`, no formatting churn.

**Return two things:** (1) the **patch file** (Gate 0 first — microbench + commits/sec metric — may be its own
leading hunks, or a separate first patch if cleaner), and (2) a **ready-to-paste "bake prompt" for local Claude**:
a self-contained runbook telling local Claude exactly how to apply→build→test→soak→bake THIS patch — branch +
`git apply --check`, the build commands (server / tests / MAUI client per CLAUDE.md), `dotnet test`, the parallel
A/B soak invocation (flag-on/off, baked realism env block, lowercase DB names, absolute script path), which
metrics to harvest (commits/sec, round-trips/order, equilibrium cap, EUR fill-rate) + the conservation/crash-window
gate, and the explicit **bake criterion** (bake a slice's default to ON only if conservation-clean AND a measured
commits/sec-or-cap win; Gate 0 first; Slice 1 before Slice 2; if Gate 0 fails, STOP and report). Local Claude
applies, builds, runs the full test suite, soaks, and bakes only conservation-clean measured wins.

## Open questions for the Ultraplan
1. **Group-commit mechanism** — Npgsql batch/pipeline on one connection, an explicit multi-group `BEGIN…COMMIT`
   wrapping several `(stock,currency)` group-txs, or a dedicated WAL-style writer? Which actually coalesces fsyncs?
2. **Is Slice 1 (coalesced fsync, cache unchanged) enough** to hit the volume target, deferring Slice 2's
   intent-channel? (Pre-check #1 should answer this.)
3. **Crash-window contract** — exactly what is acceptable to lose on crash (last tick? last N ms?), and how is the
   recovered DB+cache proven conserved? This is the gate the whole round lives or dies on.
4. **Interaction with this round's per-currency gate / sharding** — does group-commit batch ACROSS currencies (one
   fsync for both books) or per-shard (one writer per shard)? How do they compose?
5. **Interaction with the scaler** — fewer fsyncs ⇒ lighter ticks ⇒ the scaler admits more bots; confirm the
   EWMA setpoint still self-levels and the cap rises rather than oscillating.

## Soak evidence to feed in (local Claude gathers)
Group-commit A/B (on/off) on the baked realism config: **commits/sec** (new metric — add to BotPhase or a probe),
equilibrium cap, ms/round-trips-per-order, EUR fill-rate, and the full conservation battery (ConservationProbe/CK/
ReservationAuditor + the crash-window test). Both on the baked realism config, parallel A/B, ≥2 servers max.

## Council review (2026-06-18) — improvements folded into this doc
Five-advisor council run before handoff. Strong convergence; the doc above already incorporates the changes.
- **AGREE (high-confidence):** (1) the crash-window is THE gate and **ConservationProbe is blind to it** (probe
  reads the cache, which is conserved even when the durable DB is short) → crash-injection test reconciling the DB
  alone is mandatory; (2) "Slice 1 = safe/self-contained" is **false** — it's transaction-ownership surgery
  (`RunGroupTxAsync` owns the tx today) carrying ~70% of Slice 2's risk + a new all-or-nothing atomicity that is
  NOT byte-identical-on; (3) **instrument first** — no commits/sec metric exists, and a 10×-skewed docker box can't
  judge the win by wall-time.
- **CLASH — Slice 1 vs Slice 2:** First-Principles + Expansionist say **skip Slice 1** (once `synchronous_commit=off`
  lands — already approved for prod — async commit backgrounds the WAL flush, so coalescing fsyncs solves a mostly
  deleted cost; the real constraint becomes one thread doing N serial round-trips = get the write OFF the decision
  thread = Slice 2). Contrarian counters that a **single** writer just relocates the bottleneck. **Resolution:**
  per-currency **sharded** writer + parallel read-only decision stage, not one global writer; and settle the
  Slice-1-vs-Slice-2 question empirically with GATE 0 (does fsync-coalescing survive sync_commit=off?).
- **BLIND SPOT caught:** the intent channel as an **event-sourced replay log** is a second product — deterministic
  session replay would collapse the noisy realism-soak loop (the ret_acf-ceiling hunt) and give replay-based
  recovery stronger than per-commit durability. Argues for building Slice 2's log properly if pursued.
- **THE ONE THING FIRST:** the **fsync microbenchmark with and without `synchronous_commit=off`** — it kills the
  round in an hour or produces the only number that can justify it on a skewed rig. (= GATE 0.1.)
