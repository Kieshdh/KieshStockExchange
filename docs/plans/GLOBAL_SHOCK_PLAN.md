# Global Fast-Shock + Chaser — Implementation Plan (for council review, 2026-07-04)

## Goal
Add a **FAST shared (market-wide) FLOW signal** that lifts cross-stock correlation at **SHORT (1-5 min) horizons**, where the current slow
global-sentiment ring gives ~0. (The per-stock "texture" half of the news-redesign idea is redundant — RD-off's fast τ≈20-30s sentiment rings
already provide the choppy random-walk look — so this plan is the **GLOBAL half only**.)

## What already exists (config-testable, no build)
- `RandomShockSource` (IShockSource.cs) has a **GLOBAL stream**: when `GlobalFraction > 0`, a shared sign+magnitude impulse fires at rate
  `p × GlobalFraction` and is applied to **every stock that tick** (dedicated `_globalRng`; `GlobalFraction=0` never draws it ⇒ byte-identical).
  = a correlated shock by construction.
- The global impulse accumulates into each stock's `_shock[sid]` (`ExogenousShockService`, SoftWallStep = cubic soft-wall + hard ±Cap), decaying
  at the single service-level `DecayHalfLifeSec`.
- The **chaser** (`ChaserFraction` cohort, `ChaserNotionalFrac`, per-order cap `ChaserMaxNotionalFrac`) fires **TAKER market orders** into
  `_shock[sid]` (AiBotDecisionService). For a global shock, every stock's chaser fires the SAME direction = **correlated taker FLOW** (not a
  damped sentiment tilt — the arc's whole lesson is only taker flow durably moves price). CK-safe (rides the normal OrderEntry→Match→Settle path).
- All config under `Bots:ExogShock:*`, default-off, conservation-clean (the shock places NO orders; only the chaser does).

## The key risk — the prior NULL
An earlier test (GlobalFraction 0→1 + chaser frac0.25/notional0.15, at the DEFAULT 300s decay) gave **NULL correlation (~0.008 @5min)**: the
capped chaser (≤25% cohort, 2%-per-order cap) was **BOOK-ABSORBED**. A naive `GlobalFraction=on` config just repeats that null. **The plan's whole
job is to beat the absorption** — via a FASTER + more FREQUENT global shock and an UN-throttled, BIGGER chaser.

## Phase 1 — config-only (beat the absorption; NO build)
Arm A = RD-off (current best; corr ~0.25 at the slow 5-10min horizon). Arm B = RD-off + **fast global shock + bigger chaser**:
- Global shock FAST + FREQUENT: `GlobalFraction ≈ 0.4`, `MeanIntervalMinutes ≈ 0.5`, `DecayHalfLifeSec ≈ 30-45` (SHORT ⇒ shared variance at
  SHORT horizons = the point; the prior null used 300s = slow = corr only at long horizons). Magnitude range as-is (symmetric sign already built).
- Chaser BIGGER (beat the prior cap): `ChaserFraction 0.25→~0.5`, `ChaserNotionalFrac 0.15→~0.30`, `ChaserMaxNotionalFrac 0.02→~0.08`
  (un-cap the per-order size — the 2% cap was the absorption limit).
- **GATE:** 1-5min cross-stock corr rises meaningfully vs A; 5-10min corr HOLDS (≥ 0.25 − 0.03); drift ≈ 0; CK = 0; perf (bigger chaser = more
  taker orders → watch 1s-loop span + the 20k cap); no runaway (±Cap backstop). 45m A/B.
- **READ:** corr rises ⇒ the flow channel works when the shock is fast+frequent and the chaser un-throttled (the prior null = slow decay + capped
  chaser). Still null ⇒ chaser flow is structurally absorbed even fast+big ⇒ flow-correlation is dead, accept slow-sentiment 0.25 (arc's earlier read).

## Phase 2 — code extension (ONLY if Phase 1 warrants): mixed / randomized decay
Honors Kiesh's "randomize the decay" + gives a clean fast-shared / slow-idio split:
- **Minimal:** give the GLOBAL shock its OWN accumulator + its OWN (fast) decay, separate from the per-stock slow decay. New knob
  `Bots:ExogShock:GlobalDecayHalfLifeSec`. Contained change to `ExogenousShockService` (a second `_globalShock` scalar with its own decay, summed
  into `GetShock`). Default = the main decay ⇒ byte-identical.
- **Fuller (proper shot-noise, more work):** extend `ShockImpulse` with a per-impulse `HalfLifeSec` drawn from a distribution (LogNormal ~25s +
  a thin slow tail), track a per-stock LIST of decaying impulses. True "randomized decay per event" but a real refactor of the one-value-per-stock model.

## ★ COUNCIL PLAN-REVIEW VERDICT (2026-07-04) — the plan was REVISED
**Physics = sound (green light):** shared TAKER flow consumes book depth = a PERMANENT level shift ⇒ correlation at ALL horizons incl. 1-min.
The prior null was EXECUTION, not concept. But two headline knobs above were WRONG:
1. **"Bigger chaser" fixes the wrong axis — the real failure is DILUTION.** The chaser fires PROBABILISTICALLY per-bot, so the global impulse's
   one shared sign is averaged away by independent per-bot coin-flips ⇒ each stock's flow = idiosyncratic timing noise. Magnitude alone won't fix
   it. **REAL LEVER = deterministic CO-FIRING: on a global impulse the whole chaser cohort fires SAME-TICK, SAME-SIGN, sized to clear book depth.**
   (First-Principles + Expansionist converge; Expansionist's "conscript the WHOLE fleet briefly" = the ambitious co-firing.)
2. **Fast decay = RED HERRING + harmful.** Flow impact is PERMANENT ⇒ decay barely affects correlation; it only sets the CO-FIRING WINDOW. A 30s
   half-life evaporates the signal before flow accumulates (self-contradiction). ⇒ DON'T shorten decay; Phase-2 randomized-decay is misdirected
   for correlation (it's a TEXTURE lever, and texture is redundant with RD-off).
**Guardrails (unanimous):** (a) MEASURE FIRST via a SINGLE-STOCK IMPACT TEST (fire a co-firing pile-in on ONE stock, measure price move + book
depth + refill rate; if one stock won't move, correlation is dead on arrival — don't soak 50). (b) PERF is the binding gate: co-firing bursts
(cohort×50×frequent) on the commit-bound loop = over-order throttle; if the scaler drops the fleet <19k DURING shocks the corr number is INVALID
(different fleet than control) — instrument round-trips/order + ActiveBotCap FIRST. (c) 1-min corr over 45m is sample-starved ⇒ 90m + the lift must
survive BOTH non-overlapping halves.

## ★ REVISED PLAN (supersedes Phase 1/2 above)
0. **Single-stock impact test** (cheapest, decisive): co-firing pile-in on ONE stock → price move + depth + refill. Gate the whole effort.
1. **Make the chaser CO-FIRE** (small code change — probabilistic today = the dilution): on a GLOBAL impulse, the chaser cohort fires
   deterministically same-tick/same-sign at notional sized to clear depth. Keep decay MODERATE (not fast). Default-off, byte-identical.
2. **Perf-instrumented 90m A/B**: A=RD-off, B=RD-off+co-firing global chaser, notional LADDERED (0.30 first). Perf gate (cap ≥19k through shocks)
   BEFORE trusting corr; corr(1-5min) rises AND holds ≥0.22 in BOTH halves; drift≈0; CK=0. Kill on perf choke / drift / CK / runaway.
3. **Escalation if it works:** whole-fleet conscription → unified news engine (regime driver, Hawkes clustering, sector rotation).

## Open questions for the council (ORIGINAL — mostly answered above)
1. **Can the chaser-flow beat the absorption** that killed the prior attempt (fast+frequent shock + un-capped bigger chaser), or is flow-correlation
   structurally dead ⇒ accept slow-sentiment 0.25?
2. **Fast decay vs chaser impact:** a fast-decaying shock may vanish before the chaser's flow accumulates enough impact — is there a tension (the
   shock must persist LONG ENOUGH for the chaser to move price, but SHORT enough for short-horizon correlation)? What's the right decay?
3. **Phase-2 scope:** separate global accumulator (minimal) vs full per-impulse decay list (proper shot-noise)?
4. **Perf:** the bigger chaser adds taker orders on every frequent global shock — threat to the 20k cap / 1s loop? Cap the chaser by a per-tick order
   BUDGET rather than per-order notional?
