# Performance & Scaling Plan — engine throughput for more bots/money/volume

**Created 2026-06-18 (autonomous overnight perf round). Branch `feature/bot-market-realism-v2`.**
Goal: let the market sustain **more bots + more money + more frequent trading** (→ more volume, especially the
thin EUR books) without the engine choking. **Tonight = LOCAL profiling soaks + config/Postgres lever
measurement only. NO code implementation tonight — structural fixes are reserved for an Ultraplan (see §7).**

This plan was built with the LLM council (5 advisors, 2026-06-18) and the prior perf work in
[[project_bot_loop_perf]] + `docs/BOT_LOOP_*` + `docs/perf-gate-results.md`.

---

## 1. Current state (the problem)
- Single-threaded bot loop, 1 s tick. `BotScalerService` holds the active-bot cap so tick-work EWMA ≈
  **60 %** of the interval (High 0.70 / Target 0.60 / Low 0.50). Heavier ticks ⇒ fewer active bots.
- After the realism foundation (limit orders ×5 tighter, `MarketProbMult=1.5`, weakened anchors, `RegimeDrift`
  system A), each tick got heavier → cap throttled to **~1,870 bots locally** at **~700 ms/tick**:
  `batch 240 + adv 276 + arb 150 + collect 25 + maint 20` (ms). (`MaxBotCap` default 20,000; `TradeInterval` 1 s.)
- Engine self-profiles: set `Bots:PhaseTimingSeconds=30` → `BotPhase [..cap N..]: <ms>/tick = check + collect +
  batch + adv + arb + recon + maint` every 30 s. `BotEconomy` line logs wealth/injection/arb/house.

## 2. Root cost (write/commit-bound, NOT read-bound)
Reads are served from the in-memory `AccountsCache` (no read win available). The ceiling is **per-COMMIT DB
round-trips**:
- **Plain orders** share ONE commit per tick → cheap (`batch` ≈ 0.6 ms/order on prod).
- **Advanced orders** (stops/brackets/short-opens) + **arb legs** each run their OWN transaction → expensive
  (`adv` ≈ 5 ms/order on prod, ~8× costlier). This is the LLN-defeating tax.
- ⇒ real ceiling ≈ `commits/sec × orders/commit`. The scaler hides this wall behind "fewer active bots."

## 3. ⚠️ CRITICAL CAVEAT — local vs prod (read before trusting any number)
The local soak runs **Postgres in docker** = **commit-latency-skewed** (~5–10 ms per statement round-trip). On
**PROD** (in-network Postgres) the same phases are far cheaper per-order; prod reached a **13.7k cap** after the
Option-B maintenance-offload work. Therefore:
- **`synchronous_commit=off` will look like a massive win LOCALLY but mostly fixes docker, not prod.** Treat it
  as a measurement/iteration-speed lever, and a *durability* decision for prod (see open Q).
- **Only `ms-per-order` and `round-trips-per-order` transfer to prod.** Per-tick ms and plateau-cap are
  local-rig artifacts. Normalize against a measured single-statement round-trip baseline.
- `arb 150 ms` locally is likely a round-trip mirage (5-bot cohort × many tx) — deprioritize vs prod.
- **Don't ship anything tonight** — you can't validate a prod gain on a latency-skewed rig. Gather data → Ultraplan.

## 4. Prior perf work already SHIPPED (do not redo) — see [[project_bot_loop_perf]]
- **Option B** (commit `bfac7c9`, prod): moved periodic maintenance (LogSnapshot/prune/asset-reload) OFF the
  scaler-measured tick → prod cap **3.8k → 13.7k**. The maintenance crater is gone (now in `maint`, off-EWMA).
- **BatchArms** (`ec3cf81`, flag `Bots:Advanced:BatchArms`, default OFF): batches stop/trailing ARM route
  (pre-reserve + one bulk-insert tx). Safety gate met on prod; full magnitude pending higher adv/tick. A1b
  (short-opens) / A1c (brackets) DEFERRED — see `docs/BOT_LOOP_A1_ADVANCED_BATCH_BRIEF.md`.
- **C1/C3/C4/C6/C7** (`a5eaddc`, `8bf4bbb`): alloc/query trims (held-set walk, committed-totals precompute,
  de-LINQ prune, dead-query/index deletes). `collect` is now only ~5 % of the tick → not a ceiling lever.
- Worksheet: `docs/BOT_LOOP_PERF_OPTIMIZATIONS_PLAN.md`. Parked DO-NOT-MERGE: `bot-loop-batch-commits` (Option
  B savepoints / relax-fsync) — targeted the local adv mirage.

## 5. Council synthesis (2026-06-18) — what to actually do
- **Stop tuning the throttle.** The throttle exists because every decision blocks on fsync. The real fix is
  **decoupling decision from durability** (intent channel → batched group-commit writer). (First-Principles,
  Expansionist, Outsider all converged.) = the structural 5–10×.
- **Batch the advanced ENTRY route** (extend BatchArms to short-opens/brackets/arb via one pipelined tx/tick)
  = the #1 *transferable* lever, ~2–3× alone, fits shipped architecture.
- **Stagger bots** (phase-offset schedules; may build on the existing per-bot `Lateness`) → fewer actors/tick,
  more realistic, and lets us put more bots on EUR names — cheap, large.
- **Shard per currency/book** (separate connection + serialized engine per currency) → removes EUR/USD
  contention AND raises EUR fill-rate independently → directly fixes thin EUR books.
- **`synchronous_commit=off`** first as a cheap stacking baseline — but per §3 it's a local-skew lever + a prod
  durability decision, not a validated prod win.
- Keep tick interval at 1 s (raising it amortizes commits but cuts trade frequency — fights the volume goal).

## 6. Tonight's 12h experiment ladder (LOCAL, autonomous, config/Postgres only)
**Per-run capture (every arm):** `BotPhase` ms/tick per phase (mean + p95 over a STEADY window, discard first
90 s warm-up); scaler **plateau cap** + mean `LastLoadFraction` (~0.60); **orders/tick**; **derived ms-per-order
for batch & adv** (transferable metric). Conservation/CK/shortfall must stay 0 (kill-gate).
**Noise control:** run arms **in parallel** (2 servers max, same template clone, same wall-clock — cancels most
run-to-run variance). ≥12 min steady per arm. **Trust only deltas ≥20 %**; re-run the winner once to confirm.
Baseline config = the realism foundation the user wants to scale (×5 closeness, MarketProbMult 1.5, weak
anchors, RegimeDrift on).

| # | experiment | arms (parallel) | measures | keep/kill |
|---|-----------|-----------------|----------|-----------|
| 1 | **Baseline** (30m solo) | foundation+A | plateau cap, phase breakdown, ms/order | control for all below |
| 2 | **synchronous_commit=off** | baseline vs sc=off (docker) | cap, batch+adv ms, ms/order | keep if cap ↑≥20% or batch+adv ↓≥20% (note: local-skew lever) |
| 3 | **BatchArms on** | baseline vs `BatchArms=true` | adv ms, ms/order, recon/CK | keep if adv ↓≥20%, no CK/recon regression |
| 4 | **MaxPerTick 50→100** | baseline vs 100 | orders/tick, adv ms | keep if orders/tick ↑ & adv sublinear; kill if adv ≫ batch |
| 5 | **Stack winners** (15m solo) | winners combined | cap vs baseline | confirm additive |
| 6 | **MaxBotCap headroom** (15m solo) | winners + cap raised | new plateau, NEW dominant phase | the NEW dominant phase = the Ultraplan target |

(Postgres tuning to try on the §2 winner: `wal_writer_delay=200ms`, `commit_delay=100`, `full_page_writes=off`
— docker-only, durability-relaxed, for measurement.)

---

## 7. >>> FIXES FOR ULTRAPLAN (implementation later, NOT tonight) <<<
Ranked by transferable payoff. Each needs a careful Ultraplan (conservation/ReservationAuditor gates;
flag-gated; byte-identical-off where possible):
1. **Batch the advanced ENTRY route** (A1b short-opens + A1c brackets + arb legs) — extend the shipped BatchArms
   pre-reserve+bulk-insert pattern to one pipelined tx/tick. Biggest *safe, in-architecture* win. CAUTION:
   advanced orders run outside the matcher's locked region with their own gates — a shared tx changes
   gate/commit/conservation semantics; design + gate carefully.
2. **Decision/commit decoupling** (the structural 5–10×) — bots produce order *intents* into an in-memory
   channel during a parallel read-only decision stage; a dedicated writer drains + **group-commits** (N orders /
   one fsync) on a pipelined Npgsql connection. Decouples CPU from fsync stalls; advanced/arb stop being
   one-tx-each. Durability model changes (write-behind) — needs explicit design.
3. **Per-currency / per-book engine sharding** — separate connection + serialized engine per currency. Removes
   EUR/USD contention, parallelizes I/O, and lets EUR fill-rate scale independently (fixes thin EUR).
4. **Bot staggering** — phase-offset per-bot act schedules (every N s, offset by id) instead of all-every-tick;
   likely extends the existing `Lateness` field. Cheapest big win; also a realism win; rebalances load.
5. **Postgres commit tuning bake** (`synchronous_commit=off` / `commit_delay`) — only if §6.2 validates AND the
   prod durability tradeoff (lose <1 s of writes on crash) is acceptable for a simulation.

## 8. >>> OPEN QUESTIONS FOR KIESH (read when you're back) <<<
1. **Scaling target: PROD capacity, or local-soak iteration speed?** They're different optimizations — the
   council says prod *filled-orders/sec* is the real goal and local is just the measurement rig. Most levers
   below are ranked for prod transfer; confirm so I don't over-optimize the docker rig.
2. **`synchronous_commit=off` in PROD — acceptable?** It's the cheapest throughput lever but trades durability
   (a crash loses the last <1 s of committed writes). For a simulation that's probably fine — confirm and I'll
   treat it as a bake candidate rather than just a measurement knob.
3. **Bot staggering — OK to change bots from "all act every tick" to phase-offset schedules?** Big perf win +
   more realistic, but it's a behavior change (per-name flow cadence changes). Should it build on `Lateness`?
4. **Appetite for the structural decision/commit decoupling (§7.2)?** It's the 5–10× but a real architecture
   change (write-behind persistence). Worth an Ultraplan, or do you prefer to bank the safe in-architecture
   wins (§7.1 batch-entry + §7.4 stagger) first?
5. **EUR liquidity fix preference:** (a) more bots assigned to EUR names (seed/`Tools`), (b) per-currency
   sharding (§7.3), or (c) damp the one-way EUR→USD FX drain (the arb/FX desk converts only EUR→USD, draining
   EUR books). I lean (a)+(c) as cheapest; (b) is the structural version.
6. **"More money" via shortening the 1 h cash-injection cycle (it's a hard-coded const) — how aggressive?**
   (e.g. 1 h → 15 m, or raise per-bot amount). This raises load, so it's gated on the perf headroom we find.

## 9. Proactive improvement ideas (bonus — flag if you want any)
- **Make the injection interval + per-bot amount config** (currently a `const TimeSpan.FromHours(1)`), so
  "more money" is a runtime dial, not a recompile.
- **Expose `MaxPerTick`, `BatchArms`, scaler target as live admin-dashboard controls** for soak iteration.
- **Add `ms-per-order` (batch & adv) directly to the `BotPhase` log line** so the transferable metric is
  first-class, not derived.
- **A "perf soak" harness mode** that auto-sweeps a lever across arms and emits a comparison table (codifies §6).
- **Per-currency volume to the bot dashboard** (surfaces the thin-EUR problem live).
- **Group-commit metric**: log commits/sec + orders/commit so the real ceiling is directly visible.

## 10. Live findings log (filled as experiments run tonight)
- **System-A A/B (pre-perf, foundation, 50m, A-on vs A-off control):** composite 60.3→**61.8**, clustering
  (absret_acf) 0.156→**0.196**, ret_acf_lag1 −0.370→−0.375 (~flat), R² 0.316→0.311 (~flat), conservation clean.
  ⇒ the FOUNDATION (×5 + MarketProbMult 1.5 + weak anchors) is the big realism win (composite 60+, ret_acf up
  from −0.44); RegimeDrift adds clustering + visible wander (user "looks good") but doesn't move aggregate
  linearity. System A & foundation both look shippable. NOTE: cap throttled to ~1,870 → the perf problem below.
- **Perf Round 1 (DONE, 25m parallel, foundation+A):** baseline vs `BatchArms=on`. **BatchArms = KEEP:**
  adv ms/order **17.7→10.3 (−42%)**, cap 669→764 (+14%), orders/tick 38→44, adv/tick 8.7→10.1, tot ~627ms
  both, conservation clean. ⇒ fold BatchArms into the working baseline. Caveats: (a) caps are low/contended —
  both arms shared ONE docker Postgres in parallel (halves absolute cap; relative delta valid); (b) `batch`
  = 6.96 ms/order locally vs ~0.6 on prod = the ~10× docker commit-latency skew (§3) confirmed; (c) `arb`
  co-dominant (156–171 ms) = the 5-bot cohort's per-tx commits.
- **Perf Round 2 (DONE, SOLO, foundation+A+BatchArms):** true un-contended **cap ≈ 893** (range 521–1331,
  noisy), tot 646 ms = **batch 310 + arb 171 + adv 118** + collect 18 + maint 21. batch ms/order 6.12, adv
  ms/order 10.0. ⇒ after BatchArms, the LOCAL ceiling is **batch (plain submit/match/settle) + arb**, both
  commit/round-trip-bound (batch 6.12 ms/order vs ~0.6 prod = the docker skew). On PROD batch is cheap, so the
  prod ceiling will be adv+arb count — confirms §7.1 (batch advanced/arb entry) is the transferable lever.
- **Perf Round 3 (DONE, SOLO, `synchronous_commit=off`):** **BIG local win — cap 893→1424 (+59%)**, orders/tick
  51→80, batch ms/order 6.12→4.58 (−25%), adv ms/order 10.0→5.54 (−45%), arb 171→105 (−39%), tot ~630ms.
  ⇒ the local engine is **heavily FSYNC-bound**. **PROD win will be smaller** (in-network, fast disk — §3) and
  it's a durability tradeoff (§8 Q2). **DECISION: keep sc=off SET on the docker instance for the rest of
  tonight's throwaway soaks** (faster iteration + closer to prod's cheap-commit regime). ⚠️ REVERT
  (`ALTER SYSTEM RESET synchronous_commit; SELECT pg_reload_conf();`) before any durability-sensitive run.
  New dominant phase = `batch` 365 ms (plain submit/match/settle) = the structural Ultraplan target (§7.1/§7.2).
- **Perf Round 4 (DONE, SOLO, sc=off + `full_page_writes=off` + `commit_delay=50`):** cap 1424→**1539 (+8%)**,
  batch ms/order 4.58→4.19 (−9%) — within noise; minor add-on to sc=off. Keep (harmless for soaks; revert with
  sc=off before prod). **Config levers now exhausted** — the ceiling is `batch` (plain submit/match/settle, no
  config knob) → structural Ultraplan (§7).
- **MaxPerTick: SKIPPED (low value).** adv/tick ≈ 18 < the 50 cap → not binding; lowering it only sheds ~5.5
  ms/advanced-order at the cost of fewer stops/brackets (behavior/realism tradeoff). Note for Ultraplan, not a
  tonight lever.
- **CONFIG-LEVER SUMMARY (local, foundation+A):** baseline cap 893 → BatchArms (already in) → +sc=off **1424
  (+59%)** → +fpw/commit_delay **1539 (+72% vs 893)**. All conservation-clean. The big lever is sc=off (fsync).
  The rest of the headroom needs the structural §7 fixes. ⚠️ docker now has sc=off+fpw=off+commit_delay SET —
  revert before any durability-sensitive run.
- **Perf Round 5 = 4h confirmation soak: STARTED then STOPPED** at ~30 min — superseded by the user's pivot to
  the ultraplan + the BracketBatch validation A/B (below). Not harvested.
- **BracketBatch validation A/B (RUNNING, ~90m parallel):** `kse_soak_bboff` (BatchArms on, BracketBatch OFF,
  :5080) vs `kse_soak_bbon` (BatchArms on, BracketBatch ON, :5081). Foundation+A, sc=off+fpw+commit_delay.
  Measures: ConservationProbe/CK/ReservationAuditor clean? adv ms/order drop? = the pre-evidence for baking the
  already-coded BracketBatch (A1b/A1c). **RESULT (90m parallel): CONSERVATION CLEAN BOTH ARMS (0 suspect lines —
  no CK/ConservationProbe/ReservationAuditor/shortfall/phantom/unhandled).** Perf: cap 740→763 (+3%, noise),
  adv ms/order 7.36→7.27 (~flat), orders/tick 47→50. ⇒ **BracketBatch is SAFE to bake** (the key blocker
  cleared); local perf delta modest because `adv` is a minor phase here (~9 adv/tick, brackets/short-opens a
  minority) + local commit-skew — magnitude scales with adv/tick → bigger on prod (BatchArms-style caveat).
  Strong green light for the ultraplan PR's bake decision.

## 11. RESUME STATE (⚠️ mid-night snapshot — CURRENT state is §13–15; read those first)
**CURRENT (2026-06-18, post-everything):** all decisions resolved (§15). BAKED: BatchArms (`adc2f63`),
realism foundation+system-A + injection-config (`f70070c`). Batch ultraplan validated & DONE (§13). Docker PG
REVERTED to safe defaults. Nothing running (no soaks/servers/monitors). Next = the **per-currency sharding +
staggering ultraplan** (`docs/ultraplan-prompt-per-currency-sharding.md`); pending = sc=off-prod deploy +
EUR-seed Tools task. The snapshot below is from mid-night and is superseded.

---
**[mid-night snapshot]** realism work essentially DONE/shippable; an Ultraplan was running on the web for the
batch advanced/arb entry route (now validated — see §13). Original batch handoff:
`docs/ultraplan-prompt-batch-advanced-arb-entry.md`.

**In-flight right now:**
- BracketBatch validation A/B soaking (DBs `kse_soak_bboff` :5080 / `kse_soak_bbon` :5081), ~90 min.
- Generic crash-watch monitor running (follows newest `soakP-*.log`).
- ⚠️ **Docker Postgres has `synchronous_commit=off` + `full_page_writes=off` + `commit_delay=50` SET** for
  tonight's throwaway soaks. REVERT before any durability-sensitive/prod-like run:
  `docker exec kieshstockexchange-postgres-1 psql -U kse -d postgres -c "ALTER SYSTEM RESET synchronous_commit;"`
  (+ same for `full_page_writes`, `commit_delay`) then `SELECT pg_reload_conf();`
- Client `KieshStockExchange/Resources/Raw/appsettings.json` still repointed to localhost:5080 (uncommitted) —
  revert to the duckdns prod URL before any prod client build.

**NEXT ACTIONS (in order):**
1. Harvest the BracketBatch validation A/B → conservation (must be clean) + adv ms/order delta → append to §10
   + the ultraplan brief §8 "Soak evidence".
2. **When the ultraplan PATCH arrives:** `git apply --check` (one-shot clean or reject) → apply → build server
   (`dotnet build KieshStockExchange.Server/...`) + MAUI client → `dotnet test` (full suite) → soak per the
   runbook in the ultraplan doc §5 → **bake only what stays ConservationProbe=0 / CK=0 / ReservationAuditor in
   tolerance AND shows adv/arb ms-per-order drop.** Arb-leg batching (if in patch) stays flag-default-off.
3. Keep `Bots:Advanced:MaxPerTick` as the instant fallback. Structural decoupling is OUT (future ultraplan).

**Canonical soak launch (lowercase DB names; ABSOLUTE script path — relative `.\scripts` fails if cwd drifts):**
```
$env:Bots__DecisionDistanceMult='0.2'; $env:Bots__Tiers__CloseProb='0.85'; $env:Bots__Tiers__MidProb='0.10';
$env:Bots__MarketProbMult='1.5'; $env:Bots__ValueAnchor__AbsoluteCapMax='0.20'; $env:Bots__ValueAnchor__Strength='0.40';
$env:Bots__ValueAnchor__Scale='0.12'; $env:Bots__RecentAnchor__Strength='0.10'; $env:Bots__Sentiment__RegimeDrift__Enabled='true';
$env:Bots__Advanced__BatchArms='true'; $env:Bots__PhaseTimingSeconds='30';  # +Bots__Advanced__BracketBatch / arb flag per experiment
& 'C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange\scripts\kse-balance-soak-p.ps1' -Db <lowercase> -Tmpl kse_soak_seed -Port 5080 -Minutes N -SampleEverySec 120 -Note "..."
```
**BotPhase harvest:** python regex on `cap (\d+)\]: ...ms/tick = check .. + collect .. + batch .. + adv .. + arb
.. + recon .. + maint ..; (\d+) orders + (..) adv/tick`, mean over the steady window (drop first ⅓). Compare
arms by plateau cap + per-phase ms + ms-per-order. Parallel A/B halves absolute cap (shared docker PG) but the
relative delta is valid; trust deltas ≥20%; conservation/CK=0 is the kill-gate.

**Config knobs shipped this session (all flag-gated default-off, in git):** `Bots:Sentiment:SlowRingDamp`,
`Bots:SmoothedPriceHalfLifeSec`, `Bots:Sentiment:RegimeDrift:*` (system A), `Bots:MarketProbMult`, dashboard
failure date + Clear button. Foundation realism config (the user likes) = the env block above; NOT baked into
appsettings (passed via env for soaks). Open decisions for the user = §8.

## 12. ULTRAPLAN PATCH validation (2026-06-18)
Patch `advanced-batch-bake.patch` applied clean + committed `760c955` (flags default-OFF). **3 sandbox-compile
gaps fixed** (author had no SDK): missing `using` (ArbBatchLegs test), missing `GetOrderById` mock stub
(MarketShort test), missing arb-batch client stubs (`ApiOrderEntryClient`). **H0 gate PASSED: 177/177 tests +
MAUI client build clean.**
- **Patched BracketBatch on/off (90m, high load, sc=off uncontended): CONSERVATION CLEAN BOTH at adv/tick ≈ 49,
  451 orders/tick** (a strong safety validation — the determinism fix holds at prod-like load). Perf FLAT
  (cap 7843→7734, adv ms/order 1.21→1.19) — uncontended machine ⇒ `adv` already ~1.2 ms/order ⇒ no batching
  headroom to measure. ⇒ per the user's gate ("bake only with an ms-per-order drop"), **NOT baked** — the
  drop isn't demonstrable when commits are cheap.
- **Docker PG REVERTED to durability-safe defaults** (synchronous_commit=on, full_page_writes=on,
  commit_delay=0) — the §11 caveat is cleared.
- **Commit-bound A/B RUNNING (sc=on, 60m, `kse_soak_bb3off`/`bb3on`):** measures BracketBatch's adv ms/order
  drop in the regime where it matters (expensive commits). If it shows the drop + stays conservation-clean →
  bake BracketBatch (+ BatchArms) default-on. If still flat → BracketBatch is SAFE but no measurable LOCAL win;
  recommend baking on prod (where it was the measured #1 lever) or defer to user. _(pending)_
- Arb-leg `Bots:Arbitrage:BatchLegs` stays default-off (stretch; one soak ≠ bake for new sequential code).

## 13. PATCH VALIDATION — final result + bake decision (2026-06-18)
**Commit-bound A/B (sc=on, expensive commits, BatchArms on both arms): BracketBatch ON gives ZERO adv ms/order
drop (2.95→2.95), cap flat, conservation 0.** Same flat result uncontended (1.21→1.19). ⇒ **KEY STRUCTURAL
FINDING:** batching the *entry* of MATCHED orders (short-opens/brackets) doesn't help — the advanced-order cost
is the **match + settle group-tx** (per-`(stock,currency)`), and with ~50 stocks few orders share a group, so
entry-batching merges almost nothing. BatchArms won (−42%) only because **arms are reserve+insert, NO match** →
all arms collapse into ONE insert tx regardless of stock.
- **BAKED: `Bots:Advanced:BatchArms` = true** (in-scope, conservation-clean across hours, measured −42% adv/order,
  prod-gate already met). The one real, gate-meeting win.
- **NOT baked: `Bots:Advanced:BracketBatch`** (conservation-SAFE — keep the patch's determinism fix + tests as
  hardening — but no measurable throughput win; matching-bound). Flag stays default-off.
- **Arb-leg `Bots:Arbitrage:BatchLegs`: SKIPPED** (kept default-off). Arb legs also MATCH (market buy/sell), so
  by the same matching-bound logic entry-batching won't help; + 5-bot cohort = negligible. Not worth a soak.
- **NEXT-LEVER REFINEMENT (for a future ultraplan):** the real advanced/matched-order cost is the per-
  `(stock,currency)` match+settle group transaction, NOT entry-inserts. So §7.1 (batch the advanced entry) is
  largely SPENT (BatchArms got the insert-only win; matched-order entry-batching is a no-op). The remaining
  throughput lever is reducing/coalescing the **group-tx commits** themselves = §7.2 decision/commit decoupling
  (group-commit pipeline) or per-currency sharding (§7.3) — confirmed as the next frontier.

## 14. FINAL confirmation soak (3h, baked end-state config)
Config: BatchArms baked-on + foundation (×5 closeness, MarketProbMult 1.5, weak anchors) + system-A RegimeDrift,
sc=on (durability-safe). **Conservation CLEAN over 3h (0 suspect lines); cap ramped to a stable ~6.7–9k; drift
−2.0%/3h (within the ≤5%/4h budget); no runaway.** ⇒ the night's baked end state is validated stable.
NIGHT SUMMARY: BatchArms baked (the perf win), realism foundation+A validated (conservation-clean, drift
bounded), patch kept as hardening, next-lever = match/settle group-tx coalescing (§7.2/§7.3). Docker PG on
durability-safe defaults. Realism foundation config is env-passed (NOT baked into appsettings) — see §11 block.

## 15. Open decisions — RESOLVED (2026-06-18) + follow-ups
User answered all 7 §8 decisions:
1. **Scaling target = PROD capacity** (local = measurement rig; judge ms/round-trips-per-order).
2. **Bake realism (foundation + system-A) → DONE** (commit `f70070c`): DecisionDistanceMult 0.2, MarketProbMult
   1.5, Tiers 0.85/0.10, ValueAnchor Strength 0.40/Scale 0.12/AbsoluteCapMax 0.20, RecentAnchor 0.10,
   RegimeDrift on. (Was env-only; now prod defaults. Validated conservation-clean + drift-bounded.)
3. **Injection → config + 30m → DONE** (`f70070c`): `Bots:CashInjection:IntervalMinutes` (const 1h → config,
   default 30m) for more buying/volume.
4. **`synchronous_commit=off` on PROD = YES → PENDING DEPLOY** (sim, <1s loss acceptable). Apply on the Netcup
   prod box: `docker exec <pg> psql -U kse -d postgres -c "ALTER SYSTEM SET synchronous_commit='off';"` then
   `SELECT pg_reload_conf();`. (NOT a repo change.)
5. **Next perf lever = per-currency SHARDING → ultraplan handoff written:**
   `docs/ultraplan-prompt-per-currency-sharding.md` (folds in bot staggering as Slice 1).
6. **Bot staggering = YES (build on Lateness) → folded into the sharding ultraplan (Slice 1, ship first).**
7. **EUR liquidity = more bots on EUR (seed) → PENDING Tools task:** rebalance bot currency/watchlist
   allocation toward EUR names in `Tools/GenerateAIUsers.py` + reseed (out of tonight's scope; user-prioritised).
   (Sharding §5 also helps EUR structurally; the FX EUR→USD drain is the deeper cause, not chosen for now.)

**Net banked tonight:** BatchArms (`adc2f63`) + realism foundation+system-A + injection-config (`f70070c`),
all conservation-validated. **Pending:** sc=off prod deploy (1 command), the sharding+staggering ultraplan,
the EUR-seed Tools task.

## 16. SHARDING/STAGGERING round (2026-06-18 day) — patch landed, soaks run
Ultraplan patch (`bot-realism-v2.patch`) applied + committed **`2ea9e78`** (both flags default-off, byte-identical;
H0: 187/187 tests incl. StaggerDue determinism + per-currency gate-split equivalence suites; server+tests+MAUI all
build). Two slices, both default-off.
- **Slice 1 — bot staggering (`Bots:Staggering:{Enabled,Slots}`), `StaggerDue(id,tick,slots)` pure fn:**
  90m parallel A/B (Slots4 on :5081 vs off control :5080, baked-realism env, sc=on). **CONSERVATION CLEAN both arms
  (CK/CONS/ERR/shortfall=0 throughout, no runaway: beyond50=0).** **Perf WIN:** equilibrium cap OFF 1967 / ON 2508
  tail-mean (**+27%**), and still climbing at 90m — last-sample cap 3430→**5192 (+51%)** — at the SAME ~637 ms
  setpoint (the load-cut converts to headroom, exactly the hypothesis). Drift bounded both (avg −0.30%/−0.63%,
  medianAbs ~1%, no beyond50). **Realism (r4 scorer, 75m window):** composite OFF 70.9 / ON 66.9; ret_acf_lag1
  −0.336 / −0.378; clustering (absret_acf_lag1) 0.158 / **0.240** (better); has_wick 86% / 81%. ⇒ Slots4 buys big
  headroom but nudges ret_acf ~0.04 worse + composite −4 (fewer actors/min → fewer wicks + marginally more
  over-mean-reversion; within rig noise + at the already-shipped −0.37 level, but directionally real; arms also ran
  at different caps = a confound). **BAKED `Bots:Staggering:{Enabled:true, Slots:2}`** (user-approved Slots=2 over
  Slots=4: gentler per-minute cadence, ~half the headroom, minimizes the realism perturbation). The 90m gym
  confirmation soak validates the baked Slots=2 config (conservation + realism in tolerance); revert if realism
  regresses.
- **Slice 2 — per-currency group-gate (`Db:PerCurrencyGroupGates`):** 60m A/B done. **NO BAKE.** Conservation clean
  both arms; equilibrium cap flat (1236 vs 1237); EUR fill +3.8% trades / +4.5% vol, USD +1.9% (all under the ≥20%
  trust threshold = noise). Confirms §13/history (group concurrency 24→40 was marginal). Flag stays default-off,
  available. Real EUR fix = seed rebalance / FX-drain, not this throttle.

## 17. NEXT ULTRAPLAN — decision/commit decoupling, GATE 0 result (2026-06-18 day)
Handoff `docs/ultraplan-prompt-decision-commit-decoupling.md` (council-reviewed). Gate-0 patch
(`gate0-commit-decoupling.patch`) = instrumentation only: `EngineCommitMetrics` root-commit counter + commits/sec
& round-trips/order on the BotPhase line + `GroupCommitFsyncMicrobench` test. Additive, default-off (rides
`PhaseTimingSeconds>0`), byte-identical. **H0: 188/188 tests + full-stack build clean.**
- **GATE 0.1 fsync microbench (docker PG, N=2000, one connection) = PROCEED:** B/A (one-commit vs separate-commit)
  speedup **8.2× at sync_commit=on, 2.3× at sync_commit=off**; per-commit path A gets 3.7× from sc=off alone
  (394→1475 commits/sec). ⇒ **coalescing is real AND survives sc=off (2.3×, not ~1×) → Slice 1 justified**, but the
  PROD-regime win (sc=off already approved) is **modest ~2.3×**, not 8× (First-Principles was right that sc=off eats
  most of the fsync cost). **Bonus:** Mode C (pipelined `NpgsqlBatch`) = 68× even at sc=off = a *network*
  round-trip win, not fsync ⇒ statement-pipelining inside the group-tx may beat commit-coalescing alone; flag to the
  Slice-1 design. Baseline (30m): steady 51 commits/sec, 0.30 round-trips/order, cap ~5000.

## 18. SLICE-1 group-commit writer — landed `6c68a40`, A/B = NO BAKE (coalescing not firing)
Patch `slice1-group-commit.patch` (per-currency sharded group-commit writer, `Db:GroupCommit` default-off) applied
+ committed `6c68a40` (one 1-line accessibility fix on the crash-test `Frame` type; 191/191 tests incl. equivalence
+ 2 crash tests reconciling the durable DB alone; full-stack build). **A/B (off vs on, ~12m, conservation CLEAN
both, 0 suspect lines): NO BAKE.** ON shows LOWER cap (~660–960 vs ~870–1160), HIGHER `batch` ms (350–420 vs
200–296 — serial per-currency shard loses the OFF path's 24-way parallel per-group commit), and **commits/tick ≈ 38
vs 45 — NOT the ~3–5 coalescing predicts.** ⇒ **the savepoint-nesting coalescing the patch relies on is NOT firing
in the live engine**: the inner per-group `BeginTransactionAsync` under the shard's `RunInTransactionAsync` is still
producing root commits, not savepoint-nested releases. The equivalence test's mock `RunInTransactionAsync` ran the
action inline (didn't model commit-collapse) and the crash test used a hand-rolled nesting fake — neither caught
that the real `PgDBService` AsyncLocal path doesn't nest here. **Flag stays default-off.** Group-commit is
conservation-SAFE but ineffective as written. **Next iteration must FIX/verify the nesting (instrument commits/tick,
assert it drops) OR pivot per First-Principles to Slice 2: keep the proven parallel per-group writer, move the
DECISION stage off the critical path (parallel read-only decisions → intent handoff), don't touch durability.**

## 18b. ITERATION-2 parallel read-only decision stage — KILLED by the free Amdahl gate (NOT applied)
Iteration-2 ultraplan root-caused Slice-1's non-coalescing precisely: `_ambient` is a static `AsyncLocal<TxScope?>`
that only flows back to the caller when `BeginTransactionAsync` finishes SYNCHRONOUSLY; Slice-1's parallel
`Task.WhenAll` shard-open suspends → ambient lost on resume → every inner per-group tx roots (commits/tick ~38, +
lost 24-way parallelism). Council PIVOTED to (b): parallelize the read-only DECISION stage (`Bots:ParallelDecision`,
flag default-off), feed the unchanged writer, determinism as the gate (ships `BotParallelDecisionDeterminismTests`;
`AiBotContext` caches → `ConcurrentDictionary`). **KILLED at the runbook's own free Step-3 Amdahl gate WITHOUT
applying:** measured `collect` = **3.9%** of the tick (24ms/627ms) on the live baked baseline; dominant phases are
`batch` 38% + `arb` 29% + `adv` 15% = the WRITER/commit side. Parallelizing a 3.9% phase can lift the cap ≤~4% «
the 20% bake bar. Landing it would also swap hot-path `Dictionary`→`ConcurrentDictionary` for zero payoff
(net-negative). **Not applied/soaked/baked; patch kept as a documented artifact.** ⇒ **within-tick SOFTWARE levers
are now EXHAUSTED** (entry-batch spent, commit-coalesce doesn't fire + loses parallelism, decision-parallelism
Amdahl-capped). Remaining real levers: **(1) `sc=off` on PROD** (approved, pending deploy — microbench 3.7× on the
per-commit path); **(2) cross-process / multi-engine sharding** (true horizontal scale, the §7.3 "real" version);
**(3) the `arb` phase = 29% of the tick for a 5-bot cohort** — suspiciously large, worth a tx-count diagnostic
(each arb leg is its own tx; if it issues many legs/tick that's a concentrated, controlled commit-count target).

## 19. ROUND BAKE SUMMARY (2026-06-18 day)
**Baked:** staggering `Slots=2` (the round's real win — cap headroom, conservation-clean) + Gate-0 commit
instrumentation (`a5f46e7`). **Landed default-off (NOT baked):** per-currency gate (no win), group-commit writer
(coalescing not firing). **NOT applied (killed by free Amdahl gate):** iteration-2 parallel decision stage
(collect=3.9%, §18b). **CONCLUSION: within-tick software levers EXHAUSTED** — the engine ceiling is the
`batch`+`arb` commit/writer side (67% of the tick). **Next real levers (next ultraplan / ops):** (1) `sc=off`
PROD deploy (1 command, approved); (2) cross-process/multi-engine sharding; (3) `arb`-29% tx-count diagnostic.
**Pending:** gym confirmation soak of the baked Slots=2 end-state (running); EUR-seed Tools task.
