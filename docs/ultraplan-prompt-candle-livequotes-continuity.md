# Ultraplan handoff — Candle open/close continuity + live-quote↔candle unification

**Status (2026-07-04):** SECOND priority (behind the realism co-fire/sector/ship arc). Kiesh: "maybe this is part of a larger
change to the livequotes — maybe we need ultraplan for a larger fix." Written locally per the ultraplan workflow; hand off to the
cloud when Kiesh prompts; await approval before implementing.

## The problem (Kiesh's report)
On the client chart, adjacent candles' Open/Close should match (candle[N].Close == candle[N+1].Open) but on SHORTER timeframes
they very often don't — a visible discontinuity between bars. Kiesh chose the **"Both (fully continuous)"** convention: adjacent
candles match AND empty buckets fill flat = a fully gapless chart. (AskUserQuestion 2026-07-04.)

## Root-cause map (from a thorough Explore pass — file:line grounded)
The system is **GAPPED BY DESIGN**, and the intended display-time continuity is **not actually applied** anywhere on the chart:
- **`KieshStockExchange.Shared/Models/Candle.cs`** — Open = FIRST trade in the bucket, Close = LAST (within-bar model:
  `PriceChange => Close - Open`, `IsBullish => Close > Open`). Sole OHLC invariant `IsValidPrice()` = `Open/Close ∈ [Low,High]`
  (L165) — **no `Open == prevClose` invariant.** `ApplyTrade` (L214-248) re-anchors `Open=High=Low=px` on the first trade in
  MID-price mode (L232-235) ⇒ gapped; keeps the seed in last-trade mode.
- **`…Shared/Services/MarketDataServices/Helpers/CandleAggregator.cs`** — `NewCandle(start,lastClose)` (L190-197) seeds
  `Open=Close=High=Low=lastClose` (= prior close) but `ApplyTrade` immediately overwrites Close (and Open in mid mode). `FillGaps`
  (L168-188, gated by `FillGapsEnabled`) flat-fills empty buckets (`Open=…=Close=lastClose`, `MaxGapCandles=10`).
- **`…Server/Services/MarketDataServices/CandleService.cs`** — `AggregateCandles` rollup (L843-877) = `Open=first-sub.Open`,
  `Close=last-sub.Close` (faithfully preserves whatever continuity the sources had; fabricates none). `GetHistoricalCandlesAsync`
  has a `fillGaps` param **defaulting false** (L465). `ReplayTicksBuildClosed` builds with `fillGapsEnabled:true` (L598) ⇒ a
  transaction-REBUILT range persists FLAT-FILLED candles, while the Python export of the same trades is gapped = two shapes.
- **DEAD `fillGaps` end-to-end (the real bug):** `CandleController.cs:42` calls the overload WITHOUT `fillGaps` (⇒ false);
  `…/SignalRCandleService.cs:161` discards its arg (`_ = fillGaps;`). So although `ChartViewModel` requests `fillGaps:true`
  (L704, L806) the chart NEVER gap-fills — empty minutes arrive as holes.
- **Live vs historical disagree (the live-quotes entanglement):** the in-progress bar is synthesized from LIVE QUOTES
  (`…Shared/…/Helpers/LiveQuote.cs` + `…Server/…/Helpers/QuoteRegistry.cs` → SignalR → `ChartViewModel.TrySyncLiveCandle`
  L421-462, which seeds Open from "the first live price it sees" L435 and preserves it). On close, `UpsertCandle` (L832-859)
  replaces the synthetic bar with the server's authoritative candle — whose Open (first TRADE, gapped) can differ from the
  synthetic bar's Open. So the same bucket shows one Open live and another after close.
- **`…/Helpers/CandleChartDrawable.cs`** — `DrawCandles` (L723-753) draws each candle INDEPENDENTLY from its own O/H/L/C; nothing
  links `candle[N].Close` to `candle[N+1].Open`, so a gapped Open renders as a visible discontinuity (correct for gapped data).
  Hit-test/snap/volume/Y-range (`HitCandleIndex` L831, `SnapToCandleCenterX` L443, `DrawVolume` L619, Y-range L181) ALL iterate
  the same `Candles` list — so any display transform must keep the list the drawable renders and hit-tests IDENTICAL.

## What is NOT broken (do not touch)
- **Stored/DB candles + the Python CSV export are correct ground-truth** (`scripts/candle_export.py` reads raw `Transactions`,
  Open = first trade — the header states the intent: continuity is a DISPLAY concept). The realism analysis pipeline depends on
  this staying raw. **Any fix must be display-only for stored data.**

## The design question the ultraplan must settle
Deliver a **fully continuous chart** (Kiesh's pick) where LIVE and HISTORICAL agree, without corrupting stored data. Decide:
1. **Where continuity is applied** — a client DISPLAY transform (recommended: keep server/stored raw, transform at the
   ViewModel→drawable boundary) vs a server-canonical continuous series. Client-display keeps the CSV/analysis untouched and is
   the smaller blast radius.
2. **Continuity rule:** for each rendered candle set `Open := prevDisplayClose`, then extend `Low := min(Low, Open)` /
   `High := max(High, Open)` to keep `IsValidPrice` (the connecting move becomes the candle's wick). First candle keeps its true
   Open. Empty buckets (detected via `BucketSeconds` gaps) emit a flat filler `O=H=L=C=prevClose, Volume=0`.
3. **Live/historical consistency:** the synthesized in-progress bar (`TrySyncLiveCandle`) must seed its Open from the last
   DISPLAY close and, on close (`UpsertCandle`), the replacement must be run through the SAME continuity transform so the bar
   doesn't "jump" its Open at close. This is the live-quotes↔candle reconciliation that makes it "a larger change."
4. **fillGaps plumbing:** either revive it end-to-end (pass through `CandleController` + honor it in `SignalRCandleService`) OR
   fill client-side in the display transform. Client-side is self-contained and needs no server rebuild; reviving the flag fixes
   the latent dead code but is cross-layer. Pick one and remove/ænote the other so there's a single gap-fill path.
5. **Index consistency:** whatever produces the display series must be the SAME list the drawable renders AND hit-tests, or the
   crosshair OHLC readout / fill-marker snap will index the wrong bar. Simplest: build the display series in the ViewModel and set
   it as `drawable.Candles` (the crosshair then reads the display OHLC = what's drawn = correct).

## Two scopes (for Kiesh to choose at hand-off)
- **(A) Minimal cosmetic (contained, ~1 client file):** a pure display transform at the ViewModel→drawable boundary (continuity +
  client-side gap-fill). No server change; testable against the live soak server on :5083 without a server rebuild. Papers over
  the live-vs-historical Open disagreement (item 3) rather than truly unifying it.
- **(B) Proper unification (cross-layer, the "larger livequotes change" Kiesh senses):** one continuity convention applied
  consistently across the live-quote→synthetic-bar path, the historical fetch, and the close-reconciliation; revive OR retire the
  dead `fillGaps` so there's a single gap-fill path; make live and closed candles agree on Open. Touches Shared (CandleAggregator
  seed semantics), Server (CandleService/Controller fillGaps), and client (SignalRCandleService, ChartViewModel, drawable).

**Recommendation:** design (B) but with the CONTINUITY itself implemented as a client display transform (item 1 = client), so
stored/CSV stays raw; the "unification" work is making the LIVE synthetic bar + the fillGaps path consistent with that transform.
That gets Kiesh's fully-continuous chart, keeps the analysis data pristine, and closes the dead-flag + live/historical-disagreement
bugs — the genuinely "larger" part — without a risky server-canonical candle rewrite.

## Constraints / gates
- Stored candles + `candle_export.py` CSV stay RAW ground-truth (don't regress the realism pipeline).
- MVVM: display continuity is a client/ViewModel concern (CLAUDE.md — UI concerns in Views/ViewModels).
- Don't regress the live price line, volume bars, MA overlays, fill/trigger markers, or crosshair (all iterate `Candles`).
- Verify: adjacent candles match on 1m/5m; empty buckets fill; crosshair OHLC matches the drawn bar; live bar doesn't jump its
  Open at close; CSV export byte-unchanged. Eyeball on the client built against a server with data.
