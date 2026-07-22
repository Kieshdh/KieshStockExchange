# REALISM ULTRADESIGN — fire-ready spec (3 changes, council-synthesized 2026-07-22)

Ultradesign method (feasibility ×3 → architects ×3 → council ×5 → chairman). Build on the RESTRUCTURE branch
`perf/admin-table-time-indexes` (decoupled realism services) so the dedup + realism ship together (Kiesh). Line:numbers
below are from the soak worktree (master) — REMAP to the decoupled files on the restructure branch (same methods). Every
lever ships DEFAULT-OFF / byte-identical; CK=0 is the always-on HARD gate.

## BUILD ORDER (split-first, validate in isolation before stacking)
0. **Instrumentation** (no behavior change): `RegimeTakerProbe` (mirror ActivityCompositionProbe) + a per-stock
   `RegimeSignal` column in the candle CSV + a golden-master byte-identical checker (off-arm candle-CSV hash == prod-config
   baseline). Nothing downstream is trustworthy without these.
1. **CHANGE 3** (small code — owner's headline, most locally-testable, load-bearing) → ship default-off, run Test 3.
2. **CHANGE 1** (pure config — the damper that must catch Change 3) → run Test 1.
3. **CHANGE 2 EXTREME** (pure config — already-live CK-verified down-drivers) → run Test 2-extreme under FORCED crisis.
4. **COMBINED BAKE** (3+1+2 together — the interaction is the point), co-tune the taker-share set.
5. **CHANGE 2 EARNED-CALM** (new code, Calm-only) — ONLY if step 4 shows quiet is faked/absence-based.
Prod rollout incremental behind flags, days apart; the −20% pin is a prod-over-days verdict; never flip a taker/anchor prod
default unattended.

## CHANGE 3 — RegimeDrift → TAKER flow (small code, ships FIRST)
Today `sum += _regimeStrength*rg` (BotSentimentService.cs:372) = buyProb tilt = book-absorbed = "random walk doesn't move
price." KEEP :372 (the tilt = the "combination"); ADD a POST-PICK per-stock taker override. **NOT pre-pick watchlist-avg**
(regime is per-stock INDEPENDENT — averaging N independent walks washes out ~1/√N below threshold). Mirror the COMPOSITION
override's post-pick site (AiBotDecisionService.cs:816-839), NOT TrendFollower's pre-pick site.
Code:
1. `BotSentimentService.cs` (~:544, by GetSentimentSlope/GlobalSignal): `internal decimal RegimeSignal(int stockId) =>
   (decimal)_regime.GetValueOrDefault(stockId);` (raw walk ±Cap=±0.5; lock-free read; advances NO RNG).
2. `AiBotDecisionService.cs` BuildOrder — insert AFTER the composition override (ends :840), BEFORE the open-taker ramp
   (:846). Gate short-circuits before ANY draw (byte-identical off): `if (!isChase && user.Strategy != MarketMaker &&
   _regimeTakerCoupling && _regimeTakerStrength>0m) { var rsig=_sentiment.RegimeSignal(stockId); if (|rsig|>=_regimeTakerThreshold
   && HashUnit01(id^RegimeTakerCohortSalt) < _regimeTakerCohortFraction) { var contrarian=HashUnit01(id^RegimeContrarianSalt)
   < _regimeContrarianFraction; var (over,_,buy)=TrendTakerDecision(rsig,_regimeTakerStrength,contrarian,ctx.Decimal01(id),
   _regimeTakerThreshold); if(over){ if(!buy){ pos=ctx.GetPosition(userId,stockId); if(pos.Quantity<=0) skip; else
   type=SlippageMarketSell; } else type=SlippageMarketBuy; } } }`. REUSE `TrendTakerDecision` (:2543, signal-agnostic) +
   the no-share guard from ApplyExtremeReaction (:2817-2821, zero-inv sell → SKIP not naked short). Add fields/ctor-params/
   consts (RegimeTakerCohortSalt, RegimeContrarianSalt). Value-band veto (:861-863) runs DOWNSTREAM = per-tick over-extension brake.
3. `AiTradeService.cs` (~:559-563, RegimeDrift ctor-arg group): read + pass `Bots:Sentiment:RegimeDrift:{TakerCoupling(false),
   TakerThreshold(0.15m), TakerStrength(0m), CohortFraction(0.3m), ContrarianFraction(0.2m)}`.
4. `appsettings.json` — extend RegimeDrift block (:469-475) with those 5 keys + comment.
TUNING: Cap=0.5 ⇒ Threshold 0.15 fires ~30% to the wall; start TakerStrength LOW ~0.4. MECHANISM = LEVEL→VELOCITY
(integration): price ≈ ∫(regime above threshold) ⇒ a mean-reverting regime → a TRENDING price while one-signed; excursion =
strength × dwell; tune TakerStrength × anchor-drift = wander WAVELENGTH (co-tune vs Change-1 Elastic). Cohort dispersion
(CohortFraction + Decimal01 + Contrarian) prevents a lockstep giant candle. Keep prod Strength 0.8 on the tilt (owner said keep);
if same-sign over-drive, reduce Strength NOT the taker.
CK: LOW — mutates only OrderType→SlippageMarket*, rides OrderEntry→Match→Settle like the TF taker; off = byte-identical +
RNG-stream-identical. SCOPE HONESTY: delivers "positive REGIME moves price," not full "positive SENTIMENT" (news/OU/Herd still
absorbed) — full-sentiment coupling (or CoMovement, the correct market channel) is a FOLLOW-UP arc.

## CHANGE 1 — value anchor = 28-day window PAIRED WITH Elastic (pure config, ZERO code)
28 ALONE still pins (linear pull = constant snap-back = the ret_acf≈−0.43). The owner's "wander free, overextension drifts
back" comes ONLY from the elastic soft-wall (zero pull inside ±deadband, then superlinear). **SHIP THE PAIR.**
- `Bots:ValueAnchor:WindowDays` 7→28 (:157) + `Bots:ValueAnchor:Elastic` false→true (:147).
- Keep ElasticDeadbandPrc 0.20 (:148), ElasticPower 3.0 (:149), Scale (:145) — tune JOINTLY in soak.
- Keep MaxDailyDrift 0.5 (:156) = the multi-day backstop (a sustained Change-3 taker drive DRAGS the TWAP-of-price target
  with it = a long-horizon unit root = the random walk the owner wants, BUT the anchor then can't bound persistent drift —
  only MaxDailyDrift + AbsoluteCapMax ×3 can). Do NOT weaken the band.
- CO-TUNE: RecentAnchor is ON in prod at Strength 0.05 (a 30-min-EWMA damper fighting every excursion) — test 0.05 AND 0.0.
- CK: ZERO (anchor = a buyProb target, no orders). WARMUP: 28-slot window needs 28 REAL days to warm (ServiceStart) →
  near-seed early on prod; UNTESTABLE in a normal local soak (0 rotations <2h) — to mechanism-test, shrink DayLengthHours to
  ~0.25h; else read the local null as "untestable," NOT "no effect."

## CHANGE 2 — honest EXTREME (config, already-live levers) + honest EARNED CALM (deferred code)
★ The asymmetric, correlated, TAKER-driven crash apparatus ALREADY SHIPS LIVE + CK-verified (ExogShock chaser
ChaserNotionalFrac 0.06 + GlobalFraction 0.25 + GlobalCoFire + Permanence AlphaMin 0.40 + aftershocks). **Do NOT build a
state machine for extreme; do NOT enable GlobalShock** (superseded sentiment-tilt, book-absorbed). ApplyExtremeReaction is
DEAD when SentimentDynamics is on (:813 `if(!isChase && !_sentimentDynamics)`) — don't touch the symmetric DirectionalBias/
AggressionBoost paths.
EXTREME FIRST BAKE = CONFIG ONLY:
- `Bots:BearShortStrength` 0.0→~0.6 (:129) — the asymmetry amplifier: a flat bearish bot opens a cash-collateralized SHORT
  instead of no-op'ing (holds no inventory to sell) ⇒ down-moves as sharp as up. CK-safe (÷3 geometric-floor veto = the
  per-tick brake). ZERO new code.
- Optional fat down-tails: `Bots:SectorEventProb` 0→small + DownBias 0.7 (:250-252), and/or raise ExogShock:GlobalFraction.
- Drift-back = ExogShock decay + Permanence un-ratchet + the Change-1 elastic anchor + Mood:ConvictionFearBid absorber (live).
EARNED CALM = the ONE genuinely-new capability (DEFERRED, build ONLY if the combined bake shows calm is faked/absence-based).
Correlated stand-down needs a SHARED STATE (can't emerge from per-bot baselines). Because EXTREME is config-handled, the
machine is CALM-ONLY ⇒ NO crash-latch risk (removing flow can't self-reinforce a crash). New class `BotMarketRegimeService.cs`
(2-state Schmitt {Normal,Calm}, intensity = blend of LaggedGlobalMood + pooled cross-sectional |ret| vol; exposes
`CalmStanddown`∈[0,1]); consumer scales Activity G down / raises _compTakerExp fleet-wide in Calm (the taker→limit downgrade at
:822-839 rises correlated). GUARD: bound CalmStanddown + activity floor + keep Change-3 flow firing in Calm (don't re-pin).
Config identity-off `Bots:MarketRegime:{Enabled(false),EnterCalm,ExitCalm,MaxDwellSec,MoodWeight,VolWeight,CalmStanddown(0)}`
+ state-occupancy/dwell telemetry.

## A/B STRATEGY
Baseline = the LIVE PROD OVERRIDE (docker-compose.prod.yml: RegimeDrift 0.8, MPM 1.4, RecentAnchor 0.05, full ExogShock+Mood),
NOT appsettings defaults. Step 0 golden-master + instrumentation first (headline test un-runnable without the RegimeSignal probe).
- **TEST 3** (separate, first — headline, locally provable): +RegimeTakerCoupling=true, TakerStrength~0.4, Threshold 0.15,
  CohortFraction 0.3. 45m parallel, client on ON arm (5083). METRIC = within-stock corr(RegimeSignal[t], forward mid-return) >0
  (NOT a between-stock sign split — regime is a zero-crossing walk). GUARD: CK=0, no lockstep giant candle, no reject storm, ret_acf not worse.
- **TEST 1** (separate — anchor): Elastic+28 + DayLengthHours~0.25h (force rotations) + RecentAnchor 0.05 AND 0.0. METRIC =
  inside-deadband wander survives (less pinned than −0.43) AND overextension drifts back. WindowDays magnitude = prod-over-weeks.
- **TEST 2-EXTREME** (separate — config): BearShort~0.6 (+opt SectorEvent/GlobalFraction). METRIC = down>up skew, correlated,
  recovers via anchor. GUARD ELEVATED: run the CK gate under a FORCED crisis (correlated burst = the batch-settle group-tx race
  shape fixed at 853c7e6) — F1 same-user buyer+seller probe + CK_Positions/CK_Funds + reservation-reconcile WARN + latch bounded.
- **COMBINED BAKE** (3+1+2, 2h): watch chaser+regime+bear-short COMPOUNDING into runaway/giant candles; the 28-day elastic
  anchor + value-band veto + AbsoluteCapMax ×3 must catch WITHOUT re-pinning. Co-tune the 4-knob taker-share SET
  (Activity.G, _compTakerExp, CohortFraction, TakerStrength) as a set. Owner eyeballs the ON arm = a random walk.
- **EARNED-CALM** (separate, last, code — only if needed).

## CK ARGUMENT (per path)
- Change 1: CK-ZERO by construction (anchor target = a damper, no orders).
- Change 3: CK-LOW, structurally identical to TF taker (:1670-1712) + composition override (:816-839), both CK-clean on prod;
  RegimeSignal advances no RNG + gate before Decimal01 ⇒ off = byte-identical + RNG-stream-identical; zero-inv sell → SKIP;
  value-band veto downstream; no balance/position written.
- Change 2 extreme: already CK-verified live; ★ RE-PROVE under FORCED crisis (correlated burst on shared Fund/Position group-tx).
- Change 2 earned-calm: CK-trivial (only removes flow; no orders; Schmitt+max-dwell bounds the mild calm-latch).
- Universal: any soak with CK≠0 OR an unbounded latch FAILS, full stop.
