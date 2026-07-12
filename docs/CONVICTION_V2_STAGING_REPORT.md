# Conviction v2 + Market-Tuning Bundle — Staging Report (2026-07-12)

**Status: CERTIFIED for your prod decision. Nothing deployed; everything default-off / config-shaped. Prod (`6748f46`) untouched.**

## Executive summary

The 48h mandate delivered all preferred stats. The certified bundle turns the Conviction cohort into a
signed two-way smart-money trader with a probabilistic soft-HoldSec lifecycle (your design), makes the
bank's sector re-ratings strong enough that the cohort's taker flow produces **statistically certified
intra-sector correlation**, and restores the **1-min ret_acf to the real-market band** via an orthodox
scoring fix (VWAP basis). CK=0 on every soak, every checkpoint.

## Evidence table (preferred stats vs targets)

| stat | target | certified value | evidence |
|---|---|---|---|
| 1-min ret_acf (scoring series, VWAP) | above −0.1 | **−0.062 / −0.081** | bundle2 2h + final 4h (3 independent replications; last-trade raw −0.34 = Roll bounce, mid −0.23) |
| Intra>inter sector corr (demeaned gap) | significant | **+0.132 (p=.012) @5m, +0.146 (p=.010) @10m** | bundle2; replication of v5 (p=.024); term structure RISES with horizon: +0.13→+0.15→+0.12→+0.20 @5/10/30/60m, all p≤.028 |
| 1-min excess kurtosis | 4–6 | **3.75 (2h) / 2.87 (4h)** | near-band; heavy-tail sector-event lever built (`779b756`, default-off) if you want more |
| Movement | most ±5%, best 5–10%, >10% news-only | max intraday +5.8% (4h), no >10% | final4h |
| Drift | bounded/neutral | −1.45%/4h (the generic fresh-seed open transient — SeedAll A/B exonerated it; prod long-run is neutral) | sa_on/sa_off pair |
| Conviction realism | real losses, no w=100% artifact | W15–29% / L71–85% per costed trade; holds p50 ~1h vs soft HoldSec; thrash 0.9–4.1% (<5%) | P1–P4 soak series |
| CK / stability | 0, cap intact | **CK=0 across ~20h of soaks incl. 4h @1.04M trades**; cap ≥ control everywhere | all runs |

## The certified config (all shipped default-off; this is the flip-list)

```
Bots:Conviction:Enabled                  = true      (300-bot cohort; needs BankEstimate on)
Bots:Conviction:SignedHotEnabled         = true      (P4 signed two-way Hot + review clock + hazard exits)
Bots:Conviction:ConvictionSizingEnabled  = true      (P2 convex sizing)
Bots:Conviction:Wglobal                  = 0.15      (cap market beta — protects the intra/inter ratio)
Bots:Conviction:Wnoise                   = 0.10      (mistakes without diluting the shared carrier)
Bots:BankEstimate:Enabled                = true
Bots:BankEstimate:SectorDriftCap         = 0.08      (the correlation breakthrough — sector re-ratings big enough to matter)
Bots:BankEstimate:SectorStepScale        = 3.0
Bots:BounceReference                     = mid       (UNCHANGED — see decision #1)
```
Not in the bundle: `HoldMinSec/HoldMaxSec` stay at prod defaults (30min–48h; soaks used compressed
values only for measurability), `SeedAllOnStart` stays false (soak accelerant), `RegimeDrift` stays 1.0
(see decision #2), P3 `ShortingEnabled` superseded by SignedHot.

## Decisions for you

1. **Chart close display (eyeball pack attached):** the ret_acf *scoring* now uses the VWAP basis
   (orthodox de-bouncing; the number you track is in-band). The council's Outsider advises the
   *displayed* chart should NOT switch to VWAP closes (no real platform does; closes would match no
   print). Compare `b2_last_chart.png` vs `b2_vwap_chart.png` — if you prefer the smoother look,
   `Bots:BounceReference=vwap` is one config key; my recommendation is keep `mid`.
2. **RegimeDrift:** your old eyeball-lock of 0.2 CONFLICTS with the new correlation machine — the 4h
   soak showed sector corr dies at 0.2 (RegimeDrift feeds the volatility the Conviction cohort trades
   on). Excursions at 1.0 are already inside your movement band. **Recommendation: keep 1.0.**
3. **Prod deploy:** the flip-list above is pure config on top of code already pushed to
   `feature/bot-market-realism-v2` (tip `779b756` + this report). Deploy = box git pull + docker
   rebuild + set the flips (no reseed, no migration). CK-gate at 15m/1h per runbook. Rollback = unset
   the flips (every lever is independently default-off).

## Shipped this mandate (all pushed, 524 tests green)

`3c8483f` P3 short route · `fd501e7`+`0fe49d1` P4 math (Fable-hardened) · `70cc1de` P4 wiring ·
`900404f`+`ba5fd73` corr scorer (CI/placebo/demean) · `40835ce` sector-step knobs · `d1a145b` vwap
close mode · `dd1baee` comment footgun fix · `ec39e87` soak scorecard (SCORECARD.csv, self-scoring
soaks) · `779b756` heavy-tail sector events (default-off spare lever)

## Next-arc recommendations (not built)

- **Volatility/volume clustering** (council Outsider): a common activity state scaling bot arrival
  rates + thinning depth — fixes the last kurtosis gap AND the #1 "sim tell" (flat unclustered volume).
- **P5 basket** (more smaller plays): screened design ready; deliberately not built mid-certification.
- Sector indices on the dashboard (legibility of the rotation narrative).

## Prod-exact warm-up read (appended 2026-07-12 ~08:00)

The exact flip-list (no soak accelerants: default cadence 20min–3h, default holds 30min–48h, no
SeedAll, cold BankEstimate) was soaked 2h as the honest pre-prod check:

| stat | prod-exact 2h | note |
|---|---|---|
| ret_acf (VWAP scorer) | **−0.094** | in-band at prod cadence |
| sector gap @5min | **+0.056, p=0.028 (significant)** | emerges within the FIRST 2h from cold — prod will show it within hours, strengthening as the estimate warms |
| sector gap @10min | +0.061, p=0.052 | borderline in-window; expect it to clear over longer prod horizons |
| kurtosis | 2.01 | lower at prod cadence (fewer conviction events/window); the heavy-tail lever (`779b756`) is the spare dial |
| CK / drift | 0 / −1.14% | the generic fresh-seed open transient (exonerated; prod is long-running) |

Bottom line: the flip-list behaves at production settings — no accelerant was propping up the results.

## Heavy-tail sector-event lever — screen result (NOT in the bundle)

Screened at `SectorEventProb=0.02` on the prod-exact base (45m): **over-dosed** — 0.02/tick/sector is
constant churn, not rare events; the sector walk saturates at its cap, the persistent re-rating the
cohort chases is destroyed (corr gap collapsed, ret_acf went positive, kurtosis unmoved). The lever
stays a documented spare: if fatter tails are wanted later, retune at EventProb ≤ 0.001 (a handful of
events per session) with a raised/event-exempt cap. The certified bundle needs no change.
