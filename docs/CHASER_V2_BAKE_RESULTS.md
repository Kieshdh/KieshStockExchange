# Chaser-v2 (drift-neutral co-dials) — bake-soak results (2026-06-21)

**Branch `feature/bot-market-realism-v2`, patch committed default-off `0586e0d`.** Validating the v2 ratio-fix
co-dials (C3 `ChaserSellSymFrac`, C5 `ChaserBuyRoomRelaxFrac`) against the bake gate. Goal: keep v1's proven
VWAP/flow ret_acf win while making the chase flow drift-neutral so it bakes default-on.

**Bake gate (ALL must hold):** VWAP ret_acf ∈ [−0.15,−0.05] · drift ≤5%/4h (≈baseline) · peak excursion <±20% ·
absret clustering preserved (≥~0.10, lag5 not negative) · CK=0. Among passing cells, max retained gross volume.

**Method:** parallel A/B on `kse_soak_seed`, baked-realism defaults + `Bots__ExogShock__*` env. v1 reference
config: `chaserFraction=0.25 chaserNotionalFrac=0.10 chaserMaxNotionalFrac=0.05 anchorTracks=false`. PG max_conn 300.
Harvest: `bounce_diag.py` (CLOSE+VWAP) + `r4_realism_score.py` + ChaserProbe per-side net/gross. CK from server log.

---

## Round 1 (90m) — OFF baseline vs C3=0.5/C5=1.0
| Metric | A `kse_v2off` (OFF) | B `kse_v2bal` (C3=0.5/C5=1.0) | gate |
|--------|--------------------|-------------------------------|------|
| VWAP ret_acf | −0.223 | **−0.178** (Δ+0.045) | ❌ not in band |
| CLOSE ret_acf | −0.392 | −0.351 | (bounce half) |
| drift/90m | −0.62% | **−2.33%** (≈−6.2%/4h) | ❌ over budget, worse than v1 |
| absret_acf lag1 | 0.165 | **0.107** (lag5 −0.033) | ❌ clustering damaged |
| composite | 71.6 | 59.7 | ❌ regressed |
| trades | 486,395 | 188,739 (~40%) | ⚠️ liquidity halved |
| excursion (min/max) | [−7.21, +15.12] | [−12.95, **+19.39**] | ⚠️ near ±20% cap |
| CK | 0 | 0 | ✓ |
| ChaserProbe (per window) | — | buyN≈77M / sellN≈9.5M / net≈+70M / gross≈86M | buy-heavy INTENT |

**Verdict: C5=1.0 FAILS on every axis except conservation.** Root cause: C5 (full buy-room relax) places giant
marketable buys sized to cash (position-room removed) that are largely **unfillable** — the probe's buy-heavy
*intent* (net +70M) does NOT become realized up-pressure. Instead it produces lumpy spikes (a stock to +19.39%),
halves trade count, and damages volatility clustering, while realized flow STILL leans sell (drift −2.33%, worse
than plain v1's −1.91%). It even moved VWAP *less* than v1 (+0.045 vs v1's +0.089 into band). **C5 at the extreme
is the wrong lever** — buy-room-relax creates unfillable lumpiness, not smooth balancing flow.

Key structural insight: the chaser's VWAP win comes from gross FLOW VOLUME; the drift comes from the net-long
population's SELL-LEAN (sells fill into bids; buys into up-shocks are liquidity-starved). These are coupled — the
flow that moves VWAP is the same flow that leans sell. C5 tried to balance by adding buys (failed: unfillable);
C3 balances by cutting sells (costs gross → costs the VWAP win). The bake hinges on whether an interior cell
threads this needle.

---

## Round 2 (45m screen) — C5 gradient at C3=0.5 {0.0, 0.5, 1.0}
Three points on the C5 axis at fixed C3=0.5 (round-1 B supplies C5=1.0). Judged vs the absolute 5%/4h drift
budget + VWAP band + clustering; winner gets a 90m confirmation.
- C `kse_v2c`: C3=0.5 / **C5=0.5** — does a moderate C5 avoid the C5=1.0 harm?
- D `kse_v2d`: C3=0.5 / **C5=0.0** (pure C3) — least-bad on drift, but BAKE.md's "acf-destroying corner"?

| Metric | C (C5=0.5) | D (C5=0.0 pure-C3) | gate |
|--------|-----------|--------------------|------|
| VWAP ret_acf | −0.199 | **−0.165** (closest of all arms) | ❌ not in band |
| CLOSE ret_acf | −0.351 | −0.355 | (bounce half) |
| drift/45m | −1.18% | −1.20% | ❌ ~2× baseline |
| composite | 55.3 | **30.5** (book starved → flat candles, flat%9.8/wick70) | ❌ |
| absret lag1 / lag5 | 0.113 / −0.118 | 0.102 / −0.014 | ⚠️ lag5 negative |
| ChaserProbe net (intent) | ~+45M (buy-heavy) | ~+3–6M (most balanced) | — |
| CK | 0 | 0 | ✓ |

**C5 gradient at C3=0.5 {0.0, 0.5, 1.0} → drift {−1.20%/45m, −1.18%/45m, −2.33%/90m}: C5 monotonically WORSENS
drift** (more buy-room relax → more unfillable lumpy buys → more down-drift). Pure-C3 (C5=0.0) balances order
*intent* best (net ~+4M) and gives the best VWAP (−0.165) but **starves the book → flat candles** (composite 30.5).

## VERDICT — chaser-v2 NO-BAKE (council-unanimous 2026-06-22)
**No cell in the C3×C5 space passes the gate.** Across 3 soaks (R1 90m + R2 45m×2): VWAP never reaches
[−0.15,−0.05]; drift is 2–4× baseline everywhere; clustering/candle-realism regresses (catastrophically for
pure-C3). CK=0 is the only gate that holds.

**Root cause (sharpened by council code-read):** C3/C5 tune `roomValue` (position-room), but the *binding*
constraint on an up-shock chase-BUY is `ApplyDepthCap` — a fraction of resting **asks, which don't exist in an
up-shock**. The dials never touched the real asymmetry, which is **liquidity, not position room**. The drift is a
conservation identity: a net-LONG population firing marketable orders into symmetric shocks fills sells (into
resting bids) but not buys (no resting asks) → realized flow leans sell → down-drift. No order-sizing dial escapes
a one-sided book.

**Disposition:** chaser v1 (`7051f53`) + v2 (`0586e0d`) stay **shipped default-OFF** — conservation-clean,
available tools; v1 remains the proven VWAP/flow lever for ad-hoc use. **Not baked default-on.**

**One untested residual** (low-EV per the conservation-identity argument, noted for any future revisit):
`ChaserMinIntervalSec` cadence — thinning chase flow over time *might* change fill rates by letting the book
refill asks between marketable hits. The C5/C3 sizing dials are spent.

**Next (council-directed):** (1) ship the BOUNCE half (independent, drift-free win on the legitimate ~28%);
(2) pivot the FLOW half to a **market-maker / two-sided resting-liquidity cohort** (Option C) — the root cause
behind bounce + flow-drift + net-long imbalance → ultraplan handoff `docs/ultraplan-prompt-market-maker-cohort.md`.
