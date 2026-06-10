# Plan: balance bot buy/sell aggression (kill the residual down-drift)

**Status:** design for Ultraplan. The market-realism v2 work + down-drift fix are shipped and validated
(branch `feature/bot-market-realism-v2`, `appsettings.json` config on, 2.5h soak within the ≤5%/4h budget,
conservation clean). This is the **last residual** — optional, since the config already meets budget, but it
would drive the slow down-creep toward **zero**.

## 1. The problem (measured, final config, 2.5h / 230k trades)
Aggressive (liquidity-taking, market) **sell** volume exceeds aggressive **buy** volume by **~47%**:

| | Market (taker) filled | Limit (passive) filled |
|---|---|---|
| Buy | 1.17M (29%) | 1.81M (71%) |
| Sell | **1.71M (41%)** | 1.26M (59%) |

A taker-sell executes at the bid (down-tick); a taker-buy at the ask (up-tick). ~47% more taker-sells ⇒
persistently more down-ticks ⇒ the slow residual down-drift. Also: armed protective **sell-stops outnumber
buy-stops ~10:1** (39k vs 4k) — another taker-sell source when they fire.

## 2. Root cause
The fleet is **long-heavy** (~291M shares long vs ~3.8k short). Selling is "easy" — bots have inventory to
sell via market orders and protect it with sell-stops — while buying tends to **rest as passive limits**
(71% of buy volume vs 59% of sell volume) and deploys cash more cautiously. The market-vs-limit choice is
**symmetric in code** (`effectiveUseMarket` applies equally to buy/sell), so the skew is an *emergent
outcome* of the long-heavy inventory, not a probability knob — which is why neither the BuyBias A/B
(0.494 vs 0.524, ~no effect on the broad creep) nor the continuous cash controller (bounds but can't erase
it) fixes it. It needs a behavioural/structural change.

**Ruled out — sentiment is NOT the cause.** Over the 2.5h run, global sentiment averaged **−0.006** (≈0;
range −0.28..+0.34) and the news shocks were net **+0.08** (5 pos / 4 neg). Sentiment is balanced around zero
as designed (zero-mean AR(1) rings, neutral reset, ±random shocks), so it contributes no persistent
direction. The drift is the taker-flow asymmetry above, not a sentiment bias.

## 3. Fix options (Ultraplan to choose/refine)
1. **Side-symmetric taker rate (recommended, most targeted).** Make realized taker-buy volume ≈ taker-sell
   volume: bias buys slightly more toward market (or sells toward limit) so the down-tick/up-tick balance
   holds near 1.0. Could be a closed-loop nudge driven by a running realized-taker-ratio gauge, or a static
   per-side `UseMarket` adjustment. Lowest blast radius (decision layer only).
2. **Trim the structural sell pressure.** Reduce the 10:1 protective-sell-stop dominance (e.g. lower the
   bots' stop/trailing probabilities) and/or **rebalance seed holdings** (less initial long inventory, more
   cash) so there's simply less to aggressively sell. Seed/`Tools` change — overlaps with the cash recenter.
3. **Instrument + tune.** Add a realized taker buy/sell-volume gauge to telemetry as the live target metric,
   then tune (1) to hold it ~1.0.

## 4. Constraints / invariants
- Flag-gated, inert-first (off = today's behaviour byte-identical); no new RNG on the off path; loop-thread only.
- Must NOT touch the matching/settlement engine contract or the lock-order invariant. Decision-layer only.
- Preserve all other per-strategy properties (MM/Arbitrage excluded as usual). Any per-bot property change
  flows through the `/Tools` Excel pipeline (the §8 rule from `bot-down-drift-fix.md`).
- Must not flatten the chart: re-check the candle shape (body/range, wick fraction vs the RW baseline) and
  the ≤5%/4h drift budget after — bounding taker flow shouldn't kill the herding trends.

## 5. Verification
A/B soak (flag off vs on) via `scripts/kse-balance-soak-p.ps1`: realized taker buy/sell ratio → ~1.0;
`balance-drift.sql` `avg`/`medianAbs` → ~0 and flat over a multi-hour run; conservation clean
(`CONS=0`/`CK=0`/`beyond50=0`); `candle_realism.py` shape unchanged (still ≈ RW, trends intact).

## 6. Files (anticipated)
Decision layer: `AiBotDecisionService.ChooseOrderType` (the `effectiveUseMarket` per-side adjustment) +
config + a telemetry gauge. Option 2 also touches `/Tools` (seed holdings / stop probs) + xlsx. No migration.
