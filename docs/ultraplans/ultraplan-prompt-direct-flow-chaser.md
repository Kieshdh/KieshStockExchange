# ULTRAPLAN HANDOFF — direct-flow chaser (replace the buyProb tilt with real order volume to move ret_acf)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste bake
prompt for local Claude (apply→build→test→smoke→soak→bake).** Branch `feature/bot-market-realism-v2` (tip ~`1ea91a0`,
the exog-shock subsystem is already landed default-off). This is the empirically-motivated follow-up to the
exogenous-shock work. [[project_sentiment_price_reaction]], `docs/ultraplan-prompt-exogenous-information-flow.md`,
`docs/REALISM_CEILING_INVESTIGATION.md`.

## The issue (empirically established this round — read before designing)
The exog-shock subsystem shipped a **chaser cohort that biases `buyProb`** by `ChaserStrength·tanh(shock/ChaserScale)`.
A/B soaks proved this coupling is **underpowered for `ret_acf_lag1`**: the dose-response across ChaserStrength
0.2 / 1.0 / 2.0 was +0.014 / +0.033 / −0.011 on the 50-stock CLOSE ret_acf — **non-monotonic, all within the ±0.03
soak noise floor**, even at strength 2.0 where the tilt pins `buyProb≈1.0` during shocks (the maximum a probability
tilt can do). Critically, the **flow component (VWAP-based ret_acf) stayed ≈ −0.26 across every arm — completely
immune.** A probability tilt just shifts the buy/sell *ratio*; it does not inject enough persistent directional
*VOLUME* to dominate the 1-minute returns, which is what `ret_acf → 0` requires (real markets are near 0 because
per-minute innovation *volume* swamps the mean-reversion). The shock bus itself works (duty 0.64, clamped, injects
volatility clustering); the **coupling is the problem.**

## Scope (the fix) — chasers TAKE LIQUIDITY ∝ shock, not tilt a probability
Change the chaser coupling from a `buyProb` bias to **direct, aggressive order flow**: when a selected chaser acts
while its stock's shock is active, it **submits a market (or marketable-limit) order in the shock's direction, sized
∝ |shock|** — supplying real, persistent, same-direction 1-min volume that dilutes the flow-MR from the variance
side. Reuse ALL the existing ExogShock infrastructure (the shock bus `ExogenousShockService`/`IShockSource`, the
salted per-`(bot,shock)` cohort selection, `ChaserProbe`, the config block); change ONLY the coupling site in
`AiBotDecisionService` (where `ChaserStrength·tanh(...)` is currently added to the directional accumulator). Note the
SentimentDynamics design already has precedent — "Momentum strategies TAKE liquidity ∝ |bias|" — mirror that.
New primary dial (replaces ChaserStrength as the ACF lever): a **chaser order-notional / take-rate** — e.g.
`Bots:ExogShock:ChaserNotionalFrac` (chase order size as a fraction of the bot's per-position budget or a notional),
the continuous sweep dial. Keep ChaserScale/Fraction semantics (who chases, how shock maps to size).

## Hard constraints / invariants (this is now CONSERVATION-CRITICAL — unlike the tilt)
- **Conservation sacred + REAL orders now.** The tilt only moved a probability; this places actual market orders
  that consume cash/shares. They MUST go through the existing conservation-tested entry path
  (`OrderEntryService` → `MatchingEngine` → `SettlementEngine`), reserve correctly, and keep ConservationProbe=0 /
  CK_Funds/CK_Positions=0 / ReservationAuditor in tolerance. Cash-gated buys / share-gated sells (no naked flow).
- **Runaway-bounded (the big risk).** Direct buying into a rising shock is positive feedback. Bounds that MUST
  hold: the shock is hard-clamped to ±Cap (0.06) and decays; the value-anchor tracks the *bounded* shock target;
  `ValueAnchor:AbsoluteCapMax=0.20` veto + the per-order slippage cap + `StopBreaker` remain the backstops. ADD a
  **per-chaser per-tick/window notional cap** so the cohort can't sweep the book, and consider chasing only a
  FRACTION of the shock (bots don't fully front-run it). The soak MUST verify drift ≤5%/4h, no monotonic runaway,
  and that price stays interior to AbsoluteCapMax.
- Determinism: reuse the salted per-`(bot,shock)` hash for selection + a deterministic size function of (shock, bot)
  — no new RNG, or seeded; no wall-clock in the decision.
- Flag-gated, default-OFF, byte-identical when off (ChaserNotionalFrac=0 ⇒ no order ⇒ identical).
- Touch nothing in `/Tools`.

## Deliverable contract
ONE `git apply --check`-clean patch (default-off + byte-identical off), ships determinism + **conservation** tests
(chaser orders reserve/settle conserved; flag-off path unchanged), no `/Tools`, no formatting churn. PLUS a
ready-to-paste bake prompt: apply→build (server/tests)→`dotnet test`→**15-min smoke** (shock_diag duty≥0.6 +
ChaserProbe shows real orders placed + CK=0, not refused)→**45-min A/B** (OFF vs ON, baked-realism env, lowercase DB,
absolute script path, 2 servers) sweeping `ChaserNotionalFrac` → harvest `scripts/bounce_diag.py` (the **VWAP/flow
component must move toward 0** this time — that's the success signature the tilt couldn't produce) +
`scripts/r4_realism_score.py`. **WIN = ret_acf_lag1 ∈ [−0.15, 0]** with: flow/VWAP component measurably up, absret
clustering NOT collapsed, **CK=0 / conservation clean (hard gate — real orders)**, drift ≤5%/4h, no runaway (price
interior to AbsoluteCapMax). 2-hr confirmation soak before baking default-on. Bake only on a confirmed, runaway-free,
conservation-clean ret_acf win; else leave default-off + report (the −0.43 is then structural beyond reach).

## Calibration / stopping
Target band −0.05…−0.15 (real-market 1-min ret_acf at this tick density — don't overshoot to 0/positive). Sweep
ChaserNotionalFrac up until either ret_acf enters the band (→ bake) OR conservation/runaway/clustering guardrails
trip first (→ the direct-flow lever also can't do it safely → report the ceiling as final).
