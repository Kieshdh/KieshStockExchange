# RESEED_CHECKLIST.md — the final, one-way reseed: prune + fold execution manifest

Execution detail for **ROADMAP.md §3** (the final reseed). This is the mechanical checklist; the roadmap has the *why*. **Attended + Kiesh-gated** (prune COMMIT held for OK; fresh RESEED presented at return). `/Tools` change authorized for this pass only. Line numbers are **approximate — re-locate by symbol name** (the buy-stop batch `e705153` shifted some `AiTradeService.cs` lines).

## Order of operations
1. **Prune** the 5 dead-end levers (Part A — surgical; mind the entanglements).
2. **Fold** the runtime multipliers into the seed, dials → 1.0 (Part B).
3. **EUR bot-rebalance** for P2 (Part C — a separate design decision).
4. **Bake** the locked, converged tuning config (ROADMAP §1 ship bundle).
5. **Regenerate** `AIUserData.xlsx` (both copies), rebuild, **parity A/B vs the current build**, then reseed.

---

## Part A — PRUNE (#171): 5 confirmed dead-ends
All 5 verified default-off + no baked path depends on them. **⚠ Two share code/test files with KEPT levers — surgical edits, do not delete the file.**

### A1. CoMovement (`Bots:Sentiment:CoMovement`)
- **Config reads** — `AiTradeService.cs`: `:405` (`ShiftCap`), `:460-465` (`Enabled/StepSigma/Cap/SoftWallK/Strength/BetaSpread`).
- **Code** — `BotSentimentService.cs`: fields `:147-155`; ctor params `:195-197`; assigns `:249-254`; Tick walk `:313-320`; `BetaOf` `:427-433`; `CoMoveBeta` `:435-439`; `CoMoveShift` `:441-446`; Reset `:626-628`; logging `:574` (the `Mkt=` append). `AiTradeService.cs:404-405` (the `coMoveShift:`/`coMoveShiftCap:` args).
- **⚠ ENTANGLEMENT — `FundamentalService.cs`:** delete field `_coMoveShift` `:48`, `_coMoveShiftCap` `:49`, ctor param `:62`, assigns `:75-76`, and the compose branch `:153-157`. **KEEP** the shared read-time clamp scaffold `:147-152` + `:158-163` (used by the KEPT ExogShock lever); when removing `_coMoveShiftCap`, drop only the `+ _coMoveShiftCap` term from the `span` at `:160` — keep `_band + _shockCap`.
- **appsettings.json** — `_comovement_comment` `:326` + `CoMovement` object `:327-335`.
- **Tests** — `CoMovementDeterminismTests.cs` (**entire file, 5 tests**). (Comment-only name-drops in GlobalShockDelta/Jumps/RefillThrottle/AdaptiveAnchor determinism tests — no code dep.)

### A2. SlowRingDamp (`Bots:Sentiment:SlowRingDamp`)
- **Config read** — `AiTradeService.cs:451`.
- **⚠ ENTANGLEMENT (the big one) — `BotSentimentService.cs`:** SlowRingDamp is folded into the SAME machinery as the KEPT `PerStockSigmaMult`/`GlobalSigmaMult` correlation levers. Delete ctor param `slowRingDamp` `:192`, const `SlowRingTauThresholdSec` `:45`, `SlowRingSigma` helper `:407-408`, and `double slowDamp = …` `:233`. **Simplify** `EffectivePerStockSigma(ring, slowDamp, mult)` `:412-413` → `PerStockSigma[ring] * mult`. **KEEP** `_perStockSigmaEff`/`_globalSigmaEff` `:126-128`, the fold block `:232-238` (minus the slowDamp term), `perStkMult`/`glbMult`, `EffectiveGlobalSigma` — deleting these breaks the shipped correlation lever.
- **appsettings.json** — `_slowringdamp_comment` `:316` + `SlowRingDamp` `:317`. (Trim the "Compose with SlowRingDamp" phrase from the KEPT `_sigmamult_comment` `:304`; keep keys `:305-306`.)
- **Tests** — `SentimentPriceReactionTests.cs` (**SHARED file — delete only 2 named tests**): `SlowRingDamp_off_is_identity` `:78-85`, `SlowRingDamp_scales_only_slow_rings` `:87-95`. Keep the Deadband/#2 tests.

### A3. sentiment-mod-inertia (`Bots:Imbalance:Inertia:SentimentModulated`)
- **Config read** — `AiBotDecisionService.cs:617`.
- **⚠ ENTANGLEMENT — `AiBotDecisionService.cs`:** delete field `_inertiaSentimentModulated` `:140`, ctor param `:382`, assign `:529`, the ternary at `:1457-1459` (replace with `var effMaxSec = _inertiaMaxSec;`), helper `SentimentModulatedMaxSec` `:2710-2716`. **KEEP** the surrounding `if (_inertia && !_reactionPersistence && notMM)` block `:1452-1462` (the baked A1 Inertia lever) — `RollOrHoldStance`, `_inertiaMinSec/MaxSec/Leak` all stay.
- **appsettings.json** — `_Inertia_SentimentModulated_comment` `:233` + key `:234`. (Keep siblings `:229-232`.)
- **Tests** — `InertiaStanceTests.cs` (**SHARED — delete only 5 named tests**): `SentimentMaxSec_*` `:70-88`. Keep `Stance_holds_…` + `Roll_consumes_…` (KEPT A1).

### A4. TouchTighten (`Bots:TouchTightenPrc`)
- **Config reads** — `AiTradeService.cs:684` (live) + `:766` (CONFIGCHECK log — delete the log line too).
- **Code** — `AiBotDecisionService.cs`: `_touchTightenPrc` field+ctor+assign; the two `TightenOffset(...)` calls `:1707-1708`; helper `TightenOffset` `:2754+`. After removal `tierMin/tierMax` flow straight into the KEPT `_limitOffsetMult * _distanceMult` math `:1709-1710`.
- **appsettings.json** — **none** (code-default `0m`, no shipped key).
- **Tests** — `TouchTightenTests.cs` (**entire file, 6 tests**).

### A5. RefillThrottle (`Bots:RefillThrottle`)
- **Config reads** — `AiTradeService.cs:286-308` (the whole gate-build block).
- **Code** — DELETE ENTIRE FILES `Helpers/RefillThrottleGate.cs` + `Helpers/RefillThrottleProbe.cs`. `AiTradeService.cs`: gate-build `:286-308`, drain block `:1876-1881`. `AiBotContext.cs`: `RefillGate`/`RefillGateCache` fields `:148-149` (+comment `:144-147`), the `RefillGateCache.Clear()` `:160`, the whole `#region Refill throttle` `:207-253`. `AiBotDecisionService.cs`: skip-repost `:731-742`, widen `:1748-1753` (restore `offset` passthrough).
- **appsettings.json** — `_refillthrottle_comment` `:389` + `RefillThrottle` object `:390-397`.
- **Tests** — `RefillThrottleDeterminismTests.cs` (**entire file, 10 tests**).

---

## Part B — MULTIPLIER → EXCEL FOLD
Goal: bake the two runtime dials into the seed so the Excel is the single source of truth; set the dials → 1.0.

### Runtime keys + apply sites (verified)
| Key | Value | Apply sites |
|---|---|---|
| `Bots:DecisionDistanceMult` | 0.2 | **4 sites** (plan said 3): `AiBotDecisionService.cs` TP offsets `:1071-1074`, limit-tier offsets `:1709-1710`, protective-stop distance `:2011`; **+ `AiBotStateService.cs:273-274`** (Far-band prune thresholds — reads the already-scaled `FarLimit*Prc`, so no Config change needed beyond neutralizing the dial). |
| `Bots:MarketProbMult` | 1.5 | 1 site: `AiBotDecisionService.cs:1411` `Math.Min(1m, UseMarketProb * mult)` (the clamp). |
| cash-reserve-band mult | — | **does not exist** — drop from the plan (no runtime key; cash band is seeded directly). |

### Scale by 0.2 (on top of the existing ×0.32) — the absolute-distance constants in `Tools/Config.py`
`TP_OFFSET_MIN_RANGE` `:327`, `TP_OFFSET_MAX_RANGE` `:328`, `MID_LIMIT_MIN_RANGE` `:318`, `MID_LIMIT_MAX_RANGE` `:319`, `FAR_LIMIT_MIN_RANGE` `:320`, `FAR_LIMIT_MAX_RANGE` `:321`, `STOP_DISTANCE_MAX_RANGE` `:323`, and the Close tier `MAX_LIMIT_BASE` `:307` / `MAX_LIMIT_SLOPE` `:308` / `MIN_LIMIT_FLOOR` `:312`.

### Scale by 1.5 then `clamp01` — the market-prob constant
The generated `use_market` (`Person.py:200`, from `USE_MARKET_BASE` 0.20 `:267` + `USE_MARKET_RANGE` 0.30 `:268`). Faithful fold = `self.use_market = clamp01(1.5 * (USE_MARKET_BASE + USE_MARKET_RANGE * skewed01(...)))` (matches the runtime `Math.Min` on the product).

### ⚠ DO NOT scale (ratios / shape / different lever)
`MIN_LIMIT_FRACTION_LO/HI` `:310-311`, `STOP_DISTANCE_MIN_FRACTION` `:324`, `MAX_LIMIT_JITTER` `:309`, the `×0.9` stop-cap (`Person.py:280`), `USE_MARKET_SKEW` `:269` (a shape exponent, coincidentally 1.5), `USE_SLIP_*` `:270-272` (a different lever). The `_validate()` ordering invariants (`Config.py:408-424`) still hold when all endpoints scale by the same factor.

### Neutralize the dials — `KieshStockExchange.Server\appsettings.json`
`DecisionDistanceMult` 0.2 → 1.0 (`:81`); `MarketProbMult` 1.5 → 1.0 (`:83`). Keep both keys + comments as retained knobs.

---

## Part C — EUR bot-rebalance (P2 empty-candles) — design pending (Kiesh)
The freeze-safe P2 cure: a *seed allocation* change (more bots watchlist/trade the thin EUR books), NOT a new mechanism. Both this and the "enable MM cohort" option need the reseed anyway (the MM cohort is unseeded). Design (how many bots / which watchlists) is Kiesh's call; `stock_liveness.py` measures the before/after. Bundle into this reseed.

---

## Verification (before committing the reseed)
- `python Tools/GenerateAIUsers.py` → rewrites `AIUserData.xlsx`; **update BOTH copies** (client `KieshStockExchange/Resources/Raw` + server `KieshStockExchange.Server/Resources/Raw`, embedded resources). xlsx is binary + git-excluded — commit deliberately with the reseed.
- Determinism preserved (Person.py is per-bot `random`-seeded; the fold is a deterministic post-multiply). `Config._validate()` still passes.
- **Parity A/B**: seed a scratch template with the folded seed (dials 1.0) vs the current build (dials 0.2/1.5) → drift / ret_acf / candle metrics within the established noise → commit only if parity holds; else `git checkout` Config/Person/appsettings/xlsx.
- Prune: full test suite green after the surgical deletions (the shared-file levers must keep their KEPT tests passing).
