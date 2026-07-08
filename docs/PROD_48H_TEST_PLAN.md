# PROD 48h autonomous test + reseed — plan & running log (started 2026-07-08)

**Authorized by Kiesh (2026-07-08): "run and test on prod for the next 48h autonomously."** NO backup
(sim data, disposable) — rollback = redeploy `1d3fdd3`. **CK=0 is the hard gate.** Every experimental lever
is a reversible flag (via `appsettings.Production.json`), so "flip off + restart" is the fast rollback
before a full redeploy. Prod box: `ssh root@159.195.149.51`, `/opt/kse-server`.

## Scope of "everything"
Prod is on **`master @ 1d3fdd3`** (Stage-1, early July). The work is on **`feature/bot-market-realism-v2`
@ ~`3dc7a7b`** — ~100 commits ahead (the whole arc: sentiment redesign, FX-damp, co-fire, sector, rotator+
bank cohorts, per-strategy telemetry, all perf/scaler work, the tick baseline). Deploying "everything" =
merge that branch → master + fresh nuke+reseed + enable the experimental config live.

---

## STEP 1 — THE DEPLOY (execute first; not yet done as of this writing)

### 1a. Local prep
- [ ] Lower `Bots:Rotator:SeedBalanceUsd`/`SeedBalanceEur` → **30000** in server `appsettings.json` (match the
      equal-value $30k scale so the turnover bound stays meaningful).
- [ ] **Regenerate the xlsx** — the committed/uncommitted xlsx is STALE (predates the equal-value rotator
      edits): `cd Tools && py -X utf8 GenerateAIUsers.py` (set `$env:PYTHONIOENCODING="utf-8"`). Verify 20000
      users (fleet 19783 + arb5 + MM12 + rotator200).
- [ ] Seed-coverage sanity: `py -X utf8 Tools/analyze_seed_coverage.py` (60/40 USD/EUR, 0 cross-currency,
      all books covered; spot-check a rotator's equal-value holdings ~$30k/stock).
- [ ] Add the **experimental Bots block to `appsettings.Production.json`** (prod-only, reversible; local +
      tests stay default-off byte-identical):
  - `Bots:TradeInterval` — 250ms (if wired as config; else bake into appsettings.json). *(verify TradeInterval
    is config-settable — AiTradeService.cs:27/906; may need a small config-read wire.)*
  - `Bots:Staggering:{Enabled:true, Slots:4}`
  - `Bots:Scaler:{DutyCycleDenominator:true, ActionableSpanSizing:true, SelfCorrectingDelay:true,
    MaxTickMultiple:1.0}` — the corrected scaler + de-idle (STAGED: see 1c).
  - `Bots:Rotator:Enabled:true`, `Bots:BankEstimate:Enabled:true`.
- [ ] Commit: reseed spec (`Tools/Config.py`,`Person.py`,`GenerateAIUsers.py`,`analyze_seed_coverage.py`) +
      **both xlsx** (reseed = the one time the xlsx IS committed) + `appsettings.json` (SeedBalance) +
      `appsettings.Production.json`. EXCLUDE `.claude/*`, client `Resources/Raw/appsettings.json`.
- [ ] **Merge `feature/bot-market-realism-v2` → master**; push both.

### 1b. Box deploy
- [ ] `ssh root@159.195.149.51`; `cd /opt/kse-server`; record `git rev-parse HEAD` (expect `1d3fdd3` =
      rollback anchor).
- [ ] `git pull` (master → ~3dc7a7b+).
- [ ] `docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production build server`.
- [ ] **Nuke + reseed:** determine the prod reseed path (EF migrate to head + run the seeder from the new
      xlsx; the box's reseed/setup script). This is the destructive step.
- [ ] `up -d server`; `healthz` = 200; bot loop started.
- [ ] **CK=0 GATE @15m + @1h** (ck16m=0, no CK_ violations, no ConservationProbe negative-delta). If CK≠0 →
      rollback.

### 1c. Staging within the deploy (the one discipline kept)
Boot with **reseed + features + tick(250ms/Slots=4) first**, confirm **CK=0**. THEN engage the cap-raising
scaler (`DutyCycleDenominator`) — it's the behavior-changing cap lever (docker showed it can pin the cap /
4× tick if mis-tuned, though on prod ticks are cheap so it should just raise the cap healthily). Watch the
`BotPhase` line (enable `Bots:PhaseTimingSeconds`): does the cap rise without runaway, tick fits, CK=0? If
the cap runs away or the tape breaks → drop `MaxTickMultiple`/flip `DutyCycleDenominator` off (reversible).

### 1d. Rollback (no backup)
`git checkout 1d3fdd3` on the box → rebuild → reseed the prior seed (prior xlsx from git history). Fast
pre-rollback = flip the experimental Production.json flags off + restart (recovers Stage-1 behavior on the
new seed without a redeploy).

---

## STEP 2 — 48h VALIDATION (what to watch, iterate, re-reseed)
- **CK=0 continuous** — the non-negotiable gate; any break = investigate/rollback.
- **Tick + smoothness (250ms/Slots=4):** the tape should be smoother (fills every 250ms vs 1s bursts).
  Judge by gap-variance on the raw fill stream + frame-occupancy + eyeball the 15s chart. Confirm 250ms is
  the render sweet-spot (not choppier).
- **Scaler/cap:** does the corrected scaler hold a healthy cap at ~1s effective cadence on prod (cheap
  ticks)? Watch `BotPhase` (cap, tick, collect/batch/adv/cohorts ms, commits/sec, max-concurrent-committers).
- **Rotator + bank (live):** `BotStratPerf` per-strategy (return, win-rate, volume-share) — no strip-mine/
  runaway; does the rotator create cross-stock correlation (its purpose)? Equal-value seeding behaving?
- **Overall realism/drift:** the market health Kiesh eyeballs; the sentiment-redesign/co-fire/sector config live.
- **Reseed iterations:** if the seed or config needs tuning (rotator cash/scale, MM cohort, watchlist
  specialists, feature dosing), re-reseed or config-flip + re-observe. Each reseed = a LOG entry below.

---

## RUNNING LOG (append dated entries as tests + reseeds happen)
- **2026-07-08 (pre-deploy):** plan created; prod on `1d3fdd3`. Rollback anchor = `master @ 1d3fdd3`.
- **2026-07-08 ~23:53 — ★ DEPLOYED + RESEEDED (LIVE).** Bundle committed/pushed (`f41db1d`, feature branch).
  Box: `git checkout feature/bot-market-realism-v2` (f41db1d) → build server+migrate (embeds new xlsx) →
  stop server → `DROP+CREATE kse` → `run --rm migrate` (schema, Done) → `up -d server` → seeded from embedded
  workbook (~49s) → **bot loop started 23:53:36** with the experimental config (TradeIntervalMs 250 + Staggering
  Slots 4 + Rotator+BankEstimate ON; Scaler corrections STAGED OFF). healthz=401 via duckdns (app serving).
  **0 errors / 0 CK violations since boot.** Deploy = feature BRANCH (NOT merged to master ⇒ master stays clean
  1d3fdd3 = trivial rollback). `Seed:AutoOnEmptyDb=true` (safe; skips populated DB).
- **NEXT (autonomous, resume here):** (1) CK=0 GATE @15m + @1h (ssh; grep server logs for ERROR/CK_/Conservation;
  should stay 0). (2) TAPE/SMOOTHNESS: eyeball the 15s chart + confirm 250ms fills land per render frame (smoother).
  (3) CAP/TICK: `docker logs ... | grep BotPhase` (needs PhaseTimingSeconds=20 → the profiling line: cap, tick,
  collect/batch/adv/cohorts ms). (4) ROTATOR/BANK: grep `BotStratPerf` (per-strategy; rotator no strip-mine/runaway;
  correlation). (5) **STAGE 2 (after CK=0 confirmed clean ~1h+):** enable the cap-raising scaler — edit
  `appsettings.Production.json` Bots:Scaler:{DutyCycleDenominator,ActionableSpanSizing,SelfCorrectingDelay}=true,
  commit+push, box `git pull` + `up -d --build server` (or restart) → watch cap rises w/o runaway, CK=0, tape OK.
  If cap runs away / CK breaks / tape breaks → flip the offending flag off + restart. (6) Iterate/re-reseed as needed.
- **2026-07-09 ~02:12 (T+2h checkpoint):** container Up 2h healthy, **0 CK / 0 errors**. **2.23M trades, ~300/sec**
  (80,353 in 5 min) — tape flowing. BotPhase: 146–225 ms/tick, cap **~8–10k**, 17 max concurrent committers, 0.63
  round-trips/order; cohorts (rotator+bank) ~30 ms/tick = live. **Obs 1:** cap ~10k not 20k = EXPECTED (scaler
  correction staged OFF; at 250 ms tick the uncorrected scaler reads load ~4× high → conservative cap; Stage-2
  DutyCycleDenominator flip is the fix). **Obs 2:** avg drift **−4.1% / 69 stocks** at 2h = early intraday transient,
  WATCH as the window matures. Next: Stage-2 scaler flip after a longer CK-clean stretch, then keep watching drift.
- **2026-07-09 ~02:20 — EXP1: DipBuy 2.0→3.0 (Production.json override, rebuild+restart, DB preserved).** Countered the
  slow −1%/h down-drift. Result: drift ARRESTED — absolute drift (vs open, restart-invariant DB metric) held ~−4.2%
  (was sliding −1%/h), now ~flat ±0.2%/h. CK=0, healthy. NOTE: BotEconomy "avg drift" resets on restart (service-relative)
  ⇒ use the DB from-open metric across restarts.
- **2026-07-09 ~02:45 — ★ REALISM SCORECARD (prod, 35 USD stocks, best-ever):** drift arrested ~−4%; intraday range
  median 7% / only 2/70 >20% (extremes rare); **cross-stock corr 10-min mean +0.129 / factorR2 0.244** (IN real-equity
  0.2-0.5 band — the arc never broke ~0.08 locally); 5-min factorR2 0.196; **1-min excess kurtosis mean +7.57 / median
  +5.49** (real +3..+8 = realistic fat tails). The rotator cohort + 20k scale delivers the correlation + tails local
  soaks couldn't. Market is HEALTHY; tuning = optimization now. Correlation export: `data/prod/prod_usd_close.csv` via
  `ssh ... COPY(...) TO STDOUT` → `py scripts/cross_stock_diag.py --csv ... --horizons 1,5,10`.
- **2026-07-09 ~03:20 — EXP2: rotator ParticipationFraction 0.10→0.15 → REVERTED ~04:04.** Matched 38-min pre/post
  windows: corr UNCHANGED (1m factorR2 0.052→0.052, 5m 0.080→0.063, 10m 0.131→0.155 = noise; both windows 0.05-0.15 ≪
  the reliable 3h 0.244). Side effects: drift −4.7→−5.2% + trades/sec 300→240 (book thinned). Verdict: no benefit + mild
  harm → reverted to 0.10. **LEARNINGS: (1) corr is already in-band (0.244/3h) and CANNOT be tuned on sub-hour prod
  windows (noise-dominated) — leave the rotator at 0.10; (2) rapid restarts each perturb the drift settling.**
- **2026-07-09 ~05:05 — ★ MARKET TUNING CONVERGED.** 60-min UNINTERRUPTED drift watch (clean config, no restarts):
  service-relative drift 0.00 → +0.25 → −0.30 → recovered, oscillating ~−0.15, ended −0.11 = **−0.11%/h, MEAN-REVERTING**
  (dipped then bounced, NOT a slide). Absolute from-open −5.18%(04:00)→−5.30%(05:03) = −0.11%/h confirms. The earlier
  "−0.7%/h" was the EXP2 rotator confound + restart transients. **With the clean config uninterrupted, drift is SETTLED
  ~−5%, flat, mean-reverting.** trades/sec recovered to 257, CK=0, healthy. **Full scorecard all in-band: drift settled
  flat; corr factorR2 0.244@10m; kurtosis +7.6; range median 7% / extremes rare; CK=0.** Best realism the project has
  produced, at 20k-seed scale on prod.
- **★ CONFIRMED PROD CONFIG (converged, Production.json, reversible):** DipBuyStrength 3.0 · Rotator 0.10 + BankEstimate on ·
  TradeIntervalMs 250 + Slots 4 · Scaler correction STAGED OFF (fleet held ~10k **BY DESIGN**).
- **2026-07-09 ~10:09 — EXP3: Slots 4→8 (rotate the full 20k fleet at same flow) → REVERTED ~11:00.** Verified the
  mechanism (`ApplyActiveBotCap`, AiBotStateService.cs:139): the cap enables the FIRST ~10k bots in fixed order, other
  ~10k DORMANT (never trade) = fixed-10k market, half the seed wasted. Slots 8 halves per-bot load ⇒ scaler lifts the
  cap toward 20k (all rotate). RESULT: **corr NEUTRAL** (matched windows: 5m 0.086→0.069, 10m 0.116→0.149 = within
  sub-hour noise — the char comes from the ROTATOR's coordinated flow, not raw fleet-participant count) + **perf
  REGRESSION: maint phase 7ms→258ms** (scales badly with enabled count), tick 250→447ms, cap stuck ~15k (couldn't
  reach 20k), drift deepened −5.3→−6.7%. CK=0. **CONCLUSION: rotating the full fleet gives NO realism benefit + costs
  perf ⇒ 50k-rotate would be STRICTLY WORSE (heavier maint/iteration, cap more suppressed, no gain). Fixed ~10k-
  participant market is the RIGHT config; the other 10k seeds being idle is fine.** Reverted to Slots 4.
- **2026-07-09 ~11:31 — EXP4 + maint ROOT CAUSE (CORRECTED).** The maint blowup (~250ms/tick, tick→450ms) is NOT
  Slots-driven — it's **time/book-driven**. Root: `RunPeriodicMaintenanceAsync` (the `maint` bucket, AiTradeService.cs:1943)
  bundles the periodic heavy tasks — `RefreshAssets` + `PruneWorstOrdersAsync` (scans the growing book) + `LogSnapshot`
  (O(bots×stocks) ≈ 20k×50 = 1M ops). The resting-limit book grew UNBOUNDED to **210k Open + 984k Pending** over ~12h
  ⇒ the prune scan + snapshot got heavy. **EXP4 = `OrderMaxAgeSec=1800`** (expires stale resting LIMIT orders only, not
  stops; CK-safe, reversible): modest cut (maint ~250→210ms in 15m), bounds the Open pool gradually (history plateau ~110k).
  KEPT as 48h-soak hygiene (prevents unbounded book growth over the run). The full maint cost (O(bots×stocks) snapshot +
  the 984k Pending advanced-order pool) is a **PERF-TRACK item** (not market realism) — noted, not chased here.
- **★ MARKET REALISM = UNAFFECTED + still converged/good** across all of EXP3/EXP4 (corr, tails, movement, CK=0). The
  fleet-size + maint threads are CAPACITY/PERF, orthogonal to the (good) market character.
- **★ KIESH DECISION (2026-07-09): HOLD the fleet at ~10k to PROTECT REALISM** (do NOT flip the staged scaler to 20k —
  more independent bots = LLN-flattening risk to the validated corr/tails). Capacity-vs-realism tradeoff resolved = realism wins.
- **POSTURE NOW = HOLD + SOAK.** Market converged; further rapid changes risk degrading a good market. Periodic health
  monitoring over the 48h (CK=0, drift stays bounded/oscillating, no runaway, tape healthy). NO more config changes unless
  a regression appears. Stage-2 scaler + bot-parallelism = separate perf tracks, NOT market tuning, held.
- **ROLLBACK (no backup):** box `git checkout master` (1d3fdd3) → rebuild → drop+create kse → migrate → up (reseeds
  prior embedded xlsx). Fast partial = flip experimental Production.json flags off + `up -d --build server`.
- Box deploy commands (exact) for reuse are in the RUNNING LOG deploy entry above.

## Notes / open seed decisions folded (autonomous)
- Rotator seed: equal-VALUE (~$30k/stock) + cash one equal bucket ($30k/ccy) + turnover SeedBalance→$30k.
- Rotator + bank ENABLED on the fresh seed (testing live).
- Still-to-verify in the reseed spec: watchlist specialists (~25% of bots draw a 2-5 stock list), MM cohort
  count (~12), holdings weighting (lean uniform). Fold if time; else ship the current spec.
