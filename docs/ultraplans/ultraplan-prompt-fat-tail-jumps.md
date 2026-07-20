# ULTRAPLAN HANDOFF — fat-tail "news jump" lever (return kurtosis)

**Paste the block below to the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE (default-off,
byte-identical off) + a ready-to-paste bake prompt for local Claude (apply → build → test → soak → bake).**
Branch `feature/bot-market-realism-v2`. Council-ranked runner-up after the cross-stock co-movement axis closed
(structural). [[project_market_realism_v2]]

---

## PROMPT (paste this)

Design a **fat-tail / jump** lever for the AI-bot stock-exchange sim so 1-minute return **kurtosis** rises from
its current thin level toward the realistic range, WITHOUT regressing the other (now-good) realism metrics.

### The goal + the measurement
- Metric: excess kurtosis of 1-min candle returns (16/50-stock scorer). **Current ≈ +0.2..+2.0 (median ~+0.3);
  real equity ≈ +3..+8.** Measured by `scripts/cross_stock_diag.py` (reports excess kurtosis) and
  `scripts/r4_realism_score.py` / `scripts/bounce_diag.py` (clustering + ret_acf).
- Bake gate (2h soak, both instruments agree): **kurtosis ↑ toward +3..+5**, AND volatility clustering
  (abs-return ACF lag1) **not regressed** (stay ≥ ~0.12), AND ret_acf stays in its band (≈ −0.17 CLOSE), AND
  drift ≤ 5%/4h, AND **conservation CK=0 / CONS=0 / shortfall=0**.

### THE KEY INSIGHT (do not repeat the prior failure)
A previous attempt cranked the existing news-shock **frequency/amplitude** and it *LOWERED* kurtosis — because
more-frequent moderate moves add VARIANCE, making the return distribution MORE Gaussian. **Kurtosis comes from
RARE, LARGE moves against a CALM background.** So the lever must be **infrequent + big**, not frequent + medium.

### The structural limiter to work around
Per-move size is clipped by a hard **±20% cap (`Bots:ValueAnchor:AbsoluteCapMax`)** plus an order **depth cap**.
These bound every tick, so no single bucket can show a big move today → thin tails. The jump must be allowed to
**exceed the per-TICK cap for one event**, while the price LEVEL stays bounded over time (the value-anchor must
still pull it back afterward — a jump then mean-reverts; it does not permanently escape).

### Mechanism to design (a rare Poisson price JUMP, conservation-clean)
- **Trigger:** a low-intensity Poisson arrival **per stock (~1 event per 1–2 sim-hours)** — RARE. RNG-disciplined
  (dedicated seeded Random, drawn only when enabled ⇒ off path byte-identical), no wall-clock in the pure path.
- **Effect:** on fire, realize a **single large price move of ~3–8% within one ~1-min bucket** — bigger than the
  per-tick cap, but **hard-bounded at the event level** and **sign-randomized** (or sign-paired across events) so
  it adds **zero net drift**.
- **CRITICAL — CK-safe:** the move MUST be realized via **REAL matched orders** (a short burst of marketable
  orders that walk the book), NOT an injected/forced price (which would break conservation). Conservation
  (ConservationProbe) must stay CK=0. Think "a whale dumps/buys a block in one minute," settled normally through
  `MatchingEngine`/`SettlementEngine`.
- **Bounded LEVEL:** after the jump, the existing value-anchor + `AbsoluteCapMax` pull the price back over minutes
  (so the jump is a tail event, not a runaway) — verify the jump can momentarily exceed the per-tick cap but the
  price can't permanently leave the ±20% band.

### Existing infra to reuse / not duplicate (read these)
- `ExogenousShockService` (`Services/BackgroundServices/Helpers/`) — already does Poisson news shocks to
  sentiment + the fundamental anchor (`AnchorTracksShock`). Those move the *target* → price drifts gradually
  (damped) → they do NOT produce a realized one-bucket tail. The jump lever needs realized **price** flow.
- The retired **chaser** (`Bots:ExogShock:Chaser*`) placed CONTINUOUS marketable orders into shocks and carried a
  down-drift — a jump is a ONE-SHOT, sign-randomized burst, not continuous, so it should avoid that drift.
- `AbsoluteCapMax` / `ValueAnchor` — the per-tick cap + runaway guard the jump must interoperate with (exceed per
  tick within the event bound; remain LEVEL-bounded after).
- Soak harness `scripts/kse-balance-soak-p.ps1 -Minutes N -Db X -Port P`; flags via `Bots__*` env; candles
  auto-export to `data/soaks/candles-<Db>-*.csv`.

### Config + conventions (match the repo)
- New section `Bots:Jumps:{ Enabled=false, MeanIntervalHours, MinPct, MaxPct, MagnitudeExponent, SignMode, ... }`
  in `KieshStockExchange.Server/appsettings.json` with a `_comment` (mirror the `RegimeDrift` / `CoMovement`
  blocks). Wire in `AiTradeService` ctor via `_configuration.GetValue(...)`. **Default-off ⇒ byte-identical.**
- Add determinism tests (mirror `CoMovementDeterminismTests` / `ImpactDecoupleDeterminismTests`): the trigger +
  sizing are pure/seeded; off ⇒ no RNG drawn.
- A liveliness probe/log (how many jumps fired + realized %) so a soak can confirm the mechanism actually fires
  (the inert-flag trap sank an earlier lever — log it, e.g. `JUMP fired=… meanPct=…`).

### Open questions for your judgment
1. Burst delivery: one big marketable order vs a short sequence over a few ticks (book depth may not absorb 8% in
   one order — sizing vs the depth cap). 2. Sign policy: per-event random vs paired (guaranteed drift-neutral).
3. Should jumps be correlated with the existing news-shock arrivals (a shock that's "big" becomes a jump) or a
   wholly separate process? 4. Magnitude distribution (power-law for a few giant + many ~3% events).
