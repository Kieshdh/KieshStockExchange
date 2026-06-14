# R4 Realism Research — Autonomous 24h Session Plan

**Started**: 2026-06-13 16:55 UTC (after R4 §0009 Stage 6C ship)
**Budget**: ~24h autonomous (user back ~17:00 UTC 2026-06-14)
**Goal**: empirically find config that produces "most realistic" candle data — not flat, with similar wicks across stocks/timeframes, matching published stylized facts of asset returns.

## Realism criteria (synthesized from literature)

Per **Cont (2001)** and **Revisiting Cont's stylized facts** (Sun et al. 2024), modern equity markets show:

| Stylized fact | Target | How I'll measure |
|---|---|---|
| **Return autocorrelation** | ≈ 0 at any τ ≥ ~20min; small positive at 1-5min microstructure | `acf(r_t, lag=1..10)` on 1-min log returns |
| **Volatility clustering** | acf(\|r\|) decays as power-law β ∈ [0.2, 0.4] | acf of abs returns, fit exponent |
| **Heavy tails** | Tail index α ∈ [2, 5] for 1-min returns | Hill estimator on tail of \|r\| |
| **Kurtosis (excess)** | > 3 for 1-min, → 0 as τ → daily | scipy.stats.kurtosis on 1-min returns |
| **Leverage effect** | ρ(r_t, |r_{t+1..k}|) < 0, ~ -0.1 to -0.3 | rolling correlation |
| **Volume-volatility** | Positive correlation, ρ ≈ 0.2-0.5 | corr(volume, range) per 1-min bar |
| **Candle shape** | body/range mean 0.4-0.6 (Random Walk = 0.62 reference) | direct measure |
| **Has-wick %** | > 80% in 1-min bars (close ≠ high AND close ≠ low) | direct measure |
| **Body/range distribution mode** | 30-70% bracket (Body Ratio Indicator categories) | histogram |

**Anti-patterns** (flag as unrealistic):
- Body/range = 1.0 (perfectly directional, no wicks) for a meaningful fraction of bars
- Symmetric return distribution (skew ≈ 0 exactly)
- Kurtosis ≈ 3 (Gaussian)
- Zero volatility clustering (acf(|r|) decays to 0 immediately)
- Constant body/range across stocks (no class differentiation)
- Identical candles across time (no volatility variation)

## Plan structure

### Phase 0 (now — 22:15 UTC)
- 3.5h validation soak running on **Stage 6C** config (8fb220a, brackets off in Tools/)
- Stock chart classification + candle-stats script (`scripts/r4_realism_metrics.py`)
- Define A/B harness that swaps one config knob and re-runs short soaks
- Synthesize literature targets into a single `realism_score` function

### Phase 1 (22:15 — ~02:00 UTC, ~4h)
- Score the 3.5h validation baseline on every realism metric
- Identify the WORST 2-3 metrics → those are the targets

### Phase 2 (~02:00 — ~10:00 UTC, ~8h)
- Iterate config knobs to improve those metrics. Knobs available:
  - `Bots:SentimentDynamics:*Conviction` (G coefficients per strategy)
  - `Bots:SentimentDynamics:AggressionBoost` (taker push)
  - `Bots:ValueAnchor:Strength` / `:Scale` (anchor pull)
  - `Bots:RecentAnchor:Strength` / `:Scale` (medium-term EWMA pull)
  - `Bots:Imbalance:Inertia:*` (per-bot stance duration → autocorrelation)
  - `Bots:Imbalance:Herding:*` (regime tilt → volatility clustering)
  - `Bots:Activity:*` (volume-volatility coupling)
  - `Bots:Range:FatImpactProb` (fat-tail injection)
  - `Bots:CashHomeostasis:MaxShift` (drift restoring force)
- Each experiment: short (~45min) soak → measure → compare → keep or revert
- One commit per accepted change

### Phase 3 (~10:00 — ~15:00 UTC, ~5h)
- Best-config 3.5h confirmation soak with comparison chart vs validation baseline
- Write up findings doc with per-metric score + which knobs moved the needle

### Phase 4 (~15:00 — ~17:00 UTC, ~2h)
- Final summary, all artefacts on branch, clean ship state, probes off

## Baseline measurement (Stage 6C 60m snapshot kse_soak_6c)

**Composite realism score: 72.5 / 100** ("Good" range).

| metric | target | actual | score | verdict |
|---|---|---|---|---|
| body_ratio_mean | ~0.50 | 0.466 | 77% | ✅ |
| has_wick_pct | ≥85% | 92.2% | 100% | ✅ excellent |
| flat_pct | ≤5% | 1.6% | 100% | ✅ |
| range_vol_corr | ≥0.30 | 0.160 | 53% | ⚠ weak |
| return_kurt_excess | ≥5 | 7.41 | 100% | ✅ fat tails |
| tail_alpha | ~3.5 | 2.75 | 50% | ⚠ on fat side |
| **ret_acf_lag1** | ~0 | **−0.205** | **0%** | ❌ too mean-reverting |
| ret_acf_lag5 | ~0 | −0.022 | 56% | ok |
| absret_acf_lag1 | ≥0.20 | 0.244 | 100% | ✅ vol clustering present |
| **absret_acf_lag5** | ≥0.10 | **0.033** | **33%** | ❌ vol clustering decays too fast |
| absret_acf_lag20 | ≥0.05 | 0.146 | 100% | ✅ |

**Three weakest metrics drive most of the realism deficit**:
1. `ret_acf_lag1 = −0.20` — bots over-mean-revert at 1-min timescale (real markets ≈ 0)
2. `absret_acf_lag5 = 0.03` — volatility clustering decays too fast (target > 0.10)
3. `range_vol_corr = 0.16` — volume-volatility coupling too weak (target > 0.30)

## Planned experiments

Each experiment is a 45-min soak with one or more `appsettings.json` Bots:* overrides on the Stage 6C base. Compare composite score; the best-scoring config gets a 3.5h confirmation soak.

| # | Tag | Override(s) | Target |
|---|---|---|---|
| 1 | inertia_on | `Bots.Imbalance.Inertia = true` | Address ret_acf_lag1 — per-bot stance reduces tick flipping |
| 2 | herding_on | `Bots.Imbalance.Herding = true` | Address vol clustering — regime-tilted followers |
| 3 | anchor_weaker | `Bots.RecentAnchor.Strength = 0.20` | Reduce mean-reversion pull, address ret_acf_lag1 |
| 4 | momentum_stronger | `Bots.SentimentDynamics.MomentumConviction = 0.15`, `ScalperConviction = 0.18` | Address ret_acf_lag1 via more trend, may help vol clustering |
| 5 | activity_higher | `Bots.Activity.Gamma = 1.5` | Address range_vol_corr — activity boosts volume concentration |
| 6 | inertia + herding | combine 1+2 | Compound emergent dynamics |
| 7 | inertia + momentum | combine 1+4 | Compound trend |
| 8 | all_emergent | 1+2+3+4+5 | Full Lux-Marchesi-style emergent agents |
| 9 | fatimpact_higher | `Bots.Range.FatImpactProb = 0.05` (from 0.02) | Address tail_alpha, may help vol clustering |
| 10 | calm_off | `Bots.SentimentDynamics.Enabled = false` | Baseline check — what does the system look like without slope-aware dynamics? |

## Experiment log

Each row = one config experiment. Date format: HH:MM UTC.

| When | Tag | Override | Composite | ret_acf_1 | absret_acf_5 | rv_corr | trades/min | Verdict |
|---|---|---|---|---|---|---|---|---|
| 14:55 | baseline_60C | (none) | 72.5 | -0.205 | 0.033 | 0.160 | 3,547 | reference 60m |
| 22:16 | val-baseline | (none, 3.5h) | **58.3** | **-0.474** | **-0.009** | 0.228 | **5,047** | 3.5h reveals weakness: brackets-off removed bracket-driven drift BUT ALSO removed bracket-driven wicks/spikes → fat tails (kurt 0.2!) and vol clustering (lag-5 0!) collapsed; bot decisions still over-mean-revert (lag-1 -0.47). Throughput +83% vs Stage 4. CK clean. Need to re-introduce stylized-fact noise WITHOUT cascading-SL asymmetric drift. |
| 22:18 | exp1 herding | Imbalance.Herding=true | 54.0 | -0.325 | 0.16 | 0.13 | 5,038 | +lag-5 vol clustering, -kurt |
| 23:04 | exp2 momdom | Imbalance.MomentumDominance=true,strength=0.2 | 39.1 | -0.325 | -0.05 | 0.05 | ~5k | +kurt 5.5 but destroys vol clustering everywhere |
| 23:49 | exp3 herd+momdom | both | 50.7 | -0.330 | -0.08 | 0.30 | ~5k | mixed wins |
| 00:35 | exp4 momconv 0.20 | SentimentDynamics.MomentumConviction=0.2, ScalperConviction=0.2 | 47.5 | -0.236 | -0.06 | 0.15 | ~5k | +kurt 4.66 +lag-1 cluster 0.32, -lag-5 |
| 01:21 | exp5 fatimpact+activity | Range.FatImpactProb=0.06, Activity.Gamma=1.5 | 48.4 | -0.394 | -0.004 | 0.42 | ~5k | +kurt 9.6 +rv_corr, -has_wick% |
| 02:07 | exp6 anchor_weak | RecentAnchor.Strength 0.35→0.15 | 43.4 | -0.368 | -0.015 | 0.12 | ~5k | minor |
| 02:53 | exp7 rev_lower | SentimentDynamics.ReversionConviction=0.05, ReversalConviction=0.05 | 51.1 | -0.414 | -0.042 | 0.27 | ~5k | +body |
| 03:39 | **exp8 combo** | Herding+MomConv 0.15+ScalpConv 0.12+FatImp 0.04 | **61.1** | -0.320 | **0.083** | 0.09 | ~5k | **first to beat baseline** + kurt 6 + lag-5 cluster |
| 04:26 | exp9 combo+act1.5 | exp8 + Activity.Gamma=1.5 | 60.6 | -0.185 | 0.067 | **0.335** | ~5k | range_vol_corr fixed (+) |
| 05:12 | exp10 combo+lowerRev | exp8 + RevConv 0.08 + Activity.Gamma 1.2 | 57.7 | -0.283 | -0.078 | 0.40 | ~5k | -kurt -lag-5 |
| 05:57 | **exp11 inertia_long** | exp8 + Inertia 120-1800s (was 30-600s) | **73.7** | -0.326 | **0.144** | **0.357** | ~5k | **CHAMPION** — 6 metrics at 100%, kurt 8.0, vol clustering 0.21/0.14/0.08 at lags 1/5/20 |
| 06:43 | exp12 lower_homeo | exp11 + CashHomeostasis.MaxShift 0.30→0.20 | 49.8 | -0.247 | -0.030 | 0.07 | ~5k | broke vol clustering |
| 07:29 | exp13 vanchor_lower | exp11 + ValueAnchor.Strength 0.5→0.3 | 42.5 | -0.423 | 0.079 | 0.05 | ~5k | broke clustering and rv_corr |
| 08:15 | exp14 only_long_inertia | only Inertia 120-1800s (no other exp11 changes) | 41.8 | -0.464 | -0.214 | 0.34 | ~5k | confirms exp11 wins via the COMBINATION, not inertia alone |
| 09:01 | exp15 symmetric_act | exp11 + Activity.WMoveDown 2.0→1.2 | 51.2 | -0.333 | -0.020 | 0.04 | ~5k | broke vol clustering by symmetrizing |
| 09:47 | exp16 inertia_xlong | exp11 + Inertia 180-3600s | 48.2 | -0.370 | 0.019 | 0.12 | ~5k | too long — broke lag-5,20 clustering |
| **CHOICE** | **WINNER = exp11** | Herding + MomConv 0.15 + ScalpConv 0.12 + FatImp 0.04 + Inertia 120-1800s | **73.7** | | | | | Applied to appsettings.json |

## Notes + observations log

| When | Note |
|---|---|
| 16:55 | Plan written. Literature synthesized: Cont 2001 + Sun 2024 + Garman-Klass / Parkinson refs. Hill estimator + acf decay are the two critical metrics. |
| 16:55 | Stage 6C 3.5h validation running (`bo0etxvmm`). Brackets disabled in Tools/Person.py committed at 8fb220a. |
| 16:55 | Existing candle analysis at `scripts/candle_realism.py` measures body/range vs RW baseline. Will extend with autocorrelation, kurtosis, Hill, leverage. |

## Sources

- [Revisiting Cont's stylized facts for modern stock markets (arXiv 2311.07738)](https://arxiv.org/pdf/2311.07738)
- [Cont — Empirical properties of asset returns (Quantitative Finance 2001)](http://rama.cont.perso.math.cnrs.fr/pdf/empirical.pdf)
- [Econophysics: Empirical facts and agent-based models (arXiv 0909.1974)](https://arxiv.org/pdf/0909.1974)
- [Garman-Klass volatility estimator (CME paper)](https://www.cmegroup.com/trading/fx/files/a_estimation_of_security_price.pdf)
- [Range-Based Volatility Estimators (Portfolio Optimizer)](https://portfoliooptimizer.io/blog/range-based-volatility-estimators-overview-and-examples-of-usage/)
- [Body Ratio Indicator categories (MarketBulls / FxOpen)](https://market-bulls.com/candlestick-wicks/)
