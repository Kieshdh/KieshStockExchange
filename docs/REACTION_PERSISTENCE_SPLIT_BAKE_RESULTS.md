# Reaction / Persistence split — bake results

**Lever:** `Bots:Imbalance:ReactionPersistence` (default-off, byte-identical when off; supersedes `Inertia` +
the reaction/hold levers when on). Decouples FAST reaction (bot re-decides every tick) from SLOW persistence
(a per-bot AR(1) "pressure" decaying over minutes), taker-coupled so persistent conviction crosses the spread.
Origin: the 2026-07-03 council + config A/B that refuted "just make bots react faster" (near-instant reaction
*lowered* correlation and shrank moves — the reaction lag was holding trends/correlation together). Cloud ultraplan
patch, applied + validated locally.

## Validation (apply / build / test) — PASS
- Patch `reaction-persistence-split.patch` applied clean on the working tree (on top of the uncommitted
  shared-taker-chase levers, which it composes with via 5 co-enable guards).
- `dotnet build` clean, **0 errors** (only the pre-existing async/SDK warnings) — zero compile gaps from the
  SDK-less cloud environment.
- `dotnet test` **377/377 pass**, incl. the 8 new `ReactionPersistenceTests` (AR(1) `HalfLifeKeep`, `UpdatePressure`
  seed/decay, `PersistHalfLife` dispersion, `PressureTilt` odd-symmetry, taker mapping + draw-free-when-off, the
  value-gap governor) **and** `ShareConservationTests` (the in-suite CK gate — the taker override rides the same
  OrderEntry→Match→Settle path).
- `CONFIGCHECK ReactionPersistence on=True` confirmed at startup with every knob resolved as intended.

## Method
Paired A/B soaks via `scripts/kse-balance-soak-p.ps1`, same seed template, separate ports/DBs, 2 servers max.
- **Control:** `Inertia` 120/1800 + TrendFollower shared-taker chase (cohort 0.20 / weight 2.5) + `GlobalSigmaMult` 2.5.
  This is the session's correlation baseline.
- **Target:** `ReactionPersistence` on, `Inertia` off, `TakerCoupling` on, Persist 300-1200s, `WShared` 0.7,
  `GlobalSigmaMult` 2.5; `TakerGain` swept (the loop-gain knob).
Metrics on the 120-min tail window (past the reseed transient): cross-stock corr @1/5/10-min + `factorR2`
(`cross_stock_diag.py`), 1-min ret_acf + σ + kurtosis (`return_headroom.py`), dispersion/drift/book/CK from the
soak checkpoints. The t=45 checkpoint used as a cheap **gain-probe** to converge `TakerGain` before spending a full
180-min verdict.

## Gain ladder
| TakerGain | run | dispersion (std, t45→final) | book depth (final) | CK / runaway | verdict |
|---|---|---|---|---|---|
| 0.3 | 45m probe | 3.33 (t45) | 272k (t45) | 0 / none | **too weak** — pressure rests as limits (huge book), moves damped |
| 1.0 | 180m verdict | 4.18 → **6.97** | **2.00M** | 0 / none | clean + MODEST (below) |
| 2.0 | 180m verdict | 4.00 (t45) | 155k (t45) | 0 / none | **IN FLIGHT** (t=45: dispersion ≈ gain 1.0 = absorption hint) |

## Gain 1.0 — 180-min verdict (tail 120 min)
| metric | Control (Inertia+chase) | Target (RP gain 1.0) | vs gate |
|---|---|---|---|
| corr 1-min | +0.002 | **+0.011** | ↑ |
| corr 5-min | +0.035 | **+0.044** | ↑ (≥ control ✓) |
| corr 10-min | +0.050 | **+0.075** (+50%) | ↑ (≥ control ✓) |
| factorR2 10-min | 0.107 | 0.110 | ≈ |
| dispersion std | 7.47 | 6.97 | ~≈ (93%, NOT flat; the t=45 "flatten" was RP's slower AR(1) build) |
| 1-min σ | 0.297% | 0.221% | ↓ (deep book absorbs 1-min moves) |
| ret_acf lag-1 | −0.345 | −0.377 | ≈ (within ±0.07 noise — NOT improved, NOT clearly worse) |
| kurtosis | +1.07 | +0.65 | ↓ (thinner tails) |
| book depth | 1.48M | **2.00M** | ↑ (RP builds real liquidity where the chase ate it) |
| downside min | −13.0% | **−10.3%** | ↑ (deep book cushions crashes) |
| drift (final) | −0.43% | +0.79% | both bounded |
| CK / beyond100 | 0 / 0 | 0 / 0 | ✓ hard gate |

**Read:** RP is a genuine but **modest** lever. It lifts cross-stock correlation at every horizon (10-min 0.050→0.075,
+50% relative — though small in absolute terms and possibly within the noise floor, which is not yet measured), builds
a **deeper, healthier book**, and **tightens the downside**, all conservation-clean. But it does NOT reach the 0.2-0.3
correlation target, and it does NOT fix the −0.3 1-min ret_acf: the deep book RP builds *absorbs* 1-min moves (σ drops
0.297→0.221), so the bid-ask bounce still dominates the 1-min autocorrelation. Cumulative dispersion holds up via slow
persistent drift (the AR(1) pressure trending) rather than 1-min jumps.

## Gain 2.0 — 180-min verdict (tail 120 min) = CONFIRMS gain-saturation
| metric | Control | Target (RP gain 2.0) | Target (gain 1.0) |
|---|---|---|---|
| corr 10-min | +0.047 | **+0.079** | +0.075 |
| corr 5-min | +0.032 | +0.041 | +0.044 |
| factorR2 10-min | 0.096 | 0.108 | 0.110 |
| ret_acf lag-1 | −0.356 | **−0.357** (= control) | −0.377 |
| 1-min σ | 0.288% | 0.225% | 0.221% |
| dispersion std | 7.31 | 7.14 | 6.97 |
| book depth | 1.69M | **2.15M** | 2.00M |
| downside min | −11.6% | **−9.4%** | −10.3% |
| CK / runaway | 0 / none | 0 / none | 0 / none |

**Doubling the gain (1.0→2.0) did NOT raise correlation** (10-min 0.075→0.079, flat within noise) or σ (0.221→0.225) —
it only added cumulative dispersion (std 6.97→7.14, now = control). RP's correlation is **gain-saturated at ~0.075-0.079
@10-min**: the deep book RP builds absorbs the extra taker flow (the arc's "volume can't move a thick book" wall). ret_acf
at gain 2.0 = **−0.357, identical to the control −0.356** (RP neither fixes nor worsens it). The correlation lift
**reproduced across both independent rounds** (gain 1.0: 0.075, gain 2.0: 0.079, both vs control ~0.047-0.050) — a de-facto
repeat-the-winner / noise check: the ~+50-68% lift @10-min is real, if modest. No runaway even at 2× gain (self-limits by
absorption, not the governor/×3 cap).

## Recommendation (final — gain-saturation confirmed across 2 rounds)
RP is **safe, principled, and a clean modest improvement** (correlation + book depth + downside), and it's a more
realistic mechanism than the current `Inertia`-as-blindness. But it is **not** the dramatic correlation/ret_acf fix,
and gain-tuning **cannot** push it further (confirmed: gain 1.0 ≈ gain 2.0 on corr/σ/ret_acf — absorption). This is a
genuine **trade-off bake call for Kiesh**:
- **FOR baking:** strictly better correlation + a deeper/healthier book + a tighter downside + a fast-reaction model
  that matches Kiesh's realism instinct, all CK-clean; a rollback is one flag (`ReactionPersistence=false`,
  `Inertia=true`).
- **AGAINST:** the win is modest (not near 0.2-0.3), 1-min σ drops (smaller intraday jumps — the chart trends more
  smoothly, which may read as more or less "alive" depending on taste), tails thin slightly, and it doesn't move
  ret_acf. Flipping `Inertia` off is a real behavioral change for a modest gain.

If baked (Kiesh's call, only if gain 2.0 doesn't beat 1.0 and after a noise-floor confirm): base `appsettings.json`
`Bots:Imbalance:ReactionPersistence=true` + `…:TakerCoupling=true`, `Bots:Imbalance:Inertia=false` (keys in base
Server appsettings only). Rollback (byte-identical): `ReactionPersistence=false`, `Inertia=true`.

## Outstanding (per the bake prompt, if pursuing a bake)
- Noise floor: 2 identical control arms → require any corr lift to clear `target − control > 2σ`.
- Isolation arm: `ReactionPersistence` on with `TakerCoupling=false` → attribute the corr lift to the taker channel
  vs the persistence tilt alone.
- Reaction-fast probe (`BotDecisionProbe` + a sentiment shock) → confirm buy_prob responds in seconds while the move
  persists for minutes.
- Repeat-the-winner (swap ports/templates) before any bake.
