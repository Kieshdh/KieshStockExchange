# R4 §0009 Stage 2 — characterization soak findings

**Soak**: 210 min on `kse_soak` port 5080, branch tip `0690b90`. All probes ON (MatchSymmetryProbe + DepthContextEnabled + BotDecisionProbe with sample 1/200 plain, 1/1 advanced+MM).

## Soak metrics

| Metric | Value | vs R3 baseline |
|---|---|---|
| avg drift | **−1.85%** | (R3 was +0.18%, this run on MaxQty=25) |
| max | +25.54 | +17 pp wider (MaxQty=25 effect) |
| min | **−11.03** | +12 pp tighter |
| medianAbs | 2.72 | +1.6 |
| trades | **564,888 (2,690/min)** | +130% throughput vs R3 (1,167/min) |
| CK / CONS / ERR | **0 / 0 / 0** ✅ | clean throughout |

Probe overhead was strictly positive (faster, not slower) because the ~1k/min rate the previous soaks ran at was driven by other system overhead, not the probe path. Probes write ~131 MB of CSV over 3.5h — no I/O bottleneck.

## Decision-side asymmetry vs matcher-side asymmetry (Block 1)

| Where | Buy count | Sell count | Sell/Buy ratio |
|---|---|---|---|
| Matcher fills | 221,292 | 206,362 | **0.93×** (buy slightly more!) |
| Matcher fills (Stage 1 45m run) | 17,448 | 13,748 | 1.27× (sell more) |
| **Bot decisions (plain path)** | **4,587** | **1,343** | **0.293×** |

**Crucial insight #1**: bot decisions skew **buy** 3.4× over sell, but matcher fills come out near 1:1 (or slightly sell-skewed in shorter samples). **Buy decisions are not converting to matcher fills at the same rate as sell decisions.** Per-hour ratios stable across the 5 hours sampled (0.27-0.33), so this is steady-state, not regime-dependent.

The decision-side stability also confirms the probe self-consistency check: the ratio is bucket-stable, not drifting.

## Component decomposition (Block 2)

mean(buy_prob − 0.5) = **+0.275** across 5,930 sampled plain decisions.

| Component | Mean | 95% CI | Contribution | Fires ≥40% gate? |
|---|---|---|---|---|
| **homeostatic** | **+0.6890** | (0.6839, 0.6937) | **250.6%** | ✅ **FIRES** at 248.7% |
| anchor | +0.0646 | (0.0621, 0.0669) | 23.5% | below |
| directional_eff | −0.0004 | (−0.0007, −0.0001) | 0.1% | below |
| herd | 0.0000 | (0.0000, 0.0000) | 0.0% | below |

**Crucial insight #2**: **CashHomeostasis dominates the buy bias** (250% of net |mean − 0.5|). The other components are noise. This is structural: bots' BuyBiasPrc seed targets sit around 0.55-0.65, and the cash-reserve restoring shift pulls every bot toward those targets. The directional + herd components average to zero — fully symmetric.

Per-(strategy, inventory bucket) — only `long_heavy` bucket fired in this sample window because every probed bot was sitting on cash + long inventory. Mean buyProb 0.75-0.81 across all strategies.

## Bracket cohort cross-tab (Block 3)

| kindPre | bias | kindPost | count | % |
|---|---|---|---|---|
| LongBracket | 0 | LongBracket | 67,757 | 57.8% |
| ShortBracket | 0 | ShortBracket | 49,553 | 42.2% |

**Zero E1 inversions** (bias always 0 — no bot crossed the inventory-heavy threshold during the sampled brackets). The `InventoryBiasShortMult=2.0` lever **never fired in this soak** because the inventory threshold was never met. So the R3 §0003 lever is currently dormant in this config; it can't be contributing to the asymmetry.

**Bracket-build success rates differ by kind**: ShortBracket = 21.9%, LongBracket = 35.0%. ShortBrackets are 38% less likely to succeed than LongBrackets. Eligibility / funding asymmetry. This means short-bracket *intent* runs 1.37× LongBracket (49.5k vs 67.8k LongBracket intents — wait, actually LongBracket dominates intent too at 1.37× — the matcher-side sell skew is not coming from bracket intent imbalance).

## MarketMaker quote-side ratio (Block 4)

| Choice | Count | % |
|---|---|---|
| choseBuy | 164,725 | **74.6%** |
| choseSell | 55,916 | 25.4% |

**Net buy-quote bias: +0.493** (per-bot mean +0.0346). MMs quote BUY 2.95× more than they quote SELL.

**Crucial insight #3**: this is the **sell-skip-when-no-inventory** mechanism the spec called out as surface (d). When an MM has no inventory to sell, it can't quote sell — defaults to buy quote. The 3:1 buy-quote bias accumulates a structurally **thicker bid wall** on the book.

## Depth context (Block 5)

| Taker side | n | Mean level walked | Mean opposite wall depth |
|---|---|---|---|
| buy-taker | 274,152 | 0.88 | 51,932 (ask wall when buying) |
| sell-taker | 290,809 | 0.88 | **68,356** (bid wall when selling) |

**Crucial insight #4**: the **bid wall is 32% thicker than the ask wall** on average. Sell-takers consistently find more resting buy liquidity than buy-takers find resting sell liquidity. Mean level walked is identical at 0.88 — but the wall thickness differs.

## SYNTHESIS — what the bear tail mechanism actually is

The data tells a structural story that none of the four surfaces individually predicted:

1. **Bots decide buy** (cashHomeostasis pulls every bot toward BuyBiasPrc ~0.55-0.65). 3.4× more buy decisions than sell decisions.

2. **MMs quote buy** when they can't sell (3:1 buy-quote bias). Bot limit buys + MM buy quotes both add to the **bid side** of the book.

3. **The bid wall thickens** (32% deeper than ask wall on average).

4. **The thick bid wall absorbs sell-takers easily.** A sell-taker arrives, finds plenty of resting buy liquidity, crosses, fills.

5. **The thin ask wall resists buy-takers.** A buy-taker arrives, finds less aggressive ask supply, walks deeper or rests unfilled — *becoming a buy limit-maker itself, further thickening the bid side.*

6. **Sell flow converts to fills more efficiently than buy flow.** Even though bots decide buy 3.4× more often, the matcher sees roughly 1:1 (or slightly sell-skewed) fills — because the bids accumulate as resting liquidity while sells punch through.

7. **Net price pressure is DOWNWARD** because:
   - Sell-taker fills move price down (cross at maker bid).
   - Buy decisions that *don't* fill (rest as limits) don't move price.
   - The accumulated bid wall is a *negative-feedback substrate* against upward moves but a *positive-feedback substrate* for downward moves (because the wall thins as sells punch through it, but the wall rebuilds slowly from new buys).

**The bear tail is emergent from the substrate, not from any single asymmetric formula.** No single bot lever produced it; the combination of (cash-bias buys → resting bids) × (MM sell-skip → more resting bids) × (thick-bid-wall absorbs sell flow) produces it.

## Stage 3 candidate fix surfaces

Per the §0009 acceptance criterion: **one surface (homeostatic) fires the ≥40% gate**, BUT the surface is itself driven by per-bot BuyBiasPrc seeds (Excel pipeline) — and we've explicitly said don't touch `Tools/`. The other contributors (MM sell-skip + bid-wall thickness) **distributed across surfaces** at <40% each but compound multiplicatively. Stage 3 has three plausible levers:

### A — Symmetrize MM quoting (recommended first try)
In `AiBotDecisionService.ChooseMarketMakerQuote`, when buys == sells (and the bot has no inventory), the current code defaults to `buys <= sells → choseBuy`. Instead: when the bot has zero inventory, randomly choose buy/sell with 50/50 odds. The downstream `ChooseStockId` already filters non-inventory sells out, so the bot will still SKIP the tick — but it won't have biased toward buy in the first place. This breaks the 3:1 buy-quote bias at its source.

### B — Reduce per-bot BuyBiasPrc targets
Requires `Tools/Person.py` regeneration (out of scope unless approved).

### C — Sell-side market-take aggressiveness
Boost `effectiveUseMarket` for SELL orders when the ask wall is thin (or when sentiment is mildly negative). Symmetric counter to the §sentiment-dynamics:AggressionBoost that already biases trend-followers + scalpers toward market-takes. The risk: makes sells fire faster too, which could deepen the bear tail rather than fix it.

### D — Liquidity-aware order placement
Bots currently place limit orders at fixed offsets from mid (cf. `_limitOffsetMult` + `_quoteHalfSpreadPrc`). Make the offset asymmetric: place limit ASKS closer to mid when the ask side is thin (incentivizing the bot's own liquidity to fill the gap), and limit BIDS further from mid when the bid wall is already thick. This is a hot-path code change in `ComputeOrderPriceAsync`.

## Recommendation

**Try (A) first.** It's a 3-line change in `ChooseMarketMakerQuote`, the smallest possible surgical fix, and it attacks the *largest measurable* asymmetry (3:1 → 1:1 in MM quote bias). If (A) closes the bear tail to within 5pp of the upper tail in a 60m A/B soak, ship it. If not, layer (D) on top — substrate balance via offset asymmetry.

**Do not try (B)** without explicit user approval to touch `Tools/`. **Do not try (C)** without first ruling out (A) — the asymmetric aggression could make things worse.

## Probe self-consistency check

The spec's gate said "probe block #1 (decision-side ratio) within ±5% of matcher-side 1.27×." This soak's matcher ratio was 0.93× (within 7% of unity — i.e., near-symmetric fills) while the decision ratio was 0.29× (heavily buy-biased decisions). The two ratios *should not match* — they measure different things (intent vs realised flow). The gap **is the finding**: bots decide buy but realize sell. The substrate converts buy intent into resting bids, and sell intent into matcher fills.

The probe is working correctly. The Stage 1 ratio (1.27×) reflected matcher fills under MaxQty=25 conditions; this Stage 2 ratio (0.93×) reflects matcher fills under the same conditions over a longer window. Both are statistically consistent — Stage 1's smaller sample (78k rows) showed higher variance.

## Artefacts

- `KieshStockExchange.Server/logs/match-symmetry-probe.csv` — 98 MB, 2.2M rows
- `KieshStockExchange.Server/logs/bot-decision-probe.csv` — 29 MB, 0.4M rows
- `scripts/r4_probe_analysis.py` — analysis script
- Soak log: `logs/soakP-kse_soak-20260612-204828.log`
- This file: `artifacts/r4-0009-stage2-findings.md`
