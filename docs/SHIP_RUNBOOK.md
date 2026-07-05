# SHIP RUNBOOK — the realism bundle to prod (one validated decision from cutover)

**State:** the realism levers are committed **default-off** on `feature/bot-market-realism-v2` (tip `121800a`); prod is Stage-1 `1d3fdd3`.
This is the staged cutover — **HELD for Kiesh** (irreversible + needs the prod 20k-perf check). Nothing here runs unattended.

## ★ GATE 0 — prod 20k-perf check (the one thing local COULDN'T validate)
The local box can't run the heavy full bundle at 20k (it throttled the active fleet to ~1000 — machine-degradation over a long soak session +
the co-fire/arb tick cost; see roadmap [H6]). So BEFORE trusting the market at scale, confirm the PROD box sustains ~20k active bots with the
bundle ON. The **co-fire is the heavy phase** (a market-order burst every ~30 s). Check post-deploy: `ActiveBotCap` holds ≥ ~19k AND CK=0 through
co-fire pulses (admin bot dashboard / BotPhase logs).
**Perf fallback if prod throttles:** lighten the co-fire — `GlobalCoFireFraction` 0.25→0.15 and/or `MeanIntervalMinutes` 0.5→1.0 (trades a little
5-10min correlation for throughput), or `GlobalFraction` 0.4→0.2. Re-check cap. (The correlation lift is modest, so a lighter dose is acceptable.)

## STEP 1 — bake the ship config into `appsettings.json` (`Bots` block) + commit
The levers are committed but default-OFF; the bake flips them ON. Exact key → (current → **SHIP**):

| `appsettings.json` key (under `Bots`) | current | SHIP |
|---|---|---|
| `Sentiment:GlobalSigmaMult` | 1.0 | **2.5** |
| `Sentiment:RegimeDrift:Strength` | 1.0 | **0.2** |
| `RecentAnchor:Strength` | 0.10 | **0.35** |
| `Fx:Alpha` | 0.92 | **0.97** |
| `Fx:Amplitude` | 0.005 | **0.002** |
| `Fx:RateBand` | 0.20 | **0.05** |
| `ExogShock:Enabled` | false | **true** |
| `ExogShock:MeanIntervalMinutes` | 3.0 | **0.5** |
| `ExogShock:GlobalFraction` | 0.0 | **0.4** |
| `ExogShock:GlobalCoFire` | false | **true** |
| `ExogShock:GlobalCoFireFraction` | 0.0 | **0.25** |
| `ExogShock:GlobalCoFireNotionalFrac` | 0.0 | **0.15** |

Unchanged (deliberate): `Sentiment:PerStockSigmaMult`=1.0 · `RecentAnchor:Enabled`=true (already) · `RegimeDrift:Enabled`=true (already) ·
`ExogShock` per-stock chaser + `AnchorTracksShock` stay OFF (co-fire is the ONLY flow; the per-stock shocks are inert) ·
**`ExogShock:SectorCount`=1 / `SectorFraction`=0.0 — sector stays DEFAULT-OFF (the ON-flip is a separate Kiesh call; the A/B showed null intra>cross at SF0.5).**
Commit on `feature/bot-market-realism-v2` (`feat(bots): bake realism ship config`).

## STEP 2 — merge to master (clean FF)
`feature/bot-market-realism-v2` is ahead of `master` (`1d3fdd3`) by the realism commits + the bake:
`git checkout master && git merge --ff-only feature/bot-market-realism-v2`. **The CANDLE fix is a SEPARATE branch (`feature/candle-continuity`)
→ its own PR; do NOT bundle it into this deploy.**

## STEP 3 — deploy on the prod box (`159.195.149.51`, `/opt/kse-server`) — NO reseed
`ssh root@159.195.149.51` → `cd /opt/kse-server` → **pg_dump backup** (`... exec -T db pg_dump ... | gzip > /var/backups/kse/kse-preship-<ts>.sql.gz`;
verify `gzip -t`) → `git pull` → `docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build server`
(pure code+config — NO migration, NO reseed; `appsettings.Production.json` does NOT override `Bots`, so the base bake applies).

## STEP 4 — gates (watch ~30 min)
- **CK=0** (hard — the ck16m heartbeat) · no runaway (no stock pinned at ×3) · drift bounded · **GATE 0 perf** (`ActiveBotCap` ≥ ~19k through co-fire pulses).
- **Eyeball** the live chart: correlation looks real (5-10 min co-movement), damping keeps >10% moves rare, NOT lockstep-fake.

## ROLLBACK
Redeploy `1d3fdd3` (`git checkout 1d3fdd3 && docker compose … up -d --build server`) — DB untouched (config+code only). Restore the pg_dump only if data were touched (they aren't).

## DEFERRED (separate, attended — NOT part of this ship)
- **Reseed** — fold per-bot multipliers into the Python seed (dials→1.0); behavior-neutral hygiene. Do it WITH the prune as ONE fresh population.
- **Prune (#171)** — see `PRUNE_PROPOSAL.md`; your OK gates it.
- **Candle (#175)** — `feature/candle-continuity` `1b7a26f`; own PR + a :5083 eyeball.
- **Sector ON-flip** — set `SectorFraction`>0; your call.
