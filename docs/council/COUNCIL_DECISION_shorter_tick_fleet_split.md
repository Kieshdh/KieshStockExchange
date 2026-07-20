# Council decision — shorter tick + fleet-split (2026-07-08)

Two-round council (3 architects generated the menu `docs/SHORTER_TICK_OPTION_MENU.md`; 5 advisors debated it).
**Verdict: UNANIMOUS 5/5.** Kiesh's proposal: shorter ticks (~250ms) + split the ~20k fleet into N passes;
open Qs — split advanced orders? dynamically choose N? overload → scaler down?

## The decomposition that settles it (all 5 endorse)
Kiesh's goal is **two ORTHOGONAL halves**, and the menu blurred them:
- **Smooth chart = a TIMING / market-data property.** Held by *fixed* period + *fixed* N. The chart already
  renders at 250ms (`QuoteRegistry`); today's 1s tick fires all 20k at once ⇒ 1 burst then ~750ms silence ⇒
  3-of-4 render frames empty = the choppiness. `250ms tick + Slots=4` ⇒ one evenly-spaced burst per 250ms
  frame ⇒ frame-occupancy 1/4 → 4/4. **That is the entire smoothness win — config-only, CK-clean.**
- **Many bots = a CAPACITY property.** Held by the scaler moving the **CAP only**. The tick split adds ZERO
  bots; it only redistributes *when* the existing fleet acts.

**Key: of the three overload actuators, only CAP is smoothness-neutral** — cut the cap → smaller bursts,
still evenly spaced at 4Hz, still smooth. Period-stretch and dynamic-N quietly SPEND the smoothness budget
to buy bot-count. So: *fixed period + fixed N for smoothness; cap-only for capacity.* That orthogonality
also dissolves the hunting problem — with one actuator (cap), there's nothing to hunt.

## ✅ DECISION — ship the config baseline, held fixed
**`TradeInterval = 250ms` + `Bots:Staggering:{Enabled=true, Slots=4}` + `SelfCorrectingDelay` (floored at
250ms, ~2s ceiling backstop) + corrected scaler with CAP-ONLY overload.** All config, mostly already built,
single-threaded, CK-clean, replay intact. `P = tick × N = 1s` ⇒ per-bot cadence unchanged; just 4 evenly-
spaced fill-bursts/sec matching the 250ms drain.

### Direct answers to Kiesh's questions
- **"Shorter ticks?"** Yes — to **250ms, and stop there.** 250ms is the RENDER FLOOR; smoothness *saturates*
  at it. Going lower (125ms/Slots=8) piles multiple bursts into one 250ms frame = **zero extra smoothness**
  while multiplying the 20k scan (the cost that would then "require" the risky C1). "As low as possible" is
  a non-goal below the render period.
- **"Dynamically choose N (scaler)?"** **No.** N is part of the *smoothness contract* (it sets the burst
  pattern) — changing it under load = transient unevenness. And dynamic-N is *inert* until C1 (below), since
  the O(20k) scan floor is unchanged by N. Keep N a fixed config number.
- **"Split advanced orders?"** **No, for this goal.** Splitting advanced for PERF is a docker ghost (adv is
  cheap on prod; the by-count split already sheds fleet load). The *only* durable reason to split advanced
  is stop-trigger latency / crisper stop cascades — a **separate realism ticket** (B2), judged on its own
  merit, never bundled into the smoothness/capacity decision.
- **"If it can't handle the load, the scaler goes down?"** **Yes — exactly right.** Cap-down is the
  physically-correct, smoothness-neutral overload response. Give TIME first (SelfCorrectingDelay absorbs a
  transient spike, invisible on the chart), then BOTS (cap-down) for sustained overload. Never granularity.

## ❌ REJECTED (all 5)
- **A1 period-STRETCH under load** — the SelfCorrectingDelay *floor* is smoothness-good (de-idles, pins
  250ms when under-loaded); its *stretch* under overload is a load-dependent gap-variance leak (a milder
  "back-to-back = choppier"). Keep the floor, refuse the stretch.
- **A2 dynamic-N** — part of the smoothness contract + inert pre-C1.
- **A3 priority-ladder / A4 cascaded controller** — anti-hunting machinery for a hunting problem you only
  create by adding actuators. One actuator (cap) ⇒ the whole control-theory scaffold evaporates.
- **B3 multi-rate scheduler** — gold-plating; refactors the loop spine for flexibility prod won't use, and
  C1's slot index already leaves per-book cadence reachable later.
- **B2 for this goal** — a separate stop-latency/realism project.

## ⏸ DEFER — C1 (the scan-floor-breaker) behind a pre-committed PROD trigger
C1 = index bots by slot so the loop iterates only the ~`cap/N` DUE set (O(cap/N), not O(20k)). It's the
**one structural move that decouples tick length from fleet size** — iterations/sec becomes `fleet / cadence`,
invariant to tick length. Genuinely the "real prize" for capacity. BUT:
- It only matters if you push the tick *below* 250ms — which the council says you shouldn't (no smoothness
  ROI). So the max scan you ever face is 4×/sec, and the old profile (collect ~4-18ms ⇒ 16-72ms/s) says
  that's affordable. **The floor probably never binds.**
- It touches the per-tick-per-bot RNG bookkeeping contract = determinism-sensitive (a crown jewel).
- **So: defer with a pre-committed trigger** — build ONLY if a PROD profile shows the 20k scan at 4×/sec
  actually eats a material fraction of the 250ms tick. If built: as ONE package with dynamic-N, gated by a
  hard replay-equivalence test. Cheap optional insurance now: a one-page design spike on the RNG-stream-
  preservation (if it can't be preserved cheaply, the whole richer branch is dead — good to know early).

## The plan (Executor's sequence — one flag per soak, CK=0 before eyeball)
0. **Snapshot current prod config** (tick / slots / scaler flags / collect-span / trades/sec) — the baseline row.
1. **Baseline:** `250ms + Slots=4`. Judge: CK=0 (2h) → gap-variance DOWN + frame-occupancy UP → 15s chart.
2. **De-idle:** `SelfCorrectingDelay` (250ms floor + ~2s ceiling). Judge: trades/sec UP without gap-variance up.
3. **★ Prod measurement gate:** the collect/scan span at 250ms + commit-frequency (4× smaller batches). *This
   one number decides everything downstream.* Docker inadmissible.
4. **STOP (expected):** scan cheap ⇒ baseline is the whole answer.
   - Only if the *actionable* span (not the scan) saturates ⇒ consider A2 dynamic-N (small code).
   - Only if the *20k scan* binds ⇒ build C1 (+ dynamic-N, replay test). Probably never.

## Metrics (mapped to goals)
- **Smoothness** = inter-trade **gap VARIANCE on the RAW fill stream** (not candles — the 250ms drain hides
  sub-frame burstiness) + **frame-occupancy**. (trades/sec is NOT a smoothness metric — bunched is still choppy.)
- **Capacity** = trades/sec + **max bot-count at CK=0**.
- **CK=0 over a 2h soak gates every tick change BEFORE any eyeball** (a torn-read leak won't wobble the chart).
- **Validate on PROD** — local docker inflates the per-statement costs that decide the scan floor.

## One-liner
The smooth chart is already bought by a config flip (`250ms + Slots=4`, held fixed); "many bots" is the
cap-only scaler; everything richer on the menu is a capacity knob wearing a smoothness costume. C1 is the
real structural prize *and* premature — design it, build it only if a prod number proves the floor binds.
