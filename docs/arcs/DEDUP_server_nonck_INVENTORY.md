# DEDUP + Simplification Inventory — Server NON-CK Helper Layer

READ-ONLY discovery. Scope: `KieshStockExchange.Server/Services/BackgroundServices/Helpers`
(bot sentiment/conviction/regime/mood/context + decision MATH) and
`Services/MarketDataServices` (candle aggregation/read — derived, non-CK).

**Boundary rule applied:** anything that submits orders / mutates Fund·Position·reservations /
settlement, or lives in the Attended giants (`AiBotDecisionService`, `AiTradeService`,
`OrderExecutionService`) is marked **CK-TOUCHING = owner-gated, PROPOSE ONLY**. Everything else is
pure math / repeated boilerplate that does not move money.

Key context: the shared pure-math helper already exists (`BotMath.cs`) and several files already
delegate to it (`BotRegimeService.HashUnit → BotMath.HashUnit01`,
`BotSentimentService.RegimeStep → BotMath.SoftWallStep`). The remaining dedup work is (a) the last
few files that STILL reimplement primitives instead of delegating, and (b) centralizing the
tick-`dt` / EWMA-keep / arrival-prob one-liners and the cohort-select / RecordFills boilerplate.

---

## A. PROVABLY-SAFE candidates (exact dup / pure function / literal const)

### A1. Box–Muller Gaussian — exact duplicate  ★ top pick
- `FundamentalService.Gaussian()` — `FundamentalService.cs:193-198`
- `BotMath.NextGaussian(Random)` — `BotMath.cs:76-81`
Byte-identical Box–Muller (`√(-2 ln u1)·cos(2π u2)`, `u = 1 - NextDouble()`, exactly two draws in the
same order). `FundamentalService.Gaussian()` predates the shared helper.
**Change:** delete the private method, call `BotMath.NextGaussian(_rng)`. RNG draw order + count are
identical ⇒ byte-identical soaks.
**Safety:** PROVABLY-SAFE (exact dup, pure-given-rng). **Value:** medium (removes a duplicated
statistical primitive; matches the already-established BotMath delegation pattern).

### A2. `RecordFills(AIUser, OrderResult)` — three identical copies
- `RotatorDecisionService.cs:272-277`
- `ArbitrageDecisionService.cs:445-450`
- `ConvictionDecisionService.Run.cs:85`
All three are verbatim: `if (result.FillTransactions.Count == 0) return; for (…) user.RecordTrade(result.FillTransactions[i]);`.
**Change:** one shared static helper (e.g. `BotFills.Record(user, result)`), or an `AIUser`
extension. Pure bookkeeping on the in-memory user object — records fills, does NOT place/settle.
**Safety:** PROVABLY-SAFE (exact dup). **Value:** medium (3 sites).

### A3. `MinDtSec = 0.05` / `MaxDtSec = 60.0` — literal const duplicated in 8 files
`BotActivityService:30-31`, `BankEstimateService:34-35`, `BotPriceMemoryService:29-30`,
`BotRegimeService:25-26`, `BotSentimentService:53-54`, `ConvictionDecisionService.cs:61-62`,
`ExogenousShockService:33-34`, `JumpService:33-34`. (MarketMood uses a different 0.05/10.0 pair —
leave it.)
**Change:** hoist the shared pair to `BotMath` (e.g. `BotMath.TickMinDtSec` / `TickMaxDtSec`); files
reference the const. **Safety:** PROVABLY-SAFE (identical literals). **Value:** low-medium
(single source of truth; enables A4/A5 cleanly).

### A4. `Books = { CurrencyType.USD, CurrencyType.EUR }` — const dup
- `RotatorDecisionService.cs:50`, `ArbitrageDecisionService.cs:37`.
**Change:** one shared `static readonly CurrencyType[]` (a currency-set constant already conceptually
belongs with the two-book model). **Safety:** PROVABLY-SAFE. **Value:** low.

---

## B. NEEDS-CARE candidates (subtle diffs — behaviour-preserving but not byte-trivial)

### B1. Tick `dt`-clamp pattern — ~9 sites
`Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec)` in
`BankEstimateService:145`, `BotActivityService:110`, `BotPriceMemoryService:143`,
`BotRegimeService:60`, `BotSentimentService:284`, `ConvictionDecisionService.Run.cs:20`,
`ExogenousShockService:104`, `JumpService:102`, `MarketMoodService:143` (0.05/10.0 bounds).
**Change:** `BotMath.ClampDtSec(now, last, min, max)` returning the clamped seconds.
**Care:** the surrounding `_lastTickUtc == DateTime.MaxValue` "not reset yet" guard and the assignment
`_lastTickUtc = now` differ per file and must stay at the call site; only the arithmetic is shared.
MarketMood's bounds differ (pass them as args). **Safety:** NEEDS-CARE. **Value:** medium.

### B2. Poisson/flip arrival probability — `1 - exp(-dt/mean)`
- `BankEstimateService:170` (pArrival), `BotRegimeService:63` (flipProb),
  `IJumpSource.cs:65`, `IShockSource.cs:176`.
**Change:** `BotMath.ArrivalProb(dt, meanSec)`. **Care:** trivial one-liner; consolidation is for
naming/intent clarity, watch that callers guard `mean > 0`. **Safety:** NEEDS-CARE. **Value:** low-med.

### B3. EWMA "keep" — `exp(-dt/tau)`
- `MarketMoodService.Keep` (private static, `:148`), `BotSentimentService.EwmaSlope:401`,
  `BotActivityService:114,122`, and the leaky-integrator decays `BotSentimentService:351,358`.
**Change:** `BotMath.TauKeep(dt, tau)` (with the `Math.Max(MinDtSec, tau)` floor as an option).
**Care:** several sites inline `keep` inside a larger expression; MarketMood already has its own private
`Keep`. Consolidate opportunistically, not forcibly. **Safety:** NEEDS-CARE. **Value:** medium.

### B4. Half-life keep via `Math.Pow(0.5, dt/hl)` vs existing `BotMath.HalfLifeKeep`
- `BankEstimateService:169`, `ExogenousShockService:114,119` compute `Math.Pow(0.5, dt/hl)`.
- `BotMath.HalfLifeKeep` (`:49-50`) already exists but uses the `exp(-ln2·dt/hl)` form.
**Change:** route the `Pow(0.5,…)` sites through `BotMath.HalfLifeKeep`.
**Care:** `Pow(0.5,x)` and `Exp(ln0.5·x)` can differ in the last ULP ⇒ NOT guaranteed byte-identical
across a long soak. Flag before baking; validate with a CK soak. **Safety:** NEEDS-CARE. **Value:** low-med.

### B5. Cohort filter-then-sort boilerplate — 4 sites
Each dedicated decision service rebuilds "iterate `ctx.AiUsersByAiUserId.Values`, filter
`IsEnabled && Strategy == X` (+ optional per-bot `DecisionInterval` elapsed), collect, sort ascending
by `AiUserId`":
- `RotatorDecisionService.cs:123-131` (with interval check)
- `ArbitrageDecisionService.cs:99-106` (with interval check)
- `ConvictionDecisionService.Run.cs:27` (filter head)
- `MarketMakerDecisionService.cs:73` (filter head)
**Change:** an `AiBotContext.CohortByStrategy(AiStrategy, DateTime now, bool requireInterval)` returning
the sorted `List<AIUser>` (`AiBotContext` already owns `AiUsersByAiUserId` and `GetRandom`, `:33/:197`).
**Care:** the interval-elapsed predicate is present in Rotator/Arb but the MM/Conviction heads differ
slightly; keep the flag. Pure read over the in-memory user index — no CK. **Safety:** NEEDS-CARE.
**Value:** medium-high (4 sites, ~8 lines each, and it centralizes the seed-determinism contract).

### B6. Inline magnitude draw vs existing `BotMath.DrawMagnitude`
- `BotSentimentService.StepShocks:481-482` and `GlobalShockDelta:520` recompute
  `min + span·Pow(u, exp)` — the exact shape of `BotMath.DrawMagnitude` (`:69-73`).
**Change:** delegate the magnitude portion to `BotMath.DrawMagnitude`.
**Care:** `StepShocks` draws the SIGN first, then the magnitude — the single `NextDouble()` inside
`DrawMagnitude` must land on the same draw in the same order to stay byte-identical; `GlobalShockDelta`
is already a pure `(signUniform, magUniform)` function so it composes cleanly. **Safety:** NEEDS-CARE.
**Value:** low-medium.

---

## C. CK-TOUCHING — owner-gated, PROPOSE ONLY (do not touch autonomously)

### C1. `AiBotDecisionService` pure-static math block (`:2429-3048`)
`ChaseSelectCore`, `ChaseNotionalCap`, `ChaserResponse`, `TrendFollowTilt`, `DirectionalBias`,
`BuyProbHybrid`, `ElasticAnchorTilt`, `DipBuyTilt`, `CashHomeostasis`, `SnapToRoundNumber`, the private
decimal primitives `Tanh/Relu/Sign/Lerp/Clamp01/ClampSigned`, etc. These ARE pure and unit-testable,
but they live in the Attended giant that drives order submission (`AiBotDecisionService`), so per the
boundary they are owner-gated. Note: the decimal `Tanh/Relu/Sign/Lerp/Clamp01` helpers also appear
conceptually in `MarketMoodService` (`Math.Tanh`) and `AiBotContext` — a future *proposal* could lift a
`DecimalMathHelpers` set, but only with owner sign-off since the source file is CK-critical.

### C2. Is pure decision-math duplicated ACROSS the decision services? — mostly NO
The per-cohort scoring functions are DISTINCT weighted formulas, not duplicates, and should NOT be
force-merged:
- `ConvictionDecisionService.Hot / HotSigned` (`ConvictionDecisionService.Math.cs:26,103`)
- `RotatorDecisionService.Score` (`:95`, `0.6·gap + 0.25·dir + 0.10·idio + 0.05·global`)
- `AiBotDecisionService.DirectionalBias` (`:2615`)
- `MarketMakerMath.Quote` inventory-skew (`MarketMakerMath.cs:96`)
Each encodes a different strategy's economics; consolidating them would couple unrelated cohorts. The
genuinely shared PRIMITIVES underneath (avalanche hash, cubic soft-wall, gaussian, magnitude draw,
half-life keep) already live in `BotMath` and are already delegated to by `BotRegimeService` /
`BotSentimentService` — the correct pattern. The dedup opportunity is finishing that delegation
(A1, B4, B6), not merging the scorers.

---

## D. MarketDataServices (candle) — clean, low yield
- `CandleAggregationMath` (`AlignRange`, `WeightedClose`, `CheckOrdered`, `CheckContinuous`) is already
  the extracted pure-math helper; `CandleService.Aggregation.cs` delegates to it (`:56,58,89,100`).
  `WeightedClose` is exposed twice (`CandleAggregationMath.WeightedClose` + a thin forwarder
  `CandleService.Aggregation.cs:89`) — a deliberate re-export, leave it.
- `CandleRingBuffer` and `CandleService.Read` are straightforward, no material dup found.
No action recommended here beyond noting it is already in good shape.

---

## Summary counts by safety-class
- **PROVABLY-SAFE:** 4 (A1 Gaussian dup, A2 RecordFills×3, A3 MinDt/MaxDt const×8, A4 Books const×2)
- **NEEDS-CARE:** 6 (B1 dt-clamp, B2 arrival-prob, B3 EWMA keep, B4 half-life Pow→BotMath,
  B5 cohort select×4, B6 magnitude draw)
- **CK-TOUCHING (propose-only):** 2 areas (C1 AiBotDecisionService static math block; C2 note — the
  cross-service scorers are intentionally distinct, not dedup targets)

## Recommended order (cheapest, highest-confidence first)
1. **A1** FundamentalService.Gaussian → BotMath.NextGaussian (exact dup, one file).
2. **A2** RecordFills → one shared helper (3 files, exact dup).
3. **A3 + A4** hoist shared consts (mechanical).
4. **B5** AiBotContext.CohortByStrategy (best structural win; 4 call sites).
5. **B1/B2/B3** dt / arrival / keep one-liners into BotMath (validate a short CK soak).
6. **B4/B6** Pow→HalfLifeKeep and inline→DrawMagnitude — flag the ULP/RNG-order risk, soak-validate.
