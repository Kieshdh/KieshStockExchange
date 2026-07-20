# Market-balancing reference — fixes + every config constant (2026-06-27)

The complete set of levers that shape the AI-bot market, from `KieshStockExchange.Server/appsettings.json`
(`Bots:*`) plus the in-code knobs. **State** column: **BAKED** = on, shaping behavior; **OFF/TOOL** = default-off
(byte-identical when off), available but not baked. Source of truth is the branch `feature/bot-market-realism-v2`;
**bounce-mid is the one BAKED item not yet on prod (held for cutover)** — most other baked levers landed on prod in
the earlier realism+perf rounds.

---

## PART 1 — THE FIXES (the realism arc)

### ✅ Baked wins (the market's character comes from these)
- **Value-anchor + ±20% hard cap** (`ValueAnchor`) — the core restoring force; runaway structurally impossible.
- **RecentAnchor** + **Fundamental OU** — medium-term mean-reversion + a slowly-drifting anchor target.
- **RegimeDrift** (System A) — per-stock common-mode random walk → the chart visibly *wanders* (user "looks good").
- **SentimentDynamics** — slope-aware phase model (momentum/scalper/reversion convictions).
- **Imbalance: Inertia + Herding** + **DirectionalPressure (multiplicative)** — emergent order-flow imbalance makes
  the chart MOVE (not per-bot prob tuning); the multiplicative form preserves cohort spread at extremes.
- **Activity field** (Hawkes self-exciting) — volume CLUSTERS and breathes.
- **Range/wicks** + **FatTails (trade size)** — microstructure spikes + heavy-tailed trade sizes.
- **Drift control:** GreedStyle (kills panic sell-skew) + CashHomeostasis (restoring force to the cash band).
- **bounce-mid** (`BounceReference=mid`) — candle CLOSE off the matcher mid-price → **ret_acf −0.43 → −0.17**
  (the headline realism win). *(BRANCH-baked, prod HELD.)*
- **Anti-sweep depth cap**, **tiered limit ladder**, **liquidity-aware placement**, **round-snap declump**,
  **stop-cascade breaker**, **arbitrage cohort + FX house**, **per-stock personality**, **basic MM quoting**.

### 🧰 Default-off tools (tried, kept as flags, NOT baked)
- **CoMovement** — cross-stock co-movement. **Structural no-bake** (6 soaks): the weak anchor can't translate a
  shared signal to correlated price. Off.
- **PerceivedPriceDesync** — the 72% reaction-loop lever; sub-gate (asymptotes ~−0.18). Off.
- **ImpactDecoupleReference/Hold** — self-impact decouple; liveliness-confirmed null. Off.
- **ExogShock + Chaser** — exogenous flow for ret_acf; works but inherent down-drift. Off.
- **MarketMaker cohort** — all-weather two-sided liquidity; ret_acf-inert alone + churn-heavy. Off.
- **SmoothedPriceHalfLifeSec, DirectionalReactionLag, AnchorReactionLag, AnchorDeadbandPrc, PriceTickDecimals,
  Sentiment:PriceReaction(#2)/Mom(#3), SlowRingDamp** — ret_acf timing experiments, all null/sub-gate. Off.
- **BuyStopFraction** — symmetric short-protective buy-stops; low-impact (~0.5% short inventory). Off.

### ⛔ Closed / structural (no config knob moves them)
- **ret_acf flow half (72%)** — fleet reaction loop; structural at safe dials (5 levers failed).
- **Cross-stock correlation** — unreachable at safe dials (anchor too weak to impose a shared signal).
- **Fat-tail kurtosis** — config dead-end (the ±20% per-move cap clips the tail) → needs the **jump engine lever**
  (fat-tails ultraplan, PR pending).

---

## PART 2 — CONFIG CONSTANTS BY FUNCTION

### A. Sentiment / mood (directional bias source)
| Knob | Value | State | Balances |
|---|---|---|---|
| `PersonalSentiment` | true | BAKED | per-bot sentiment variety |
| `SentimentMaxBias` | 0.1 | BAKED | regime bias weight (augment, not double-count) |
| `Sentiment:RegimeDrift:{Enabled,StepSigma,Cap,SoftWallK,Strength}` | true, 0.03, 0.5, 0.1, 1.0 | BAKED | per-stock multi-min trend/wander |
| `SentimentDynamics:{SlopeTauFast/Slow, SlopeScaleFast/Slow, MomentumConviction, ScalperConviction, ReversionConviction, ReversalConviction, MarketMakerLean, AggressionBoost}` | 45/180, 0.01/0.005, 0.15/0.12/0.15/0.10/0.05/0.10 | BAKED | slope-aware trend/reversal phase model |
| `Sentiment:SlowRingDamp` | 1.0 (off) | OFF | damp slow OU rings (drift source) |
| `Sentiment:PriceReaction` + `ReactStrength/ReactTauSec/ReactDeadband/ReactCap` | false; 6.0/300/0.01/0.40 | OFF | #2 contrarian price→sentiment |
| `Sentiment:MomStrength/MomTauSec/MomCap` | 0/60/0.25 (off) | OFF | #3 FOMO momentum waves |
| `Sentiment:CoMovement:{Enabled,StepSigma,Cap,SoftWallK,Strength,BetaSpread,ShiftCap}` | false; 0.03/0.4/0.1/0.15/0.4/0.08 | OFF/TOOL | cross-stock co-movement (structural no-bake) |
| `NewsEvents`, `ShockMeanIntervalHours`, `ShockMin/MaxMagnitude`, `ShockMagnitudeExponent`, `ShockDecayPerTick` | true, 12, 0.05/0.2, 3.0, 0.999 | BAKED | per-stock decaying news shocks |
| *(in code)* sentiment OU rings | per-stock τ {20,90,360,1800,10800}s σ {.25,.25,.20,.12,.08}; global τ {600,3600,21600} σ {.10,.08,.06} | BAKED | the mood mixture |

### B. Anchoring / runaway guard (mean-reversion to value)
| Knob | Value | State | Balances |
|---|---|---|---|
| `ValueAnchor:Strength` / `Scale` | 0.40 / 0.12 | BAKED | restoring-force tilt + saturation |
| `ValueAnchor:OverheatCap` | 0.3 | BAKED | per-stock veto threshold |
| `ValueAnchor:AbsoluteCapMax` | 0.20 | BAKED | **hard ±20% price cap** (the runaway backstop) |
| `ValueAnchor:UsePreviousDayAverage` / `WindowDays` / `MaxDailyDrift` / `DayLengthHours` / `DayBoundaryMode` | true / 7 / 0.5 / 24 / ServiceStart | BAKED | weighted-week TWAP anchor target |
| `ValueAnchor:CapFromSeed` | true | BAKED | cap measured from seed (not moving target) |
| `ValueAnchor:TargetSelection` | false | OFF | (destabilizing — keep off) |
| `RecentAnchor:{Enabled,HalfLifeSec,Strength,Scale}` | true, 1800, 0.10, 0.04 | BAKED | 30-min EWMA mean-reversion (fades fast moves) |
| `Fundamental:{Enabled,Band,Theta,Sigma,DriftIntervalSeconds}` | true, 0.12, 0.02, 0.004, 60 | BAKED | the OU fundamental the anchor tracks |
| `Anchor:FastSlack` | 0 (off) | OFF | widen intraday overheat band |
| `AnchorReactionLag` + `AnchorLagMin/MaxAlpha` | false; 0.05/0.30 | OFF | Lateness-lagged anchor (R5 #B) |
| `AnchorDeadbandPrc` | 0 (off) | OFF | anchor deadband (R5 #C) |

### C. 1-min mean-reversion / ret_acf (microstructure + reaction loop)
| Knob | Value | State | Balances |
|---|---|---|---|
| `BounceReference` | "mid" | BAKED (branch) | **the bounce fix: ret_acf −0.43→−0.17** |
| `PriceTickDecimals` | 0 (off) | OFF | finer print grid (confirmed dud) |
| `SmoothedPriceHalfLifeSec` | 0 (off) | OFF | lagged price bots react to |
| `DirectionalReactionLag` + `DirLagMin/MaxAlpha` | false; 0.05/0.30 | OFF | #1 Lateness-lagged directional |
| `PerceivedPriceDesync` + `PerceivedPriceMin/MaxAlpha` + `PerceivedSlopeScaleFast/Slow` | false; 0.05/0.45; 0.01/0.02 | OFF/TOOL | per-bot perceived-price desync (sub-gate) |
| `ImpactDecoupleReference` + `…HalfLifeSec` / `ImpactDecoupleHold` + `…HoldWindowSec` / `ImpactHoldProbe` | false; 240 / false; 90 / false | OFF/TOOL | self-impact decouple (null) |
| `RoundSnapProb` / `RoundSnapSpread` | 0.30 / 0.40 | BAKED | order-wall declump (round-snap dispersion) |

### D. Trend / movement (emergent imbalance — chart MOVES)
| Knob | Value | State | Balances |
|---|---|---|---|
| `Imbalance:Inertia` + `:MinSec/:MaxSec/:Leak` | true; 120/1800/0.1 | BAKED | directional persistence (A1) |
| `Imbalance:Herding` + `:FollowerFraction/:Tilt/:RegimeMeanSec` | true; 0.25/0.1/300 | BAKED | herding amplifier (A2) |
| `Imbalance:MomentumDominance` + `:Strength` | false; 0 | OFF | A3 refinement |
| `Imbalance:RoleSplit` + `:NoiseDamp` | false; 1.0 | OFF | A4 noise/directional split |
| `DirectionalPressure:Multiplicative` + `:DiversityGain` | true; 1.5 | BAKED | hybrid multiplicative pressure |

### E. Volume / activity (clustering + breathing)
| Knob | Value | State | Balances |
|---|---|---|---|
| `Activity:{Enabled,Baseline,GlobalTauSec,GlobalSigma,PerStockTauSec,PerStockSigma,Floor,SMax,Gamma,WNews,WMoveUp,WMoveDown,WSent,Theta,WSelf,Decay,BDriftAmp}` | true,0.6,3600,0.2,600,0.3,0.2,6,1,0.6,1,2,0.3,0.3,0.009,0.99,0.15 | BAKED | Hawkes self-exciting volume (n≈0.9) |
| `MarketProbMult` | 1.5 | BAKED | more taker/market orders → volume |
| `CashInjection:IntervalMinutes` | 30 | BAKED | cash top-ups → buying/volume |

### F. Range / wicks (microstructure spikes + fat trade sizes)
| Knob | Value | State | Balances |
|---|---|---|---|
| `Range:{ActivityImpact,MaxSlippage,FatImpactProb}` | true,0.02,0.04 | BAKED | wicks on hot names + calm-period spikes |
| `FatTails` / `TradeSizeTailShape` | true / 0.3 | BAKED | heavy-tailed trade sizes |
| `BlockTradeProb` / `BlockTradeMultiple` | 0.01 / 2 | BAKED | occasional block trades |
| `MarketSlippagePrc` | 0.003 | BAKED | base market-order slippage |

### G. Drift control (hug the seed)
| Knob | Value | State | Balances |
|---|---|---|---|
| `ExtremeReaction:GreedStyle` / `GreedSplit` | true / 0.5 | BAKED | neutralize panic 2:1 sell-skew |
| `CashHomeostasis:{Continuous,MaxShift,EdgeForceBuy,EdgeForceSell}` | true,0.30,0.65,0.35 | BAKED | restoring force to cash-band midpoint |
| `Advanced:BuyStopFraction` | 0 (off) | OFF/TOOL | symmetric buy-stops (low-impact: ~0.5% shorts) |
| `Advanced:InventoryBiasShortMult` | 2.0 | BAKED | short-side threshold divisor (bear-tail tune) |

### H. Limit-order placement / ladder
| Knob | Value | State | Balances |
|---|---|---|---|
| `DecisionDistanceMult` | 0.2 | BAKED | global order-distance tightness (stacks on Excel ×0.32) |
| `Tiers:CloseProb` / `MidProb` | 0.85 / 0.10 | BAKED | limit-ladder tier probabilities |
| `Liquidity:OffsetMult` / `MaxOpenMult` / `MaxSweepFractionOfDepth` | 1.0 / 1.0 / 0.25 | BAKED | ladder offsets + anti-sweep depth cap |
| `LiquidityAwarePlacement` / `LiquidityAwareGain` | true / 0.2 | BAKED | tilt limits by book imbalance |

### I. Market-making / spread
| Knob | Value | State | Balances |
|---|---|---|---|
| `MarketMakerQuoting` / `QuoteHalfSpreadPrc` | true / 0.003 | BAKED | basic two-sided quoting |
| `MarketMaker:{Enabled,HalfSpreadBps,QuoteSize,SkewBps,RequoteThresholdBps,MaxCashFrac,PriceJitterBps,OneSidedWidenMult,UseMicro,Probe}` | false,15,5,20,5,0.5,2,2.0,false,false | OFF/TOOL | all-weather MM cohort (healthy params: cohort≤8, RequoteThreshold≥50) |

### J. Advanced orders (stops / brackets)
| Knob | Value | State | Balances |
|---|---|---|---|
| `Advanced:{Enabled,StopOffsetPrcMin/Max,TpOffsetPrcMin/Max,BracketSlippagePct,StopSlippagePct,MaxQty,MaxPerTick}` | true,.006/.016,.01/.025,0.5,0.3,25,50 | BAKED | stop/bracket geometry (per-bot in Excel; these = fallbacks) |
| `Advanced:BatchArms` | true | BAKED | batched stop/trailing arm route (perf) |
| `Advanced:BracketBatch` | false | OFF | batched bracket/short route (no win) |
| `StopBreaker:{MaxPromotionsPerWindow,WindowSeconds}` | 3, 10 | BAKED | stop-cascade circuit breaker |

### K. Exogenous shock / chaser (ret_acf flow tool — OFF)
| Knob | Value | State | Balances |
|---|---|---|---|
| `ExogShock:{Enabled,MeanIntervalMinutes,DecayHalfLifeSec,MinMagnitude,MaxMagnitude,MagnitudeExponent,Cap,SoftWallK,AnchorTracksShock,ChaserNotionalFrac,ChaserMaxNotionalFrac,ChaserFraction,…}` | false,3,300,0.01,0.06,1.8,0.06,0.1,false,0,0.02,0,… | OFF/TOOL | news shocks + marketable chaser flow (drift → no-bake) |

### L. Arbitrage / FX house
| Knob | Value | State | Balances |
|---|---|---|---|
| `Arbitrage:{Enabled,ValueDrainCeilingPct,ConversionSkewBand,BatchLegs}` | true,5.0,0.03,false | BAKED | cross-listing arb + FX-desk house profit |
| `Platform:HouseUserId` | 20002 | BAKED | the house account id (config-driven) |

### M. Personality
| Knob | Value | State | Balances |
|---|---|---|---|
| `Personality:Enabled` | true | BAKED | per-stock volatility class Calm..Meme (amp, fundamental σ, veto) |

### N. Engine / perf (indirect — affects achievable bot count)
| Knob | Value | State | Balances |
|---|---|---|---|
| `Staggering:{Enabled,Slots}` | true, 2 | BAKED | per-tick load-cut (higher equilibrium bot count) |
| `Db:GroupCommit:Enabled` / `Db:PerCurrencyGroupGates` | false / false | OFF | commit-batching / per-ccy gates (no win) |
| `OrderMaxAgeSec` | 0 (off) | OFF | age-based order expiry (caps book growth) |
| `MaxBotCap` *(code)* | 20000 | BAKED | max bot cap (scaler ramps to it) |

---

## PART 3 — In code, not appsettings: `FxRateService.cs` (FX/parity)
| Constant | Value | Note |
|---|---|---|
| `Alpha` | 0.92 | AR(1) mean-reversion |
| `Amplitude` | 0.005 | per-step volatility — **S1: too hot (~50-100× real); recommend → 0.0015** |
| `RateBand` | 0.20 | clamp ±20% — **recommend → 0.05** |
| `ConvertSpread` | 0.001 | FX-desk spread (house profit) |
| `BaseMidRate` EUR/USD | 1.08 | base parity |
*(mirror in `Tools/Config.py` FX_*; config + restart, no reseed. Cross-currency parity itself is GOOD — only the rate's volatility is the issue.)*

## PART 4 — Pending / staged (not yet live)
- **Fat-tail jump lever** (`Bots:Jumps:*`) — ultraplan PR in flight; rare large CK-safe price jump for kurtosis.
- **FxRate damping** — awaiting your chosen number (rec above).
- **Marketcap / 20k-bot split** — DB `SharesOutstanding` column landed; seed-gen + client column + reseed staged.
- **bounce-mid prod deploy** — branch-baked, held for your cutover.
