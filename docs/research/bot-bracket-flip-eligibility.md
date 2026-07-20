# Plan: bracket-flip eligibility — kill the negative-drift residual at the population-substrate layer

**Branch base**: `feature/bot-market-realism-v2`, tip `f18530b` (bake of weighted-week anchor +
cap-from-seed + RecentAnchor + multiplicative pressure, validated on the Step 3 soak 2026-06-12).

**Motivation**: §10.3b of `docs/bot-down-drift-fix.md` hypothesised that the residual `−2.3 %/2h`
aggregate drift was caused by long-bracket vs short-bracket *population* asymmetry. An A/B
falsification soak (2026-06-12 00:20) confirmed it:

| Config | avg drift | max | min | medianAbs | trades | beyond50 / CK / CONS / ERR |
|---|---|---|---|---|---|---|
| Step 3 (advanced **ON**, 2h) | **−2.32 %** | +26.65 % | −13.21 % | 2.75 % | 333k | 0 / 0 / 0 / 0 |
| 5081 (advanced **OFF**, 30m) | **+0.54 %** | +3.44 % | −4.58 % | 0.68 % | 374k | 0 / 0 / 0 / 0 |

So advanced orders (brackets + stops + trailing + shorts) account for the entire negative-drift
plateau. The §10.3b hypothesis: brackets are the dominant member of that class because:

1. **Long brackets attach freely** (`FirstLongableStock`, eligible flat-or-long).
2. **Short brackets are flat-only** (`FirstFlatStock`, eligible only when `qty == 0`).
3. Open-long inventory is ~5 orders of magnitude larger than open-short inventory (3,820 short
   shares vs 291M long at v2 launch), so the substrate dwarfs even the prob-side asymmetry.

Plain sells that close longs are **naked** — they carry no balancing buy-flow because the existing
bracket system doesn't attach a buyback TP to them.

## 1. Chosen design — Path 2: full eligibility extension with flip allowed

Both `LongBracket` and `ShortBracket` become eligible on every position sign. When the entry
quantity exceeds the existing position size (with sign), the entry **flips** the position in one
trade.

| starting `qty` | kind | entry effect (entry qty `Q`) |
|---|---|---|
| `+X` | `ShortBracket` | sells `min(Q, X)` from inventory + opens `max(0, Q − X)` of new short |
| `+X` | `LongBracket` (today's) | adds `Q` to the long; SL/TPs sit below/above |
| `−X` | `LongBracket` | covers `min(Q, X)` of the short + opens `max(0, Q − X)` of new long |
| `−X` | `ShortBracket` (today's) | not eligible (flat only) — would scale into short |
| `0`  | either | today's behaviour |

The two new mixed cases split into two portions at settlement:

1. **Inventory-close portion** — round-trip on existing inventory. Self-funding (sell proceeds fund
   buy-limit TPs; cover outlay releases collateral that funds sell-limit TPs). **No new collateral
   pool needed.**
2. **Flip-into-new-position portion** — structurally identical to today's brand-new bracket.
   Existing collateral / cash reservation logic in `BuildBracketAsync`. **Sized to the flip
   quantity only, not the whole entry quantity.**

Allowing flip reopens the long↔short flip path that the P6 cover-clamp invariant forbids. Path 2
must either carve a *bracketed-only* exception around the clamp or replace it with a richer
invariant. (Alternative Path 1 = clamp to current position size, no flip — was considered and
rejected by the user. Path 2 is the chosen direction.)

## 2. Engine touchpoints

1. **`AiBotDecisionService.BuildBracketAsync`** (`:446–516`).
   - Remove the eligibility filters (`FirstFlatStock` / `FirstLongableStock`) — replace with
     "first watchlist stock the bot can fund a bracket on regardless of sign".
   - Compute `inventoryPortion = min(Q, |currentQty|)` only when current `qty` sign differs from
     entry side; `flipPortion = Q − inventoryPortion`.
   - Cash/collateral checks must size the SL pool / buy budget to `flipPortion` ONLY. The
     inventory portion is self-funding.
   - SL/TP price computation unchanged.

2. **`AiBotDecisionService.ComputeOrderQuantityAsync` + cover-clamp helpers**
   (`ComputeCommittedCoverShares`, `ComputeCommittedSellShares`).
   - These currently clamp plain orders to prevent flip. With Path 2, brackets need an exception.
   - Either gate the clamp on `IsBracketChild == false` for plain orders, or thread a `permitFlip`
     flag through.

3. **`TradeSettler` — buyer / seller consume paths**.
   - The mixed entry settles as one order ID but spans two position transitions: e.g. long→flat
     (proceeds = X × fill_price, available cash up) and flat→short (collateral reserved for
     `Q − X` shares).
   - Confirm `Position` write path can persist a single trade that takes `qty: +X` to
     `qty: −(Q − X)` in one update without violating CK invariants mid-write. May require a
     two-step internal transition (long→flat, then flat→short) under a savepoint, similar to the
     P6 buy-to-cover handling.
   - Buyer/seller fund consume on the inventory-close portion is structurally identical to a plain
     long-close sell / short-cover buy; the new behaviour is only on the flip portion.

4. **`BracketCoordinator` — SL/TP pool accounting**.
   - SL pool sized to `flipPortion`, not entry qty. Today's pool is `slWorst × entryQty`; new is
     `slWorst × flipQty`.
   - The coordinator's cushion accounting (the `OnChildFillShortAsync` bug class from P6bc — see
     `docs/P6bc_DRIFT_FINDINGS.md` ROOT CAUSE + FIX) needs to use the new sizing rule. Real
     regression risk here.
   - Pool resize on partial TP fills currently subtracts a pro-rata fraction of entry qty; under
     the new rule the pro-rata must be over flip qty.

5. **Reservation invariants + reconciler**.
   - `Σ position ShortCollateral == Σ fund ReservedBalance` modulo SL pools + live buy
     reservations. The mixed bracket adds a new combination it has to validate.
   - Extension to `ReservationAuditor` test catalogue covering the flip-portion case
     (`Σ CSR per (user, ccy, position)` invariant against the actual position).

6. **`/Tools` per-strategy prob seeds**.
   - Per-strategy `LongBracketProb` / `ShortBracketProb` in `Tools/Config.py:181–197` keep their
     current meaning ("per-tick prob of trying to attach a bracket of this kind"). With wider
     eligibility a roll succeeds more often; may need a downward adjustment so total bracket
     attempts/tick stays in the v2-soak-calibrated budget. Re-seed `AIUserData.xlsx` after final
     value set.

## 3. Design extensions (E1–E8) — should be designed-in, not bolted-on

### E1 — Inventory-aware decision branching (the structural drift fix)
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
LongBrackets (buy-flow). The drift force §10.3b is pointing at gets *cancelled at the source*
rather than balanced after the fact.

Add a per-bot `InventoryBiasPrc` ∈ [0, 1] (0 = ignore inventory, 1 = always direction-flip toward
flat). Default ~0.5. **This is the cleanest single lever for the residual drift — easier to tune
than collateral mechanics.**

### E2 — Per-portion TP and SL geometry
Inventory-close and flip-into-new portions have different risk profiles:
- **Inventory-close**: you already own the shares. Want tight TP (intraday round-trip), optionally
  no SL (just hold).
- **Flip-into-new**: new directional risk. Want a wider TP AND a wider, harder SL.

Add two distance bands (or two prob fields):
- Round-trip portion: `TpOffsetRtMin / Max`, optionally `IncludeSL = false` (P4 TP-only brackets
  already support this — `project_tponly_brackets`).
- Flip portion: today's `TpOffsetMin / Max` + `StopOffset`, always with SL.

### E3 — Watchlist picker priority
With wider eligibility, picker has more candidates. Priority order:
1. Stock where round-trip beats flip (inventory >= entryQty after clamping) — cheapest collateral.
2. Flat stocks (today's behaviour for fresh brackets).
3. Stocks where flip is required (collateral cost).

Single ordering pass, no new structures. Naturally biases brackets toward the cheap round-trip
path the substrate makes plentiful.

### E4 — Risk-adjusted SL on round-trips
A round-trip "SL" is fundamentally different from a flip SL. A round-trip SL is "give up the
round-trip and accept inventory at entered level" (no-op — bot already wanted the shares); a flip
SL is loss-bearing. Default: **round-trip portion has no SL** (TP-only). If a `RtSlOffset` is
configured, it triggers a *plain sell* of the still-held inventory portion at the SL price rather
than a buyback. Structurally equivalent to a normal long-close — avoids the buyback collateral
arithmetic on a portion that doesn't have a pool.

### E5 — Round-trip-bias config (per-bot prob decomposition)
Today's two probs (`LongBracketProb`, `ShortBracketProb`) keep their meaning. Add one new field:

```
RoundtripBiasPrc ∈ [0, 1]   # bot's preference for round-trip vs flip when both possible
```

At decision time, when the bot can either round-trip (entry qty ≤ |inventory|) OR flip
(entry qty > |inventory|), qty is drawn from a distribution biased by `RoundtripBiasPrc`:
- 1.0 = always size entry to `≤ |inventory|` (always round-trip, never flip)
- 0.0 = always size entry to `> |inventory|` (always flip)
- 0.5 = roughly 50/50

Single new column through the `/Tools` pipeline: `Tools/Config.py` per-strategy range,
`Tools/Person.py` assignment, `Tools/ExcelLayout.py` column add, server `AIUserRow` /
`AIUserMapper` / `PgDBService` Dapper + EF migration. **The Lateness bug (`cc6d863`) showed that
the hand-written Dapper SQL is easy to forget — explicitly verify the new column lands in
`AIUserCols`, `AIUserInsertCols`, `AIUserInsertVals`, and `UpdateAIUser` UPDATE SET list.**

Suggested per-strategy ranges:
- MarketMaker: 0.5 (symmetric)
- TrendFollower: 0.2 (prefers flip — trend bets)
- MeanReversion: 0.8 (prefers round-trip — mean-reversion thesis)
- Random: 0.5
- Scalper: 0.7 (quick round-trips, occasional flip)

### E6 — Telemetry the soak A/B needs (LAND IN SAME PR)
Three counters in `BotTelemetryCache`:
- `BracketEntries{Kind, Mode}` where `Mode ∈ {RoundTrip, Flip, MixedRtPlusFlip}`
- `BracketFillsBySide{Kind, Side}` — bracket-child fills broken down by buy/sell
- `RoundtripCloseRate` — fraction of round-trip TPs that fire vs expire

Telemetry is **mandatory** for verification. Without it we can't tell whether the population skew
is dropping. Same PR, not follow-up.

### E7 — Interaction with the v2 imbalance regime (Kirman shared shock)
- **Up regime**: bots that swing bullish want LongBrackets. With wider eligibility, short-holding
  bots can now bracket-cover — converting shorts to longs *with* a profit target. Positive
  feedback on the up regime.
- **Down regime**: mirror. Long-holding bots ShortBracket out of inventory.

Intentional and probably desirable. Verify in the soak that regime amplitudes don't blow past
`OverheatCap`. Defensive option: gate the inventory-aware picker (E1) on `RegimeAlignment` so it
doesn't *fight* the regime — amplifies when aligned, neutral when not.

### E8 — Concurrency / staleness of inventory snapshot
Decision-time reads `_accounts.GetPosition(user.UserId, stockId)?.Quantity`. Settlement is later.
A plain order on the same `(user, stock)` between decision and settle changes the inventory at
settle-time. Path 2 introduces a new failure: the round-trip portion was sized assuming inventory
`X`, settlement sees `X − ε` (some other order ate a piece). Entry sells more than the bot owns
→ unintended flip-portion appears.

Two mitigations:
1. **Clamp at settlement.** At settle time recompute `inventoryPortion` from CURRENT inventory.
   Any remainder is the flip portion. Requires the settler to know the order is a bracket entry
   (already true via `IsBracketParent`).
2. **Per-user serialization gate.** The existing money-probe parallel-group race fix provides
   per-`(user, currency)` serialization. Confirm bracket-entry order placement happens inside
   that gate so concurrent plain orders are sequenced. (Likely already true via the entry route.)

Exactly the bug class that bit P6b/c. Must be designed-in, not patched-on.

## 4. Open questions for Ultraplan

1. **Cover-clamp invariant**: Should it become "no flip in plain orders, flip-OK in bracket entry"
   (carve a controlled exception, preserve v2 safety for plain trades), or "no flip in any single
   order ever" (split the bracket entry into two physical orders, clean accounting but doubles
   reservation footprint)? **Decide and justify.**
2. **Per-portion SL pool sizing**: derived from `flipQty` only, or sized to include a round-trip
   safety net? (E4 says round-trip portion wants zero pool; flip portion wants normal pool. If
   round-trip SL is added later (E4 sell, not buyback), it doesn't claim against
   `Fund.ReservedBalance`. Pool sizing is `slWorst × flipQty`, period — confirm.)
3. **P6c `OnChildFillShortAsync` pool-resize formula**: does the bracket-local
   `poolDrop = sl.CurrentBuyReservation − desiredPool` form still hold when the bracket is
   mixed-portion? Verify the "desired pool" calculation accounts for partial round-trip fills
   shrinking the flip portion's notional remaining.
4. **Two enum entries vs one parameterised**: Keep `LongBracket` / `ShortBracket` as two enum
   entries or merge into a single `Bracket` kind parameterised by direction? Two entries preserve
   the per-strategy `LongBracketProb` / `ShortBracketProb` semantics in `/Tools`. Merging would
   require renaming + more `/Tools` churn. Recommendation: keep two entries; cleaner migration.
   **Confirm or override.**
5. **TTL on un-filled round-trip TPs**: Today's brackets have no TTL. A round-trip whose TP never
   fires is a long-term holding decision. Probably defer with a flag — but **decide and document
   the deferral.**

## 5. Hard constraints (non-negotiable)

- **Conservation gate**: CK=0, CONS=0, ERR=0 over a >100k-trade soak at the final config.
- **Flag-off byte-identical**: with `Bots:Advanced:BracketFlipEligibility = false`, behaviour is
  IDENTICAL to today (eligibility filters intact, mixed-portion code path untouched). Verified by
  a determinism test.
- **Loop-thread only** for new decision-side state; no async or thread crossings.
- **The cover-clamp invariant must be preserved for plain orders**. Any exception is
  bracketed-only and explicit.
- **`/Tools` Excel pipeline**: any new per-bot field (E5's `RoundtripBiasPrc`) flows through the
  full Excel/seed pipeline AND every Dapper SQL constant (Lateness lesson).
- **Throughput gate** (see §5.5): Path 2 ON must achieve **at least 6,000 trades/min** at 70-stock
  / ~20k-bot scale on `kse_soak` (≥ 2× today's `f18530b` baseline). NON-NEGOTIABLE.

## 5.5 Throughput / performance requirements — heavy optimization required

### The cost we're paying today
The 2026-06-12 A/B falsification soak measured an unexpected throughput delta:

| Config | trades/min | per-tick advanced budget |
|---|---|---|
| `f18530b` advanced ON, 2h | **2,780** | 100% (baseline) |
| 5081 advanced OFF, 30m | **12,453** | 0% |

Advanced orders consume ~78 % of the per-tick processing budget at scale. The hot paths are:
- **`BuildBracketAsync`** — per-bracket call: `FirstFlatStock` / `FirstLongableStock` watchlist
  iteration → `GetStockPriceAsync` → `IsOverBandAsync` (async!) → `StopOffset` →
  `ApplyDepthCapAsync` (async!) → trade-cap arithmetic. 8 dependent calls before a single decision
  is emitted.
- **`BuildAdvancedAsync` gate roll** — every tick, every bot rolls `r ∈ [0,1)` against
  `advProb = Σ five probs`. Reads 5 `AIUser` fields, branches into 5 builders by cumulative-prob
  range.
- **`IsOverBandAsync` per-stock-per-tick** — the cap-from-seed check (recently added) reads stock
  listings + computes deviation. Recomputed by every bot evaluating the same stock in the same
  tick. Trivially memoizable.
- **`BracketCoordinator` watcher updates** — fires per child fill, per cancel; state machine on
  every armed bracket.

### Path 2 adds to the hot path
Naive Path 2 ADDS:
- Per-decision `_accounts.GetPosition(user.UserId, stockId)?.Quantity` reads (already cheap, but
  one more dict hop per tick).
- Watchlist priority sort (E3) — currently linear scan; sort is O(N log N).
- Inventory-aware kind branching (E1) — additional position-state checks before prob roll.
- Round-trip vs flip qty calculation (E5) — bounded arithmetic.
- Settle-time inventory recompute (E8) — adds work to `TradeSettler` for bracket-parent orders.

If implemented naively, Path 2 makes the already-heavy advanced path heavier. The user's perf
budget cannot tolerate that. **Ship Path 2 alongside heavy optimization to the advanced surface.**

### Required optimizations (deliver in same patch series)

1. **Memoize `IsOverBandAsync` per (stockId, currency, tick)**. The answer is identical for every
   bot in the same tick. Cache on the existing `AiBotContext` (per-tick scope), invalidate on tick
   boundary. Likely a 5-10x win on the cap-check call count.

2. **Memoize `Fundamental()` / `SeedPrice()` / `GetStockPriceAsync()` per (stockId, currency,
   tick)**. Same reasoning. These are read once per stock per tick; today they're called by every
   bot's `BuildBracketAsync`.

3. **Precompute the per-(bot, tick) eligible-watchlist**. Today every advanced builder
   re-iterates the bot's watchlist filtered by `(_stocks.IsListedIn, position_qty)`. With Path 2
   + E3, this happens THREE times per bracket attempt (once for round-trip-preferred, once for
   flat, once for flip). Compute it ONCE per bot per tick, cache the priority-ordered list, pass
   it down.

4. **Sync `IsOverBand`** — the call is on the async path today but is functionally synchronous
   (reads from in-memory caches). Drop the async signature so we don't ConfigureAwait+Task
   allocate per bot per tick.

5. **Sync `ApplyDepthCap`** — same argument. `OrderBook.SumQuantity(buySide)` (per
   `OrderBook.cs:309`) is already sync; the wrapping `ApplyDepthCapAsync` only spawns Task
   overhead. Sync it.

6. **Ship A1b / A1c from the deferred bot-loop perf plan**. Per
   `project_bot_loop_perf.md` memory: "A1a SHIPPED+DEPLOYED (master ec3cf81): batched
   stop/trailing ARM route... A1b/A1c deferred." A1b/A1c batch the bracket-entry / coordinator
   submit path the same way A1a batched stop/trailing. Path 2 is the right time — read `A1a` and
   apply the same pattern to brackets.

7. **`BracketCoordinator` state-machine batching**. Coordinator events today fire one-at-a-time
   per child fill. Batch coordinator updates to end-of-tick. P6c regression risk: the
   `OnChildFillShortAsync` / `OnStopFiringShortAsync` order matters — batch but **preserve causal
   ordering** within each `(user, ccy)` group (the same gate the money-probe parallel-group race
   uses).

8. **Per-tick AIUser snapshot once, not per-decision**. Today `user.StopProb` etc. are read on
   every call. The AIUser object is a singleton per bot; field reads are cheap, but consolidating
   them into a `ref struct` snapshot at the start of each bot's tick avoids volatile re-reads in
   the hot loop. Micro-optimization but free.

### Perf gate to validate in the soak

- **Baseline**: `f18530b` advanced ON, 2 h soak, 70 stocks, ~20k bots = **2,780 trades/min**.
- **Path 2 ON + all listed optimizations**: same soak shape, **≥ 6,000 trades/min** (= 2× baseline,
  half the gap to the advanced-OFF 12,453/min). **If the soak misses this target, the patch is
  NOT mergeable** — bracket eligibility is meaningless if it tanks throughput.
- Stretch target: ≥ 9,000 trades/min.
- Reporting: include a trade-rate sample per 5-min sample line in the soak script output (already
  in the tuple) plus a final cumulative rate vs baseline. Add CPU% if cheap.

### Profiling guidance for Ultraplan
- Use `dotnet-trace` or BenchmarkDotNet on a 30-second snapshot. Top frames should be matching /
  settlement, NOT `BuildBracketAsync` or `IsOverBandAsync`. Today they're heavy because they're
  recomputed per-bot per-tick.
- Look for Task allocations on the bot loop (`AsyncMethodBuilder`). Every `async` on a per-bot
  per-tick path is a heap allocation candidate.
- The `AiBotContext` already exists as a per-tick scope — reuse it for memoization caches.

## 6. Recommended soak / tuning order

Match the §10 + sensitivity-tuning patterns. **Every soak reports the cumulative trades/min** as
the perf gate (§5.5).

1. Flag-off determinism (5 min) — byte-identical to current `f18530b`. Trade rate must match
   baseline 2,780/min ± 5 %.
2. **Perf-only run**: optimizations 1–8 from §5.5 ON, Path 2 OFF (20 min). Drift identical to
   `f18530b`; **trade rate must hit ≥ 5,000/min** (proves optimizations work in isolation).
3. + Path 2 ON, E1–E3 OFF, default `RoundtripBiasPrc = 0.5` (20 min) — measures the structural
   effect of just allowing mixed-portion entries.
4. + E1 inventory-aware picker (20 min) — measures the decision-layer drift fix.
5. + E5 per-strategy `RoundtripBiasPrc` ranges (60 min) — measures the per-strategy character
   preservation.
6. Full 2-hour soak with all extensions on (kse_soak template). Conservation gate every run.
7. `candle_realism.py` on the final config. Drift target: `|avg| < 0.5 %/2h` (vs current `−2.3 %`).
8. **A/B against `f18530b`** (baked baseline) on parallel ports 5080/5081 for the final hour.
   Acceptance: **drift improvement ≥ 1.5 pp AND trade rate ≥ 6,000/min** (§5.5 perf gate).

## 7. Files to read first (Ultraplan reading order)

1. This doc.
2. `docs/bot-down-drift-fix.md` §10 — the empirical motivation, the §10.3b A/B evidence, and the
   ruled-out list so you don't re-litigate.
3. `KieshStockExchange.Server/Services/BackgroundServices/Helpers/AiBotDecisionService.cs`:
   - `BuildBracketAsync` (`:446–516`) — primary target.
   - `FirstFlatStock` / `FirstLongableStock` (`:519–545`) — the eligibility filters you remove.
   - `ComputeOrderQuantityAsync` + cover-clamp helpers — the invariant you carve an exception in.
4. `KieshStockExchange.Server/Services/MarketEngineServices/TradeSettler.cs` — the mixed-position
   settlement path.
5. `KieshStockExchange.Server/Services/MarketEngineServices/BracketCoordinator.cs` —
   especially `OnChildFillShortAsync` and `OnStopFiringShortAsync` (the P6c pool-resize bug class).
6. `docs/P6bc_DRIFT_FINDINGS.md` — the previous round of conservation drift fixes; the
   `OnChildFillShortAsync` bug pattern is the regression risk to avoid.
7. `Tools/Config.py:181–197` + `Tools/Person.py` + `Tools/ExcelLayout.py` — the `/Tools` pipeline
   for the `RoundtripBiasPrc` field (E5).

## 8. Revision log
- v1 (2026-06-12): extracted from `docs/bot-down-drift-fix.md` §10.3b-2/3/4 as a standalone plan
  for Ultraplan handoff. Includes A/B evidence, Path 2 design, E1–E8 extensions, 5 open
  questions, hard constraints, soak order.
