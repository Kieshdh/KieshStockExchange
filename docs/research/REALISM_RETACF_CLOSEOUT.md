# ret_acf<0.1 realism effort — close-out (2026-06-22, council-driven)

**Branch `feature/bot-market-realism-v2`.** Goal: pull the 16-stock scorer's 1-min return autocorrelation
`ret_acf_lag1` from the long-standing ≈ −0.43 ceiling toward the realistic band **[−0.15, −0.05]** (real equity is
slightly negative, NOT 0 — exactly 0 = unrealistic random walk), WITHOUT killing volatility clustering, with drift
≤5%/4h and conservation CK=0. Decomposition (R5 bounce diagnostic): **~28% bid-ask bounce + ~72% fleet
reaction-loop mean-reversion.**

## Outcome: ONE clean bake (bounce-mid); flow half concluded structural
Council-unanimous (5 advisors, 2026-06-22). The headline `ret_acf` is now ≈ **−0.17** (from −0.43) via the bounce
fix alone — essentially inside the real-equity range. The Outsider's reframe: *the goal is basically met.*

### ✅ BAKED default-on (branch; prod held per Q2) — `Bots:BounceReference=mid` (`124853d`)
The **28% bid-ask-bounce** half. Candle CLOSE keys off the matcher-captured mid-price ((bid+ask)/2, sampled once
per taker) instead of last-trade, removing the mechanical spread zig-zag at the source. O/H/L stay on the trade
tape (wicks preserved); cash settles on the maker `Price` (conservation untouched). **2h confirm (chaser off =
ship config):** CLOSE ret_acf −0.374 → **−0.170**, clustering PRESERVED (absret 0.161 ≈ OFF 0.138), drift −0.94%
(better than OFF), CK=0. Both bounce_diag and r4 agreed on −0.170. (The R1 45m "clustering collapse" was
short-window noise; finer-tick `PriceTickDecimals` was a confirmed dud — coarse grid wasn't the bounce source.)
**User-visible:** the chart's candle-close is now smoother (mid, less tick zig-zag) — eyeball before prod.

### ❌ NO-BAKE, shipped default-off as tools — the FLOW half (72% reaction-loop MR)
- **Chaser** (`Bots:ExogShock:Chaser*`, v1 `7051f53` + v2 `0586e0d`): marketable orders into news-shocks. Moves
  VWAP/flow ret_acf into band but has an inherent **down-drift** (net-long pop sells freely into shocks; buys
  liquidity-starved). v2 drift-neutral dials (C3/C5) FAILED across a full C3×C5 sweep — the binding constraint is
  **liquidity, not position-room** (`ApplyDepthCap`, no resting asks into up-shocks). Conservation identity, no
  sizing dial escapes. `docs/CHASER_V2_BAKE_RESULTS.md`.
- **Market-maker cohort** (`Bots:MarketMaker:*`, `a6c9b56`): all-weather two-sided resting liquidity
  (`AiStrategy.MarketMakerHouse=6`). **MM-alone (prod config, chaser off) is ret_acf-INERT** (CLOSE −0.413→−0.415)
  — drift-neutral + CK=0 + tightens the down-tail, but doesn't touch the fleet reaction loop. It only moves ret_acf
  *jointly with the chaser* (chaser+MM: ret_acf improves + clustering preserved 0.10→0.17 — the only lever that
  moved ret_acf AND kept clustering — BUT drags in the chaser's drift + over-corrects). Net: no clean bakeable
  flow win alone. **Re-confirmed 2026-06-22 (cranked-skew test, full 45m paired A/B, both instruments):** cranking
  `SkewBps` 20→150 (the Expansionist's "inventory-skew makes MM a drift-free flow lever" dissent) is doubly refuted —
  (1) NOT a clean flow lever: bounce_diag VWAP −0.187→−0.174 (Δ+0.013, null), r4 mid-CLOSE −0.268→−0.214 (Δ+0.054,
  small + instrument-inconsistent), and it REGRESSES clustering (r4 absret_lag1 0.109→0.063, lag20 +0.032→−0.069 —
  the MM's two-sided quoting smooths vol); (2) NOT drift-free: avg drift −0.10%→−0.42%/45m (ON declines monotonically
  +0.23→−0.42; MM bids absorb the net-long pop's chase-sells = more realized sell flow). CK/CONS/ERR=0 both arms,
  trades 509k vs 463k (ON 91% — mild MM load, healthy at RequoteThresholdBps=50). **Net: cranked skew is mildly
  HARMFUL (clustering↓, drift↑) for no clean ret_acf gain — strictly worse than default MM. Dissent closed.**
  - **MM perf learnings (for whoever activates it):** an always-on maker on the COMMIT-bound single-loop engine
    must be LOW-CHURN. cohort 40 → loop-choke (12% trades); cohort 8 + default RequoteThresholdBps=5 → still 27%
    (cancel-replace churn saturates DB round-trips, round-trips/order 2.95 vs 0.7, scaler throttles fleet);
    **cohort 8 + RequoteThresholdBps=50 + HalfSpreadBps=20 → healthy (74-95% trades), CK=0.** Three load dials:
    cohort size, cadence (`MM_DECISION_INTERVAL`), requote threshold. For prod scale, batch the cohort's cancels
    (deferred `CancelOrdersBatchAsync`). Activation: set `MARKET_MAKER_COHORT_SIZE>0` in Tools + reseed + flip
    `Enabled=true` + use the healthy params above.

## The structural ceiling + the desync root-cause lever (TESTED — sub-gate, default-off)
The 72% is the **fleet reaction loop**: ~20k bots read the SAME shared price each tick and each nudge toward it →
synchronized mechanical 1-min mean-reversion, by construction. Flow band-aids (chaser/MM) *mask* it (and cost
drift); they don't remove it. 25+ config experiments + the chaser + the MM all failed to bake a clean flow win.

### ❌ NO-BAKE, shipped default-off — `Bots:PerceivedPriceDesync` (the deepest root-cause lever; `9b440d9`, 2026-06-23)
The council's First-Principles pick, delivered by ultraplan: each bot reacts to its OWN fast+slow perceived-price
EWMA (dispersed per bot by `Lateness` + a salted `AiUserId` hash) instead of the shared live price / sentiment
slope — directly attacking the *synchronicity*. Pure/RNG-free, default-off byte-identical, supersedes
`DirectionalReactionLag`, no `/Tools` touch, 274/274 tests (8 new determinism). **It is the CLEANEST lever the arc
produced — drift-free AND clustering-safe AND direction-correct (every prior flow lever traded one for another).
But it is too WEAK to pull the flow ret_acf into band at safe dials:**
- **R1 (45m @default MaxAlpha0.45):** looked great — VWAP −0.160→−0.096, r4-mid −0.195→−0.089 (both in-band). **Noise.**
- **R2 (2h confirm, the reliable window):** the win SHRANK to noise-floor — VWAP −0.204→**−0.179** (Δ+0.025, at the
  ±0.03–0.04 floor, NOT in band); r4-mid −0.191→−0.183 (Δ+0.008, flat). Clustering PRESERVED both instruments
  (bounce_diag absret 0.163→0.142; r4 0.209→0.251 *improved*), drift EQUAL (−1.28 vs −1.34%/2h), CK=0, walls fine,
  composite 66.5→75.7. The R1 in-band win AND its clustering scare were BOTH 45m noise.
- **R3 (council-gated bounded sweep, 45m):** stronger dials did NOT amplify — Cell A (2× strength, scales 0.005/0.01)
  VWAP −0.183; Cell B (wider dispersion, MaxAlpha 0.65) VWAP −0.190 — both in the same −0.18/−0.19 cluster as default
  and OFF. (r4-mid showed −0.13/−0.14 in-band, but that's the noisy 45m instrument that already evaporated in R1.)
  Gate (VWAP ≤−0.15, both instruments agree) not met → council's pre-registered hard-stop → ship default-off.

**CONCLUSION: the 72% flow half is STRUCTURAL at safe dials, confirmed by the deepest root-cause lever.** Breaking
the shared-price synchronicity (desync) moves the flow ret_acf in the right direction but only by a noise-floor
amount; pushing the dials harder doesn't amplify it (it asymptotes ~−0.18) without risking overshoot-past-0
(random walk = worse). Activation if ever wanted: `Bots:PerceivedPriceDesync=true` + restart (cleanest tool; the
default 0.45/0.01/0.02 cell is the validated clean point). **The ret_acf arc is closed: the user-visible headline
CLOSE ret_acf is −0.17 (bounce-mid, in the real range); the residual flow/VWAP sub-component (~−0.18) is one
noise-width outside a fuzzy band, on a measure the chart doesn't surface, and is not worth chasing further.**
Handoff that produced this: `docs/ultraplan-prompt-desync-price-reads.md`.

**Caveat (council Contrarian):** the two ret_acf instruments (r4 16-stock vs bounce_diag 50-stock) can disagree by
~0.2 on noisy/short arms — bake decisions need the 2h-window, both-instruments-agree standard the bounce-mid win
met. Don't tune to sub-0.1 precision on a single short screen.
