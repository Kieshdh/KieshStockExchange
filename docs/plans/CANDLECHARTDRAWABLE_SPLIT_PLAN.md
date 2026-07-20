# CandleChartDrawable Split Plan — structural move arc

Target: `KieshStockExchange/Services/MarketDataServices/Helpers/CandleChartDrawable.cs`
(2182 lines, `public sealed class CandleChartDrawable : IDrawable`).

This file is the contract for the split. The executor follows it member-by-member; the reviewer
audits the diff against it. Precedent: the ChartView/ChartViewModel partial-class split at commit
`61687fd` (spine + `ChartView.Windows.cs` + `ChartView.Drawing.cs` in the same folder).

---

## 1. Purpose + the 2-arc rule

This is a **PURE MOVE arc**. The single class becomes a partial class across 9 files in the same
folder. The hard invariant is **byte-identical behaviour**, enforced by byte-identical member text:

- Cut/paste whole members (fields, properties, methods, consts) into partial files.
- **ZERO logic edits. No renames. No comment rewrites. No compaction. No signature changes.
  No access-modifier changes. No reordering inside a member.**
- The only permitted textual deltas are structural scaffolding:
  1. `public sealed class` → `public sealed partial class` (spine) and the
     `public sealed partial class CandleChartDrawable : IDrawable` / `public sealed partial class
     CandleChartDrawable` declaration line in each new file (the `: IDrawable` base list appears
     ONLY in the spine — repeating it elsewhere is legal but redundant; keep it spine-only).
  2. A using-block + `namespace` line at the top of each new file.
  3. `#region` / `#endregion` markers may be dropped or carried along — carrying them verbatim is
     preferred because it keeps the sorted-line diff audit (§8) trivially clean.

Everything cosmetic — comment tightening, dead-member pruning (e.g. `MarkerColor` appears to be
set by ChartView but never read inside the drawable), pure-math extraction to a Helpers static,
renaming — is **deferred to the separate polish arc** (§6). Two arcs, never mixed: the reviewer of
this arc must be able to verify "no member body changed" mechanically, and any functional edit
hiding inside a 2000-line move diff would be invisible.

---

## 2. Full member → concern map

Every member of the class, with its current line range, assigned to exactly one target file.
Line numbers refer to the file at branch tip (`feature/bot-market-realism-v2`, post-`8eaaf42`).

### → `CandleChartDrawable.cs` (spine — declaration, inputs, orchestrator, shared cache)

| Member | Lines | Kind |
|---|---|---|
| using block + `namespace KieshStockExchange.Services.MarketDataServices;` | 1–11 | scaffolding |
| Class declaration (`sealed` → `sealed partial`) + `: IDrawable` | 13 | scaffolding |
| `_scale` (`IScaleTransform`, readonly field) | 17 | field |
| **Entire `#region Properties`** — all public settable inputs: `Candles`, `Style`, `ScaleMode`, `YPaddingPercent`, `XPaddingPercent`, `Viewport`, `YAutoFit`, `ManualYMin`, `ManualYMax`, `Crosshair`, `Measure`, `ZoomBox`, `MaSeries`, `Drawings`, `DraggingDrawingId`, `SelectedDrawingId`, `BuildingPolyline`, `BuildingPolylineCursor`, `BuildingIsFreehand`, `BuildingStyle`, `DrawingColor` (field), `LabelPillBg` (private static field), `SelectedDrawingIds`, `DraggingOrderId`, `DraggingOrderPrice`, `OpenOrderLines`, `Position`, `CurrentPrice`, `SessionOpenPrice`, palette fields (`Bg`, `Axis`, `Grid`, `Bull`, `Bear`, `PriceLineUp`, `PriceLineDown`, `CrosshairColor`, `MarkerColor`, `OpenOrderBuyColor`, `OpenOrderSellColor`, `OpenOrderStopColor`, `PositionLineColor`, `FillMarkers`, `FillBuyColor`, `FillSellColor`, `TriggerMarkers`, `TriggerColor`), `ShowVolume`, `OverlayVolume`, `VolumeBullTint`, `VolumeBearTint`, `ShowMoodPane`, `MoodSeries`, `MoodLineColor`, `ShowDepth`, `DepthLevels`, `AxisFont`, `PriceTagFont`, layout consts (`RightAxisW`, `BottomAxisH`, `TopPad`, `LeftPad`, `VolumePaneRatio`, `VolumePaneGap`, `VolumePaneMinChartHeight`, `MoodPaneRatio`, `MoodPaneGap`) | 19–183 | props/fields/consts |
| `Draw(ICanvas, RectF)` — **public**, the orchestrator | 186–374 | method |
| `ComputePlotRect(RectF)` — **public** | 1419–1423 | method |
| Cached paint geometry: `_lastPlot`, `_lastVolRect`, `_lastMoodRect`, `_lastYMin`, `_lastYMax`, `_lastTMin`, `_lastTMax` | 1427–1433 | fields |
| Autofit hysteresis state: `_autoFitInit`, `_autoFitLo`, `_autoFitHi`, `_autoFitContractFrames` | 1437–1440 | fields |
| `VolumeRect`, `MoodRect`, `PlotRect`, `LastYMin`, `LastYMax` — **public** read-only props | 1443–1450 | props |
| `DrawNoData` | 1452–1457 | method |
| Autofit tunables: `AutoFitMargin`, `AutoFitContractRatio`, `AutoFitContractHold`, `AutoFitContractLerp` | 2060–2063 | consts |
| `SmoothAutoFit` (mutates `_autoFit*` — Draw's Y-range pipeline) | 2069–2101 | method |
| `SnapRange` (only caller: `SmoothAutoFit`) | 2104–2109 | method |

Rationale: spine = class declaration + ctor-equivalent state + the whole input surface + `Draw()`
+ the shared paint cache it writes + the autofit machinery `Draw()`'s Y-fit calls. Everything the
other partials read (`_lastPlot`, `_lastYMin`, …) is declared here, once.

### → `CandleChartDrawable.Axes.cs` (grid, labels, scale transforms, tick math)

| Member | Lines |
|---|---|
| `PriceToFrac` (instance — reads `ScaleMode`) | 1464–1472 |
| `FracToPrice` (instance — reads `ScaleMode`) | 1474–1482 |
| `DrawYGridAndLabels` | 1484–1516 |
| `DrawXGridAndLabels` | 1518–1546 |
| `NiceRange` (static; called by `DrawYGridAndLabels` here and `SnapRange` in spine) | 2112–2142 |
| `ChooseTimeStep` (static) | 2145–2169 |
| `AlignToStep` (static) | 2172–2180 |

### → `CandleChartDrawable.Candles.cs` (price series body)

| Member | Lines |
|---|---|
| `DrawCandles` (all 6 chart styles dispatch) | 1550–1626 |
| `DrawCloseLine` (Line/Area styles) | 1630–1667 |
| `ComputeHeikinAshi` | 1671–1686 |
| `DrawCurrentPriceLine` (live-price dashed line + gutter tag) | 1688–1738 |

### → `CandleChartDrawable.Indicators.cs` (data-series sub-panes + overlays)

| Member | Lines |
|---|---|
| `DrawMovingAverages` | 1226–1264 |
| `DrawVolume` | 1266–1304 |
| `DrawMood` (Fear/Greed sub-pane) | 1312–1374 |
| `DrawDepth` (order-book liquidity histogram) | 1383–1412 |

### → `CandleChartDrawable.Overlays.cs` (order / position / fill / trigger markers)

| Member | Lines |
|---|---|
| `DrawOpenOrderLines` | 385–435 |
| `DrawPositionLine` | 443–483 |
| `DrawFillMarkers` | 491–539 |
| `DrawTriggerMarkers` | 547–591 |
| `SnapToCandleCenterX` (reads `Candles`; used by fill + trigger markers) | 595–607 |
| `OutlineForBackground` (reads `Bg`; also called cross-partial by `DrawHandle` in `.Drawings.cs`) | 610–614 |

### → `CandleChartDrawable.Drawings.cs` (user drawings + building preview + labels/tags)

| Member | Lines |
|---|---|
| `DrawHandleR`, `DrawHitTol` consts (shared with `.HitTest.cs` — declared here, next to the geometry they describe; partial class shares them) | 618–619 |
| `DrawDrawings` (the giant per-DrawTool dispatch) | 627–883 |
| `DrawBuildingPolyline` (in-progress polyline/freehand preview) | 888–933 |
| `DashPattern` (static) | 936–941 |
| `DrawGutterPriceTag` | 944–957 |
| `DrawVLineTimeTag` | 960–975 |
| `DrawEndpointPriceTag` | 979–996 |
| `DrawTrendLabels` (reads `_lastPlot` directly — see §4) | 1000–1033 |
| `RayExit` (static; also called cross-partial by `HitDrawing`) | 1037–1046 |
| `EndSize` (static) | 1050 |
| `DrawHandle` | 1052–1059 |
| `BlockArrowPath` (static; currently sits inside the "Measure ruler drawing" region but serves only the Arrow draw branch — concern-correct home is here) | 1983–2002 |
| `DrawFreehandPath` (static, draws on canvas; serves Freehand + building preview) | 2009–2046 |
| `PullBack` (static; freehand head-trim) | 2049–2055 |

### → `CandleChartDrawable.HitTest.cs` (public hit-testing + inverse transforms)

| Member | Lines |
|---|---|
| `PriceToPixelY(decimal)` (instance — reads `_lastPlot`/`_lastYMin`/`_lastYMax`) | 1064–1065 |
| `TimeToPixelX(DateTime)` (instance — reads `_lastPlot`/`_lastTMin`/`_lastTMax`) | 1067–1072 |
| `HitDrawing(PointF)` — **public** | 1078–1178 |
| `Dist` (static; also called cross-partial by `DrawDrawings`) | 1180–1181 |
| `PointSegDist` (static) | 1184–1191 |
| `HitOpenOrderLine(PointF)` — **public** | 1197–1224 |
| `PixelToPrice(float)` — **public** (also called cross-partial by `DrawCrosshair`/`DrawMeasure`) | 1747–1754 |
| `PixelToTime(float)` — **public** (also called cross-partial by `DrawCrosshair`/`DrawMeasure`) | 1760–1767 |
| `HitCandleIndex(PointF)` — **public** | 1775–1798 |
| `IsInChartArea(PointF)` — **public** | 1804–1808 |
| `IsInYAxisGutter(PointF)` — **public** | 1815–1821 |

### → `CandleChartDrawable.Crosshair.cs`

| Member | Lines |
|---|---|
| `DrawCrosshair` | 1825–1883 |

### → `CandleChartDrawable.Measure.cs` (measure ruler + zoom box)

| Member | Lines |
|---|---|
| `DrawMeasure` | 1891–1950 |
| `HumanizeSpan` (static) | 1953–1959 |
| `DrawZoomBox` | 1963–1979 |

Completeness check: the map covers lines 1–2182 with no member unassigned. There is no explicit
constructor (the class uses the implicit parameterless ctor + initializers — all initializers move
WITH their member declarations, verbatim).

---

## 3. The FROZEN SURFACE (byte-identical contract)

Partial-class splitting is invisible to callers: same class name, same namespace, same assembly,
same members. The public surface below is **frozen** — the split must not add, remove, rename, or
re-type any of it. External call sites (grepped 2026-07-17):

**Consumers** (the complete set — no others in the repo, and `KieshStockExchange.Tests` does NOT
link the drawable at all, see §7):

- `KieshStockExchange/Views/TradePageViews/Chart/ChartView.xaml.cs` — owns the instance
  (`private readonly CandleChartDrawable _drawable = new();`, line 19), assigns it as the
  GraphicsView drawable, pushes all render inputs.
- `KieshStockExchange/Views/TradePageViews/Chart/ChartView.Windows.cs` — pointer/wheel handlers.
- `KieshStockExchange/Views/TradePageViews/Chart/ChartView.Drawing.cs` — drawing-tool handlers.
- `KieshStockExchange/Services/MarketDataServices/Helpers/Drawing/ChartSnapshotRenderer.cs` —
  takes a `CandleChartDrawable` parameter, calls `Draw()` offscreen (line 40).

**Public methods** (external call-site counts, excluding the drawable's own internal calls):

| Member | External call sites |
|---|---|
| `Draw(ICanvas, RectF)` | 1 direct (ChartSnapshotRenderer.cs:40) + implicit via `IDrawable` (GraphicsView paint loop) |
| `HitDrawing(PointF)` | 3 (ChartView.Windows.cs:130, 184, 714) |
| `HitOpenOrderLine(PointF)` | 1 (ChartView.Windows.cs:280) |
| `PixelToPrice(float)` | 11 (ChartView.Windows.cs:169, 217, 341, 391, 423, 444, 576×2, 836; ChartView.Drawing.cs:30, 46) |
| `PixelToTime(float)` | 8 (ChartView.Windows.cs:175, 219, 393, 428, 574×2; ChartView.Drawing.cs:29, 47) |
| `HitCandleIndex(PointF)` | 1 (ChartView.xaml.cs:399) |
| `IsInChartArea(PointF)` | 7 (ChartView.Windows.cs:130, 143, 155, 168, 216, 352; ChartView.xaml.cs:397) |
| `IsInYAxisGutter(PointF)` | 2 (ChartView.Windows.cs:309, 820) |
| `ComputePlotRect(RectF)` | 0 external (internal use in `Draw`; deliberately public per its doc-comment — stays public) |

**Public read-only properties:**

| Member | External call sites |
|---|---|
| `PlotRect` | 1 (ChartView.Windows.cs:851) |
| `LastYMin` / `LastYMax` | ~9 each (ChartView.Windows.cs:311, 330, 336/337, 360, 369/370, 821, 827, 831/832; ChartView.xaml.cs:421/422) |
| `VolumeRect`, `MoodRect` | 0 external (frozen anyway) |

**Public settable properties + public fields** — all written by ChartView (construction/theme at
ChartView.xaml.cs:189–242, render pipeline at 307–340, 397–488; drag state from
ChartView.Windows.cs and ChartView.Drawing.cs). Every one is frozen; the notable interactive-state
writers outside the xaml.cs pipeline are: `Measure`, `ZoomBox`, `BuildingPolyline`,
`BuildingPolylineCursor`, `BuildingIsFreehand`, `DraggingOrderId`, `DraggingOrderPrice`,
`DraggingDrawingId` (ChartView.Windows.cs / ChartView.Drawing.cs, many sites each).

**Conclusion: the split is surface-neutral by construction.** No call site changes. No caller
recompiles differently. `ChartSnapshotRenderer`'s parameter type is untouched.

### ⚠ Namespace ground truth (differs from folder!)

The class's actual namespace is:

```csharp
namespace KieshStockExchange.Services.MarketDataServices;
```

**NOT** `KieshStockExchange.Services.MarketDataServices.Helpers`, despite the file living in the
`Helpers/` folder. (ChartSnapshotRenderer's header comment even documents this: "CandleChartDrawable
lives in the enclosing ...MarketDataServices namespace".) Every partial file MUST carry exactly
`namespace KieshStockExchange.Services.MarketDataServices;` — a partial declared under
`...MarketDataServices.Helpers` would be a *different class*, and the build would explode with
missing-member errors across all nine files. This is the #1 way to botch this split. Do not
"fix" the namespace-vs-folder mismatch in this arc (that's a polish-arc decision, and it would
touch call-site usings — out of scope).

---

## 4. The critical hazard: `X()`/`Y()` local functions + shared private cache

`Draw()` builds two local functions closing over the frame's plot geometry:

```csharp
float X(DateTime utc) => plot.Left + (float)(((utc - tMin).TotalSeconds / spanSec) * plot.Width);
float Y(double price) => plot.Bottom - (float)(PriceToFrac(price, yMin, yMax) * plot.Height);
```

**Verified: every draw helper receives these as `Func<>` parameters — none captures the closures
directly.** The full dispatch (lines 332–371) passes `X`/`Y`/`plot`/`cur`/`tMin`/`tMax` explicitly:

- `DrawOpenOrderLines(canvas, plot, Y, cur)` · `DrawPositionLine(canvas, plot, Y, cur)`
- `DrawFillMarkers(canvas, plot, X, Y)` · `DrawTriggerMarkers(canvas, plot, X, Y)`
- `DrawCurrentPriceLine(canvas, plot, Y, cur, tMin, tMax)` · `DrawDrawings(canvas, plot, X, Y, cur)`
- `DrawCandles(canvas, plot, X, Y)` · `DrawMovingAverages(canvas, plot, tMin, tMax, yMin, yMax, X, Y)`
- `DrawVolume(canvas, volRect, X)` · `DrawMood(canvas, moodRect, X, tMin, tMax)` · `DrawDepth(canvas, plot, Y)`
- `DrawCrosshair(canvas, plot, currency, X)` · `DrawMeasure(canvas, plot)` · `DrawZoomBox(canvas, plot)`
- `DrawBuildingPolyline(canvas, X, Y)` (called from inside `DrawDrawings`)

So the helpers move into partials cleanly — the closures stay local to `Draw()` in the spine and
cross the file boundary only as ordinary `Func<DateTime,float>` / `Func<double,float>` arguments.

**Helpers that ALSO read instance cache directly** (not only via params) — the flagged list:

| Helper | Instance state read (beyond params) |
|---|---|
| `DrawDrawings` | `_scale`, `_lastYMin`, `_lastYMax`, `ScaleMode` (the `_scale.PriceToPixelY(...)` calls in the ExtendedLine/Rectangle/Ellipse/Arrow branches), plus `Drawings`, selection ids, `BuildingPolyline`, `DrawingColor` |
| `DrawTrendLabels` | `_lastPlot` (used directly for clamping, not passed in), `Viewport`, `Bull`/`Bear` |
| `DrawCrosshair` | `_lastVolRect`, `Crosshair`, `Candles`; calls `PixelToPrice`/`PixelToTime` (cache-based, in `.HitTest.cs`) |
| `DrawMeasure` / `DrawZoomBox` | `Measure`/`ZoomBox`, `Viewport`, `Candles`; call `PixelToPrice`/`PixelToTime` |
| `SnapToCandleCenterX` | `Candles` |
| `OutlineForBackground` | `Bg` |
| `PriceToFrac` / `FracToPrice` | `ScaleMode` (this is why they are NOT extractable statics — see §6) |
| `SmoothAutoFit` | mutates `_autoFitLo/Hi/Init/ContractFrames` |
| All of `.HitTest.cs` | `_lastPlot`, `_lastVolRect`, `_lastYMin/Max`, `_lastTMin/Max`, `Candles`, `Drawings`, `OpenOrderLines`, drag state |

**Why this is safe:** a C# partial class is ONE class — all partial files compile into a single
type sharing every private field, const, and method. `_lastPlot` declared in the spine is directly
readable from `.HitTest.cs` with zero changes. Sequencing is also preserved: `Draw()` commits
`_lastYMin/_lastYMax/_lastTMin/_lastTMax/_lastPlot` (lines 231–233, 320–323) **before** dispatching
to any helper that reads them, and the split does not reorder a single statement inside `Draw()`.

The only real hazards are (a) the namespace trap (§3), and (b) forgetting a `using` in a partial
(compile error, caught by the gate — MAUI's implicit usings cover `Microsoft.Maui.Graphics`; the
explicit ones to replicate are the 9 usings at lines 1–9, notably `System.Globalization`
(`DrawVLineTimeTag`, `DrawXGridAndLabels`, `DrawCrosshair`), `KieshStockExchange.Models`,
`Models.ChartDrawing.{Objects,Style,Tools}`, `KieshStockExchange.Helpers` (`CurrencyHelper`,
`TimeHelper`), `Services.MarketDataServices.Helpers` (`StylePreviewDrawable`, `ChartTypes` types),
`Services.MarketDataServices.Helpers.Drawing` (`IScaleTransform`), `Services.MarketDataServices.Interfaces`).
Simplest byte-safe approach: **replicate the full 9-line using block verbatim in every partial**
(unused-using is at worst an info diagnostic, not a warning-as-error in this repo).

---

## 5. Target file tree (all in `KieshStockExchange/Services/MarketDataServices/Helpers/`)

| File | Content (short) | Est. lines |
|---|---|---|
| `CandleChartDrawable.cs` (spine, exists) | declaration, `_scale`, Properties region, `Draw()`, `ComputePlotRect`, paint cache + autofit state/consts, `SmoothAutoFit`+`SnapRange`, public rect/range props, `DrawNoData` | ~470 |
| `CandleChartDrawable.Axes.cs` | grid+labels, `PriceToFrac`/`FracToPrice`, tick math (`NiceRange`, `ChooseTimeStep`, `AlignToStep`) | ~160 |
| `CandleChartDrawable.Candles.cs` | `DrawCandles`, `DrawCloseLine`, `ComputeHeikinAshi`, `DrawCurrentPriceLine` | ~195 |
| `CandleChartDrawable.Indicators.cs` | `DrawMovingAverages`, `DrawVolume`, `DrawMood`, `DrawDepth` | ~185 |
| `CandleChartDrawable.Overlays.cs` | order/position lines, fill/trigger markers, `SnapToCandleCenterX`, `OutlineForBackground` | ~220 |
| `CandleChartDrawable.Drawings.cs` | `DrawDrawings` + building preview + tag/label helpers + `RayExit`/`EndSize`/`DashPattern`/`DrawHandle`/`BlockArrowPath`/`DrawFreehandPath`/`PullBack` + `DrawHandleR`/`DrawHitTol` | ~500 ⚠ at-cap |
| `CandleChartDrawable.HitTest.cs` | all public hit-test/inverse-transform members + `PriceToPixelY`/`TimeToPixelX`/`Dist`/`PointSegDist` | ~215 |
| `CandleChartDrawable.Crosshair.cs` | `DrawCrosshair` | ~75 |
| `CandleChartDrawable.Measure.cs` | `DrawMeasure`, `HumanizeSpan`, `DrawZoomBox` | ~100 |

Total ≈ 2,120 content lines + per-file using/namespace boilerplate ≈ the original 2,182. Every
file is under the ~500 cap; `.Drawings.cs` sits right at it (`DrawDrawings` alone is 257 lines —
acceptable overflow per the convention, and the polish arc's math extraction will pull ~80 lines
of statics out of it). Never `.Part2.cs`.

**Folder: flat-in-place** (all 9 files next to each other in `Helpers/`). Reasons: (a) the
ChartView precedent puts partials beside the spine in the same folder; (b) the shared
`CandleChartDrawable.` prefix makes them sort and read as one unit; (c) a `Chart/` subfolder
(folder ≠ namespace, so it *would* be legal without touching the namespace) buys nothing here and
churns paths in docs/`Resources/Raw/FileStructure.txt`. Note `Helpers/Drawing/` already exists for
the *collaborator* classes (IScaleTransform, MagnetSnapper, SplineSmoother, ChartSnapshotRenderer)
— the drawable's own partials are a different thing and should not move in there.

---

## 6. Pure math → Helpers static: what qualifies, and the recommendation

**Genuinely pure, stateless candidates** (already `static`, no instance reads):

| Method | Current home | Notes |
|---|---|---|
| `Dist` | 1180 | point distance |
| `PointSegDist` | 1184 | point-to-segment distance |
| `RayExit` | 1037 | ray/rect far intersection |
| `EndSize` | 1050 | thickness → head size (1-liner) |
| `DashPattern` | 936 | `DashKind` → `float[]?` |
| `BlockArrowPath` | 1983 | pure geometry, returns `PathF` (no canvas) |
| `PullBack` | 2049 | pull tip toward prev by dist |
| `HumanizeSpan` | 1953 | TimeSpan formatting |
| `SnapRange` / `NiceRange` | 2104 / 2112 | axis-tick quantization |
| `ChooseTimeStep` / `AlignToStep` | 2145 / 2172 | time-tick selection/alignment |

**Must STAY instance members in the partials** (state-coupled):

- `PriceToFrac` / `FracToPrice` — read `ScaleMode` (log vs linear). Extracting would force a
  `mode` parameter = signature change = call-site edits inside member bodies. Stays.
- `PriceToPixelY` / `TimeToPixelX` — read `_lastPlot`/`_lastYMin/Max`/`_lastTMin/Max`. Stays.
- `SnapToCandleCenterX` — binary-searches `Candles`. Stays.
- `OutlineForBackground` — reads `Bg`. Stays.
- `SmoothAutoFit` — mutates `_autoFit*` hysteresis state. Stays.
- `DrawFreehandPath` — static but takes `ICanvas` and draws (side-effects). Stays.

**Destination when extraction happens:** NOT the existing `ChartMath`
(`KieshStockExchange/Helpers/ChartMath.cs`) — that is VM-level chart math (zoom offsets, cost
basis, P&L, mood reconstruction). Pixel-geometry belongs in a NEW static
`KieshStockExchange/Helpers/ChartGeometry.cs` (same `KieshStockExchange.Helpers` namespace):
`Dist`, `PointSegDist`, `RayExit`, `EndSize`, `PullBack`, `BlockArrowPath`, `DashPattern`. The
tick/format group (`NiceRange`, `ChooseTimeStep`, `AlignToStep`, `HumanizeSpan`, `SnapRange`) can
join `ChartGeometry` or a `ChartAxisMath` — decide in the polish arc.

**RECOMMENDATION: DEFER math extraction to the polish arc. Do NOT do it in this arc.**
Extraction is behaviour-identical but not *text*-identical: every call site inside member bodies
changes (`Dist(...)` → `ChartGeometry.Dist(...)`), access modifiers change (`private` → `public`),
and `#region` bodies shrink. That destroys the mechanical "no member-body line changed" audit
(§8) that is this arc's entire safety argument — exactly what the 2-arc rule exists to prevent.
The extraction is cheap to do immediately after, on top of a verified-clean split, where its diff
is small and legible on its own. The one thing this arc DOES do for it: the concern map above
already isolates every candidate, so the polish arc is a mechanical follow-up.

---

## 7. Execution steps

Work on `feature/bot-market-realism-v2`. One commit for the whole split (it is one atomic
refactor; intermediate states don't compile because members would be missing).

1. **Spine prep**: in `CandleChartDrawable.cs`, change line 13 to
   `public sealed partial class CandleChartDrawable : IDrawable`. Nothing else in the file yet.
2. **Create the 8 partial files**, each with the verbatim 9-line using block from the spine +
   `namespace KieshStockExchange.Services.MarketDataServices;` +
   `public sealed partial class CandleChartDrawable` + `{ }`.
   ⚠ Namespace exactly as written — see §3 trap.
3. **Cut/paste per the §2 map**, one target file at a time, whole members only, verbatim bodies
   including doc-comments and inline comments. Recommended order (roughly bottom-up so remaining
   line numbers in the map stay accurate longest): `.Measure.cs` → `.Crosshair.cs` →
   `.HitTest.cs` → `.Candles.cs` + `.Indicators.cs` → `.Axes.cs` → `.Drawings.cs` →
   `.Overlays.cs`. The spine keeps what §2 assigns it — including `SmoothAutoFit`/`SnapRange` and
   the autofit consts, which today live at the bottom of the file and move UP into the spine's
   remaining content (a within-file move, still verbatim text).
4. **Gate: build.**
   `dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`
   must be green with **0 CS errors**. Known benign failure mode: if the client app is running,
   the final exe copy step fails with a file-lock — the compile itself still validates; 0 compile
   errors is the gate, re-run the copy after closing the app if a fresh binary is needed.
5. **Gate: tests.** Full `dotnet test` green (619 tests at last count). Note: the test csproj
   links only `MagnetSnapper.cs`, `SplineSmoother.cs`, `UndoStack.cs` +
   `Models/ChartDrawing/**` — it does NOT compile the drawable, so tests are a no-regression
   guard for the collaborators, not a rendering gate. No test-csproj edit is needed (the new
   partials are not in its link globs and must not be added).
6. **Gate: diff audit** per §8.
7. **Gate: human eyeball of the running chart** (the behavioural gate — tests don't cover
   rendering). Launch the client, and walk: all 6 chart styles (Candles / HollowCandles / Bars /
   Line / Area / Heikin-Ashi); volume overlay AND sub-pane modes; mood pane on; depth overlay on;
   MAs; log + percent scale; open-order lines (incl. drag-to-modify, stop dash, dormant alpha);
   position line + P&L tag; fill + trigger markers; every draw tool (HLine, HRay, VLine, Trend,
   Ray, ExtendedLine, Polyline + building preview, Freehand incl. ending heads, Rectangle,
   Ellipse, Arrow) + selection handles + tags/labels; hit-testing (select/drag each kind, order
   line drag, Y-gutter wheel-zoom); crosshair + OHLCV snap; Shift-drag measure ruler; magnifier
   zoom box; snapshot-to-PNG button (exercises `ChartSnapshotRenderer` → `Draw`).
8. **Commit** (structural-move message, e.g.
   `refactor(chart): partial-class CandleChartDrawable split (pure move, 9 files)` — mirroring
   the ChartView split commit style). Optional follow-up commits: regenerate
   `Resources/Raw/FileStructure.txt` if the workflow keeps it current.

---

## 8. Verification — proving the pure move

The property to prove: **every member body line of the original appears verbatim in exactly one
new file; nothing else changed.**

Primary check — sorted-line multiset diff (mechanical, catches any body edit):

```powershell
# from repo root; scratchpad path per session
git show HEAD:KieshStockExchange/Services/MarketDataServices/Helpers/CandleChartDrawable.cs |
  Sort-Object > $scratch\old_sorted.txt
Get-Content KieshStockExchange\Services\MarketDataServices\Helpers\CandleChartDrawable*.cs |
  Sort-Object > $scratch\new_sorted.txt
Compare-Object (Get-Content $scratch\old_sorted.txt) (Get-Content $scratch\new_sorted.txt)
```

Expected output = ONLY the scaffolding deltas, each explainable on sight:
- the changed class-declaration line (`sealed class` → `sealed partial class`) plus 8 new
  `public sealed partial class CandleChartDrawable` lines;
- 8 duplicated copies of each using line + `namespace` line + brace lines;
- `#region`/`#endregion` lines if the executor chose to drop or re-title them (prefer carrying
  them verbatim so this set is empty);
- blank-line count drift at file boundaries.

**Any other line in the compare output = a member body changed = reject and redo that member.**

Secondary checks:
- `git status` shows exactly: 1 modified file (spine) + 8 added files. No other file touched —
  in particular NO edits to ChartView*, ChartSnapshotRenderer, the test csproj, or any caller
  (§3 surface-neutrality makes call-site edits impossible in a correct split).
- `git diff` on the spine reads as pure deletions + the one declaration-line change; each deleted
  block reappears verbatim in exactly one added file.
- Reviewer spot-check: pick 3 gnarly members (`DrawDrawings`, `HitDrawing`, `SmoothAutoFit`) and
  eyeball old-vs-new body text side by side.

---

## Appendix: surprises found while grounding this plan (executor must know)

1. **Namespace ≠ folder** — the class is in `KieshStockExchange.Services.MarketDataServices`,
   not `...Helpers`. All partials must match it exactly (§3).
2. **`DrawVolume` is NOT in the Candle Drawing region** — it sits at lines 1266–1304 inside the
   IDrawable region (between `DrawMovingAverages` and `DrawMood`). Mapped to `.Indicators.cs`.
3. **`BlockArrowPath`/`DrawFreehandPath`/`PullBack` live in the "Measure ruler drawing" region**
   (1983–2055) but serve the Drawings concern exclusively — mapped to `.Drawings.cs`.
4. **The test project never compiles the drawable** (explicit link set), so `dotnet test` cannot
   catch a split mistake in it; the build gate + eyeball gate carry the weight.
5. **Cross-partial private calls exist by design** after the split (`Dist` from `DrawDrawings`,
   `RayExit` from `HitDrawing`, `OutlineForBackground` from `DrawHandle`,
   `PixelToPrice`/`PixelToTime` from `DrawCrosshair`/`DrawMeasure`, `NiceRange` from `SnapRange`)
   — all safe, one class.
6. `MarkerColor` (line 120) is assigned by ChartView but appears unread inside the drawable —
   candidate for the polish arc's dead-member pass, untouched here.
