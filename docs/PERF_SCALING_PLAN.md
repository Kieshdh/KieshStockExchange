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
  already-coded BracketBatch (A1b/A1c). _(pending — harvest then append + feed the ultraplan brief §8)_

## 11. RESUME STATE (compaction handoff — read this to pick up)
**Big picture:** realism work is essentially DONE/shippable (foundation + system-A default-off, user "looks
good", composite 60+, conservation clean). Tonight = PERF. An **Ultraplan is running on the web** (kicked off
via `/ultraplan`) to return a PATCH for batching the advanced/arb entry route; scope + runbook in
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
