# REALISM COUNCIL BRIEF — consolidated history of the "market realism" arc

**Purpose:** one dense reference so a council can reason about the current pinning problem WITHOUT re-deriving
years of dead ends. Compiled 2026-07-21 from every realism doc under `docs/`, the condensed `memory/project_*.md`
findings, and a fresh read of the live services + config. Cites doc paths and `file:line`. Point-in-time constants;
where prod differs from `appsettings.json`, prod is set by `docker-compose.prod.yml` env overrides.

> **THE QUESTION:** every stock is pinned dead-flat ~20% BELOW seed (MSFT seed 639 → stuck ~510, ±0.3% band, 30+h);
> news pops snap back. A knob deploy (RegimeDrift 0.5→0.8, MarketProbMult 1.35→1.4, news Tau 1500→2000, AlphaMin
> 0.30→0.40) briefly unstuck MSFT then it regressed. Owner wants the RANDOM WALK to make big PERSISTENT moves like
> news does.

---

## ★★ TOP CORRECTION TO THE FRAMING (read first — changes the diagnosis)

The premise "a value-anchor spring reverts to the FIXED seed, with a 20% ElasticDeadband (one-sided floor)" is **NOT
what the live prod code does.** Verified in code + `appsettings.json`:

1. **The anchor is NOT a fixed-seed spring — it tracks a 7-day WEIGHTED TWAP OF PRICE ITSELF.**
   `ValueAnchor:UsePreviousDayAverage = true` (`appsettings.json:153`). This routes the anchor target through
   `BotPriceMemoryService.GetPreviousDayAverage()` instead of the seed/OU (`AiBotDecisionService.cs:2788,2793`).
   That target = a **linearly-tapered weighted average of the last `WindowDays=7` daily TWAPs**, hard-clamped to
   `seed × [1 ± MaxDailyDrift=0.5]` (`BotPriceMemoryService.cs:223-331`). Missing early slots route their weight to
   seed, so day-0 ≈ seed and it **re-rates toward wherever price has been sitting** as history fills.
   ⇒ **This is a self-fulfilling pin:** after price drifted −20%, the weighted TWAP followed it down, so the spring
   now pulls toward the pinned level, not toward 639. The `seed×[1±0.5]` clamp is the only thing bounding it (would
   permit down to ~320 for MSFT). The FundamentalService OU-to-seed exists but is **bypassed** for the anchor while
   `UsePreviousDayAverage=true` (it keeps Ticking only so rollback is a flag flip — `AiBotDecisionService.cs:2778`).

2. **The 20% ElasticDeadband is OFF.** `ValueAnchor:Elastic = false` (`appsettings.json:147`). The elastic soft-wall
   path (`ElasticAnchorTilt`, zero pull within ±`ElasticDeadbandPrc=0.20`, cubic beyond — `AiBotDecisionService.cs:2701`)
   is **not taken**. The live path is the LINEAR tilt `gap/Scale × Strength` (`:1519-1523`), and the general
   `AnchorDeadbandPrc = 0` (`appsettings.json:528`) ⇒ **no deadband at all** — the linear anchor pulls proportional to
   the price-vs-TWAP gap everywhere. (The 0.20 value the earlier finding saw is a dormant default for a disabled path.)

3. **The genuinely one-sided force is DipBuy, not the anchor.** `DipBuyStrength = 3.0` on prod
   (`appsettings.Production.json:38`; base 2.0) boosts buy-prob for below-anchor bots holding idle cash — an
   **upward-only** support on dips. Its symmetric counterpart `BearShortStrength = 0.0` (OFF, `appsettings.json:129`).
   So the asymmetry (a floor, no ceiling) is real but lives in DipBuy.

**Why it still pins ~20% below seed even though the TWAP is still mostly seed-weighted at 30h:** the anchor tilt is a
**buyProb tilt → rests as LIMIT orders → absorbed by the book** (the project's central axiom, below). The anchor
"wants" ~600 but can only *lean* the book, not lift the mark; price settles where absorbed buy-leaning limits meet
sell flow. **RegimeDrift, the intended "energy," is ALSO a sentiment/buyProb tilt** (`BotSentimentService.cs:372`) —
so it too is largely absorbed unless it saturates. Only genuine TAKER flow (news chaser, co-fire, or a taker cohort)
moves the level.

---

## 1. THE CORE AXIOMS THIS PROJECT HAS ESTABLISHED

- **A1. Only TAKER FLOW moves price. Limit/buyProb/sentiment tilts are ABSORBED by the book.**
  A shared *sentiment* tilt rests as limit orders that ~20k bots refill every ~1s tick, so the mid doesn't move; a
  shared *flow* (marketable orders) does. This is the single most-repeated finding.
  Sources: `docs/plans/REALISM_OVERHAUL_PLAN.md:38-44`; `memory/project_market_realism_v2.md` (sentiment
  anchor-dominated ~20:1; ±0.6 lean shifts buy% only 0.2-1.2pp); the whole ExogShock **chaser** rewrite exists
  precisely because "the retired ChaserStrength·tanh buyProb tilt only shifted the buy/sell ratio and could not move
  [price]" (`appsettings.json:567`).
- **A2. ~20k INDEPENDENT bots average out by the LLN.** Net imbalance ≈ 1/√N ≈ 0.35% → huge volume, no direction.
  Realistic markets need agent COUPLING (correlated behaviour). Only **common-mode** (shared) drivers survive; every
  mean-reverting per-stock ring cancels. `docs/plans/REALISM_OVERHAUL_PLAN.md:9-18`; `docs/plans/random-walk-sentiment-plan.md:9-16`.
- **A3. The −0.43 1-min return-autocorrelation ceiling is STRUCTURAL / microstructural, not a tunable.**
  `ret_acf_lag1 ≈ −0.43` decomposes into **~28% bid-ask bounce** (Roll, mechanical, a real stylized fact) + **~72%
  fleet reaction-loop mean-reversion** (the deep fast-refilling book + ~20k agents = textbook efficient price
  discovery). **No config knob moved it across 25+ experiments** (R4's 19 + R5 B/C + #1 + SmoothedPrices engine lever).
  It was "resolved" cosmetically by sampling candle CLOSE off the matcher **mid** (`Bots:BounceReference=mid`), which
  took CLOSE ret_acf −0.43→−0.17 (real range). The chart never renders ret_acf; the council said stop chasing it.
  Sources: `docs/research/R5_REALISM_ROUND_REPORT.md:28-34`; `docs/research/REALISM_CEILING_INVESTIGATION.md`;
  `docs/research/REALISM_RETACF_CLOSEOUT.md`; `docs/research/REFILL_THROTTLE_BAKE_RESULTS.md` (council 5/5).
- **A4. Symmetric HARD caps pin the tail and halve throughput.** `AbsoluteCapMax` as a symmetric veto was shipped and
  **reverted twice** (R3 + sensitivity): price parks on the clamp edge (fake "flat-on-ceiling" look), throughput ~halved.
  It survives only as an opt-in ×3/÷3 **geometric** runaway backstop (`AbsoluteCapMax=2.0`, `GeometricBand=true`),
  not a body-shaping force. `memory/project_bracket_flip_r3_complete.md`; `memory/project_sensitivity_tuning_overnight.md`.
- **A5. The book/anchor/±cap = "THE WALL."** value-anchor + fast-refilling fleet book means flow gets absorbed; every
  flow lever hit this ceiling. Correlation was only ever moved by killing the idiosyncratic drown-out (RegimeDrift-off
  + GlobalSigmaMult) or by **deterministic co-firing** (whole cohort, same-tick, same-sign, sized to clear depth) —
  probabilistic per-bot firing DILUTES the shared sign and averages away. `docs/plans/GLOBAL_SHOCK_PLAN.md`.
- **A6. Soaks are NON-deterministic and NOISY.** Wall-clock dt jitter advances the OU/regime/Hawkes RNG differently
  run-to-run: composite ±15-30, ret_acf ±0.03-0.1, R² ±0.05 at 45-90m. **Only effects reproducible across parallel
  arms (max 2 servers, Postgres cap) are trustworthy.** A single 45-90m A/B cannot measure a small lever. The long-
  horizon PIN itself does NOT reproduce in a fresh 45-min soak (fresh MSFT falls from seed; prod MSFT is pinned) —
  see the inconclusive A/B in `docs/arcs/MARKET_PINNING_ARC.md:156-160`.
- **A7. Persistent DOWN-DRIFT is a structural taker-flow asymmetry, repeatedly re-found.** Plain market orders are
  ~50/50 balanced; the entire taker sell-skew (~40% vs ~29%) comes from **protective STOPS being ~100% SELL-side**
  (no buy-stop kind exists) + net-long fleet + Itô/Jensen −σ²/2 floor + long>short bracket population. Config tuning
  bounds it (~−1 to −2.3%/hr, in budget) but cannot zero it; needs engine work (BearShort firepower, symmetric stops).
  `docs/research/bot-down-drift-fix.md`; `docs/research/bot-aggression-balance.md`; `memory/project_market_balancing_value_anchor.md`.

---

## 2. EVERY LEVER TRIED — verdict table

Verdict key: **BAKED** = live default/prod · **OFF** = built, flag-gated default-off (available) · **REVERTED** =
shipped then pulled · **FAILED** = no/negative effect on its target · **DEAD-END** = proven structurally futile.

| Lever (config key) | What it does | Verdict | Source |
|---|---|---|---|
| **Value anchor (averaged)** `ValueAnchor:Strength 0.40 / Scale 0.12` | buyProb tilt toward anchor target = the reversion floor that bounded the runaway | **BAKED** (the original stabilizer) | `memory/project_market_balancing_value_anchor.md`; `appsettings.json:144` |
| **Value anchor TARGETED** `ValueAnchor:TargetSelection` | concentrate anchor via stock selection | **DEAD-END** (catastrophic: +15,664%) — gated OFF | `memory/project_market_balancing_value_anchor.md` |
| **Liquidity depth** (relax prune) | deeper book to absorb | **FAILED** (8× WORSE, stddev 865%) | same |
| **OU-ring sentiment redesign** (τ 20s–3h per-stock + global) | cleaner mean-reverting noise, cut dispersion ~3× | **BAKED** | same; `appsettings.json:453` |
| **Weighted-week anchor** `WindowDays 7` | ring of last-7 daily TWAPs, linear taper → a runaway day moves anchor only 1/4× | **BAKED** (kills the ratchet: +91.8%→+27%) | `docs/research/bot-anchor-weighted-week-and-cap-from-seed.md`; `memory/project_weighted_week_anchor_validation.md` |
| **Cap-from-seed** `CapFromSeed true` | overheat veto measures dev from FIXED seed, not the moving TWAP | **BAKED** (makes compounding ratchet impossible) | same |
| **RecentAnchor** `Enabled, HalfLife 1800, Strength 0.10 (prod 0.05), Scale 0.04` | 30-min EWMA medium-term mean-reversion; damps fast moves | **BAKED** (biggest-mover 17.8→12.7% w/o thinning); prod **softened 0.10→0.05** so moves stick | `docs/research/bot-price-memory-and-pressure-hybrid.md`; `docker-compose.prod.yml:55` |
| **RecentAnchor OFF** (as a de-linearizer) | remove it to let mini-trends persist | **FAILED** (ret_acf −0.460→−0.472, wrong way) | `docs/research/SENTIMENT_PRICE_REACTION_FINDINGS.md:165-180` |
| **RegimeDrift** `Enabled, StepSigma 0.03, Cap 0.5, SoftWallK 0.1, Strength` (base 1.0, prod 0.5→0.8) | per-stock PERSISTENT common-mode bounded random walk added to sentiment — the main "trend engine" | **BAKED** (on). BUT it's a **sentiment/buyProb tilt ⇒ mostly ABSORBED** (A1). Also = idiosyncratic ⇒ ~0 cross-stock corr | `appsettings.json:469`; `BotSentimentService.cs:364-372` |
| **RegimeDrift Strength tuning** | 1.0 vs 0.2 vs 0.5 vs 0.8 | **CONTESTED:** 0.2 eyeball-locked for damping (GSM2.5 epoch) but **0.2 KILLS sector correlation** (conviction machine) → conviction-v2 kept **1.0**; prod later ran **0.5** (over-damped = a cause of the pin) → deploy bumped **0.8** | `memory/project_conviction_v2_bundle.md`; `docs/arcs/MARKET_PINNING_ARC.md:30-42` |
| **CoMovement** `Sentiment:CoMovement` (shared market-factor walk into the FUNDAMENTAL anchor) | cross-stock co-move via a shared beta-loaded walk on the anchor target (not sentiment) | **OFF** (built; the "correct channel" idea — anchor not sentiment) | `appsettings.json:477` |
| **GlobalSigmaMult / PerStockSigmaMult** | rebalance shared-vs-idiosyncratic ring σ for correlation | **OFF** (GSM2.5 + RegimeDrift-off moved corr 0.05→0.25, but correlation later declared NOT a goal) | `appsettings.json:454`; `memory/project_market_realism_v2.md` |
| **Bubble config** (`Sentiment:PriceReaction` #2 contrarian str12 + `MomStrength` #3 mom15/τ240) | slow-strong momentum self-reinforces then slow contrarian brakes → boom-bust waves | **OFF** (livelier/fatter tails but NOT a de-linearizer; R² went wrong way, confirmed variance; user: keep hunting) | `docs/research/SENTIMENT_PRICE_REACTION_FINDINGS.md:47-116` |
| **SlowRingDamp** `Sentiment:SlowRingDamp` | damp the slow per-stock OU rings (drift source) | **FAILED / OFF** (−6.8 composite, more linear, less activity) | `docs/research/SENTIMENT_PRICE_REACTION_FINDINGS.md:118-140` |
| **SmoothedPriceHalfLifeSec** (time-based perception-lag EWMA) | bots perceive a ~1-min-lagged price so they stop counter-trading their own impact | **FAILED / OFF** (ret_acf UNMOVED −0.443→−0.447; lifts lag5 not lag1 ⇒ ceiling is microstructural not perception) | `docs/research/SENTIMENT_PRICE_REACTION_FINDINGS.md:182-218` |
| **Order-wall / round-snap** `RoundSnapSpread 0.40` | disperse round-number-snapped limits so no monolithic wall | **BAKED** (round-grid vol 22%→1%; the one robust R5 win) | `docs/research/R5_REALISM_ROUND_REPORT.md:19-26` |
| **BounceReference=mid** | candle CLOSE keys off matcher mid, not last-trade | **BAKED** (CLOSE ret_acf −0.43→−0.17 — the headline ret_acf win) | `docs/research/REALISM_RETACF_CLOSEOUT.md`; `appsettings.json:533` |
| **Anchor reaction-lag (B) / dead-band (C) / directional-lag (#1)** | stagger the cohort's correction across minutes; zero pull inside a band | **OFF** (neutral-within-noise on ret_acf) | `docs/research/R5_REALISM_ROUND_REPORT.md`; `appsettings.json:524-528,543` |
| **PerceivedPriceDesync / ImpactDecouple A+B / TouchTighten / RefillThrottle** | engine-level attacks on the 72% flow-MR ceiling | **OFF / DEAD-END** (cleanest asymptotes ~−0.18; refill-throttle: book absorbs a 4-7% jump to ~0.1% regardless → council 5/5 STOP) | `docs/research/REALISM_CEILING_INVESTIGATION.md`; `REFILL_THROTTLE_BAKE_RESULTS.md`; `appsettings.json:546-565` |
| **ExogShock news + chaser cohort** `ExogShock:Enabled, ChaserNotionalFrac` | per-stock Poisson news shock; a hash-selected cohort submits REAL marketable orders INTO the shock (the actual VWAP ret_acf/flow lever) | **BAKED (prod on)** — first lever to actually move ret_acf/level via flow, but carries inherent down-drift alone | `appsettings.json:567`; `docs/research/CHASER_RETACF_INVESTIGATION.md`; `docker-compose.prod.yml:64-75` |
| **Chaser drift-neutral co-dials** (`ChaserSellSymFrac`, `ChaserBuyRoomRelaxFrac`) | try to zero the chaser's down-drift | **DEAD-END** (no cell passes; binding constraint = missing resting asks in an up-shock = liquidity, not room) | `docs/research/CHASER_V2_BAKE_RESULTS.md` |
| **GlobalCoFire** `ExogShock:GlobalCoFire` | on a market-wide pulse, a cohort fires ONE same-sign marketable order same-tick on a spread stock = correlated taker BURST | **DEPLOYED then REVERTED** (~2× corr; but moves "too big/too news-dependent"; correlation dropped as a goal 2026-07-16) | `docs/plans/SECTOR_PULSE_PLAN.md`; `memory/project_news_system_and_chart_tuning.md`; still wired `docker-compose.prod.yml:77` |
| **Sector pulse** `ExogShock:SectorCount/SectorFraction` | scope a pulse to `stockId % N` → intra-sector-high corr + sector rotation | **OFF** (A/B null at 45m; council-blessed to ship but deferred) | `docs/plans/SECTOR_PULSE_PLAN.md`; `appsettings.json:594` |
| **News permanence + α-coupling** `ExogShock:Permanence` | split each shock into permanent floor α·M + transient (1−α)·M; α∈[0.30,0.90] coupled −0.6 to per-event τ; residual bleeds ~3h; feeds anchor via AnchorTracksShock | **BAKED (prod on)** — the mechanism for "breakouts re-rate & stick" | `docs/research/NEWS_PERMANENCE_COUPLING.md`; `appsettings.json:597`; `docker-compose.prod.yml:81` |
| **Conviction v2** `Conviction:Enabled + SectorDriftCap 0.08 + SectorStepScale 3.0 + BankEstimate` | signed two-way smart-money cohort trading bank sector re-ratings | **BAKED (prod, re-anchor reseed)** — certified intra>inter sector corr +0.13 (p=.01); ret_acf VWAP −0.06 | `docs/research/CONVICTION_V2_STAGING_REPORT.md`; `memory/project_conviction_v2_bundle.md` |
| **Volume/vol clustering (composition coupling)** `Activity:Composition:TakerExp 0.5` + field recal | couple the Hawkes activity field → taker SHARE (the price-moving channel, previously only cadence) | **BAKED (prod, master)** — vol~|ret| +0.26, pin 45→5-8/50, ret_acf VWAP →0 | `docs/research/VOL_CLUSTERING_STAGING_REPORT.md`; `memory/project_vol_clustering_deployed.md` |
| **Size coupling** `Activity:Composition:SizeExp` | activity → order SIZE to raise volume CV | **DEAD-END** (bounded median-1 multiplier averages out by LLN; CV needs FAT-TAILED whale sizes; also worsens ret_acf) | `docs/research/SIZE_COUPLING_DECISION.md` |
| **Fear/Greed index + reflexive coupling** `Mood:*` (TakerCoupling, ConvictionFearBid, PerStrategy, MMWiden) | 0-100 mood gauge; lagged capped **intensity-only** taker multiplier; per-strategy fear reactions | **BAKED (prod, staged A→B)** — delivers down-skew/character; latch=0.00; correlation unmeasurable at 45m | `docs/ultraplans/FEAR_GREED_INDEX_ULTRAPLAN.md`; `memory/project_chart_overhaul_and_fear_greed.md`; `docker-compose.prod.yml:49-53` |
| **GlobalShock** `Sentiment:GlobalShock` (down-biased discrete market-wide bear event) + **BearShortStrength** | the "elevator down" + crash correlation + negative skew; fleet sell firepower | **OFF** (built; the planned Step-5 skew/tail/crash lever) | `appsettings.json:487,129` |
| **Jumps** `Jumps:Enabled` (rare per-stock Poisson price jump via a house aggressor, mean-reverting) | fat-tail kurtosis without a level shift | **OFF** (built; kurtosis lever, needs jump engine on) | `appsettings.json:627` |
| **TrendFollower cohort** `TrendFollower:Enabled + Strength + TakerCoupling` | chartist cohort that CHASES momentum and (v2) CROSSES the spread (real taker momentum) | **OFF, Strength 0** — the designed momentum/positive-feedback leg is BUILT but never enabled | `appsettings.json:131-141` |
| **DipBuyStrength** (one-sided idle-cash dip demand) | floors the down-drift w/o the anchor's move-killing spring | **BAKED** (prod 3.0, base 2.0) | `appsettings.json:127`; `appsettings.Production.json:38` |
| **DecisionDistanceMult** (the "limit orders ×5 closer" foundation) | rest limits closer to mid so buyProb tilts convert to fills, not deep-resting absorbed orders | **BAKED** (0.2 = 5× closer; the 2026-06-18 breakthrough that cracked ret_acf −0.44→−0.37 + composite 60+) | `appsettings.json:85`; `memory/project_sentiment_price_reaction.md` |
| **MarketProbMult** (taker share) | global multiplier on market-order rate = how much flow is taker vs absorbed limit | **BAKED** (base 1.5, prod 1.35→1.4). Lower = deeper absorbing book = more pinning | `appsettings.json:87`; `docs/arcs/MARKET_PINNING_ARC.md:34` |
| **Bracket-flip / bracket-disable** | brackets caused stop-cascade escapes + down-drift | brackets **disabled in seed** (drift −2.32%→+0.54%, +43% throughput); flip-eligibility = **OFF** (perf-gated) | `docs/research/bot-bracket-flip-eligibility.md`; `memory/project_r4_realism_session_complete.md` |
| **Bank estimate + Rotational bots** | published per-stock fair value + a no-cash-band cohort that rotates capital toward it = taker delivery of a fundamental | BankEstimate **BAKED (prod on)**; Rotator **BAKED on @ PF 0.10** (EXP2 raising it gave no corr + deepened drift → reverted to 0.10) | `docs/plans/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md`; `docker-compose.prod.yml:53-54`; `appsettings.Production.json:39` |

---

## 3. THE ANCHOR / FUNDAMENTAL / DRIFT / NEWS MECHANISMS AS THEY EXIST NOW

### 3a. The value anchor (the pin) — `AiBotDecisionService.cs`
- Computes `rawValueGap = AverageWatchlistValueGap = mean over watchlist of (target − price)/target` (`:2713-2732`),
  where `target = Fundamental()` which, with `UsePreviousDayAverage=true`, is
  `BotPriceMemoryService.GetPreviousDayAverage()` — the **7-day weighted TWAP of price, clamped to seed×[1±0.5]**
  (`:2782-2795`; `BotPriceMemoryService.cs:223-331`).
- Live tilt path (Elastic=false, AnchorDeadbandPrc=0): `anchorTilt = (gap/Scale) × Strength`, linear, no deadband
  (`:1519-1523`). Added into `buyProb` (`:1549`, `BuyProbHybrid` `:2672-2681`) — **a probability tilt, so it leans
  order direction/limit placement, it does not place taker orders.** ⇒ absorbed (Axiom A1).
- **History / why it's set here:** the anchor was added to stop unbounded runaway (`project_market_balancing`).
  `UsePreviousDayAverage` + `WindowDays 7` + `CapFromSeed` were added to kill a multi-day compounding ratchet
  (`bot-anchor-weighted-week-and-cap-from-seed`). Nobody re-examined that the price-tracking TWAP makes the anchor a
  **self-fulfilling pin** once the market sits somewhere — that is the likely structural driver of the current flat pin.
- Hard veto (separate from the tilt): `IsOverBand` measures deviation from **seed** (CapFromSeed) against the
  geometric `AbsoluteCapMax=2.0` (×3/÷3) — a runaway backstop, NOT the pinning force. `:2250-2270`.

### 3b. FundamentalService (currently BYPASSED for the anchor) — `FundamentalService.cs`
- OU walk reverting to seed, clamped `seed×[1±Band]`, `Band 0.12, Theta 0.02, Sigma 0.004, DriftInterval 60s`
  (`appsettings.json:107-114`). Used as the anchor target ONLY when `UsePreviousDayAverage=false`. It still Ticks
  (deterministic RNG) so a rollback is one flag. `Get()` clamps to `seed×[1±(Band+ShockCap+CoMoveShiftCap)]`
  (`:183-186`). `AnchorTracksShock=true` makes its target = `current×(1+shock)` — this is the channel news permanence
  re-rates (`:35-36`), and it feeds the anchor via `_bankTarget`/estimate when on.

### 3c. RegimeDrift (the intended random-walk energy) — `BotSentimentService.cs`
- Per-stock **bounded random walk** (NOT mean-reverting): `step = U(±1)·√3·StepSigma·√dt`; `RegimeStep` applies a
  **cubic soft-wall** (`BotMath.SoftWallStep`): ~free in the middle, walled near ±Cap; then `_combined[sid] +=
  Strength × _regime[sid]` (`:364-372,418-422`). Config `StepSigma 0.03, Cap 0.5, SoftWallK 0.1, Strength` (base 1.0,
  prod 0.5, deploy 0.8) (`appsettings.json:469`).
- **KEY LIMITATION:** its output is added to SENTIMENT → GetSentiment → buyProb. It is a tilt, so **absorbed by the
  book** (A1) unless it pushes sentiment past |1| (which forces market orders via `ApplyExtremeReaction`,
  `:2797-2813`). At Cap 0.5 × Strength 0.8 it can't reach that regime on its own ⇒ it wiggles the buy/sell ratio but
  rarely delivers persistent TAKER flow. **This is why halving it over-damped and restoring it only partly unstuck
  the pin — it was never the taker-flow lever it's treated as.** Design intent: `docs/plans/random-walk-sentiment-plan.md`.

### 3d. News permanence — `ExogenousShockService.cs` + `RandomShockSource` (prod ON)
- Each Poisson news event draws latent `z` → permanent floor fraction `α = clip(AlphaMin + (AlphaMax−AlphaMin)·Φ(z+..),
  AlphaMin, AlphaMax)` and transient half-life `τ = clip(TauMedian·exp(−Coupling·z+..), TauMin, TauMax)`, corr(α,lnτ)
  ≈ −0.6. Impulse: `residual += α·M`, `transient += (1−α)·M`; per tick `transient *= 2^(−dt/τ)` (bleeds to raised base
  α·M, never 0), `residual *= 2^(−dt/ResidualHalfLife)` (~3h). `GetShock=residual+transient` → anchor tilt (via
  AnchorTracksShock); `GetTransient` → chaser cohort. `docs/research/NEWS_PERMANENCE_COUPLING.md`.
- Prod-effective knobs (base `appsettings.json:597` + `docker-compose.prod.yml`): `AlphaMin 0.30 (deploy 0.40),
  AlphaMax 0.90, TauMedianSec 1500 (deploy 2000), TauMin/Max 300/2400, Coupling 0.6, ResidualHalfLifeSec 10800`,
  aftershocks Poisson `Lambda 0.6` MaxDepth 1, tiers Individual/Sector/Global.
- ExogShock envelope on prod (`docker-compose.prod.yml:64-79`): `Enabled true, MeanIntervalMinutes 60, MaxMagnitude
  0.12, Cap 0.25, MagnitudeExponent 2.5, AnchorTracksShock true, ChaserFraction 0.10, ChaserNotionalFrac 0.06,
  GlobalFraction 0.25, GlobalCoFire true 0.15/0.10`.
- **⚠️ `DecayHalfLifeSec` is INERT while Permanence is on** (each event carries its own τ); the real transient dial is
  `Permanence:TauMedianSec`. (`docs/arcs/MARKET_PINNING_ARC.md:44-60`.)
- **Why news pops snap back:** news is drift-NEUTRAL (50/50 sign) and sparse (~hourly), so residuals cancel over time,
  AND the market is too damped/absorbed to follow the re-rated anchor; the transient decays → the pop reverts.

### 3e. Mood / Fear-Greed reflexive coupling (prod ON) — `MarketMoodService`, `Bots:Mood:*`
- Composite `mood = 50+50·tanh(Σ w·signals)`; a **lagged (5-min EMA), capped (±0.15), intensity-only** multiplier on
  each bot's market-order propensity (`TakerCoupling`), plus per-strategy fear reactions (`PerStrategy`, `MMWiden`,
  `ConvictionFearBid`). Never fed back as a decision DIRECTION (no circularity). Asymmetric-V locked (GainGreed 0.10/
  GainFear 0.07). Prod all-on (`docker-compose.prod.yml:49-53`). `docs/ultraplans/FEAR_GREED_INDEX_ULTRAPLAN.md`.

### 3f. The stabilizers around it
- `RecentAnchor` (30-min EWMA reversion, prod Strength 0.05) + `DipBuyStrength 3.0` (one-sided dip floor) +
  `CashHomeostasis` (MaxShift 0.30, restores cash to band midpoint) + `SentimentDynamics` (slope-aware per-strategy
  phases, MomentumConviction 0.15 etc.) + Herding (FollowerFraction 0.25, Tilt 0.10). All BAKED.

---

## 4. PRIOR COUNCIL VERDICTS ON REALISM

- **Bank-estimate / rotational bots (2026-07-07, 2 rounds):** the decoupling ("what's true" = a drifting published
  fair value, separated from "who enforces it" = a market-order cohort that CAN'T be book-absorbed) is structurally
  right and the sim's first semantic layer. **BUT it's a VALUE/CONVERGENCE mechanism, not MOMENTUM** — once price
  reaches the estimate the cohort stops = a RE-PIN (magnet moves seed→estimate), snappier first 10-20% then flatline.
  Persistent TRENDS need momentum/correlated belief (buy because rising), which this isn't. CRUX: only works if the
  estimate genuinely DRIFTS directionally; if it mean-reverts in its ±10% clamp it's just a twitchier pin. Sequence:
  knobs first → estimate in SHADOW mode → rotators+reseed only if shadow data justifies. `docs/arcs/MARKET_PINNING_ARC.md:106-129`;
  `docs/plans/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md`.
- **Pinning-arc chairman (2026-07-21):** ship the knob A/B first (the honest, reversible test of flow-vs-absorption);
  the estimate is worth building for semantics + a one-time re-rating but is NOT by itself the cure for "won't trend"
  — pair it with a momentum leg. Do NOT start rotator+reseed until tuning is settled. (Deploy went ahead; pinned
  count 20→7, MSFT unfroze 511.6→513.8, then regressed — the deeper pin remains.) `MARKET_PINNING_ARC.md:126-166`.
- **ret_acf close-out (2026-06-22, 5/5) + refill-throttle (2026-06-27, 5/5):** the flow half (72%) of the −0.43
  ceiling is STRUCTURAL at the bot-decision layer; stop chasing it; ship bounce-mid; only a core-engine rewrite
  (excluded by scope) could move it. `docs/research/REALISM_RETACF_CLOSEOUT.md`; `REFILL_THROTTLE_BAKE_RESULTS.md`.
- **Global co-fire (2026-07-04):** "bigger chaser" fixes the wrong axis — the failure is DILUTION; the real lever is
  DETERMINISTIC CO-FIRING (whole cohort, same-tick, same-sign, sized to clear depth). Fast decay is a red herring &
  harmful — flow impact is permanent. Perf is the binding gate. `docs/plans/GLOBAL_SHOCK_PLAN.md`.
- **Fear/Greed (Fable, 2026-07-14):** 2-stage prod deploy (absorber core PerStrategy+ConvictionFearBid first, prove it
  cushions a real fear-excursion-that-recovers, THEN +MMWiden amplifier); kill on slow-fear ratchet. Latch-up is the
  top risk; first dial-down is gain not lag. `memory/project_chart_overhaul_and_fear_greed.md`.
- **Conviction-v2 (2026-07-12):** keep the displayed chart on mid (not VWAP — VWAP is a scorer-only basis, a sim
  tell); keep RegimeDrift 1.0 (0.2 kills sector corr); judge correlation DEMEANED. Next arc = fat tails via
  volatility/volume clustering (which then shipped). `memory/project_conviction_v2_bundle.md`.
- **Market-realism-v2 root cause (council, ongoing):** the three pathologies (no autocorr, no correlation, thin tails)
  are ONE root cause — ~20k independent bots; the fix is a shared persistent DIRECTIONAL order-FLOW factor a fraction
  of bots chase, acting downstream of the anchor (shared sentiment is damped; shared flow is not). Keep a residual
  idiosyncratic term (don't fully delete RegimeDrift → lockstep). `docs/plans/REALISM_OVERHAUL_PLAN.md:36-44`.

---

## 5. KNOWN DEAD ENDS (do not re-propose)

1. **Fighting the −0.43 ret_acf with any timing/perception/anchor knob.** 25+ experiments null: bubble, SlowRingDamp,
   RecentAnchor-off, SmoothedPrices (τ60 engine lever), ImpactDecouple A+B, TouchTighten, RefillThrottle, PerceivedPriceDesync,
   directional/anchor reaction-lag. It's ~28% mechanical bounce + ~72% structural flow-MR. Resolved cosmetically by
   bounce-mid. (§A3.)
2. **Symmetric hard caps to shape the body** (`AbsoluteCapMax` as a pull) — reverted twice; pins the tail, halves
   throughput. Keep only as the ×3/÷3 geometric runaway veto. (§A4.)
3. **Making buyProb/sentiment tilts bigger to move the level** — absorbed by the book. This includes cranking the
   value anchor, RegimeDrift, or a buyProb-tilt "chaser" (`ChaserStrength·tanh` was retired for exactly this). (§A1.)
4. **Targeted/selection-based value anchor** — catastrophic runaway (+15,664%). Liquidity-depth relaxation — 8× worse.
5. **Size coupling for volume CV** — bounded multiplier averages out (LLN); real CV needs fat-tailed whale sizes.
6. **Chaser drift-neutral co-dials** (`ChaserSellSymFrac`/`BuyRoomRelaxFrac`) — no cell in the grid passes; the
   binding constraint is missing resting asks in an up-shock (liquidity), not position-room.
7. **Probabilistic per-bot co-firing for correlation** — DILUTES the shared sign; needs deterministic same-tick co-fire.
8. **Raising the news floor to make events "noticeable"** — the spec explicitly forbids it; most events must be
   invisible or it reads rigged. Also raising Rotator PF (EXP2) gave no corr + deepened drift.

---

## 6. OPEN THREADS / UNTESTED IDEAS for "make the random walk persistently move price"

These are the live candidates the council should weigh. The through-line: **the random walk currently pushes only
SENTIMENT (absorbed); to move the LEVEL persistently it must push FLOW or the ANCHOR TARGET, and the anchor must stop
re-pinning to recent price.**

1. **Break the self-pinning anchor (highest-leverage, addresses the actual pin).** The value anchor tracks a 7-day
   price TWAP (§1, §3a) ⇒ it re-rates to wherever price sits = a self-fulfilling flat. Options: (a) shorten/zero
   `WindowDays` so the anchor stops chasing price; (b) flip `UsePreviousDayAverage=false` back to the OU-to-seed (with
   a wandering fundamental, see #2); (c) an ASYMMETRIC deadband so the anchor doesn't defend the pinned level. Untested
   in the current config. **NOTE:** the anchor being weak (absorbed) is why unpinning also needs a flow lever, not just
   an anchor change.
2. **A WANDERING FUNDAMENTAL (drive the random walk through the ANCHOR TARGET, not sentiment).** The `CoMovement`
   mechanism already injects a bounded random walk into the FUNDAMENTAL target (the un-damped channel) — repurpose that
   pattern per-stock: a persistent random walk on each stock's *fundamental* (not sentiment) so the anchor itself
   drifts and DipBuy/anchor flow chase it. This is the council's own "shared flow downstream of the anchor" idea
   applied to persistence. Bounds: `seed×[1±(Band+ShiftCap)]` + geometric veto. `appsettings.json:477`; REALISM_OVERHAUL Step-4.
3. **Turn ON the momentum/trend-follower cohort (BUILT, `Strength 0`).** `TrendFollower:Enabled=true` with
   `TakerCoupling=true` is a chartist cohort that CROSSES the spread in the momentum direction = real taker momentum =
   positive short-term autocorrelation = persistent runs. This is exactly the momentum leg every council said the value/
   convergence mechanisms lack. Risk: positive feedback → runaway; introduce via the geometric STRENGTH ladder
   (0.15→0.25→0.35), contrarian minority + per-bot stagger, kill-at-10-min on ret_acf-crosses-0 or a cap touch.
   `appsettings.json:131-141`; `docs/plans/REALISM_OVERHAUL_PLAN.md:77-92`.
4. **Deterministic co-fire on a PERSISTENT (AR(1)/OU) shared factor, not just impulse news.** Make the shared driver a
   persistent level (not decaying impulses) whose co-fire delivers correlated TAKER flow — the council's revised
   global-shock design. Gives durable directional moves + correlation. Perf-gated. `docs/plans/GLOBAL_SHOCK_PLAN.md`;
   REALISM_OVERHAUL Step-4.
5. **Deadband ASYMMETRY / re-baselining the seed.** Owner's movement model wants "20%+ over a few days STICKS but
   becomes a SELL DRIVER." A one-sided/graduated soft-wall (weak with the move, stiff against re-extension) plus a slow
   re-baseline of the seed toward a stuck level would let a move persist without the symmetric snap-back. Untested; note
   AbsoluteCapMax's symmetric-veto failure (§A4) is the cautionary precedent — must be graduated, not a hard clamp.
6. **Bank estimate that genuinely DRIFTS (not mean-reverts in its clamp) + rotators.** Council-approved IF the estimate
   drifts directionally; in SHADOW mode first. Delivers a one-time re-rating with a causal "why" + rotator taker flow,
   but is convergence not momentum — pair with #3/#4 for persistence. `docs/plans/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md`.
7. **Raise news permanence α further** (`AlphaMin`/`AlphaMax`) so more of each event permanently re-rates the anchor —
   the correct knob for "breakouts stick" (already nudged 0.30→0.40). Bounded; but news is sparse/symmetric so it can't
   be the sole trend source. `docs/arcs/MARKET_PINNING_ARC.md:62-73`.

**Synthesis for the council:** the persistent-move problem is two coupled failures — (i) the LEVEL-restoring machinery
is a price-tracking self-pin (fix via #1/#5), and (ii) the only "energy" injected (RegimeDrift) is a sentiment tilt
that the book absorbs (fix by moving the walk into FLOW or the ANCHOR TARGET — #2/#3/#4). News works *because* it hits
FLOW (chaser) + the anchor (AnchorTracksShock) simultaneously; the random walk does neither today. The cheapest honest
test is likely #3 (momentum taker cohort, already built) and/or #2 (wandering fundamental), gated by the standard
parallel A/B + CK=0 + kill-at-10-min discipline.
