# BOT_MECHANICS.md — how the KieshStockExchange bots work + the target behavior

Compact reference for the bot-trading systems and the market-behavior targets. **Consult + UPDATE this file whenever a bot mechanism changes** (same commit).
Config VALUES live in `appsettings.json` (`Bots:*`) and the seed `Tools/Config.py`; §2 references the config KEYS, not hard values. (Decision history and config snapshots live in the plan log, not here.)

---

## 1. TARGET VALUES — the scorecard the market should hit
**Realism** = real-market norm · **Kiesh** = the given target · **P** = priority 1 (high) – 5 (low). *[Priorities provisional — pending final lock.]*

| Group | Metric | Realism | Kiesh target | P |
|---|---|---|---|---|
| **Movement** | typical intraday move | ±1–3% most days | most ±5% | 2 |
| | active / best movers (daily) | 5–10% | 5–10% | 1 |
| | >10% moves | news-driven, rare | NEWS-ONLY, rare | 1 |
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
| | **per-stock liveness** (no empty candles) | continuous | **every stock traded ≥1× per 15 s** (very rare empties) — raise the ACTIVATION amount (active bots/tick), NOT sparse activation, NOT fewer pairs (70 is fixed); calibrate on PROD | **2** |
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

**Grade it (soak gate-set):** ret_acf(VWAP) −0.5…−0.1 · kurtosis ≥ 4 · median excursion 3–8% · p95 10–20% · max 15–35% · CK = 0 · taker 20–50% ·
spread < 0.5% · |return|-autocorr > 0.05 · cross-sectional dispersion > 0.002 · pairwise corr 0–0.25.
**Eyeball / long-soak only** (unfalsifiable in a 45m soak): big-news frequency, weekly drift, daily skew, leverage effect, aggregational Gaussianity,
multi-day trend, sector rotation, momentum. **Out-of-scope for a 24/7 sim:** day-of-week, intraday U-shape, auctions, implied vol.
*(The bid-ask bounce is a mechanical source of negative 1-min ret_acf — not a bug.)*

---

## 2. SYSTEMS — mechanism reference
Each entry: **what** · `config keys` (under `Bots:*` unless noted) · *why*. Values + decision history live in `appsettings.json` / the plan log, not here.
Levers tagged *(off)* are default-off + byte-identical (add exactly 0 when disabled). Per-bot geometry is SEEDED (see 2.9), not in appsettings.

### 2.1 Sentiment — the mood signal each bot reads (`BotSentimentService`)
- **OU sentiment rings** — every stock sums a stack of mean-reverting AR(1) rings across ~5 per-stock timescales (seconds→hours) plus a shared 3-timescale global/common-mode ring. `Sentiment:PerStockSigmaMult`, `Sentiment:GlobalSigmaMult` (amplitude scalers). *Fast rings → per-stock dispersion; the slow global ring → a shared regime. Corr ≈ shared²/(shared²+idio²), so GlobalSigmaMult↑ / PerStockSigmaMult↓ raises cross-stock correlation.*
- **RegimeDrift ("System A")** — a per-stock BOUNDED random walk (not mean-reverting), cubic soft-walled near its cap. `Sentiment:RegimeDrift:{Enabled,StepSigma,Cap,SoftWallK,Strength}`. *Gives each stock an independent minutes-long wander; `Strength` is the idiosyncratic denominator of the correlation ratio.*
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
- **Market-maker quoting** — strategy-0 MM bots post two-sided resting quotes on the thinner book side at a half-spread. `MarketMakerQuoting`, `QuoteHalfSpreadPrc`. (A dedicated MM-house cohort `MarketMaker:Enabled` exists but is off/unseeded.) *Two-sided resting liquidity → tight spreads + depth for sweeps.*

### 2.4 Flow persistence — beats the LLN cancellation of 20k independent bots
- **Herding / Inertia** — Inertia locks a bot's buy/sell side for a seeded multi-minute hold (no re-draws during the hold); Herding gives a follower fraction a common regime tilt. `Imbalance:{Inertia,Inertia:MinSec,Inertia:MaxSec,Inertia:Leak,Herding,Herding:FollowerFraction,Herding:Tilt}`. *Persistent stances create genuine order-flow imbalance instead of averaging to zero.*
- **Reaction-persistence split** *(off — supersedes Inertia)* — a fast conviction signal feeds a per-bot AR(1) "pressure" with a seeded half-life; a taker override crosses the spread when pressure is high. `Imbalance:ReactionPersistence:{*,PersistMinSec,PersistMaxSec,WLocal,WShared,TakerCoupling}`. *Fast reaction (no lockstep latency) + separately-decaying conviction that STICKS as taker flow.*

### 2.5 Anchors & caps — bound + revert price (`FundamentalService` + anchor tilts)
- **Value anchor** — tilts buyProb toward the stock's fundamental target ∝ deviation/scale, capped; optional elastic (deadband + superlinear) variant. `ValueAnchor:{Strength,Scale,Elastic,ElasticDeadbandPrc,ElasticPower}`. *Probabilistic restoring force to fair value — bounds drift without hard-capping moves.*
- **Recent anchor — the damping lever** — mean-reversion tilt toward a recent price EWMA. `RecentAnchor:{Enabled,Strength,Scale,HalfLifeSec}`. *Fades fast excursions so price falls back from the cap instead of pinning — the primary >10%-move damper.*
- **Fundamental anchor** — the slow OU walk (per stock/currency) the value anchor pulls toward, hard-clamped to seed×[1±Band]. `Fundamental:{Enabled,Band,Theta,Sigma,DriftIntervalSeconds}`; per-stock sigma seeded. *A slowly-moving bounded target → long-horizon liveliness without runaway.*
- **Price band / caps** — hard veto on orders crossing the anchor by more than the cap; `GeometricBand` makes it log-symmetric (×F up / ÷F down); `CapFromSeed` pins the reference to the immutable seed. `ValueAnchor:{OverheatCap,AbsoluteCapMax,CapFromSeed}`, `GeometricBand`. *The runaway backstop; no order crosses the band in the overheated direction.*

### 2.6 Cash & drift control
- **Cash homeostasis** — restoring force on buyProb from each bot's cash fraction vs its seeded reserve band (smooth pull + hard edge forces). `CashHomeostasis:{Continuous,MaxShift,EdgeForceBuy,EdgeForceSell}`; bands seeded. *Keeps the fleet solvent + bounds cash-hoard down-drift.*
- **Dip-buy** — idle cash above the max reserve adds buyProb on dips ∝ depth×excess-cash. `DipBuyStrength`. *The demand side the net-long fleet lacks — cures down-drift without the anchor's spring-back.*
- **Cash injection** — periodic per-bot cash top-ups (seeded frequency/amount). `CashInjection:IntervalMinutes`; rest seeded. *Sustains buying power over long sessions.*
- **Bear-short** — appends a sentiment-scaled short-open bucket so flat bearish bots can sell; value-band vetoed. `BearShortStrength` (off in prod). *Sell-side firepower symmetric to the cash-abundant buy side.*

### 2.7 Exogenous flow — the price-MOVING channel (`ExogenousShockService`)
- **Exogenous shock / news** *(off)* — per-stock Poisson decaying value innovations; a chaser cohort trades INTO them with real marketable orders (`ChaserNotionalFrac` = impact dial; `ChaserStrength/Scale` retired). `ExogShock:{Enabled,MeanIntervalMinutes,DecayHalfLifeSec,Min/MaxMagnitude,MagnitudeExponent,Cap,AnchorTracksShock,ChaserNotionalFrac,ChaserFraction}`. *Directional TAKER volume (not a buyProb tilt) → moves ret_acf toward 0.*
- **Co-fire — the banked correlation lever** *(off)* — on a market-wide pulse, a cohort fires ONE same-sign marketable order same-tick across hash-spread stocks. `ExogShock:{GlobalFraction,GlobalCoFire,GlobalCoFireFraction,GlobalCoFireNotionalFrac}`. *Simultaneous shared taker burst → 5-10min cross-stock correlation (shared sentiment is book-absorbed; shared flow isn't).*
- **Sector pulse** *(off)* — a `SectorFraction` of co-fire pulses scope to one sector (stockId % SectorCount); the cohort pushes only that sector. `ExogShock:{SectorCount,SectorFraction}`. *Intra-sector-high / cross-sector-low correlation (sector rotation). [Built 2026-07-05; 45m A/B = null signature at SF0.5 — sentiment swamps it.]*
- **Fat-tail jumps** *(off)* — a rare per-stock aggressor burst (dedicated account) walks the book to a target %, then aftershock nudges for clustering; never moves the fundamental. `Jumps:{Enabled,MeanIntervalHours,Min/MaxPct,MagnitudeExponent,MaxSlices,SlippagePct,AftershockBuckets,AftershockDecay,DriftGuardPct}`. *Fat 1-min return tails as a bounded tail event, not a level shift.*

### 2.8 FX & arbitrage
- **Arbitrage + FX house** — a small cohort keeps cross-listed USD/EUR books coupled (buy the cheap book / sell the dear, net-flat), rebalancing its currency mix through the FX desk; the conversion spread accrues to the house account, throttled by a wealth ceiling. `Arbitrage:{Enabled,ValueDrainCeilingPct,ConversionSkewBand,BatchLegs}`, `Platform:HouseUserId`; per-bot params seeded. *Tight cross-currency coupling; the spread funds the pure-profit house.*
- **FX walker** — AR(1) mean-reverting bounded walk for the EUR/USD mid, clamped to base×(1±RateBand); `ConvertSpread` = the arb-coupling floor + house revenue. `Fx:{Alpha,Amplitude,ConvertSpread,RateBand}` (read once at startup). *A realistic bounded FX rate, not a driftless walk.*

### 2.9 Seeded population (`Tools/`)
- **Per-bot seeded params** — all per-bot geometry (aggressiveness, decision interval, strategy, lateness/FOMO tail, cash-reserve band, buy-bias, limit tiers, stop distances, advanced-order probs, cash-injection freq/amount) is drawn once by `Tools/Person.py` from `Tools/Config.py` constants into `AIUserData.xlsx`; runtime is nominally ×1.0 so the Excel IS the production geometry. Composing runtime dials: `DecisionDistanceMult`, `MarketProbMult`, `Liquidity:OffsetMult`. *Source of truth = the seed, not appsettings; the pending reseed folds the remaining runtime mults into the Excel (dials→1.0).*

### 2.10 Fear/Greed index & mood-reflexive coupling (`MarketMoodService`) — LIVE on prod (enabled via env)
One emotional axis, three horizon bands, exposed as a 0-100 gauge AND fed back as a bounded taker-flow lever. All keys under `Bots:Mood:*`; base appsettings ships every flag `false` (byte-identical off) — prod enables them through `docker-compose.prod.yml` env so each flip is reversible with no schema/rebuild. Relation to the rest of the stack: **sentiment (2.1) = the slow direction, activity (composition, 2.3/wick) = clustering, F&G = the global regime INTENSITY + correlation** — the same emotional axis at a third timescale, not a parallel system.
- **The gauge (composite MoodScore)** — per stock `mood = 50 + 50·tanh(WMom·momZ + WBreadth·(2b−1) − WVol·volZ + WFlow·flowZ + WSent·sentiment)`; direction `momZ` = TREND-vs-ANCHOR `ln(price/EMA(price,AnchorTau))` ÷ POOLED cross-sectional σ, winsorized ±3 (pooled, NOT own-σ: in this mean-reverting sim own-σ AND reversion-flow both point anti-trend, so a rising stock would misread as fear — the council fix). Global mood = the cross-stock mean; per-stock mood shows on each chart. `Mood:{Enabled,WMom,WBreadth,WVol,WFlow,WSent,AnchorTauSec,VolTauSec,VolBaselineTauSec,FlowTauSec,SmoothTauSec}` (shipped weights 1.35/0.5/0.3/0.2/0.3 = ×1.5 "Medium" sensitivity; SmoothTau EWMAs the reported score to damp whipsaw). *Trustworthy-as-OUTPUT (crash→fear→recover→~50, direction corr +0.6, bounded, smooth) before it's trusted-as-INPUT.*
- **Per-timeframe bands** — the same axis at three horizons: Fast (15s-5m = the top-level keys), Mid, Slow — each a bigger AnchorTau at lower sensitivity (`WeightMult<1`). `Mood:Bands:{Mid,Slow}:{AnchorTauSec,VolTauSec,VolBaselineTauSec,SmoothTauSec,WeightMult}`. *Fast jitters; Slow rates the multi-hour regime.*
- **Candle persistence** — flush stamps `Candle.{MarketMood,MoodMid,MoodSlow}` (nullable) when Enabled; `AggregateCandles` picks the displayed band by target bucket (≤5m Fast / ≤1h Mid / else Slow) and carries the two slow cols = zero client change. *Mood history rides the existing candle feed, not a live-only poll.*
- **Reflexive taker coupling (uniform)** *(base off; prod on)* — lagged (5-min EMA) GLOBAL mood scales each bot's taker share (INTENSITY only, never direction): `mult = clamp(1 + GainGreed·max(tilt,0) + GainFear·max(−tilt,0), 1−Cap, 1+Cap)`, `tilt=(laggedMood−50)/50` (asymmetric-V — greed and fear both raise activity). `Mood:{TakerCoupling,MoodTakerGainGreed,MoodTakerGainFear,MoodTakerCap,MoodEmaSeconds}` + runtime kill-switch `MarketMoodService.ReflexiveKillSwitch`. *Shared mood → shared taker flow = the correlation channel; bounded + lagged so it can't latch.*
- **Per-strategy reaction table** *(base off; prod on)* — replaces the uniform gains with a per-`AiStrategy` taker-intensity table: TrendFollower chases both ways (0.12/0.10), MeanReversion/Scalper chase (0.08/0.05), Conviction FADES greed (`Sign −1`, GainFear 0), Random 0.10/0.07; MM/Rotator/Arb/House exempt. `Mood:{PerStrategy,PerStrategyGains:{<Strategy>:{GainGreed,GainFear,Sign}}}`. *Different cohorts feel the regime differently instead of one uniform gain.*
- **MM fear-widen ("elevator down")** *(base off; prod on = Stage B)* — in fear the MM cohort widens its half-spread (×up to `MMWidenSpreadMax` 1.5) and shrinks quote size (×down to `MMWidenSizeMin` 0.6). `Mood:{MMWiden,MMWidenSpreadMax,MMWidenSizeMin}`. *Thin book in fear → drops cut deeper = the downside amplifier; the highest-value new liquidity channel.*
- **Conviction fear-bid ("the absorber")** *(base off; prod on)* — the FIRST directional mood term: Conviction bots add BUY conviction on panic, bounded fear-only + buy-only: `conviction += ConvictionFearBidGain·max(0,(50−moodLag)/50)`. `Mood:{ConvictionFearBid,ConvictionFearBidGain}`. *Smart money buys the panic → cushions the MMWiden elevator so fear stays controlled, not a spiral.*
- **Guardrails** — `Mood:JointTakerCapMult` (1.5) bounds the combined taker multiplier; a mood-latch telemetry line reports the fraction of time global mood is pegged <30/>70 (`DrainLatchFraction` — a persistent latch = the fear-spiral tell). *Soak + prod gate: latch=0 = the absorber holds; CK=0 always.*

*Prod rollout (council 2-stage, both LIVE 2026-07-14): Stage A = gauge + persistence + per-strategy table + Conviction fear-bid; Stage B = +MMWiden, flipped only after a real prod fear-excursion recovered cleanly with latch=0 (the absorber proved on prod). Cross-stock correlation is a regime/duration effect judged on prod over days, not measurable in a 45m soak.*
