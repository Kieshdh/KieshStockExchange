# Ultraplan handoff — Reaction / Persistence split (bot decision layer)

**Goal in one line:** decouple *how fast a bot reacts to new information* (should be fast — seconds) from *how long its
directional order-flow pressure persists* (should be minutes), so the fleet reacts realistically fast **without** losing the
trend-persistence and cross-stock correlation that today's slow "Inertia" accidentally provides.

Default-OFF, byte-identical when disabled. Bot-decision layer only. CK-safe (rides OrderEntry→Match→Settle). Keep MaxBotCap=20k
(perf-neutral). This is a design+implementation handoff; **await Kiesh's approval before implementing.**

---

## 1. Why (the finding that motivates this)

Cross-stock correlation and the over-negative 1-min return autocorrelation are both governed by one accidental knob:
`Bots:Imbalance:Inertia` (per-bot reaction lag, currently **120s–1800s = 2–30 min**). A 5-advisor council + peer review + a
config A/B (2026-07-03) established:

- **The council's peer-review prize (the absorption layer):** in this engine, buyProb / sentiment / stance tilts **rest as limit
  orders and get absorbed** — *only TAKER FLOW moves price* (the arc's 19-experiment finding). So any reaction/persistence change
  only affects correlation/price if it acts through **taker flow**, not a buyProb tilt.
- **Config A/B refuted "just make bots faster":** near-instant reaction (`Inertia 2s/300`) vs current (`120/1800`), both with the
  shared-taker chase, **lowered** correlation at every horizon (5-min 0.073→0.024, 10-min 0.173→0.029, factorR2 0.31→0.20) and
  **shrank the moves** (dispersion std 5.11→3.10, range ±25/−18 → ±12). CK=0, no runaway. Mechanism: fast bots mean-revert their
  OWN and the shared move faster → they damp the shared push before co-movement accumulates and kill trend persistence.
- **Root cause (First-Principles + Expansionist):** today's `Inertia` **conflates two independent clocks** into one parameter.
  It implements persistence as *blindness* ("refuse to look at the world for 2–30 min"), so you cannot have fast reaction and
  persistence at the same time. The current slow lag already yields decent correlation **at the 5–10-min horizon** (0.07 / 0.17)
  precisely because the slow bots HOLD their stance while a shared push accumulates — but they're also unrealistically blind.

**The fix (Kiesh chose this):** split the two clocks. Fast reaction (bots see news in seconds) + a *separate* persistence
mechanism (order-flow pressure decays over minutes) that carries trends and correlation — and that drives **taker** flow so it
isn't absorbed.

---

## 2. Current mechanism (what we replace)

`AiBotContext.cs:255` — `RollOrHoldStance(aiUserId, buyProb, now, minSec, maxSec)`:
```
if (Stances.TryGetValue(aiUserId, out var st) && now < st.until) return st.dir;   // BLINDNESS: stale dir, ignores buyProb
sbyte dir = Decimal01(aiUserId) < buyProb ? 1 : -1;                                // reroll side
double secs = lo + (hi-lo)*Decimal01(aiUserId);                                    // reroll hold duration in [MinSec,MaxSec]
Stances[aiUserId] = (dir, now + secs);
```
State: `AiBotContext.cs:69` — `Dictionary<int,(sbyte dir, DateTime until)> Stances`.

Applied at `AiBotDecisionService.cs` (~L1351): when `_inertia && notMM`, `buyProb` is clamped to `1−Leak` (held-buy) or `Leak`
(held-sell). Config: `Bots:Imbalance:Inertia` (+ `:MinSec`,`:MaxSec`,`:Leak`), currently `true / 120 / 1800 / 0.1`.

The problem: direction is a **step function** frozen for minutes = blindness. Short hold = fast reaction but no persistence; long
hold = persistence but blind. One knob, two jobs.

---

## 3. The redesign — two clocks, one continuous pressure state

Replace the frozen-direction step with a **continuous per-bot pressure** that (a) updates every tick from a fresh signal (fast
reaction, no blindness) and (b) decays slowly (persistence). Then couple |pressure| to **taker** propensity (un-absorbed).

**Per tick, per bot:**
```
fresh   = reactionSignal(bot, now)                      // fast: current directional conviction in [-1,+1]
                                                         //   (buyProb→2·buyProb−1, or the sentiment/momentum slope the bot reads)
pressure[bot] = ρ · pressure[bot] + (1-ρ) · fresh       // AR(1): ρ = exp(-Δt·ln2 / HalfLifePersistSec)
```
- **Reaction clock (fast):** `fresh` is recomputed every tick from current info — the bot is never blind. Optionally smooth it with
  a SHORT EWMA `τ_react` so reaction is fast-but-not-twitchy. **Kiesh constraint: τ_react must be SLOWER than the 2s near-instant
  test that flattened the market** — start ~15–30s (moderate-fast), tunable. The persistence mechanism, NOT near-instant reaction,
  carries trends.
- **Persistence clock (slow):** `HalfLifePersistSec` per-bot, ~5–20 min (draw from `[PersistMinSec, PersistMaxSec]` by AiUserId
  hash). This is the memory that makes order-flow pressure persist across minutes even though the bot re-decides fast. THIS defeats
  LLN — persistent *pressure*, not stale *opinion*.
- **Act on `pressure`:** map `pressure` → the directional term the bot already uses (replace the `buyProb` clamp). Sign = order
  side; |pressure| = conviction.

**Taker coupling (the absorption fix — essential):** when `|pressure| ≥ TakerThreshold`, with prob `clamp(TakerGain·|pressure|)`
override the order to a **slippage-market taker** in the pressure direction (reuse the existing `TrendFollower:TakerCoupling` /
shared-taker-chase primitive in AiBotDecisionService). Persistent pressure that only tilts buyProb is absorbed as limit orders;
persistent pressure that **crosses the spread** moves price. This is what finally lets a shared signal create correlation: a shared
mood → persistent shared pressure → **sustained shared taker flow** (un-absorbed) → cross-stock co-movement at the horizon
persistence produces; and the fast reaction picks the shared mood up quickly.

**Why this fixes all three at once (Expansionist):** correlation = shared pressure persisting into shared taker flow; trends =
per-bot pressure persistence; ret_acf toward 0 = the fast-reaction / slow-persistence interaction (fast bots overshoot, the
persistent pool fades it back over minutes instead of snapping it back in the same minute).

---

## 4. Levers (all default-off, byte-identical when off)

New block, e.g. `Bots:Imbalance:ReactionPersistence` (SUPERSEDES the `Inertia` buyProb-clamp path when Enabled — same pattern as
`PerceivedPriceDesync` superseding `DirectionalReactionLag`; when OFF, `Inertia` path runs unchanged = byte-identical):
- `Enabled` (false)
- `ReactionTauSec` (~20; the fast reaction EWMA — **must stay > the 2s that flattened**, per Kiesh)
- `PersistMinSec` / `PersistMaxSec` (~300 / ~1200 = 5–20 min persistence half-life spread; per-bot by AiUserId hash)
- `TakerCoupling` (false) / `TakerThreshold` (~0.15) / `TakerGain` (~1.0) — the un-absorbed channel
- (optional) `ReactionShape` — fat-tailed/log-normal skew of the reaction distribution (most fast, a slow tail); a later refinement.

Keep `Inertia` in place as the OFF path / rollback. Do NOT delete it in this change.

---

## 5. Implementation notes

- **State:** add `Dictionary<int, decimal> Pressures` (+ last-update tick for Δt) to `AiBotContext`, mirroring `Stances`. Pure/RNG-
  light (persistence is deterministic decay; taker override uses the existing `Decimal01(aiUserId)` draw). No wall-clock beyond the
  tick timestamp already threaded in.
- **Injection point:** `AiBotDecisionService.cs` ~L1351 — where `_inertia` currently clamps `buyProb`. When ReactionPersistence is
  on, compute `pressure`, set the directional term from it, and (if TakerCoupling) apply the market-order override AFTER the
  isBuy/isMarket draws (same spot the shared-taker chase overrides, ~L1349-1393).
- **Byte-identical off:** Enabled=false ⇒ no `Pressures` read/write, no extra draw, `Inertia` path untouched. Add a determinism
  test (off = identical order stream) like the other levers.
- **Conservation:** taker overrides ride OrderEntry→Match→Settle with the existing depth-cap + value-band veto (no naked flow).
- **Perf:** one dict lookup + one AR(1) update per bot per tick = cheap; MaxBotCap stays 20k. No extra DB round-trips.
- **Do NOT** co-enable with `Inertia`'s buyProb clamp (ReactionPersistence supersedes it) or double-drive with the standalone
  shared-taker chase (fold the shared component into `fresh` instead, or gate one).

---

## 6. Validation plan (A/B soaks, this repo's harness)

Baseline to beat = the current slow-`Inertia` config (this session): 1/5/10-min corr **0.014 / 0.073 / 0.173**, dispersion std
**5.11**, ret_acf **−0.285**, CK=0. Arms (parallel, same seed, 45-min; escalate a winner to 2h because the sim is real-time and
5–10-min correlation is sample-starved at 45m):
- Control: `Inertia 120/1800` (current) + shared-taker chase.
- Target: `ReactionPersistence` on (ReactionTau ~20, Persist 300/1200, TakerCoupling on) — Inertia off.

**PASS gate:** (1) correlation ≥ control at 5–10-min (ideally lifts 1-min above ~0) — measure `cross_stock_diag.py --horizons
1,5,10`; (2) **reaction is genuinely fast** (bots respond to a shock within seconds — realism win); (3) **moves still stick**
(dispersion not collapsed vs control; range-eff healthy) = the FLATTEN-GUARD; (4) ret_acf toward 0 (not through into positive
runaway); (5) **CK=0** (hard gate); (6) drift in band, book not collapsed, throughput ≥ ~5k/min. **Kill at 10-min** if ret_acf goes
positive or any stock runs to the ×3 band (positive-feedback). Ladder persistence + taker gain up a geometric rung at a time.

**What NOT to do:** don't lower reaction lag via config alone (refuted — flattens). Don't route persistence through buyProb only
(absorbed). Don't touch `/Tools` / reseed for this (runtime config + code only). Don't ship default-on; Kiesh bakes after A/B.

## 7. Deliverables
1. Patch (default-off, byte-identical) implementing §3–§5 + a determinism test + a small unit test on the AR(1)/taker mapping.
2. `dotnet build` clean + full test suite green.
3. An A/B results note (`docs/REACTION_PERSISTENCE_SPLIT_RESULTS.md`) against the §6 gate.
4. Recommendation: bake dose (ReactionTau / Persist / TakerGain) or iterate — Kiesh's bake call.
