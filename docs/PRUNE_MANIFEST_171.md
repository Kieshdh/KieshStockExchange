# Prune Manifest — Task #171 (prepared 2026-07-13, Kiesh-gated; NOTHING deleted yet)

Cross-checked against `docs/FLAG_REGISTER.md` + `docs/PRUNE_PROPOSAL.md`. All rows default-off today.

## Unambiguous DELETE (both prior docs agree)
| Lever | Code | Tests |
|---|---|---|
| `Bots:Sentiment:CoMovement:*` | BotSentimentService + FundamentalService CoMoveShift | CoMovementDeterminismTests |
| `Bots:ExogShock:ChaserStrength/ChaserScale` (v1, retired log-only) | AiTradeService reads | — |
| `Bots:Imbalance:Inertia:SentimentModulated` | AiBotDecisionService SentimentModulatedMaxSec | InertiaStanceTests slice |
| `Bots:RefillThrottle:*` | RefillThrottleGate.cs + RefillThrottleProbe.cs | RefillThrottleDeterminismTests |
| `Bots:TouchTightenPrc` | AiTradeService L883 | TouchTightenTests |
| `Bots:Advanced:BatchShortOpens/BracketBatch/BatchBuyStops` (NOT BatchArms — baked WINNER) | AiTradeService + TradeSettler | MarketShortBatch*Tests |
| `Bots:Sentiment:SlowRingDamp` | BotSentimentService | — |
| `Db:PerCurrencyGroupGates`, `Bots:Arbitrage:{SharedScan,EventDriven,BatchRebalance,BatchLegs}` | per FLAG_REGISTER | SharedScanEquivalenceTests |
| DirectionalReactionLag / AnchorReactionLag / AnchorDeadbandPrc / ImpactDecouple* / ImpactHoldProbe | AiBotDecisionService | — |

## KEEP (validated/live infra)
`PerStockSigmaMult`/`GlobalSigmaMult` (shipped corr dial) · `Bots:MarketMakerQuoting` (strategy-0, on) · `Advanced:BatchArms` (baked winner) · everything WINNER/INTERIM in FLAG_REGISTER · the new Composition/wick/ramp levers.

## ASK KIESH (docs conflict or held-as-tool)
| Lever | Why held |
|---|---|
| `ExogShock:GlobalFraction`+`GlobalCoFire*` + chaser v2 keys | co-fire = validated 5-10min corr lift; PRUNE_PROPOSAL keeps as ship bundle |
| `TrendFollower:*` | buyProb half dead, taker-momentum half = held chart-realism tool |
| `PerceivedPriceDesync` | "cleanest lever," sub-gate — keep-as-tool candidate |
| `ValueAnchor:Elastic*` | LOSER by register, HOLD by proposal (Kiesh's elastic-band vision) |
| `Bots:Jumps:*` | validated igniter, held for jump+anchor combo |
| `Bots:MarketMaker:*` cohort (strategy 6) | inert-alone, unseeded |
| `Bots:Scaler:{DutyCycleDenominator,ActionableSpanSizing,MaxTickMultiple,SelfCorrectingDelay}` | throughput product curve — explicit "surface for Kiesh" |
| `MaxArmedStopsPerBot`/`ArmedStopCapProbe` | reverted but "could help on a clean pool" |
| `Sentiment:GlobalShock:*` | future elevator-down |
| `Imbalance:ReactionPersistence:*` | Kiesh said keep default-off tool |
| `Db:SynchronousCommit` | perf knob, out of realism-prune scope |

## Execution (when Kiesh approves)
One PR, same window as the reseed. Inside-out order: (1) delete dedicated test files → (2) leaf helper classes → (3) config→ctor wiring + branch reads → (4) appsettings keys + comments (+ prod overrides). Full `dotnet test` + one client build gate. No soak needed for proven-dead default-off rows. Do NOT touch BatchArms / MarketMakerQuoting / SigmaMults.
