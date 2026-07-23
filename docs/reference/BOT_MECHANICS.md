# BOT_MECHANICS.md — how the KieshStockExchange bots work + the target behavior

Compact reference for the bot-trading systems and the market-behavior targets. **Consult + UPDATE this file whenever a bot mechanism changes** (same commit).

**What this is:** ~20k simulated trader bots on 50 stocks (70 cross-listed USD/EUR listings). A single server-side loop ticks ~once/second; on each tick a slice of the fleet each computes a directional probability (`buyProb`), turns it into a REAL limit/market order, and submits it to a REAL matching engine. Price is not scripted — it EMERGES from the order book as those orders match. The bots are the only participants (plus a few house cohorts), so every mechanism in this doc exists to shape that emergent price into something that looks like a real market (§1). Nothing else here makes sense without that frame.

**Where the code lives:** the loop + per-bot decision + all signal services are in `KieshStockExchange.Server/Services/BackgroundServices/` and `…/Helpers/` (e.g. `BotSentimentService`, `MarketMoodService`, `ExogenousShockService`, `FundamentalService`, `BankEstimateService`, `BotActivityService`, `BotScalerService`, `AiBotDecisionService`, `MarketMakerDecisionService` all live under `…/Helpers/`); models are in `KieshStockExchange.Shared/Models/`. Line refs (`AiTradeService.cs:NNNN`) are as of commit `cccc9d0` — **the symbol name is the stable handle; grep the method if a number has drifted.** Config VALUES live in `appsettings.json` (`Bots:*`) and the seed `Tools/Config.py`; §2 references the config KEYS, not hard values (§2.10 is the marked exception). Decision history + config snapshots live in the plan log, not here.

**Map:** §0 = *why price moves at all* (the kernel every §2 lever hangs off). §1 = the behavioral scorecard (what the market should look like). §2 = the mechanism catalog (each lever: what/keys/why). §3 = the **main tick loop** (how the fleet actually runs, per tick). §4 = the **per-bot decision path** (mood → order). §5 = the strategy cohorts. §6 = order → engine → telemetry/scaler feedback. §7 = the loop/infra `Bots:*` config index. §3–§7 are the "how it runs" half; §2 is the "what each knob does" half — they cross-reference, they don't duplicate.

**First read (newcomer):** §0 → §3 (the loop) → §4 (one bot's decision) → §5 (cohorts) → §6 (engine handoff). Use §1 (scorecard) + §2 (lever catalog) as lookup tables, not narrative.

**The loop, in one line:**
```
sentiment/regime/news/bank/mood ticks ─▶ per-bot decide (buyProb ─▶ order) ─▶ batch submit ─▶ match + settle
        ▲                                                                                        │
        └──────────── fills feed back: activity field · mood taker-flow · price cache · load scaler ◀┘
```

**Status tags in §2** (a lever's default state, NOT its prod state — prod enablement lives in `docker-compose.prod.yml` env, see §2.10): *(off)* = default-off + byte-identical (adds exactly 0 / draws 0 RNG when disabled); *(off — NULL …)* = built but found ineffective in testing, kept for the record; *(base off; prod on)* = ships off in `appsettings.json`, enabled on the live box via env.

**Glossary** (terms used unglossed throughout):
- **CK** — conservation check: the invariant that no money or shares are created/destroyed. `CK=0` = clean. Verified live by `ConservationProbe`. HARD gate.
- **taker / maker** — a *taker* (marketable/market order) crosses the spread and CONSUMES resting depth → moves the mid. A *maker* (resting limit) ADDS depth at a fixed level and waits → does not move the mid. This asymmetry is the doc's central mechanism (§0).
- **LLN** — law of large numbers: N independent ±1 bets net to ~√N, so the *imbalance fraction* → 0. Why 20k independent bots flatline the chart unless their bets are correlated (§0, §2.4).
- **ret_acf** — lag-1 autocorrelation of 1-min VWAP returns. ~0 = random walk (the target); strongly negative = over-mean-reverting (the known structural failure).
- **soak** — a timed local test run (15m smoke / 45m A/B workhorse / 2h bake); the scorecard in §1 is graded against a soak.
- **OU ring** — an Ornstein-Uhlenbeck (mean-reverting AR(1)) process; sentiment/fundamentals are sums of these at several timescales.
- **byte-identical (off)** — a lever disabled leaves the RNG stream + every number unchanged vs. before it existed → default runs are reproducible.
- **latch** — global mood pegged <30 or >70 for a sustained stretch = the fear-spiral tell (§2.10); `latch=0` is a gate.
- **factorR² / market-R²** — share of a stock's return variance explained by the common (market) factor = the cross-stock correlation measure.
- **book-absorbed** — a resting-limit or sentiment tilt that adds/removes depth without moving the mid because the opposing book (or MM refill) swallows it. The reason non-taker levers don't move realized price (§0).

---

## 0. WHY PRICE MOVES — the first-principles kernel
Everything in §2 is an answer to one question: with 20k independent bots, why does the chart move at all? The causal chain, derived once:

1. **The LLN flatline.** 20k bots each drawing an independent buy/sell side net to an imbalance of order ~√N, so the imbalance *fraction* ~1/√N → ~0. Independent bots average out → a dead, arithmetic-looking tape. **Correlating the draws is the whole game:** if a fraction of bots hold the same side (inertia/herding, shared sentiment, a co-fire pulse), net imbalance scales with N·(correlated fraction) instead of √N. That is why a *hold-time* or *herding* lever moves a *price* metric.
2. **Depth absorbs limits.** A resting limit adds depth at a fixed level; the opposing book (and MM refill) consumes it *without the mid moving*. So a `buyProb` tilt that produces mostly resting limits is **book-absorbed** — it changes queue depth, not price. This is why shared *sentiment* alone caps cross-stock correlation ~0.08: the tilt lands as limits and gets swallowed.
3. **Only taker flow prints through levels.** A marketable (taker) order consumes standing depth and executes at successively deeper levels → the mid moves. Impact ≈ **direction × taker-ness × size**. Price moves iff conviction is expressed as *taker flow*, not resting depth.
4. **Therefore one conviction signal, three routings.** Feed a directional signal to taker flow three ways and you get three realism fixes from one mechanism: **per-stock momentum** taker → `ret_acf` toward 0 (§2.2/§2.4); a **shared** taker burst → cross-stock correlation (§2.7 co-fire, §2.10 mood coupling); a **down-skewed global** taker shock → fat left tails (§2.7 shock/jumps). Anchors (§2.5) are a *separate, upstream* damper — they bound the sentiment tilt *before* it becomes orders; they are not what absorbs a resting limit (the book is).

Keep this split straight: **the anchor damps the tilt; the book absorbs the limit; only the taker moves the mid.**

---

## 1. TARGET VALUES — the scorecard the market should hit
*This table is the acceptance scorecard graded against a soak run — reference/tooling, skip on a first read.*
**Realism** = real-market norm · **Kiesh** = the owner's chosen target · **P** = priority 1 (high) – 5 (low).

**★ REVISION 2026-07-23 (Kiesh sign-off steer) — RANDOM-WALK-FIRST, less news-dependent.** The prior "typical ±5% /
>10% NEWS-ONLY" targets were TOO STRONG: they made the charts too DEPENDENT ON NEWS EVENTS for movement and too little
random-walk-like. New north star = **NATURAL + RANDOM-WALK-LIKE**: (a) DECREASE the typical-move target (smaller everyday
moves, **~±2–3%** not ±5%); (b) reduce NEWS strength + dependence (news = a *contributor*, not the main mover — see the
news-strength cut in §methods / `FINE_TUNING_TARGETS.md`); (c) INCREASE **organic** random-walk texture so price wanders on
its own (MarketPulse osc+jitter breathing the regime-taker rate + base taker flow), and relax ">10% = news-only" (rare
ORGANIC >10% moves are fine too). Movement rows below carry the revised targets; the pre-revision values are struck through.

| Group | Metric | Realism | Kiesh target | P |
|---|---|---|---|---|
| **Movement** | typical intraday move | ±1–3% most days | **~±2–3%, random-walk-driven** (was ~±5%) | 1 |
| | active / best movers (daily) | 5–10% | 5–10% | 1 |
| | source of movement | mixed | **ORGANIC random-walk FIRST; news a contributor, not the main mover** | 1 |
| | >10% moves | news-driven, rare | rare — **organic OR news** (was NEWS-ONLY) | 2 |
| | biggest movers | 10–20% on news | 15–25% on news | 2 |
| | big-news frequency | occasional | very rare — ~once per stock per WEEK | 1 |
| | rise vs crash shape | crashes sharper (leverage) | stairs-up (slow +drift) + elevator-down (RARE global crash events override the buy-floor) | 3 |
| | multi-day trend | mean-reverts over weeks | 20%+ sticks → SELL driver | 3 |
| | price band (backstop) | none (fat tails) | ×3 / ÷3 elastic; extreme rare | 2 |
| **Returns** | random-walk path @ ALL timeframes | 1-min VWAP ret_acf −0.02…−0.10 | random-walk on every timeframe (ret_acf→−0.1 VWAP); damp SLOW trends, keep the FAST 1-min walk | 1 |
| | excess kurtosis (fat tails) | 10+ | fat-but-RARE, bounded ~4-6 (diagnostic, ×3 cap) | 3 |
| | daily return skew | −0.3…−0.5 | ~log-symmetric per move; crashes sharper | 3 |
| **Cross-stock** | pairwise corr (calm) | 0.2–0.3 | ≥ ~0.2 | 2 |
| | crisis corr | 0.7–0.9 | correlated crashes | 2 |
| | idiosyncratic share (market-R²) | 0.2–0.3 (70–80% idiosyncratic) | distinct, NOT lockstep | 2 |
| | sector rotation | sectors co-move | intra > cross | 4 |
| **Safety** | conservation (CK) | exact | 0 ALWAYS — no money/shares created or destroyed | 1 HARD |
| | net drift (direction) | ~0 + small premium | POSITIVE + low over a WEEK (intraday can dip on crashes) | 3 |
| | price runaway | bounded | none (band + cap) | 1 |
| **Liquidity** | volume / activity | continuous | lively, NOT deadened | 2 |
| | **per-stock liveness** (no empty candles) | continuous | **every stock traded ≥1× per 15 s** (very rare empties) — raise the ACTIVATION amount (active bots/tick), NOT sparse activation, NOT fewer pairs (the 70 cross-listed USD/EUR listings are fixed); calibrate on PROD | **2** |
| | taker share | (subsumed by volume + impact) | — | 4 |
| | spread / book depth | tight liquid / wider thin | realistic / adequate | 4 |
| **Population** | momentum-amplifier share | significant but takers-IN / limits-OUT | TBD (maybe 47% → 25–30%, reseed) | 3 |
| | strategy diversity | momentum / value / MM / arb mix | diverse | 3 |
| **FX** | USD/EUR coupling band | 0.1–0.5% | ~0.3–0.5% (don't force → parity) | 4 |
| | FX intraday vol | ~1% | mean-reverting bounded ~1% | 4 |
| **Clustering** | \|return\| autocorr (vol clustering) | +0.15…+0.35, long-memory | vol clusters (calm → storms) | 2 |
| | leverage effect (return → vol) | −0.1…−0.4 | vol rises after drops | 3 |
| | aggregational Gaussianity | kurtosis ↓ with horizon | fat 1-min thins by daily | 3 |
| **Volume / flow** | volume ↔ volatility corr | +0.4…+0.7 | big-move days = high volume | 3 |
| | daily turnover (vol / float) | 0.5–2%/day liquid | plausible vs float | 4 |
| | price-impact shape | concave / √size | sub-linear in trade size | 3 |
| | order-flow (trade-sign) autocorr | +0.3…+0.7, long-memory | buys follow buys (trend mechanism) | 3 |
| **Global** | index vol vs single-stock | ratio ~0.35–0.55 | diversification works | 3 |
| | cross-sectional dispersion | 1–3% calm / 4–8% crisis | not lockstep | 3 |
| | market breadth (adv/decl) | up-days ~55–65% advance | broad-based | 4 |
| | index return autocorr | ~0 (near-martingale) | no exploitable pattern | 4 |
| **Tails** | tail index α | 3–5 | crash magnitude sane | 4 |
| | trade-size distribution | power-law ~1.5–2.5 | most tiny, rare huge | 4 |
| | short-term reversal (~1 wk) | losers → winners | mean-reversion days-weeks | 4 |

**Grade it (soak gate-set — these are REGRESSION BOUNDS "don't get worse", NOT pass-targets):** ret_acf(VWAP) −0.5…−0.1 (−0.5 = the known structural ceiling, §4.2; the target column's →−0.1 is aspirational) · kurtosis ≥ 4 · median excursion 3–8% · p95 10–20% · max 15–35% · CK = 0 · taker 20–50% ·
spread < 0.5% · |return|-autocorr > 0.05 · cross-sectional dispersion > 0.002 · pairwise corr 0–0.25 (the target ≥0.2 is ASPIRATIONAL — the arc's finding is a ~0.13 factorR² ceiling with bot levers alone; real 0.2–0.5 needs core-engine shared-book coupling, and corr is only judged over PROD days, not a 45m soak).
**Eyeball / long-soak only** (unfalsifiable in a 45m soak): big-news frequency, weekly drift, daily skew, leverage effect, aggregational Gaussianity,
multi-day trend, sector rotation, momentum, **cross-stock correlation**. **Out-of-scope for a 24/7 sim:** day-of-week, intraday U-shape, auctions, implied vol.
*(The bid-ask bounce is a mechanical source of negative 1-min ret_acf — not a bug.)*

---

## 2. SYSTEMS — mechanism reference
Each entry: **what** · `config keys` (under `Bots:*` unless noted) · *why*. Values + decision history live in `appsettings.json` / the plan log, not here (§2.10 is the marked exception). Status tags *(off)* / *(off — NULL …)* / *(base off; prod on)* are defined in the legend at the top of this file. Per-bot geometry is SEEDED (see §2.9), not in appsettings.

### 2.1 Sentiment — the mood signal each bot reads (`BotSentimentService`)
- **OU sentiment rings** — every stock sums a stack of mean-reverting AR(1) rings across ~5 per-stock timescales (seconds→hours) plus a shared 3-timescale global/common-mode ring. `Sentiment:PerStockSigmaMult`, `Sentiment:GlobalSigmaMult` (amplitude scalers). *Fast rings → per-stock dispersion; the slow global ring → a shared regime. Corr ≈ shared²/(shared²+idio²), so GlobalSigmaMult↑ / PerStockSigmaMult↓ raises cross-stock **sentiment** correlation — but note (per §0) that a sentiment tilt lands as resting limits and is book-absorbed, so this channel is INERT for realized-RETURN correlation (caps ~0.08); real return corr needs the taker channel (§2.7 co-fire, §2.10 mood coupling).*
- **RegimeDrift ("System A")** — a per-stock BOUNDED random walk (not mean-reverting), cubic soft-walled near its cap. `Sentiment:RegimeDrift:{Enabled,StepSigma,Cap,SoftWallK,Strength}`. *Gives each stock an independent minutes-long wander; `Strength` is the idiosyncratic denominator of the correlation ratio.* **RegimeDrift→TAKER coupling LIVE on prod 2026-07-22** (`Sentiment:RegimeDrift:{TakerCoupling,TakerStrength,TakerThreshold,CohortFraction,ContrarianFraction}`, prod TakerStrength 0.4): post-pick per-stock override routes |regime|≥threshold to a slippage-market taker in the regime direction (reuses `TrendTakerDecision`, no-share guard, value-band vetoed) — the buyProb tilt alone was book-absorbed, so this is what makes the random-walk actually MOVE price.
- **GlobalShock** *(off)* — a market-wide Poisson shock: a signed, down-biased scalar decays over ticks into every stock. `Sentiment:GlobalShock:{Enabled,MeanIntervalHours,Min/MaxMagnitude,MagnitudeExponent,DecayPerTick,DownBias}`. *Correlated market-wide fear ("elevator down") that per-stock sentiment can't make.*
- **PriceReaction** *(off)* — contrarian price→sentiment feedback: leaky-integrates each stock's return and pushes sentiment against a sustained move (optional fast-momentum term). `Sentiment:{PriceReaction,ReactStrength,ReactTauSec,ReactDeadband,ReactCap,MomStrength,MomTauSec,MomCap}`. *Bends the linear drift a long-τ ring prints.*
- **CoMovement** *(off — NULL for corr in the arc)* — one shared bounded walk each stock loads onto via a per-stock beta, shifting the FUNDAMENTAL anchor target (not sentiment). `Sentiment:CoMovement:{Enabled,StepSigma,Cap,SoftWallK,Strength,BetaSpread}`. *Shared repricing via the channel the anchor supports rather than damps.*
- **SlowRingDamp** — scalar on the SLOW per-stock rings only. `Sentiment:SlowRingDamp` (1.0 = inert). *Attacks slow-ring drift without blunting the fast bounce.*

### 2.2 Decision model — mood → order (`AiBotDecisionService`)
- **SentimentDynamics slope model** — biases on the two-timescale sentiment SLOPE, not the raw level; each strategy responds with a distinct shape (Scalper→fast slope, TrendFollower→slow slope, MeanReversion→fade + reversal-at-extreme, MM→gentle lean). `SentimentDynamics:{Enabled,SlopeTauFastSec,SlopeTauSlowSec,SlopeScaleFast,SlopeScaleSlow}`. *Decouples buyProb from a static level → trend/reversal/rollover behave differently.*
- **Conviction dials** — per-strategy amplitudes on the directional bias; `AggressionBoost` converts conviction magnitude into extra TAKER (spread-crossing) share. `SentimentDynamics:{MomentumConviction,ScalperConviction,ReversionConviction,ReversalConviction,MarketMakerLean,AggressionBoost}`. *The directional loop gain + how strongly conviction crosses the spread.*

### 2.3 Strategies & orders
- **Strategy mix** — seeded population: MarketMaker / TrendFollower / MeanReversion / Random / Scalper (weighted) + a small separate Arbitrage cohort. Seeded (`Tools/Config.py:STRATEGY_WEIGHTS`, `ARBITRAGE_COHORT_SIZE`). *~half the fleet are momentum amplifiers (trend+scalper), the rest dampers — sets the loop gain.*
- **Order type + limit tiers** — each bot draws market/limit + slippage probabilities; limit orders ladder into Close/Mid/Far tiers at seeded distances. `Tiers:{CloseProb,MidProb}`, `MarketProbMult`, `DecisionDistanceMult` (global distance scaler), `Liquidity:OffsetMult`; tier bands seeded. *Dense Close touch churns; the Far rung is the wall that absorbs stop-sweeps.*
- **Advanced orders** — protective stop-market sell + a `BuyStopFraction` of buy-stops, trailing stops, short-opens; per-strategy probs seeded. Brackets are seeded-ZERO (removed: SL-cascade + throughput). `Advanced:{Enabled,BuyStopFraction,StopSlippagePct,MaxQty}`; profiles seeded (`ADVANCED_PROFILES`). *Stops add real taker flow; BuyStopFraction symmetrizes up/down taker pressure (no sell-stop-only down-drift).*
- **Market-maker quoting** — strategy-0 MM bots (in the normal fleet) post two-sided resting quotes on the thinner book side at a half-spread. `MarketMakerQuoting`, `QuoteHalfSpreadPrc`. *Two-sided resting liquidity → tight spreads + depth for sweeps.* (Distinct from the dedicated MM-**house** cohort — a separate `RunAsync` pass, strategy-6, master-gated by `MarketMaker:Enabled` and default-off in base appsettings; that cohort + its MMWiden fear-widen are described in §5 and §2.10.)

### 2.4 Flow persistence — beats the LLN cancellation of 20k independent bots
- **Herding / Inertia** — Inertia locks a bot's buy/sell side for a seeded multi-minute hold (no re-draws during the hold); Herding gives a follower fraction a common regime tilt. `Imbalance:{Inertia,Inertia:MinSec,Inertia:MaxSec,Inertia:Leak,Herding,Herding:FollowerFraction,Herding:Tilt}`. *Correlating the ±1 draws makes net imbalance scale ~N·(correlated fraction) instead of ~√N (§0 step 1) — that's the whole reason a hold-time lever moves a price metric.*
- **Reaction-persistence split** *(off — designed to REPLACE Inertia when enabled; Inertia is what runs today)* — a fast conviction signal feeds a per-bot AR(1) "pressure" with a seeded half-life; a taker override crosses the spread when pressure is high. `Imbalance:ReactionPersistence:{*,PersistMinSec,PersistMaxSec,WLocal,WShared,TakerCoupling}`. *Fast reaction (no lockstep latency) + separately-decaying conviction that STICKS as taker flow.*

### 2.5 Anchors & caps — bound + revert price (`FundamentalService` + anchor tilts)
- **Value anchor** — tilts buyProb toward the stock's fundamental target ∝ deviation/scale, capped; optional elastic (deadband + superlinear) variant. `ValueAnchor:{Strength,Scale,Elastic,ElasticDeadbandPrc,ElasticPower,WindowDays}`. *Probabilistic restoring force to fair value — bounds drift without hard-capping moves.* **Elastic + WindowDays 28 LIVE on prod 2026-07-22**: elastic soft-wall (wander free inside the deadband, superlinear pull past it = "overextension drifts back") + a 28-day weighted-average anchor window (warms over 28 real prod days). Pairs with the RegimeDrift→taker mover above.
- **Recent anchor — the damping lever** — mean-reversion tilt toward a recent price EWMA. `RecentAnchor:{Enabled,Strength,Scale,HalfLifeSec}`. *Fades fast excursions so price falls back from the cap instead of pinning — the primary >10%-move damper.*
- **Fundamental anchor** — the slow OU walk (per stock/currency) the value anchor pulls toward, hard-clamped to seed×[1±Band]. `Fundamental:{Enabled,Band,Theta,Sigma,DriftIntervalSeconds}`; per-stock sigma seeded. *A slowly-moving bounded target → long-horizon liveliness without runaway.*
- **Price band / caps** — hard veto on orders crossing the anchor by more than the cap; `GeometricBand` makes it log-symmetric (×F up / ÷F down); `CapFromSeed` pins the reference to the immutable seed. `ValueAnchor:{OverheatCap,AbsoluteCapMax,CapFromSeed}`, `GeometricBand`. *The runaway backstop; no order crosses the band in the overheated direction.*

### 2.6 Cash & drift control
- **Cash homeostasis** — restoring force on buyProb from each bot's cash fraction vs its seeded reserve band (smooth pull + hard edge forces). `CashHomeostasis:{Continuous,MaxShift,EdgeForceBuy,EdgeForceSell}`; bands seeded. *Keeps the fleet solvent + bounds cash-hoard down-drift.*
- **Dip-buy** — idle cash above the max reserve adds buyProb on dips ∝ depth×excess-cash. `DipBuyStrength`. *The demand side the net-long fleet lacks — cures down-drift without the anchor's spring-back.*
- **Cash injection** — periodic per-bot cash top-ups (seeded frequency/amount). `CashInjection:IntervalMinutes`; rest seeded. *Sustains buying power over long sessions.*
- **Bear-short** — appends a sentiment-scaled short-open bucket so flat bearish bots can sell; value-band vetoed. `BearShortStrength` (off in prod). *Sell-side firepower symmetric to the cash-abundant buy side.*

### 2.7 Exogenous flow — the price-MOVING channel (`ExogenousShockService`)
- **Exogenous shock / news** *(default-off; ENABLED on prod since 2026-07-16 to carry co-fire — `MeanIntervalMinutes` 0.5, `GlobalFraction` 0.4, `AnchorTracksShock` false)* — per-stock Poisson decaying value innovations; a chaser cohort trades INTO them with real marketable orders (`ChaserNotionalFrac` = impact dial; `ChaserStrength/Scale` retired). `ExogShock:{Enabled,MeanIntervalMinutes,DecayHalfLifeSec,Min/MaxMagnitude,MagnitudeExponent,Cap,AnchorTracksShock,ChaserNotionalFrac,ChaserFraction}`. *Directional TAKER volume (not a buyProb tilt) → moves ret_acf toward 0.*
- **Co-fire — the banked correlation lever** *(default-off in code; LIVE on prod since 2026-07-16 @ `GlobalCoFireNotionalFrac` 0.10 / `GlobalCoFireFraction` 0.15 — council-approved, reversible env override)* — on a market-wide pulse, a cohort fires ONE same-sign marketable order same-tick across hash-spread stocks. `ExogShock:{GlobalFraction,GlobalCoFire,GlobalCoFireFraction,GlobalCoFireNotionalFrac}`. *Simultaneous shared taker burst → ~doubles 5-10min cross-stock correlation (validated +0.037 factorR2@10min across 2 clean 2h A/Bs; shared sentiment is book-absorbed, shared flow isn't). Cost = deeper CORRELATED moves.*
- **Sector pulse** *(off)* — a `SectorFraction` of co-fire pulses scope to one sector (stockId % SectorCount); the cohort pushes only that sector. `ExogShock:{SectorCount,SectorFraction}`. *Intra-sector-high / cross-sector-low correlation (sector rotation). [Built 2026-07-05; 45m A/B = null signature at SF0.5 — sentiment swamps it.]*
- **Fat-tail jumps** *(off)* — a rare per-stock aggressor burst (dedicated account) walks the book to a target %, then aftershock nudges for clustering; never moves the fundamental. `Jumps:{Enabled,MeanIntervalHours,Min/MaxPct,MagnitudeExponent,MaxSlices,SlippagePct,AftershockBuckets,AftershockDecay,DriftGuardPct}`. *Fat 1-min return tails as a bounded tail event, not a level shift.*

### 2.8 FX & arbitrage
- **Arbitrage + FX house** — a small cohort keeps cross-listed USD/EUR books coupled (buy the cheap book / sell the dear, net-flat), rebalancing its currency mix through the FX desk; the conversion spread accrues to the house account, throttled by a wealth ceiling. `Arbitrage:{Enabled,ValueDrainCeilingPct,ConversionSkewBand,BatchLegs}`, `Platform:HouseUserId`; per-bot params seeded. *Tight cross-currency coupling; the spread funds the pure-profit house.*
- **FX walker** — AR(1) mean-reverting bounded walk for the EUR/USD mid, clamped to base×(1±RateBand); `ConvertSpread` = the arb-coupling floor + house revenue. `Fx:{Alpha,Amplitude,ConvertSpread,RateBand}` (read once at startup). *A realistic bounded FX rate, not a driftless walk.*

### 2.9 The `Tools/` seeding pipeline (`Tools/Config.py`, `Person.py`, `GenerateAIUsers.py`)
> **DOCUMENT-ONLY area (CLAUDE.md out-of-scope).** This describes the offline generator; do not edit `Tools/`. It runs ONCE per reseed to produce `AIUserData.xlsx`, which the server loads at DB-seed time. Symbol names below are the durable handle — the constants get retuned every reseed, so grep, don't trust a number here.

Every per-bot value that isn't a runtime `Bots:*` dial is **drawn once, offline, and frozen into an Excel workbook** — the fleet's DNA. Three Python files, one artifact:

| File | Role | Key symbols |
|---|---|---|
| `Config.py` | the **tunables + the stock/listing universe + invariant validator** — all constants, no per-bot logic | `STOCKS`, `SECTORS`, `CROSS_LISTED_STOCK_IDS`/`EUR_ONLY_STOCK_IDS`, `STRATEGY_WEIGHTS`, `ADVANCED_PROFILES`, `*_RANGE`/`*_BASE`/`*_SLOPE`, `_validate()` |
| `Person.py` | the **per-bot draw** — one `Person()` interpolates every geometry field from the `Config` constants against a single `aggressive∈[0,1]` axis (+ jitter/skew helpers) | `_trade_properties`/`_portfolio`/`_order_types`/`_trade_limits`/`_advanced_orders`/`_tiers`, `make_arbitrage`/`make_market_maker`/`make_rotator_bot`/`make_conviction_bot` |
| `GenerateAIUsers.py` | the **driver** — seeds RNG, writes the sheets, appends the special cohorts + reserved accounts in id order, mirrors the file to both `Resources/Raw` | `generate_aiuser_excel`, `NUM_PEOPLE`, `GENERATOR_SEED=42` |

**How one bot is drawn (`Person.__init__`).** A bot picks `aggressive` (right-skewed toward conservative, `AGG_SKEW`), then every other field interpolates off it: decision interval (aggressive → shorter), trade-prob, strategy (weighted by `STRATEGY_WEIGHTS`, ids 0–4 only — cohorts 5–8 never come from the general draw), log-distributed balance, cash-reserve band (seed cash% = band midpoint = the cash-homeostasis rest-point), a currency-gated watchlist (big-cap-biased `1/sid**α`; EUR-home bots only see EUR/cross-listed books), initial holdings sampled from that watchlist, order-type probs, buy-bias, per-bot **advanced-order probs** from the strategy's `ADVANCED_PROFILES` band, and the **limit tiers** (Close/Mid/Far) + protective-stop distance + take-profit band, ordering-enforced (`Close ≤ Mid ≤ Far`, `StopMax < FarMin`). Determinism is load-bearing: `GENERATOR_SEED=42` + a fixed draw order means the same reseed reproduces the identical fleet — which is why `_advanced_orders` still *consumes* the two bracket RNG draws even though brackets are seeded-ZERO (removes the value, keeps the stream aligned; §2.3/§4.3).

**Multipliers folded to 1.0.** The realism-breakthrough distance dial (`DecisionDistanceMult`, historically 0.32) is **baked directly into the tier constants** (`MAX_LIMIT_*`, `MID/FAR_LIMIT_*_RANGE`, `TP_OFFSET_*`) so the generated per-bot values ARE the production geometry — no runtime distance multiplier. *Flag (drift-prone, verify against `appsettings.json`): `Config.py`'s comments assert this folding is done, but the runtime `Bots:MarketProbMult`/`DecisionDistanceMult` dials still exist as composable knobs (§4.2, §7) — treat the SEED as the source of truth for geometry and confirm which dials are ×1.0 on the live box before trusting either.*

**Sheets → DB.** `generate_aiuser_excel` writes five sheets — **Stocks** (id/ticker/name/sector), **Listings** (per-`(stockId, currency)` row + a primary flag + seed price; this sheet is what derives the 70 live books, §4.1 in ENGINE_MECHANICS), **Identity** (login/email/admin), **Profile** (the ~40 per-bot columns above; column order must match `ExcelLayout.prepare_profile_sheet` because the server reads by position), **Holding** (cash + per-stock share float + a trailing dual-currency column). Cash-injection knobs are a **second pass** (inverse-size scaled off the population-median portfolio value, so small bots inject more). The workbook is saved to both `KieshStockExchange/Resources/Raw/AIUserData.xlsx` and the server copy — the server's embedded-seed path reads its copy at first DB seed and inserts one `User`+`Profile`+`Holding` per row.

**Special cohorts + reserved accounts (appended in strict id order).** After the `NUM_PEOPLE` random fleet come, sequentially: **admin** (Identity only, no Profile ⇒ not a bot), the **platform house** (`HOUSE_USER_ID_OFFSET`, dual-currency, no Profile), then the strategy cohorts each generated separately (`make_*`) so `STRATEGY_CHOICES` never yields their ids — **Arbitrage (5)**, **MarketMakerHouse (6)**, **Rotator (7)** (equal-VALUE holdings across all stocks), **Conviction (8)** (cash-heavy, REALLOCATED out of `NUM_PEOPLE` so the grand total stays 20k) — and finally the **jump aggressor** (no Profile). Id order is a hard invariant: the DB seeder auto-increments `UserId`, so a gapped id would make the DB id differ from the object's and crash on the "UserId is immutable" guard; the jump aggressor is appended LAST so enabling it shifts no existing id. The server's `Platform:HouseUserId` / `Bots:Jumps:AggressorUserId` must equal `NUM_PEOPLE + offset` for these to resolve.

*Runbooks for the reseed/re-anchor procedure (incl. `Tools/current_prices.csv` price injection) live in `docs/RESEED_RUNBOOK.md` / `RESEED_CHECKLIST.md`, not here.*

### 2.10 Fear/Greed index & mood-reflexive coupling (`MarketMoodService`)
> **Deliberate exception to the "keys, not values" rule:** this entry embeds hard weights/gains because the exact composite is load-bearing for reading the code. **Values as of 2026-07-16; they are under active tuning (e.g. a WSent 0.3→0.15 halve is queued) — trust `appsettings.json` over these numbers if they disagree.** Which of these ship enabled on the live box is a `docker-compose.prod.yml` env matter, not a mechanism fact; the *(base off; prod on)* tags below note the current live state but that is a snapshot.

One emotional axis, three horizon bands, exposed as a 0-100 gauge AND fed back as a bounded taker-flow lever. All keys under `Bots:Mood:*`; base appsettings ships every flag `false` (byte-identical off) — prod enables them through `docker-compose.prod.yml` env so each flip is reversible with no schema/rebuild. Relation to the rest of the stack: **sentiment (2.1) = the slow direction, activity (§2.11 composition/wick) = clustering, F&G = the global regime INTENSITY + correlation** — the same emotional axis at a third timescale, not a parallel system.
- **The gauge (composite MoodScore)** — per stock `mood = 50 + 50·tanh(WMom·momZ + WBreadth·(2b−1) − WVol·volZ + WFlow·flowZ + WSent·sentiment)`; direction `momZ` = TREND-vs-ANCHOR `ln(price/EMA(price,AnchorTau))` ÷ POOLED cross-sectional σ, winsorized ±3 (pooled, NOT own-σ: in this mean-reverting sim own-σ AND reversion-flow both point anti-trend, so a rising stock would misread as fear — the council fix). Global mood = the cross-stock mean; per-stock mood shows on each chart. `Mood:{Enabled,WMom,WBreadth,WVol,WFlow,WSent,AnchorTauSec,VolTauSec,VolBaselineTauSec,FlowTauSec,SmoothTauSec}` (shipped weights 1.35/0.5/0.3/0.2/0.3 = ×1.5 "Medium" sensitivity; SmoothTau EWMAs the reported score to damp whipsaw). *Trustworthy-as-OUTPUT (crash→fear→recover→~50, direction corr +0.6, bounded, smooth) before it's trusted-as-INPUT.*
- **Per-timeframe bands** — the same axis at three horizons: Fast (15s-5m = the top-level keys), Mid, Slow — each a bigger AnchorTau at lower sensitivity (`WeightMult<1`). `Mood:Bands:{Mid,Slow}:{AnchorTauSec,VolTauSec,VolBaselineTauSec,SmoothTauSec,WeightMult}`. *Fast jitters; Slow rates the multi-hour regime.*
- **Candle persistence** — flush stamps `Candle.{MarketMood,MoodMid,MoodSlow}` (nullable) when Enabled; `AggregateCandles` picks the displayed band by target bucket (≤5m Fast / ≤1h Mid / else Slow) and carries the two slow cols = zero client change. *Mood history rides the existing candle feed, not a live-only poll.*
- **Reflexive taker coupling (uniform)** *(base off; prod on)* — lagged (5-min EMA) GLOBAL mood scales each bot's taker share (INTENSITY only, never direction): `mult = clamp(1 + GainGreed·max(tilt,0) + GainFear·max(−tilt,0), 1−Cap, 1+Cap)`, `tilt=(laggedMood−50)/50` (asymmetric-V — greed and fear both raise activity). `Mood:{TakerCoupling,MoodTakerGainGreed,MoodTakerGainFear,MoodTakerCap,MoodEmaSeconds}` + runtime kill-switch `MarketMoodService.ReflexiveKillSwitch`. *Shared mood → shared taker flow = the correlation channel; bounded + lagged so it can't latch.*
- **Per-strategy reaction table** *(base off; prod on)* — replaces the uniform gains with a per-`AiStrategy` taker-intensity table: TrendFollower chases both ways (0.12/0.10), MeanReversion/Scalper chase (0.08/0.05), Conviction FADES greed (`Sign −1`, GainFear 0), Random 0.10/0.07; MM/Rotator/Arb/House exempt. `Mood:{PerStrategy,PerStrategyGains:{<Strategy>:{GainGreed,GainFear,Sign}}}`. *Different cohorts feel the regime differently instead of one uniform gain.*
- **MM fear-widen ("elevator down")** *(base off; prod on = Stage B)* — in fear the MM cohort widens its half-spread (×up to `MMWidenSpreadMax` 1.5) and shrinks quote size (×down to `MMWidenSizeMin` 0.6). `Mood:{MMWiden,MMWidenSpreadMax,MMWidenSizeMin}`. *Thin book in fear → drops cut deeper = the downside amplifier; the highest-value new liquidity channel.*
- **Conviction fear-bid ("the absorber")** *(base off; prod on)* — the FIRST directional mood term: Conviction bots add BUY conviction on panic, bounded fear-only + buy-only: `conviction += ConvictionFearBidGain·max(0,(50−moodLag)/50)`. `Mood:{ConvictionFearBid,ConvictionFearBidGain}`. *Smart money buys the panic → cushions the MMWiden elevator so fear stays controlled, not a spiral.*
- **Guardrails** — `Mood:JointTakerCapMult` (1.5) bounds the combined taker multiplier; a mood-latch telemetry line reports the fraction of time global mood is pegged <30/>70 (`DrainLatchFraction` — a persistent latch = the fear-spiral tell). *Soak + prod gate: latch=0 = the absorber holds; CK=0 always.*

*Prod rollout (council 2-stage): Stage A = gauge + persistence + per-strategy table + Conviction fear-bid; Stage B = +MMWiden, flipped only after a real prod fear-excursion recovered cleanly with latch=0 (the absorber proved on prod). Cross-stock correlation is a regime/duration effect judged on prod over days, not measurable in a 45m soak. Rollout history lives in the plan log.*

### 2.11 Activity field & order composition (Pillar B) (`BotActivityService`)
- **Self-exciting activity field** — a per-stock multiplier `A = G·S·B` (global × sentiment × self-exciting) that makes volume CLUSTER and breathe instead of running flat. `Activity:{Enabled,Baseline,GlobalTauSec,GlobalSigma,PerStockTauSec,PerStockSigma,Floor,SMax,Gamma,WNews,WMoveUp,WMoveDown,WSent,Theta,WSelf,Decay,BDriftAmp}`. *`Baseline<1` centers calm below 1 so quiet is genuinely quiet; `WMoveDown>WMoveUp` = the leverage effect (vol rises after drops); `SMax` is the hard backstop. This is the "**Pillar B / `G·B`**" the activity gate (§3.4 gate 5) and §6 self-excitation refer to. `WSelf=0` today — the fills→self-excite channel saturated the field at prod fill rates, so the rings + move/news/sentiment drivers carry the dynamics.*
- **Composition seam** — the field's PRICE-MOVING output: hot names upgrade limits→slippage TAKERS (prob `1−act^-k`), quiet names downgrade takers→limits, and limit tier bands stretch by `act^-k`; direction untouched, MM exempt. `Activity:Composition:{TakerExp,GExp,DistExpClose,DistExpMid,DistExpFar,Floor,Cap}`; a default-off SIZE lever `Composition:{SizeExp,SizeCap}` scales order notional by `act^SizeExp` (the volume-CV closer). *The cadence seam is scaler-absorbed (§3.5), so COMPOSITION — taker-share + tier distance + size — is the channel that actually moves volume/vol-clustering; all exponents default 0 = byte-identical.*
- **Filtered-tape wick rule** — fills below `Candles:HLMinFillSize` count toward volume/close/VWAP but do NOT set candle High/Low. `Candles:HLMinFillSize` (0 = off, byte-identical). *The SIP odd-lot / TradingView rule: tiny prints sweeping thin levels aren't representative of the accessible market, so real consolidated tapes exclude them from the official range — kills unrepresentative extreme wicks. RE-MEASURE after any reseed (fill-size distribution shifts); the CLIENT must set the same value in its `Resources/Raw/appsettings.json`.*

### 2.12 Bank estimate & rotation flow (`BankEstimateService`)
- **Bank fair-value estimate** — a "dominant house analyst" that periodically republishes a per-stock fair-value ESTIMATE (a fractional deviation from seed); the slow OU value-anchor (`FundamentalService`, §2.5) is pivoted to TRACK it instead of the raw seed. `BankEstimate:{Enabled,Alpha,PoissonMeanIntervalSec,WrongnessFraction,SectorCount,SeedAllOnStart,SectorDriftCap,SectorStepScale,SectorEventProb,SectorEventMult,SectorEventDownBias}`. *`Enabled` is the master gate (default off ⇒ anchor target stays the seed ⇒ byte-identical). `Alpha` weights zero-meaned sentiment vs the prior estimate; the irregular Poisson cadence + `WrongnessFraction` make the estimate SOMETIMES WRONG on purpose — the price-vs-estimate GAP is the tradeable feature the Rotator/Conviction cohorts chase. `SectorDriftCap`/`SectorStepScale` drive a per-sector shared re-rating walk (higher intra-sector correlation); `SectorEventProb>0` adds rare heavy-tailed down-biased sector re-rating events (fat tails + elevator-down + sector correlation in one lever) — the SUSTAINED sector→estimate→conviction-taker flow survives the book-refill wall that absorbs one-shot jumps.*
- **Value + momentum, one mechanism** — the estimate is the VALUE leg (anchor pivots to it) and the Rotator/Conviction cohorts (§5) are the MOMENTUM leg (taker flow INTO the gap). *Self-stabilizing: the gap opens on a re-rating, the taker flow closes it, then the cohort goes dormant until the next republish.*

---

## 3. THE MAIN TICK LOOP — how the fleet runs (`AiTradeService`)
The whole bot market is one **single-threaded loop**. `Services/BackgroundServices/AiTradeService.RunLoopAsync` (AiTradeService.cs:1296) runs `while (!ct.IsCancellationRequested)`, does a fixed slice of work, then `await Task.Delay(TradeInterval)`. Single-threaded is a hard invariant: all state — `AiBotContext`, the accounts cache, the sentiment/mood/activity/fundamental services — is mutated only here with **no locks**, so nothing may be reordered onto another thread. The only cross-thread readers are the dashboard (reads EWMAs / counters via `Volatile`/`Interlocked`) and `OnQuoteUpdated` (the market-data drain thread, which writes only the price caches — see §3.6).

### 3.1 Entry & lifecycle
- **Server-owned start** — `Services/HostedServices/BotLoopHostedService` (an `IHostedService`) starts the loop iff `Bots:AutoStart=true`; otherwise the loop stays dormant. Flip the flag + restart = the operational on/off. *The client used to own the loop; the server owns it now.*
- **`StartBotAsync`** (AiTradeService.cs:1190) — ensures stocks loaded, `SubscribeAllAsync(forUi:false)` on every currency (keeps `_quotes` populated + ref-counted without dispatching UI tick work), `ResetSessionState()`, then spawns `RunLoopAsync` on a `Task.Run`. Two linked CTS: `_schedulingCts` (normal drain) + `_engineCts` (hard-cancel in-flight engine work only after a drain-timeout, `Bots:GracefulStopMs` default 8000).
- **`ResetSessionState`** (AiTradeService.cs:1250) — zeroes session counters + **Resets every stateful service in dependency order** (sentiment, regime, activity, bank, funds, news, rotator, conviction, jump, priceMemory, fxDesk) so a Start replays deterministically. Arms all the `_next*Time` timer clocks.
- **`RunLoopAsync` prologue** (AiTradeService.cs:1296) — `_state.LoadAsync` hydrates the fleet into `_ctx.AiUsersByAiUserId`, applies the current `ActiveBotCap`, and warms the accounts cache for every bot userId + the house + (if enabled) the jump aggressor, so the first batch pays no cold-DB cost inside the book lock.

### 3.2 The per-tick phase pipeline
Each iteration of `RunLoopAsync` runs these phases **in order**, timestamping between each for the opt-in `BotPhase` profiling line (`Bots:PhaseTimingSeconds`>0). The order is load-bearing — external state advances first, the fleet reads it, then orders leave, then the cap-exempt cohorts, then maintenance:

| # | Phase | Call | What |
|---|---|---|---|
| 1 | **CheckTimers** | `CheckTimers` (:2213) | advance all external signal state + daily refresh (§3.3) |
| 2 | **Collect** | `CollectPendingOrdersAsync` (:1831) | walk the fleet, build this tick's `(plain, advanced)` decision lists (§3.4) |
| 3 | **Batch submit** | `SubmitAndApplyBatchAsync` (:1944) | one `PlaceAndMatchBatchAsync` for all plain orders → match → settle → apply to cache (§6) |
| 4 | **Advanced** | `SubmitAdvancedAsync` (:1465) | stop/trailing/short/bracket decisions via the entry/arm route, **outside** the matcher lock, ascending-AiUserId |
| 5 | **Arbitrage** | `_arbitrage.RunAsync` | `Bots:Arbitrage:Enabled` cohort pass (§5) |
| 6 | **Market-maker** | `_marketMaker.RunAsync` | `Bots:MarketMaker:Enabled` house-MM quoting pass |
| 7 | **Rotator** | `_rotator.RunAsync` | `Bots:Rotator:Enabled` estimate-gap rotation pass |
| 8 | **Conviction** | `_conviction.RunAsync` | `Bots:Conviction:Enabled` discretionary-taker pass |
| 9 | **Jumps** | `_jump.RunAsync` | `Bots:Jumps:Enabled` rare realized price-jump pass |
| 10 | **Bracket drain** | `_bracket.DrainAsync` | end-of-tick bracket coordinator queue (no-op unless `Bots:Advanced:BatchCoordinator`) |
| 11 | **RecordTickLatency** | `RecordTickLatency` (:2022) | fold this tick's elapsed µs into the EWMA the scaler reads |
| 12 | **Scaler** | `_scaler.OnTick(this)` | maybe move `ActiveBotCap` (§3.5) |
| 13 | **Reconcile** | `_auditor.AuditAsync` | every `ReconcileInterval` (5 min) — passive reservation-leak hunter, run AFTER RecordTickLatency so it never skews the EWMA |
| 14 | **Maintenance** | `RunPeriodicMaintenanceAsync` (:2264) | asset reload / prune / stats / economy / sentiment+probe logs / cash injection, each on its own timer (§3.7) |

Phases 5–10 are the **cap-exempt cohorts** — they run OUTSIDE the load scaler's actionable span (`RecordActionableLatency` measures only Collect+Batch, tCheck→tBatch) so enabling them can't make the scaler starve the main fleet. Steps 13–14 are deliberately **after** `RecordTickLatency` so their amortized spikes (an O(bots×stocks) economy walk, prune, asset reload) never crater the cap — a lesson baked in after those walks once spiked a tick to ~100% load. The whole tick body is wrapped in try/catch: a transient failure (e.g. a DB timeout) is logged and the loop continues after the delay, so one bad tick never kills the fleet.

### 3.3 `CheckTimers` — advancing the world (phase 1)
Advances external state **before** any bot reads it, in strict dependency order (AiTradeService.cs:2213): `_fxRates.Tick` → `_sentiment.Tick` → `_regime.Tick` → `_activity.Tick` (reads the sentiment shock above) → `_news.Tick` (decay+arrive shocks) → `_bank.Tick` (republish estimates after sentiment/news, before funds reads them) → `_funds.Tick` (advance the OU fundamentals, self-gated to its own interval) → `_priceMemory.Tick`. Then, when `_mood.Enabled`, the **Fear/Greed rescore** (§2.10): `Observe` all stocks (fold returns into the momentum EMAs) → for each of the 3 bands compute breadth + pooled-σ → `Score` all → `UpdateLaggedGlobal` (advance the 5-min-EMA global mood the reflexive taker lever reads). Taker flow is buffered during phase 3 (`RecordTakerFlow`) and drained here on the NEXT tick (1-tick lag, negligible). Finally the once-a-minute `_state.CheckDailyRefresh`. *Getting this order wrong = bots trade on last tick's world.*

### 3.4 `CollectPendingOrdersAsync` — who trades this tick (phase 2)
Stamps `_ctx.TickId`/`TickNowTicks`, clears the per-tick memoization caches (`ClearTickCaches` — Fundamental/SeedPrice/IsOverBand/mid-price/committed are memoized within one tick), warms the shared per-stock caches once (`PrecomputeSharedTickCaches`), then does ONE pass over `_ctx.AiUsersByAiUserId.Values`. Per bot, in this gate order (each `continue` skips the bot cheaply):
1. **Enabled + `CanPlaceMoreOrder`** — `IsEnabled`, `ErrorsToday<10` (a persistently-failing bot goes quiet for the day), and open-order count < `MaxOpenOrders×MaxOpenOrdersMult` (+ lean-reload armed-stop count).
2. **Cohort skip** — `Arbitrage / MarketMakerHouse / Rotator / Conviction` strategies `continue` here; they never touch the normal path (they run in phases 5–8). *Dead branches until those strategies are seeded ⇒ byte-identical when absent.*
3. **Burst** — ~0.2%/tick a bot enters a 2–8 min focused session (halves its decision interval, ×1.5 trade-prob).
4. **Quiet period** — a bot waits 10–60 s after its last trade (calmer bots wait longer, ∝ `1−AggressivenessPrc`).
5. **Activity gate** (§2.11 Pillar B) — `effectiveTradeProb`/`effectiveInterval` scaled by the activity field `G·B` (hot watchlist ⇒ trades more often); clamped so a hot name can't drive every-tick trading.
6. **Stagger** — `StaggerDue(aiUserId, tickId, slots)` (:1823): a bot in slot `aiUserId%slots` acts only on ticks where `tickId%slots` matches ⇒ each tick sees ~1/slots of the fleet. Pure hash, no RNG. `Bots:Staggering:Enabled`/`Bots:Staggering:Slots`; slots≤1 ⇒ always due. *The per-tick load-cut factor.*
7. **Decision interval** — skip if `now − LastDecisionTime < effectiveInterval`.
8. **Trade-prob draw** — `RecordDecision`, then skip if `Decimal01(aiUserId) > effectiveTradeProb`.
9. **Decide** — try `ComputeAdvancedDecisionAsync` first (only if `Bots:Advanced:Enabled` and under `Bots:Advanced:MaxPerTick`); if it returns an advanced decision, queue it and `continue`; else `ComputeOrderAsync` → queue any plain order. Bots decide in their **HomeCurrency only**.

Returns `(pending, advanced)`. The whole pass is a single serial sweep — the memoization + shared-cache prepass exist so a future parallel-collect is a pure-read region.

### 3.5 The load scaler / `ActiveBotCap` (phase 12)
`Services/BackgroundServices/Helpers/BotScalerService` is a clamped-proportional controller driven directly by `_scaler.OnTick(this)` each tick (no events, no re-entrancy lock). It samples `TickWorkMsEwma` (α=0.2, ~5-tick reaction) at most every `SampleInterval` (2 s), computes `loadFrac = tickWorkMs / TradeIntervalMs`, and moves `ActiveBotCap` within `[MinBotCap, MaxBotCap]` toward `TargetLoadFraction` (0.60), shrinking above `HighLoadFraction` (0.70) and growing below `LowLoadFraction` (0.50), bounded by `MaxDeltaFraction` (±25%/step) with a `CooldownAfterChange` (4 s) and `ConsecutiveSamples` hysteresis. `_state.ApplyActiveBotCap` then sets `IsEnabled` on exactly that many bots. *This is why "online bots" moves at runtime — the box self-tunes how many bots it can carry at the target tick budget.* Several §B levers (denominator correction, actionable-span sizing, tick-multiple recenter) are default-off and byte-identical; they were the shorter-tick/fleet-split perf work.

### 3.6 `OnQuoteUpdated` — the only off-loop writer
Subscribed to `IMarketDataService.QuoteUpdated`, runs on the market-data drain thread (AiTradeService.cs:2090). Writes only the price caches on `_ctx`: `StockPrices`, `PreviousPrices`, and the **SmoothedPrices** EWMA (legacy fixed α=0.15 per-quote, or a time-based half-life when `Bots:SmoothedPriceHalfLifeSec>0`). Bots read the *smoothed* price so they don't counter-trade their own ~1-min impact. `ConcurrentDictionary`, RNG-free, per-key ⇒ no coordination with the loop needed.

### 3.7 Timers & cadences
All scheduling is `_next*Time` fields compared against `now`; the work lives in the relevant helper. `TradeInterval` = `Bots:TradeIntervalMs` (default 0 ⇒ 1 s). `RunPeriodicMaintenanceAsync` fires: asset reload (`ReloadAssetsInterval` 1 min), prune worst orders (`PruneInterval` 30 s), stats log (60 s), economy snapshot (`Bots:EconomyLogIntervalSeconds`), sentiment + news + probe logs (`Bots:SentimentLogIntervalSeconds`), cash injection (`Bots:CashInjection:IntervalMinutes`). The probe log-lines (CHASER/MM/JUMP/REFILL/ACTCOMP/ARMEDCAP/IMPACTHOLD/MOOD) are the **liveliness kill-checks** — grep the soak log (server console / `logs/`) for the tag in the first minute: a default-off lever that stays inert must show `fired=0`/`eligible=0` or it's mis-wired; a lever you just enabled that STILL shows `fired=0` is the "on but market unchanged" symptom = a wiring bug, not a tuning miss.

---

## 4. THE PER-BOT DECISION PATH (`AiBotDecisionService.ComputeOrderAsync`)
Stateless order computation: given `(ctx, user, currency)` produce one `Order` or `null` (AiBotDecisionService.cs:737). The mechanism catalog for each *tilt* is §2; this is the **assembly order + control flow**.

1. **Co-fire branch** (§2.7) — resolved FIRST, above any RNG draw. On the tick a market-wide pulse fires, a hash-selected co-fire cohort member emits ONE same-sign marketable order on a hash-spread watchlist stock (`CoFireSelect`, notional-capped by `GlobalCoFireNotionalFrac × seedPV`). Short-circuits on `GlobalCoFireNotionalFrac==0` before any read ⇒ OFF is byte-identical.
2. **Chaser branch** (§2.7) — else, if a news shock is live and this bot is a hash-selected chaser due this cadence, emit a marketable order INTO the shock (`ChaseSelect`, `ChaseNotionalCap`). A chase is a **complete draw-free substitution** of the normal decision (0 RNG) — takes precedence, resolved above `ChooseOrderType`, so OFF leaves the normal draw stream untouched.
3. **Order type** — `type = ChooseOrderType(ctx, user, currency)` (:1375) for non-chasers. This is where **buyProb** is built (see §4.1). MM-strategy bots short-circuit to `ChooseMarketMakerQuote` (two-sided resting limit, no direction).
4. **Stock pick** — `ChooseStockId(ctx, user, type, currency, committed)` (:1765); `committed` = one snapshot of already-committed totals reused across sell candidates. `≤0` ⇒ no order.
5. **Size, price tier, veto** — trade size (fat-tail power draw + rare block trade), limit tier (Close/Mid/Far seeded bands × `DecisionDistanceMult` × composition seam), and the hard **price-band veto** (`IsOverBand` — no order may cross the anchor by more than the cap; §2.5).

### 4.1 buyProb assembly (`ChooseOrderType`)
A directional probability in [0,1], summed from independent terms (every §v2 lever off ⇒ collapses byte-for-byte to the original additive line):
- **Homeostatic base** — `BuyBiasPrc` + cash-reserve restoring shift (`CashHomeostasis`, §2.6): keeps the fleet solvent. Kept for every bot, never damped.
- **Directional** — momentum + sentiment. Either the **SentimentDynamics slope model** (`DirectionalBias`, §2.2: per-strategy response to the two-timescale sentiment slope) when `Bots:SentimentDynamics:Enabled`, or the legacy level-only momentum+sentiment terms. Plus the optional TrendFollower chartist tilt (§2.3), reaction-lag/hold/perceived-desync smearing, `RoleSplit` noise-damp, and `Herding` tilt (§2.4).
- **Anchors** (§2.5) — value-anchor tilt toward `Fundamental()` (linear or elastic), medium-term RecentAnchor tilt, optional per-bot lag. Anchors are additive structural overrides — never damped by the role split.
- **Combine** — `BuyProbHybrid` (additive by default; multiplicative-around-0.5 when `Bots:DirectionalPressure:Multiplicative`, to preserve diversity at extremes), then the **dip-buy** cash-deployment tilt (§2.6).

### 4.2 Taker vs limit (`effectiveUseMarket`)
Separate from *direction*: how often the order **crosses the spread**. The asymmetry (§0): a taker consumes standing depth and executes at successively deeper levels → the mid MOVES; a resting limit adds depth at one level and is swallowed by the opposing book / MM refill → the mid does NOT move. (The anchor is a separate, upstream damper on the sentiment tilt — it is NOT what absorbs the limit; the book is.) Base = `UseMarketProb × Bots:MarketProbMult`, then:
- **Aggression boost** — Scalper/TrendFollower add `AggressionBoost × |directional|` (SentimentDynamics) so conviction takes liquidity symmetrically for buys and sells (no taker skew ⇒ no down-drift). MM subtracts 0.15.
- **Reflexive mood coupling** (§2.10) — the lagged GLOBAL mood scales taker share, **intensity only, never direction**. Per-strategy table (`Bots:Mood:PerStrategy`: TrendFollower chases both ways, Conviction fades greed, etc.) supersedes the uniform `Bots:Mood:TakerCoupling` gains; both bounded by `JointTakerCapMult` and the runtime `ReflexiveKillSwitch`. Shared mood ⇒ shared taker flow ⇒ the cross-stock-correlation channel.
- **Reaction-persistence taker override** (§2.4) — when `Bots:Imbalance:ReactionPersistence` is on, a per-bot AR(1) pressure that crosses the spread when conviction is high.

### 4.3 Advanced path (`ComputeAdvancedDecisionAsync`)
Tried before the plain path when `Bots:Advanced:Enabled`. Produces a `BotAdvancedDecision` (`enum BotAdvancedKind`: StopMarketSell, TrailingStopSell, ShortOpen, LongBracket, ShortBracket, StopMarketBuy). Per-kind probabilities are **per-bot, seeded by strategy** (`Tools/Person.py` `ADVANCED_PROFILES`). `BuyStopFraction` routes a fraction of protective triggers to buy-stops (up-trigger) so protective flow is symmetric (the long-only sell-stops were the entire down-drift). Brackets are seeded-ZERO in prod (removed: SL-cascade + throughput). Returning null with zero RNG consumed keeps the plain-order stream byte-identical when off.

---

## 5. STRATEGY COHORTS (`enum AiStrategy`, AIUser.cs:15)
`{ MarketMaker=0, TrendFollower=1, MeanReversion=2, Random=3, Scalper=4, Arbitrage=5, MarketMakerHouse=6, Rotator=7, Conviction=8 }`. Strategy is **seeded per bot** (`Tools/Config.py:STRATEGY_WEIGHTS`). Two groups:

**In the normal path** (phases 1–4, decided by `AiBotDecisionService`):
- **MarketMaker (0)** — posts two-sided resting limits at a half-spread on the thinner book side (`ChooseMarketMakerQuote`); provides liquidity, not direction.
- **TrendFollower (1)** — chases momentum (`directional += chase`); a momentum amplifier.
- **MeanReversion (2)** — fades momentum; a damper.
- **Random (3)** — no directional momentum term; homeostatic + noise only.
- **Scalper (4)** — fast-slope conviction + extra taker aggression.

**Out of the normal path** (dedicated `RunAsync` passes, phases 5–8; each is master-gated, skipped in Collect, CK-safe because its legs are ordinary engine orders):
- **Arbitrage (5)** — `ArbitrageDecisionService`: keeps a cross-listed stock's USD/EUR books coupled at the live FX rate (buy the cheap book / sell the dear, net-flat), rebalances its currency mix through the FX desk → funds the house account. `Bots:Arbitrage:*`.
- **MarketMakerHouse (6)** — `MarketMakerDecisionService`: a separately-seeded house cohort maintaining continuous two-sided resting quotes around a reference that survives a one-sided book; supplies asks into up-shocks + shrinks the bounce; widens in fear (§2.10 MMWiden). Tracks its own order ids privately (prune-immune, invisible to the open-order cap). `Bots:MarketMaker:*`.
- **Rotator (7)** — `RotatorDecisionService`: ranks the board by the price-vs-bank-estimate gap (§2.12), buys one favoured / sells one disfavoured name via aggressive market orders (rank-weighted lottery ⇒ dispersed "sector rotation" flow). Turnover-bounded + scaler-coupled (`PF×(1−load)`) so it can't cash-bomb or freeze the loop. It is a **~100%-win mechanical rebalancer** — acceptable *because* it's turnover-bounded (small notional per fire) and books no house edge; it's a liquidity/flow shaper, not a P&L engine (that role is the Conviction cohort, which takes real risk). `Bots:Rotator:*`.
- **Conviction (8)** — `ConvictionDecisionService`: REALISTIC discretionary cash-heavy traders who occasionally rotate into "good plays" on sentiment/sector-momentum, carry per-bot personality (chaser/fader, risk, patience), take REAL directional risk (win-rate ≪ 100%). Every acted order is an aggressive directional TAKER (a limit would be absorbed). Cash-floor + `RiskAppetite≤0.25` hard-clamp + one bet per fire + scaler-coupling = stable. Adds the mood fear-bid absorber (§2.10). `Bots:Conviction:*`.

All cohorts iterate **ascending AiUserId, consume no RNG** (pure hashes of aiUserId + a monotonic pass counter), and do two sequential BATCH passes per currency book (SELLS settle first → BUYS sized from the fresh post-sell `AvailableBalance`) ⇒ Σ buys ≤ available cash ⇒ CK-safe.

---

## 6. ORDER HANDOFF → ENGINE → FEEDBACK
- **Plain batch** (phase 3) — `SubmitAndApplyBatchAsync` (:1944) hands the tick's whole plain-order list to `IOrderExecutionService.PlaceAndMatchBatchAsync` (one batched reserve→match→settle group-tx; the engine flow is `OrderEntryService → OrderExecutionService → MatchingEngine → SettlementEngine`, see `docs/explainers/ENGINE_MECHANICS.md`). Per result: on success bump `_tradesPlacedThisSession`, `_stats.RecordPlacement`, and on any fills → `_stats.AddVolume`, `_activity.RecordFill` (Pillar-B self-excitation), `_mood.RecordTakerFlow` (buffered for next tick's F&G), and `_state.ApplyResultToCache` (fold fills into the in-memory accounts/positions/open-orders cache — no DB re-read). Failures are recorded to `BotFailureTracker` (category + per-stock counters, dashboard-visible).
- **Advanced route** (phase 4) — `SubmitAdvancedAsync` (:1465) goes through `IOrderEntryService` (stop/trailing/short/bracket entry+arm), NOT the batch matcher, sequentially in ascending AiUserId, each call owning its own book→fund→position gates while the loop holds none. `Bots:Advanced:BatchArms`/`BatchBuyStops`/`BatchShortOpens`/`BracketBatch` partition the pure-arm kinds into single batched entry calls; the rest stay per-order. `Bots:StopReplaceOld` cancels a bot's prior (stock,side) standalone stop before arming a new one (MOVE not STACK).
- **Feedback loops** — (a) **scaler**: `RecordTickLatency` → EWMA → `BotScalerService` → `ActiveBotCap` (§3.5). (b) **economy telemetry**: `BotEconomyTelemetry.LogSnapshot` (maintenance) walks aggregate bot wealth + per-strategy Δ-vs-seed win-rate (`BotStratPerf`) and drives the arbitrage value-drain throttle (`Bots:Arbitrage:ValueDrainCeilingPct`). (c) **state**: `AiBotStateService` owns load/daily-refresh/asset-refresh/prune/cache-apply; `ApplyResultToCache` is the hot one — every fill mutates the cache in place so the next tick's decisions read fresh holdings without a DB round-trip. **CK=0** (ConservationProbe) + reservation reconcile (`ReservationAuditor`, phase 13) are the invariant gates — every cohort routes through the same engine so they're covered uniformly.

### 6.1 Observability — probes & telemetry (a pointer, not a catalog)
How you tell a soak/prod run is HEALTHY and a lever is actually FIRING. Two families, different jobs; grep the class name (line numbers rot). This is a map, not an exhaustive list — each probe's own source is authoritative.

- **The conservation gate — `ConservationProbe`** (`Settlement/ConservationProbe.cs`) — the one HARD invariant. Post-apply, pre-commit, it sums each settled batch and `LogError`s `"Money probe:"` if net `Σ ΔTotalBalance ≠ 0` per currency or `"Shares probe:"` if net `Σ ΔQuantity ≠ 0` per stock (a missing pre-state snapshot is also an error). "CK=0" operationally means **that error line never appears** — a single hit fails the soak gate. The engine-side story is `docs/explainers/ENGINE_MECHANICS.md §5`; here it's the fleet's health backstop.
- **The liveliness kill-checks — the `*Probe` family** (`BackgroundServices/Helpers/`): `ChaserProbe`, `MarketMakerProbe`, `JumpsProbe`, `RefillThrottleProbe`, `ArmedStopCapProbe`, `ImpactHoldProbe`, `ActivityCompositionProbe`, plus `BotDecisionProbe` and the engine-side `MatchSymmetryProbe`. Each emits a periodic maintenance log-line tagged in ALL-CAPS (CHASER / MM / JUMP / REFILL / ARMEDCAP / IMPACTHOLD / ACTCOMP / MOOD; §3.7) carrying `eligible`/`fired`/`skipped`-style counters. The assertion pattern (§3.7): a **default-off** lever must show `fired=0`/`eligible=0` (proof it draws 0 and stays byte-identical); a lever you just **enabled** that STILL shows `fired=0` is the "on but market unchanged" symptom = a wiring bug, not a tuning miss. Grep the tag in the first minute of a soak log.
- **The economy telemetry — `BotEconomyTelemetry`** — not a gate but the P&L/wealth lens: `LogSnapshot` emits aggregate fleet wealth, the per-strategy `BotStratPerf` win-rate/Δ-vs-seed line (the bot-dashboard breakdown reads this), and the arb/house value-drain fraction that arms `ArbThrottleEngaged` (`Bots:Arbitrage:ValueDrainCeilingPct`).
- **The reservation reconciler — `ReservationAuditor`** (phase 13) — a passive leak hunter, not a conservation check; its mismatch warnings are benign self-healing rounding (warn-gated by `Bots:ReservationPhantomWarnThreshold`). Watch `ConservationProbe` for real breaks, not this.

---

## 7. LOOP / INFRA CONFIG KEYS (the `Bots:*` keys §2 doesn't cover)
§2 documents the *mechanism* keys; these are the *loop plumbing* keys, all under `Bots:*`:

| Key | Tunes |
|---|---|
| `AutoStart` | server owns the loop (false ⇒ dormant) |
| `TradeIntervalMs` | tick period (0 ⇒ 1 s); prod may run shorter (e.g. 250 ms) so staggered fills land per render frame |
| `Staggering:Enabled`, `Staggering:Slots` | per-tick fleet split = load-cut factor N (each tick sees ~1/N of bots) |
| `GracefulStopMs` | drain grace before hard-cancelling in-flight engine work (default 8000) |
| `CashInjection:IntervalMinutes` | cash top-up cadence (nominal-growth driver) |
| `EconomyLogIntervalSeconds`, `SentimentLogIntervalSeconds` | telemetry snapshot cadences (thin prod / densify soak exports) |
| `PhaseTimingSeconds` | opt-in `BotPhase` per-phase µs breakdown + commits/sec + trades/sec (>0 also arms engine commit counting) |
| `Advanced:Enabled`, `Advanced:MaxPerTick` | master switch + per-tick cap on entry-route submissions |
| `Advanced:BatchArms`/`BatchBuyStops`/`BatchShortOpens`/`BracketBatch`/`BatchCoordinator` | batch the respective advanced routes (default off) |
| `StopReplaceOld` | move a bot's prior standalone stop instead of stacking |
| `MarketProbMult`, `DecisionDistanceMult` | global taker-share + order-distance dials over the seeded per-bot values |
| `Arbitrage:Enabled`, `MarketMaker:Enabled`, `Rotator:Enabled`, `Conviction:Enabled`, `Jumps:Enabled`, `ExogShock:Enabled` | cohort / channel master gates (all default off; a single bool check when off) |
| `ReconcileClamp`, `ReservationPhantomWarnThreshold` | reservation-auditor behaviour |
| `SmoothedPriceHalfLifeSec` | perceived-price lag half-life (0 ⇒ legacy α=0.15/quote) |

The scaler tunables (`HighLoadFraction`, `TargetLoadFraction`, `MinBotCap`, `MaxBotCap`, etc.) live on `BotScalerService`/`AiTradeService` and are dashboard-bound/admin-settable, not `Bots:*` appsettings keys. Per-bot geometry (aggressiveness, decision interval, strategy, tiers, stop distances, advanced probs, cash bands) is **SEEDED** (`Tools/`, §2.9), not in appsettings — the Excel is the base geometry, composed with the runtime dials above (§2.9).

---

## 8. INVARIANTS — never break these
The load-bearing rules the rest of the doc assumes. Break one and something subtle rots.

| Invariant | Why it holds | What breaks if violated | Verified by |
|---|---|---|---|
| **Single-threaded loop, no locks** | all `AiBotContext` / cache / signal-service state is mutated only in `RunLoopAsync` (§3) | data races, torn reads, silent corruption | code structure; only off-loop writer is `OnQuoteUpdated` (price caches only, §3.6) |
| **CK = 0 (conservation)** | every cohort routes through the same reserve→match→settle engine (§6) | money/shares created or destroyed | `ConservationProbe` (soak + prod gate) |
| **Default-off = byte-identical** | a disabled lever adds exactly 0 and draws 0 RNG (§0/§2/§4.3) | default runs stop being reproducible; A/B baselines drift | soak diff of default run before/after the lever landed |
| **Phase order is load-bearing** | world advances (CheckTimers) → fleet reads it → orders leave → cohorts → maintenance (§3.2) | bots trade on last tick's world; scaler starves the fleet | §3.2/§3.3 ordering; maintenance runs AFTER `RecordTickLatency` |
| **Cohorts consume no RNG, ascending AiUserId, SELLS-before-BUYS** | pure hashes + two sequential batch passes per book (§5) | Σ buys > available cash → CK break; non-determinism | `ReservationAuditor` (phase 13) + `ConservationProbe` |
| **Bots read the SMOOTHED price** | `OnQuoteUpdated` EWMA (§3.6) so a bot doesn't counter-trade its own 1-min impact | over-mean-reversion (ret_acf → −0.5), fake liquidity | `SmoothedPrices` cache; ret_acf gate (§1) |

---

## Related docs
- `docs/explainers/ENGINE_MECHANICS.md` — the order → match → settle engine (§6 hands off to it).
- `docs/REALISM_OVERHAUL_PLAN.md` — the decision history / arc §2 defers to (why levers exist, what was tried).
- `docs/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md` — §2.12 / §5 Rotator + Conviction design.
- `docs/SHIP_RUNBOOK.md`, `docs/RESEED_RUNBOOK.md`, `docs/RESEED_CHECKLIST.md` — deploy + reseed procedure (§2.9 seed folding).
- `Tools/Config.py`, `Tools/Person.py` — the per-bot seed (§2.9 source of truth).
