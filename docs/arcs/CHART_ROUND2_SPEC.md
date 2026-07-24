# CHART round-2 — CONSOLIDATED SPEC (Step 0 doc-mine + gap analysis)

Companion output of round-2 Step 0 (per `docs/arcs/CHART_ROUND2_HANDOFF.md`). For each of the 5 backlog
items: **Intended design** (doc + line) → **Current v1 impl** (file:line) → **Gap to close** (file-level) →
**Test** (visual verify in the running client). Implement per this spec, one item per commit.

## Build / test note
- **Client csproj (ABSOLUTE):** `C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange-chart\KieshStockExchange\KieshStockExchange.csproj`
  built with `-f net9.0-windows10.0.19041.0`. Client-only work — **no server / money / CK**.
- **Run the client:** `dotnet run --project KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`, open a stock's chart.
- **Append-only rule:** `DrawTool` enum, `DrawStyle`, `DrawingObject` fields are **append-only** — never
  reorder/insert existing members and add new fields only as trailing defaults (old persisted JSON must still
  deserialize). `DrawingObject.Kind` persists by value; `DrawStyle`/`DrawingObject` legacy trailing defaults
  are re-applied by `DrawingBackCompat.ApplyLegacyTrailingDefaults` (System.Text.Json ignores optional-ctor defaults).
- **Styling:** shared XAML styles in `Resources/Styles` (e.g. `PenToolTile`, `ToolGroupStrip`, `ToolFlyoutTile`,
  `ToolFlyoutLabel`, `ChartOhlcKey/Value`, `ChartLiveButton.Off`, `ChartIconButton`) over inline values. MVVM: bindings/commands over code-behind.
- **Disk-gate every build** (pre-flight %Disk Time < 70%, Idle priority, `-maxcpucount:1`, parse logs). The app may
  be running → the exe-copy step can fail with a benign file lock; grep the log for `error CS`, not the copy step.

## The four dispatch sites (any new/aligned Kind must arm ALL of them)
Per `docs/ultradesigns/ultradesign-chart-renderwiring.md` lines 11-22 — there are four parallel `switch/if-else on
d.Kind` sites, all ending in a `Trend/Ray` fallthrough. A Kind wired at only one draws-but-is-dead. The four:
1. **Render** — `Services/MarketDataServices/Helpers/Drawing/DrawingRenderer.cs` `DrawDrawings` (if/else on `d.Kind`, lines 60-417).
2. **Hit-test** — `Services/MarketDataServices/Helpers/Drawing/ChartHitTester.cs` `HitDrawing` (lines 46-194).
3. **Placement** — `Views/TradePageViews/Chart/ChartView.Windows.cs` Priority 0.6 (lines 219-290) + commit hook (lines 633-663).
4. **Body-drag** — `Views/TradePageViews/Chart/ChartView.Drawing.cs` `DragDrawing` (lines 72-112).
Plus the **panel preset** table `Models/ChartDrawing/Tools/DrawToolPreset.cs` and the **rail/flyout** XAML.

---

## 1. Fib label-box fix

**Intended design (per docs).** `CHART_ROUND2_HANDOFF.md` lines 20-24: the Fib level tags are variable-width
(price digit-count differs) so they don't align → ugly. Make every tag a **uniform/universal width**, ratio
**left**-aligned + price **right**-aligned so the pills line up in a clean column. Small standalone commit.
(`CHART_DRAWING_OVERHAUL_PLAN.md` describes the Fib grid at lines 45/marketing but not the tag alignment — the
handoff is the authority here.)

**Current v1 impl.** `DrawingRenderer.cs` `DrawFibLevelTag` **lines 534-551**. It builds ONE combined string
`text = $"{ratio:0.###}  {CurrencyHelper.Format(price, cur)}"` (line 537), sizes the pill to the string length
`float w = Math.Max(64f, text.Length * 6.3f)` (line 538 = the variable width, the bug), and draws it with a
single left-aligned `canvas.DrawString(text, …, HorizontalAlignment.Left, …)` (lines 549-550). Call site is the
Fib arm loop, `DrawFibLevelTag(canvas, t, plot, left, y, lvl.Ratio, lvl.Price, color, cur)` at **line 348**.

**Gap to close (file-level — `DrawingRenderer.cs` only).**
- Add a fixed-width const near the other tag consts (line 37-39 block), e.g. `private const float FibTagW = 96f;`.
- In `DrawFibLevelTag`: drop the combined string; compute the pill rect with `w = FibTagW` (remove the
  `text.Length * 6.3f` sizing at line 538) so `lx`/`r` use the constant width.
- Replace the single `DrawString` (549-550) with **two** `DrawString` calls sharing rect `r`:
  - ratio: `canvas.DrawString($"{ratio:0.###}", new RectF(r.X + 3, r.Y, r.Width - 6, r.Height), HorizontalAlignment.Left, VerticalAlignment.Center)`.
  - price: `canvas.DrawString(CurrencyHelper.Format(price, cur), new RectF(r.X + 3, r.Y, r.Width - 6, r.Height), HorizontalAlignment.Right, VerticalAlignment.Center)`.
- Keep the pill mechanics unchanged (fill `LabelPillBg`, colour border, `StrokeDashPattern = null`, font `theme.PriceTagFont`).
- Hit-test unaffected (the Fib hit arm tests the level LINES, not the tag — `ChartHitTester.cs` lines 145-161).

**Test.** Pick Fib → drag a swing (low→high). Every level tag is the same width and lines up in a clean left
column; the ratio hugs the left edge, the price digits are flush-right (so 1-digit and 4-digit prices still align).

---

## 2. Text rework (plain text + font-size dropdown)

**Intended design (per docs).** `CHART_TOOL_PANELS_DESIGN.md` line 48 (Text row = **Color ✔ · Size ✔ · content ✔**,
**no Width/Dash/Ending/Head**) and line 4 (append-only). `CHART_ROUND2_HANDOFF.md` lines 26-31: render **plain
text ONLY** (no pill/box), drawn in the **pen colour** (= the text colour, not a border). Pen panel for Text:
**remove Width**, **add Size** = a **dropdown of standard font sizes (8,9,10,11,12,14,16,18,20,24,28,36,48,72)
+ ▲/▼ steppers** that jump to the next/previous standard size. The doc's older "S/M/L 3-tile" (`CHART_TOOL_PANELS_DESIGN.md`
lines 48/67, `SizeKind`) is **superseded** by this dropdown+stepper.

**Current v1 impl.**
- **Render (the pill):** `DrawingRenderer.cs` Text arm **lines 309-331** — fills `LabelPillBg` rect (321-322),
  strokes a colour border (323-325), draws WHITE text (326-329) at font `t.PriceTagFont + 1f`. Pill metrics
  consts `TextPillCharW/MinW/H` at **lines 37-39**.
- **Hit-test:** `ChartHitTester.cs` Text arm **lines 64-75** + mirror consts `TextPillCharW/MinW/H` **lines 25-27**
  (comment at 22-24 says the two const sets MUST stay in lockstep).
- **Panel preset:** `DrawToolPreset.cs` Text row **lines 60-64** — `ShowStroke:true, ShowDash:false, ShowText:true,
  ShowSize:true`. NOTE: **`ShowSize` is a gate with no XAML** (no SIZE section exists in `ChartPenPanelView.xaml`),
  and the WIDTH tiles are gated on `ShowStroke` (`ChartPenPanelView.xaml` lines 83/95/107) — so for Text, which is
  `ShowStroke:true` (needed for the Colour swatch, panel lines 40/43), the **Width row currently shows** = exactly
  what must be removed. Colour and Width share the one `ShowStroke` gate today → they must be split.
- **Placement / edit:** `ChartView.Windows.cs` lines 256-263 (one-click anchor + `PromptTextLabelAsync`);
  `ChartView.Drawing.cs` `PromptTextLabelAsync` lines 25-52. Panel re-edit path = `SetSelectedText` (`ChartDrawingViewModel.Pen.cs` line 236).
- **Data model:** `DrawStyle.cs` lines 16-22 (`Size = SizeKind.Medium` exists; NO numeric font size field).

**Gap to close.**
- **Data model — `DrawStyle.cs` (append-only):** add a trailing field `int FontSize = 0` (0 = "use the default
  size", so legacy JSON with no field reads as default). Register it in `DrawingBackCompat.ApplyLegacyTrailingDefaults`
  if a non-zero default is chosen. (Keep `SizeKind Size` — leave it; it's now unused by Text but stays for back-compat.)
- **Render — `DrawingRenderer.cs` Text arm (309-331):** delete the pill (fill 321-322 + border 323-325); set
  `canvas.FontColor = color;` (the pen colour, not white) and `canvas.FontSize = d.Style.FontSize > 0 ? d.Style.FontSize : (t.PriceTagFont + 1f);`
  then `DrawString(d.Text, …, HorizontalAlignment.Left, VerticalAlignment.Center)` anchored at (ax, ay). Keep the
  blank-text + off-plot clips (315-318) and the selection handle (330).
- **Hit-test — `ChartHitTester.cs` Text arm (64-75):** the clickable rect must now track the PLAIN text bounds,
  sized off `FontSize` (not `TextPillCharW`). Add a shared text-measure helper or a `FontSize`-scaled width/height
  so the hit zone matches the drawn glyphs; update the mirror consts note (22-27).
- **Panel gate split — `ChartDrawingViewModel.Pen.cs` + `DrawToolPreset.cs`:** introduce a `ShowWidth` gate
  distinct from `ShowStroke` (colour). Width tiles bind `ShowWidth`; Text = `ShowStroke:true (colour), ShowWidth:false`.
  All other current tools that show width keep `ShowWidth:true`. Wire `ShowWidth` through `EditingPreset` + the
  `RefreshPenTiles` re-notify block (`Pen.cs` lines 152-161 + 323-332) exactly like the other `Show*` bools.
- **VM font-size state — `ChartDrawingViewModel.Pen.cs`:** add a bound `PenFontSize` (int) + the standard-size
  array, `SetFontSize(int)` (via `ApplyPenStyle(s => s with { FontSize = … })` so it edits the selected drawing
  else the default pen), and `StepFontSizeUp`/`StepFontSizeDown` commands that snap to the next/prev standard size.
  Re-sync `PenFontSize` from the effective style inside `RefreshPenTiles`.
- **Panel XAML — `ChartPenPanelView.xaml`:** change the three WIDTH tiles' `IsVisible` from `ShowStroke` to
  `ShowWidth` (lines 83/95/107) so Width hides for Text; add a **SIZE section** (gated `ShowSize`) = a `Picker`/
  dropdown bound to `PenFontSize` over the standard sizes + two ▲/▼ `Button`s bound to `StepFontSizeUp/DownCommand`.
  Use shared styles.

**Test.** Pick Text → click → type a label. It renders as **plain coloured text** (no box/pill), in the pen
colour. Open the panel: **no Width row**; a **Size dropdown** (8…72) + ▲/▼ steppers. Change size → the text
scales; step ▲/▼ → jumps to the next/prev standard size; change colour → the text recolours. Reopen the chart →
persisted (and a Text drawn before this change still loads, now as plain text at the default size).

---

## 3. Comment tool (new `DrawTool.Comment`)

**Intended design (per docs).** `CHART_DRAWING_OVERHAUL_PLAN.md` line 29 (**Text ▸ Plain text · Comment · Price
label**). `CHART_ROUND2_HANDOFF.md` lines 32-34: a **Text + rounded-Rectangle** callout — text inside a rounded
rect with a small **downward "v" tail** at the bottom pointing to its anchor (a callout bubble). Same **text +
size + colour** panel as Text (a Text-group variant). `CHART_TOOL_PANELS_DESIGN.md` line 107 (Text variants →
`ShowTextSection` + size).

**Current v1 impl.** None — no `DrawTool.Comment` exists (`DrawTool.cs` line 16-23 enum ends at
`RotatedRect, Triangle, Arc, FibRetracement`).

**Gap to close (append `DrawTool.Comment` + wire all four dispatch sites + panel + rail).**
- **Enum — `DrawTool.cs`:** append `Comment` to the trailing UP-CORE list (line 22) — e.g.
  `RotatedRect, Triangle, Arc, FibRetracement, Comment,`. Append-only (Kind persists by value).
- **Render — `DrawingRenderer.cs`:** add a `d.Kind == DrawTool.Comment` branch BEFORE the Trend/Ray fallthrough
  (near the Text arm, ~line 331). Draw: a rounded rect (sized to `d.Text` + `FontSize`, reuse the Text sizing) at
  the anchor, a filled callout background + colour border, the text inside in the pen colour, and a small
  downward `v` tail (a 3-point `PathF` from the rect's bottom edge to the anchor point). Selection handle at the anchor.
- **Hit-test — `ChartHitTester.cs`:** add a `Comment` branch mirroring the Text arm (rect at the anchor sized off
  `FontSize`), optionally including the tail triangle; return `DrawingHitPart.Body`.
- **Placement — `ChartView.Windows.cs`:** extend the one-click-text branch (lines 256-263) to also fire for
  `DrawTool.Comment` (`_vm.Drawing.DrawTool is DrawTool.Text or DrawTool.Comment`), placing a `DrawingObject`
  with `Kind = _vm.Drawing.DrawTool` then `PromptTextLabelAsync(id)` (works for both).
- **Body-drag — `ChartView.Drawing.cs` `DragDrawing`:** Comment is single-anchor like Text → it already falls to
  the default body-shift (`ShiftTrend` path); confirm no explicit Position-style special-case is needed. No change expected beyond verifying the fallthrough.
- **Preset — `DrawToolPreset.cs`:** add `DrawTool.Comment` to the **Text row** (lines 60-64) so it gets
  `ShowStroke(colour) + ShowText + ShowSize`, `ShowWidth:false`.
- **Rail — Text group** (see item 5): `ChartDrawingViewModel.Pen.cs` `ToolIcon` (add a `tool_comment.png` case),
  the Text-group predicate, and the Text flyout in `ChartView.xaml` (Text · Comment rows).

**Test.** Pick Comment (Text group flyout) → click → type. A **rounded-rect bubble with a downward v tail**
points at the click anchor; text is inside in the pen colour. Drag the body → moves; select → colour/size panel
(same as Text, no Width). Reopen the chart → persisted.

---

## 4. Position → Long / Short / Manual + panel

**Intended design (per docs).** `CHART_DRAWING_OVERHAUL_PLAN.md` line 26 (**Position ▸ Long · Short · Manual** —
Long/Short = drag entry/target/stop on the chart; **Manual** = type entry/target/stop/qty directly, same panel/
data, no drag) and lines 74-81 (three legs + zones + when-selected panel edits every detail). `CHART_TOOL_PANELS_DESIGN.md`
line 50 (Position row: **opacity-only** fill, zones fixed bull/bear; **Delete-only** footer; no "Set as default"),
line 68-70 (three numeric Entries Entry/Target/Stop → P1/P2/P3, **Risk%** entry — a single global `Preferences`
scalar, not per-drawing — + read-only **R:R** `|Target−Entry|/|Entry−Stop|`), line 107 (Manual opens with the
numeric entries focused instead of starting a drag). `CHART_ROUND2_HANDOFF.md` lines 35-41.

**Current v1 impl (ONE Position tool).**
- **Enum:** single `DrawTool.Position` (`DrawTool.cs` line 19).
- **Render:** `DrawingRenderer.cs` Position arm **lines 357-393** (fixed bull/bear zones at hardcoded alpha
  `0.12f`, lines 372/375; entry line + thin bull/bear borders) + `DrawPositionLabel` **lines 556-586** (ONE pill:
  R:R + live PnL via `ChartMath.PositionPnl`).
- **Hit-test:** `ChartHitTester.cs` Position arm **lines 163-178** (Anchor1 = Entry, Anchor2 = Target; stop P3 has
  no handle; Body = box bounds).
- **Placement / commit:** `ChartView.Windows.cs` **lines 645-652** — on release derives `P3 = P1-(P2-P1)` (mirror
  stop), `Direction = P2 >= P1 ? 1 : -1` (**inferred from drag direction**), `Qty = 1`.
- **Body-drag:** `ChartView.Drawing.cs` `ShiftPosition` **lines 111-112** (shifts all three legs + both time anchors).
- **Preset:** `DrawToolPreset.cs` Position row **lines 67-71** — `ShowStroke:true, ShowPosition:true`. NOTE:
  **`ShowPosition` is a gate with no XAML** (no Position section in `ChartPenPanelView.xaml`), and the footer today
  is the standard `HasSelectedDrawing` footer (Set-as-default + Delete, `ChartPenPanelView.xaml` lines 254-261).
- **VM setters already present** (`ChartDrawingViewModel.Pen.cs`): `SetSelectedEntryPrice/TargetPrice/StopPrice`
  (lines 241-243), `SetSelectedQty` (237), `SetSelectedDirection` (238). Missing: bound props, Risk%, R:R readout.
- **Rail:** Position lives in the **shapes** group (`Pen.cs` `IsShapesGroupActive` line 76 + `PickGroupTool` line
  112; `ChartView.xaml` shapes flyout line 329-332).

**Gap to close.**
- **Long/Short/Manual arming — `DrawTool.cs` (append-only):** add trailing `PositionLong, PositionShort,
  PositionManual` (after `Comment`). These are **arming** tools; on commit the `DrawingObject.Kind` is stored as
  **`DrawTool.Position`** (so render/hit/persist stay on the one Position Kind) with `Direction` set from the
  chosen tool — NOT from drag direction. (Alternative kept for the implementer: keep a single arming
  `DrawTool.Position` + set Direction from drag; but the docs name three tools, so three arming members is the aligned choice.)
- **Placement — `ChartView.Windows.cs`:** in the two-anchor drag branch (280-289) and the commit hook (645-652),
  when the armed tool is `PositionLong`/`PositionShort`, create `Kind = DrawTool.Position` and set
  `Direction = +1 / −1` from the **tool** (drop the `P2 >= P1 ? 1 : -1` inference at line 650). `PositionManual`
  = a **one-click** placement (no drag): drop a default box (entry at click, target/stop at small default offsets),
  set Direction from target-vs-entry default, `FinishPlacement` + open the panel with the numeric entries focused
  (per `CHART_TOOL_PANELS_DESIGN.md` line 107).
- **Panel section — `ChartPenPanelView.xaml`:** add a **Position section** (gated `ShowPosition`): three numeric
  `Entry` controls bound to `PositionEntry`/`PositionTarget`/`PositionStop`, a **Risk%** `Entry` bound to
  `PositionRiskPct`, and a read-only **R:R** `Label` bound to `PositionRiskReward`. Change the footer so Position
  is **Delete-only** — gate the "Set as default ✓" button (lines 254-257) off when `ShowPosition` (a default
  entry/target price is incoherent per the doc).
- **VM — `ChartDrawingViewModel.Pen.cs`:** add bound props `PositionEntry/Target/Stop` (mutate P1/P2/P3 via the
  existing `SetSelected*Price` setters), `PositionRiskPct` (a single global `Preferences` scalar, per doc — NOT
  per-drawing), read-only `PositionRiskReward` (`|P2−P1|/|P1−P3|`), and the Text/Position-group predicates + `ToolIcon`.
- **Opacity-only fill (doc):** the doc says Position fill = **opacity only, zones fixed bull/bear**. v1 hardcodes
  `0.12f` (lines 372/375). Optionally wire `d.Style.FillOpacity` into those two alphas + flip the preset to
  `ShowOpacity:true, ShowFillColor:false` (lines 67-71). See Surprises §(c) for the mild tension with the entry-line stroke colour.
- **Rail — Position group** (item 5): move Position OUT of the shapes predicate; give it its own group with the
  Long/Short/Manual flyout (`ChartView.xaml`), `ToolIcon` cases, and `PickGroupTool` routing.

**Test.** Open the Position group flyout → **Long · Short · Manual**. Long → drag entry→target = green-up / red-
down box (Direction fixed = long regardless of drag direction). Short → mirrored. Manual → single click drops a
box you then edit. Select any → panel shows numeric **Entry / Target / Stop** + **Risk%** + read-only **R:R**;
footer is **Delete only** (no "Set as default"). Edit a leg number → the box repositions; reopen chart → persisted.

---

## 5. Rail regrouping + Alert → top-row

**Intended design (per docs).** `CHART_DRAWING_OVERHAUL_PLAN.md` lines 15-50 (grouped rail) + line 42 (**Alert is
NOT on the rail — top-row button next to the moving-average button**). `CHART_ROUND2_HANDOFF.md` lines 42-53
(Kiesh CONFIRMED "regroup existing tools" + "Alert → top-row"). Aligned groups (leave the unbuilt Circle/
RotatedRect/Triangle/Arc, Brush/Highlighter, Price-label, Magnet, Cursor-as-group for later):
- **Lines** ▸ Trend · Ray · ExtendedLine · HLine · HRay · VLine · Polyline  (**+ Fib for now**)
- **Shapes** ▸ Rectangle · Ellipse
- **Drawing** ▸ Freehand(Brush) · Arrow
- **Position** ▸ Position (→ Long/Short/Manual per item 4)
- **Text** ▸ Text · Comment
- **Measure** (single) · **Magnifier** (single)
- **Alert** — OFF the rail → a top-row button next to the MA button.

**Current v1 impl (only lines + shapes groups; Alert/Text/Fib wrongly in lines, Position in shapes).**
- **Predicates — `ChartDrawingViewModel.Pen.cs`:** `LinesGroupContains` **lines 80-82** wrongly includes
  `Alert, Text, FibRetracement`. `IsShapesGroupActive` **line 76** = `Rectangle or Ellipse or Arrow or Position`.
  `PickGroupTool` **lines 108-115** routes only lines/shapes. Group state = `LinesGroupTool`/`ShapesGroupTool`/
  `OpenToolGroup` (**lines 61-63**), `IsLines/ShapesGroupOpen` (77-78), `IsLines/ShapesGroupActive` (75-76),
  `ToolIcon` table (84-103).
- **Rail XAML — `ChartToolRailView.xaml`:** Cursor (21-31), **Lines group** (35-47), **Shapes group** (50-62),
  divider (64), **Measure** single (66-77), **Freehand** standalone (80-90), **Magnifier** single (92-102),
  zoom-out (104-106), undo/redo/eye (109-122).
- **Flyout XAML — `ChartView.xaml`:** **lines flyout** (lines 198-293) contains Trend/Ray/Extended/HLine/HRay/
  VLine/Polyline/**Alert**(268-271)/**Text**(277-280)/**Fib**(286-289); **shapes flyout** (295-333) contains
  Rectangle/Ellipse/**Arrow**(320-323)/**Position**(329-332).
- **Toolbar XAML — `ChartToolbarView.xaml`:** MA button at **Grid.Column 3, lines 170-180** (binds
  `ChartViewModel`, `ToggleMaSettingsCommand`). The toolbar's outer `Grid` has fixed
  `ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto"` (line 13). Alert arming lives on the **Drawing**
  VM (`Drawing.SelectDrawToolCommand`), reachable from the toolbar the way the removed pen selector was.

**Gap to close.**
- **`ChartDrawingViewModel.Pen.cs`:**
  - `LinesGroupContains` (80-82): **remove `Alert` and `Text`** (keep `FibRetracement` for now). Result = Trend/
    Ray/ExtendedLine/HLine/HRay/VLine/Polyline/FibRetracement.
  - `IsShapesGroupActive` (76): reduce to `Rectangle or Ellipse` (remove Arrow + Position).
  - Add **three new groups**: `Drawing` (Freehand · Arrow), `Position` (the PositionLong/Short/Manual arming tools
    + Kind Position), `Text` (Text · Comment). For each: an `[ObservableProperty] *GroupTool`, `*GroupIcon`,
    `Is*GroupActive`, `Is*GroupOpen` (keyed off `OpenToolGroup == "drawing"|"position"|"text"`), the
    `On*GroupToolChanged`/`OnOpenToolGroupChanged` notifications (mirror 65-71), and `PickGroupTool` routing (108-115).
  - `ToolIcon` (84-103): add `Comment` (+ `tool_comment.png`) and the Position arming tools (reuse `tool_position.png`).
- **`ChartToolRailView.xaml`:** add a **Drawing group** tile + `›` strip, a **Position group** tile + strip, a
  **Text group** tile + strip (mirror the Lines/Shapes `Grid` at 35-47). **Move Freehand** out of its standalone
  button (80-90) into the Drawing group. Keep **Measure** (66-77) and **Magnifier** (92-102) as singles.
- **`ChartView.xaml` flyouts:** from the **lines flyout** remove the Alert (268-271) and Text (277-280) rows (keep
  Fib). Reduce the **shapes flyout** to Rectangle + Ellipse (remove Arrow 320-323 + Position 329-332). Add three
  new flyout `Border`s (mirror 198-293): **Drawing** (Freehand · Arrow), **Position** (Long · Short · Manual →
  `PositionLong/Short/Manual`), **Text** (Text · Comment), each gated on the matching `Is*GroupOpen`.
- **`ChartToolbarView.xaml`:** add an **Alert top-row button next to MA** (after Grid.Column 3) — insert a column
  into the `ColumnDefinitions` (line 13) and shift the subsequent columns, or nest MA+Alert in a small
  `HorizontalStackLayout`. It must arm the Alert TOOL: bind `Command="{Binding Drawing.SelectDrawToolCommand}"
  CommandParameter="{x:Static tools:DrawTool.Alert}"` (add the `tools:` xmlns) with an active-highlight
  `DataTrigger` on `Drawing.DrawTool == Alert`. Alert stays a placeable drawing (unchanged render/hit/placement);
  only its ENTRY point moves from the rail flyout to the toolbar.

**Test.** The rail shows group tiles **Lines · Shapes · Drawing · Position · Text** plus **Measure** and
**Magnifier** singles. Lines flyout = Trend/Ray/Extended/HLine/HRay/VLine/Polyline/Fib (no Alert, no Text).
Shapes flyout = Rectangle/Ellipse only. Drawing flyout = Freehand/Arrow. Text flyout = Text/Comment. A new
**Alert** button sits next to **MA** in the top toolbar and arms the alert tool (click → then click a price →
dashed bell line). Arming any group tool still closes an open flyout and highlights the active tool.

---

## Suggested implementation order (each = its own commit)
1. **Fib fix** — smallest, self-contained (`DrawingRenderer.cs` only). Ship first.
2. **Rail regroup + Alert → top-row** — the foundation the later group flyouts (Text·Comment, Position·Long/Short/
   Manual) build on. `Pen.cs` predicates + `ChartToolRailView.xaml` + `ChartView.xaml` flyouts + `ChartToolbarView.xaml`.
   (Add the Text + Position + Drawing group scaffolding here; the group members Comment / Long/Short/Manual land in
   their own commits below but the group tiles + empty-ish flyouts exist now.)
3. **Text rework** — `DrawStyle.FontSize` + plain-text render + `ShowWidth` gate split + Size dropdown/steppers.
4. **Comment** — new `DrawTool.Comment`, wired at all four dispatch sites + Text-group flyout (reuses the Text panel).
5. **Position → Long/Short/Manual + panel** — arming tools, panel section (numeric legs + Risk% + R:R), Delete-only
   footer, opacity-only fill. The biggest rework (touches a committed feature) — review carefully; Kiesh visual-tests.

Reworks of committed features (Text, Position) are NOT purely additive → adversarial diff review + Kiesh eyeball each.

---

## Surprises / ambiguities flagged for the implementer
- **(a) `ShowSize` / `ShowPosition` are dead gates today.** Both booleans exist in `DrawToolPreset.cs` and are
  re-notified in `RefreshPenTiles`, but **neither has any XAML** in `ChartPenPanelView.xaml` — there is no SIZE
  section and no POSITION section. So "add the control" (items 2 + 4) is genuinely NEW panel XAML, not a tweak of
  an existing row. The v1 Text/Position round-1 features shipped their gates ahead of the UI.
- **(b) Colour + Width share one `ShowStroke` gate.** Text needs colour-yes / width-no, but both the header Colour
  swatch (`ChartPenPanelView.xaml` 40/43) and the Width tiles (83/95/107) bind `ShowStroke`. Item 2 therefore
  requires splitting a new `ShowWidth` gate — a small cross-cutting change that also touches every other tool's
  preset row (all set `ShowWidth:true` where they currently rely on `ShowStroke`).
- **(c) Doc vs v1 tension on the Position panel/stroke.** `CHART_TOOL_PANELS_DESIGN.md` line 50 lists Position as
  Color "—" / opacity-only, but v1 draws the **entry line in the drawing's stroke colour** (`DrawingRenderer.cs`
  line 379) and the preset is `ShowStroke:true` (line 69). Honouring "opacity-only, no colour" would drop the
  entry-line colour control. Recommend keeping a minimal stroke colour for the entry line (matches what ships and
  reads better) and treating "opacity-only" as "no per-zone fill colour" (zones stay fixed bull/bear) — flag for Kiesh.
- **(d) Direction: doc "Long/Short tools" vs v1 "drag-inferred".** v1 sets `Direction = P2 >= P1 ? 1 : -1` at
  commit (`ChartView.Windows.cs` line 650). The docs want Long/Short to be the CHOSEN tool (Direction fixed by the
  tool, independent of drag direction) — a deliberate behaviour change of a committed feature, not a bug fix. The
  cleanest append-only route is three new arming `DrawTool` members that all persist `Kind = DrawTool.Position`
  (so render/hit/persistence stay on the one Kind) — but that means the placement code (which today does
  `new DrawingObject(id, _vm.Drawing.DrawTool, …)`) must **map the arming tool → `Kind = Position`** instead of
  storing the arming tool as the Kind. Confirm this mapping is acceptable vs. adding three real Position Kinds.
- **(e) `SizeKind` (Small/Medium/Large) is superseded but stays.** Item 2 adds a numeric `FontSize`; the existing
  `SizeKind Size` field (`DrawStyle.cs` line 19, `StyleEnums.cs`) is left in place for back-compat but goes unused
  by Text. Don't remove it (append-only) — just stop reading it for Text sizing.
- **(f) Handoff supersedes the ultradesign on Text.** `ultradesign-chart-renderwiring.md` line 42 specced Text as
  a "pill-rendered" v1 — that is exactly what item 2 now reworks away. Where the ultradesign and the round-2
  handoff disagree, the **handoff wins** (it's the newer owner steer).
- **(g) Rail flyouts are hosted in `ChartView.xaml`, not the rail view.** `ChartToolRailView.xaml` holds only the
  group TILES + `›` strips; the actual flyout option lists live as `Grid.Column="1"` overlays in `ChartView.xaml`
  (198-333). New groups (Drawing/Position/Text) need edits in BOTH files.
