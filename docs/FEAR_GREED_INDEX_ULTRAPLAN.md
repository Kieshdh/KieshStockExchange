# Fear/Greed Index — Ultraplan (3 architects + 5-advisor council, Fable 5, 2026-07-14)

## Goal
A real, lively Fear/Greed index that (1) is a **composite** of fast market signals (not the smooth
`sentiment×activity` v1), (2) is **persisted on the candles** so its history lives in the DB and rides the
existing candle pipeline to the client, (3) drives **reflexive bot behavior** (greed → volume/aggression up),
and (4) is **visualized** in the client.

## The unification (Kiesh's key insight — the spine of the design)
Fear/Greed is **NOT a 5th parallel system.** Sentiment is a *smooth, longer-timeframe version of the same
emotional axis*; F&G is the *fast* layer. The coherent stack is **one axis at three timescales:**

> **sentiment (slow OU anchor) → F&G composite (fast, 0–100) → activity / taker-share (behavioral expression)**

Mood is a **read-only downstream projection** of sentiment + market observables onto 0–100 — it is **never**
added back into the bot decision accumulator (no circularity as a *source*). Reflexivity rides through the
*activity/taker* channel that sentiment+composition already drive, so greed literally raises taker
volume and fear lowers it — the systems reflect each other, on ONE axis. Sentiment stays the **independent
slow anchor** (do NOT let F&G subsume it — it's the mean-reverting floor that bounds the reflexive loop).

## The composite (council formula)
`mood = 50 + 50·tanh( w_mom·momZ + w_breadth·(2·breadth−1) − w_vol·volZ + w_flow·flowZ + w_sent·sentiment )`
- **momentum** (dominant): 5-min log-return ÷ its EWMA σ (fast, the liveliness driver)
- **breadth**: % of stocks up (broad rally = greed)
- **volatility** (inverted): 1-min realized vol ÷ its EWMA baseline (spike = fear)
- **flow imbalance**: (buy−sell)/(buy+sell) taker notional
- **sentiment**: the old signal, demoted to a small **slow anchor** (~0.25)
Normalize each to a z-score / bounded ratio; squash via tanh. Weights are config `Bots:Mood:*` (live-tunable).
`MoodScore(...)` is a **pure static** (unit-tested). Real F&G indices feel alive because they're dominated by
FAST price-derived components, not a sentiment level — that's the fix for "too smooth."

## Decision criterion (all 3 architects)
**A signal must be trustworthy as an OUTPUT before it is trusted as an INPUT.** Validate the gauge → persist it →
let the client see it → *only then* close the feedback loop. You cannot debug a reflexive loop while the gauge
itself is unproven (a bad `MoodScore` and a too-strong coupling produce the same symptom). Persistence lands
*before* reflexivity so every reflexive soak's mood is in the candle CSV as a legible baseline.

## Phases

### Phase 0 — pure `MoodScore` + `MarketMoodService` skeleton (auto-ship, feature branch)
New `KieshStockExchange.Server/Services/MarketDataServices/MarketMoodService.cs` (singleton) + pure static
`MoodScore`. Per-stock EWMA state struct (retEwmaSigma / volBaseline / buy-sell notional). Config bind `Bots:Mood:*`.
Replace `AiTradeService.MoodScore` internals so there's ONE definition. **Gate:** unit tests `MoodScoreTests`
(neutral→50, monotone per term, clamp [0,100], weight-zero degeneracy, tanh saturation).

### Phase 1 — live wiring (auto-ship, config-only)
`AiTradeService` loop calls `_mood.Observe(stockId, ret, vol, buy, sell)` + `_mood.Score(stockId, breadth, sentiment)`
per bot-loop tick (loop-thread, lock-free); breadth = one global scan. `MoodForStock`/`GetMarketMood` delegate to
the service (endpoint response shape unchanged → existing client pane keeps working). **Gate:** 15m eyeball soak —
crash a stock → mood drops; quiet → ~50; no NaN; bounded.

### Phase 2 — persist on candles (OWNER-GATED: schema + prod migration)
`Candle.MarketMood double?` (nullable, clamp setter, **carried in Clone()**, NOT in IsValid). Land the **four
Dapper sites** (the "Lateness trap"): `CandleCols` + `CandleRow` + `CandleMapper.ToDomain` + `UpsertCandlesAsync`
(INSERT cols/VALUES + `ON CONFLICT DO UPDATE SET "MarketMood"=EXCLUDED."MarketMood"`). EF migration
`AddCandleMarketMood` (additive nullable double, `Down`=DropColumn, boot-applied — clone `AddSharesOutstanding`).
Stamp **last-value on the 1m base candle at flush-drain** in `CandleService.FlushLoopAsync` (inject `IAiTradeService`;
`c.MarketMood = _bots.MoodForStock(c.StockId)` before `UpsertCandlesAsync`). Aggregation carries **LAST** (mood is
a level like Close, not a flow). Backfill/replay paths leave null (unreconstructable = honest). **Gate:** migration
applies clean on a scratch DB + 45m CK-soak, mood rows populate. **Owner-gated because merging to master = shipping
schema (prod boots migrations).** Rollback: binary rollback safe without Down (old SELECT ignores the extra column).

### Phase 3 — client history (auto-ship)
Mood pane reads `Candle.MarketMood` from `api/candles/by-stock-range` history + SignalR `CandleClosed`; **delete the
4s `/api/market/mood` poll** for history. Keep `/api/market/mood` only for the live *current* needle (in-progress
bucket has no closed candle). Null on old candles → render a gap (not 50). Optional: a **5-zone needle-dial** widget
(the recognized F&G idiom) beside the time-series pane. **Gate:** eyeball vs server gauge (inspect-and-present the dial).

### Phase 4 — reflexive coupling (OWNER-GATED flip: default-off flag + kill-switch + soak)
At `AiBotDecisionService.cs:1529` (`effectiveUseMarket`, beside the existing ±0.15 momentum/extreme mods), a
**lagged, capped, intensity-only** multiplier:
```
moodTilt = (LaggedMood(stockId) − 50)/50            // 5-min EMA; per-stock multiplier
stress   = |moodTilt|  (or moodTilt for a signed variant — see FORK)
effectiveUseMarket *= Clamp(1 + MoodTakerGain·stress, 1−cap, 1+cap)   // gain 0.10, cap 0.15, HARD-clamped
```
- **Intensity, never direction** (council red line): the coupling scales taker-share for whichever side the bot
  *already* chose; direction stays from sentiment's buyProb tilt. Direction-gain = runaway; intensity-gain = clustering.
- **De-conflict with composition-coupling (`_compTakerExp`, line 791):** DROP the composition-activity term from the
  *coupled* composite so the two channels stay orthogonal — composition = micro/instantaneous/post-pick,
  mood = macro/lagged/pre-pick.
- **The lag is load-bearing:** mood is computed FROM price/flow; the coupling MOVES price/flow → instantaneous read =
  a tick-loop that oscillates/ratchets. The 5-min EMA low-passes the loop gain to ~0 at tick frequency → reflexivity
  only at regime timescale (bubbles/capitulation, the point).
- **Global term = the shared-aggression channel** the correlation arc lacked (corr capped ~0.08 with nothing shared
  driving taker flow) — the composite's global component is the upside lever.
- Config `Bots:Mood:{TakerCoupling=false, MoodTakerGain=0.10, MoodTakerCap=0.15, MoodEmaSeconds=300}` + a runtime
  `volatile bool` kill-switch (admin endpoint, no restart). MM-exempt, `isChase`-exempt, one gated draw (mirror 791).

**⚑ FORK to resolve at Phase 4 (council + Kiesh):** Kiesh's "greed→volume up, **fear→down**" (MONOTONIC/signed:
greed=active, fear=withdrawn) vs Architect B's **V-shape** (`|mood−50|`: both extremes raise urgency, fear=panic-
volume — realistic). Council this before wiring; the read-only gauge (P0–3) is independent of it.

**Soak plan:** 45m A/B (coupling off@5081 vs on@5083, live client on ON) → 2h bake. **Gates:** CK=0 (hard) · drift
bounded (no band-punch) · vol-clustering preserved/improved (`vol_cluster.py`) · **cross-stock correlation LIFTS**
(`sector_corr.py --demean` — the upside) · ActiveBotCap held · taker-share mean shift ≤ ~+12%. **Kill:** CK≠0 · any
stock pinned at ×3 · global mood pegged <10 or >90 for >10min (latch-up) · taker >1.2× baseline. **Top risk =
saturation latch-up** (coupling→taker→vol/flow→mood pegs extreme→permanent elevated aggression); mitigations
layered: EMA lag + StressCap 0.8 + sentiment OU mean-reversion + pegging kill-condition; **first dial-down = gain
(0.10→0.05), not lag.** Default-off, **staged for Kiesh — never auto-flipped.**

### Phase 5 (future, owner-gated) — F&G subsumes/leads sentiment
Only after the loop validates: consider making F&G the primary emotional layer with sentiment as a pure slow
anchor. Not a starting assumption (would lose the independent bounding floor).

## Docs
Update `docs/BOT_MECHANICS.md` (§2 mechanism reference) with the full F&G system: the one-axis-three-timescales
model, the composite formula + signals, the candle persistence, and the reflexive coupling — Kiesh: "I want this
system explained well."

## Ownership summary
Auto-ship (feature branch): P0, P1, P3, P4-code(default-off). **Owner-gated:** P2 merge-to-master (schema→prod),
P4 flip (reflexivity on prod). NO prod flips unattended.
