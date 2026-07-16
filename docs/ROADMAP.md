# ROADMAP.md — KieshStockExchange V1 wrap-up: plan · levers · test/ship playbook

The single forward-looking roadmap. **Companion to `explainers/BOT_MECHANICS.md`** (that file = *targets + how the mechanisms work*; this file = *what's left to do, every lever's status, and how we test + ship*). Session history lives in the plan logs (`~/.claude/plans/`), not here. **Update this file whenever work is planned / started / finished, or a lever changes status.**

## 0. The wrap-up frame (Kiesh, 2026-07-05)
The market is approaching a very correct state. V1 is **wrapping up** — this is not open-ended research.
- **Sequence (the spine):** finish tuning (*this week*) → speed-ups → **final reseed (one-way)** → final client + server testing → ship to prod → wrap.
- **Priorities:** **P1 = the wrap-up sequence.** **P2 = autonomous market tuning** to lock the correct state (concurrent during testing; *after* the reseed this is appsettings-dials-only, since seed geometry freezes).
- **Guardrail — LIMIT NEW FEATURES.** Tuning + perf/speed-ups + prune only. No new mechanisms. (Reuse/configure existing machinery is fine; a bespoke new lever is not.)
- **Post-V1 (next chapter, out of scope now):** rich mechanism explanations + inner-workings visualizations. `explainers/BOT_MECHANICS.md` stays the compact V1 reference until then.

---

## 1. Phase 1 — Finish tuning (this week)   ·   P1
Config-level realism polish toward the "correct state." Dial what's already built; no new mechanisms. The realism **ship bundle** is defined + built default-off; tuning converges its dial values, then it locks into the final reseed.

**Ship-bundle dials to converge (current branch default → ship value):** `Sentiment:GlobalSigmaMult` 1.0→**2.5** · `Sentiment:RegimeDrift:Strength` 1.0→**0.2** · `RecentAnchor:Strength` 0.10→**0.35** · `ExogShock:Enabled`→**true** + `GlobalCoFire` on (Fraction **0.25**, NotionalFrac **0.15**, GlobalFraction **0.4**, MeanIntervalMinutes **0.5**) · FX-damp (`Fx:Alpha` 0.92→**0.97**, `Amplitude`→**0.002**, `RateBand`→**0.05**). bounce-mid, GeometricBand, ValueAnchor, DipBuy2.0, BuyStopFraction0.45 already baked.

**Open tuning decisions:**
- **ReactionPersistence bake — YES/NO** (Kiesh call). Validated *modest*: corr@10min 0.050→0.075, deeper book, tighter downside, CK=0; does NOT hit the 0.2–0.3 corr target, does NOT fix ret_acf, σ drops (smoother), gain-saturated (1.0≈2.0). Flipping it replaces `Inertia` = a real behavior change. Needs a noise-floor confirm (2 controls, >2σ) + isolation arm before any bake. `REACTION_PERSISTENCE_SPLIT_BAKE_RESULTS.md`.
- **Sector pulse** — default-off; 45m A/B at SF0.5 was null (sentiment swamps intra>cross). Leave off for V1, OR a real `Stock.Sector` column + reseed + UI is the **post-V1** version. `SECTOR_PULSE_PLAN.md`.

---

## 2. Phase 2 — Speed-ups (perf / cap)   ·   P1
Optimizations, not features — in-scope under the freeze. Engine is single-threaded, 1s tick, **commit-bound** (ceiling ≈ commits/sec × orders/commit); the scaler hides the wall by cutting active bots. Local base config currently settles **~1,700–2,300 active** (a ~9× regression from the old 20k-capable A/B, caused by accumulated realism cost). **MaxBotCap = 20,000 stays** (Kiesh); the goal is a higher *sustainable active* count, not a bigger pool.

**Prizes, by ROI:**
| Lever | Prize | Status |
|---|---|---|
| **`synchronous_commit=off` on prod** | cheapest real win (microbench 3.7× per-commit) | **approved, PENDING 1-cmd deploy** — do this |
| **Ultraplan A · short-opens** (Slice 2, match+settle group-tx) | the bulk of the adv ≈**30–45%** cap prize | NOT built — hard/CK-risky; the real adv win |
| **Ultraplan A · buy-stop batch** (Slice 1) | ~**1–4%** commits/sec | **applied to branch, default-off**; validate + bank (§7). Marginal ROI — fold soak into the final test round, don't dedicate one |
| **Ultraplan B · Phase-0 profiler** | measurement (gates all B work) | NOT built — mandatory first step of any B lever |
| **Ultraplan B · arb event-trigger** | reclaim fixed ~100ms arb (but partly a prod round-trip mirage) | NOT built; needs a spread-drift validator |
| **Ultraplan B · memory/GC** (Position column-store, scalar→array, prune `PerceivedPriceDesync` ~64MB) | GC-pressure → tick-time; column-store also a CPU/cache win | NOT built; gate on a Gen2-stall measurement first |

**Settled — do NOT reopen:** config easy-wins SPENT (BatchArms baked; BracketBatch + BatchLegs + per-currency-group-gate + group-commit-slice-1 all tested = no-win — matched-order cost is the per-(stock,ccy) **match+settle** group-tx, not the entry insert). Entry-batching is spent. Parallel-decision killed by an Amdahl gate (`collect` ~4% of tick). **Sparse activation REJECTED** (cuts per-stock volume → empty candles → violates P2). Docker PG-tuning wins were a latency artifact (only sc=off transfers). Cross-process sharding = deferred major rewrite, *measure prod first*.

**Prod hardware (SSH-measured, not in old docs):** Netcup VPS **8 GB RAM / 4 vCPU**; server container ~5.4 GiB (~70%), ~1.5 GB free. ⇒ RAM caps a pool at ~22–24k; the 4 vCPU caps active count. A 16 GB VPS (~$10–25/mo) would beat memory engineering if a bigger pool were ever wanted (moot — pool stays 20k).

**Fleet rotation + special-bot scaling (NEW — Kiesh 2026-07-07; council debate below):** two coupled gaps in the scaler, orthogonal to the estimate/rotator bake — slot AFTER it (the new cohorts make it bite more, since they push the cap below the fleet more often).
1. **Rotate the active fleet = "load all 20k while preserving the scaler" (Kiesh's online-% goal).** When the scaler caps below the ~19.8k fleet, `ApplyActiveBotCap` enables a FIXED first-N (stable dict/id order) → the tail is *permanently dormant* → book staleness (the "rotating attention" fix flagged across the realism arc). Fix: rotate the active *window* through the fleet so the scaler still sets the per-tick active COUNT (load-safe) while every bot cycles through being online over a sweep ⇒ **per-tick online% = cap/fleet, but coverage-over-a-sweep = 100%** (all 20k get airtime). **CRITICAL: advance the offset REGULARLY (per tick / per few ticks), NOT just on cap-change** — the scaler settles to a stable cap, and an advance-on-change-only offset would then freeze the window = no rotation. Sweep cycle = fleet/cap ticks ≈ each bot's decision cadence (fast when cap high / sc=off; gracefully slower under load — vs today's ~18k permanently dead). Deterministic (offset = tick-count-derived) ⇒ A/B-safe from tick 0.
2. **Special bots (arb 5 / MM 12 / rotator 200) — COUNCIL VERDICT 5/5 (2026-07-07): keep the structural cohorts EXEMPT; the "minimum floor" resolves to 100% for MM/arb and to `ParticipationFraction` for the rotator.** Kiesh's "scale-with-a-minimum" instinct was validated but the floor for the structural cohorts = their full size:
   - **MM + arb: MinActive = full (stay exempt), do NOT headcount-scale.** Four converging reasons: (a) death-spiral — throttling makers under load thins the book → more volatility → more load; (b) anti-realistic — real MMs have quoting obligations, last to pull when busy; (c) ~1% of a COMMIT-bound loop = rounding error; (d) MM may be **stock-partitioned** ⇒ rotating it off leaves books unquoted (worse staleness). GATE for any future MM scaling: confirm the MM stock-assignment model first.
   - **Rotator: its scaler IS `ParticipationFraction`** (a continuous per-tick throttle). No second headcount cap (double-valve confounds the EWMA + makes taker-flow load-dependent ⇒ un-A/B-able). If load-responsiveness is wanted, make **PF load-adaptive in production** (fixed during A/B).
   - **★ The real prize = a per-book LIVENESS SCHEDULER** (guarantee every book, esp. thin EUR, is serviced within N ticks) — exemption doesn't guarantee individual-book servicing (70 books/12 makers → stale seconds). Ties to the P2 liveness requirement. Plus an optional unified per-cohort `[floor,size]` participation budget for a dashboard + free future cohorts (correctness/instrumentation win, NOT throughput).
   - **Effort/sequencing:** fleet rotation (item 1) = **S**, ship standalone (A/B-safe, zero special-cohort risk); liveness scheduler / unified budget = **M**, follow-up gated on the MM stock-assignment check. ALL orthogonal to + after the estimate/rotator bake.

---

## 3. Phase 3 — Final reseed (ONE-WAY door)   ·   P1
Fires **once tuning has converged** (it folds runtime dials → 1.0, which shrinks the tuning surface). `/Tools` change authorized for this. Bundle **every seed-level decision, decide together, fire once:**
1. **Prune dead-end levers (#171)** — remove code + tests + config so the seed carries no dead weight. `PRUNE_PROPOSAL.md`.
2. **Fold runtime multipliers into the seed** (`Tools/Config.py` + `Person.py`): `DecisionDistanceMult 0.2`, `MarketProbMult 1.5`, cash-band + per-bot strength mults → **1.0**; Excel becomes the single source of truth. `ultraplan-prompt-multiplier-to-excel.md`.
3. **EUR bot-rebalance** — the freeze-safe **P2 fix**: a *seed allocation* change (more bots watchlist/trade the thin EUR books), not a new mechanism. See §5 P2.
4. **The locked, converged tuning config** (the ship bundle from §1).

**Prune scope (reconcile before firing):**
- **RECOMMEND-prune (5 dead-ends):** `CoMovement`, `SlowRingDamp`, `sentiment-mod-inertia`, `TouchTighten`, `RefillThrottle`.
- **⚠️ Do NOT prune** `SigmaMult` / `GlobalFraction` — an *old* prune list marked them dead, but co-fire now USES them (KEEPERS at ship 2.5 / 0.4). Reconcile the stale list.
- **HOLD (per-lever Kiesh call):** GlobalShock, Elastic anchor, TrendFollower(taker), ReactionPersistence, PerceivedPriceDesync, BearShortStrength, Jumps, MarketMaker cohort, PriceReaction/bubble, SmoothedPrice.

After the reseed, tuning = **appsettings dials only** (seed frozen).

---

## 4. Phase 4 — Final testing round (client + server)   ·   P1  (autonomous tuning = P2, config-only)
Validate the **reseeded** state (test what ships).
- **Server:** a 2h acceptance soak (CK=0 hard gate, drift bounded, no runaway) + **GATE-0 perf** (box sustains cap ≥ ~19k through the co-fire bursts, else lighten co-fire — see §9) + the full gate-set (§8) + **P2 liveness** (`stock_liveness.py` shows no >15s-gap books, thin EUR included).
- **Client:** the UI battery — tasks **#131–140** (auth, market/watchlist, trade page, portfolio, account/funds, admin/bot dashboard, notifications, adversarial, resilience, sign-off). Point the client at the tested arm; revert BaseUrl to the duckdns prod URL before a prod client build.
- **★ Post-deploy monitor (Kiesh, STANDING — every prod deploy):** watch the **ACTIVE BOT COUNT continuously** (the `Scaler: ActiveBotCap` log line / dashboard `OnlineBotCount`) alongside the CK=0 heartbeat. It's the scaler's throughput/health signal: a sustained drop after a deploy = the realism/cohort cost over-pressuring the commit-bound loop (scaler trading fleet breadth for cohort cost). **Post-fleet-rotation, a low count = a slower sweep, NOT dead bots** — read it as throughput, not coverage.

---

## 5. Phase 5 — Ship to prod + wrap   ·   P1
The final ship bundles the **reseed** (§3), so it uses the **reseed deploy path**, not the no-reseed config bake. Runbook: `SHIP_RUNBOOK.md` (adapt to the reseed variant: pg_dump → merge to master → migrate if schema-touched → nuke+reseed → **CK=0 hard gate on live prod** → 2h scorer confirm; rollback = redeploy `1d3fdd3`, restore dump). Details + box in §9.

**P2 — empty candles (thin-book liveness):** *not* solvable by active-bot count — **proven** (3 DBs, arithmetic floor: a book with <(window/15s) trades can't fill 15s buckets; extra bots dilute onto liquid USD books; +14% bots bought ~4pp). Freeze-safe cures, no new mechanism: **(a) EUR bot-rebalance in the final reseed** (more bots trade the thin books) + optionally **(b) enable the already-coded `MarketMaker` house cohort** on thin books. Probe: `stock_liveness.py`. (Descope to primary/USD books is the fallback — thin EUR ~6 tx/min is a realistic secondary listing.)

---

## 6. Post-V1 (next chapter — out of scope now)
Mechanism explanations + inner-workings **visualizations**. Deferred *features* (all new — frozen for V1): order-**SIZE**/volume phase-2 (conviction→trade size; the missing leg of direction×taker×size, blocks the trade-size-distribution + price-impact scorecard rows), real `Stock.Sector` column + UI, jump+anchor fat-tail combo (kurtosis→10), heterogeneous-horizon/latency tiers, and any core-matching-engine change to break the ret_acf ceiling (explicitly discouraged).

---

## 7. Lever inventory — master status
Cross-references `explainers/BOT_MECHANICS.md` §2. Legend: **BAKED** (on in ship config) · **OFF** (built, validated, default-off) · **NOT** (designed only) · **DEAD** (tested no-win, prune candidate).

### Realism
| Lever (`Bots:…`) | Status | Note |
|---|---|---|
| `BounceReference=mid` | **BAKED** (branch; prod-pending) | ret_acf CLOSE −0.43→−0.17, the one clean win |
| `GeometricBand` + `ValueAnchor:AbsoluteCapMax=2.0` | **BAKED** (prod) | ×3/÷3 elastic band |
| `ValueAnchor` base (Str0.40/Scale0.12, CapFromSeed) | **BAKED** (prod) | restoring force |
| `DipBuyStrength=2.0` | **BAKED** (prod) | down-drift cure |
| `Sentiment:RegimeDrift` | **BAKED** (Str→ship 0.2) | per-stock character; ⚠️ `random-walk-sentiment-plan.md` says "not impl" = STALE |
| `RecentAnchor` | **BAKED** (Str→ship 0.35) | primary >10%-move damper |
| `Sentiment:GlobalSigmaMult` | **BAKED at ship** (→2.5) | correlation lever (shared²/idio²) |
| `ExogShock:GlobalCoFire` (+ GlobalFraction/Fraction/NotionalFrac) | **BAKED at ship** | the headline correlation mechanism (shared taker burst); GATE-0 heavy phase |
| `Fx:{Alpha,Amplitude,RateBand}` | **BAKED at ship** | mean-reverting bounded FX |
| `Advanced:BuyStopFraction=0.45` | **BAKED** (prod) | up/down taker symmetry |
| `ValueAnchor:Elastic` / `:Adaptive` | **OFF** | modest / no-win (cap not binding) |
| `BearShortStrength` | **OFF** | sell-side symmetry, drift-neutral@1.0 |
| `Sentiment:GlobalShock` | **OFF** | down-biased "elevator down"; corr capped ~0.08 |
| `ExogShock` per-stock chaser | **OFF** | flow lever, inherent down-drift; co-fire is the only ship flow |
| `ExogShock:Sector*` | **OFF** (weak/null) | SF0.5 null; real version = post-V1 Stock.Sector |
| `Jumps` (fat-tail) | **OFF** | no-bake alone; held as igniter |
| `Imbalance:ReactionPersistence` (+TakerCoupling) | **OFF — bake call pending** | modest corr+book+downside; gain-saturated |
| `PerceivedPriceDesync` | **OFF** | cleanest ret_acf lever but sub-gate (~64MB if enabled → prune/mem candidate) |
| `MarketMaker` house cohort | **OFF** | inert alone; **reuse candidate for P2 thin-book liquidity** |
| `TrendFollower:TakerCoupling` | **OFF** | momentum-taker makes trends stick (chart); ret_acf plateaus |
| `PriceReaction`/bubble, anchor-timing (R5) | **OFF** | kept-hunting, not baked |
| `CoMovement`, `SlowRingDamp`, `Inertia:SentimentModulated`, `TouchTightenPrc`, `RefillThrottle`, `ImpactDecouple*`, `SmoothedPriceHalfLifeSec` | **DEAD** | prune candidates (§3) |

### Perf
| Lever | Status | Note |
|---|---|---|
| Maintenance offload (Option B) | **BAKED** | cap 3.8k→13.7k prod |
| `Advanced:BatchArms` | **BAKED** | −42%/arm; the one 06-18 win |
| `Staggering:{Enabled,Slots=2}` | **BAKED** | per-tick load-cut; Slots=2 (realism-safe) |
| C1/C3/C4/C6/C7 alloc/query trims + gate-0 commit metrics | **BAKED** | `collect` ~5% of tick |
| `Advanced:BatchBuyStops` | **OFF** (this patch, §later) | ~1–4%, marginal |
| `Advanced:BracketBatch`, `Arbitrage:BatchLegs`, `Db:PerCurrencyGroupGates`, `Db:GroupCommit`, `Bots:ParallelDecision` | **DEAD** | tested no-win / killed by Amdahl |
| Ultraplan A short-opens, Ultraplan B (profiler/arb-trigger/GC/column-store), cross-process sharding | **NOT** | the perf frontier |

---

## 8. Soak-test playbook
**Harness (`scripts/`, PowerShell):** `kse-balance-setup.ps1` (one-time: seed → clone pristine `kse_soak_seed` zero-trade template) · `kse-balance-soak-p.ps1` (**the A/B workhorse** — `-Db -Tmpl kse_soak_seed -Port -Minutes -SampleEverySec -Note`; two side-by-side instances = control/treatment). `r4_experiment.ps1 -Tag -Overrides @{"Bots.X.Y"="v"}` orchestrates a single-config run (edits appsettings, builds, soaks, scores, restores).

**Config overrides:** `Bots__*` env vars (double-underscore = nesting; `candle_export.py` auto-captures them into the CSV header = the "what we tested" record) **or** `r4_experiment.ps1` appsettings edits. **DB reset:** every run drops + `CREATE DATABASE $Db TEMPLATE kse_soak_seed`. **Ports:** 5080 control / 5081 treatment; **5083** = the live eyeball server (point the client here at the arm under test).

**Per-soak output:** `logs/soakP-$Db-*.log` (per-sample `drift=… // depth=… // ERR CK CONS shortfall`) + `logs/soakP-$Db-results-*.csv` + candle export → `data/soaks/candles-$Db-<ts>.csv` (1-min OHLCV, self-describing header). drift tuple = `stocks,avg,std,medianAbs,min,max,beyond50,beyond100,trades` (medianAbs + beyond50/100 = the gate metrics).

**Analysis tooling** (`py scripts/…`; all read the soak DB via `docker exec … psql`, primary-listing join):
| Script | Measures |
|---|---|
| `candle_export.py` | Transactions → 1-min OHLCV CSV (the durable record) |
| `candle_plot.py` / `candle_compare.py` | candlestick PNGs (`--bucket-sec` any TF) / A-vs-B side-by-side |
| `candle_realism.py` | flatness vs random-walk + magnitude budget (per-stock move vs 4h target) |
| `r4_realism_score.py` | **composite /100** (all Cont stylized facts; Roll-corrected mid-close) |
| `cross_stock_diag.py` | pairwise corr @horizons, factor-R², intra-vs-cross sector, vol concentration |
| `return_headroom.py` | 1-min σ, ret_acf lag1, kurtosis, cap headroom |
| `bounce_test.py` / `bounce_diag.py` | ret_acf CLOSE vs VWAP (is the −0.35 bid-ask bounce vs behavioral) |
| `news_move_dist.py` | move-size distribution + up/down log-symmetry |
| **`stock_liveness.py`** | **P2 gate** — per-book max_gap_s + empty-15s-% (thin EUR surfaced) |
| `fx_pair_corr.py` | USD/EUR level/return corr + parity band |
| `wall_diag.py` / `trend_diag.py` | order-wall concentration / linearity R² |
| `shock_diag.py` | ExogShock 5-min pre-flight (duty cycle ≥0.60, Cap) |
| `phase_harvest.py` | **perf A/B** — equilibrium cap, orders/tick, adv/tick, ms/tick from soak logs |

**Gate-set** (`explainers/BOT_MECHANICS.md` §1; 45m soak): **CK=0 (hard)** · ret_acf(VWAP) −0.5…−0.1 · kurtosis ≥4 · median excursion 3–8% · p95 10–20% · max 15–35% · taker 20–50% · spread <0.5% · |return|-autocorr >0.05 · dispersion >0.002 · pairwise corr 0–0.25. **P2:** no >15s-gap books. Composite bands (`r4_realism_score`): <40 bad / 40–70 ok / 70–90 good / >90 excellent.

**Discipline:** CK=0 hard gate every soak · **one lever per soak**, default-off + byte-identical, LOCK before the next · **one soak-pair at a time** (Postgres conn cap; max 2 servers) · **machine-light** (box throttles; a perf/corr number is INVALID unless `ActiveBotCap` held ≥~19k during the measured phase) · tiered **15m** screen / **45m** standard A/B / **2h** acceptance · **exit-49 benign** (forced `Stop-Process` at deadline, data already captured) · **`py` not `python`** for analysis · **kill `KieshStockExchange.Server` before any `dotnet build`** (live exe locks the output) · soaks are **not bit-deterministic** (wall-clock dt) ⇒ trust only cross-run-reproducible, parallel-arm deltas.

---

## 9. Deploy / prod runbook + box
**Box:** `159.195.149.51` (`root@`), path `/opt/kse-server`, DuckDNS-fronted (client prod URL = the duckdns host). Self-hosted Postgres + server + Caddy via docker-compose. **HW: 8 GB / 4 vCPU** (measured). **Current prod = Stage-1 `1d3fdd3`** (master); realism bundle on `feature/bot-market-realism-v2`.

**Always stack the prod override:** `docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production <cmd>` (base file alone = local-dev shape). Migrations run via the profiled one-shot `migrate` service, not on boot.

**Config-only deploy (no reseed):** ssh → `cd /opt/kse-server` → **pg_dump backup** (`… pg_dump | gzip > /var/backups/kse/…sql.gz`, verify `gzip -t`) → `git pull` → `docker compose … up -d --build server`. `appsettings.Production.json` does NOT override `Bots` ⇒ base bake applies. **GATE-0:** confirm cap ≥~19k + CK=0 through co-fire bursts; fallback = lighten co-fire (`GlobalCoFireFraction` 0.25→0.15 / `MeanIntervalMinutes` 0.5→1.0 / `GlobalFraction` 0.4→0.2). **Post-deploy (watch ~30m):** CK=0 (ck16m heartbeat) · no runaway · drift bounded · eyeball the live chart. **Rollback:** redeploy `1d3fdd3` (DB untouched).

**Final-ship = RESEED path** (this V1 wrap ships a reseed): pg_dump → merge to master → migrate if schema-touched → nuke+reseed → **CK=0 hard gate on live prod post-reseed** → 2h scorer confirm. Generic first-provisioning: `deploy/RUNBOOK.md`.

**Also pending ops:** `synchronous_commit=off` on prod (1 command, approved, cheapest perf win).

---

## 10. Closed arcs — settled, do NOT reopen
- **ret_acf −0.43 is STRUCTURAL** at the bot-decision layer (~20k independent agents + deep fast-refilling book = efficient price discovery). = 28% bid-ask bounce (FIXED by bounce-mid → CLOSE −0.17, in-band) + 72% fleet reaction-loop (VWAP flow ~−0.18 asymptotic). Every mechanism class proven absorbed across 25+ experiments + 2 engine patches. Only a core-engine change breaks it (discouraged).
- **Cross-stock correlation via shared SENTIMENT is dead** — the anchor damps it (CoMovement null ×6 soaks, sentiment-mod-inertia null). Only shared **taker FLOW** (co-fire) moves correlated price. The arc's core lesson.
- **"React faster" REFUTED** — near-instant reaction *lowered* corr + shrank moves (the lag holds trends/corr together).
- **ReactionPersistence corr is GAIN-SATURATED** ~0.075 @10min (gain 1.0≈2.0; deep book absorbs extra flow). Can't reach 0.2–0.3 by tuning.
- **"De-linearize / waviness" (R²) is not config-achievable** + un-measurable at 45–60m (noise ±0.05 R² / ±15 composite).
- **Chaser down-drift = a conservation identity** (net-long fleet + one-sided book), not a tunable.
- **Candle-close ret_acf ≈ Roll bounce** = a real stylized fact, fixed at source (mid-keyed close), not a bug.

---

## 11. Detail-doc index
Perf: `PERF_SCALING_PLAN.md`, `ultraplan-prompt-advanced-orders-reimpl.md` (A), `ultraplan-prompt-perf-optimization.md` (B), `PRUNE_PROPOSAL.md`. Realism: `REALISM_OVERHAUL_PLAN.md`, `SHIP_RUNBOOK.md`, `GLOBAL_SHOCK_PLAN.md`, `SECTOR_PULSE_PLAN.md`, `REACTION_PERSISTENCE_SPLIT_BAKE_RESULTS.md`, `REALISM_RETACF_CLOSEOUT.md`, `REALISM_CEILING_INVESTIGATION.md`. Reseed: `ultraplan-prompt-multiplier-to-excel.md`. Status/deploy: `PROJECT_STATUS.md`, `deploy/RUNBOOK.md`. Targets + mechanisms: `explainers/BOT_MECHANICS.md`.
