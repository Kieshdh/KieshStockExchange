# Attended-Arc Prep Map: `AiBotDecisionService`

**Purpose of this doc:** pre-load the owner's eventual ATTENDED restructure session with *judgment*, not
archaeology. This class is CK-critical (it sizes every bot order against live cash/share/reservation state)
and can only be restructured with the owner present. Read this before the session; execute one extraction
per soak against the gates in §7.

---

## 1. File identity

| Field | Value |
|---|---|
| Path | `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs` |
| Exact LOC | **3063** |
| Namespace | `KieshStockExchange.Services.BackgroundServices.Helpers` |
| Declaration | `internal sealed class AiBotDecisionService` — **NOT partial**, **no base class, no interface** |
| Co-located types (same file) | `enum BotAdvancedKind` (L19); `internal sealed record BotAdvancedDecision` (L21); nested `internal readonly record struct CommittedTotals` (L2116) and `ChasePick` (L2394) |
| .csproj | `KieshStockExchange.Server/KieshStockExchange.Server.csproj` — SDK-style, globbed `**/*.cs` auto-include, so **new partial `.cs` files need NO csproj edit** |
| InternalsVisibleTo | `KieshStockExchange.Tests` and `DynamicProxyGenAssembly2` (Moq) — csproj L43/L45. The class + all its `internal static` helpers are already test-visible. |

### The behavioural oracle (20 test files exercise it)
`BotMathTests`, `DirectionalBiasTests`, `PressureFormulaTests`, `ReactionPersistenceTests`,
`InertiaStanceTests`, `ElasticAnchorTiltTests`, `DipBuyTiltTests`, `BearShortBoostTests`,
`TrendFollowTiltTests`, `MoodTakerCouplingTests`, `ActivityCompositionTests`, `ChaserDirectFlowTests`,
`AnchorTimingTests`, `TouchTightenTests`, `RoundSnapTests`, `GreedReactionTests`, `CashHomeostasisTests`,
`ArmedStopCapGateTests`, `CommittedTotalsTests`, `BotPrecomputeEquivalenceTests`.

**Key fact:** the oracle is *already* concentrated on the `internal static` math surface — the same surface
AiBotDecisionMath would extract. That is the safety net that makes the Math split near-zero risk (§4) and
also the reason BotDecisionConfig must go first (it doesn't touch any tested method body — see §3).

### Structure (regions, L37–L3062)
| Region | Lines | Contents |
|---|---|---|
| Services and Constructor | 37–701 | ~11 injected services + **~155 config fields** + the mega-ctor |
| Public Interface | 703–1372 | `CanPlaceMoreOrder`, `ComputeOrderAsync`, `ComputeAdvancedDecisionAsync`, advanced builders |
| Order Decision Logic | 1374–1843 | `ChooseOrderType`, `ChooseStockId`, `PickStock`, MM quote |
| Price and Quantity Computation | 1845–2335 | `ComputeOrderPriceAsync`, `ComputeOrderQuantityAsync`, committed totals, band veto, depth cap |
| Sentiment Integration | 2337–2888 | watchlist aggregators + the big `internal static` math block (chaser/trend/mood/anchor/pressure) |
| OrderType Enum and Helpers | 2890–2967 | order-type predicates, composition helpers |
| Math Helpers | 2969–3062 | `Lerp`/`Clamp01`/`CashHomeostasis`/`SnapToRoundNumber`/… |

---

## 2. The ~320-field ctor coupling (quantified)

The prompt's "~320-field" figure is the aggregate coupling surface. Measured precisely:

- **Constructor parameters: ~185 total.** 11 are injected services/delegates; the remaining **~155 are scalar
  config values** with defaults (`bool`/`decimal`/`double`/`int`), plus a handful of optional delegates
  (`Func<int,double>? shockOf`, `Func<int>? globalCoFireSignOf`, …) and one `IReadOnlyDictionary` and one
  optional `MarketMoodService? mood`.
- **Instance fields: ~166.** 11 service refs (`_market`, `_accounts`, `_books`, `_stocks`, `_sentiment`,
  `_funds`, `_profiles`, `_regime`, `_activity`, `_priceMemory`, `_logger`) + ~155 `readonly` config fields
  (each a 1:1 sink for a ctor param) + `LoopStartUtc` (mutable, see §3) + a few `const`s
  (`OverflowGain`, `BuySafetyBuffer`, `TailExponentScale`, and the salt consts).
- The "~320" is params + fields + call-sites read together; the load-bearing number is **~155 tunable config
  fields**, every one assigned exactly once in the ctor body (L539–L699) and read-only thereafter.

### Categories of the ~155 config fields (this is what BotDecisionConfig encapsulates)
1. **Order-size / fat tails** — `_fatTails`, `_tradeSizeTailShape`, `_blockTradeProb`, `_blockTradeMultiple`.
2. **Market-maker quoting** — `_mmQuoting`, `_quoteHalfSpreadPrc`.
3. **Liquidity / ladder / distance dials** — `_limitOffsetMult`, `_maxOpenOrdersMult`, `_distanceMult`,
   `_marketProbMult`, tier probs (`_tierCloseProb`, `_tierMidProb`), `_maxSweepFractionOfDepth`.
4. **Value anchor family** — `_valueAnchorStrength/Scale`, `_dipBuyStrength`, elastic
   (`_anchorElastic/Deadband/Power`), `_valueTargetSelection`, `_overheatCap`, `_absoluteCapMax`,
   `_geometricBand`, `_anchorFastSlack`, `_capFromSeed`, `_adaptiveAnchor`, `_maxTotalExcursion`.
5. **Recent/daily price-memory anchor** — `_useDailyAnchor`, `_recentAnchorEnabled/Strength/Scale`,
   anchor-lag (`_anchorReactionLag`, `_anchorLagMin/MaxAlpha`, `_anchorDeadbandPrc`).
6. **Slippage caps** — `_marketSlippagePrc`, `_stopSlippagePct`, `_bracketSlippagePct`.
7. **Advanced-order generation** — `_advancedEnabled`, `_buyStopFraction`, stop/TP offset bands
   (`_stopOffsetMin/Max`, `_tpOffsetMin/Max`), `_advancedMaxQty`, `_bracketFlip`,
   inventory-bias (`_inventoryBias`, `_inventoryBiasThresholdPrc`, `_inventoryBiasShortMult`),
   `_bearShortStrength`, `_maxArmedStopsPerBot`, `_leanReload`.
8. **Inertia / reaction-persistence** — `_inertia`, `_inertiaMin/MaxSec`, `_inertiaLeak`,
   `_inertiaSentimentModulated`; `_reactionPersistence`, `_rpPersistMin/MaxSec`, `_rpWLocal/WShared`,
   `_rpLeak`, `_rpTakerCoupling/Threshold/Gain/GovScale`; `_reactionHold`, `_reactionHoldWindowTicks`.
9. **Herding / momentum / trend-follower** — `_herding`, `_followerFraction`, `_herdTilt`,
   `_momentumDominance`, `_momentumStrength`, `_roleSplit`, `_noiseDamp`; trend cohort
   (`_trendFollowerEnabled`, `_trendCohortFraction`, `_trendStrength`, `_trendContrarianFraction`,
   `_trendTakerCoupling/Threshold`, `_trendSharedChaseWeight`).
10. **Activity / composition seam** — `_activityEnabled`, `_activityGamma`, `_compTakerExp`,
    `_compDistExp[3]`, `_compSizeExp`, `_compSizeCap`, `_openRampMin`, `_openRampStaggerMin`.
11. **Reflexive mood coupling** — `_moodTakerCoupling`, `_moodGainGreed/Fear`, `_moodTakerCap`,
    `_moodPerStrategy`, `_moodPerStrategyGains` (dict), `_jointTakerCapMult`.
12. **Microstructure** — `_rangeActivityImpact`, `_rangeMaxSlippage`, `_fatImpactProb`, `_greedStyle`,
    `_greedSplit`, cash-homeostasis (`_cashHomeostasisContinuous`, `_cashMaxShift`, `_cashEdgeBuy/Sell`),
    `_roundSnapProb/Spread`, `_touchTightenPrc`, `_liquidityAwarePlacement`, `_liquidityAwareGain`.
13. **Sentiment-dynamics phase model** — `_sentimentDynamics`, `_slopeScaleFast/Slow`,
    conviction set (`_momentumConviction`, `_scalperConviction`, `_reversionConviction`,
    `_reversalConviction`), `_marketMakerLean`, `_aggressionBoost`, `_sentimentMaxBias`.
14. **Perceived-price desync** — `_perceivedDesync`, `_perceivedMin/MaxAlpha`,
    `_perceivedSlopeScaleFast/Slow`, `_directionalReactionLag`, `_dirLagMin/MaxAlpha`.
15. **Directional loop hybrid** — `_multiplicativeDirectional`, `_diversityGain`, `_memoizeTickValues`.
16. **Exogenous chaser + global co-fire (delegates)** — `_chaserFraction`, `_chaserNotionalFrac`,
    `_chaserMaxNotionalFrac`, `_chaserSellSymFrac`, `_chaserBuyRoomRelaxFrac`, `_chaserIntervalTicks`,
    `_exogCap`, `_shockOf?`, `_shockIdOf?`, `_anyShockActive?`; `_globalCoFire`, `_globalCoFireFraction`,
    `_globalCoFireNotionalFrac`, `_globalCoFireSignOf?`, `_globalPulseIdOf?`, `_globalCoFireSectorOf?`,
    `_sectorCount`.

**Where the values come from:** `AiTradeService`'s constructor reads `IConfiguration` (`Bots:*` /
`Bots:Advanced:*` keys) and forwards them positionally/by-name into this ctor. So the coupling is a
straight config-plumbing chain: `appsettings → AiTradeService → AiBotDecisionService ctor → ~155 fields`.
BotDecisionConfig collapses the middle two hops into one record hand-off.

---

## 3. BotDecisionConfig extraction plan

**Shape:** an immutable `record BotDecisionConfig` (init-only or positional) holding the ~155 scalar config
values in categories 1–16 above **plus the 6 optional delegates and the mood-gain dict**. It does **NOT**
hold the 11 injected services or `_logger` (those stay ctor params on the service — they are collaborators,
not config) and does **NOT** hold `LoopStartUtc` (runtime clock, see risk below).

**How the class reads them afterward:** two viable shapes —
- **(A) minimal-churn:** keep every `_field`, but assign it from `cfg.X` in the ctor
  (`_overheatCap = Math.Max(0m, cfg.OverheatCap)`). The ~155 field reads in method bodies are **untouched**
  → the entire tested surface is byte-identical; only the ctor's assignment RHS changes. **Recommended** —
  it makes the config-equivalence gate trivial and keeps the behaviour-bearing method bodies frozen.
- **(B) full:** delete the fields, read `_cfg.Overheat­Cap` at every call site. Larger diff, touches tested
  bodies, higher review cost. Not recommended for the first attended step.

**The normalization must move with the value.** The ctor doesn't just copy params — it *clamps/floors* almost
every one (`Math.Max(0m, …)`, `Clamp01`, `<= 0m ? default : …`, the min/max-alpha band clamps at L658–L675,
the derived `_reactionHoldWindowTicks` at L680, the `_compDistExp` array build at L606, the
`_inertiaMaxSec = Math.Max(_inertiaMinSec, …)` cross-field guard at L582). **These normalizations are
behaviour.** Under shape (A) they stay in the service ctor (config carries raw inputs, service normalizes) —
that is the safest split and keeps the record a dumb bag. If instead the record normalizes in its own ctor,
every clamp must be copied verbatim and the cross-field guards (inertiaMax≥inertiaMin, rpMax≥rpMin,
maxAlpha≥minAlpha) preserved.

### Config-equivalence gate (MUST pass before any behaviour soak)
Add a debug-only dump that, for a fixed `IConfiguration`, constructs the service **both ways** (legacy
positional ctor vs new `BotDecisionConfig` ctor) and asserts every one of the ~166 instance fields is equal
(reflection over private readonly fields; compare the `_compDistExp` array element-wise, the dict by
key/value, the delegates by reference-non-null parity). Green = the record + assignment produce a
bit-identical field set → the frozen method bodies cannot behave differently. Only after this passes does a
CK behaviour soak run. `BotPrecomputeEquivalenceTests` is the existing template for this equivalence style.

### RISK — post-construction mutation (the immutability blockers)
Verified by grep: **every `_field =` assignment is inside the ctor (L528–L699); no config field is reassigned
anywhere else.** So the config fields are safe to freeze into an immutable record. Specifically:
- **`LoopStartUtc { get; set; }` (L214)** is the *only* mutable member — set by `AiTradeService.Reset` and
  read in `ComputeOrderAsync` (L847–L849) for the open-taker ramp. It is **runtime state, not config** →
  **keep it on the service, do NOT put it in the record.** This is the one thing an over-eager "move all
  fields into the immutable record" pass would break.
- `_baseWeightByStockId` (L1840) is a `static readonly ConcurrentDictionary` memo — static, not per-instance
  config; leave it in place.
- `_compDistExp` is a `readonly double[]` — the reference is never reassigned and the elements are never
  written (reads only at L1894/L1896), so it is immutable in practice. In the record prefer `double[]`
  built once (or an `IReadOnlyList<double>`); either is safe.

**Verdict: BotDecisionConfig extraction is FEASIBLE with zero post-construction-mutation blockers among the
config fields.** The single caveat is `LoopStartUtc` (exclude it) — a classification call, not a blocker.

---

## 4. `AiBotDecisionMath` candidates (pure / stateless)

The math surface is **already largely `static` and already unit-tested** — extraction is a mechanical *move*
of existing static methods to a new `static class AiBotDecisionMath`, with `internal` visibility preserved so
the existing test files keep compiling unchanged.

**Purely functional (no `ctx`, no instance field, no clock, no RNG, no I/O) — ~43 methods:**

| Method (line) | Reads |
|---|---|
| `DirectionalBias` (2615) | strategy, s, dsFast, dsSlow, convictions, lean — args only |
| `BuyProbHybrid` (2671) | homeostatic, directional, noiseFactor, diversityGain — args |
| `PressureTilt` (2567) | buyProb, pressure, leak — args |
| `PersistHalfLife` (2559) | aiUserId, min/maxSec — hash, args |
| `ReactionTakerEffectiveGain` (2598) | takerGain, rawValueGap, govScale — args |
| `MoodTakerMult` (2577) / `JointTakerShare` (2585) | tilt/gains/cap / shares — args |
| `TrendFollowTilt` (2534) / `TrendTakerDecision` (2543) / `IsTrendFollower` (2523) | momentum/strength/hash — args |
| `IsChaser` (2504) / `ChaserResponse` (2516) / `ChaseCadenceDue` (2490) | aiUserId/shockId/hash — args |
| `ChaseNotionalCap` (2453) / `ChaseSymmetricSellQty` (2474) / `ChaseSelectCore` (2429) | shock/caps/delegates — args |
| `AnchorDeadband` (2689) / `ElasticAnchorTilt` (2701) | gap, deadband, scale, strength, power — args |
| `DipBuyTilt` (2986) / `BearShortBoost` (2995) / `CashHomeostasis` (3006) | value-gap/cash/strength — args |
| `SentimentModulatedMaxSec` (3000) | minSec, maxSec, sentimentMag — args |
| `ComposeTakerOverrideKind` (2920) / `CompositionSizeMult` (2935) / `OpenTakerRampMult` (2950) | act, k, cap/uptime — args |
| `TightenOffset` (3042) / `SnapToRoundNumber` (3048) | offset/price/spread/jitter — args |
| `BullDirection` (2854) / `BearDirection` (2863) / `DefaultExtremeStyle` (2875) | style enum — args |
| `IsBuyOrder`/`IsSellOrder`/`IsSlippageOrder`/`IsTrueMarketOrder`/`IsCompConvertible` (2898–2911) | OrderType — arg |
| `ToOrderTypeString` (2957) | OrderType — arg |
| `Tanh` (2647) / `Relu` (2648) / `Sign` (2649) / `Lerp` (2970) / `Clamp01` (2972) / `ClampSigned` (2974) | scalar — args |

**Static but read `AiBotContext` (pure over ctx — deterministic, read-only, but see caveat):**
- `ComputeCommitted` (2132) — walks `ctx.OpenOrders`, returns `CommittedTotals`. Read-only, no RNG. Safe to
  move; already tested by `CommittedTotalsTests`. (Its instance wrapper `GetCommitted` (2124) stays on the
  service — it touches `_memoizeTickValues` + `ctx.CommittedCache`.)
- `CountArmedStandalone` (728) — walks `ctx.OpenOrders`, read-only. Safe to move.
- `BaseWeight` (1841) — pure hash with a `static` memo dict; deterministic. Movable (carry the static dict).
- `ChooseMarketMakerQuote` (1737) — **static but consumes `ctx`'s seeded RNG** → **RNG-draw-order-sensitive**;
  moving it is fine *only if the call order is unchanged*. Treat as pure-over-ctx, not pure-functional.

**Count: ~43 purely-functional + ~4 pure-over-ctx = ~47 static candidates.** The purely-functional 43 carry
zero draw-order risk; the pure-over-ctx four must preserve call ordering. Because ~40 of these already have
dedicated test files, the Math split's oracle is pre-built.

---

## 5. CK / conservation touch-points (do-NOT-fragment seams)

**Critical framing:** this class **never mutates** Fund/Position/reservation state and **never submits
orders** — it *reads* live account state and *returns* an `Order?` / `BotAdvancedDecision?`. Submission +
settlement happen downstream (`AiTradeService → OrderExecutionService → MatchingEngine → SettlementEngine`).
So it is CK-critical by **sizing correctness**, not by mutation: if it sizes an order past real capacity,
the order is either doomed at Phase 1.6 or (worse) breaches conservation. The seams below must stay coherent.

**The load-bearing invariant — `min(ctx-snapshot, engine-authoritative)`:** every quantity gate clamps to the
*lower* of the per-tick `ctx` snapshot and the live `_accounts` engine view, so a bot can never generate an
order the settlement engine would reject. This pattern appears at:
1. **Buy sizing** — `ComputeOrderQuantityAsync` L2040–L2046: `freeBalance = min(ctxFreeBalance,
   engineFreeBalance)`, then `− BuySafetyBuffer`; cover-clamp against `enginePos.Quantity` (L2072–L2079,
   "never buy past flat on a short"); `ApplyDepthCap` anti-sweep (L2082).
2. **Sell sizing** — same method L2087–L2094: `availableQty = min(ctxAvailable, engineAvailable)`, depth cap
   (L2096), chase-symmetric cap (L2102–L2104).
3. **Sell-eligibility in `ChooseStockId`** — L1778–L1783: `ctxAvail = pos.Quantity − committedSell` vs
   `enginePos` (engine authoritative).
4. **Committed reservation totals** — `GetCommitted`/`ComputeCommitted` (2124/2132): the buy-fund /
   sell-share / cover-share buckets that feed every clamp above. `CommittedTotalsTests` guards this.
5. **Band veto** — `IsOverBand` (2244) + `ApplyDepthCap` (2326): structural anti-runaway / anti-sweep gates
   that both the plain and advanced paths route through.
6. **Advanced builders** — `BuildProtectiveStopAsync` (1086, incl. the `_maxArmedStopsPerBot` source-cap
   gate), `BuildCappedTriggerAsync` (1144), `BuildShortOpenAsync` (1164), `BuildBracketAsync` (1184): read
   `_accounts.GetFund(...).AvailableBalance` (L1126, L1225) and `_accounts.GetPosition(...).AvailableQuantity`
   (L1116) / `.Quantity` (L1259) for reservation-aware sizing and the flip/round-trip split.
7. **Inventory-bias reads** — `ComputeInventoryBias` (1022) / `WatchlistInventoryNotional` (1041): read
   `_accounts.GetPosition(...).Quantity` (L1051–L1055).

**Do-NOT-fragment rule:** the buy/sell sizing block in `ComputeOrderQuantityAsync` (L2020–L2107) and its
committed-totals input are a single conservation unit — keep them in the **same partial** (see §6, the
`PriceQty` partial). The `min(ctx, engine)` clamp, the cover-clamp, the depth cap, and the safety buffer must
not be separated from the qty they guard. Likewise the advanced builders' reservation reads stay together in
the `Advanced` partial. A partial-only split keeps all of this in one type, so the reads remain coherent by
construction — the risk is purely *mis-filing* a sizing helper away from its caller.

---

## 6. Partial-carve proposal (after BotDecisionConfig lands)

Byte-identical partial split of `internal sealed partial class AiBotDecisionService` (add `partial`;
globbed csproj needs no edit). Concern groups + rough line counts (source ≈ 3063 LOC, minus the
~120-line ctor region that shrinks once config is a record):

- **`AiBotDecisionService.cs`** (spine, ~200) — 11 service fields, the config field-set (or `_cfg` under
  shape B) + ctor, `LoopStartUtc`, `const`s + salts. Keeps `sealed`. Also keep the co-located
  `BotAdvancedKind` / `BotAdvancedDecision` here (or a tiny `.Types.cs`).
- **`AiBotDecisionService.Public.cs`** (~380) — `CanPlaceMoreOrder`, `ComputeOrderAsync`,
  `ComputeAdvancedDecisionAsync`, `CountArmedStandalone` (if kept instance-side).
- **`AiBotDecisionService.Advanced.cs`** (~340) — `ComputeInventoryBias`, `WatchlistInventoryNotional`,
  `BuildProtectiveStopAsync`, `BuildCappedTriggerAsync`, `BuildShortOpenAsync`, `BuildBracketAsync`,
  `FirstAnyStock`/`FirstFlatStock`/`FirstLongableStock`/`EligibleWatchlist`, `AdvancedExposureQty`.
  **(CK seam — advanced reservation reads; keep together.)**
- **`AiBotDecisionService.Decision.cs`** (~470) — `ChooseOrderType`, `ChooseMarketMakerQuote`,
  `ChooseStockId`, `PickStock`, `BaseWeight`.
- **`AiBotDecisionService.PriceQty.cs`** (~490) — `ComputeOrderPriceAsync`, `GetMidPriceAsync`,
  `ComputeOrderQuantityAsync`, `GetCommitted`/`ComputeCommitted`, `GetStockPriceAsync`, `PickLimitTier`,
  `StopOffset`, `PrecomputeSharedTickCaches`, `IsOverBand`, `SeedPrice`, `ApplyDepthCap`.
  **(CK seam — the sizing unit; keep the whole qty block together.)**
- **`AiBotDecisionService.Sentiment.cs`** (~250) — watchlist aggregators (`AverageWatchlist*`,
  `AveragePerceivedSlope`), `ChaseSelect`/`CoFireSelect`, `Fundamental`, `ApplyExtremeReaction`,
  `PickExtremeReactionStyle`.
- **`AiBotDecisionMath.cs`** (~330, **its own `static class`, not a partial**) — the ~43 purely-functional
  statics from §4 (+ the pure-over-ctx `ComputeCommitted` if not kept beside its wrapper). Extracted
  *after* BotDecisionConfig; carries the existing tests unchanged (all `internal`).

---

## 7. Recommended ATTENDED SEQUENCE (one extraction per soak)

Order is deliberate: the two hardest/riskiest judgment calls first while the owner is present, then the
mechanical low-risk moves.

1. **BotDecisionConfig (shape A: record carries raw inputs; ctor keeps all normalization + field reads).**
   *Gate:* server build green → **config-equivalence dump green** (both ctors produce a bit-identical
   ~166-field set, §3) → full test suite → CK behaviour soak (mid 45m) with `ConservationProbe`/`CK_`/auditor
   clean. This is the unlock for every later bot split; it touches only ctor assignment RHS, so the tested
   method bodies stay frozen. **Owner call:** confirm `LoopStartUtc` stays out of the record, and confirm
   normalization lives in the service ctor (not the record).
2. **`AiBotDecisionMath` extraction (move the ~43 purely-functional statics; keep `internal`).**
   *Gate:* build green → **moves-only sorted-line diff** (each moved method identical byte-for-byte; the
   `static class` gains exactly the removed lines) → full suite (the ~40 math tests are the oracle) → no soak
   required (pure functions, zero state/RNG/CK surface) unless a pure-over-ctx method (§4 caveat) is included,
   in which case add a short CK smoke to confirm draw order.
3. **Partial-carve (§6), one partial per soak, spine last.** Recommended order: `Public` → `Advanced`
   (CK) → `Decision` → `PriceQty` (CK) → `Sentiment`.
   *Gate per partial:* build green → moves-only sorted-line diff (spine loses exactly the moved members;
   partial gains exactly them; only added token is `partial`) → full suite → for the two CK partials
   (`Advanced`, `PriceQty`) a mid (45m) CK soak with conservation/auditor clean; non-CK partials need
   build+suite+moves-diff only.

**Standing gates for every step:** (a) diff is moves-only — no logic edits ride along; (b) `internal`
visibility preserved so `InternalsVisibleTo` tests compile untouched; (c) RNG draw order unchanged on any
method that consumes `ctx`'s seeded stream; (d) `LoopStartUtc` and the static memo dict never migrate into
an immutable/misfiled home.

---

### One-glance summary
- **3063 LOC**, `internal sealed` (not partial, no base/interface), namespace
  `…BackgroundServices.Helpers`; server csproj globbed (no csproj edits needed); 20 test files are the oracle.
- **BotDecisionConfig is feasible — no post-construction-mutation blockers** among the ~155 config fields
  (all assigned once in-ctor); the only mutable member is `LoopStartUtc` (runtime clock — exclude it).
- **~43 purely-functional static math candidates** (+~4 pure-over-ctx), mostly already `static` and already
  unit-tested → the Math split is a near-zero-risk mechanical move.
- **Class never mutates Fund/Position and never submits** — CK-critical by *sizing correctness*; the
  `min(ctx, engine)` clamp + committed-totals + band/depth caps + advanced reservation reads are the
  do-not-fragment seams (concentrated in the `PriceQty` and `Advanced` partials).
