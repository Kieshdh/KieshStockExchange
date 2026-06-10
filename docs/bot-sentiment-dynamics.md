# Plan: sentiment-driven directional dynamics (slope-aware, phase-differentiated)

**Status:** design for Ultraplan. Goal: make **sentiment actually move the chart** — realistic up/down
**trends with reversals (boom-bust)** — instead of being faded into nothing. Builds on the shipped
market-realism v2 + down-drift fix (branch `feature/bot-market-realism-v2`). Land flag-gated, inert-first;
bounded by the existing value anchor + cash controller; verified against the ≤5%/4h magnitude budget.

> Guiding principle: real price dynamics come from traders reacting to sentiment at **different phases**
> (momentum leads, late money tops it out, contrarians fade the extreme). Encode *that*, not a stronger
> uniform bias. The system must self-correct (boom → bust), never one-way drift.

## 1. Problem (measured)
Sentiment is inert: a realistic ±0.6 lean shifts bot buy% by ~1pp and price by ~0 (swamped by noise),
because the extreme-reaction population is **~2:1 fade-heavy** (MeanReversion **and** MarketMaker both →
Contrarian; Scalper neutralized by the Panic/Greed split; only TrendFollower follows). So sentiment gets
*faded* harder than *followed* → no trend. Also: everything keys off the sentiment **level** only; the
**rate of change** (rising vs rolling over) is unused.

## 1b. Empirical findings (overnight soaks — context to design against)
Measured on `feature/bot-market-realism-v2` over many soaks (incl. a 4h + parallel 2h A/Bs). Treat these as
ground truth — and **use them to make the best decisions, including improving on this plan's specific
proposals where the data points to a better approach.** This plan is a well-reasoned starting point, not a
spec to follow blindly; the goal (sentiment-driven trends + reversals, bounded, conservation-safe) and the
hard constraints are fixed, but the *how* is open to Ultraplan's judgment informed by this context.

**Motivation / baseline** (→ §1, §7)
- **Sentiment is anchor-dominated ~20:1.** A ±0.6 sentiment lean shifted bot buy% only ~0.2–1.2pp and price
  ~0; the value anchor moves buy% ~22pp on a 43% price deviation. That's *why* sentiment is inert.
- **Sentiment is balanced** (global avg −0.006, news shocks net +0.08) — the inertness is the fade/level
  design, NOT a sentiment-direction bias.
- The shipped config is **realistic at steady state** (body/range 0.653 = RW, wick 0.347 = RW). The new
  trends must be added *on top* without breaking this shape.

**The reversion floor is validated** (→ §5, §9.3)
- Value anchor + cash controller (`CashHomeostasis:Continuous`, `MaxShift 0.45`) bound drift **~3×** (from
  −6.7%/3h unfixed → medianAbs ~3%/2.5h, sub-linear, within budget) **without over-damping** (shape stayed
  ≈ RW). Momentum can lean on this; co-tune `MomentumConviction` against `MaxShift 0.45`.

**Taker-flow asymmetry** (→ §9.5)
- Aggressive **sell volume exceeds buy by ~47%** (long-heavy fleet: buys rest as limits, sells take).
  Momentum that buys via *limits* won't move price up — it must **take** liquidity. The aggression-balance
  is a co-requisite of trending.

**Conservation precedent** (→ constraints)
- All decision-layer soaks held **CK=0 / CONS=0 / beyond50=0** over 230k+ trades / 4h → decision-layer
  changes have been conservation-safe; the new design should hold the same.
- **Warning:** heavy volume concentration (`Activity:Gamma 1.4`) produced **CK=4** → if coupling
  `|ds|→activity` (§9.7), don't over-concentrate.

**Measurement methodology** (→ §7, §10 — so the verification is sound)
- Fresh-reseed **startup transient** dominates short soaks (low-id Calm stocks show +30–40% artifacts) →
  measure **tail windows** past startup.
- **Cross-process A/B diverges** under contention (unpinned control stock differed −7% vs −15%) → prefer
  **within-run / decision-level** metrics (buy%) over cross-process price comparison.
- Drift creeps **sub-linearly** → use **multi-hour** soaks (the 3h smoke caught what 18–35-min runs hid).

## 2. Core idea
Two changes, together:
1. **Use sentiment slope, not just level.** Add a smoothed `ds = d(sentiment)/dt` and let strategies react
   to `(level s, slope ds)`.
2. **Phase-differentiate strategies** so they enter at different points of the sentiment cycle, creating
   momentum → blow-off → reversion:
   - **Scalper** — fast momentum: `ds>0` buy, `ds<0` sell; quick exits (TP).
   - **TrendFollower** — mid momentum: follow `ds` while it persists.
   - **FOMO (late)** — buy when `s` high but `ds` rolling over (buys the top); seeds the reversal.
   - **MeanReversion/Contrarian** — fade the extreme + the reversal (sell high as `ds` turns down).
   - **MarketMaker** — neutral liquidity, mild lean against extremes.
   - **Random** — noise.
3. **Non-even population ratios** tuned so momentum can build a trend but reverters + anchor reliably end it.

## 3. The sentiment slope (EWMA)
Per `Tick`, compute the raw per-stock change `raw = (s_now − s_prev)/dt`, then maintain an **EWMA of the
slope**: `ds = α·raw + (1−α)·ds_prev`. EWMA (not a windowed diff) because it's O(1)/stateless-per-stock,
matches the rest of the engine, and its time-constant is a single tunable. Details:
- **α from a time-constant**, frame-rate independent: `α = 1 − exp(−Δt/τ_slope)`, so the smoothing horizon
  is `τ_slope` seconds regardless of tick jitter. Start `τ_slope ≈ 60–120 s` (long enough to kill the 20 s
  fast-ring chatter, short enough to catch a real turn).
- **"Rolling over"** = `s` still high while `ds` has crossed below a small band (was rising, now flat/falling)
  — this is the FOMO/Contrarian trigger; needs `ds` clean, hence the EWMA.
- Expose `GetSentimentSlope(stockId)` (the EWMA `ds`); reset with `Reset(now)`; advance only inside `Tick`
  on the loop thread (same contract as the sentiment itself). Sign = trend direction, magnitude = conviction.
- Optionally a **two-timescale** slope (a fast EWMA for Scalper, a slow one for FOMO/Contrarian) if one
  τ can't serve both the twitchy and the patient strategies — decide in the per-strategy sweep.

## 4. Per-strategy phase model
Each strategy maps `(s, ds)` to a directional bias added to `buyProb` (and to the market-vs-limit/aggression
choice). Use a **fast slope `ds_f`** (τ≈30 s) for Scalper and a **slow slope `ds_s`** (τ≈120 s) for the
patient strategies. Bias is signed in [−k, +k] per strategy (k = that strategy's conviction weight):

| Strategy | Directional bias | Aggression |
|---|---|---|
| **Scalper** | `+k·tanh(ds_f/σ)` — ride the fast slope; flips fast on reversal | market (taker), small size, quick TP exit |
| **TrendFollower** | `+k·tanh(ds_s/σ)` — ride the persistent slope | mixed |
| **FOMO (late)** | buy when `s>θ_hi AND ds_s` has fallen below `δ` after being positive (rolled over); mirror at troughs. Per-bot **lateness `L`** sets `θ_hi`/the roll-over delay → spread entries | market (chases) |
| **MeanReversion** | `−k·clamp(s)` plus extra `−k₂` when `s` extreme & `ds_s` turning down (fade the peak) | limit (provides) |
| **MarketMaker** | small `−k·clamp(s)` (gentle lean) | limit, two-sided |
| **Random** | 0 | as today |

The **cycle** this produces: `ds>0` → Scalper then TrendFollower buy (momentum builds) → `s` high, `ds_s`
slowing → **FOMO piles in late (blow-off top)** while **MeanReversion starts selling the extreme** → `ds<0`
→ momentum flips to sell, FOMO/late buyers underwater → MeanReversion buys the dip → trough → recovery.
Per-bot lateness `L` (and the conviction `k` jitter) **desynchronize** entries so it's a smooth wave, not a
step. This is the directional engine; it **replaces the level-only `ApplyExtremeReaction` overflow path**
(or subsumes it — see §9).

## 5. Stability / feedback (the make-or-break)
Two loops: **momentum** (price up → `ds>0` → momentum buys → price up) is *positive* feedback
(destabilizing); **reversion** (Contrarian fade + FOMO-late-buyers-trapped + the value anchor + cash
controller) is *negative* feedback (stabilizing). The behaviour is set by the **effective loop gain G** ≈
(momentum conviction × follower share) ÷ (reversion conviction × fader share + anchor strength):
- `G < 1` (reversion wins): mean-reverting chop, weak trends (today-ish).
- `G ≈ 1` (near-critical): **realistic trends, reversals, fat tails** — the target band, like the v2
  activity field's near-critical Hawkes.
- `G > 1` (momentum wins): runaway / violent oscillation — forbidden.

**Failure modes & guards:**
- *Runaway* → the **value anchor + cash controller are the hard ceiling** (price can't escape the band even
  at G slightly >1); back off momentum conviction / FOMO share if `beyond50>0` or drift exceeds budget.
- *Oscillation/whipsaw* → slope τ too short or Scalper conviction too high; lengthen τ_slope, cap Scalper size.
- *One-way drift* → asymmetry in the bias (must be **symmetric**: bull-buy magnitude == bear-sell magnitude)
  AND no residual taker-aggression skew (the down-drift fix's concern — co-tune).
**Tuning order:** set reversion + anchor first (the floor), raise momentum conviction until trends appear,
stop before `G≈1` breaches the ≤5%/4h budget. The budget is the objective function.

## 6. Population ratios & the FOMO question
**FOMO is best modelled as a per-bot *lateness* parameter `L`, not a separate strategy.** The momentum
cohort (TrendFollower, and Scalper for the fast version) carries a *distribution* of `L`: low `L` = early
momentum (reacts to `ds` rising), high `L` = late/FOMO (reacts only once `s` is high and `ds` rolls over).
This gives a **continuum of entry timing** → the desynchronized wave, with no new enum value or schema row
(just one seeded knob). The "FOMO %" below is the high-`L` tail of the momentum cohort.

Starting ratios (tune to land loop gain `G≈1`, §5):
| Cohort | share | role |
|---|---|---|
| Momentum: TrendFollower (`L` spread incl. ~10% high-`L`/FOMO) | ~35% | builds + tops the trend |
| Scalper (fast momentum, quick TP) | ~12% | leads, adds turnover |
| MeanReversion (fade extremes/reversals) | ~20% | ends the boom |
| MarketMaker (gentle fade, liquidity) | ~13% | liquidity floor |
| Random (noise) | ~20% | entropy/liquidity |

Net follow-leaning *during* a move, reversion-heavy *at extremes*. Ratios are a **seed/`/Tools` change**
(Person.py strategy assignment + the `L` draw), not config — so they regenerate the xlsx (§9).

## 7. Realism targets & verification
What "working" means, and how to prove it:
- **Sentiment→price gain is now clearly non-zero** — re-run the `Bots:Sentiment:Offset` A/B (the test hook,
  re-added temporarily for validation only): a +0.6 lean should now produce a *visible* up-trend on the
  pinned stock vs a −0.6 lean down, well above the ~1pp baseline. This is the headline before/after.
- **Trends *and* reversals (boom-bust)** — eyeball `candle_plot.py`: sustained directional runs that *turn*
  and revert, not a flat staircase and not a one-way ramp. Cross-check the price series leads/lags the
  sentiment series (momentum follows `ds`, FOMO tops lag the sentiment peak).
- **Stylized facts** — fat-tailed returns (tail index 2–5), slow autocorrelation of |returns| (vol
  clustering), positive volume–volatility coupling, return autocorr ≈ 0 beyond minutes. `candle_realism.py`
  shape stays ≈ RW.
- **Bounded** — `balance-drift.sql`: `beyond50=0`, drift within ≤5%/4h, conservation clean (`CK=0`/`CONS=0`)
  over a multi-hour soak. The budget is the hard gate; trends must revert, not accumulate.
- **Telemetry** — log/expose per-stock `ds` and the realized sentiment→price gain so the loop gain `G` is
  observable and tunable live (sweep §8).
Tooling exists: `candle_realism.py`, `candle_plot.py`, `kse-balance-soak-p.ps1` (parallel A/B),
`sentiment-check.sql`.

## 8. Implementation — code seams & data flow
- **Slope (BotSentimentService.cs):** in `Tick`, before overwriting `_combined`, read `s_prev`; compute
  `raw=(s_now−s_prev)/dt`; update per-stock EWMA(s) — keep **two** (`_dsFast` τ≈30s, `_dsSlow` τ≈120s) in
  dicts mirroring `_combined`. Add `GetSentimentSlope(stockId, fast)` accessors; clear in `Reset`. No new
  RNG; loop-thread only; O(1) per stock.
- **Per-strategy directional bias (AiBotDecisionService.ChooseOrderType ~:491+):** add a
  `DirectionalBias(user, s, dsFast, dsSlow)` helper returning the signed `buyProb` shift per the §4 table,
  keyed off `user.Strategy` + the per-bot lateness `L`. It **replaces/absorbs the old level-only momentum
  block + `ApplyExtremeReaction`** (§9). Aggression (market vs limit) per the §4 table feeds the same
  `effectiveUseMarket` seam — co-located with the down-drift aggression fix.
- **Lateness `L` (AIUser):** a per-bot field (like `BuyBiasPrc`), seeded with a distribution over the
  momentum cohort; default mid so un-reseeded bots behave sanely. Wired through `AIUserRow`/mapper/PgDBService
  /EF — a real schema add (migration), per the `/Tools` §5 pattern.
- **Config/flags:** `Bots:SentimentDynamics:Enabled` (master, inert default → today's behaviour), plus
  `:MomentumConviction`, `:ReversionConviction`, `:ScalperConviction`, `:FomoThreshold`, `:SlopeTauFast/Slow`,
  `:ScalperTP`. All read in `AiTradeService` ctor like the v2 flags.
- **Determinism:** the bias is a deterministic function of `(s, ds, L, strategy)` — no new RNG draws; the
  flag-off path is byte-identical. `ds` advances once per `Tick`.
- **Telemetry:** extend the sentiment log with per-stock `dsSlow`; add a realized sentiment→price-gain /
  loop-gain `G` gauge (sweep §7) for live tuning.

## 9. Model-wide ripple effects (must be handled together)
This is a **decision-layer redesign**, not an add-on — several existing pieces overlap and must be
reconciled or they'll fight it:
1. **Extreme-reaction taxonomy is superseded.** The `FOMO/Contrarian/Panic/Greed` styles + `BullDirection`/
   `BearDirection` + `PickExtremeReactionStyle` + the `GreedStyle` down-drift fix were a *level-overflow*
   directional hack. The new `(s, ds)` phase model **replaces** them (FOMO/Contrarian/momentum become the
   strategy-phase behaviour). Decide: delete the old path, or keep it behind the old flag for fallback.
2. **Old momentum + sentiment-bias blocks go.** `ComputeWatchlistMomentum` ±0.175 and the
   `sentimentClamped·SentimentMaxBias` term in `ChooseOrderType` are replaced by `DirectionalBias(s,ds,L)`.
   `SentimentMaxBias` likely retires (or becomes the momentum conviction).
3. **Value anchor must be re-tuned** — it's now the primary *reversion floor* bounding the new momentum
   trends. Too weak → runaway; too strong → trends can't form. Co-tune with `MomentumConviction` to `G≈1`.
4. **Herding regime (A2) overlaps.** The regime injects a shared directional sentiment-like swing; the new
   slope-momentum will *amplify* it. Decide: keep the regime as the macro driver (momentum rides it) and
   reduce its tilt, or let sentiment dynamics be the driver and dial the regime down — avoid double-driving.
5. **Down-drift fix interplay.** The **recentered seed + cash controller stay** (the bound); the
   **GreedStyle/extreme-reaction balance is replaced** (point 1); the **aggression (taker-flow) balance
   still matters** and co-tunes (momentum that buys via limits won't move price — momentum likely needs to
   *take* liquidity to trend, which interacts with the buy-passive/sell-aggressive fix).
6. **Scalper TP exits** need the existing **bracket/advanced-order** path (arming TPs on entry) — integrate,
   don't reinvent; mind the advanced-order batching/perf (the A1a work).
7. **Activity field (B)** could optionally take `|ds|` as a driver (big slope → more volume) — strengthens
   volume–volatility coupling (the open r≈0.19 gap); optional, flag-gated.
8. **Personal sentiment** (per-bot idiosyncratic) still adds; confirm it doesn't swamp/clash with `ds`.
9. **Seed/`/Tools`:** new strategy *ratios* + the per-bot **lateness `L`** column → Person.py/Config.py/
   ExcelLayout + `AIUserRow`/mapper/PgDBService/EF **migration** + xlsx regen (the §5 seeding chain).
10. **Re-validate the magnitude budget + loop perf** (the decision layer is hotter now: two EWMAs/stock +
    a richer per-bot bias; should still be cheap, but check the scaler cap, per the "worth-it test").

## 10. Rollout / flags / tests / migration
- **Pre-work (before Ultraplan):** remove the uncommitted **`Bots:Sentiment:Offset` test hook**
  (BotSentimentService + AiTradeService) so Ultraplan starts from a clean branch. (It can be re-added
  temporarily for the §7 validation A/B, then removed before merge.)
- **Inert-first:** `Bots:SentimentDynamics:Enabled=false` default → byte-identical to today (old momentum/
  extreme-reaction path intact behind the flag for fallback). The `L` column defaults mid (sane un-reseeded).
- **Migration:** EF migration for the new `AIUser.Lateness` column; xlsx regenerated with the new strategy
  ratios + `L` distribution (disposable DB → nuke+reseed is fine).
- **Tests** (mirror the existing `*Tests` precedent):
  - EWMA slope: rising/falling/flat series produce correct `ds` sign + smoothing; `Reset` clears.
  - DirectionalBias: each strategy's `(s,ds,L)` → expected sign (Scalper follows `ds`, FOMO buys high-`s`/
    rolled-over, Contrarian fades extreme) at representative inputs.
  - Flag-off determinism: identical order stream vs pre-change for a fixed seed (no new RNG).
- **Verification soak** (`kse-balance-soak-p.ps1`): flag-off vs on; re-run the `Sentiment:Offset` A/B
  (sentiment→price gain now visible); `candle_realism.py` (trends + reversals, shape ≈ RW, vol clustering);
  `balance-drift.sql` (≤5%/4h, beyond50=0, conservation clean). Tune `G≈1` (§5), then bake the flag on +
  the seed ratios; confirm scaler cap (perf).

## 11. For Ultraplan
**Refine this design again, then implement it** — use the §1b empirical findings as decision context and
feel free to improve on the proposals below where the data suggests something better (goal + hard
constraints fixed; the *how* is yours). Specifically:
- Critique/lock the open choices: one vs two slope timescales; FOMO-lateness `L` curve; the exact per-strategy
  `(s,ds)` response & conviction weights; which old paths to delete vs keep-behind-flag.
- **Handle the model-wide ripple (§9) as one coherent change** — the extreme-reaction taxonomy, old momentum/
  sentiment blocks, value-anchor re-tune, herding-regime de-confliction, the down-drift-fix interplay,
  Scalper-TP integration, the seed ratios + `L` migration. Don't ship the directional engine while the old
  level-overflow path still double-acts.
- Keep it **flag-gated/inert-first, deterministic, conservation-safe, within the ≤5%/4h budget**; co-tune so
  loop gain `G≈1` (trends + reversals, not runaway).
- **Deliverable:** a `git am`-compatible **patch series + bundle** against `feature/bot-market-realism-v2`
  (current tip), with a cover note (design decisions, flags + defaults, the `/Tools`+migration changes, test
  results, and the recommended soak/tuning order). Local Claude builds, tests, and soaks it.

## 12. Revision log
- v1: initial draft (core idea, slope, phase model, stability, ratios, verification).
- Conceptual sweep 1 (slope): locked **EWMA** slope with τ-based α (frame-rate independent), defined
  "rolling over," optional fast/slow two-timescale.
- Conceptual sweep 2 (phase model): precise per-strategy `f(s,ds)` table + aggression; FOMO as a per-bot
  **lateness `L`**; the boom-bust cycle mechanics; replaces the level-only `ApplyExtremeReaction`.
- Conceptual sweep 3 (stability): the **loop gain `G`** framing (G≈1 target), failure modes (runaway/
  oscillation/drift) + guards, tuning order (reversion first, raise momentum to G≈1).
- Conceptual sweep 4 (ratios): FOMO = lateness param (not a strategy); non-even cohort ratios shaped to G≈1;
  ratios are a seed/`/Tools` change.
- Conceptual sweep 5 (realism): sentiment→price-gain headline metric, trends+reversals + stylized-fact
  targets, bounded-budget gate, telemetry for live `G`.
- Impl sweep 1 (seams): slope in BotSentimentService (two EWMAs), DirectionalBias in ChooseOrderType,
  Lateness on AIUser, config flags, determinism, telemetry.
- Impl sweep 2 (ripple §9): enumerated everything else that must change together (extreme-reaction taxonomy
  superseded, old momentum/sentiment blocks retired, anchor re-tune, regime de-confliction, down-drift-fix
  interplay, Scalper-TP, activity coupling, seed ratios + `L` migration, perf re-check).
- Impl sweep 3 (rollout): pre-work test-hook removal, inert-first flags, migration, tests, verification soak;
  added §11 For Ultraplan (refine + implement the whole ripple coherently; patch-series deliverable).
- Empirical findings: added §1b (A–E from the overnight soaks) as decision context, framed so Ultraplan
  uses them to make the best calls and may improve on the plan's specifics (goal + hard constraints fixed).
