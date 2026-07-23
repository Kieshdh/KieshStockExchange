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
Both changes alter decision behavior, so — matching the repo's inert-first norm (all v2 pillars default OFF)
— **flag-gate both and default them OFF.** Flag-off must reproduce today's behavior byte-for-byte; the new
behavior is enabled + validated in the soak, and a *later* commit flips the defaults (and bakes the /Tools
band-centering, §8) only once proven. **Coupling:** the `MaxCashReservePrc` band-centering changes the OLD
edge logic too (bots would hit the max edge sooner), so it must NOT ship in the default seed while Fix B is
off — apply it only when `CashHomeostasis:Continuous` is on (a soak-time DB `UPDATE` for now; into /Tools
only when the default flips). Flag-gate list:
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
- v8 (2026-06-11): added §10 — residual investigation after Greed + Continuous shipped. Captures the
  observed `−2.3 %/2.5h` plateau, what is now ruled out, and the candidate mechanisms (Itô floor,
  bracket-pop asymmetry, slippage-depth asymmetry, etc.) plus a 6-experiment plan.

---

## 10. Residual after Greed + Continuous shipped — investigation

**Where we are.** Fix A (`Bots:ExtremeReaction:GreedStyle = true`) and Fix B
(`Bots:CashHomeostasis:Continuous = true`, `MaxShift = 0.45`, `/Tools` recentered) are shipped and
default-on (validated 2026-06-10). After the sensitivity-tuning round on top, the avg drift over a
2.5h soak **plateaus around `−2.3 %`** with `medianAbs ≈ 3 %`, `max ≈ +11 %`, `beyond50 = 0`,
conservation `CK=CONS=ERR=0` throughout. The down-drift is **bounded and stable**, not runaway, but
there is a structural floor we have not yet removed. This section is about that floor.

This is inside the `≤5 %/4h` magnitude budget (`≤3.1 %/2.5h`), so the residual is **in-budget** and
not currently blocking. We document the cause-of-floor so a future round can act on it cleanly.

### 10.1 Measurement contract
Same harness as §4: `scripts/kse-balance-soak-p.ps1` samples `balance-drift.sql` every N seconds.
Drift tuple = `stocks, avg%, stddev%, medianAbs%, min%, max%, beyond50, beyond100, trades`. The
sign we care about is the **avg %**. Conservation has to stay `0/0/0`.

### 10.2 Ruled out by measurement (so we don't re-litigate these)
1. **Shorts.** Open-short interest ~3,820 shares vs 291M long (§1). Can't move a 73M-share market by 2%.
2. **Cash starvation.** Median bot free cash $723k (§1). Bots could buy if they wanted to.
3. **Money leak.** `CK=CONS=ERR=0` everywhere. The P6 short-collateral / cover-flip / TP-funding
   bugs (`docs/P6bc_DRIFT_FINDINGS.md`) were real drift sources but are all fixed; the persisted
   invariants confirm.
4. **News shocks.** Round-5 shock-stress drove `avg ≈ 0` (symmetric shocks cancel); calm soaks with
   shocks rare/off still show ~`−1.5 %/30m`, so drift is present *without* shocks.
5. **OverheatCap tuning.** Same plateau at cap 0.10, 0.12, 0.30. Cap shapes the max, not the sign.
6. **Anchor strength / scale.** 0.15 → 0.07 helped medianAbs; barely moved the avg plateau.
7. **The two shipped fixes themselves.** Greed killed the Panic 2:1 sell-skew; Continuous bounds the
   residual. Together they took the plateau from `−1.6 %` worsening → `−3.15 %` stable → `−2.3 %`.
   What is *left* is a different mechanism.
8. **BuyBias remnant.** Aggregate `BuyBiasPrc ≈ 0.494` — slight sell lean — explicitly out-of-scope
   per §1. Not "ruled out as contributor"; ruled out as a *fix path* for this round.

### 10.3 Candidate mechanisms for the residual

Ranked roughly by my prior on contribution. Each is testable.

#### 10.3a Itô / Jensen drift — *structural floor, can't be tuned away*
A random walk in **log price** with zero-mean log-returns drifts **down in level** by `≈ −σ²/2` per
period. The cash homeostasis pins price in **arithmetic** terms (`cash% = mid`); bot decisions act in
**proportional** terms (buyProb, slippage % bands, anchor relative deviation). The mismatch produces a
structural negative bias.

Estimate: `σ_30min ≈ 3%` per stock → `−σ²/2 ≈ −0.045 %/30min ≈ −0.09 %/h ≈ −0.225 %/2.5h`.
Small on its own; cannot be removed by tuning a probability lever. The only "fix" is to change the
homeostasis target so equilibrium = `seed × e^(+σ²/2)` instead of `seed`, or accept it as a floor.

Treat this as a **null hypothesis**: even with every per-bot bias zeroed, the market still drifts by
≈ this amount.

#### 10.3b Long-bracket vs short-bracket population asymmetry — *most likely the dominant remainder*

**Both directions have brackets, with TPs and SLs.** A long bracket fires a SELL on TP (closing the
long after a rally) or a SELL on SL (closing after a drop). A short bracket fires a BUY on TP
(closing the short after a drop) or a BUY on SL (closing after a rally). In principle the long-TP
sell-flow is cancelled by the short-TP buy-flow. In practice the **population** of active long
brackets vastly outnumbers the population of active short brackets, so the cancelling buy-flow is
present but small.

The skew comes from two layers, NOT just one:

1. **Mild prob-side seed skew.** Per-strategy ranges in `Tools/Config.py:181–197`:

   | Strategy | `long_bracket` mid | `short_bracket` mid | ratio |
   |---|---|---|---|
   | MarketMaker | 0.0125 | 0.0125 | 1.0 |
   | TrendFollower | 0.040 | 0.020 | **2.0** |
   | MeanReversion | 0.070 | 0.050 | **1.4** |
   | Random | 0.055 | 0.055 | 1.0 |
   | Scalper | 0.035 | 0.035 | 1.0 |

   2 of 5 strategies are long-skewed at modest ratios (1.4–2.0); the other 3 are symmetric. By
   itself this is small.

2. **Large structural eligibility-filter skew (the dominant layer).** The bracket builder enforces
   different eligibility per side (`AiBotDecisionService.cs:449,519–525`):

   - `LongBracket` calls `FirstLongableStock` → bot must be **flat OR long** on that stock.
   - `ShortBracket` calls `FirstFlatStock` → bot must be **flat** (`Quantity == 0`) on that stock.

   The seed gives every bot starting inventory, so for any (bot, stock) pair where the bot owns
   shares, **only long brackets are eligible**. Short brackets are filtered out before the prob
   roll even happens. A short bracket opens a NEW short (flat → negative inventory) with attached
   SL/TP; it does **not** attach to a plain sell that closes an existing long. Those plain
   close-out sells are naked, one-sided sell pressure with no balancing buy-flow at all.

   The down-drift-fix §1 measurement at v2 launch was **~3,820 short shares vs ~291M long shares**
   — five orders of magnitude. That's the substrate brackets attach to, and it's lopsided before
   any bot decision logic runs.

So even with symmetric prob seeds, the open-bracket population would still skew long because the
substrate skews long. The seed prob skew adds on top of that.

This is the leading explanation for the ~47% sell-skew at peak and is consistent with the drift
being *steady* (a population rate constant), not shock-driven.

Test plan refined:
- (i) Count bracket-child fills by side per minute over a 2h soak. Compare against open-bracket
  population by side (also sampled per minute). If the fill ratio matches the open-pop ratio, the
  population-skew hypothesis is confirmed.
- (ii) Sample open-long-bracket count vs open-short-bracket count every minute over a 2h soak.
  Expected: a large stable skew (10:1 or worse) consistent with the substrate.
- (iii) A/B against a config where `ShortProb` is bumped *and* `LongBracketProb = ShortBracketProb`
  per strategy. If the substrate-skew theory is right, just rebalancing bracket probs without also
  raising the open-short substrate won't move the needle — both knobs need to move together. This
  is a stronger experiment than the original §10.4 #6 which only touched the bracket probs.

#### 10.3b-2 Generalise bracket eligibility to all position signs — *direct fix for the substrate asymmetry*

Today's eligibility filters (`AiBotDecisionService.cs:449,519–525,528…`) restrict each bracket kind
to one side of the position axis:

| kind | eligible when | entry side | bracket on |
|---|---|---|---|
| `LongBracket` | `qty ≥ 0` (`FirstLongableStock`) | market BUY | new long inventory |
| `ShortBracket` | `qty == 0` (`FirstFlatStock`) | market SELL | new short position |

So a bot with existing inventory can never get a `ShortBracket` (which would buyback below), and a
bot with an existing short can never get a `LongBracket` (which would resell above). The naked
plain sells that close longs (and naked plain buys that close shorts) carry no balancing flow at
all — this is the substrate asymmetry §10.3b is pointing at.

**Chosen direction: extend both kinds to all position signs, with flip allowed.** Both kinds become
eligible on `qty < 0`, `qty == 0`, and `qty > 0`. When the entry quantity exceeds the existing
position size (with sign), the order **flips** the position in one trade. Concretely:

| starting `qty` | kind | entry effect (entry qty `Q`) |
|---|---|---|
| `+X` | `ShortBracket` | sells `min(Q, X)` from inventory + opens `max(0, Q − X)` of new short |
| `+X` | `LongBracket` (today) | adds `Q` to the long; SL/TPs sit below/above |
| `−X` | `LongBracket` | covers `min(Q, X)` of the short + opens `max(0, Q − X)` of new long |
| `−X` | `ShortBracket` (today) | not eligible (only flat is) — would scale into short |
| `0`  | either | today's behaviour |

The two new mixed cases — `ShortBracket` on a long, `LongBracket` on a short — split into TWO
portions at settlement:

1. **The inventory-close portion** is a round-trip. The sell proceeds (for `ShortBracket` on long)
   fund the buy-limit TPs below; the buy outlay (for `LongBracket` on short) covers the short and
   releases collateral that funds the sell-limit TPs above. No new collateral pool needed for this
   portion.
2. **The flip-into-new-position portion** is structurally identical to today's brand-new
   `ShortBracket` or `LongBracket`. Requires the existing collateral / cash reservation logic in
   `BuildBracketAsync` — but sized to the *flip* quantity only, not the whole entry.

This subsumes the `LongRoundtrip` earlier sketch — there's no separate kind, just an eligibility
extension on `ShortBracket` (and its mirror on `LongBracket`).

##### Engine touchpoints (Ultraplan-grade scope)
The flip portion is what makes this Path 2 (vs the no-flip Path 1 that was the original sketch).
Allowing brackets to flip position sign **reopens the long↔short flip path** that the P6 cover-clamp
exists to forbid. The clamp is the invariant that stopped the CK_Positions cascades in
`docs/P6bc_DRIFT_FINDINGS.md`; this section's plan has to either *carve a bracketed-only exception*
around the clamp or replace it with a richer invariant.

The touchpoints:

1. **`AiBotDecisionService.BuildBracketAsync`** (`:446–516`)
   - Remove the eligibility filters (`FirstFlatStock` / `FirstLongableStock`) — replace with
     "first watchlist stock the bot can fund a bracket on regardless of sign".
   - Compute `inventoryPortion = min(Q, |currentQty|)` (only when sign differs) and
     `flipPortion = Q − inventoryPortion`.
   - Sizing: cash/collateral checks must size the SL pool / buy budget to the `flipPortion` only.
     The inventory portion is self-funding.
   - SL / TP price computation unchanged.

2. **`AiBotDecisionService.ComputeOrderQuantityAsync` + cover-clamp helpers**
   - `ComputeCommittedCoverShares` / `ComputeCommittedSellShares` need to know the bracketed-flip
     route is **allowed** — today they assume any new advanced order respects the no-flip clamp.
     Either gate the clamp on `IsBracketChild == false` for plain orders, or thread a `permitFlip`
     flag through.

3. **`TradeSettler` — buyer/seller consume paths**
   - The mixed entry (e.g. `ShortBracket` on `+X`, qty `Q > X`) settles as one order ID but spans
     two position transitions: long→flat (proceeds = X × fill_price, available cash up) and
     flat→short (collateral reserved on the position for `Q − X` shares).
   - Need to confirm `Position` write path can persist a single trade that takes `qty: +X` to
     `qty: −(Q − X)` in one update without violating CK invariants mid-write. May require a
     two-step internal transition (long→flat, then flat→short) under a savepoint, similar to the
     P6 buy-to-cover handling.
   - Buyer/seller fund consume on the inventory-close portion is structurally identical to a plain
     long-close sell / short-cover buy; the new behaviour is only on the flip portion.

4. **`BracketCoordinator` — SL/TP pool accounting**
   - SL pool sized to the `flipPortion`, not the entry qty. The inventory portion's TP buybacks
     are funded from the realised cash (not a reserved pool) so they don't claim against
     `Fund.ReservedBalance`.
   - This is a NEW pool-sizing rule: today the pool is `slWorst × entryQty`; new is
     `slWorst × flipQty`. The coordinator's cushion accounting (the `OnChildFillShortAsync` bug
     class from P6bc) needs to use the new sizing — risk of regression here is real.
   - Pool resize on partial TP fills currently subtracts a pro-rata fraction of the entry qty;
     under the new rule the pro-rata must be over the flip qty.

5. **Reservation invariants + reconciler**
   - The reconciler asserts `Σ position ShortCollateral == Σ fund ReservedBalance` modulo SL pools
     + live buy reservations. The mixed bracket adds a new combination it has to validate.
   - Likely needs a small extension to the `ReservationAuditor` test catalogue covering the
     flip-portion case (`Σ CSR per (user, ccy, position)` invariant against the actual position).

6. **`/Tools` per-strategy prob seeds**
   - Per-strategy `LongBracketProb` / `ShortBracketProb` in `Tools/Config.py:181–197` keep their
     current meaning — "per-tick probability of TRYING to attach a bracket of this kind". With the
     wider eligibility a roll succeeds more often; may need a downward adjustment so the total
     bracket attempts/tick stays in the budget the v2 soaks were calibrated against. Re-seed
     `AIUserData.xlsx` after final value set.

##### Why this is the right shape but the wrong moment to implement
- **Right shape:** removes the population-skew floor the §10.3b hypothesis is pointing at without
  introducing a new advanced kind; symmetric by construction; per-strategy prob seeds already in
  the pipeline. Gives bots a usable round-trip and flip-bracket vocabulary they should have had.
- **Wrong moment:** the P6 cover-clamp exists because flip paths previously corrupted positions
  and funds at scale; touching it requires the same careful Ultraplan + soak loop the original P6
  rounds used. Should NOT land alongside the current sensitivity-tuning bake.

##### Suggested sequencing
1. Land the bake of the converged sensitivity-tuning config (current Step 3 soak).
2. Run §10.4 experiments 1 + 2 + 6 against the baked config to *measure* the substrate skew
   empirically. If the measured `LongBracketPop / ShortBracketPop` exceeds ~5:1 with ~47 % sell
   flow, the §10.3b hypothesis is confirmed and Path 2 is justified.
3. Hand off this section to Ultraplan as a stand-alone plan doc
   (`docs/bot-bracket-flip-eligibility.md`). The Ultraplan round should explicitly cover the
   cover-clamp exception design, the mixed-position settlement, the SL pool resizing rule, and the
   `/Tools` rebalance.
4. Implement behind a flag (`Bots:Advanced:BracketFlipEligibility`, default OFF) per the same
   inert-first norm as every other v2/P6 lever. Build + tests + dedicated bot-soak A/B against the
   flag.

#### 10.3b-3 Design extensions and refinements

Stuff I would not bury in the first Ultraplan round but think hard about up front so the design
shape supports them.

##### E1 — Inventory-aware decision branching (kills the population skew at the decision layer)
Today's `BuildAdvancedAsync` picks a bracket kind by **prob roll alone**, then asks the eligibility
filter whether a stock matches. With wider eligibility the bot has more stocks to pick from but
still rolls direction blindly. Better: **bias the kind by current inventory state**:

```
if (heavyLong  > threshold) → prefer ShortBracket (round-trip out of the over-long position)
if (heavyShort > threshold) → prefer LongBracket (round-trip cover of the over-short position)
if (~flat)                  → today's behaviour (prob roll picks side)
```

This makes each bot a *position mean-reverter* by default. Across the population, the long-heavy
substrate keeps generating ShortBrackets (sell-flow); the short-heavy substrate keeps generating
LongBrackets (buy-flow). The drift force §10.3b is pointing at gets *cancelled at the source* rather
than balanced after the fact. Add a per-bot `InventoryBiasPrc` (0 = ignore inventory, 1 = always
direction-flip toward flat); default ~0.5. This is the cleanest single lever for the residual
drift — easier to tune than collateral mechanics.

##### E2 — Per-portion TP and SL geometry
The inventory-close portion and the flip-into-new portion have **different risk profiles**:

- **Inventory-close.** You already own the shares. The "loss" if price moves against you is
  ephemeral — you keep the shares, no forced cover. Want tight TP (intraday round-trip),
  optionally **no SL** (just hold).
- **Flip-into-new.** New directional risk. Want a wider TP (give the new position room) AND a
  wider, harder SL (real loss protection on a position you didn't originally have).

Today's bracket builder uses ONE distance for both. Allow the two portions to draw from
*different* TP/SL distance bands:
- Round-trip portion: `TpOffsetRtMin / Max`, optionally `IncludeSL = false` (already supported by
  §3.6 P4 TP-only brackets — see `project_tponly_brackets`).
- Flip portion: today's `TpOffsetMin / Max` + `StopOffset`, always with SL.

Adds 2 prob fields per bot (or 2 global config keys), no schema fork.

##### E3 — Watchlist picker priority
With wider eligibility the picker has more candidates. Priority order should be:
1. First stock where round-trip beats flip (inventory >= entryQty after clamping). Cheapest in
   collateral (none).
2. Then flat stocks (today's behaviour for fresh brackets).
3. Then stocks where flip is required (collateral cost).

Single ordering pass, no new structures. This naturally biases brackets toward the cheap round-trip
path the substrate makes plentiful.

##### E4 — Risk-adjusted SL on round-trips
The round-trip "SL" — if we wanted one — is fundamentally different from a flip SL. A round-trip
SL is a "give up the round-trip and accept the inventory at the entered level"; a flip SL is a
"close the new position at a loss". The first is a no-op (the bot already wanted the shares); the
second is loss-bearing. Default: **round-trip portion has no SL** (TP-only). If a `RtSlOffset` is
configured per bot, it triggers a *plain sell* of the still-held inventory portion at the SL price
rather than a buy-back. This is structurally equivalent to a normal long-close and avoids the
buyback collateral arithmetic on a portion that doesn't have a pool.

##### E5 — Round-trip-bias config (per-bot prob decomposition)
Today's two probs (`LongBracketProb`, `ShortBracketProb`) keep their meaning. Add one new field:

```
RoundtripBiasPrc ∈ [0, 1]   # bot's preference for round-trip vs flip when both possible
```

At decision time, when the bot can either round-trip (entry qty ≤ |inventory|) OR flip
(entry qty > |inventory|), the qty is drawn from a distribution biased by `RoundtripBiasPrc`:
- 1.0 = always size entry to `≤ |inventory|` (always round-trip, never flip)
- 0.0 = always size entry to `> |inventory|` (always flip)
- 0.5 = roughly 50/50

Single new column through the `/Tools` pipeline (`Tools/Config.py` per-strategy range,
`Tools/Person.py` assignment, `Tools/ExcelLayout.py` column add, server `AIUserRow` /
`AIUserMapper` / `PgDBService` Dapper + EF migration — the SAME drill the Lateness bug taught us
(see `cc6d863`); easy to forget the Dapper constants).

Suggested per-strategy ranges:
- MarketMaker: 0.5 (symmetric)
- TrendFollower: 0.2 (prefers flip — trend bets)
- MeanReversion: 0.8 (prefers round-trip — mean-reversion thesis)
- Random: 0.5
- Scalper: 0.7 (quick round-trips, occasional flip)

##### E6 — Telemetry the soak A/B needs
Add three counters to `BotTelemetryCache` (or wherever the BOT-level metrics live):

- `BracketEntries{Kind, Mode}` where `Mode ∈ {RoundTrip, Flip, MixedRtPlusFlip}`
- `BracketFillsBySide{Kind, Side}` — bracket-child fills broken down by buy/sell, already useful
  for §10.4 experiment 1
- `RoundtripCloseRate` — fraction of round-trip TPs that actually fire vs expire

Telemetry is *mandatory* for the post-deploy verification — without it we can't tell whether the
population skew is actually dropping. Land the telemetry **in the same PR** as the eligibility
change, not in a follow-up.

##### E7 — Interaction with the v2 imbalance regime (Kirman shared shock)
The Kirman regime makes a slice of bots commit-together to a directional side. With Path 2:
- In an **up regime**, the bots that swing bullish want LongBrackets. With wider eligibility,
  short-holding bots can now bracket-cover — converting their shorts to longs *with* a profit
  target. This is real positive feedback on the up regime.
- In a **down regime**, mirror: long-holding bots ShortBracket out of inventory.

This is intentional and probably desirable (regimes get clearer flows), but worth verifying in the
soak that the regime amplitudes don't blow past the OverheatCap. A defensive option: gate the
inventory-aware picker (E1) on `RegimeAlignment` so it doesn't FIGHT the regime — it amplifies it
when aligned, stays neutral when not.

##### E8 — Concurrency / staleness of inventory snapshot
Decision-time reads `_accounts.GetPosition(user.UserId, stockId)?.Quantity` to compute the
inventory portion. Settlement happens later. If a plain order on the same (user, stock) fires
between decision and settlement, the inventory at settle-time differs from decision-time. Today's
bracket builder is already robust to this (it just opens what it intends to open), but Path 2
introduces a NEW failure: the round-trip portion was sized assuming inventory `X`, settlement sees
`X − ε` (some other order ate a piece). Now the entry sells more than the bot owns → unintended
flip-portion appears.

Two mitigations:
1. **Clamp at settlement.** At settle time recompute the round-trip portion from CURRENT inventory
   (held-shares vs total entry qty). Any remainder is the flip portion. Requires the settler to
   know the order is a bracket entry (already true via `IsBracketParent`).
2. **Per-user serialization gate.** The existing money-probe parallel-group race fix already
   provides per-(user, currency) serialization. Confirm bracket-entry order placement happens
   inside that gate so concurrent plain orders are sequenced. (Likely already true via the entry
   route.)

This is exactly the class of bug that bit P6b/c — must be designed-in, not patched-on.

#### 10.3b-4 Open questions for Ultraplan

1. **Should the cover-clamp invariant become "no flip in plain orders, flip-OK in bracket entry"
   or "no flip in any single order ever"?** The first preserves the v2 safety net for plain trades
   and carves a controlled exception. The second would require splitting the bracket entry into
   two physical orders (close + new-position), which gives clean accounting but doubles the
   reservation footprint. Recommendation pending — Ultraplan should weigh the settler complexity
   trade.
2. **Per-portion SL pool sizing — derived from flipQty only, or sized to include a round-trip
   safety net?** The round-trip portion arguably wants zero pool (no SL → no pool). The flip
   portion wants a normal pool. If we ever add a round-trip SL (E4), it's a *sell* not a buyback,
   so it doesn't claim against `Fund.ReservedBalance`. Pool sizing seems clean: `slWorst × flipQty`,
   period.
3. **Does the existing `BracketCoordinator.OnChildFillShortAsync` pool-resize formula** (P6c
   2026-06-07 fix in `OnChildFillShortAsync`, see `docs/P6bc_DRIFT_FINDINGS.md` ROOT CAUSE + FIX
   section) **still hold when the bracket is mixed-portion?** The bracket-local
   `poolDrop = sl.CurrentBuyReservation − desiredPool` form is what's correct; verify the
   "desired pool" calculation accounts for partial round-trip fills shrinking the flip portion's
   notional remaining.
4. **Do we keep `LongBracket` / `ShortBracket` as two enum entries or merge into a single
   `Bracket` kind parameterised by direction?** Two entries preserve the per-strategy
   `LongBracketProb` / `ShortBracketProb` semantics in `/Tools`. Merging would require renaming
   that to e.g. `BracketProb` + `BracketDirectionBias`, more `/Tools` churn. Recommendation: keep
   two entries; cleaner migration.
5. **TTL on the round-trip TP** — should an un-filled round-trip TP expire after N hours? Today's
   brackets have no TTL. A round-trip whose TP never fires is a long-term holding decision; the
   bot might want to cancel and try a different round-trip. Probably defer to v2 with a flag.

#### 10.3c Trailing-stop population asymmetry — *same shape as 10.3b*
Long-trailing-stop fires as SELL on a pullback; short-trailing-stop fires as BUY on a bounce. Same
population-asymmetry story on the trailing sub-population. Lower volume than bracket children but
real.

Test: count trailing-stop fires by side; same ratio expected.

#### 10.3d Slippage-cap depth asymmetry — *possible but smaller*
`_marketSlippagePrc = 0.003` caps a market order's worst price symmetrically across `isBuy`
(buy at `bestAsk × 1.003`, sell at `bestBid × 0.997`). The asymmetry comes from the **depth
distribution** the cap acts on, not the cap formula. If the resting-bid ladder is consistently
thinner than the resting-ask ladder, a market sell sweeps deeper before the cap binds → larger
price excursion per share than the symmetric buy → faster down-ticks than up-ticks over time.

The bid/ask depth ratio is already in the soak harness's `depth=` tuple.

Test: graph `total_bid_depth / total_ask_depth` over a 2h soak. If consistently `< 1`, this
hypothesis is alive.

#### 10.3e Cancellation re-reserve asymmetry — *unlikely but cheap to check*
Cancelled BUY releases reserved CASH; cancelled SELL releases reserved SHARES. Homeostasis reads
`cash%`. If buy cancellations consistently outnumber sell cancellations (e.g. because the buy
reservation price floor undercuts the moving anchor more often than the sell ceiling), each cancel
batch ratchets `cash%` up → homeostasis pushes more sells next tick.

Test: cancel counts by side per minute.

#### 10.3f Limit-tier reference is moving / trailing — *needs verification*
Limit-tier offsets placed relative to `Fundamental()` move *with* the anchor and stay symmetric. But
if the tier reference is a **traded-price EWMA / LastPrice**, the trailing reference ratchets down
asymmetrically as sell flow prints downticks → next-tick sell tier is closer to mid → fills first →
another downtick. Classical microstructure feedback loop.

Test: read `ChooseLimitPrice` in `AiBotDecisionService.cs` and confirm whether the reference is
`Fundamental` or a trade-derived EWMA. If trade-derived, this loop exists and contributes.

#### 10.3g Cash-injection vs drift balance — *calibration note, not a fix path*
`P_eq ∝ totalCash`. Cash injection is on and growing totalCash, so the *equilibrium* price the
homeostasis aims at is drifting **up** slowly. The observed *down* drift therefore **understates**
the true down-force — the down-force ≥ `(injection-driven upward slope) + 2.3 %/2.5h`.

Not a fix path; a calibration note. The down-force is larger than the bare aggregate suggests.

#### 10.3h Geometric vs arithmetic shock recovery — *overlap with 10.3a*
A symmetric percentage shock pair (−10% then +10%) returns price to `0.9 × 1.1 = 0.99`. Even with the
news-shock generator zero-mean in *log* terms, realized price after a shock pair lands `~σ_shock²`
below start. Same Itô floor as 10.3a; contribution per shock-pair at `ShockMaxMagnitude = 0.20` and
~1 shock/h is `≈ −0.04 %`, scarcely measurable. Mostly subsumed by 10.3a.

#### 10.3i Engine-path measurement contamination — *unlikely after P6, worth confirming*
`balance-drift.sql` reads the deviation of `last traded price` from seed. If `last traded price`
is updated *before* a settlement rollback completes, it can read the would-be-fill price even on
a rejected trade. P6 removed the settle-rollback paths that produced this, but a sanity-check that
the SQL reads the same `Stock.LastPrice` snapshot the matching engine commits is cheap.

### 10.4 Proposed experiments (cheapest-first)

Each experiment **either kills a candidate or sharpens a measurement**. None of them ship code; all
are observational diagnostics on top of the existing harness.

1. **Per-side fill telemetry.** Patch the bot soak script (or a side-process) to count, per minute,
   bracket-child fills by side, trailing-stop fires by side, cancellations by side. Confirms or
   kills **10.3b / 10.3c / 10.3e**. Cheapest single test.
2. **Bid vs ask depth ratio over time.** Already in the `depth` tuple; plot
   `total_bids / total_asks` over a 2h soak. Confirms or kills **10.3d**.
3. **`ChooseLimitPrice` reference-source audit.** Read the code; identify whether the tier reference
   is `Fundamental`, a TradedEwma, or LastPrice. Confirms or kills **10.3f**.
4. **σ²/2 calculation per stock.** From the soak's per-tick price log, compute realized per-stock
   `σ²/2` and compare to realized avg drift per stock. If they match, **10.3a / 10.3h are dominant**
   and we have a floor we cannot tune away.
5. **Zero-injection soak.** Run a 2h soak with cash injection disabled. If avg drift gets *worse* by
   exactly the expected injection slope, **10.3g is confirmed** and the un-injected residual is the
   true down-force we are measuring.
6. **Bracket-population balance soak.** Run a 2h soak with `LongBracketProb = ShortBracketProb`
   (force-seed equally). If avg drift improves materially, **10.3b is confirmed** as the dominant
   remaining cause and the fix is per-strategy probability rebalancing in `/Tools`, not code.

Experiments 1 and 2 collect data from the same run. 5 and 6 are config-only A/B soaks on ports
5080/5081 with the existing parallel harness.

### 10.5 Outcomes that would close this investigation

- **A. Identify and remove the dominant residual force** (most likely 10.3b — bracket-pop rebalance)
  and confirm the new plateau is within `−1 %/2.5h`.
- **B. Decompose the residual into 10.3a + 10.3b + 10.3g** with measured magnitudes that sum to the
  observed `−2.3 %/2.5h`. Then accept the Itô floor (10.3a), rebalance bracket pops (10.3b) as a
  *partial* fix, and document.
- **C. Accept the current `−2.3 %/2.5h`** as in-budget (it is: `≤5 %/4h` ↔ `≤3.1 %/2.5h`) and close
  this with a documented "known and bounded" entry in `bot-sensitivity-tuning-report.md` and
  `bot-market-realism-v2-plan.md`.

Recommendation: **B**. Itô is real and not removable by tuning; bracket-pop is removable by
reseeding; everything else is rounding. Experiment order **1 → 4 → 6** gets there with two A/B
soaks plus a side-process diagnostic.

### 10.6 What §10 deliberately does NOT do
- Does not propose a code change. Every fix path that emerges is either a config flip, a `/Tools`
  reseed, or its own Ultraplan-grade plan doc.
- Does not re-litigate the shipped Greed + Continuous fixes (§§2–8); those are baseline.
- Does not propose changing the equilibrium target away from `seed`. The `/Tools` recenter to
  `mid ≈ seed cash%` is correct by the closed-market equilibrium math (§2.4); moving the target
  invites a different drift problem.

### 10.7 Reading-list pointers for the next implementer
- `AiBotDecisionService.cs` — `Continuous` cash homeostasis (`:571…`), `Greed` style (`:1349–1388`),
  reference-price reads (for 10.3f), slippage-cap reads (for 10.3d).
- `OrderBook.cs` — depth aggregates; `SumQuantity(buySide)` is what experiment 2 reads.
- `TradeSettler.cs` — buyer / seller consume paths; relevant only if 10.3i ever needs revisiting.
- `BracketCoordinator.cs` — bracket child fire paths; relevant to 10.3b.
- `Tools/Config.py` + `Tools/Person.py` — the source of the per-strategy bracket-prob distribution
  that any rebalance in 10.3b would have to touch (per the `/Tools` pipeline contract in §8).
