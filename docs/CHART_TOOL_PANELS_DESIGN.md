# Chart Drawing Suite — Per-Tool Settings-Panel Spec (Fable-5 council synthesis)

Companion to `CHART_DRAWING_OVERHAUL_PLAN.md`. The council was unanimous: **one panel, kind-gated
sections, no per-tool panels, no framework.** Everything is trailing-default / append-only so existing
persisted drawings load untouched.

## 1. Architecture
**One shell, gated section-stack.** Keep the existing single pen `Border` (`IsVisible={IsPenPanelOpen}`),
a `VerticalStackLayout` of sections. Per-tool = show/hide sections; never a second panel type, never a
floating near-drawing toolbar.

**One new switch — `EditingKind`** (the selected drawing's `Kind`, else the armed `DrawTool`):
```csharp
public DrawTool EditingKind => HasSelectedDrawing
    ? Drawings.First(d => d.Id == SelectedDrawingId).Kind : DrawTool;
```
Re-notify it + the `Show*` bools in the existing hub trio (`OnSelectedDrawingIdChanged`,
`OnDrawToolChanged`, `RefreshPenTiles`) — the exact `CanEditHead` path. No property-descriptor registry.

**Section visibility = plain computed bools** (modeled on `CanEditHead`):
```csharp
ShowStrokeRows   => EditingKind is not (Text or Position);
ShowEndingHead   => EditingKind is Trend or Ray or ExtendedLine or Polyline;
ShowFillSection  => EditingKind is Rectangle or Ellipse or Position;
ShowTextSection  => EditingKind is Text or Arrow or Alert;
ShowSizeTiles    => EditingKind is Text or Arrow;
ShowArrowHead    => EditingKind == Arrow;                 // always enabled (no Ending dependency)
ShowPositionSection => EditingKind == Position && HasSelectedDrawing;
ShowAlertSection    => EditingKind == Alert;
```
**Section order (top→bottom):** TOOL row (default mode) · COLOR (always) · WIDTH·DASH · ENDING·HEAD ·
ARROW-HEAD · FILL · SIZE · TEXT · POSITION · ALERT · footers.

**Rules:** HIDE (don't grey) sections a concept can't apply to (greying already means "enable-able" via
the Ending→head dependency). Active tool shown via the tool-aware `PenPanelHeader` + the existing tool-grid
highlight — no new chrome. Cursor/eye/delete-last = commands → no panel. Measure/Magnifier = transient →
no panel, and arming them must not clear an open flyout. Cap: base rows + ≤3 extra sections + footer.

## 2. Tool → controls
| Tool | Color | Width·Dash | Ending·Head | ArrowHead | Fill+opacity | Size | Text | Special | Footer |
|---|---|---|---|---|---|---|---|---|---|
| Cursor | — | — | — | — | — | — | — | — | *(no own panel)* |
| Trend / Ray / Extended | ✔ | ✔ | ✔ | — | — | — | — | — | std |
| HLine / HRay / VLine | ✔ | ✔ | — | — | — | — | — | — | std |
| Polyline | ✔ | ✔ | ✔ | — | — | — | — | — | std |
| Freehand | ✔ | ✔ | — | — | — | — | — | — | std |
| Rectangle / Ellipse | ✔ border | ✔ | — | — | ✔ fill + opacity slider | — | — | — | std |
| Text | ✔ | — | — | — | — | ✔ S/M/L font | ✔ content | — | std |
| Arrow | ✔ | ✔ width | — | ✔ head-shape | — | ✔ S/M/L head | ✔ label | — | std |
| Position | — | — | — | — | opacity only (zones fixed bull/bear) | — | — | Entry/Target/Stop + Risk% + R:R | Delete only |
| Alert | ✔ | ✔ dash | — | — | — | — | ✔ message | Trigger price + condition ↑/↓/⇅ | Delete only |
| Measure / Magnifier | — | — | — | — | — | — | — | — | **no panel** |

`std` footer = default-mode tool row / selected-mode `Set as default ✓ / 🗑`. Position + Alert drop "Set as
default" (a default entry/trigger price is incoherent). Reused verbatim: COLOR/WIDTH/DASH/ENDING/HEAD tiles,
the ring `DataTrigger`, stamped-command wiring, `RefreshPenTiles`. New rows all reuse the tile grammar
(FILL = a 2nd color grid; SIZE = 3 dot-tiles; ARROW-HEAD = head tiles on their own row; ALERT condition =
3-tile segment). Only **two genuinely new primitives: an opacity `Slider` + a text `Entry`.**

## 3. New controls
- **Fill + opacity** (Rect/Ellipse/Position): fill colour = a 2nd `PenColorTile` grid (`PenFillTiles`,
  tile[0] = "∅ none"); opacity = one inline `Slider` 0–100 step 5 + `%` label, **commit on `DragCompleted`**
  (per-tick JSON writes + `RefreshPenTiles` would jank). Position uses opacity only; zones fixed
  `ChartBull`/`ChartBear`.
- **Text + size**: one `Entry` (placeholder by kind: "Label…"/"Arrow label…"/"Alert message…") bound to
  `PenText` (payload, via `MutateSelectedDrawing`, not style). SIZE = 3 dot-tiles S/M/L; one shared `SizeKind`
  = font size for Text, head size for Arrow (a drawing is only one kind, no conflict).
- **Position legs** (selected only): three numeric `Entry` — Entry/Target/Stop (mutate P1/P2/P3) — + Risk%
  `Entry` + read-only R:R label (`|Target−Entry|/|Entry−Stop|`). Risk% = a single global `Preferences`
  scalar (account property), not per-drawing. Drag-on-chart stays primary; entries are the precision fallback.
- **Alert**: trigger-price `Entry` (P1); condition = 3-tile ↑/↓/⇅ (default Any); message = the shared TEXT
  entry; seed new alerts amber+dash.

## 4. Data model (all trailing defaults → old JSON loads untouched)
```csharp
DrawStyle( … , Color? Fill = null, float FillOpacity = 0.15f, SizeKind Size = SizeKind.Medium );
DrawingObject( … , string? Text = null, decimal P3 = 0m, AlertCondition Condition = AlertCondition.CrossAny );
// new append-only enums: SizeKind{Small,Medium,Large}; AlertCondition{CrossAny,CrossUp,CrossDown}
```
Fill hue + opacity are separate fields (not alpha-in-color) so tile selection stays unambiguous. `DrawStyle`
= stylistic (eligible for Set-as-default); `DrawingObject` Text/P3/Condition = per-drawing payload (NOT
captured by Set-as-default). Add `MutateSelectedDrawing(Func<DrawingObject,DrawingObject>)` parallel to
`MutateSelectedStyle` — two mutate seams total.

## 5. Build notes
- **ChartViewModel**: `EditingKind` + 8 `Show*` bools (re-notified in the hub trio); `MutateSelectedDrawing`;
  commands `SetDefaultFill`/`SetSize` (via ApplyPenStyle), `SetAlertCondition` + text/leg/trigger setters (via
  MutateSelectedDrawing); bound props `FillOpacityPct`, `PenText`, `PositionEntry/Target/Stop`,
  `PositionRiskPct` (global pref), read-only `PositionRiskReward`; extend `RefreshPenTiles` + `PenPanelHeader`.
- **PenTiles**: reuse `PenColorTile` for `PenFillTiles`; add `PenSizeTile` (dot visual, like width).
- **ChartView.xaml**: add gated sections as new stack children in §1 order; leave the existing 8-col
  ENDING·HEAD grid untouched (new sections are separate children → compiled bindings stay warning-free).
- **StylePreviewDrawable**: one new `Swatch` mode (filled square at FillOpacity) for fill tiles; size tiles
  reuse Dot mode.

**Deferred (cut list):** per-drawing position zone colours (→ fixed bull/bear); per-tool default styles
(→ single shared default + a seeded amber+dash for alerts); Measure/Magnifier settings (none); Arrow/Alert
font family/bold/background; account lot-size calculator; fib/channel sections (design-on-paper, one gated
section each when asked).
