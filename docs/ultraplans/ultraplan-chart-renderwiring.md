# Ultraplan — Chart deferred render-wiring (5 tools)

Feasibility → 3 architects → council teardown DONE (2026-07-20). This is the FIRE PROMPT for local implementation.
Branch `feature/bot-market-realism-v2` (client-only MAUI; **no money/CK**). User visually tests after (one commit/feature).

## Goal
Wire up 5 chart features whose MATH/MODELS ALREADY EXIST — only render + input/hit/drag/UI wiring is missing:
Fibonacci retracement, Text tool, Position tool, Alert line, Indicators (RSI / Bollinger / VWAP).

## Council verdict (the load-bearing decision)
This is a **dispatch problem, not a render problem.** There are **FOUR parallel `switch/if-else on d.Kind` sites**,
all ending in a `Trend/Ray` fallthrough, so a new Kind that only gets a render branch will **draw but be
un-placeable / un-hittable / un-draggable**. Every new Kind MUST get an explicit arm at ALL FOUR:
1. **Render** — `DrawingRenderer.DrawDrawings` (the if/else on `d.Kind`).
2. **Hit-test** — `ChartHitTester.HitDrawing`.
3. **Placement** — `ChartView.Windows.cs` Priority 0.6 (currently hardcoded tool lists).
4. **Body-drag** — `ChartView.Drawing.cs DragDrawing`.
Persistence + undo are ALREADY tool-agnostic (flat `List<DrawingObject>`) → zero changes. `DrawToolPresets.For(tool)`
is already a per-tool descriptor table (rows exist for Text/Position/Alert/Fib) — GROW it, don't build a new registry.
Approach: **insert explicit new-Kind branches before the Trend/Ray fallthrough** (do NOT rewrite the 11 working
tools into a switch — lowest regression risk); formalize only the placement site via a `PlacementKind` on the preset.

## Repo facts
- `Models/ChartDrawing/Tools/DrawTool.cs` — enum has Text, Position, Alert; FibRetracement is reserved.
- `Models/ChartDrawing/Objects/DrawingObject.cs` — one record struct; Position uses P1=Entry/P2=Target/P3=Stop/Qty/Direction(±1); Text uses `Text`.
- `Models/ChartDrawing/Objects/FibonacciLevels.cs`, `PriceAlert.cs` — math/models.
- `Services/MarketDataServices/Helpers/{Rsi,Bollinger,Vwap}Calculator.cs` — indicator math.
- `Services/MarketDataServices/Helpers/Drawing/DrawingRenderer.cs` — per-Kind render; helpers `DrawHandle`, `DrawGutterPriceTag`, `DrawEndpointPriceTag`, `DrawTrendLabels`, `StylePreviewDrawable.DrawStraightSegment/DrawEndings`.
- `Services/MarketDataServices/Helpers/Drawing/IndicatorRenderer.cs` — `DrawMovingAverages` (price polylines → copy for Bollinger/VWAP), `DrawMood` (0..100 sub-pane → copy for RSI). `RenderFrame` carries VolRect/MoodRect/Bucket.
- `Services/MarketDataServices/Helpers/CandleChartDrawable.cs` — spine; has `CurrentPrice` (~line 100); assembles `RenderFrame` + pane layout (mood-pane carve ~line 205 + `WithRects`).
- `ViewModels/TradeViewModels/Chart/ChartViewModel.Overlays.cs` — toggle pattern (`[ObservableProperty] bool`, `BuildEnabledMas(resolveColor)`, `RequestRedraw()`); also `PositionLine` live PnL via `ChartMath.PositionPnl`.
- `Views/TradePageViews/Chart/{ChartView.Drawing.cs, ChartView.Windows.cs, ChartToolRailView.xaml, ChartToolbarView.xaml}` — input, placement lists, tool rail, indicator toggles.

## Build sequence (ONE Kind/feature per commit; each independently testable)
0. **Plumbing (commit 1, gated, behavior-neutral):** (a) add `decimal? CurrentPrice` to `RenderFrame` as a **nullable
   DEFAULTED PROPERTY, NOT a ctor param** (a ctor param breaks all 619 test fixtures + every `new RenderFrame`); thread
   the spine's `CurrentPrice` in at build. (b) add `PlacementKind`/`AnchorCount` to `DrawToolPresets` and route the
   placement site through `switch(preset.Placement)` — existing tools keep identical placement. Gate: build + 619 tests.
1. **Alert** — HLine-style line + bell glyph + right-gutter price tag; triggered-state from `CurrentPrice` crossing.
   PERSISTS as a `DrawingObject` (session-only would be a data-loss bug). Arms at all 4 sites.
2. **Text** — pill-rendered `d.Text`; input via `DisplayPromptAsync` modal for v1 (in-place edit later). 1-click placement.
3. **Fibonacci** — 2-anchor; `FibonacciLevels.Levels(P1,P2)` → level lines + ratio+price labels (reuse `DrawEndpointPriceTag`).
4. **Position** — 2-anchor (Entry=P1, Target=P2), mirror stop `P3=P1-(P2-P1)`, `Direction` set once at commit, `Qty=1`;
   render entry line + green target zone + red stop zone + ONE info pill (R:R + live PnL via `CurrentPrice`+`ChartMath.PositionPnl`).
5. **Bollinger + VWAP** — clone `DrawMovingAverages`; toggles like the MA pattern; on the price plot (no new pane).
6. **RSI — LAST + opt-in** — clone `DrawMood`; add `RsiRect` to `RenderFrame` (mirror `WithRects`), carve the pane like
   the mood block. VERIFY crosshair/hit pane-checks (`IsInChartArea`/`HitCandleIndex`) still route correctly — the one real hazard.

## Acceptance contract (per feature commit)
- [ ] Builds; 619 tests still green (`dotnet test`, absolute csproj, disk-gated).
- [ ] The new Kind has an explicit arm at render + hit-test + placement + drag — NO fallthrough to Trend/Ray.
- [ ] Adversarial diff review: the 11 existing tools' render/hit/placement/drag branches are UNCHANGED.
- [ ] Row appended to `docs/arcs/` chart test-plan: what to click to see it + verify (place/select/drag/undo/persist-reload).

## Fire-contract footer
- One Kind per commit; never two features in one diff. Commit 1 (plumbing) is behavior-neutral or it's reverted.
- Do NOT touch server/money/CK, the 11 existing tools' bodies, or persistence/undo internals.
- Disk-gate every build (pre-flight `%Disk Time`<70%, Idle+`-maxcpucount:1`, PARSE logs; client build for client-only).
- DEFER (v2): custom Fib ratios, Fib/Bollinger zone-fills, Position drag-edit + multi-target + equity risk%, in-place
  text editing, alert condition UI (cross-up/down), RSI period config. Ship the base Kind first.
