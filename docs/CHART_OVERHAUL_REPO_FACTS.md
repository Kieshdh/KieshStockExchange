# Repo-Facts Appendix — Chart Drawing Model (ground truth for the UP-CORE ultraplan)

Branch `feature/bot-market-realism-v2`. Line numbers from the current tree.

## 1. `KieshStockExchange/Services/MarketDataServices/Helpers/ChartTypes.cs`
Namespace (file-scoped): `KieshStockExchange.Services.MarketDataServices.Helpers`. One file holds many chart types (ChartStyle, VolumeMode, PriceScaleMode, MaKind, ChartViewport, MaPoint, MovingAverageSeries, OpenOrderLine, FillMarker, PositionLine, DepthLevel, TriggerMarker) beyond the drawing ones. Drawing-relevant:
- **L51-56 `enum DrawTool`** = `None, HLine, Trend, Ray, HRay, Polyline, Measure, VLine, Freehand, Rectangle, Ellipse, Text, Magnifier, Position, Alert, Arrow, ExtendedLine`. Append-only (L49-50 comment) — `DrawingObject.Kind` persists by ordinal.
- **L60 `enum DrawingHitPart`** = `Body, Anchor1, Anchor2, Close`
- **L64 `enum DashKind`** = `Solid, Dash, Dot`
- **L69 `enum LineEnding`** = `None, End, Start, BothOut, BothForward` (Start/BothForward legacy)
- **L74 `enum ArrowHeadStyle`** = `FilledTriangle, Open, Outline` (FilledTriangle=ordinal 0)
- **L18 `record struct MeasureState(bool Active, float X0,Y0,X1,Y1)`**; **L13 `record struct CrosshairState(bool Visible, float X,Y, int? CandleIndex)`**
- **L82-87 `record struct DrawStyle(Color Color, float Thickness, DashKind Dash, bool Arrow=false, LineEnding Ending=None, ArrowHeadStyle Head=FilledTriangle)`** + `static Default = new(Color.FromArgb("#4C9AFF"),1.5f,Solid)`. `Arrow` = legacy (load-path migrates Arrow→Ending=End). **Uses MAUI `Color`.**
- **L90 `record struct DrawPoint(DateTime T, decimal P)`**
- **L98-100 `record struct DrawingObject(Guid Id, DrawTool Kind, DateTime T1, decimal P1, DateTime T2, decimal P2, DrawStyle Style, IReadOnlyList<DrawPoint>? Points=null)`**
- **L104-126 `record struct MaColorOption(string Key, string Name)`** + `All`/`FromKey`
- **L131-143 `sealed class ColorJsonConverter : JsonConverter<Color>`**: Read (L133-138) empty→`DrawStyle.Default.Color`; **Write (L140-142) `(value ?? DrawStyle.Default.Color).ToArgbHex(true)` — the null→blue BUG at L142.**

## 2. `KieshStockExchange/ViewModels/TradeViewModels/ChartViewModel.cs`
`public partial class ChartViewModel : StockAwareViewModel`, ns `KieshStockExchange.ViewModels.TradeViewModels`.
- L304 `DrawTool _drawTool=None`; L307-316 `PenToolLabel`; L318 `OnDrawToolChanged`; L324-329 `SelectDrawTool` (persists `chart_draw_tool_last`).
- L333 `ObservableCollection<DrawingObject> Drawings`; L337 `Guid? _selectedDrawingId`; L339 `HasSelectedDrawing`; L342 `IsDefaultPenMode`; L343 `PenPanelHeader`; L348-365 `OnSelectedDrawingIdChanged` (→RefreshPenTiles+RequestRedraw).
- L367 `DrawingsPrefKeyBase="chart_drawings_"`; L369 `_drawingsKey`.
- L467 `DefaultDrawStylePrefKey="chart_draw_style_default"`; L468 `DrawStyle _defaultDrawStyle=LoadDefaultDrawStyle()`; L470-475 `OnDefaultDrawStyleChanged`; L477-487 `LoadDefaultDrawStyle`.
- L500 `StylePreviewDrawable _penSpecimen`; L503-525 pen palette + tiles (Color 10 / Width {1,1.5,2} / Dash / Ending {None,End,BothOut} / Head {Filled,Outline,Open}); commands stamped L657-661.
- L1137-1141 `AddDrawing`; L1144-1150 `RemoveDrawing`; L1155-1167 `MutateSelectedStyle`; L1172-1176 `ApplyPenStyle`; L1178-1209 `SetDefaultColor/Thickness/Dash/Ending/Head` (Head no-ops if `!CanEditHead`); L1202 `CanEditHead => EffectivePenStyle().Ending != None`; L1213-1224 `SetSelectedAsDefault`/`DeleteSelectedDrawing`; L1227-1233 `EffectivePenStyle`; L1236-1239 `NormalizeStyle`; L1241-1243 `MakeSpecimen`; L1249-1281 `RefreshPenTiles`; L1288-1292 `UpdateDrawing`.
- **L1296-1299 `_drawingJson = new JsonSerializerOptions { Converters = { new ColorJsonConverter() } }`**; L1302-1307 `PersistDrawings` (`Preferences.Default.Set(_drawingsKey, JsonSerializer.Serialize(Drawings.ToList(), _drawingJson))`); L1313-1339 `LoadDrawingsForSelected` (`_drawingsKey = $"{DrawingsPrefKeyBase}{StockId}_{Currency}"`; normalizes; L1332-1334 migrates Arrow→Ending=End).

## 3. `KieshStockExchange/ViewModels/TradeViewModels/PenTiles.cs`
5 `public partial class : ObservableObject` tiles (PenColorTile L13 [no specimen], PenWidthTile L21, PenDashTile L31, PenEndingTile L40, PenHeadTile L49): value prop + `ICommand? Command` + `[ObservableProperty] bool _isSelected` + (all but color) `StylePreviewDrawable _specimen`.

## 4. `KieshStockExchange/Services/MarketDataServices/Helpers/CandleChartDrawable.cs`
`public sealed class CandleChartDrawable : IDrawable`, ns `KieshStockExchange.Services.MarketDataServices` (NOT .Helpers). 1855 lines. Consumes: L50 `Drawings`, L51 `DraggingDrawingId`, L52 `SelectedDrawingId`, L56 `BuildingPolyline`, L57 `BuildingPolylineCursor`, L41 `Measure`, L37 `Crosshair`, L12 `Candles`.
- L160-346 `Draw(ICanvas,RectF)` — master paint; local transforms **L301 `float X(DateTime)`**, **L302 `float Y(double)`** (→PriceToFrac); `DrawDrawings` at L322.
- **L600-731 `DrawDrawings(...)`** — per-Kind if/else: **L623 HLine / L641 HRay / L654 Polyline / L709 else Trend|Ray** (only 4 branches; VLine/Freehand/Rect/Ellipse/Text NOT handled). Straight segments → `StylePreviewDrawable.DrawStraightSegment`/`DrawEndings`.
- Helpers: L758 DashPattern, L766 DrawGutterPriceTag, L780 DrawEndpointPriceTag, L798 DrawTrendLabels, L835 RayExit, L848 EndSize, **L850-857 DrawHandle**, **L859-867 DrawCloseGlyph ("✕")**. Consts L590-592.
- Hit-test: L872 PriceToPixelY, L875 TimeToPixelX, **L886-943 `HitDrawing(PointF) → (DrawingObject, DrawingHitPart)?`** (same 4-kind chain, topmost-first), L945-965 WithinBox/Dist/PointSegDist/HitOpenOrderLine.
- Transforms: L1187-1191 ComputePlotRect, L1216 PlotRect, L1217-1218 LastYMin/Max, L1195-1201 cached geometry, L1232-1250 PriceToFrac/FracToPrice (ScaleMode-aware), **L1515-1522 PixelToPrice**, **L1528-1535 PixelToTime**, L1593 DrawCrosshair, L1659 DrawMeasure.

## 5. `StylePreviewDrawable.cs`
ns `KieshStockExchange.Services.MarketDataServices.Helpers`. L6 `enum StylePreviewMode{Line,Dot,Dash}`; L12 `sealed class StylePreviewDrawable : IDrawable`; L21-57 Draw. Statics: L60 DashPattern, L71 DrawStraightSegment, L112 DrawEndings, L132 DrawArrowHead.

## 6. Shared project — ⚠️ THE KEY CONSTRAINT
`KieshStockExchange.Shared.csproj`: `TargetFramework=net9.0`, `RootNamespace=KieshStockExchange`, plain `Microsoft.NET.Sdk` (NOT MAUI). **Namespace convention: the `.Shared` folder is NOT in the namespace** — models under `Shared/Models/` are `namespace KieshStockExchange.Models;` (e.g. Candle.cs L3). Client refs Shared at csproj L70.
**⚠️ Shared does NOT reference `Microsoft.Maui.Graphics`.** Its only packages = CommunityToolkit.Mvvm + Logging.Abstractions. So `Color`/`IDrawable`/`ICanvas`/`RectF`/`PathF`/`Colors` are UNAVAILABLE in Shared today. `DrawStyle`/`DrawingObject`/`ColorJsonConverter`/`MaColorOption` all use `Color` ⇒ moving them to Shared **requires adding a `Microsoft.Maui.Graphics` PackageReference to Shared** (which the server would then transitively reference). The pure-data types (`DrawTool`,`DashKind`,`LineEnding`,`ArrowHeadStyle`,`DrawingHitPart`,`MeasureState`,`CrosshairState`,`DrawPoint`) move with zero new dependency.

## 7. Cross-references (blast radius of moving the drawing types)
Hard references (6 files): ChartTypes.cs (declares), CandleChartDrawable.cs, StylePreviewDrawable.cs, ChartViewModel.cs, PenTiles.cs, ChartView.xaml.cs. Each resolves via `using KieshStockExchange.Services.MarketDataServices.Helpers;` → needs a `using` update if types move. `ChartView.xaml` references CLR types via `xmlns clr-namespace:` — check if `CandleChartDrawable`/`StylePreviewDrawable`/`DrawTool` are used as XAML types. Namespace-only users (unaffected if namespace re-exposed): ChartView.xaml, MarketPage.xaml, Server CandleService.cs, TrendingService.cs, CandleRingBuffer.cs, MoverRow.cs, MaConfig.cs, MovingAverageCalculator.cs.
