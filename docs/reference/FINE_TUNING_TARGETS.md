# FINE-TUNING TARGETS — the market-behavior scorecard & soak gate-set

The named reference for **what the simulated market should look like** and **the bounds a soak is graded
against** when tuning bot/realism config. This is the operational subset; the full ~40-row target scorecard
(Realism norm · Kiesh target · priority per metric) lives in [`BOT_MECHANICS.md`](BOT_MECHANICS.md) **§1
(TARGET VALUES)** — keep that table authoritative and update it in the same commit as any mechanism change.

## Soak gate-set (REGRESSION BOUNDS — "don't get worse", NOT pass-targets)
Grade a 45m A/B soak against these (see [`METHOD_ab_soak_and_gates.md`](METHOD_ab_soak_and_gates.md)):

| Gate | Bound | Note |
|---|---|---|
| `ret_acf` (1-min VWAP) | −0.5 … −0.1 | −0.5 = the known structural ceiling; →−0.1 is aspirational |
| excess kurtosis | ≥ 4 | fat-but-bounded (×3 cap) |
| median excursion | 3–8% | typical intraday move |
| p95 excursion | 10–20% | |
| max excursion | 15–35% | news-driven, rare |
| **CK (conservation)** | **= 0** | **HARD gate — no money/shares created or destroyed** |
| taker share | 20–50% | |
| spread | < 0.5% | |
| \|return\|-autocorr | > 0.05 | vol clustering present |
| cross-sectional dispersion | > 0.002 | not lockstep |
| pairwise corr | 0–0.25 | ≥0.2 is ASPIRATIONAL (≈0.13 factorR² ceiling with bot levers alone; judged over PROD days, not a 45m soak) |

## ★ REVISION 2026-07-23 (Kiesh) — RANDOM-WALK-FIRST, less news-dependent
The prior "typical ±5% / >10% NEWS-ONLY" targets were too strong — too news-dependent, too little random-walk. New
north star = **NATURAL + RANDOM-WALK-LIKE**: smaller everyday moves (~±2–3%, not ±5%), **organic random-walk movement
FIRST** (MarketPulse osc+jitter + base taker flow) with **news a CONTRIBUTOR, not the main mover**, and rare >10% moves
may be organic OR news (was news-only). Live lever: the news-strength cut (`ExogShock:MaxMagnitude`↓ / `MagnitudeExponent`↑
⇒ news mostly tiny, rare bigger). Grade the tape toward calmer 1-min moves (p95 well under the old bounds) + a still-alive
organic dispersion (NOT frozen/re-pinned). See [`BOT_MECHANICS.md`](BOT_MECHANICS.md) §1 (revised Movement rows).

## Owner's headline targets (from §1, the "Kiesh target" column — REVISED 2026-07-23)
- **Movement:** typical ~±2–3% (random-walk-driven); best movers 5–10%/day; >10% rare (organic OR news);
  biggest movers 15–25% on news. **Source of movement = ORGANIC random-walk first; news a contributor.**
- **Shape:** stairs-up (slow positive drift) + elevator-down (rare global crash events override the buy-floor).
- **Returns:** random-walk on every timeframe (`ret_acf` → −0.1 VWAP); damp SLOW trends, keep the FAST 1-min walk.
- **Drift:** POSITIVE + low over a WEEK (intraday can dip on crashes); price runaway bounded (band + cap).
- **Liveness:** every stock traded ≥1× per 15 s (very rare empty candles) — raise the per-tick activation amount,
  not sparse activation.
- **Cross-stock:** pairwise corr ≥ ~0.2 with distinct (not lockstep) names; correlated crashes in crisis.

## Eyeball / long-soak only (unfalsifiable in a 45m soak)
big-news frequency, weekly drift, daily skew, leverage effect, aggregational Gaussianity, multi-day trend,
sector rotation, momentum, cross-stock correlation.

**Source of truth:** [`BOT_MECHANICS.md`](BOT_MECHANICS.md) §1; the levers that move each metric are catalogued
in [`MARKET_BALANCING_CONFIG.md`](MARKET_BALANCING_CONFIG.md).
