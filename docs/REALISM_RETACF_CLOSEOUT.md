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
  flow win alone.
  - **MM perf learnings (for whoever activates it):** an always-on maker on the COMMIT-bound single-loop engine
    must be LOW-CHURN. cohort 40 → loop-choke (12% trades); cohort 8 + default RequoteThresholdBps=5 → still 27%
    (cancel-replace churn saturates DB round-trips, round-trips/order 2.95 vs 0.7, scaler throttles fleet);
    **cohort 8 + RequoteThresholdBps=50 + HalfSpreadBps=20 → healthy (74-95% trades), CK=0.** Three load dials:
    cohort size, cadence (`MM_DECISION_INTERVAL`), requote threshold. For prod scale, batch the cohort's cancels
    (deferred `CancelOrdersBatchAsync`). Activation: set `MARKET_MAKER_COHORT_SIZE>0` in Tools + reseed + flip
    `Enabled=true` + use the healthy params above.

## The structural ceiling + the next root-cause lever
The 72% is the **fleet reaction loop**: ~20k bots read the SAME shared price each tick and each nudge toward it →
synchronized mechanical 1-min mean-reversion, by construction. Flow band-aids (chaser/MM) *mask* it (and cost
drift); they don't remove it. 25+ config experiments + the chaser + the MM all failed to bake a clean flow win.
**Next root-cause lever (council First-Principles):** desync the per-bot price reads (per-bot stale/offset price)
so bots stop reacting to the same tick — directly tests whether *synchronicity* is the −0.43. Handoff:
`docs/ultraplan-prompt-desync-price-reads.md`.

**Caveat (council Contrarian):** the two ret_acf instruments (r4 16-stock vs bounce_diag 50-stock) can disagree by
~0.2 on noisy/short arms — bake decisions need the 2h-window, both-instruments-agree standard the bounce-mid win
met. Don't tune to sub-0.1 precision on a single short screen.
