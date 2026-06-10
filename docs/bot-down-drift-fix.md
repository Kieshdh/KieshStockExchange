# Plan: remove the bot market's structural down-drift

**Status:** in progress on `feature/bot-market-realism-v2`. Discovered during the v2 movement/texture soak:
once the chart actually *moves* (A1+A2+B+C), it drifts persistently **down** (avg drift −0.5%→−1.6%,
bounded: `beyond50=0`, conservation clean). Root-caused to three structural sell-leans, two of which we fix
here. This is balance-tuning of pre-existing decision logic, complementary to the v2 pillars.

> Revision log at the bottom (§9). Drafted then improved across 5 passes.

## 1. Diagnosis (measured, not guessed)

Down-drift is **not** shorting (only 3,820 shares short vs 291M long) and **not** cash starvation (median
bot free cash $723k; only 1 of 13,959 bots under $5k; 0.11% of cash reserved). It's **aggressive-sell flow**:
market sells (15,895) exceed market buys (11,909) by ~33% → more trades print at the bid (down-ticks). Three
structural causes:

1. **Extreme-reaction Panic always sells.** `BullDirection`/`BearDirection`: FOMO and Contrarian mirror
   correctly across bull/bear, but `Panic → Sell` on *both* sides ("take profit" up, "capitulate" down). So
   every extreme trigger is 2-sell:1-buy. v2 herding makes extremes fire more → amplifies the drift.
2. **Cash homeostasis is off-center, edge-only.** The old logic shifts `buyProb` *only* outside the band
   (`<Min` sell, `>Max` buy), neutral in between. Bots operate at ~19–23% cash inside a [16.6%, 37%] band —
   ~2pp from the sell edge but ~15pp from the buy edge — so rallies hit the sell edge fast while dips need a
   ~60% crash to reach the buy edge. Net: caps rallies, no dip support → a down-lean.
3. `BuyBias` averages 0.494 (slight sell lean). **Out of scope per decision — not fixing this one.**

## 2. The fix (two changes)

### Fix A — a buy-side counterpart to Panic (extreme reactions)
**Do not forget this — it is half the fix.** Rather than flip Panic's direction (which would make it a
duplicate of FOMO and erase the genuine "sell in fear" behavior), **keep Panic as-is and add a new style that
BUYS in both bull and bear** — the missing mirror of Panic's sell-in-both. Call it `Greed` (accumulate):
```
BullDirection: Greed → Buy   (chase / accumulate the rally)
BearDirection: Greed → Buy   (buy the dip / accumulate the crash)
```
Only Panic is unbalanced today: FOMO (follow) and Contrarian (fade) net to zero over balanced bull/bear, but
Panic nets SELL with no buy-both counterpart. Adding `Greed` with a population comparable to Panic's cancels
that net sell. **Balancing mechanism:** Scalper currently maps to Panic; split it deterministically ~50/50
**Panic vs Greed** (hash of `aiUserId`, so it's per-bot stable and reproducible) → the Scalper cohort becomes
net-neutral while half retain genuine panic-selling. Also add `Greed` to the out-of-character random pool in
`PickExtremeReactionStyle` (currently FOMO/Contrarian/Panic/None) so random picks stay symmetric.
**Invariant to hold:** aggregate extreme-reaction direction is net-neutral over balanced sentiment. (The live
`Panic→Buy` edit was reverted; this supersedes it.)

### Fix B — Continuous cash homeostasis (restoring to midpoint + hard edges)
Replace the dead-zone edge logic with a **continuous restoring force toward the band midpoint**, plus hard
forced choices at the boundaries (the user's spec: *"reach the boundaries → forced to choose; in between →
bias away from the edges"*):
```
cashMid      = (Min + Max)/2
cashHalfBand = (Max − Min)/2
cashDev      = clamp((cashPrc − cashMid)/cashHalfBand, −1, +1)
homeostatic += maxShift * cashDev          // in-band SOFT bias: above mid → buy, below mid → sell
if (cashPrc >= Max) homeostatic = max(homeostatic, 0.95)   // hard wall: too much cash → must buy
if (cashPrc <= Min) homeostatic = min(homeostatic, 0.05)   // hard wall: too little → must sell
```
Why it removes the down-lean: a price **fall** lifts everyone's cash% (shares shrink) → pushes above mid →
bots **buy** → price recovers; a **rise** lowers cash% → bots sell → falls back. Symmetric price-stabilizer
centered where `cash% = mid`, with no off-center bias.

### The midpoint MUST equal the seed cash% (the make-or-break detail)
The continuous force pins price to where `cash% = mid`. Seed cash% is **23.2%**; current band mid is **26.8%**
→ the market would still reprice *down* to the cash=27% level. **Set the midpoint ≈ 23%** (e.g., lower avg
`MaxCashReservePrc` 37%→~30%, keeping Min ~16.6% → mid ~23.3%) so the equilibrium sits at the seed price and
there is no residual drift.

### Equilibrium math (why mid = seed cash%)
In a closed market the resting price is where aggregate `cash% = mid`:
```
mid = totalCash / (totalCash + P_eq·shares)   ⇒   P_eq = (totalCash/shares) · (1−mid)/mid
```
At seed prices cash% = 23.2%, so `P_eq = P_seed` requires `mid = 0.232`. With the current mid 0.268,
`P_eq/P_seed = (1−.268)/.268 ÷ (1−.232)/.232 = 2.731/3.310 = 0.825` → price would settle ~17% below seed.
Centering mid at 0.232 removes that residual. Mid is **per-bot** (heterogeneous `Min`/`Max`), so it's the
*aggregate/portfolio-weighted* mid that sets `P_eq` — center the **distribution** there, not just the mean.

### Interaction with cash injection
`P_eq ∝ totalCash`. Cash injection raises `totalCash` over time → `P_eq` slowly *rises* (mild inflation) →
a gentle **upward** drift on top. That's benign (and partly offsets any residual down-lean), but means the
"resting price" tracks the injected cash, not a fixed level. If a flat resting price is wanted, the injection
rate sets the inflation slope; document it rather than fight it.

## 2.2 Strength tuning & anchor deconfliction (the central risk)
The old homeostasis was **edge-only** (zero force in-band). The new one is **always-on**, so reusing
`maxShift = 0.40` makes it far stronger in the normal range — and at 0.40 it dwarfs the v2 herd tilt
(`f·δ ≈ 0.025–0.10`) and momentum bias (0.175), so it would **pin price to the cash-equilibrium and erase the
excursions A1+A2 just created.** Two adjustments:
- **Separate the in-band gain from the edge force.** Use a *gentle* in-band slope (e.g. `maxShift ≈ 0.10–0.20`)
  so it bounds drift without flattening movement, while the **hard edges stay strong** (0.95/0.05) as the
  walls. The in-band force should be weaker than, or comparable to, the herding force — bound, don't dominate.
- **Deconflict with the existing value anchor.** The value anchor already pulls price toward fundamental
  (= seed). The continuous cash homeostasis now *also* pulls toward the cash-equilibrium price (≈ seed once
  mid is centered). Two anchors on the same target → double-damping → over-flat. Recommendation: with the
  continuous homeostasis on, **reduce `_valueAnchorStrength`** (or treat the value anchor as a far backstop,
  not the primary bound). The cash homeostasis becomes the main bounded restoring force; A5 fast-slack still
  governs intraday breathing room.

## 3. Determinism, flags & config
Both changes alter *default* decision behavior. To A/B them cleanly against the old logic and to attribute
each lever during the soak, **flag-gate both** (default to the new behavior once validated):
- `Bots:CashHomeostasis:Continuous` (Fix B) — off ⇒ the old edge-only logic verbatim (true byte-for-byte
  fallback). Expose `:MaxShift` (in-band gain, ~0.15), `:EdgeForceBuy`/`:EdgeForceSell` (0.95/0.05).
- `Bots:ExtremeReaction:GreedStyle` (Fix A) — off ⇒ no Greed style, Scalper→Panic as before; on ⇒ the
  50/50 Scalper Panic/Greed split + Greed in the random pool. Expose the split fraction.
Determinism: the Scalper Panic/Greed split is a **stable hash of `aiUserId`** (no new RNG, reproducible);
the continuous homeostasis is pure arithmetic on existing state. Both run loop-thread only. With every new
flag off, the stream is byte-identical to pre-fix — same inert-first discipline as the v2 pillars. Mid the
band-centering is data (DB/seed), not a flag.

## 4. Verification (attribute each lever)
A/B on the full-stack config (A1+A2+B+C on), flags toggled one at a time:
1. **Fix A alone** (Greed): extreme-reaction direction net-neutral; `avg drift` less negative.
2. **Fix B alone** (continuous homeostasis, mid centered): aggregate `cash%` converges to **~mid (23%)** and
   price **holds ~seed** (run `balance-drift.sql`: `avg`/`medianAbs` → ~0, `max`/`min` symmetric, `beyond50=0`).
3. **Both + full v2:** `avg drift ≈ 0`, conservation clean (`CONS=0`, `CK=0`), **and** `candle_realism.py`
   still shows movement + wicks — the must-not-flatten check (body/range, range CV, wick% unchanged vs the
   pre-fix full-stack run). If the chart flattened, `MaxShift` is too high (§2.2).
Sanity gauges: market-sell vs market-buy fill counts should converge (the asymmetry that started this);
aggregate `cash%` should track `mid`.

## 5. Risks
- **Over-damping:** a strong always-on restoring force can pin price and kill the v2 excursions. Tune
  `maxShift` so it bounds drift without erasing movement (it trades off against the herding/imbalance force).
- **Midpoint mis-set:** if mid ≠ seed cash%, the fix just relocates the resting price (down or up).

## 6. Files & permanence
- `AiBotDecisionService.cs`: new `Greed` enum value + `BullDirection`/`BearDirection` cases + the Scalper
  Panic/Greed hash-split in `PickExtremeReactionStyle` (Fix A); `ChooseOrderType` homeostatic block — the
  continuous restoring + hard edges (Fix B). Plus the two flags + config reads. (Local prototype edits were
  reverted — implement cleanly from the committed branch.)
- **Band centering must be permanent, not a one-off `UPDATE`.** A DB `UPDATE AIUsers` on `kse_soak` is fine
  for the soak but is **wiped on every reseed from the template**. For permanence the cash-reserve target
  *distribution* has to move in the `/Tools` seed generation (`Config.py`/`Person.py`: lower the
  `MaxCashReservePrc` draw so the per-bot **midpoint distribution centers on the seed cash% ≈ 23%**), then
  regenerate `AIUserData.xlsx` and reseed. Until then, the soak uses a scripted `UPDATE` after each reset.

## 7. Rollout
1. Implement Fix A (Greed) + finish Fix B wiring + the two flags; build Server + Tests; flag-off determinism.
2. Soak with a scripted post-reset `UPDATE` centering the band at ~23%; tune `MaxShift` (movement preserved)
   and the value-anchor reduction (§2.2); verify §4 with both monitors.
3. Once values are settled, bake the band centering into `/Tools`, regenerate + reseed for permanence.

## 8. Don't break other strategy properties; reflect property changes in /Tools (Excel)
Each strategy carries **many** per-bot properties beyond direction — `BuyBiasPrc`, `Min/MaxCashReservePrc`,
`TradeProb`, `DecisionInterval`, `Min/MaxTradeAmountPrc`, `AggressivenessPrc`, `UseMarketProb`,
`SlippageTolerancePrc`, `ExtremeReactionRandomnessPrc`, the limit-tier offsets, stop/TP distances, the
advanced-order probs, `CashInjection*`, watchlist, etc. — all seeded *by strategy* in `/Tools`. **These fixes
must leave every other property and the per-strategy character intact.** In particular the Scalper
Panic/Greed split must not alter Scalpers' other behavior, and the new `Greed` style only changes the
extreme-reaction *direction* for the split cohort, nothing else.

**Any per-bot property this fix changes MUST flow through the full Excel/seed pipeline** (the
`bot-market-realism.md` §5 pattern), or it's lost on the next reseed and the runtime/DB drift apart:
- **Band centering changes `MaxCashReservePrc`** → update `Tools/Config.py` (the cash-reserve draw params so
  the per-bot **midpoint distribution** centers on the seed cash% ≈ 23%, keeping the spread/variation),
  `Tools/Person.py` (assignment), `Tools/ExcelLayout.py` if columns shift, then `ExcelSeedService` read, and
  `AIUserRow`/`AIUserMapper`/`PgDBService`/EF + migration only if a *new* column is added (it isn't — the
  column exists; just its values move). Regenerate `AIUserData.xlsx` and reseed.
- If Fix A is made **data-driven** (a per-bot style column instead of the runtime hash-split), that new column
  needs the same end-to-end wiring. The recommended hash-split avoids this (no schema/Excel change for Fix A).

## 9. Revision log
- v1: initial draft (diagnosis, Fix A, Fix B continuous homeostasis + hard edges, midpoint=seed).
- v2 (pass 1): added the closed-market equilibrium math (`P_eq=(cash/shares)(1−mid)/mid`; mid=0.232 to hold
  seed) and the cash-injection→mild-inflation interaction.
- v3 (pass 2): reworked Fix A — keep Panic, add a `Greed` always-buy style, balance via a 50/50 Scalper
  Panic/Greed hash-split + Greed in the random pool (supersedes the reverted `Panic→Buy` edit).
- v4 (pass 3): §2.2 — over-damping risk (separate gentle in-band `MaxShift` from strong hard edges) and
  value-anchor deconfliction (reduce `_valueAnchorStrength` so the two anchors don't double-flatten).
- v5 (pass 4): flag-gating both fixes (`CashHomeostasis:Continuous`, `ExtremeReaction:GreedStyle`) for clean
  A/B + byte-identical fallback; config-exposed constants; hash-based (deterministic) Scalper split.
- v6 (pass 5): per-lever verification protocol; **band-centering permanence via `/Tools`** (DB UPDATE is wiped
  on reseed); explicit rollout sequence.
- v7: §8 — preserve all other per-strategy properties; any property the fix changes (esp. `MaxCashReservePrc`
  band-centering) must flow through the full `/Tools` Excel/seed pipeline. Local prototype edits reverted for
  a clean Ultraplan base.
