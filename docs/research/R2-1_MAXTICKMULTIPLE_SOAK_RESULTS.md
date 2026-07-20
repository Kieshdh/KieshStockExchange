# R2-1 `MaxTickMultiple` soak sweep — the tick-cadence trade curve (2026-07-08)

Validates the R2-1 scaler setpoint knob (`Bots:Scaler:MaxTickMultiple`, commit `2ff021a`). Config:
`DutyCycleDenominator=true` + `ActionableSpanSizing=true` + `MaxTickMultiple=k`, rotator + bank ON,
15 min each, sweep `k ∈ {1,2,3,4}`. Baseline = the B-soak control (default scaler, rotator on). All
arms **CK=0 / CONS=0 / 0 errors / no runaway; the cap SETTLES (no hunting)** — the knob works.

## The curve

| config            | ActiveBotCap | tick     | trades/s | vs control |
|-------------------|--------------|----------|----------|------------|
| control (default) | ~1,575       | ~0.7 s   | ~57      | 1×         |
| **k=1**           | ~5,650       | ~1.5 s   | ~164     | 2.9×       |
| **k=2**           | ~17,240      | ~3.0 s   | ~450     | ~8×        |
| **k=3**           | 20,000 (max) | ~3.9 s   | ~470     | ~8×        |
| **k=4**           | 20,000 (max) | ~3.7 s   | ~500     | ~9×        |

Local docker box; absolute caps are docker-skewed, but the SHAPE (cap↔tick↔throughput) transfers.

## Reading it

- **The knob controls the equilibrium** — the un-knobbed B soak PINNED at 20k/4× tick; here k=1 settles at
  ~5.6k and k=2 at ~17k. The band re-centering does what it claims.
- **Throughput scales ~linearly with the cap** until the 20k fleet ceiling: k=2 (17k) already gets ~8×
  the control's trades; k=3/k=4 just pin at 20k with a slightly slower tick — **k≥3 buys nothing over k=2
  except latency.** The useful range is **k=1 … k=2**.
- **k over-targets the tick by ~1.5–1.9×** (k=1 → 1.5 s not 1.0 s; k=2 → 3.0 s not 2.0 s). Root cause: a
  **refinement bug in `ActionableSpanSizing`** — it sizes the cap from the Collect+Batch span only and
  **excludes the advanced-order phase (~250–500 ms), which IS fleet-scaling work** (adv orders grow with
  the active-bot count). So it under-counts fleet cost, over-sizes the cap, and the full tick overshoots;
  the full-tick guard is what actually stops the runaway. **R2 follow-up: fold `adv` into the actionable
  span** (Collect+Batch+Adv, still excluding only the fixed cohorts arb/mm/rotator/jump) so `k` maps
  truthfully to the tick, and a strict 1 s tick becomes `k≈1` instead of `k≈0.6`.

## Recommendation (the pick is Kiesh's — this is a product decision)

There is no single "right" k — it's a latency-vs-activity dial. Given the sim is real-time (a "day" = 24 h),
a multi-second tick is not obviously bad; it just means bots re-decide every few seconds instead of every
second, in exchange for far more market activity.

- **Responsive 1 s tick** → `k≈0.6` today (or `k≈1` after the adv-span fix). Cap ~3–4k, ~2× control
  activity. Keeps the chart lively at a 1 s cadence.
- **Balanced** → **`k=1`** (cap ~5.6k, 1.5 s tick, ~2.9× activity) — my lean if you want a clear win
  without a coarse tick.
- **Max activity / all-20k active** → **`k=2`** (cap ~17k, 3 s tick, ~8× activity). `k≥3` is strictly
  worse (same 20k, slower tick).

All still Kiesh-gated: `DutyCycleDenominator`/`ActionableSpanSizing`/`MaxTickMultiple` remain **default-off**;
flipping them on (and picking `k`) is your call, ideally after the adv-span refinement lands so `k` reads true.

## Artifacts
DBs `kse_mtk{1..4}`; logs `logs/soakP-kse_mtk{1..4}-*.log`. Baseline: the B-soak control (`kse_b_ctl`).
