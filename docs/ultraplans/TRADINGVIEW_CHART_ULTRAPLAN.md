# TradingView-like Client Chart — Ultraplan (3 architects + 5-advisor council, Fable 5, 2026-07-13)

## Goal
Make the MAUI client candlestick chart "much more TradingView-like." Owner (Kiesh) is open to embedding
TradingView's `lightweight-charts` JS lib "if it's not too much work" — but not at the cost of breaking what works.

## The decision: **Path B (extend the native `IDrawable`), staged, FEEL-FIRST — with a Path-A tripwire.**

### Why (the gap-ledger, Architect C + First-Principles, 4/5 council)
The current chart is already a hand-rolled TradingView-lite (~1750 lines): pan, zoom, crosshair, OHLCV readout,
7 timeframes, volume (overlay+subpane), SMA/EMA, **draggable order-lines → Modify-Order modal (51 call-sites)**,
fill/trigger markers, client-side live-bar synthesis, 8 runtime themes. Run the ledger honestly:
- `lightweight-charts` gives **free** exactly what we already have (pan/zoom/crosshair/chart-types-are-trivial).
- It gives **nothing** for the actual gaps: **no drawing tools, no RSI/MACD** (those are Advanced-Charts-only).
- It **charges a month** to re-bridge the deep trading integration across a C#↔JS boundary + retires 1750 working lines.
⇒ This is an **integration+feature problem, not a rendering problem.** Native extension keeps the deep integration
and stays pure-C#/MVVM (CLAUDE.md: "prefer extending the current architecture"). "Free cross-platform gestures"
(the main Path-A win) is a win on a **non-goal** — the repo's primary target is Windows.

### The dissent + the tripwire (Contrarian + First-Principles)
The one thing that flips the answer: **does native `IDrawable` deliver the kinetic sub-pixel FEEL of TradingView
(≥~50fps inertial pan/zoom on a full candle+volume+MA scene)?** If it stalls at ~20fps after invalidation tuning,
rendering becomes the irreducible thing and Path A wins. **So Phase 1 IS the feel spike** — the feel-polish work is
needed for Path B anyway, and it produces the verdict in the first shippable week. No separate throwaway spike.
- **Tripwire → reconsider Path A** iff: (a) P1's flick-pan feel can't reach smooth after tuning, OR (b) a *full*
  drawing-tool SUITE (channels/pitchforks/text/brushes) is later demanded. Contrarian's standing rule: **do NOT build
  a full native drawing suite** — that's the one place a library genuinely wins.

## Feel > features (Outsider — the perception ranking that makes a chart READ as TradingView)
These ~3 days move Kiesh's needle MORE than a whole RSI engine:
1. **Right-edge whitespace + last-price axis tag with countdown-to-close.** The live bar breathing against a labeled
   tag ticking down to the bar close is *the* TradingView signature; a candle jammed into the axis screams "homemade."
2. **Crosshair magnet + floating OHLC legend** that updates per-bar as you scrub (traders read by scrubbing).
3. **Cursor-anchored kinetic zoom + coasting pan** (anchor-at-center or rubber-band is instantly felt).
4. **Wick/body rendering discipline** — 1px crisp wicks, clean body borders, exact up/down colors, no AA smear.
5. **Log / percent scale toggle** — one afternoon, real trader utility.

## Phased plan

### Phase 1 — chart types + FEEL (~1 week, ships alone, = the feel verdict)  ← START HERE
**Files:** `ChartTypes.cs` (add `enum ChartStyle { Candles, HollowCandles, Bars, Line, Area, HeikinAshi }`);
`ChartViewModel.cs` (`[ObservableProperty] ChartStyle chartStyle`, persist via Preferences; Heikin-Ashi = a derived
candle list so autofit/crosshair/orders reuse it); `ChartView.xaml` (style toggle by the timeframe strip, styled from
`ChartStyles.xaml`); `CandleChartDrawable.cs` (`switch(Style)` inside `DrawCandles` → `DrawCandleBody/Hollow/Bar/LineArea`;
shared X/Y transform untouched so orders/markers/MAs work on every style for free) + the 5 ranked feel-polish items
(last-price tag+countdown, crosshair magnet+floating legend, cursor-anchored zoom, right-edge whitespace, pan-invalidation
coalesce to 1/frame).
**Acceptance:** all 6 styles × 8 themes render; order-line drag + crosshair readout correct in each; HA legend shows RAW
OHLC; 1-min live bar updates in every style; **flick-pan 5k candles with no visible stutter — Kiesh eyeballs side-by-side
vs tradingview.com** (the FEEL test = the tripwire gate).

### Phase 2 — series-agnostic indicator sub-pane framework (~1–2 weeks)
Generalize the existing volume-subpane rect-split into a **pane/layer abstraction** (the `Draw()` is already
phase-decomposed sharing one `(plot, X, Y)` transform trio). Panes MUST accept **any time series, not just OHLC-derived**
(the design constraint that unlocks the moat below). Ship **RSI + MACD** (computed in C# beside the MA calc,
unit-testable), each in its own pane with independent y-scale + guide lines.
- **★ The sim's moat (Expansionist): a "Market Mood" Fear/Greed pane** — the bots' real `BotSentimentService`/activity
  field as a time-series pane synced to the chart axis. No real exchange can render its own ground-truth sentiment;
  for a human trading against 20k bots this is the doorway's hook. Build as P2's **3rd** indicator (after RSI/MACD prove
  the pane API). Queue: order-book depth heatmap, sector-correlation ribbon (new render types = real scope).

### Phase 3 — cross-platform gestures (optional / deferrable — Windows-primary)
Lift the `#if WINDOWS` pointer/wheel/keyboard handlers into a shared gesture layer (`PanGestureRecognizer` +
`PinchGestureRecognizer` + MAUI `PointerGestureRecognizer` fallback), keeping the Windows pointer path as the rich one.
Lower priority — a win on a non-goal.

### Phase 4 — drawing tools MVP, gated (~later)
Horizontal ray + trendline + fib retracement ONLY (anchored in time/price space so they survive pan/zoom via the
transform; persist per-stock via the existing data layer; reuse the order-line drag machinery). **Do NOT build a full
suite** — if a full suite is demanded, that's the Path-A tripwire (2-day WebView2 spike first).

## Repo-facts appendix (for the implementer)
- Rendering: pure `Microsoft.Maui.Graphics IDrawable` on a `GraphicsView`. No WebView/Skia anywhere.
- `KieshStockExchange/Services/MarketDataServices/Helpers/CandleChartDrawable.cs` (~1014 ln): `Draw()` phase-decomposed
  (`DrawCandles`~L723, `DrawVolume`, `DrawMovingAverages`, `DrawOpenOrderLines`, `DrawMarkers`, `DrawCrosshair`), all
  sharing `(RectF plot, Func<DateTime,float> X, Func<double,float> Y)` + inverse hit-tests (`PixelToPrice/Time`,
  `HitCandleIndex/Marker/OpenOrderLine`). Wilkinson `NiceRange` Y ticks, `ChooseTimeStep` X grid.
- `KieshStockExchange/Views/TradePageViews/ChartView.xaml` (323) + `.xaml.cs` (735, rich gestures behind `#if WINDOWS`).
- `KieshStockExchange/ViewModels/TradeViewModels/ChartViewModel.cs` (~1012): `_candleBuffer`, viewport/zoom, `IsYAutoFit`,
  MA/marker/order-line/fill collections, `StreamClosedCandles` (SignalR CLOSED only) + `TrySyncLiveCandle` (client live-bar
  synthesis from `IMarketDataService` ticks).
- Data: history `GET api/candles/by-stock-range/{stockId}/{ccy}?resolution&from&to` → `List<Candle>`; live via SignalR
  hub "CandleClosed". `Candle` = shared model (`KieshStockExchange.Shared/Models/Candle.cs`, has the HLMinFillSize wick filter).
- Enums: `ChartTypes.cs`. Indicators today: `MovingAverageCalculator.cs` (SMA/EMA). Styles: `Resources/Styles/ChartStyles.xaml`
  + `Themes/Theme.*.xaml` (8 themes; keys `ChartBull/Bear/Bg/Grid/Axis/Crosshair/...`). MVVM = CommunityToolkit.Mvvm.
- Path-A hedge (if the tripwire fires): Architect A's `IChartSurface` seam — both the native drawable and a HybridWebView
  implement one interface (`SetHistory/UpdateLiveBar/SetOrderLines/SetMarkers/SetTheme/SetTimeframe`), flag-gated rollback;
  `lightweight-charts` v5.2.0 standalone bundled offline as a MauiAsset; live-bar stays C#-side, coalesced ~10Hz.

## Fire-contract
Additive/reversible; each phase a small PR with a visual-diff acceptance against the current chart; nothing regresses the
order-line/fill/marker/theme integration; Phase 1 gates the whole path (feel verdict). UI work ⇒ Kiesh eyeballs each phase.
