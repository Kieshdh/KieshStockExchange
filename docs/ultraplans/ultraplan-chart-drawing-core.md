# UP-CORE — Chart Drawing Overhaul: Core Foundation (BLIND-PATCHABLE)

**Fire with:** `/ultraplan docs/ultraplan-chart-drawing-core.md`
**Branch:** `feature/bot-market-realism-v2`
**This is the FIRST of two ultraplans** (UP-CORE now; UP-STORE = server persistence, fired after this lands). It **blocks all five local UI phases (LP1–LP5)** — nothing UI-side starts until this merges.

---

## 0. Mission & the one hard contract

Land the deterministic, headless-verifiable foundation for the TradingView-style chart drawing overhaul: the **data model, the JSON converter fix, the pure geometry/state helpers as named seams, the logic-only ViewModel plumbing, and the new renderers/hit-tests** — everything whose acceptance test is *compile + unit test + math*, not *Kiesh's eyeball on a running build*.

**THE HANDOFF CONTRACT (non-negotiable):** with **no XAML/UI change in this patch**, the running chart must **render byte-identically to today**. Every new field is trailing-default (back-compat by construction); every new `DrawTool`/`Kind` member is unreachable without UI that this patch does not add; the converter fix is behavior-preserving for every non-null colour. LP1 must be able to start from a chart that looks exactly like the current one. If a change would alter today's render, it does not belong in UP-CORE.

**HARD SCOPE RULES:**
- ✅ Touches: model records/enums, `ColorJsonConverter`, new pure helper classes, logic-only VM members (properties, bools, methods, setters), new renderer/hit-test branches in the drawable.
- ❌ **Never** touches: `*.xaml`, pointer/gesture handlers, timers, cursor (`ProtectedCursor`), page layout, `ChartView.xaml.cs` event wiring beyond namespace `using` updates.
- ✅ Must **compile the client csproj app-closed** (`dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`). Note: the exe cannot LINK while Kiesh's client runs (it locks `KieshStockExchange.Shared.dll`); a clean **compile** is the gate, a full testable exe needs the client closed.
- ✅ Must pass new unit tests for the pure helpers (`MagnetSnapper`, `SplineSmoother`, `UndoStack`, `ColorJsonConverter` round-trip) — these have **zero MAUI-lifecycle dependency**, run in the existing `KieshStockExchange.Tests` project.

---

## 1. Architecture decision (RESOLVED — do not re-open)

**OPTION A (Kiesh, 2026-07-16): the drawing model stays entirely CLIENT-side. Nothing moves to `KieshStockExchange.Shared`.** Rationale: `Shared` has no `Microsoft.Maui.Graphics` reference and the model uses MAUI `Color`; moving it would force a graphics dependency onto the headless server for no gain. The server (UP-STORE, a separate ultraplan) stores the serialized drawings-list JSON as an **opaque string blob** keyed by `(userId, stockId, currency)` and never deserializes it. **The `"v":1` JSON schema this patch establishes IS the client↔server wire contract.**

---

## 2. Build items

### Item 0 — Code organization (client-only folder split)

The drawing types live in ONE flat file today: `KieshStockExchange/Services/MarketDataServices/Helpers/ChartTypes.cs` (namespace `KieshStockExchange.Services.MarketDataServices.Helpers`). Split into grouped **client** folders under `KieshStockExchange/Models/ChartDrawing/`:

- **`Models/ChartDrawing/Tools/`** → `DrawTool` enum + the new `Kind→preset` static table. Namespace `KieshStockExchange.Models.ChartDrawing.Tools`.
- **`Models/ChartDrawing/Style/`** → `DrawStyle`, `DashKind`, `LineEnding`, `ArrowHeadStyle`, `SizeKind`, `ColorJsonConverter`, `MaColorOption`. Namespace `KieshStockExchange.Models.ChartDrawing.Style`.
- **`Models/ChartDrawing/Objects/`** → `DrawingObject`, `DrawPoint`, `AlertCondition`. Namespace `KieshStockExchange.Models.ChartDrawing.Objects`.
- **Stay under the chart helpers** (transient render state + pure helpers, NOT persisted): `MeasureState`, `CrosshairState`, `DrawingHitPart` keep living near the drawable; the new pure helpers (`MagnetSnapper`, `SplineSmoother`, `UndoStack`, `ChartSnapshotRenderer`, `IScaleTransform`) go in `Services/MarketDataServices/Helpers/` (or a `Helpers/Drawing/` subfolder).
- **Leave the non-drawing types** in `ChartTypes.cs`/its namespace (ChartStyle, VolumeMode, PriceScaleMode, MaKind, ChartViewport, MaPoint, MovingAverageSeries, OpenOrderLine, FillMarker, PositionLine, DepthLevel, TriggerMarker) — do **not** churn them.

**Namespace convention (verified):** `RootNamespace=KieshStockExchange` for the client. New namespaces are folder-derived: `KieshStockExchange.Models.ChartDrawing.{Tools,Style,Objects}`. Moving the types changes their namespace ⇒ **update every `using`** in the blast-radius files (§Repo-Facts §7): `CandleChartDrawable.cs`, `StylePreviewDrawable.cs`, `ChartViewModel.cs`, `PenTiles.cs`, `ChartView.xaml.cs`. **Also check `ChartView.xaml`'s `xmlns:clr-namespace`** — if it references `DrawTool`/`DrawStyle`/etc. as a XAML element or `x:Static`, the `clr-namespace` must be updated (this is the ONE XAML touch allowed, and only for namespace correctness — no visual change). If no drawing type is referenced as a XAML type, leave the XAML untouched.

> **NOT in UP-CORE (owner: no haste, do LAST):** tidying the flat `KieshStockExchange.Shared/Models/` (~24 files) into subfolders (task #222). Namespace-neutral, unrelated to the chart split. Do not include.

### Item 1 — Model additions (append-only, back-compat)

**`DrawTool` enum** is append-only (persists by ordinal). Current members (verbatim): `None, HLine, Trend, Ray, HRay, Polyline, Measure, VLine, Freehand, Rectangle, Ellipse, Text, Magnifier, Position, Alert, Arrow, ExtendedLine`. **Append only** (do NOT reorder): `RotatedRect, Triangle, Arc, FibRetracement`. (`FibRetracement` = enum member reserved for a post-ship build; no impl this patch.)

**New enums** (new files under `Style/` and `Objects/`):
- `enum SizeKind { Small, Medium, Large }`
- `enum AlertCondition { CrossAny, CrossUp, CrossDown }`

**`DrawStyle` trailing-default fields** (append to the record struct, all with defaults so existing serialized JSON deserializes and existing constructor call-sites compile):
- `Color? Fill = null`
- `float FillOpacity = 0.15f`
- `SizeKind Size = SizeKind.Medium`

**`DrawingObject` trailing-default fields** (append to the record struct):
- `string? Text = null`
- `decimal P3 = 0m` — third price anchor (Position stop leg)
- `decimal Qty = 0m` — Position quantity (shares)
- `bool Locked = false` — protects from move/edit/delete/undo
- `float Smoothing = 0.5f` — Brush/Highlighter spline tension
- `int Direction = 0` — **explicit** Position long(+1)/short(−1); **do NOT infer from `target>entry`** (that flips the box mid-type). 0 = not a position.

Document in a comment on `DrawingObject`: for a Position, **P1 = Entry, P2 = Target, P3 = Stop, Qty = shares, Direction = ±1**. Risk% is **cut** (no equity hookup in v1); PnL is **live-only** (computed at render from current price, never stored).

**Kind→preset static table** (in `Tools/`): a `static IReadOnlyDictionary<DrawTool, ...>` (or a `switch`-based `DrawToolPreset.For(DrawTool)`) that maps each tool to its default `DrawStyle` + which panel sections it shows. This is **load-bearing** — the rail/panel logic keys off it. Shapes (Rectangle/Ellipse/RotatedRect/Triangle/Arc) → border stroke + Fill + FillOpacity; Brush/Highlighter → stroke (+ Smoothing); Highlighter preset = low FillOpacity/alpha (no separate field); Text/PriceLabel → Text + Size + Colour; Position → the Position section. Reserve preset rows for `FibRetracement`, `RotatedRect`, `Triangle`, `Arc` (harmless — no UI reaches them).

### Item 2 — Correctness fixes

- **`ColorJsonConverter.Write` null bug** (`ChartTypes.cs:142`, in the moved `Style/ColorJsonConverter.cs`): today it writes `(value ?? DrawStyle.Default.Color).ToArgbHex(true)` — a null `Fill` silently serializes as blue. Fix so **null round-trips as null** (or a `Colors.Transparent` sentinel the reader maps back to null): write `null` JSON for a null Color; the `Read` path already maps empty→`DrawStyle.Default.Color` for the non-nullable `Color` member — add a nullable path or a distinct converter for `Color?` so `DrawStyle.Fill` (nullable) preserves "no fill". **Behavior-preserving for every non-null colour** (identical hex output). Add a unit test proving `null → null` and `#RRGGBBAA → same` round-trip.
- **`EditingKind` `.First()` → `.FirstOrDefault()`** in `ChartViewModel` (the property that resolves the currently-edited kind) — avoids an exception when the selection set is momentarily empty.
- **`"v":1` versioned payload:** the serialized drawings-list JSON gains a top-level `"v":1` version tag. This is UP-STORE's wire contract. Establish the envelope now: serialize `{ "v": 1, "drawings": [ ... ] }` (or equivalent) via the existing `_drawingJson` `JsonSerializerOptions`; the load path reads `v` and tolerates a missing `v` (legacy = the current bare-array `Preferences` blob → migrate-up to v1 on read). Keep `PersistDrawings`/`LoadDrawingsForSelected` as the call sites (UP-STORE swaps their bodies behind an `IDrawingStore`; UP-CORE only changes the payload shape + adds the Arrow→Ending migration already present).

### Item 3 — Pure headless primitives (named, unit-tested seams)

Each is a standalone class with **no MAUI-lifecycle dependency** (may use `Microsoft.Maui.Graphics` value types — `PointF`, `RectF`, `Color`, `PathF` — which are available in the client project). Unit-test each.

- **`MagnetSnapper.Snap(PointF px, MagnetMode mode, AxisMask axis, IReadOnlyList<SnapCandidate> candidates) → SnapResult`**
  - `enum MagnetMode { Off, Weak, Strong }`; `[Flags] enum AxisMask { None, X, Y, Both }`.
  - **Weak:** snap only if cursor within **8px** of a candidate; else pass through (cursor position, no snap).
  - **Strong:** always snap to the nearest candidate of the candle under/near the cursor (snap the anchor's time to that candle too).
  - Not near any candle → cursor position, no snap (both modes).
  - **Candidate set:** OHLC of the nearest candle (wick-high/low, open, close) **+ open-order lines + MA points** (C5). Body-move + `Brush`/`Highlighter`/`Measure`/`Magnifier` are **exempt** (pass `MagnetMode.Off` at those call sites — the helper still handles it).
  - `SnapResult { PointF Point; bool Snapped; SnapCandidateKind Kind; }`. O(candidates), pure.
- **`SplineSmoother`** — Catmull-Rom (tension = `Smoothing`, 0 = follow points exactly → 1 = very rounded) through captured points + a decimation pass (drop points closer than N px). Two static methods: `Decimate(IReadOnlyList<DrawPoint>, minPx)` and `Evaluate(IReadOnlyList<DrawPoint>, tension) → PathF` (re-evaluated at draw time; points are what persist, like a polyline).
- **`ChartSnapshotRenderer.Render(CandleChartDrawable drawable, int w, int h) → byte[]` (PNG)** — offscreen `SkiaBitmapExportContext` → `drawable.Draw(ctx.Canvas, new RectF(0,0,w,h))` → `ctx.Image` → PNG bytes. ~30 lines, deterministic. (LP1 wires the button/clipboard/toast.)
- **`UndoStack`** — bounded (cap 50) stack of drawing mutations: `Push(add|move|delete)`, `Undo()`, `Redo()`, **one entry per gesture**, **purge-on-lock** (a locked drawing's entries drop), **lock is not itself undoable**, **clear-on-stock-switch**. State-machine only; unit-test the rules (undo skips a locked target, redo invalidated on new push, cap eviction, clear).
- **`IScaleTransform`** — the price↔pixel seam. Ship the **`RegularScaleTransform`** impl only (linear, current behavior). `float PriceToPixelY(decimal price, ...)` / `decimal PixelToPrice(float y, ...)`. **Every drawing render must route price→pixel through this seam** so LP4 can add Log/Percent/Indexed impls without touching each renderer. Match the current `PriceToFrac`/`FracToPrice` math exactly (identical-render).

### Item 4 — ViewModel plumbing (logic-only, follows the `CanEditHead` precedent)

In `ChartViewModel` (`ViewModels/TradeViewModels/ChartViewModel.cs`), following the **exact** existing `CanEditHead` pattern (computed bool + `OnPropertyChanged` in `RefreshPenTiles`):
- **`EditingKind`** — the currently-edited `DrawTool` (selection's kind, else the armed tool), via `.FirstOrDefault()`.
- **8 `Show*` bools** gating panel sections, computed from `EditingKind` via the preset table. **Split `ShowFillColor` and `ShowOpacity`** (M5 — a shape shows both; a line shows neither; text shows neither). Suggested set: `ShowStroke, ShowFillColor, ShowOpacity, ShowDash, ShowEnding, ShowHead, ShowText, ShowPosition`. (Exact bool list is the agent's call — must cover every tool's panel via the preset table; add `ShowSize`/`ShowSmoothing` if the panel design needs them.)
- **`MutateSelectedDrawing(Func<DrawingObject, DrawingObject>)`** — the single seam LP2/LP3/LP5 route every edit through (respects `Locked`: no-op on a locked target; raises redraw + persists). Mirror the existing `MutateSelectedStyle`.
- **Setters** for each new editable field (Fill/FillOpacity/Size/Text/Qty/entry-target-stop prices/Direction/Smoothing) that call `MutateSelectedDrawing` — logic only, no UI binding added here.
- Extend `RefreshPenTiles`/`PenPanelHeader` to raise `OnPropertyChanged` for `EditingKind` + all new `Show*` bools.
- **Remove the on-chart delete glyph:** delete `DrawCloseGlyph` (render) + the `DrawingHitPart.Close` hit path + the `Close` enum member. Deletion is settings-panel + Delete-key only (LP3 wires those).

### Item 5 — Renderers & hit-tests

In `CandleChartDrawable` (`Services/MarketDataServices/CandleChartDrawable.cs`):
- Add render + hit-test branches for **`ExtendedLine`** (infinite both directions) and **`Rectangle`/`Ellipse` fill-opacity paths** (border stroke via existing straight-segment helpers; fill via `Fill`+`FillOpacity`). Route all price→pixel through `IScaleTransform`.
- **`Position`-box geometry is EXCLUDED** (interaction-entangled, drag↔panel sync) → goes to LP5.
- `Triangle`/`Arc`/`RotatedRect`/`FibRetracement` renderers **NOT built** (reserved).
- Keep the existing HLine/HRay/Polyline/Trend|Ray branches; extend the per-Kind if/else and `HitDrawing` chain consistently.

---

## 3. Repo-Facts appendix (ground truth — line numbers from the current tree)

### `KieshStockExchange/Services/MarketDataServices/Helpers/ChartTypes.cs`
Namespace `KieshStockExchange.Services.MarketDataServices.Helpers`. One file, many chart types (non-drawing ones LEAVE in place: ChartStyle, VolumeMode, PriceScaleMode, MaKind, ChartViewport, MaPoint, MovingAverageSeries, OpenOrderLine, FillMarker, PositionLine, DepthLevel, TriggerMarker). Drawing-relevant:
- **L51-56 `enum DrawTool`** = `None, HLine, Trend, Ray, HRay, Polyline, Measure, VLine, Freehand, Rectangle, Ellipse, Text, Magnifier, Position, Alert, Arrow, ExtendedLine`. Append-only (L49-50 comment).
- **L60 `enum DrawingHitPart`** = `Body, Anchor1, Anchor2, Close` (remove `Close`).
- **L64 `enum DashKind`** = `Solid, Dash, Dot`.
- **L69 `enum LineEnding`** = `None, End, Start, BothOut, BothForward` (Start/BothForward legacy).
- **L74 `enum ArrowHeadStyle`** = `FilledTriangle, Open, Outline` (FilledTriangle=ordinal 0).
- **L18 `record struct MeasureState(bool Active, float X0,Y0,X1,Y1)`**; **L13 `record struct CrosshairState(bool Visible, float X,Y, int? CandleIndex)`**.
- **L82-87 `record struct DrawStyle(Color Color, float Thickness, DashKind Dash, bool Arrow=false, LineEnding Ending=None, ArrowHeadStyle Head=FilledTriangle)`** + `static Default = new(Color.FromArgb("#4C9AFF"),1.5f,Solid)`. Uses MAUI `Color`. Append Fill/FillOpacity/Size here.
- **L90 `record struct DrawPoint(DateTime T, decimal P)`**.
- **L98-100 `record struct DrawingObject(Guid Id, DrawTool Kind, DateTime T1, decimal P1, DateTime T2, decimal P2, DrawStyle Style, IReadOnlyList<DrawPoint>? Points=null)`**. Append Text/P3/Qty/Locked/Smoothing/Direction here.
- **L104-126 `record struct MaColorOption(string Key, string Name)`** + `All`/`FromKey`.
- **L131-143 `sealed class ColorJsonConverter : JsonConverter<Color>`**: Read (L133-138) empty→`DrawStyle.Default.Color`; **Write (L140-142) `(value ?? DrawStyle.Default.Color).ToArgbHex(true)` — the null→blue BUG at L142.**

### `KieshStockExchange/ViewModels/TradeViewModels/ChartViewModel.cs`
`public partial class ChartViewModel : StockAwareViewModel`, ns `KieshStockExchange.ViewModels.TradeViewModels`.
- L304 `DrawTool _drawTool=None`; L307-316 `PenToolLabel`; L318 `OnDrawToolChanged`; L324-329 `SelectDrawTool`.
- L333 `ObservableCollection<DrawingObject> Drawings`; L337 `Guid? _selectedDrawingId`; L339 `HasSelectedDrawing`; L342 `IsDefaultPenMode`; L343 `PenPanelHeader`; L348-365 `OnSelectedDrawingIdChanged`.
- L467-487 default-draw-style persistence; L500-525 pen palette + tiles; L657-661 stamped commands.
- L1137-1141 `AddDrawing`; L1144-1150 `RemoveDrawing`; **L1155-1167 `MutateSelectedStyle`** (mirror for `MutateSelectedDrawing`); L1172-1176 `ApplyPenStyle`; L1178-1209 `SetDefault*` (Head no-ops if `!CanEditHead`); **L1202 `CanEditHead => EffectivePenStyle().Ending != None`** (the precedent to follow); L1213-1224 `SetSelectedAsDefault`/`DeleteSelectedDrawing`; L1227-1233 `EffectivePenStyle`; L1236-1239 `NormalizeStyle`; L1249-1281 `RefreshPenTiles`; L1288-1292 `UpdateDrawing`.
- **L1296-1299 `_drawingJson = new JsonSerializerOptions { Converters = { new ColorJsonConverter() } }`**; L1302-1307 `PersistDrawings`; L1313-1339 `LoadDrawingsForSelected` (`_drawingsKey = $"{DrawingsPrefKeyBase}{StockId}_{Currency}"`; L1332-1334 migrates Arrow→Ending=End).

### `KieshStockExchange/ViewModels/TradeViewModels/PenTiles.cs`
5 `public partial class : ObservableObject` tiles (PenColorTile/PenWidthTile/PenDashTile/PenEndingTile/PenHeadTile): value prop + `ICommand? Command` + `[ObservableProperty] bool _isSelected` + (all but color) `StylePreviewDrawable _specimen`.

### `KieshStockExchange/Services/MarketDataServices/Helpers/CandleChartDrawable.cs`
`public sealed class CandleChartDrawable : IDrawable`, ns `KieshStockExchange.Services.MarketDataServices` (NOT .Helpers). ~1855 lines. Consumes: L50 `Drawings`, L51 `DraggingDrawingId`, L52 `SelectedDrawingId`, L56 `BuildingPolyline`, L57 `BuildingPolylineCursor`, L41 `Measure`, L37 `Crosshair`, L12 `Candles`.
- L160-346 `Draw(ICanvas,RectF)`; local transforms **L301 `float X(DateTime)`**, **L302 `float Y(double)`** (→PriceToFrac); `DrawDrawings` at L322.
- **L600-731 `DrawDrawings`** — per-Kind if/else: L623 HLine / L641 HRay / L654 Polyline / L709 else Trend|Ray (only 4 branches; VLine/Freehand/Rect/Ellipse/Text NOT handled — add Rect/Ellipse/ExtendedLine).
- Helpers: L758 DashPattern, L766 DrawGutterPriceTag, L780 DrawEndpointPriceTag, L798 DrawTrendLabels, L835 RayExit, L848 EndSize, L850-857 DrawHandle, **L859-867 `DrawCloseGlyph ("✕")` — REMOVE**.
- Hit-test: L872 PriceToPixelY, L875 TimeToPixelX, **L886-943 `HitDrawing(PointF) → (DrawingObject, DrawingHitPart)?`** (4-kind chain, topmost-first — remove the `Close` part), L945-965 WithinBox/Dist/PointSegDist/HitOpenOrderLine.
- Transforms (route through `IScaleTransform`): L1187-1191 ComputePlotRect, L1216 PlotRect, L1232-1250 PriceToFrac/FracToPrice (ScaleMode-aware), **L1515-1522 PixelToPrice**, **L1528-1535 PixelToTime**, L1593 DrawCrosshair, L1659 DrawMeasure.

### `StylePreviewDrawable.cs`
ns `KieshStockExchange.Services.MarketDataServices.Helpers`. `enum StylePreviewMode{Line,Dot,Dash}`; `sealed class StylePreviewDrawable : IDrawable`. Statics: DashPattern, DrawStraightSegment, DrawEndings, DrawArrowHead. Uses MAUI Color/`Colors`/`ICanvas`/`PathF` — all fine in the client project.

### Shared project — the constraint that MADE Option A
`KieshStockExchange.Shared.csproj`: `TargetFramework=net9.0`, `RootNamespace=KieshStockExchange`, plain `Microsoft.NET.Sdk` (NOT MAUI). **No `Microsoft.Maui.Graphics` reference** (packages = CommunityToolkit.Mvvm + Logging.Abstractions only). So `Color`/`IDrawable`/`ICanvas`/`RectF`/`PathF`/`Colors` are UNAVAILABLE in Shared. **Under Option A nothing moves here** — the model stays client-side. Shared/Models files declare `namespace KieshStockExchange.Models;` (folder-derived, NOT `.Shared.Models`).

### Blast radius (files needing `using` updates for Item 0)
`ChartTypes.cs` (declares), `CandleChartDrawable.cs`, `StylePreviewDrawable.cs`, `ChartViewModel.cs`, `PenTiles.cs`, `ChartView.xaml.cs`. Each resolves the moved types via `using KieshStockExchange.Services.MarketDataServices.Helpers;` → add the new `using KieshStockExchange.Models.ChartDrawing.{Tools,Style,Objects};`. **`ChartView.xaml`** references CLR types via `xmlns:clr-namespace` — check whether `DrawTool`/`DrawStyle`/etc. appear as a XAML element or `x:Static`; if so, update that one `clr-namespace` (namespace-correctness only, no visual change); if not, do not touch the XAML.

---

## 4. FIRE-CONTRACT — verbatim seam signatures (LP1–LP5 and UP-STORE bind to THESE)

The following public surface is the handoff contract. Emit it exactly so downstream local phases and UP-STORE don't reinvent it:

```csharp
// Models/ChartDrawing/Tools
enum DrawTool { None, HLine, Trend, Ray, HRay, Polyline, Measure, VLine, Freehand,
                Rectangle, Ellipse, Text, Magnifier, Position, Alert, Arrow, ExtendedLine,
                RotatedRect, Triangle, Arc, FibRetracement }   // append-only

// Models/ChartDrawing/Style
enum SizeKind { Small, Medium, Large }
record struct DrawStyle(Color Color, float Thickness, DashKind Dash,
                        bool Arrow = false, LineEnding Ending = LineEnding.None,
                        ArrowHeadStyle Head = ArrowHeadStyle.FilledTriangle,
                        Color? Fill = null, float FillOpacity = 0.15f, SizeKind Size = SizeKind.Medium);

// Models/ChartDrawing/Objects
enum AlertCondition { CrossAny, CrossUp, CrossDown }
record struct DrawingObject(Guid Id, DrawTool Kind, DateTime T1, decimal P1, DateTime T2, decimal P2,
                            DrawStyle Style, IReadOnlyList<DrawPoint>? Points = null,
                            string? Text = null, decimal P3 = 0m, decimal Qty = 0m,
                            bool Locked = false, float Smoothing = 0.5f, int Direction = 0);

// Helpers/Drawing
enum MagnetMode { Off, Weak, Strong }
[Flags] enum AxisMask { None = 0, X = 1, Y = 2, Both = X | Y }
readonly record struct SnapResult(PointF Point, bool Snapped, SnapCandidateKind Kind);
static class MagnetSnapper { static SnapResult Snap(PointF px, MagnetMode mode, AxisMask axis,
                                                    IReadOnlyList<SnapCandidate> candidates); }
static class SplineSmoother { static IReadOnlyList<DrawPoint> Decimate(IReadOnlyList<DrawPoint> pts, float minPx);
                              static PathF Evaluate(IReadOnlyList<DrawPoint> pts, float tension); }
static class ChartSnapshotRenderer { static byte[] Render(CandleChartDrawable drawable, int w, int h); }
sealed class UndoStack { void Push(DrawingMutation m); bool Undo(); bool Redo(); void Clear();
                         bool CanUndo { get; } bool CanRedo { get; } }
interface IScaleTransform { float PriceToPixelY(decimal price, /*plot ctx*/); decimal PixelToPrice(float y, /*plot ctx*/); }

// ChartViewModel (public logic surface)
DrawTool EditingKind { get; }
bool ShowStroke/ShowFillColor/ShowOpacity/ShowDash/ShowEnding/ShowHead/ShowText/ShowPosition { get; }  // final set = agent's call, must cover every tool
void MutateSelectedDrawing(Func<DrawingObject, DrawingObject> mutate);   // no-op if selection Locked
```

Adjust exact member shapes where the codebase demands (e.g. `IScaleTransform`'s plot-context params must match `ComputePlotRect`/`PriceToFrac`), but keep the NAMES stable — they are referenced by the build strategy and downstream phases.

---

## 5. Validation gates (the blind-patch acceptance test)

1. **Compile client app-closed:** `dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0` — clean (0 errors). (Full exe link needs the client closed; compile is the gate.)
2. **Unit tests green** in `KieshStockExchange.Tests`: new `MagnetSnapper` (weak-8px / strong-always / not-near-candle / exempt), `UndoStack` (one-per-gesture / skip-locked / redo-invalidation / cap-50 / clear-on-switch), `SplineSmoother` (decimation drops sub-minPx, tension=0 passes through points), `ColorJsonConverter` (null→null round-trip + `#RRGGBBAA` unchanged) + the full existing suite stays green.
3. **Identical-render:** manual/visual confirmation the chart looks exactly as today (no new UI reachable; all new fields default). A serialize→deserialize round-trip of an existing drawings blob must produce an equal `DrawingObject` list (back-compat).
4. **No forbidden touches:** `git diff --stat` shows no `*.xaml` change except (at most) one `clr-namespace` line, and no pointer/gesture/timer/cursor edits.

---

## 6. Post-fire (my job, not the ultraplan's)

Kiesh fires `/ultraplan docs/ultraplan-chart-drawing-core.md`. **I** apply the returned patch, fix any compile gaps minimally (the cloud agent has no SDK), run the gates above with the client closed, then **merge to `feature/bot-market-realism-v2` before any XAML/LP work** (the `ChartViewModel` merge conflict costs more than any parallelism). Then author + fire **UP-STORE** (its wire contract = this patch's `"v":1` payload), and begin **LP1** once UP-CORE merges.
