# CandleChartDrawable Arc 2 — Composed-Collaborator Plan

Target: the 9-file partial class `CandleChartDrawable` in
`KieshStockExchange/Services/MarketDataServices/Helpers/` (spine + `.Axes` / `.Candles` /
`.Indicators` / `.Overlays` / `.Drawings` / `.HitTest` / `.Crosshair` / `.Measure`), as shipped by
Arc 1 at commit `2d4491f`. Namespace ground truth (unchanged, still the #1 trap):
`KieshStockExchange.Services.MarketDataServices` — **NOT** `...Helpers`.

This file is the contract for the composition arc. Prerequisite reading for the executor:
`docs/CANDLECHARTDRAWABLE_SPLIT_PLAN.md` (Arc 1) — its §2 member→concern map, §3 frozen public
surface, and §4 state-flow analysis are **incorporated by reference and not re-derived here**.

---

## 1. Purpose + why this is a separate, behaviour-touching arc

Arc 1 was a **pure move**: one class → 9 partial files, byte-identical member bodies, verified by a
mechanical sorted-line multiset diff. That gave us navigable files but **removed zero coupling** —
all 9 partials still share every private field of one god-class.

Arc 2 is the owner's preferred end state: **real composed collaborator classes** — a renderer per
concern plus a hit-tester — that the parent `CandleChartDrawable` constructs and delegates to.
This arc **changes member bodies**:

- `Dist(...)` becomes `ChartGeometry.Dist(...)`;
- the `X(time)` / `Y(price)` local closures `Draw()` builds become methods on a reified
  `RenderFrame` context object;
- private cross-partial field reads (`_lastPlot`, `_lastYMin`, …) become explicit parameters.

Because bodies change, **the sorted-line diff proof is gone**. The 2-arc rule exists precisely for
this: Arc 1's reviewer could verify "nothing changed" mechanically; Arc 2's reviewer cannot, so
Arc 2 needs a real behavioural gate. That gate is the **golden-image render comparison** (§2) plus
a **hit-test probe-grid dump** — both must exist and be baselined **before the first collaborator
is extracted**. Every step of this arc is gated on: build green, full `dotnet test` green, golden
images byte-match (or within the declared tolerance), probe dump identical.

### The anti-goal: composition theater

The council's core finding, which this plan is built around: naively extracting a
`CandleRenderer` that takes the parent (or a 40-field parameter blob) converts implicit coupling
into ceremonial coupling and removes nothing. The rules that keep this real:

1. **No collaborator ever holds a reference to `CandleChartDrawable`.** One-way dependency only:
   parent knows collaborators; collaborators know `RenderFrame`, `ChartTheme`, and their own
   explicit inputs.
2. **The per-frame mutable cache is reified into `RenderFrame`** (§3) — one immutable context
   built per paint, consumed by every renderer and by hit-testing. This is the seam that actually
   removes coupling (and fixes the latent temporal-coupling smell where hit-testing reads
   "whatever `Draw()` last happened to write").
3. **Palette/config travels as a cohesive `ChartTheme`** (§4.2), not as a junk-drawer blob and not
   smuggled through a parent back-reference. Data series (candles, drawings, orders…) travel as
   explicit per-call parameters.
4. **No new interfaces.** Collaborators are `internal sealed` classes. The only interface in play
   is the pre-existing `IScaleTransform`. An interface earns its existence when a second
   implementation exists; none does.

---

## 2. The golden-image verification harness (BUILD THIS FIRST)

This is the linchpin. Nothing may be extracted until the harness exists and a baseline is captured
on unmodified `2d4491f` code.

### 2.1 Feasibility verdict: FEASIBLE via a link-compile console harness — no blocker found

Grounded facts (all verified in-repo 2026-07-17):

- `ChartSnapshotRenderer.Render(...)` (`Helpers/Drawing/ChartSnapshotRenderer.cs`) already renders
  a `CandleChartDrawable` to PNG bytes via `SkiaBitmapExportContext` — deterministic CPU raster,
  headless, no windowing, no MAUI application object.
- **The drawable's full compile closure needs NO MAUI-Controls type.** It touches only:
  - `Microsoft.Maui.Graphics` types (`ICanvas`, `Color`, `RectF`, `PointF`, `PathF`, `Colors`,
    alignment/line enums) — available as the **standalone, workload-free**
    `Microsoft.Maui.Graphics` NuGet package. The existing test project already proves this
    pattern: `KieshStockExchange.Tests.csproj` is plain `net9.0`, references
    `Microsoft.Maui.Graphics` **9.0.90**, and link-compiles chart files headlessly.
  - Shared-project types via ProjectReference: `Candle`, `CurrencyType`, `CurrencyHelper`,
    `TimeHelper` (all in `KieshStockExchange.Shared`, TFM `net9.0`). The
    `...MarketDataServices.Interfaces` using resolves via Shared too (`ICandleService` et al.
    live there), so the replicated 9-line using block compiles as-is.
  - Client-side headless files that must be link-compiled alongside the drawable:
    `Helpers/ChartTypes.cs` (ChartViewport, CrosshairState, MeasureState, ChartStyle,
    PriceScaleMode, MovingAverageSeries, OpenOrderLine, PositionLine, FillMarker, TriggerMarker,
    DepthLevel, DrawingHitPart), `Helpers/StylePreviewDrawable.cs` (DrawStraightSegment /
    DrawEndings / DrawArrowHead), `Helpers/Drawing/IScaleTransform.cs`, and
    `Models/ChartDrawing/**` (already link-proven headless by the test project).
- `Microsoft.Maui.Graphics.Skia` (client csproj carries it at `$(MauiVersion)`) targets plain
  .NET — it does **not** require the Windows TFM or the MAUI workload. Its transitive `SkiaSharp`
  package ships the `win-x64` native `libSkiaSharp` in the main nupkg, so a plain `net9.0`
  console/test host on Windows loads it without extra `NativeAssets` packages.
- Determinism hook exists for the one wall-clock read:
  `TimeHelper.NowUtc` is `public static Func<DateTime> { get; set; }`
  (`KieshStockExchange.Shared/Helpers/TimeHelper.cs:8`) — the harness pins it to a fixed instant
  inside the fixture viewport so `DrawCurrentPriceLine`'s `now < tMin || now > tMax` gate is
  deterministic and the line actually renders.

**Rejected alternatives** (evaluated, do not use):
- *Add Skia + the drawable to `KieshStockExchange.Tests`* — the test csproj comment explicitly
  and deliberately excludes ChartSnapshotRenderer/Skia from its link set; polluting the fast
  headless unit-test project with a native dependency for an arc-scoped gate is the wrong trade.
  Do NOT touch the test csproj link set (one optional exception in §7 step 2).
- *A `net9.0-windows10.0.19041.0` harness that ProjectReferences the MAUI client csproj* — drags
  the MAUI workload + a SingleProject Exe reference into every gate run; slow and fragile. Keep as
  the **fallback recipe** only if link-compile hits an unforeseen wall (none found).

### 2.2 The harness project — concrete recipe

New project `KieshStockExchange.RenderHarness/` at repo root (a console app, not xunit — two
verbs, exit-code gated, no test-discovery overhead). It is committed with the arc; the golden
PNGs are **not** committed (fonts + timezone are machine-properties, see §2.5) — they live in
`data/chart-goldens/` (the `data/` folder is already untracked).

`KieshStockExchange.RenderHarness/KieshStockExchange.RenderHarness.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <!-- Align with the test project's pinned Graphics version; Skia must match it exactly. -->
    <PackageReference Include="Microsoft.Maui.Graphics" Version="9.0.90" />
    <PackageReference Include="Microsoft.Maui.Graphics.Skia" Version="9.0.90" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Microsoft.Maui.Graphics" />
    <Using Include="Xunit" Condition="false" /> <!-- none: console, not xunit -->
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\KieshStockExchange.Shared\KieshStockExchange.Shared.csproj" />
  </ItemGroup>

  <!-- Link-compile the drawable + its headless closure out of the client project.
       Globs are deliberate: new CandleChartDrawable partials shrink/disappear and new
       collaborator classes appear under Helpers/Drawing/ as the arc proceeds — both are
       swept automatically, so the harness needs no csproj edit per step. -->
  <ItemGroup>
    <Compile Include="..\KieshStockExchange\Services\MarketDataServices\Helpers\CandleChartDrawable*.cs"
             Link="Chart\%(Filename)%(Extension)" />
    <Compile Include="..\KieshStockExchange\Services\MarketDataServices\Helpers\Drawing\*.cs"
             Link="Chart\Drawing\%(Filename)%(Extension)" />
    <Compile Include="..\KieshStockExchange\Services\MarketDataServices\Helpers\ChartTypes.cs"
             Link="Chart\ChartTypes.cs" />
    <Compile Include="..\KieshStockExchange\Services\MarketDataServices\Helpers\StylePreviewDrawable.cs"
             Link="Chart\StylePreviewDrawable.cs" />
    <Compile Include="..\KieshStockExchange\Models\ChartDrawing\**\*.cs"
             Link="ChartDrawing\%(RecursiveDir)%(Filename)%(Extension)" />
    <!-- Step 2 adds: ..\KieshStockExchange\Helpers\ChartGeometry.cs -->
  </ItemGroup>
</Project>
```

Notes on the link set: the `Drawing\*.cs` glob sweeps `ChartSnapshotRenderer.cs` (wanted — Skia is
referenced here, unlike in the unit-test project), `IScaleTransform.cs`, `MagnetSnapper.cs`,
`SplineSmoother.cs`, `UndoStack.cs` (all already proven headless-compilable). When Step 2 creates
`KieshStockExchange/Helpers/ChartGeometry.cs`, add the one explicit `Compile Include` line for it.

### 2.3 Program shape: `capture` and `verify`

`Program.cs` responsibilities (keep it one file + a `Scenes.cs`; this is a gate tool, not a
product):

1. **Pin the environment**:
   ```csharp
   CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
   CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // CurrencyHelper / ToString paths
   TimeHelper.NowUtc = () => new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
   ```
   (Timezone is NOT pinned — `ToLocalTime()` in the axis/tag formatting uses the machine zone;
   same-machine before/after comparison cancels it, §2.5.)
2. **Build each scene** (§2.4): construct a **fresh** `CandleChartDrawable` per scene (autofit
   hysteresis `_autoFit*` is cross-frame state — a fresh instance + a single `Draw` per image is
   the deterministic first-frame path), set its properties from the deterministic fixture, call
   `ChartSnapshotRenderer.Render(drawable, 900, 600, Colors.Black, scale: 2f, title: sceneId)`.
3. **Hit-probe dump** for the scenes marked `[probe]`: after rendering, walk a fixed lattice
   `x = 0, 8, 16 … 900; y = 0, 8 … 600` and for each point record
   `HitDrawing`, `HitOpenOrderLine`, `HitCandleIndex`, `PixelToPrice` (rounded to 6 decimals),
   `PixelToTime` (ticks), `IsInChartArea`, `IsInYAxisGutter` into a sorted text file
   (`<scene>.probes.txt`, one line per non-trivial result). This is the gate for the hit-test
   concern, which images cannot cover.
4. **`capture`**: write `<scene>.png` + `<scene>.probes.txt` to `data/chart-goldens/`.
   **`verify`**: re-render, byte-compare PNG + text files against the goldens; print a per-scene
   PASS/FAIL table; on any FAIL also write the offending render to
   `data/chart-goldens/diff/<scene>.actual.png` for eyeballing; exit 0 only if all pass.
5. **Tolerance fallback** (only if byte-compare proves flaky run-to-run on the SAME commit — not
   expected from Skia CPU raster, but pre-authorized so a flake doesn't stall the arc): decode
   both PNGs (SkiaSharp is already available) and pass when max per-channel delta ≤ 1 and
   mismatching pixels ≤ 0.01%. If the fallback is engaged, record that fact in the arc log; the
   probe text files must always be byte-identical, no tolerance there.

Gate commands (used at every step in §7):

```powershell
dotnet run --project KieshStockExchange.RenderHarness -- capture   # baseline only
dotnet run --project KieshStockExchange.RenderHarness -- verify    # every step
```

### 2.4 The fixture scene matrix — every concern exercised

Fixture data is **closed-form deterministic** (no `Random`, not even seeded — closed-form survives
runtime upgrades): 120 one-minute candles starting `2026-07-17 10:00Z`,
`close(i) = 100 + 8*sin(i/9.0) + 3*sin(i/2.3)`, `open(i) = close(i-1)`,
`high/low = max/min(open, close) ± (1.5 + |sin(i/5.0)|)`, `volume(i) = 500 + i*7 % 400`,
`CurrencyType.USD`. Viewport = `[10:00Z, 12:06Z]` bucket 1 min (so `NowUtc` = 12:00Z is inside →
current-price line renders). MAs: SMA-9 and EMA-21 precomputed from the closes.

| Scene | Exercises |
|---|---|
| `S01-core` **[probe]** | Candles style, linear scale, YAutoFit, volume **overlay**, mood pane ON (60-pt mood series sweeping 10→90), depth ON (12 bid + 12 ask levels), MAs, open-order lines (resting buy, resting sell, armed stop, stop-limit, dormant TP + SL legs — covers dash/alpha/pill variants), position line (long, in profit), 4 fill markers (2 buy / 2 sell), 2 trigger markers, current-price line + `SessionOpenPrice` (2-line tag) |
| `S02-hollow` | HollowCandles style (up-bar outline path) |
| `S03-bars` | Bars style + volume **sub-pane** mode (`OverlayVolume=false` — pane border + post-border draw order) |
| `S04-line` | Line style + `ShowVolume=false` + mood OFF (off-paths) |
| `S05-area` | Area style (gradient fill polygon) |
| `S06-heikin` | Heikin-Ashi (transform path) |
| `S07-log` | S01 data + `ScaleMode=Logarithmic` (PriceToFrac log branch, gridlines, hit-inverse) |
| `S08-percent` | `ScaleMode=Percent` (pctRef relabeling branch) |
| `S09-drawings` **[probe]** | Every committed DrawTool: HLine, HRay, VLine, Trend (+labels), Ray, ExtendedLine, Polyline (ending `BothOut`, head cut), Freehand (smoothing=1, ending `End`, head-trim `PullBack` path), Rectangle + Ellipse (fill+opacity), Arrow (block-arrow). Varied `DashKind` per drawing. One drawing in `SelectedDrawingId` (handles), two in `SelectedDrawingIds` (multi-select), one in `DraggingDrawingId` (emphasis) |
| `S10-building` | In-progress **polyline** preview (3 vertices + cursor + ending head + vertex dots) — then a second image `S10b` for the in-progress **freehand** preview (B-spline, no dots) |
| `S11-interact` | Crosshair visible + snapped to a candle (`CandleIndex` set), Shift-drag measure ruler active (down-move → red tint), position/order lines still on |
| `S12-transient` | Zoom box active + one order line dragging (`DraggingOrderId`+`DraggingOrderPrice` ≠ stored price) + `YAutoFit=false` with `ManualYMin/Max` (manual-range branch) |
| `S13-empty` | `Candles=[]`, invalid viewport → the `DrawNoData` path |
| `S14-autofit` *(two `Draw` calls, second image only)* | Draw once, append a +15% spike candle, draw again — exercises `SmoothAutoFit`'s immediate-expand path deterministically. (Contract-lerp path is inherently multi-frame; it stays IN the spine untouched, so this single expand-path scene is sufficient.) |

14 scenes ≈ 15 PNGs @1800×1200 + 2 probe dumps. Render cost is seconds; run `verify` at every
step gate.

### 2.5 Baseline + the machine caveat

- Capture the baseline on **unmodified `2d4491f`** (harness commit contains no drawable edits).
  The baseline is the golden. Every subsequent step re-renders and compares.
- **Fonts and timezone are machine properties** (Skia default typeface resolution, `ToLocalTime`).
  Goldens are therefore valid only on the machine + SDK/package-restore that captured them —
  which is exactly the arc workflow (before/after on the same machine cancels both). Hence:
  goldens untracked, package versions pinned, and if the machine or SDK changes mid-arc,
  re-capture the baseline from the last verified-green commit before continuing.
- **The harness itself is frozen during the arc.** If a scene must change (bug in the fixture),
  fix it, re-capture from the last verified-green commit, and note it — never change scene +
  drawable in the same step.

---

## 3. The `RenderFrame` design

One immutable context object per paint, replacing the seven `_last*` cache fields AND the
`X`/`Y` closures. Home: `Helpers/Drawing/RenderFrame.cs`, namespace
`KieshStockExchange.Services.MarketDataServices.Helpers.Drawing` (the established collaborator
folder — MagnetSnapper/SplineSmoother/IScaleTransform precedent).

```csharp
// One paint's worth of committed geometry: pane rectangles, axis ranges, and every data<->pixel
// transform. Built exactly once per Draw(); renderers receive it as a parameter; hit-testing reads
// the last-built instance. Immutable — a frame never changes after construction.
internal sealed class RenderFrame
{
    public RectF Plot { get; }
    public RectF VolRect { get; }
    public RectF MoodRect { get; }
    public double YMin { get; }
    public double YMax { get; }
    public DateTime TMin { get; }
    public DateTime TMax { get; }
    public double SpanSec { get; }            // precomputed once, exactly as Draw() does today
    public PriceScaleMode Mode { get; }       // captured — renderers stop reading the live property
    public TimeSpan Bucket { get; }           // Viewport.Bucket snapshot (measure/trend bar counts)
    public CurrencyType Currency { get; }
    private readonly IScaleTransform _scale;  // stateless; injected by the drawable's ctor field

    // ---- forward transforms (RENDER form — float-cast order preserved from the closures) ----
    public float MapX(DateTime utc)
        => Plot.Left + (float)(((utc - TMin).TotalSeconds / SpanSec) * Plot.Width);
    public float MapY(double price)
        => Plot.Bottom - (float)(ChartGeometry.PriceToFrac(price, YMin, YMax, Mode) * Plot.Height);
    public float ScaleY(decimal price)        // the ExtendedLine/Rect/Ellipse/Arrow seam
        => _scale.PriceToPixelY(price, Plot, YMin, YMax, Mode);

    // ---- forward transforms (HIT form — subtract-in-double, cast last; see §8 float warning) ----
    public float HitPriceToPixelY(decimal price)
        => (float)(Plot.Bottom - ChartGeometry.PriceToFrac((double)price, YMin, YMax, Mode) * Plot.Height);
    public float TimeToPixelX(DateTime t) { /* verbatim body of today's TimeToPixelX */ }

    // ---- inverse transforms (verbatim bodies of today's public PixelToPrice / PixelToTime) ----
    public decimal? PixelToPrice(float y) { … }
    public DateTime PixelToTime(float x) { … }

    // Pre-first-paint stand-in reproducing today's field initializers:
    // Plot/VolRect/MoodRect = default, YMin = 0, YMax = 1.0, TMin = TMax = default.
    public static readonly RenderFrame Empty = …;

    // Early-return paths in Draw() commit new pane rects while KEEPING the previous ranges —
    // reproducing today's behaviour where _lastPlot/_lastVolRect/_lastMoodRect are written
    // before the no-data / zero-span bailouts (spine lines 231–233) but the ranges are not.
    public RenderFrame WithRects(RectF plot, RectF vol, RectF mood) => …;
}
```

### 3.1 How it replaces the cache + closures

- Spine deletes `_lastPlot`, `_lastVolRect`, `_lastMoodRect`, `_lastYMin`, `_lastYMax`,
  `_lastTMin`, `_lastTMax` and gains a single `private RenderFrame _frame = RenderFrame.Empty;`.
- `Draw()` builds the frame **after** the Y-fit commit point (today's spine lines 319–323) and
  assigns `_frame = frame` **before dispatching any renderer** — the same ordering guarantee the
  cache has today, now visible in one assignment instead of seven.
- **The two early returns are the trap**: today, `Draw()` writes the three pane rects at lines
  231–233 and *then* may bail out (no candles+no viewport at ~254; `spanSec <= 0` at ~260),
  leaving NEW rects paired with OLD ranges in the cache. Preserve exactly:
  both bailout paths execute `_frame = _frame.WithRects(plot, volRect, moodRect);` before
  returning. Skipping this would silently change `IsInChartArea`/`PlotRect` behaviour after a
  pan-into-empty paint.
- The `X`/`Y` local functions are deleted; call sites use `frame.MapX(...)` / `frame.MapY(...)`.
  During the transition (§7 step 1) the un-extracted helpers keep their `Func<>` parameters and
  `Draw()` passes `frame.MapX` / `frame.MapY` as method groups — same math, one delegate
  allocation per frame (as today). Each renderer extraction then replaces that renderer's
  `Func<>` parameters with the `RenderFrame` parameter, and the method-group delegates disappear
  entirely once the last consumer is extracted.
- Public read-only surface forwards: `PlotRect => _frame.Plot`, `VolumeRect => _frame.VolRect`,
  `MoodRect => _frame.MoodRect`, `LastYMin => _frame.YMin`, `LastYMax => _frame.YMax`; public
  `PixelToPrice`/`PixelToTime` forward to `_frame`. Signatures unchanged (frozen surface, §6).

### 3.2 Why `sealed class`, not `readonly struct`

The frame is ~90 bytes + a reference; a `readonly struct` would need `in` at every signature to
avoid copies and invites accidental-copy bugs; the class costs one small allocation per paint in a
path that already allocates `PathF` instances per candle batch. Identity also matters: "the last
built frame" is naturally a reference. If profiling ever shows the alloc (it won't), converting to
a struct later is mechanical.

### 3.3 What deliberately does NOT go in the frame

- Theme/palette (goes in `ChartTheme`, §4.2 — hit-testing consumes the frame and must not drag
  colors along).
- Data series (candles/drawings/orders — per-call renderer parameters; the frame is geometry).
- Autofit hysteresis (`_autoFit*` + `SmoothAutoFit`/`SnapRange`) — that is *input pipeline* for
  the next frame's YMin/YMax, inherently cross-frame mutable state, and it stays in the spine.

---

## 4. The collaborator set

All renderers: `internal sealed`, stateless, constructed once in the drawable
(`private readonly AxisRenderer _axes = new();` …), living in `Helpers/Drawing/`, namespace
`...Helpers.Drawing`. Method pattern:
`public void Draw(ICanvas canvas, RenderFrame f, ChartTheme t, <specific inputs>)`.

### 4.1 `ChartGeometry` — the pure-math static

Per Arc-1 §6: new file `KieshStockExchange/Helpers/ChartGeometry.cs`, namespace
`KieshStockExchange.Helpers` (NOT the VM-level `ChartMath`). Absorbs, verbatim bodies with
`private`→`public`:

| From | Members |
|---|---|
| `.HitTest.cs` | `Dist`, `PointSegDist` |
| `.Drawings.cs` | `RayExit`, `EndSize`, `DashPattern`, `BlockArrowPath`, `PullBack` |
| `.Axes.cs` | `NiceRange` (+ its local `NiceNum`), `ChooseTimeStep`, `AlignToStep` |
| `.Measure.cs` | `HumanizeSpan` |
| `.Axes.cs` (instance→static) | `PriceToFrac`, `FracToPrice` — gaining a `PriceScaleMode mode` parameter (they read only `ScaleMode`; the frame passes its captured `Mode`) |

Also new consts (shared by DrawingRenderer + ChartHitTester so the visible shape and its
clickable zone keep one source of truth): `DrawHandleR = 4f`, `DrawHitTol = 5f`. And an
`internal static class ChartLayout` (same file or sibling) for the 9 layout consts
(`RightAxisW`, `BottomAxisH`, `TopPad`, `LeftPad`, `VolumePaneRatio`, `VolumePaneGap`,
`VolumePaneMinChartHeight`, `MoodPaneRatio`, `MoodPaneGap`) — read by the spine
(`ComputePlotRect`, pane split) and by every renderer that today references `RightAxisW`/
`BottomAxisH` for gutter tags. `SnapRange` stays in the spine (it is autofit plumbing) and calls
`ChartGeometry.NiceRange`.

NOTE, not done this arc: `RegularScaleTransform` carries private mirror copies of
PriceToFrac/FracToPrice, and `StylePreviewDrawable` a private `DashPattern` — collapsing those
onto `ChartGeometry` is a polish-arc dedup (behaviour-identical only if verified by the golden
gate; the mirrors exist deliberately per their comments, so touch them last or never).

### 4.2 `ChartTheme` — how the ~40 palette/config fields travel

The public palette fields **stay on the drawable** — they are frozen public surface written by
ChartView (§6). What changes is how renderers *read* them: `Draw()` builds one
`internal readonly record struct ChartTheme(...)` per paint (a ~20-member copy, negligible) and
passes it alongside the frame. A theme is a real, cohesive domain concept — this is not the
council's "40-field blob" (that warning was about mixing geometry+state+data+config into one bag;
frame/theme/data stay three separate things).

Members (from the §5 matrix): `Bg, Axis, Grid, Bull, Bear, PriceLineUp, PriceLineDown,
CrosshairColor, OpenOrderBuyColor, OpenOrderSellColor, OpenOrderStopColor, PositionLineColor,
FillBuyColor, FillSellColor, TriggerColor, VolumeBullTint, VolumeBearTint, MoodLineColor,
DrawingColor, AxisFont, PriceTagFont`. Excluded: `MarkerColor` (dead — written by ChartView,
read by nothing; the field stays on the drawable untouched, its deletion is a polish-arc +
ChartView edit). `LabelPillBg` moves as a private static onto `DrawingRenderer` (its only
consumer).

`OutlineForBackground()` becomes `ChartTheme.Outline` (computed in the ctor or a method on the
theme — it derives purely from `Bg`; consumed by OverlayRenderer's fill markers and
DrawingRenderer's handles).

### 4.3 The renderers + hit-tester

| Collaborator | Absorbs (current partial → members) | Inputs beyond `(ICanvas, RenderFrame, ChartTheme)` |
|---|---|---|
| `AxisRenderer` | `.Axes`: `DrawYGridAndLabels`, `DrawXGridAndLabels` | `IReadOnlyList<Candle>` (Percent-mode pctRef = `Candles[0].Close`) |
| `CandleRenderer` | `.Candles`: `DrawCandles`, `DrawCloseLine`, `ComputeHeikinAshi`, `DrawCurrentPriceLine` | `IReadOnlyList<Candle>`, `ChartStyle`, `decimal? currentPrice`, `decimal? sessionOpenPrice` |
| `IndicatorRenderer` | `.Indicators`: `DrawMovingAverages`, `DrawVolume`, `DrawMood`, `DrawDepth` | `MaSeries`, `Candles` (volume bars), `bool overlayVolume`, `MoodSeries`, `DepthLevels` (frame carries `Bucket` for the half-bucket MA shift) |
| `OverlayRenderer` | `.Overlays`: `DrawOpenOrderLines`, `DrawPositionLine`, `DrawFillMarkers`, `DrawTriggerMarkers`, `SnapToCandleCenterX` | `OpenOrderLines`, `int? draggingOrderId`, `decimal? draggingOrderPrice`, `PositionLine?`, `FillMarkers`, `TriggerMarkers`, `Candles` (snap binary-search) |
| `DrawingRenderer` | `.Drawings`: `DrawDrawings`, `DrawBuildingPolyline`, `DrawGutterPriceTag`, `DrawVLineTimeTag`, `DrawEndpointPriceTag`, `DrawTrendLabels`, `DrawHandle`, `DrawFreehandPath` (stays here as private static — it draws on canvas, not pure math), + `LabelPillBg` | `Drawings`, `draggingId`, `selectedId`, `selectedIds`, `BuildingPolyline`, `BuildingPolylineCursor`, `bool buildingIsFreehand`, `DrawStyle buildingStyle`. Scale seam via `frame.ScaleY(...)`; `DrawTrendLabels`'s direct `_lastPlot` reads become `f.Plot` (identical values — Draw commits the frame first — the temporal smell goes away) |
| `CrosshairRenderer` | `.Crosshair`: `DrawCrosshair` | `CrosshairState`, `Candles` (snap-to-candle); uses `f.VolRect`, `f.PixelToPrice/PixelToTime` |
| `MeasureRenderer` | `.Measure`: `DrawMeasure`, `DrawZoomBox` | `MeasureState measure`, `MeasureState zoomBox`, `Candles` (currency fallback — or drop the param and use `f.Currency`, which is byte-equivalent since both derive from `Candles[0]`; prefer `f.Currency`) |
| `ChartHitTester` | `.HitTest`: `HitDrawing`, `HitOpenOrderLine`, `HitCandleIndex`, `IsInChartArea`, `IsInYAxisGutter` (the inverse/forward transforms `PixelToPrice`, `PixelToTime`, `PriceToPixelY`, `TimeToPixelX` move ONTO `RenderFrame`, §3) | per call: `frame` + the relevant list (`Drawings` / `OpenOrderLines` + drag state / `Candles`). No theme. |

**Stays on the drawable** (the spine, final state ≈ 400 lines, one file — the 8 partial files are
deleted as they empty):

- The entire Properties region (frozen public input surface) incl. the dead `MarkerColor`.
- `Draw()` — the single orchestrator: background fill → pane split → time window → Y-fit
  (`SmoothAutoFit`/`SnapRange` + `_autoFit*` state + autofit consts) → build frame + theme →
  dispatch renderers in the exact current order → plot border (the border draw between sub-pane
  volume and mood stays inline in `Draw()`, it is orchestration) .
- `ComputePlotRect` (public), `DrawNoData` (trivial, spine-local).
- `_frame` + the public forwards (`PlotRect`, `VolumeRect`, `MoodRect`, `LastYMin`, `LastYMax`,
  `PixelToPrice`, `PixelToTime`, `HitDrawing`, `HitOpenOrderLine`, `HitCandleIndex`,
  `IsInChartArea`, `IsInYAxisGutter` — the last five delegating to `_hitTester` with `_frame` +
  the live input lists).
- `_scale` (passed into each frame at construction).
- `PriceToFrac`/`FracToPrice` leave the drawable (into `ChartGeometry` with a mode parameter);
  the drawable itself no longer needs them once `Draw()`'s Y closure and `SnapRange`'s callers go
  through frame/geometry.

---

## 5. The field→region usage matrix — measure before cutting

Before extracting anything, run a cheap static pass to confirm the seams: for every settable
property / public field / private field of the drawable, which partial files reference it. This
catches any coupling this plan missed and finalizes each renderer's parameter list.

```powershell
$members = @('Candles','Style','ScaleMode','Viewport','YAutoFit','ManualYMin','ManualYMax',
  'Crosshair','Measure','ZoomBox','MaSeries','Drawings','DraggingDrawingId','SelectedDrawingId',
  'SelectedDrawingIds','BuildingPolyline','BuildingPolylineCursor','BuildingIsFreehand',
  'BuildingStyle','DrawingColor','LabelPillBg','DraggingOrderId','DraggingOrderPrice',
  'OpenOrderLines','Position','CurrentPrice','SessionOpenPrice','Bg','Axis','Grid','Bull','Bear',
  'PriceLineUp','PriceLineDown','CrosshairColor','MarkerColor','OpenOrderBuyColor',
  'OpenOrderSellColor','OpenOrderStopColor','PositionLineColor','FillMarkers','FillBuyColor',
  'FillSellColor','TriggerMarkers','TriggerColor','ShowVolume','OverlayVolume','VolumeBullTint',
  'VolumeBearTint','ShowMoodPane','MoodSeries','MoodLineColor','ShowDepth','DepthLevels',
  'AxisFont','PriceTagFont','_scale','_lastPlot','_lastVolRect','_lastMoodRect','_lastYMin',
  '_lastYMax','_lastTMin','_lastTMax','_autoFitInit','_autoFitLo','_autoFitHi',
  '_autoFitContractFrames','RightAxisW','BottomAxisH','TopPad','LeftPad')
$files = Get-ChildItem KieshStockExchange\Services\MarketDataServices\Helpers\CandleChartDrawable*.cs
foreach ($m in $members) {
  $hits = $files | Where-Object { Select-String -Path $_.FullName -Pattern "\b$m\b" -Quiet } |
          ForEach-Object { $_.Name -replace 'CandleChartDrawable\.?|\.cs','' }
  "{0,-24} {1}" -f $m, (($hits | Where-Object { $_ }) -join ', ')
}
```

Seed expectation from reading the code (the pass must confirm; any surprise = re-plan that seam
before cutting):

| Region | Fields it touches beyond its own inputs |
|---|---|
| Axes | `ScaleMode`, `Candles`, `Axis`, `Grid`, `AxisFont` + layout consts — **clean** |
| Candles | `Candles`, `Style`, `Bull/Bear/Bg`, `CurrentPrice`, `SessionOpenPrice`, `PriceLineUp/Down`, `PriceTagFont` + `TimeHelper.NowUtc` — **clean** |
| Indicators | `MaSeries`, `Viewport`(Bucket), `Candles`, `OverlayVolume`, `VolumeBull/BearTint`, `Bull/Bear/Grid/Axis`, `AxisFont`, `MoodSeries`, `MoodLineColor`, `DepthLevels` — **clean** |
| Overlays | order/position/fill/trigger inputs + their colors, `Candles` (snap), `Bg` (outline), `PriceTagFont` — **clean** |
| Drawings | drawing inputs + `DrawingColor`, `LabelPillBg`, **`_scale` + `_lastYMin/_lastYMax` + `ScaleMode`** (the scale seam → `frame.ScaleY`), **`_lastPlot`** (TrendLabels → `f.Plot`), `Viewport`(Bucket), `Bull/Bear`, `PriceTagFont` — clean AFTER RenderFrame exists |
| HitTest | **all seven `_last*`**, `Candles`, `Drawings`, `OpenOrderLines`, drag state, `ScaleMode` (via frac helpers) — clean AFTER RenderFrame exists |
| Crosshair | `Crosshair`, `Candles`, **`_lastVolRect`** (→ `f.VolRect`), `CrosshairColor`, fonts — clean after frame |
| Measure | `Measure`, `ZoomBox`, `Viewport`(Bucket), `Candles`(currency), `Bull/Bear`, `CrosshairColor`, `PriceTagFont` — **clean, smallest** |
| Spine | `_autoFit*` + everything (orchestrator) |

Read of the matrix: **Measure and Crosshair are the most self-contained renderers** (tiny, no
scale seam); **HitTest and Drawings become clean the moment `RenderFrame` exists** — they are the
two the council flagged as early candidates *because* they are the two that force the frame to be
designed right (hit-testing = the frame's second consumer by construction; Drawings = the only
user of the `IScaleTransform` seam). The order in §7 follows exactly that: frame first, then
hit-tester (proves the frame), then two tiny renderers (prove the renderer+theme pattern cheaply),
then Drawings (the biggest, with the pattern proven), then the remaining four.

---

## 6. Frozen public surface

Identical rule to Arc 1 §3 (the enumeration there remains the authority): the split must keep
`CandleChartDrawable`'s public API **byte-for-byte identical** — every public settable property,
public field, public method (`Draw`, `ComputePlotRect`, `HitDrawing`, `HitOpenOrderLine`,
`PixelToPrice`, `PixelToTime`, `HitCandleIndex`, `IsInChartArea`, `IsInYAxisGutter`), and public
read-only property (`PlotRect`, `VolumeRect`, `MoodRect`, `LastYMin`, `LastYMax`). The only
consumers are `ChartView.xaml.cs` / `ChartView.Windows.cs` / `ChartView.Drawing.cs` and
`ChartSnapshotRenderer` — **none of these files is touched in this arc** (a correct step's
`git status` shows changes only under `Helpers/`, `Helpers/Drawing/`, `KieshStockExchange/Helpers/
ChartGeometry.cs`, and the harness). All collaborators, `RenderFrame`, `ChartTheme`,
`ChartGeometry`/`ChartLayout` are `internal` — implementation detail, invisible to callers.
`MarkerColor` stays. The namespace mismatch stays. `VolumeMode`/`DrawingHitPart.Close` stay.

---

## 7. Ordered execution steps

Branch: `feature/bot-market-realism-v2`. **One collaborator per commit.** Per-step gate, no
exceptions:

1. `dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`
   — 0 CS errors (exe-copy file-lock while the client runs is the known benign failure);
2. `dotnet test` — all green (619 at last count; they don't compile the drawable, so they guard
   the linked collaterals + everything else, not rendering);
3. `dotnet run --project KieshStockExchange.RenderHarness -- verify` — **all scenes + probe dumps
   pass**;
4. commit.

| Step | Commit content | Extra gate notes |
|---|---|---|
| **0** | Harness project + scenes (§2). Capture baseline goldens on unmodified `2d4491f` drawable code. | Gate for step 0 itself: `capture` then immediate `verify` passes (self-consistency); run `verify` twice to prove run-to-run byte-stability before anything else happens. Commit = harness only. |
| **1** | **`RenderFrame` reification.** Add `RenderFrame.cs`; spine swaps the seven `_last*` fields for `_frame`; the two early-return `WithRects` commits (§3.1 trap); `Draw()` builds the frame and passes `frame.MapX`/`frame.MapY` as the existing `Func<>` args; `.HitTest.cs` members rewritten onto `_frame` (still partial-class members this step); public forwards wired. `PriceToFrac`/`FracToPrice` get the mode parameter (private statics for now, or jump straight to ChartGeometry if diff stays legible — executor's call, prefer smaller). | The float-math step — §8 warnings apply in full. Probe dumps are the sharp gate here. |
| **2** | **`ChartGeometry` + `ChartLayout`** statics; call-site renames across all partials; harness csproj gets the one `Compile Include`. *(Optional, recommended: also link `ChartGeometry.cs` into `KieshStockExchange.Tests` and add real unit tests for the pure math — it is Maui.Graphics+BCL only, exactly the test project's link-set pattern.)* | Mechanical rename diff; goldens must be bit-identical. |
| **3** | **`ChartHitTester`** extracted; `.HitTest.cs` partial file **deleted**; drawable's five public hit methods forward `(_frame, inputs)`. | Probe dumps identical = the concern's proof. Council early-candidate #1 done. |
| **4** | **`MeasureRenderer`** + introduce **`ChartTheme`** (full member list from the confirmed §5 matrix, defined once). `.Measure.cs` deleted. | Smallest renderer proves the `(canvas, frame, theme, inputs)` pattern end-to-end. |
| **5** | **`CrosshairRenderer`**; `.Crosshair.cs` deleted. | |
| **6** | **`DrawingRenderer`**; `.Drawings.cs` deleted. Scale seam → `frame.ScaleY`; `_lastPlot` reads → `f.Plot`; `LabelPillBg` moves in. | The biggest one; S09/S10 scenes are the gate. Council early-candidate #2 done with the pattern proven. |
| **7** | **`AxisRenderer`**; `.Axes.cs` deleted (frac helpers already gone to ChartGeometry in step 2). | |
| **8** | **`CandleRenderer`**; `.Candles.cs` deleted. | S01–S08 style/scale scenes gate it. |
| **9** | **`IndicatorRenderer`**; `.Indicators.cs` deleted. | |
| **10** | **`OverlayRenderer`**; `.Overlays.cs` deleted (`OutlineForBackground` → `ChartTheme.Outline`, consumed here + DrawingRenderer's handles — retrofit DrawingRenderer's call in this commit). | |
| **11** | **Spine sweep**: `Draw()` reads as pure orchestration; the last `Func<>` plumbing gone; dead usings pruned; optionally regenerate `Resources/Raw/FileStructure.txt`. **Final gate adds the human eyeball**: launch the client and walk the full Arc-1 §7-step-7 checklist (all 6 styles, volume modes, mood, depth, MAs, log+percent, order-line drag, position/fill/trigger, every draw tool + building previews + freehand heads, selection/multi-select, crosshair snap, measure, zoom box, Y-gutter wheel zoom, snapshot-to-PNG button). | End state: `CandleChartDrawable.cs` (spine only) + 9 collaborator/support files under `Helpers/Drawing/` + `Helpers/ChartGeometry.cs`. |

Rollback rule: a step whose `verify` fails is fixed or reverted **that step** — never "carry a
known diff forward to fix later"; the golden chain's whole value is that each link is short.

---

## 8. Risks / do-nots

1. **No parent back-reference, ever.** If a renderer "just needs one more thing", that thing
   becomes a parameter or a `ChartTheme`/`RenderFrame` member — it does not become
   `CandleChartDrawable` in a constructor. A back-ref is the failure mode that makes this arc
   pointless.
2. **No new interfaces / no DI registration.** These are `new()`-ed private implementation
   details of one drawable. `IScaleTransform` already exists and stays as-is.
3. **`Draw()` remains the single orchestrator.** Renderers never call each other and never call
   back; the paint order is legible in one method. (`DrawBuildingPolyline` called from inside
   `DrawDrawings` is fine — both live inside `DrawingRenderer`.)
4. **Float-math preservation — the sharpest edge in this arc.** Three distinct transform forms
   exist today and MUST stay distinct:
   - render Y (closure): `plot.Bottom - (float)(frac * plot.Height)` — cast **then** subtract in
     `float`;
   - hit-test `PriceToPixelY`: `(float)(plot.Bottom - frac * plot.Height)` — subtract in
     `double`, cast **last** (differs in low bits; `IScaleTransform`'s header comment documents
     this asymmetry deliberately);
   - `HitOpenOrderLine`'s inline inverse is **linear-only** (ignores log mode — a live
     inconsistency under `Logarithmic`). Keep it **verbatim** inside `ChartHitTester`; routing it
     through `MapY`/`PriceToFrac` would *fix a bug*, i.e., change behaviour, i.e., out of scope
     (note it for the polish arc).
   Same discipline for `MapX` (`SpanSec` precomputed once, same expression order) and for keeping
   `ChartGeometry.PriceToFrac` textually identical to today's body. Goldens + probe dumps catch
   violations, which is exactly why they exist.
5. **The early-return `WithRects` commits** (§3.1) — the one behavioural subtlety in frame
   construction; forgetting them changes post-bailout hit-testing.
6. **Observed smells that are deliberately NOT fixed in this arc** (each would break the "only
   intended deltas" review): `MarkerColor` dead field; namespace≠folder;
   `HitOpenOrderLine` linear inverse; `DrawingHitPart.Close` unreachable; `VolumeMode` unused by
   the drawable; frac/DashPattern mirror copies in `RegularScaleTransform`/`StylePreviewDrawable`.
   All are polish-arc items.
7. **Harness discipline**: goldens are same-machine artifacts (fonts, timezone) — never commit
   them, never compare across machines, re-baseline consciously from a verified-green commit if
   the harness or environment must change; pin `Microsoft.Maui.Graphics(.Skia)` to the same
   explicit version pair; the probe dumps tolerate zero drift even if the PNG tolerance fallback
   is ever engaged.
8. **Don't grow the blast radius**: no ChartView edits, no test-csproj link-set edits (except the
   optional ChartGeometry addition in step 2), no `/Tools`, no behavioural "while I'm here"
   improvements. Every commit's diff must be explainable line-by-line against this plan.

---

## Appendix: facts grounding this plan (verified 2026-07-17)

1. `TimeHelper.NowUtc` is a settable `Func<DateTime>` (Shared) — the determinism hook for
   `DrawCurrentPriceLine`.
2. `KieshStockExchange.Tests.csproj` proves the standalone `Microsoft.Maui.Graphics` 9.0.90
   link-compile pattern on plain `net9.0`; its comments deliberately exclude Skia +
   ChartSnapshotRenderer — hence the separate harness project.
3. The `...MarketDataServices.Interfaces` using resolves via `KieshStockExchange.Shared`
   (ICandleService/IMarketDataService/... live there), so the replicated using block compiles in
   a harness that links no client interface file.
4. The drawable's closure needs exactly four client-side headless files beyond its own 9:
   `ChartTypes.cs`, `StylePreviewDrawable.cs`, `Drawing/IScaleTransform.cs`,
   `Models/ChartDrawing/**` — all previously proven MAUI-workload-free.
5. Two `DepthLevel` types exist (client `ChartTypes.cs` record vs Shared `OrderBookSnapshot.cs`
   record) — the drawable uses the **client** one; the harness link set must include
   `ChartTypes.cs` or the Shared one would silently shadow with a different shape (it wouldn't
   compile — different members — but know why).
6. `Draw()` commits pane rects (spine 231–233) BEFORE its two early returns, but ranges only at
   319–323 — the origin of the `WithRects` requirement.
7. `DrawTrendLabels` reads `_lastPlot` instead of taking `plot` — identical values today because
   `Draw()` writes the cache first; becomes `f.Plot` (Arc-1 §4 flagged it; the frame makes the
   ordering explicit).
8. `ChartSnapshotRenderer.Render` fills an opaque background and scales the canvas before calling
   `drawable.Draw(canvas, new RectF(0, 0, w, h))` — the harness needs no canvas plumbing of its
   own.
