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
- **2026-07-08 (pre-deploy):** plan created; prod still on `1d3fdd3`; branch `3dc7a7b`; deploy NOT yet fired
  (near weekly limit + compaction). Deploy = the first autonomous action when the run resumes. Rollback
  anchor = `1d3fdd3`.
- _(next: deploy fired → CK gate result → tape/cap read → …)_

## Notes / open seed decisions folded (autonomous)
- Rotator seed: equal-VALUE (~$30k/stock) + cash one equal bucket ($30k/ccy) + turnover SeedBalance→$30k.
- Rotator + bank ENABLED on the fresh seed (testing live).
- Still-to-verify in the reseed spec: watchlist specialists (~25% of bots draw a 2-5 stock list), MM cohort
  count (~12), holdings weighting (lean uniform). Fold if time; else ship the current spec.
