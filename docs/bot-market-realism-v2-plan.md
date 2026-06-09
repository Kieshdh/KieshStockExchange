# Plan: make the bot market move and breathe like a real one

**Status:** design, not implemented. Companion to `docs/bot-market-realism.md` (direction realism
shipped: fat tails, news shocks, MM quoting) and `docs/bot-variable-volume.md` (the activity-field
volume spec). This plan unifies the *root-cause* fix for the flat, directional chart and sequences
the work. Land everything **flag-gated / inert-first**, the §3.4–3.5 way. Tune by eye, not by test.

> Revision log lives at the bottom (§11). This document was drafted then improved across 5 passes.

> **Guiding principle for the implementer (read first).** The chart is flat because ~20k bots each run
> *independent per-bot probability arithmetic* (buyProb coin-flips) whose net flow averages to ~zero — and
> the prior fix attempts were *more* of that same arithmetic, which is why they failed. **Do not solve this
> by tuning per-bot probabilities or adding cleverer per-bot formulas.** The fix is *emergent correlated
> behaviour*: persistence (inertia, A1) and herding (a shared regime, A2) so order flow stops cancelling.
> Movement must come from genuine *imbalance*, not from a better formula on each isolated bot. If a proposed
> change is essentially "adjust buyProb/biases," it's the wrong layer. And keep it bounded to the §2
> magnitude budget — realistic *texture*, not big swings.

---

## 1. Problem statement (the diagnosis)

The chart is flat and directional — candles have near-identical highs/lows, no wicks, a smooth
rise/fall — despite high volume. Root causes, in order of importance:

1. **20k independent traders average each other out (the law of large numbers).** Price moves on
   order-flow *imbalance* (buys − sells), not volume. With ~20,000 bots each flipping a near-fair
   buy/sell coin **independently every tick**, net imbalance scales ~√N while volume scales ~N, so
   the *fractional* imbalance ≈ 1/√N ≈ 0.7 %. Massive volume, ~zero net pressure; any excursion is
   cancelled next tick. A single random-walk ticker looked real because N = 1 — no cancellation.
2. **Sentiment is smooth and broad, not sharp and concentrated.** The shared AR(1) sentiment nudges
   *everyone a little, smoothly* (max ±0.20 tilt). Smooth, fleet-wide bias produces a gentle drift,
   never the sharp imbalance that prints range.
3. **Price impact is clamped flat.** Anti-sweep (`_maxSweepFractionOfDepth=0.25`) + low slippage cap
   (`_marketSlippagePrc=0.003`) + tight limit tiers pin every trade near mid, so even net flow can't
   print a wick. These were added to stop runaways — they also killed candle range.
4. **The value anchor double-suppresses** any intraday excursion on top of the LLN averaging.

The literature is blunt: realistic agents must be *"not identical and **not independent**."*
Independence is the bug (Kirman, Cont–Bouchaud, Lux–Marchesi).

## 2. Goal & non-goals

**Goal:** a chart that *moves* (real excursions/trends from genuine imbalance) and *breathes*
(clustered, variable volume; wicks; varied ranges) — while staying bounded (no runaway) and
conservation-clean.

**Movement-magnitude budget (a hard target, not a nice-to-have):** the goal is *texture*, not big swings.
Calibrate so that over a **4-hour window** the **typical** stock moves **≤ ~5 %**, and a **20 %** move is
*possible but rare* (a tail outlier, not a regular event). Concretely: median |4h return| small, ~95th
percentile ≈ 5 %, and only the extreme tail approaching ~20 %. This budget is the primary constraint that
sizes the imbalance pillar (§4c) and defines "escape" for the bounding stack (§6) and the gate (§7).

**Non-goals:** passing econometric calibration; matching real tickers; changing the matching/
settlement engine contract; per-bot Excel re-seeding in v1 (defaults in code first).

## 3. The three pillars

Movement comes from **imbalance**; texture from **volume**; wicks from **microstructure**. They are
complementary — all three are needed, but they have very different risk and sequence (§4).

### Pillar A — Imbalance / movement (makes the chart go somewhere)
Break independence so net flow stops cancelling. Four mechanisms, highest-impact first:

- **A1 Inertia (Cont–Bouchaud's key ingredient).** Bots hold a directional *stance* across ticks
  instead of re-rolling buy/sell every tick. A stance persists for a drawn duration, then flips.
  This alone stops tick-to-tick self-cancellation and produces lumpy, fat-tailed flow. **Highest
  impact, lowest risk.** Seam: `ChooseOrderType` (`AiBotDecisionService.cs:480`), state per bot.
- **A2 Herding via a sharp common factor (Kirman).** Replace the smooth, broad sentiment tilt with a
  **regime** a large fraction of bots *commit to together*: draw the directional decision from a
  shared per-tick innovation + small idiosyncratic noise, so net flow ≈ (common shock)·N (does not
  cancel). Regime is **bimodal and flips sharply**, not smooth AR(1).
- **A3 Momentum dominance (Lux–Marchesi).** Let trend-following *temporarily win* over mean-reversion
  (today they're balanced to net zero by design, `:440-447`), so a move recruits followers → overshoot
  → exhaustion → revert. A directional self-exciting loop.
- **A4 Role split.** Most bots = noise/liquidity traders (both sides, ~zero net → volume); a minority
  (or time-varying activated subset) = directional cohort that leans hard together → imbalance.

Plus **A5: weaken the *fast* value anchor** (let intraday excursions happen); keep it firm only on the
slow/multi-hour scale so price still can't escape.

### Pillar B — Volume / activity (makes it breathe) — see `bot-variable-volume.md`
The three-layer `A = G(t)·S(stock,t)·B(bot,t)` self-exciting activity field in a new
`BotActivityService`. `G·B` modulates how often a bot trades (gate seam,
`AiTradeService.cs:776-786`); `S` modulates which name catches volume (PickStock seam,
`AiBotDecisionService.cs:539`) and the extreme-reaction gain. Near-critical Hawkes (`n≈0.9`),
multi-timescale decay, **leverage asymmetry** (down excites more than up).

### Pillar C — Candle range / microstructure (makes the wicks)
- **C1 Activity-scaled price impact.** Make `_marketSlippagePrc` and `_maxSweepFractionOfDepth`
  functions of `S` (and StockProfile class): hot → relax → sweeps print wicks/spikes; calm → tight.
  Reuses the activity field and the existing `ApplyDepthCapAsync`/`EffectiveSlippage` seams.
- **C2 Bid-ask bounce.** Stop pinning trades to mid: allow a real spread so trades alternate bid/ask,
  printing High=ask/Low=bid each bar even with zero net move.
- **C3 Fat-tailed *impact*** on a minority of market orders (occasional deeper sweeps), so spikes
  print even in calm periods.

## 3b. Implementability assessment (what's actually cheap to build)

Rated against the real decision path. "Effort" assumes mirroring an existing pattern; "Risk" is to
conservation/stability. The existing `AiBotContext.BurstEndTimes` dict (`AiBotContext.cs:47`) is the
template for any per-bot transient state, so all per-bot mechanisms have a proven home.

| Idea | Effort | Risk | Seam / pattern it reuses | Verdict |
|---|---|---|---|---|
| **A1 Inertia** | **low** | **low** | per-bot stance dict like `BurstEndTimes`; bias applied in `ChooseOrderType` before the `isBuy` roll (`:480`) | **MVP — build first** |
| A5 weak fast anchor | low | low | it's already a knob: raise `_overheatCap`/`_valueAnchorScale`, soften the band veto (`IsOverBandAsync :828`) | easy companion to A1 |
| A3 momentum dominance | low–med | med | re-weight the TF/MR switch (`:436-447`) so trend can exceed reversion under a regime flag | easy edit, needs A2's regime to key off |
| A4 role split | low | low | tag noise vs directional by `user.Strategy` or a stable hash; noise bots forced ~50/50 | easy |
| **A2 herding common factor** | **med** | **med–high** | a tiny regime service ticked once/loop (like sentiment); per-bot follower flag by hash; large tilt added to `buyProb` | the real mover; gate hard |
| B activity field | med | low–med | new `BotActivityService` mirroring `BotSentimentService`; two existing seams | self-contained |
| C1 activity-scaled impact | low | med | multiply `_marketSlippagePrc`/`_maxSweepFractionOfDepth` by f(S) in `EffectiveSlippage`/`ApplyDepthCapAsync` | trivial **once B exists** |
| C3 fat-tailed impact | low | med | per-order roll relaxing the caps for a minority | easy |
| C2 bid-ask bounce | **high** | med | needs a real spread + market orders executing at ask/bid, not mid — touches book/exec, not just the decision layer | **defer / investigate** |

**MVP (the smallest change that should visibly move the chart): A1 inertia.** Add a per-bot stance
`{ dir, expiresAt }` in `AiBotContext` (a dict keyed by `aiUserId`, exactly like `BurstEndTimes`). In
`ChooseOrderType`, *after* `buyProb` is computed: if a stance is active, force/strongly-bias `isBuy`
to the stance direction and skip the independent coin-flip; if not, roll a fresh stance from `buyProb`
and set `expiresAt = now + drawnDuration` (e.g. 30 s–10 min, per-bot varied). A fill may extend or
reset the stance (open question §9). This is ~30 lines, reuses the daily-seeded RNG, is inert behind a
flag, and directly attacks the LLN cancellation (stances persist → flow stops self-cancelling
tick-to-tick). Measure with `candle_realism.py` before/after; expect `range CV` up and `body/range`
moving toward the RW baseline even before A2.

**Not implementable cheaply:** C2 (bid-ask bounce) — the engine executes bot market orders against mid
with capped slippage, so genuine bid/ask alternation would require spread/quote changes in the
matching/exec layer. Treat as a separate investigation; A1+A2+C1 likely deliver the look without it.

## 3c. A1 inertia — build-ready spec (the MVP)

Flag `Bots:Imbalance:Inertia` (default false → no new RNG draws, byte-identical stream).

**State** (in `AiBotContext`, mirroring `BurstEndTimes`):
```
Dictionary<int,(sbyte dir, DateTime until)> Stances;   // dir +1 buy / -1 sell, keyed by aiUserId
```
**Decision** in `ChooseOrderType`, *after* `buyProb` is computed, **only for directional strategies**
(skip `MarketMaker`/`Arbitrage` — they're the liquidity/noise layer):
```
if (flag off) -> unchanged.
if (now < stance.until):                       // stance active
    buyProb = dir==+1 ? max(buyProb, 1-Leak) : min(buyProb, Leak)   // Leak~0.1 (0 = hard commit)
else:                                          // roll a fresh stance
    dir = Decimal01 < buyProb ? +1 : -1        // initial side from today's probability
    T   = Lerp(MinSec, MaxSec, Decimal01)      // per-bot varied duration (e.g. 30s..10min)
    Stances[aiUserId] = (dir, now + T)
    apply the same bias
// isBuy is then derived from the biased buyProb exactly as today
```
**Fill interaction:** a fill does **not** reset the stance (a working view persists); it expires on `T`.
(Alternative — a counter-stance fill shortens it — deferred.)

**Sell-when-flat caveat (the §4b trap, stated honestly):** a flat bot drawn into a *sell* stance is
filtered downstream (no inventory) and goes silent, while flat *buy*-stance bots act — so the flat cohort
tilts **net up**. A1 alone therefore has a mild upward bias; balancing it needs A4 (sell cohorts that hold
inventory) or routing flat sells to short-open. Acceptable for the MVP *measurement* (it still proves
movement), but pair with A4 before relying on the level.

**Determinism:** the two `Decimal01` draws (dir, T) happen only when the flag is on, in fixed order →
flag-off identical, flag-on reproducible.

## 4. Sequencing (cheapest, highest-impact, safest first)

1. **A1 Inertia** — smallest change, biggest movement gain, lowest risk. Ship first, measure with the
   monitor.
2. **A5 weak-fast-anchor** — trivially complements A1 by not killing the excursions it creates.
3. **B activity field** (volume) — independent, restores variable/clustered volume.
4. **A2 herding common-factor + A3 momentum dominance** — the sharp, coordinated moves. Higher risk
   (imbalance → runaway), gate carefully.
5. **C range/microstructure** — wicks, once movement + volume exist to hang them on.
6. **A4 role split** — refinement once the above are tuned.

## 4b. Correctness & integration (real interactions, not hand-waving)

Each mechanism must compose with constraints already in the decision path, or it silently misfires:

- **Sell stance ≠ sell pressure when flat.** `ChooseStockId` filters sells to stocks the bot actually
  holds (`:514-528`), and Phase 1.5 rejects sells without inventory. So a forced *sell* stance on a flat
  bot does nothing → mechanisms that only add sell *intent* create an **upward bias** (buys execute,
  sells get filtered). Mitigations: route flat-bot sell stances to the short-open advanced path, or have
  the **role split (A4)** ensure directional sell cohorts are bots that hold inventory, or pair sell
  regimes with shares to sell. Must be explicit or the market drifts up.
- **MM and Arbitrage bots are out of scope for A1–A3.** `ChooseMarketMakerQuote` returns before the
  directional logic (`:411-412`) — MMs stay two-sided liquidity (good, they're the "noise/volume" layer).
  Arbitrage bots skip the whole flow (`AiTradeService.cs:759`). Inertia/herding apply only to the
  directional strategies; the role split (A4) is partly *already there* via `user.Strategy`.
- **Determinism is draw-order sensitive.** The advanced/plain stream is seeded and sequential in
  ascending `aiUserId` (`AiBotContext.Decimal01`). Any new RNG draw (stance roll, follower noise) must be
  taken **only when the flag is on**, so the flag-off sequence is byte-identical, and in a fixed position
  so flag-on stays reproducible.
- **Ordering vs extreme reactions.** `ApplyExtremeReaction` overrides the type *after* `ChooseOrderType`
  (`ComputeOrderAsync :182`). A stance/herd tilt feeds `buyProb` *upstream* of that, so a shock can still
  flip a stance bot — intended (news beats your standing view), but document the precedence.
- **A3 breaks a deliberate zero-sum.** The TF/MR ±0.175 symmetry is intentionally net-zero (`:440-447`).
  Letting trend dominate creates standing directional bias; it **must** be regime-scoped and bounded by
  A5's slow anchor, or it's just a one-way drift.
- **C1 reopens the cascade path.** Relaxing `_marketSlippagePrc`/`_maxSweepFractionOfDepth` is exactly
  what the anti-sweep prevented. Keep an **absolute ceiling** (relaxation only between the tight default
  and a capped max) and gate it on activity `S`, never unconditional.

## 4c. How much coordination beats the LLN (sizing A1 + A2)

The whole problem is quantitative, so size the fix quantitatively.

**Independent baseline (today):** N bots at buyProb ≈ 0.5 → net buy-fraction has mean 0, std ≈ 1/(2√N).
At N = 20,000 that's ±0.35 %. That noise floor *is* the flat chart.

**Herding (A2) makes imbalance N-independent.** Let a fraction `f` of bots be *followers* who tilt their
buy probability by `δ` in the regime's direction; the rest are noise at 0.5. Expected net buy-fraction:

```
imbalance ≈ f · 2δ          (independent of N — coordination doesn't average away)
```

So to get a **5 % net imbalance** (≈14× today's noise floor → visible directional pressure): `f·δ = 0.025`,
e.g. `f = 0.25, δ = 0.10`. The regime flips sharply (2-state Markov, low per-tick flip prob → minutes-to-
hours regimes), so imbalance is *persistent then reverses* → trends and excursions, not chop.

**Inertia (A1) supplies time-correlation even without a regime.** A stance held for `T` ticks makes each
bot's flow one-directional over `T`, so per-bot contributions are autocorrelated; aggregate imbalance no
longer cancels tick-to-tick. A1 alone lifts the noise floor and fattens tails; A1 + A2 give directed
trends. This is why A1 is the MVP and A2 is the amplifier.

**Tuning levers that fall out:** `f` (follower fraction, A4 role split sets it), `δ` (per-follower tilt),
regime flip rate (excursion frequency), stance duration `T` (trend length). Start `f·δ ≈ 0.02–0.03` and
watch the monitor; back off if price escapes the bands.

**Calibrate to the §2 magnitude budget.** `f·δ` × (regime duration) sets how far price travels per regime;
this is the knob you turn to land the **≤ ~5 % typical / ~20 % rare per 4 h** target. The imbalance
*persists* only while a regime holds, so a higher flip rate (shorter regimes) caps the per-window travel
even at the same `f·δ`. If the monitor's 4h-return p95 runs above ~5 %, lower `f·δ` first, then shorten
regimes; reserve the ~20 % tail for the rare aligned-regimes-plus-shock coincidence, not steady state.

## 5. Determinism, performance, scale (the 20k bar)

- All stochastic state advances once per `Tick` on the loop thread (like `BotSentimentService`);
  per-stock fields cached for O(1) hot-path reads. No new RNG draws on disabled paths.
- Inertia/stance state is per-bot O(1); herding common factor is O(1) per tick shared across bots.
- Everything seeded + reproducible; flags inert → byte-identical to today.

## 5b. Loop-speed cost vs realism (the worth-it test)

These mechanisms live in the **decision/collect phase — the cheap part of the loop.** Post-Option-B the
steady tick is ≈ `check 0.03 + collect ~20 + batch ~290 + adv ~245 + arb ~28 ms`; the decision layer
(where A/B/C compute) is ~20 ms of ~590 ms (**~3 %**). The expensive 90 % is **engine submission**, which
these don't touch directly.

| Mechanism | Direct hot-path cost | Why |
|---|---|---|
| A1 inertia | ~free | one dict lookup + occasionally 2 RNG draws/bot — same order as the existing `BurstEndTimes` check |
| A2 herding | ~free | one shared regime step per loop (O(1)) + a cached per-bot follower hash + one add |
| A3 / A4 / A5 | ~free | a few arithmetic ops / a flag check / a constant |
| B activity | small | a per-loop `Tick` like `BotSentimentService` (50 stocks × few rings) + per-bot `B` = a watchlist average (≈ doubles the existing `AverageWatchlistSentiment` cost) |
| C1 / C3 | ~free | a couple multiplies; `ApplyDepthCapAsync` already reads the book |

Direct CPU cost is dominated by Pillar B, and even that is on the scale of the existing sentiment
averaging — small absolute ms. **The real perf question is indirect:**

1. **Order volume into the engine** (the 90 % span). More *executed* trades ⇒ more settlement. But the
   activity field with `ActivityBaseline < 1` *lowers average* volume (quiet most of the time, bounded
   spikes), so it's likely perf-neutral-to-positive; imbalance converts resting/cancelled flow into
   matches but doesn't necessarily raise order *count*.
2. **`BotScalerService` self-protects.** If per-tick work rises, it lowers `ActiveBotCap` to hold the loop
   in budget. So the failure mode isn't a slow loop (the scaler prevents that) — it's a **lower
   sustainable bot count** for the same budget. That is the number to measure.

**Worth-it test (per pillar, flag on vs off):** with `Bots:PhaseTimingSeconds` on, compare the `collect`
phase ms and the scaler's steady `ActiveBotCap`. If a pillar materially drops the cap (fewer bots / less
volume) for a small monitor gain, **cut it.** Expected: A1/A2/A3/C ≈ free; **B is the only one that needs
a real before/after**, and its `baseline<1` may pay for itself by reducing average order load.

## 6. Stability, failure modes & rollback

This work *reintroduces the imbalance the balancing effort suppressed* — that's the point and the risk.
The bounding contract: excursions must be **bounded and reverting** (move then snap back), never escape.

**"In the bands" is now a number (§2 budget):** typical stock ≤ ~5 % per 4 h, ~20 % only as a rare tail.
The existing `scripts/balance-drift.sql` is the detector — its `medianAbs_pct`, `max_pct`, and
`beyond50`/`beyond100` columns measure exactly this (drift vs seed). "Escape" = medianAbs creeping up,
`beyond50` going nonzero, or `max` regularly past the tail. The prior balancing tuning already holds this;
this work must not undo it.

**The bounding stack (what stops runaway), slow-to-fast:**
- Slow value anchor (A5 keeps it) + far limit walls — the multi-hour ceiling/floor on price.
- A3 exhaustion — momentum dominance is regime-scoped and decays, so trends end.
- C1 impact relaxation is **capped** (tight default → bounded max) and **activity-gated** — never
  unconditional; the §3.6 anti-sweep remains the absolute structural ceiling.
- Per-stock `S_max` and the overheat band cap how hot any single name can get.

**Failure modes to watch (and the tell):**
| Failure | Tell in telemetry/monitor | Fix |
|---|---|---|
| One-way upward drift | flat-bot sell stances filtered (§4b) → price climbs | route sell stances to shorts / A4 inventory cohorts |
| Runaway escape | price leaves bands; overheat vetoes firing constantly | lower `f·δ`, raise anchor, tighten C1 cap |
| Whipsaw chop returns | `range CV` high but mean-reverting; regime flips too fast | slow the regime flip rate, lengthen stance `T` |
| Volume collapses | activity floor too low; bots stuck in stance not trading | raise `S` floor; stance affects *side*, not whether to trade |
| Conservation breach | `ConservationProbe`≠0 / `CK_*`≠0 | hard stop — flag off immediately (none of A/B/C touch settlement, so this would be a bug, not tuning) |

**Rollback:** every pillar is an independent flag defaulting off; flip off to revert instantly (A0-style,
like `Bots:Advanced:MaxPerTick`). Flags are orthogonal, so a bad pillar can be disabled without losing the
others. No data migration, so rollback is config-only.

## 7. Merge gate (non-negotiable)
Flag-gated; merge a pillar only after a PROD soak shows `ConservationProbe`=0, `CK_*`=0,
`ReservationAuditor` in tolerance, tests green, the candle monitor shows movement toward the random-walk
baseline, **and** the balance harness (`kse-balance-soak.ps1` / `balance-drift.sql`) confirms drift stays
within the §2 budget — `medianAbs_pct ≲ 5`, `beyond50 = 0`, `max_pct` ~20 only as a rare tail. Movement
*and* boundedness, both proven, or it doesn't merge.

## 7b. Config flags & default parameters

All default **off / inert**; all under `Bots:` to match the existing config tree. Each pillar is one
master flag so it can be A/B'd and rolled back independently (§6).

| Flag (`Bots:…`) | Pillar | Default | Range / start | Meaning |
|---|---|---|---|---|
| `Imbalance:Inertia` | A1 | false | — | master: stance persistence |
| `Imbalance:Inertia:MinSec` / `MaxSec` | A1 | 30 / 600 | 10..1800 | stance duration band |
| `Imbalance:Inertia:Leak` | A1 | 0.10 | 0..0.3 | off-stance trade chance (0 = hard commit) |
| `Imbalance:Herding` | A2 | false | — | master: regime common-factor |
| `Imbalance:Herding:FollowerFraction` | A2 | 0.25 | 0..0.5 | `f` |
| `Imbalance:Herding:Tilt` | A2 | 0.10 | 0..0.25 | `δ` (start `f·δ≈0.025`, §4c) |
| `Imbalance:Herding:RegimeFlipProbPerTick` | A2 | 0.001 | →~16min mean | regime sharpness |
| `Imbalance:MomentumDominance` | A3 | false | strength 0..0.3 | trend > reversion under regime |
| `Imbalance:RoleSplit` | A4 | false | — | noise vs directional cohorts |
| `Anchor:FastSlack` | A5 | 0 | 0..1 | how much to relax the intraday band veto |
| `Activity:Enabled` (+ `bot-variable-volume.md` knobs) | B | false | — | the activity field |
| `Range:ActivityImpact` | C1 | false | cap 0.003→~0.02 | activity-scaled slippage/sweep, capped |
| `Range:FatImpactProb` | C3 | false | 0..0.05 | minority deep-sweep orders |

C2 (bid-ask bounce) has no flag here — it's an engine-layer investigation, not a decision-layer toggle.

## 8. Verification & tuning protocol

`scripts/candle_realism.py` is the instrument; it prints every shape metric next to a driftless
random-walk baseline, so "looks like a real market" has a number.

**Per-pillar A/B procedure:**
1. Baseline read with the pillar flag **off**: `python scripts/candle_realism.py --db kse_soak --window-min 180`.
   Record `body/range`, `range CV`, wick %, range~volume r.
2. Flag **on**, soak the same duration, re-read the same window length.
3. Compare to (1) and to the RW column. Tune the pillar's knobs (§4c levers), re-soak, repeat.

**Variance guardrail — reuse the prior balancing tuning (do not undo it):** the earlier advanced-orders
work tuned the market to *low* variance via the value anchor, overheat cap, anti-sweep fraction, slippage
caps, and far walls, and verified it with `scripts/kse-balance-soak.ps1` (a 4h soak sampling
`balance-drift.sql` drift + depth + conservation). **That harness is the variance gate for this work too.**
Run it alongside the candle monitor: the movement pillars must raise candle texture *while* the balance
harness keeps drift inside the §2 budget — `medianAbs_pct ≲ 5`, `beyond50 = 0`, `max_pct` near 20 only as a
rare tail, conservation clean. The two instruments are complementary: the candle monitor says "does it
*move/breathe*," the balance harness says "is it *still bounded*." A pillar passes only if both hold.

**Co-tuning, not bypassing:** the new knobs are tuned *against* the existing balancing knobs, never around
them. A5 (weak fast anchor) is the explicit trade-off dial — relax the anchor only until the balance
harness drift approaches the budget ceiling, then stop. If movement needs more than the budget allows,
shorten regimes (§4c) rather than weakening the anchor further. (Optional: add a `p95_pct` column to
`balance-drift.sql` so the ~5 % typical target reads directly instead of via medianAbs.)

**Directional targets (move toward, not hit exactly):**
| Metric | Today (expected) | RW baseline | Target after A+B+C |
|---|---|---|---|
| body/range | ~0.9 (flat/directional) | ~0.4 | trend toward 0.5–0.7 |
| range CV | low (uniform bars) | moderate | clearly higher (varied bars) |
| has-wick % | low | high | substantially up |
| range~volume r | ~0 (flat volume) | positive | clearly positive |
| flat (H==L) % | high in quiet names | low | down (without starving names) |

**Acceptance per pillar (the gate, §7):** the metric it targets moved the right way, price stayed in the
bands, and `ConservationProbe`=0 / `CK_*`=0 / `ReservationAuditor` in tolerance / tests green. Movement
realism is judged by eye on the chart; the metrics are the corroboration, not the pass/fail.

**Recommended tuning order (matches §4 sequencing):** A1 stance duration `T` → A5 anchor slack → B
activity baseline/`S_max` → A2 `f·δ` + regime flip rate → A3 momentum-dominance strength → C1 impact cap.

## 8b. Runtime observability

The candle monitor is post-hoc; add live telemetry so you can see *why* (or whether) it's moving,
riding the existing structured-metrics pipeline (`InMemoryTelemetrySink` → log viewer):

- **Realized order-flow imbalance** per stock per window — the leading indicator. Today it should read
  ≈ ±0.35 % (the LLN floor, §4c); the whole project is "make this number lump up to ±a-few-percent in
  regimes." This is the single most diagnostic series. *Caveat:* if `Transactions` lacks an aggressor
  side, approximate imbalance from the per-tick batch (count buy- vs sell-initiated orders submitted)
  rather than from settled trades — note which, since they differ.
- **Regime state + follower fraction** (A2): current regime sign, ticks-since-flip, effective `f·δ`.
- **Active-stance census** (A1): how many bots hold a stance and the buy/sell split — confirms inertia
  is engaged and not silently no-op.
- **Activity field** (B): global `G`, top-N hottest `S` (already specced in `bot-variable-volume.md` §1).

These make the A/B in §8 readable in real time instead of waiting for candles to accumulate. They are
read-only counters on state the loop already computes — no hot-path cost beyond a periodic log.

## 9. Resolved decisions & cross-pillar deconfliction

**Decisions (were open, now recommended):**
- **A2 regime: 2-state Markov flip**, not a thresholded continuous factor. Sharper transitions print
  range; it's simpler and matches Kirman's bimodal herding. The smooth continuous version risks
  re-creating the gentle drift we're escaping.
- **A1 stance: per-bot uniform `[MinSec,MaxSec]`, fill does not reset.** A held view persists; expiry on
  `T` is the only end. Simplest, and gives heterogeneous trend lengths for free.
- **C2: defer**, and if pursued, widen MM quoting to a real spread rather than synthesizing one in the
  matcher (less invasive — stays in the quoting bot, not the engine).

**Deconfliction (so pillars don't double-count or fight):**
- **A2 regime vs the smooth sentiment:** *augment, don't replace.* Sentiment keeps the **slow** roles
  (value gap, news shocks); the regime carries the **sharp directional herding**. To avoid double bias,
  **lower `SentimentMaxBias`** (`AiBotDecisionService.cs:31`, currently 0.20) as the regime takes over the
  directional weight — same total force, redistributed to actually create imbalance.
- **A1 inertia vs existing bursts:** orthogonal — bursts modulate *frequency* (how often a bot acts,
  `BurstEndTimes`), inertia modulates *direction* (which side, persistently). Keep separate in v1; a later
  "bot mood" abstraction could unify them, not now.
- **Pillar B activity vs A imbalance:** independent and multiplicative — B sets *how much* volume, A sets
  *which way*. They only couple through C1 (activity → range).
- **A3 momentum vs A2 regime:** A3 is *scoped by* the regime (trend dominates only while a regime holds),
  so they reinforce rather than stack into a permanent one-way drift.

## 10. Files (anticipated)
- New `BotActivityService` (Pillar B). New stance/regime state for Pillar A (in `AiBotContext` +
  `AiBotDecisionService`/`AiTradeService`). Pillar C edits in `AiBotDecisionService` impact helpers.
- `scripts/candle_realism.py` (done). Docs: this file + the two companions.

## 10b. Test strategy & milestones

**Automated tests** (mirror the `ArmStopBatchEquivalenceTests` precedent in `KieshStockExchange.Tests`):
- **Flag-off determinism** (the safety net): with every pillar flag off, the generated order stream for a
  fixed seed is byte-identical to pre-change. This is the contract that makes inert-first real.
- **Imbalance math** (A2): a headless harness with `N` synthetic bots, fraction `f` tilted by `δ`, asserts
  net buy-fraction ≈ `f·2δ` within tolerance — locks the §4c relationship.
- **Inertia persistence** (A1): a bot's stance holds its direction across `T` worth of ticks and only
  flips after expiry; flag-off consumes no extra RNG.
- **Bounding** (A3/C1): under a forced sustained regime, price stays within the anchor/far-wall bands
  (no escape) and `ConservationProbe` stays 0 in a short integration soak.

**Milestones** (each = implement → local soak → `candle_realism.py` read → prod soak → gate §7):
- **M1 — movement:** A1 + A5. Smallest change; confirms the LLN diagnosis on the chart. *Ship-alone-able.*
- **M2 — breathe:** Pillar B activity field (volume varies/clusters).
- **M3 — sharp moves:** A2 + A3 (the herding amplifier). Highest risk; longest soak.
- **M4 — wicks:** C1 (+ C3). Needs M2 live.
- **M5 — balance:** A4 role split + full-system tuning (fix A1's upward bias, §3c).

**Fast local validation loop** (before any prod soak): run the server locally against a small reseed,
flip one flag, let it trade a few minutes, `python scripts/candle_realism.py --db kse_soak --window-min 30`.
Iterate knobs locally; only promote a pillar to a prod soak once the local monitor moves the right way.

## 11. Revision log
- v1: initial consolidated draft (problem, 3 pillars, sequencing, gate).
- v2 (pass 1 — implementability): added §3b feasibility table rating each idea by effort/risk against
  real seams; named A1 inertia as the MVP with a concrete ~30-line mechanism; flagged C2 bid-ask bounce
  as not cheaply implementable (engine-layer change).
- v3 (pass 2 — correctness): added §4b — sell-stance-when-flat upward-bias trap, MM/Arbitrage out of
  scope, draw-order determinism, extreme-reaction precedence, A3's broken zero-sum, C1 cascade reopening.
- v4 (pass 3 — sizing): added §4c imbalance math — `imbalance ≈ f·2δ` is N-independent; `f·δ≈0.025` for
  ~5% pressure; inertia supplies time-correlation; derived the actual tuning levers.
- v5 (pass 4 — stability): rewrote §6 into the slow→fast bounding stack, a failure-mode table with tells,
  and config-only rollback (orthogonal per-pillar flags).
- v6 (pass 5 — verification): rewrote §8 into a per-pillar A/B procedure, directional metric targets vs
  the RW baseline, per-pillar acceptance gate, and a concrete tuning order.
- v7 (pass 6 — A1 spec): added §3c, a build-ready inertia spec (state, decision pseudocode, fill rule,
  sell-when-flat caveat, determinism).
- v8 (pass 7 — flags): added §7b, the consolidated `Bots:` flag registry + default/range table.
- v9 (pass 8 — observability): added §8b runtime telemetry (realized imbalance as the leading indicator,
  regime/stance/activity series).
- v10 (pass 9 — decisions): rewrote §9 — resolved the open questions (2-state regime, fixed stance, defer
  C2) and added cross-pillar deconfliction (augment-not-replace sentiment; lower `SentimentMaxBias`).
- v11 (pass 10 — tests/milestones): added §10b automated tests (flag-off determinism net), the imbalance
  unit test, M1–M5 milestones, and the fast local validation loop.
- v12 (loop-speed): added §5b — cost-vs-realism analysis; the mechanisms live in the cheap ~3% decision
  phase; the real risk is the scaler lowering the bot cap; the per-pillar "worth-it test."
- v13 (magnitude budget): added the hard movement target to §2 (≤~5%/4h typical, ~20% rare) and wired it
  into §4c calibration and §6 as the numeric "escape" definition.
- v14 (balance-harness verification): baked the prior balancing tuning into the gate — reuse
  `kse-balance-soak.ps1`/`balance-drift.sql` as the variance guardrail; co-tune (don't bypass) the existing
  anchor/anti-sweep/slippage knobs; A5 is the explicit trade-off dial. Updated §7 and §8; monitor extended
  to report the 4h-move distribution.
