# ULTRAPLAN HANDOFF — chaser v2: drift-neutral (symmetric) chase flow

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply --check`-clean PATCH FILE + a ready-to-paste
bake prompt for local Claude. Branch `feature/bot-market-realism-v2` (tip has chaser v1 committed default-off,
`7051f53`).** This completes the exogenous-flow lever: v1 PROVED the chaser moves ret_acf, but it has an inherent
down-drift that blocks baking it default-on. v2 makes the chase flow **drift-neutral** so the proven ret_acf gain
can be baked. Context: `docs/ultraplan-prompt-exogenous-information-flow.md` (the v1 design), and the empirical
results below.

## What v1 proved (keep this — it works)
The direct-flow chaser (`Bots:ExogShock:ChaserNotionalFrac`) submits real marketable orders into live shocks and
**moves the VWAP/flow ret_acf into the target band, reproducibly**: across 4 soaks (K0/S1/S2/S3) VWAP ret_acf went
**−0.20…−0.23 → −0.12…−0.16** (band is [−0.15,−0.05]), r4 ret_acf −0.34…−0.42 → −0.19…−0.25, clustering preserved
or improved, **conservation CK=0 throughout**. This is the first lever in 20+ experiments to move ret_acf. The
mechanism is correct; do not change it.

## The v1 problem v2 must fix — an inherent, dose-independent DOWN-DRIFT
Every chaser arm adds **~−1.3%/90m of net down-drift** on top of baseline (total ~−2%/90m ≈ −5.4%/4h, OVER the
≤5%/4h budget) + price excursions reaching ~−18 to −23% (past the ±20% `AbsoluteCapMax` promise). Diagnosis (from
the soaks):
- **Dose-independent:** ChaserNotionalFrac 0.05 / 0.10 / 0.15 ALL drift ~−2%/90m → it's a DIRECTIONAL asymmetry,
  not volume. Lowering the dose does not help (it just loses the ret_acf gain too).
- **NOT the value-band veto / static fundamental:** `AnchorTracksShock=true` (anchor target follows the shock, so
  the band moves with it) did NOT reduce the drift (S3b still −2.24%/90m). And shocks-with-anchor-tracking but
  chaser-OFF (S3a) drift ~baseline (−0.94%) — so the drift is the CHASER's trading, ~−1.3%/90m of it.
- **Most likely cause = suppression asymmetry:** chase-BUYS require CASH (suppressed when a bot's cash runs low —
  the v1 `ChaserProbe` shows `suppressed` ≈ `orders`), while chase-SELLS require SHARES (the bot population is
  net-long, so shares are ~always available). Net realized chase flow leans SELL over the run → persistent
  down-drift, independent of the requested dose.

## Scope (the fix) — make the chase flow drift-NEUTRAL, keep the ret_acf mechanism
Make the realized buy/sell chase notional balanced (net ≈ 0 over any window) so the chaser adds no directional
drift, while still supplying the persistent two-sided 1-min volume that dilutes the over-mean-reversion. Candidate
mechanisms for the council to weigh (DESIGN question):
1. **Symmetric suppression (preferred):** when a chase order is blocked on one side (buy cash-gated / sell
   share-gated), proportionally throttle the opposite side for the same shock/window so realized flow stays
   balanced. Track the per-window net realized chase notional (already in `ChaserProbe`) and damp the leaning side.
   Must stay deterministic (per-(bot,shock) seeded, no global RNG-coupled controller — the determinism rule).
2. **Fund the buy side:** give chase-BUYS access to a small dedicated reserve / symmetric short-cover allowance so
   they are gated the same way sells are (so the cash/share asymmetry disappears). Conservation-critical — gate
   carefully.
3. **Inventory-symmetric gating:** gate chase-SELLS by the same scarcity logic as buys (e.g., cap a bot's
   chase-sell to its free inventory the way a chase-buy is capped to free cash), so neither side has a structural
   advantage.
4. **Cohort balancing at selection:** deterministically split the chase cohort so the expected buy/sell notional
   per shock is balanced regardless of the cash/share state (per-bot seeded, no controller).

Also useful for v2 (deferred from v1): **per-side `ChaserProbe` counters** (buy vs sell orders AND suppressed) so
the drift-neutrality is directly auditable from the log; an optional `ChaserMinIntervalSec` cadence; and confirm
the ±`AbsoluteCapMax` excursion stays bounded under the (now higher, drift-free) usable dose.

## Hard constraints / invariants
- **Conservation sacred** (ConservationProbe=0 / CK_Funds/CK_Positions=0 / ReservationAuditor in tolerance). Chase
  orders ride the existing OrderEntry→Match→Settle path (no naked flow).
- **Drift-neutral is THE acceptance criterion:** net price drift ≤5%/4h (ideally ≈ baseline) AND per-stock
  excursion < ±20% (`AbsoluteCapMax`) at a dose that keeps VWAP ret_acf in [−0.15,−0.05].
- **Determinism:** per-bot seeded RNG, ascending-aiUserId, no wall-clock in decisions, NO global feedback
  controller coupling RNG to aggregate state. Any balancing must be a deterministic per-(bot,shock) function.
- **Flag-gated, default-OFF, byte-identical when off.** Keep the v1 dials; add v2 ones default-inert.
- Keep the proven mechanism (real marketable chase into shocks, seed-price-sized, draw-free on a chase tick).

## Deliverable contract
ONE `git apply --check`-clean patch (default-off + byte-identical off), ships determinism + conservation tests
(symmetry helper is pure/seeded; chase flow conserves), touches nothing in `/Tools`, no churn. PLUS a ready-to-paste
bake prompt: apply→build (server/tests/MAUI)→`dotnet test`→**parallel A/B soak** (OFF vs ON, baked-realism env,
lowercase DB, absolute script path, ≥90 min, max 2 servers — local PG `max_connections` is now 300) → harvest
`scripts/bounce_diag.py` (CLOSE + **VWAP**) + `scripts/r4_realism_score.py`. **WIN = VWAP ret_acf in [−0.15,−0.05]
AND drift ≤5%/4h (≈baseline) AND excursion < ±20% AND clustering (absret) preserved AND CK=0.** Sweep
ChaserNotionalFrac (drift-neutrality should let it go higher than v1's 0.10-0.15 without drift). Bake the winning
config default-on only on a confirmed, drift-neutral, conservation-clean ret_acf win (re-run the winner once).

## Evidence (local Claude has it)
v1 soak data (kse_k0_*, kse_s1_*, kse_s2_*, kse_s3_* DBs + candle CSVs), `docs/REALISM_CEILING_INVESTIGATION.md`,
the v1 patch (`chaser-direct-flow-v1.patch`), and the per-interval `ChaserProbe` log lines (orders/suppressed/
netNotional). NOTE the CLOSE headline also needs the bounce fix (`docs/ultraplan-prompt-microstructure-bounce.md`,
the OTHER half) — v2 here handles the flow drift; the bounce is separate.
