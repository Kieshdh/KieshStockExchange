# R4 §0009 Stage 3 (A1) — acceptance soak findings

**Soak**: 60 min on `kse_soak` port 5080, branch tip `1bc6483` (A1 patch on top of Stage 2 `d89520f`). Probes ON (MatchSymmetryProbe + DepthContextEnabled + BotDecisionProbe).

## Acceptance gate result: ❌ FAILS

Two of three gates not met. Patch is structurally clean (conservation invariants hold) but the formal acceptance criteria are not satisfied.

| Gate | Threshold | A1 result | Pass? |
|---|---|---|---|
| Bear tail ≤ 5pp from upper tail | \|min\| within 5pp of max | 19.58 pp gap (−10.07 vs +29.65) | ❌ |
| CK / CONS / ERR = 0 | 0 across full run | 0 / 0 / 0 | ✅ |
| Throughput ≥ 2.4k/min | (Stage 2 baseline 2.69k/min) | **849 trades/min** | ❌ (-66%) |

## Drift comparison vs Stage 2 baseline

| Metric | Stage 2 t=60m | Stage 3 A1 t=60m | Δ |
|---|---|---|---|
| avg drift | −1.37 | **−0.45** | +0.92 pp ✅ |
| max | +25.07 | **+29.65** | +4.6 pp ⚠ (upper widened) |
| min | −10.90 | **−10.07** | +0.83 pp |
| medianAbs | 2.56 | **1.23** | -52% ✅ |
| trades | 150,085 | **50,930** | -66% ❌ |
| stddev | 5.72 | 5.34 | tightened |

**Where A1 succeeded**: drift centered closer to zero, medianAbs cut in half, conservation perfectly clean. The tail-symmetry rebalancing did happen at the substrate level (see Block 5).

**Where A1 failed**: throughput crashed; upper tail expanded by 4.6 pp.

## Block 4 (MM quote ratio) — the patched surface

| | Stage 2 (210m) | Stage 3 A1 (60m) | Δ |
|---|---|---|---|
| choseBuy | 164,725 (74.6%) | 9,828 (74.4%) | unchanged |
| choseSell | 55,916 (25.4%) | 3,384 (25.6%) | unchanged |
| Aggregate net buy-quote bias | +0.493 | +0.488 | virtually unchanged |
| Per-bot mean (b−s)/(b+s) | +0.0346 | **−0.0934** | **flipped sell-skewed** |

**Key insight**: the aggregate 74:26 buy:sell ratio is unchanged because the **strict-inequality branch** (the dominant path) was untouched. A1 only affects the tied-decision case. Most MM decisions are NOT ties — they're strict `buys != sells` where the existing logic already steers correctly.

But the per-bot mean flipped from slight buy-skew (+0.035) to moderate sell-skew (−0.093). This means **per-bot MM behavior shifted**: bots now have fewer resting buys than sells on average, so they keep choosing buy via strict inequality. The aggregate ratio looks the same but the underlying state is different.

## Block 5 (depth context) — the substrate response

| | Stage 2 (210m) | Stage 3 A1 (60m) |
|---|---|---|
| buy-taker hit ask wall (mean) | 51,932 | **4,365** |
| sell-taker hit bid wall (mean) | **68,356** | 3,890 |
| Bid:ask wall ratio | 1.32× (bid thicker) | **0.89× (ask now thicker)** |

**A1 DID rebalance walls — over-corrected slightly.** The ask wall is now ~12% thicker than the bid wall (was 32% thinner). The walls are smaller in absolute terms because the soak is 60m not 210m and throughput is lower.

## Why throughput crashed

Hypothesis: by making MMs place more limit sells, the ask wall accumulates with **less aggressive** sell orders (they sit further from mid because MMs don't always optimize their sell prices the same way they optimize buys). The ask wall is thicker but less crossable; buy-takers find fewer aggressive asks to cross.

Combined effect: more resting liquidity, fewer matched crossings → throughput drops.

The conservation invariants hold because no bot is losing money or share state — they're just placing more limits that don't cross. But the matcher does less work.

## Why upper tail expanded

Counter-intuitive but consistent with the throughput finding: with fewer total trades, individual taker-driven price moves have larger relative impact. The same taker that walked 5 levels in Stage 2 walks just as far in A1, but there are fewer counter-takers behind it to absorb the move. So peak-to-trough excursions widen.

The bear tail stayed roughly the same (−10) — A1 successfully neutralized the bid-wall-driven bear-tail mechanism — but the upper tail widened because the ask-wall is now thinner-in-absolute-terms (lower throughput → smaller book).

## Decision-side asymmetry (Block 2) is unchanged

| | Stage 2 | Stage 3 A1 |
|---|---|---|
| mean(buy_prob − 0.5) | +0.275 | +0.253 |
| homeostatic contribution | 250.6% | 279.9% (FIRES at 272.7%) |
| All other components | <25% | <4% |

**Same dominant pattern**: cashHomeostasis still drives the buy bias overwhelmingly. A1 didn't touch this — couldn't have, since it lives in plain-path ChooseOrderType and is seeded from per-bot BuyBiasPrc (Excel scope).

## Synthesis

A1 is **structurally clean** but **insufficient**. The MM tie-break was indeed a real asymmetry — but it accounted for less of the bear-tail mechanism than Stage 2's data suggested in isolation. The **dominant** driver remains the decision-side buy bias (cashHomeostasis at ~275% contribution) which is upstream of the matcher and outside Stage 3's scope (Excel pipeline).

The wall-thickness rebalance worked: bid wall thinned, ask wall accumulated. But the upstream decision flow still produces 3.4× more buy intent than sell intent, so the system still puts buys onto the book where sells punch through to fill. With balanced walls AND too few aggressive takers (throughput crash), price excursions widen instead of tighten.

## Recommendation: Stage 4 brief

A1 should likely be **kept committed** (it's correct) but **augmented**. Two paths:

### Stage 4 Path 1 — Option D (liquidity-aware order placement)
Layer asymmetric limit offsets in `ComputeOrderPriceAsync`. When the bot is about to place a limit ASK and the ask side is thin (`book.SumQuantity(buySide: false) < bid_depth`), place CLOSER to mid (more aggressive — fills the gap that A1's thicker-ask-wall created). When placing a limit BID and the bid wall is already thick, place FURTHER from mid (less aggressive — slows wall rebuild).

This attacks the throughput-crash root cause directly: A1 builds more sells but they're not aggressive enough to be crossed. Option D makes them more aggressive when the substrate needs them.

### Stage 4 Path 2 — revisit `Tools/` BuyBiasPrc with user approval
The Stage 2 + Stage 3 data both agree: cashHomeostasis drives 250-280% of the asymmetry signal. This is a per-bot configuration question. If user approves a `/Tools` edit, regenerating bot personas with lower mean `BuyBiasPrc` (e.g. 0.50 instead of 0.55-0.65) would directly address the root cause. Net throughput effect would depend on whether sell decisions also produce fills — which Stage 3 showed they do, just more slowly than buy decisions did before A1.

### Stage 4 Path 3 — revert A1, take a different surgical approach
If neither D nor Tools is palatable, A1 could be reverted and Stage 4 attempts a different specific lever (sell-side market-take aggressiveness boost when ask is thin, for example). But this is the least appealing because A1 was the most direct attack on the most measurable asymmetry.

**Local recommendation**: keep A1, add Option D on top in Stage 4. Path 2 (Tools/) only if user explicitly authorizes it.

## Artefacts

- `KieshStockExchange.Server/logs/match-symmetry-probe.csv` — 9 MB (gitignored)
- `KieshStockExchange.Server/logs/bot-decision-probe.csv` — 2 MB (gitignored)
- `docs/R4_0009_STAGE3_A1_ANALYSIS_STDOUT.txt` — full analysis output
- Soak log: `logs/soakP-kse_soak-20260613-024021.log`
- This file: `docs/R4_0009_STAGE3_A1_FINDINGS.md`

## Test status

- A1 patch applied cleanly via `git am`
- Server builds clean in Release
- 21/21 R4-scope tests pass (no regression from A1)
