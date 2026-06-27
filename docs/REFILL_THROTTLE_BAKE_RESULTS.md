# Refill-throttle bake results — NO-BAKE (the realism ceiling is structural)

**Date:** 2026-06-27. **Branch:** `feature/bot-market-realism-v2`. **Verdict:** refill-throttle **NO-BAKE**,
committed default-off as a tool (`4718725` + probe-drain `ac48171`). This doc closes the bot-market-realism arc.

## The lever
`Bots:RefillThrottle` (council's "refill RESPONSE" fix for the book-refill wall): on a confirmed mover (Schmitt-trigger
gate on a fill-derived realized-return signal, per-event move-budget force-disarm), the resisting side's resting-limit
offset is **widened** and/or its repost is **skipped**, so the wall the move pushes into stops instantly reforming.
Bot-decision layer only → CK-safe; default-off → byte-identical. Validated: build clean, **304/304 tests** (10 new
determinism), probe-drain telemetry wired (REFILL widen/skip line).

## A/B soaks (all CK=0 / drift bounded / 20k-cap-safe; signal = RealizedReturnFast)
| config | engagement | jump meanPct (target 4-7%) | ret_acf | range-eff | verdict |
|---|---|---|---|---|---|
| **Calibration** (arm0.002, widen2, skip0) | widen ~20-40/15s | 0.001 (=OFF) | flat | flat | engages, no effect |
| **Aggressive** (arm0.0008, widen3, skip0.7) | skips 23-79/15s, depth thinned | ~0.001 (=OFF) | flat (VWAP −0.150→−0.159) | 0.456→0.483 (noise) | engages hard, headline flat |
| **Maximal** (arm0.00005=arms-on-everything, skip0.85) | skips 228-552/15s, depth HALVED | ~0.001 (=OFF) | mixed/noise | 0.463→0.463 (identical) | only effect = asymmetric bid-skip DOWN-drift artifact + degraded thin book |

**The decisive metric, jump-impact, stayed ~0.1% of target in every config** — the book absorbs the jump regardless of
how aggressively new resisting reposts are widened/skipped.

## Diagnosis
Widening/skipping **new** reposts cannot overcome the **existing** accumulated wall (fed by ~20k bots), especially vs a
jump aggressor that **slices** to avoid impact. The signal is also partly circular (the jump's realized return is
self-suppressed to 0.1%, so the gate arms on noise-movers rather than the jumped stock). The maximal config approximated
wall-*removal* (depth halved) and still didn't move the headline — only producing the BuyStopFraction-style asymmetric
drift artifact the lever's bounded design was meant to avoid.

## Council (5/5, 2026-06-27)
- **Reject BookImbalance** (the one untested signal): a foregone no-bake (maximal already skipped market-wide with no
  gain); non-trivial plumbing (AiBotContext has no book access). "Buying a receipt for a conclusion you already hold."
- **Stop chasing `ret_acf`**: it's a stat the chart never renders; CLOSE is already in-band (−0.17); the user's complaint
  is *perceptual*. Judge realism by **eyeball + range-efficiency**, not ret_acf.
- Sharp dissent (Outsider/First-Principles): every lever **adds liquidity or slows refill — none removes the wall**, and
  every aggressor **slices to avoid impact**. The one cheap untried test = a **non-slicing whale** (`Jumps:MaxSlices=1`).
  Run as a config-only spike (in flight at writing).

## The mechanism-class map (why the ceiling is structural)
Every class of intervention at the bot-decision layer has now been tried or closely anticipated, and the fast-refilling
book absorbs them all:
| class | lever | result |
|---|---|---|
| push harder (take liquidity) | chaser, fat-tail jump | absorbed (jump 7%-target → ~0% realized) |
| add resting liquidity | market-maker cohort | inert / choked engine at scale |
| correlate across stocks | shared market factor | null (6 soaks, structurally unreachable) |
| loosen the price cap | adaptive anchor | null (cap was never the binding constraint) |
| throttle new reposts | refill-throttle (this) | absorbed across all dials |
| ~remove the wall | ≈ maximal-skip (depth halved) | drift artifact, no clean win |
| move the fundamental anchor | ≈ cross-stock fundamental channel | weak (anchor pull sensitivity-tuned down) |

**Conclusion:** the realism ceiling (`ret_acf_lag1 ≈ −0.43`, the over-mean-reverting 1-min flow) is **structural at the
bot-decision layer**. ~20k independent agents + a deep fast-refilling book = textbook price-discovery efficiency that no
config lever overcomes. Breaking it would require core matching-engine changes (explicitly discouraged). **The chart is
already metrically realistic** (range-eff 0.44 in the real 0.3-0.5 band; clean held trends at native scale — see
`logs/aa_off_45m.png`).

## What's actually high-value now (next steps)
**User-gated (queued — need Kiesh's trigger/number; not touched autonomously):**
- **Ship the validated realism win to prod**: bounce-mid is baked branch-only, **held Q2**; branch is **41 commits ahead
  of origin/master**. This is the highest *user-visible* realism value and it's already validated — just needs the deploy
  trigger (runbook in `PROJECT_STATUS.md`; CK=0 gate; pg_dump first).
- FxRate damping number (recommended `Amplitude 0.005→0.0015`); marketcap/20k reseed (+ atomic `Platform:HouseUserId`
  20002→19997).
**Autonomous (unblocked):** per-currency sharding / config-level perf to protect the 20k cap (the user's hard constraint);
this closeout; tooling.

## Tools shipped default-off this arc (all validated, all reusable)
adaptive anchor (`57a898a`), refill-throttle (`4718725`/`ac48171`), fat-tail jump (`da89c29`/`52a7ec0`), cross-stock
co-movement (`b92df6a`), perceived-price desync (`9b440d9`), chaser v1/v2, MM cohort, impact-decouple (`9d360e2`).
