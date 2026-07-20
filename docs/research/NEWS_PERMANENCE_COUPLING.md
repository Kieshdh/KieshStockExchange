# SPEC — Variable-Permanence, Decay-Coupled News Event (emergent, bot-driven)

**Status:** decisive synthesis. Ships as a `Bots:ExogShock:Permanence` extension. Byte-identical when off. CK=0 by construction (no price/balance writes; permanence is a bounded read-time anchor tilt fed by real taker flow).

**One-sentence thesis:** a news event draws a single latent "cleanliness" `z`; `z` jointly sets a **permanent residual-floor fraction** `α∈[0.30,0.90]` and a **transient decay half-life** `τ½` with `corr ≈ −0.6`; the event enters the market as a shock signal that **decays toward the raised floor `α·M` instead of toward 0**, and permanence *emerges* because sustained chaser flow + `AnchorTracksShock` re-rate the level the bots defend — no fundamental step is ever written.

---

## 1. GENERATIVE MODEL

### 1.1 The per-event draw

Given a signed magnitude `M` (existing Merton-style draw in `RandomShockSource`, unchanged), draw **one latent** per event and derive the pair:

```
z   ~ N(0,1)                                             # cleanliness: +z informational, −z hype
α   = clip( AlphaMin + (AlphaMax−AlphaMin)·Φ(z + AlphaSpread·n1), AlphaMin, AlphaMax )
τ½  = clip( TauMedianSec · exp(−Coupling·z + TauSpread·n2), TauMinSec, TauMaxSec )
n1, n2 ~ N(0,1) independent
```

- **`α` = permanent residual-floor fraction** (0.30–0.90, center ≈ 0.60). This is the number that determines realized permanence.
- **`τ½` = transient overshoot half-life** (5–40 min working range). This shapes *how the overshoot bleeds off*, not how much sticks.

### 1.2 The EXACT coupling `f`

- **Sign: NEGATIVE.** `corr(α, ln τ½) ≈ −0.6`. Clean/informational (`+z`) → **high α + short τ½** = "done quickly, gains a level." Hype (`−z`) → **low α + long τ½** = "big overshoot, slowly eases back to a small raised base."
- **Shape:** monotone through the single latent `z`; `α` via a probit (`Φ`) squash into `[AlphaMin,AlphaMax]`, `τ½` via a log-linear (`exp(−β·z)`) map. `Coupling=β=0.6`.
- **Jitter (load-bearing, anti-rig):** `AlphaSpread=TauSpread=0.40` are the σ of the independent noise terms `n1,n2`. This deliberately loosens the welded relationship to `|ρ|≈0.6`, **not** 1.0, so the joint draw naturally emits **~10–15% "rule violators"** (fast-pop-that-fully-reverts, slow-bleed-that-mostly-sticks). A deterministic `α(τ)` is the #1 rigged tell; the violators are what keep a human from cracking the pattern after ~10 events. **`Coupling=0` degrades cleanly to fully independent draws** — the Contrarian's fallback is a one-line config change, not a rebuild.

**Why NEGATIVE, and why it does NOT invert under the emergent path (the trap the Contrarian flagged):** a naive traded-price-EWMA design makes permanence = `f(hold-time ÷ EWMA-halflife)`, which would make *slow* events stick more — the opposite sign. We dissolve this by making **`α` the drawn residual floor of the shock signal itself**, and requiring the pulse to snap down to that floor and *hold*. `τ½` governs only the overshoot above the floor. Realized permanence ≈ `α × re-rate efficiency`, independent of `τ½`. The negative coupling then lives purely in the *visual archetype* (fast+sticky vs slow+hollow), which is exactly where the owner wants it, and never fights the anchor machinery.

### 1.3 Transient decay toward the RAISED base

Per-stock shock state becomes `(transient, residual, τ½)`:

```
on impulse:   residual  += α · M
              transient += (1−α) · M              # jointly soft-walled to existing Cap
per tick:     transient *= 2^(−dt/τ½)             # overshoot bleeds at the per-event half-life
              residual  *= 2^(−dt/ResidualHalfLifeSec)   # floor bleeds SLOWLY toward 0
GetShock()      = residual + transient            # total → FundamentalService anchor tilt
GetTransient()  = transient                       # fresh burst only → chaser cohort
```

Target trajectory (what the shock signal traces, before bot noise):
```
S(t) = M · [ α + (1−α)·2^(−t/τ½) ]
S(0)=M (full pop)   S(∞→residual bleed)=α·M (raised base)   never reverts past α·M
```

The transient decays **toward the raised base `α·M`, never toward 0** — the owner's point-3 invariant is encoded in the functional form, not in branching logic.

### 1.4 Both owner archetypes fall out of one draw

| | latent `z` | `α` | `τ½` | on-chart |
|---|---|---|---|---|
| **(a) "quick, gains a level"** | `+1.5` | ~0.85 | ~8 min | 3–10 candle burst, tiny overshoot wick, resumes sideways at the new height in ~15–20 min; pre/post texture identical |
| **(b) "slow ease-back to raised base"** | `−1.5` | ~0.42 | ~30–40 min | spike (peak 1.5–2.5× base), lumpy downhill glide with counter-rallies, **visibly flattens above origin inside the session** |

Same draw, continuum between them. **No archetype enum, no discrete 2-class coin flip** (a stamped-shape tell) — the archetypes are the tails of one continuous 2-tuple.

---

## 2. CONFIG — `Bots:ExogShock:Permanence`

Minimal knob set. **All keys inert at default; when `Enabled=false` the permanence RNG is never constructed, impulses carry `α=0/τ=0` sentinels, and `Tick` math is byte-identical to today's decay-to-zero.**

```jsonc
"Bots": {
  "ExogShock": {
    // ...existing keys unchanged...
    "Permanence": {
      "Enabled": false,            // master gate; false ⇒ BYTE-IDENTICAL (no RNG, sentinel impulses)
      "AlphaMin": 0.30,            // permanent-fraction floor (pure-liquidity spikes)
      "AlphaMax": 0.90,            // permanent-fraction ceiling (clean info events)
      "AlphaSpread": 0.40,         // σ of n1 — de-rigs the α↔τ line, seeds violators
      "TauMedianSec": 1500.0,      // transient half-life median ~25 min (sim-compressed)
      "TauSpread": 0.40,           // σ of n2
      "TauMinSec": 300.0,          // ~5 min clean-pop floor
      "TauMaxSec": 2400.0,         // ~40 min slow-bleed ceiling (session-visible flatten)
      "Coupling": 0.6,             // β: fast-settle↔high-permanence; 0 ⇒ independent draws
      "ResidualHalfLifeSec": 10800.0,  // permanent floor slow bleed (~3h ≈ session-permanent)
      "PermRngSeed": 0,            // 0 ⇒ derive as RngSeed ^ 0x3B (dedicated stream)

      // --- Aftershocks (§ below); Lambda=0 ⇒ inert ---
      "Aftershock": {
        "Lambda": 0.6,             // Poisson mean follow-ups (0..~3, mostly 0–1)
        "DelayMedianSec": 300.0,   // lognormal delay median ~5 min
        "DelaySpread": 0.6,
        "MagFracMin": 0.3,
        "MagFracMax": 0.7,
        "SameSignProb": 0.7,       // same-sign dominance = emergent PEAD/continuation
        "Decay": 0.5,              // mag_k *= Decay^k
        "MaxDepth": 1              // NO aftershocks-of-aftershocks (bounded, non-branching)
      },

      // --- Per-tier means on the SAME draw (one table, no duplicated subsystems) ---
      "Tiers": {
        "Individual": { "AlphaShift":  0.00, "TauMult": 1.0, "LambdaAfter": 0.6 },
        "Sector":     { "AlphaShift": -0.10, "TauMult": 1.6, "LambdaAfter": 0.4 },
        "Global":     { "AlphaShift": -0.22, "TauMult": 2.4, "LambdaAfter": 0.3 }
      }
    }
  }
}
```

**Per-tier semantics:** tiers are the *same event object* differing only in `(AlphaShift, TauMult, LambdaAfter)` applied to the base draw plus the fan-out set. Global subsumes today's `GlobalShock`/co-fire narrative role (co-fire remains the *flow* mechanism the global tier triggers; `DownBias` stays there). Sector launches on the existing `stockId % SectorCount` modulo as-built — real `ISectorMap` is a later, design-untouched upgrade.

**The THREE knobs Kiesh tunes on the chart** (everything else ships at these defaults and stays fixed): `TauMedianSec` (how long the ease-back takes), `AlphaMin`/`AlphaMax` as a range (how much of a circled move survives), `Coupling` (how visually distinct the two archetypes are).

---

## 3. BOUNDING + CK + ANTI-RIGGED GUARDS

### 3.1 No unbounded ratchet (the #1 failure mode)

- **Per-event clips:** `α∈[0.30,0.90]`, `τ½∈[300,2400]s`, `|M| ≤ ShockCap`.
- **Accumulated permanent floor is seed-relative and hard-capped:** `GetShock` composes onto the anchor as `target = current × (1+shock)`, bounded to `seed × [1 ± (Band + ShockCap + CoMoveShiftCap)]` (existing `FundamentalService.Get` clamp) AND vetoed by the **geometric ×3/÷3 `AbsoluteCapMax=2.0`** cap. Floors stacking across many events can never exceed this envelope.
- **`ResidualHalfLifeSec=10800`** bleeds the accumulated permanent component back toward seed on a ~3h scale — session-permanent but **not eternal**, so 24h of events cannot ratchet without bound. This is the days-scale bleed the Contrarian named as mandatory.
- **Per-stock refractory period:** a new impulse on a stock with a live event **accumulates into** existing `(residual, transient)` and overwrites `τ½` (newer event wins) rather than spawning a parallel stacked floor — no mid-event floor-stacking.

### 3.2 Sign mix / no up-only ratchet

- Magnitude sign is drawn as today (with global-tier `DownBias` intact). **No asymmetric α or magnitude compensation** for the `DipBuyStrength=2.0` down-news-sticks-less effect — it is an emergent, half-real asymmetry; **instrument it, don't pre-tune it** (pre-compensation is itself a rigging tell and risks the Stage-1 drift cure).
- **Guard:** instrument **news-attributed net drift per stock per 24h** with an alarm threshold; the symmetric `seed×[1±…]` excursion cap is the hard backstop.

### 3.3 Anti-rigged (chart-tell) guards

- **Jitter `≥0.40`** on both marginals ⇒ `|ρ|≈0.6` not 1.0 ⇒ ~10–15% rule-violators (built-in, not a separate mechanism).
- **Continuous marginals only** — no discrete archetype classes, no constant overshoot ratio (peak/base spans 1.1×–2.5× and includes no-overshoot events via the draw).
- **Decay must arrive as lumps, not a smooth exponential** — the chaser cohort firing over multiple ticks + aftershock steps + the **undamped walk riding on top** break the synthetic exp silhouette. **NEVER damp the walk during decay**, and never let the re-rated anchor defend so hard that post-event 1-min σ falls below pre-event σ (a volatility crush is a tell).
- **Most events invisible by design** — with the existing magnitude exponent the median event drowns in the walk. Target ~1–3 "hmm" moves and ONE circle-able event per 2–4 sessions. Do not raise the floor to make news "noticeable"; rare big events (8–12%) carry realism.

### 3.4 CK / conservation

- Zero orders, zero balance writes on the permanence path — it only changes a **read-time anchor tilt**. CK=0 is inherited unchanged. The *flow* (chaser/co-fire) is the existing, already-CK-clean machinery.
- **Variance budget:** news ≤ 15–25% of per-stock return variance. If emergent permanence overshoots the budget, cut `ChaserNotionalFrac` / pulse amplitude — **never the walk**.

---

## 4. IMPLEMENTATION vs EXISTING CODE

All paths under `KieshStockExchange.Server\Services\BackgroundServices\Helpers\`.

**A. `ExogenousShockService` — decay-to-floor (the core ~15-line change).**
Per-stock shock state `double _shock` → `(double transient, double residual, double tauSec)`. `Tick` (:101–111) replaces the single global decay factor with per-entry `transient *= 2^(−dt/tauSec)` and `residual *= 2^(−dt/ResidualHalfLifeSec)` (≤~50 active entries, trivial cost). Expose **`GetShock()` = residual+transient** (→ anchor) and **`GetTransient()`** (→ chaser). Entry drops only when both `< floor`. Add a `Residual` column to the `ShockSample` CSV.

**B. `RandomShockSource` — the (α,τ) draw + sentinel.**
Extend `ShockImpulse` → `(StockId, SignedMagnitude, PermanentFraction=0.0, DecayHalfLifeSec=0.0)`. `0.0` = sentinel ("legacy: no residual, use global `DecayHalfLifeSec`"). Draw `(α,τ½)` per §1.1 in `Poll`, on a **dedicated `PermRngSeed = RngSeed ^ 0x3B`** stream drawn *only when `Enabled`* — so per-stock/global/sector RNG streams stay byte-identical mid-migration (same pattern as `GlobalRngSeed`/`SectorRngSeed`). Global/sector pulses draw ONE shared `(α,τ½)` for the cohort.

**C. `FundamentalService` — NO change beyond turning on `AnchorTracksShock`.**
`Get()` (:162–190) already composes `target = current × (1+shock)` bounded to `seed×[1±(Band+ShockCap+CoMoveShiftCap)]`. A shock that decays to a nonzero floor **is** a persistent, bounded anchor re-rate. **Do NOT write a fundamental log-step** (prior plan's CODE #2 — CUT; it double-counts with `AnchorTracksShock` and violates the emergent constraint). **Gap A (adaptive anchor re-rating toward realized traded price — BlendWeight/FastHalfLifeSec/MaxTotalExcursion) is DEFERRED to v2.** v1 uses `AnchorTracksShock`; a traded-price EWMA anchor can't tell news from noise (it permanent-izes every walk excursion, a positive-feedback runaway risk) and must soak clean first.

**D. Chaser age-gate — KEEP unchanged, now load-bearing.**
The chaser reads **`GetTransient()`** (fresh burst) so it chases the *pop*, while **`GetShock()`** (incl. residual) sustains the *anchor* without generating perpetual taker flow. This split is exactly what separates the fast burst from the durable floor. The chaser is the only thing that reliably moves the mark — the anchor converts that sustained flow into a stuck level.

**E. Aftershocks — a `JumpService.AfterState` timer-wheel inside the scripted source (separate PR).**
Reuse the `NextFireUtc` pattern to emit follow-up `ShockImpulse`s (NOT raw `JumpService` orders), each a full mini-event with **its own `(α,τ½)` draw**. `MaxDepth=1` (non-branching — a recursive cascade is an unbounded-seed hazard). Same-sign `p=0.7` is the emergent PEAD/continuation channel.

**F. Emergent orchestration.** A thin `NewsEventService` (Gap D) drives the scripted source, arms aftershocks, optionally raises `BotSentimentService._shock` for the slow-sentiment tilt. `IShockSource.Poll(simTick)` keeps everything deterministic/replayable. Reserve DTO metadata fields (`EventId, ParentId, Tier, Sign, MagBucket, ClarityBucket=quantized z, SimTick`) for a future news feed — build zero UI now.

**Companion flips to activate the emergent stack** (existing keys, owner-gated — flipping `AnchorTracksShock` true is a character change): `ExogShock:Enabled=true`, `AnchorTracksShock=true` (startup-refused unless `CapFromSeed && Band+ShockCap < AbsoluteCapMax` — holds at defaults: 0.12+0.06 < 2.0), `ChaserNotionalFrac>0`.

### Unit tests (proof obligations)

1. **Byte-identical off:** `Enabled=false` ⇒ no `PermRng` construction, sentinel impulses (`α=0/τ=0`), `Tick` output bit-equal to pre-change; other RNG streams bit-equal mid-migration.
2. **Coupling sign:** over N=10k draws, `corr(α, ln τ½) ∈ [−0.7,−0.5]`; `Coupling=0` ⇒ `|corr| < 0.05`.
3. **Marginals:** `α∈[0.30,0.90]` mean ≈ 0.60; `τ½∈[300,2400]`; violator fraction (fast∧low-α ∪ slow∧high-α) ∈ ~8–18%.
4. **Decay-to-floor:** single impulse `M>0` ⇒ `GetShock(t→∞ before residual bleed) → α·M ± ε`; `transient` monotone-decreasing; `GetShock` never crosses below `α·M` from above.
5. **Bound:** any impulse sequence keeps composed anchor within `seed×[1±(Band+ShockCap+CoMoveShiftCap)]` and inside `AbsoluteCapMax` ×3/÷3; `ResidualHalfLifeSec` drives accumulated residual → 0 with no live impulses.
6. **Refractory:** second impulse on a live stock accumulates `residual/transient` and overwrites `τ`, does not spawn a parallel entry; `shockId` hysteresis (chaser cohort) unchanged.
7. **Split exposure:** `GetTransient` excludes residual; `GetShock == transient+residual`.
8. **Aftershock:** `N~Poisson(Lambda)` capped, `MaxDepth=1` (no grandchildren), `mag_k = parent·U(MagFracMin,MagFracMax)·Decay^k`, same-sign rate ≈ `SameSignProb`; `Lambda=0` ⇒ zero follow-ups.
9. **CK/conservation:** full soak with feature on ⇒ CK=0, ConservationProbe clean (no orders/balances touched by the permanence path).

---

## 5. SUGGESTED STARTING NUMBERS (eyeball tier)

| Knob | Start | Rationale |
|---|---|---|
| `AlphaMin / AlphaMax` | 0.30 / 0.90 | owner range; center ≈ 0.60 (metaorder permanent 0.5–0.7, news higher, liquidity lower) |
| `AlphaSpread / TauSpread` | 0.40 / 0.40 | `|ρ|≈0.6` + ~10–15% violators |
| `TauMedianSec` | 1500 (25 min) | fast pops settle ≤20 min, slow bleeds flatten inside the session |
| `TauMinSec / TauMaxSec` | 300 / 2400 | 5 min clean-pop … 40 min slow-bleed; **NOT** the doc's 5400 (90 min ⇒ 4.5h to settle = invisible) |
| `Coupling` | 0.6 | archetypes clearly separable; 0 = homogeneous |
| `ResidualHalfLifeSec` | 10800 (3h) | session-permanent, days-scale un-ratchet |
| `Aftershock.Lambda` | 0.6 | mostly 0–1 follow-ups |
| `Aftershock.SameSignProb` | 0.7 | emergent PEAD "5% up → −2% → +3% later" |

**Acceptance gate is the eyeball test, NOT the numbers:** circle a move, say "that was news," and for archetype (b) *point at where it flattened above the old level*. If you can't point at the flat, `τ½` is too long or `α` too low. If every session has a clean textbook event, amplitude/rate is too high — most events must be invisible.

---

## APPENDIX — reconciling the opposite-sign literature (put this in the design doc so nobody "fixes" it)

- **Single-event overshoot-unwind → NEGATIVE coupling** (Chan 2003 newsless-reverts / De Bondt–Thaler overreaction): the part that bleeds off slowly is the part that was never information. This is the `(α, τ½)` draw. Fast=sticky, slow=hollow.
- **Multi-event continuation → POSITIVE** (PEAD / underreaction): a slowly *developing story* accretes same-sign permanent floors and keeps stepping the *same* direction. This is the **aftershock cluster**, each child with its own `(α,τ½)` draw.

These are **different mechanisms, not a contradiction.** Do not let anyone re-sign the single-event coupling to match PEAD — PEAD lives in the recursion.

**Decisive cut list:** hardcoded 0.80 / fundamental log-step (CODE #2); Gaussian copula (latent-z is the same joint with 3 fewer knobs); explicit PEAD/drift parameter (it's the aftershock recursion); discrete archetype classes; per-event adaptive-anchor-speed override; asymmetric down-news magnitude compensation; Gap A traded-price EWMA anchor (defer to v2 after v1 soaks clean); `τ½ > ~40 min` in the default draw (rare "regime" tier only).