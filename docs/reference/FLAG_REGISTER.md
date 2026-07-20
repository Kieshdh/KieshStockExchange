# FLAG_REGISTER.md — the `Bots:*` lever lifecycle (bake / kill / interim)

Council-mandated (2026-07-09 planning council): every flag is born with a death condition. This is the register that
drives the flag-debt paydown (task #171) and the post-IVM-PR cleanup. **State** ∈ {WINNER (baked or prod-on, keep),
INTERIM (prod-on, scheduled for demolition), LOSER (default-off dead-end, delete code+tests+config), PROBE (diagnostic
default-off, keep as a tool OR delete), VERIFY (re-confirm outcome before acting)}.
Rule (Outsider): *you don't need a soak to delete code that's default-off and proven dead.* Rule (Contrarian): the hot
path should end at ≤1–2 flags, not 5 coupled ones.

---

## WINNERS — prod-on / baked realism (KEEP; fold multipliers→seed at the eventual reseed, don't delete)
These are the deployed, converged config (market corr 0.244 / kurtosis +7.6). Not flags to prune — the *market itself*.
| Key | Where | Note |
|---|---|---|
| `Staggering:{Enabled,Slots=4}` | prod override | fixed ~10k-participant cadence |
| `DipBuyStrength=3.0` | prod override | idle-cash dip demand, floors down-drift |
| `Rotator:Enabled`, `BankEstimate:Enabled` | prod override | the taker-flow correlation engine |
| `OrderMaxAgeSec=1800` | prod override | bounds the Open *limit* pool |
| `TradeIntervalMs=250`, `PhaseTimingSeconds=20` | prod override | tick cadence + telemetry |
| baked realism (GSM2.5, RegimeDrift, co-fire ExogShock, FX-damp, GeometricBand, ValueAnchor) | base appsettings (ON) | Stage-1/2 bakes — the converged structure |

## INTERIMS — prod-on, SUPERSEDED by the incoming IVM/source-cap PR → DELETE after the PR soaks CK-clean
The stop-management stack. **Do NOT bake these.** Expand/contract: land IVM default-off → parity soak (IVM-on vs these)
→ flip IVM default → delete these + their tests in a follow-up. Kill-trigger = IVM PR passes its 45m CK-gated soak.
| Key | Commit | Role | Fate |
|---|---|---|---|
| `StopReplaceOld` | 884fd28 | cancel prior stop before arming (source netting) | IVM's source-cap subsumes → DELETE (or keep netting if IVM reuses) |
| `PruneLimitOnly` (B2) | 884fd28 | prune iterates limit-only index | IVM/off-thread subsumes → DELETE |
| `LeanReload` (B3) | cf7d96e | reload = limits + stop COUNT (index-only) | IVM incremental aggregates make the COUNT obsolete → DEMOTE to reconciliation OR DELETE |
| `StopMaxAgeSec=0` / `StopCullMaxPerSweep` | (interim) | the retired per-order TTL cull | already retired (=0) → DELETE key |
| migration `AddArmedStopPartialIndexes` | cf7d96e | indexes backing LeanReload's queries | keep only if IVM still queries; else drop in a later migration |

## LOSERS — default-off dead-ends (validated null/modest across the realism arc) → PRUNE (#171)
Per the plan-log: each shipped default-off as a "tool" after A/B showed null-or-modest. Delete code + tests + config.
**VERIFY each against the plan log before deleting** (a few were "modest, kept as tool" — Kiesh call whether to keep).
| Key(s) | Verdict in the arc |
|---|---|
| `Sentiment:CoMovement:*` (Enabled/ShiftCap/…) | co-movement via shared sentiment = STRUCTURALLY null (6 soaks) |
| `Imbalance:ReactionPersistence:*` (+ TakerCoupling/RoleSplit/MomentumDominance) | RP = modest+gain-saturated; **Kiesh bake-call was "keep default-off tool"** → VERIFY |
| `PerceivedPriceDesync` | cleanest lever but sub-gate → shipped off |
| `ValueAnchor:Elastic` / `ElasticDeadbandPrc` / `ElasticPower` / `TargetSelection` | modest intraday, needs long soak; dead-end |
| `TrendFollower:*` (Enabled/TakerCoupling/SharedChaseWeight/…) | buyProb version DEAD; taker version = chart-good but ret_acf plateau |
| `Sentiment:GlobalShock:*` / `ExogShock:GlobalCoFire*` | GlobalShock corr caps ~0.08; co-fire = the modest 5-10min lift (**partly baked?**) → VERIFY |
| `BearShortStrength` | symmetry lever; drift-neutral at 1.0 but shipped off |
| `Sentiment:PriceReaction/Mom*` | contrarian feedback; dead-end |
| `Imbalance:Inertia:SentimentModulated` | NULL/worse |
| `DirectionalReactionLag`, `AnchorReactionLag`, `AnchorDeadbandPrc` | R5 anchor-timing; neutral-within-noise |
| `ImpactDecoupleReference/Hold`, `SmoothedPriceHalfLifeSec` | impact-decouple; null |
| `RefillThrottle:*` | NO-BAKE across all 3 dials (absorbed) |
| `Advanced:BracketBatch`, `Advanced:BatchShortOpens`, `Arbitrage:{SharedScan,EventDriven,BatchRebalance,BatchLegs}` | batch/arb levers = spent / no-win |
| `Scaler:{DutyCycleDenominator,ActionableSpanSizing,SelfCorrectingDelay,MaxTickMultiple}` | R2 scaler corrections = low-tick tool, over-conservative; not on prod |
| `PerCurrencyGroupGates` | sharding gate = null (17 concurrent committers already amortize) |

## PROBES — diagnostic default-off (keep as debug tools OR delete with #171)
`MatchSymmetryProbe(+DepthContext)`, `BotDecisionProbe`, `ExogShock:ChaserProbe`, `ImpactHoldProbe`, `RefillThrottle:Probe`,
`BankEstimate:Probe`, `Fx:Probe`. Cheap when off; low reasoning load. Default: keep the ones still useful, delete the rest at #171.

## KEPT-ON (base appsettings, real features, NOT prune) — Advanced:BatchArms (baked), arb/house, retention, etc.

---
**Next action on this register:** nothing until the IVM PR lands + soaks. Then: (1) delete the INTERIM rows, (2) run #171
over the LOSER rows (verify each vs the plan log first), (3) leave WINNERS. Target: hot-path flags ≤1–2.
