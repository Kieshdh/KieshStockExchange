# BOT_MECHANICS.md — how the KieshStockExchange bots work + the target behavior

Compact reference for the bot-trading systems and the market-behavior targets. **Consult + UPDATE this file whenever a bot mechanism changes** (same commit).
Config VALUES live in `appsettings.json` (`Bots:*`) and the seed `Tools/Config.py`; §2 references the config KEYS, not hard values. (Decision history and config snapshots live in the plan log, not here.)

---

## 1. TARGET VALUES — the scorecard the market should hit
**Realism** = real-market norm · **Kiesh** = the given target · **P** = priority 1 (high) – 5 (low). *[Priorities provisional — pending final lock.]*

| Group | Metric | Realism | Kiesh target | P |
|---|---|---|---|---|
| **Movement** | typical intraday move | ±1–3% most days | most ±5% | 2 |
| | active / best movers (daily) | 5–10% | 5–10% | 1 |
| | >10% moves | news-driven, rare | NEWS-ONLY, rare | 1 |
| | biggest movers | 10–20% on news | 15–25% on news | 2 |
| | big-news frequency | occasional | very rare — ~once per stock per WEEK | 1 |
| | rise vs crash shape | crashes sharper (leverage) | stairs-up (slow +drift) + elevator-down (RARE global crash events override the buy-floor) | 3 |
| | multi-day trend | mean-reverts over weeks | 20%+ sticks → SELL driver | 3 |
| | price band (backstop) | none (fat tails) | ×3 / ÷3 elastic; extreme rare | 2 |
| **Returns** | random-walk path @ ALL timeframes | 1-min VWAP ret_acf −0.02…−0.10 | random-walk on every timeframe (ret_acf→−0.1 VWAP); damp SLOW trends, keep the FAST 1-min walk | 1 |
| | excess kurtosis (fat tails) | 10+ | fat-but-RARE, bounded ~4-6 (diagnostic, ×3 cap) | 3 |
| | daily return skew | −0.3…−0.5 | ~log-symmetric per move; crashes sharper | 3 |
| **Cross-stock** | pairwise corr (calm) | 0.2–0.3 | ≥ ~0.2 | 2 |
| | crisis corr | 0.7–0.9 | correlated crashes | 2 |
| | idiosyncratic share (market-R²) | 0.2–0.3 (70–80% idiosyncratic) | distinct, NOT lockstep | 2 |
| | sector rotation | sectors co-move | intra > cross | 4 |
| **Safety** | conservation (CK) | exact | 0 ALWAYS — no money/shares created or destroyed | 1 HARD |
| | net drift (direction) | ~0 + small premium | POSITIVE + low over a WEEK (intraday can dip on crashes) | 3 |
| | price runaway | bounded | none (band + cap) | 1 |
| **Liquidity** | volume / activity | continuous | lively, NOT deadened | 2 |
| | taker share | (subsumed by volume + impact) | — | 4 |
| | spread / book depth | tight liquid / wider thin | realistic / adequate | 4 |
| **Population** | momentum-amplifier share | significant but takers-IN / limits-OUT | TBD (maybe 47% → 25–30%, reseed) | 3 |
| | strategy diversity | momentum / value / MM / arb mix | diverse | 3 |
| **FX** | USD/EUR coupling band | 0.1–0.5% | ~0.3–0.5% (don't force → parity) | 4 |
| | FX intraday vol | ~1% | mean-reverting bounded ~1% | 4 |
| **Clustering** | \|return\| autocorr (vol clustering) | +0.15…+0.35, long-memory | vol clusters (calm → storms) | 2 |
| | leverage effect (return → vol) | −0.1…−0.4 | vol rises after drops | 3 |
| | aggregational Gaussianity | kurtosis ↓ with horizon | fat 1-min thins by daily | 3 |
| **Volume / flow** | volume ↔ volatility corr | +0.4…+0.7 | big-move days = high volume | 3 |
| | daily turnover (vol / float) | 0.5–2%/day liquid | plausible vs float | 4 |
| | price-impact shape | concave / √size | sub-linear in trade size | 3 |
| | order-flow (trade-sign) autocorr | +0.3…+0.7, long-memory | buys follow buys (trend mechanism) | 3 |
| **Global** | index vol vs single-stock | ratio ~0.35–0.55 | diversification works | 3 |
| | cross-sectional dispersion | 1–3% calm / 4–8% crisis | not lockstep | 3 |
| | market breadth (adv/decl) | up-days ~55–65% advance | broad-based | 4 |
| | index return autocorr | ~0 (near-martingale) | no exploitable pattern | 4 |
| **Tails** | tail index α | 3–5 | crash magnitude sane | 4 |
| | trade-size distribution | power-law ~1.5–2.5 | most tiny, rare huge | 4 |
| | short-term reversal (~1 wk) | losers → winners | mean-reversion days-weeks | 4 |

**Grade it (soak gate-set):** ret_acf(VWAP) −0.5…−0.1 · kurtosis ≥ 4 · median excursion 3–8% · p95 10–20% · max 15–35% · CK = 0 · taker 20–50% ·
spread < 0.5% · |return|-autocorr > 0.05 · cross-sectional dispersion > 0.002 · pairwise corr 0–0.25.
**Eyeball / long-soak only** (unfalsifiable in a 45m soak): big-news frequency, weekly drift, daily skew, leverage effect, aggregational Gaussianity,
multi-day trend, sector rotation, momentum. **Out-of-scope for a 24/7 sim:** day-of-week, intraday U-shape, auctions, implied vol.
*(The bid-ask bounce is a mechanical source of negative 1-min ret_acf — not a bug.)*

---

## 2. SYSTEMS — mechanism reference  *(FILLED DURING THE LONG-BAKE DOC SWEEP — task #177)*
Placeholder. Each subsystem gets a compact section: **what it does · config keys · brief why**. Cover: sentiment (rings / RegimeDrift / GlobalShock /
PriceReaction / CoMovement / SlowRingDamp) · SentimentDynamics slope model + conviction dials · strategy types + seeded mix · order types + limit tiers
(Close/Mid/Far) · advanced orders (stops / trailing / brackets) · arbitrage + FX desk/house · exogenous shock + news + co-fire · fat-tail jumps ·
value / recent / fundamental anchors + caps · cash homeostasis + dip-buy + injection · herding / imbalance · market-maker quoting · bear-short ·
reaction-persistence · FX walker · per-bot SEEDED params (aggression / lateness / cash bands / buy-bias / strategy weights). Reference config keys, not values.
