# Chaser / exogenous-flow ret_acf investigation (2026-06-20)

**Branch `feature/bot-market-realism-v2`.** Follows `docs/REALISM_CEILING_INVESTIGATION.md` (which proved no
*config* lever moves the `ret_acf_lag1 ≈ −0.43` ceiling). This round tested the council's structural fix: **add
exogenous directional flow** (a news/shock bus + a "chaser" cohort that trades into shocks). Chaser v1 patch =
`chaser-direct-flow-v1.patch` (committed default-off, `7051f53`).

## Headline result
**The chaser is the FIRST lever in 25+ experiments to actually move ret_acf** — but v1 has an inherent down-drift
that makes it unbakeable as-is. The flow half is solved (pending a drift-neutral v2); the bounce half is separate.

## What the chaser does
On a "chase tick," a selected bot submits a real marketable (slippage-market) order INTO a live exogenous shock,
**replacing** its normal order — supplying persistent 1-min directional VOLUME. Sized off the bot's SEED-price
portfolio (mark-independent, no self-amplify), deterministic argmax selection, 0 RNG draws on a chase tick. Dials:
`Bots:ExogShock:ChaserNotionalFrac` (primary), `ChaserFraction` (cohort ≤0.25), `ChaserMaxNotionalFrac` (per-order
cap). All ride OrderEntry→Match→Settle (no naked flow). Default-off ⇒ byte-identical.

## Soak results (all conservation-clean, CK=0)
`ret_acf` measured two ways: **CLOSE** (last-trade, includes bid-ask bounce — the headline) and **VWAP** (bounce
removed — the pure flow component the chaser targets). Baseline OFF: CLOSE ≈ −0.42, VWAP ≈ −0.22 to −0.26.

| Round | config | VWAP ret_acf | CLOSE ret_acf | drift | note |
|-------|--------|--------------|---------------|-------|------|
| K0 | OFF vs frac0.15 (55m) | −0.20 → **−0.127** | −0.40→−0.33 | — | kill-gate PASS — lever moves ret_acf |
| S1 | frac0.15 + TT0.40 (90m) | −0.227 → **−0.138** | −0.42→−0.33 | −2.15%/90m | reproduced; TT did NOT cut bounce; drift over budget |
| S2 | frac0.05 / 0.10 (90m) | −0.116 / −0.159 | — | −2.04% / −1.91% | **drift DOSE-INDEPENDENT** |
| S3a | AnchorTracks-only, no chaser | −0.217 (=baseline) | — | −0.94% | anchorTracks alone does nothing |
| S3b | AnchorTracks + frac0.10 | −0.142 | — | −2.24% | **anchorTracks does NOT fix drift** |
| LONG | OFF vs frac0.05 (3h) | −0.261 → **−0.067** | −0.44→−0.32 | −3.8%/3h (no plateau) | chaser is a powerful flow lever; drift unbounded |
| INJ | frac0.05 inject30 vs 15 | −0.120 / −0.130 | — | −2.14% / −2.27% | **cash injection does NOT fix drift** |

## Conclusions
1. **The chaser robustly moves the flow (VWAP) ret_acf into the target band** [−0.15,−0.05] (−0.067 over 3h at the
   gentlest dose) — reproduced across 6 soaks. The ret_acf ceiling IS movable by adding exogenous directional flow,
   exactly as the council predicted. This validates the whole approach.
2. **v1 has an inherent ~−1.3%/90m down-drift that is NOT bakeable** — it does NOT plateau (grows to the ±20% cap
   over hours), regressing the composite (73→65). It is **dose-independent, anchor-tracking-independent, and
   cash-injection-independent** — i.e. a structural DIRECTIONAL asymmetry (chase-BUYS gated by cash/value-band,
   chase-SELLS by shares which net-long bots always have → net sell-lean), not a tuning problem.
3. **The CLOSE/headline ret_acf is bounce-limited** (~−0.33): the chaser fixes the flow but the bid-ask bounce
   (~+0.20, which the chaser slightly WORSENS) keeps CLOSE negative. `TouchTightenPrc` does not cut it under chaser
   flow.

## Path to `ret_acf < 0.1` (two orthogonal halves)
- **FLOW half → `docs/ultraplan-prompt-chaser-v2-symmetry.md`** — make the chase flow drift-neutral (symmetric
  buy/sell suppression) so the proven ret_acf gain bakes without drift.
- **BOUNCE half → `docs/ultraplan-prompt-microstructure-bounce.md`** — cut the bid-ask bounce (mid-price/micro-price
  fills, finer tick) so the CLOSE headline follows the VWAP into band.
- Both are flag-gated, default-off, and independent. Chaser v1 stays shipped default-off + available.

## Reference (clean 3h OFF baseline, ~180 candles/stock)
CLOSE −0.438 / VWAP −0.261 / r4 −0.420 / composite 73.3 / clustering(absret) 0.205 / drift −1.34%/3h.
