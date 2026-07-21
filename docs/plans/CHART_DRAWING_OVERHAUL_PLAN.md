# Chart Drawing Overhaul — full spec + phased build

Client-side TradingView-style chart overhaul (task #193). Owner (Kiesh) streamed the spec 2026-07-16.
All work is on `feature/bot-market-realism-v2`, client only (no server/prod impact). Build note: the MAUI
client exe cannot be produced while the client app is running (it locks `KieshStockExchange.Shared.dll`);
compilation still surfaces C# errors, so compile-check with the app closed for a testable build.

## ✅ DONE (committed `0efa414`)
- Measure tool = one-shot (drag → ruler → release clears it **and** disarms the tool, TradingView-style).
- Right-click on a **selected** line → deselects it (instead of removing).
- Open head (`>`) → the connecting line runs to the **tip** (Filled/Outline still stop at the base).
- Head-shape tiles greyed + disabled unless the line has a **LineEnding** (`CanEditHead`).
- Polyline label glyph → `/\↗`.

## The tool set (LEFT RAIL) — grouped, TradingView-style
A slim vertical icon rail on the chart's left edge. Tools are **grouped into categories** (TradingView
pattern): each group is ONE rail button showing the **last-used tool** of that group. **Final groups
(top → bottom)** — REVISED 2026-07-21 (owner-decided; see the "## 2026-07-21 rail/toolbar revision" section
below for the full rationale + behavioral checklist):

1. **Cursor** — (single)
2. **Lines** ▸ Trend line · Ray · **Extended line** (infinite both ways) · Horizontal line · Horizontal ray ·
   Vertical line · Polyline   *(Fib retracement REMOVED from Lines → moved into Position)*
3. **Shapes** ▸ **Arrow (now the FIRST item)** · Circle (perfect) · Ellipse · Rectangle · **Rotated rectangle**
   (2 points, then adjust width) · **Triangle** · **Arc**   *(Arrow moved here from the old Drawing group)*
4. **Position** ▸ Long · Short · **Manual** · **Fib retracement**   *(Long/Short = drag entry/target/stop on
   the chart; **Manual** = type the entry/target/stop/qty directly to place the box — same panel/data, no
   dragging. Fib retracement now lives in this group.)*
5. **Draw** (Drawing + Text COMBINED, placed AFTER the Position group) ▸ **Brush** (Freehand) · **Highlighter**
   (brush at high transparency) · Plain text · **Comment** · **Price label**
6. **Measure + Magnifier** (COMBINED into one group) ▸ Measure · **Magnifier(+)** (box-zoom-in) ·
   **Magnifier(−)** (box-zoom-out) — the group's armed icon auto-switches to the *opposite* magnifier after use.
7. **Actions** (COMBINED) ▸ **Undo** · **Redo** · **Delete-all**
8. **👁 Show/hide drawings** — (single)

*(Superseded by this revision: the old standalone **Magnet** rail group — deferred/unbuilt — is NOT one of the
eight groups above; the old separately-pinned **🗑 Delete-all** button is folded into the new Actions group
(item 7) and its dialog is reworked to a 3-choice dialog — see change **E** below.)*

**Flyout interaction:** hovering a multi-option group **widens it to reveal its options** (each with its
name); the flyout stays open while you interact. **Picking a tool collapses the flyout** back to the group
icon **and shows that tool's settings panel** (per `CHART_TOOL_PANELS_DESIGN.md`). The **active tool is
highlighted**.

**Alert is NOT on the rail** — top-row button next to the moving-average button. Delete-last = `Delete` key
(+ optional button); Snapshot is top-right.

**Panel mapping (council architecture scales to the new tools):** shapes (circle/ellipse/rect/rotated-rect/
triangle/arc) → border stroke + fill + opacity; Brush → stroke; Highlighter → stroke at high opacity/
transparency; text variants (plain/comment/price-label) → text + size + colour; Long/Short → the Position
section.

*(All rail tools resolved. Long/Short/Manual = drawable R:R tools, no order-engine hookup.)*

## 2026-07-21 rail/toolbar revision (owner-decided)
Kiesh re-decided the rail grouping and a batch of rail/toolbar behaviors on 2026-07-21. This section is the
authoritative record; the "Final groups (top → bottom)" list above has been revised to match.

### Revised grouping (the eight groups, top → bottom)
- **Group order + membership** is exactly the 8-item list above. Net moves vs. the prior spec:
  - **Fib retracement**: REMOVED from **Lines**, MOVED INTO **Position** (Position ▸ Long · Short · Manual · Fib).
  - **Arrow**: MOVED from the old **Drawing** group INTO **Shapes**, and it is now the **FIRST** item in Shapes
    (Shapes ▸ Arrow · Circle · Ellipse · Rectangle · Rotated-rect · Triangle · Arc). Of Shapes, the tools
    **built today** are **Arrow · Rectangle · Ellipse**.
  - **Draw** = the old **Drawing** group **COMBINED with Text**, and the combined group is placed **AFTER**
    Position (Draw ▸ Brush/Freehand · Highlighter · Plain text · Comment · Price label).
  - **Measure + Magnifier**: COMBINED into one group (Measure · Magnifier(+) · Magnifier(−)).
  - **Actions**: **Undo · Redo · Delete-all** COMBINED into one cluster/group.
  - **👁 Show/hide drawings** stays a single tile.

### Behavioral changes (remaining issues — implementation checklist for council)
- **A — Scrollable rail.** The rail must be a **scrollable list** (with a scroll bar) so every group is
  reachable even when there are more groups than fit vertically. This is the *fallback* once the dynamic
  scale (B) bottoms out at its floor. *(behavioral — needs design)*
- **B — Dynamic sizing to window height.** The rail is **dynamically sized to the window height**: at max
  window size it renders at **scale 1.0**; as the window shrinks and not all groups fit, the rail scales its
  icons/spacing **DOWN proportionally** to keep the full list visible, down to a **minimum scale ~0.6 (0.6
  floor)**. Model it as a **dynamic sizing factor in [0.6, 1.0]** driven by *available height ÷ natural
  full-list height*. Order of operations: **B first** (shrink to fit), then **A** as the fallback once 0.6 is
  hit (scroll the remainder). *(behavioral — needs design)*
- **C — Measure+Magnifier auto-switch.** With Measure + Magnifier combined into one group, when the user uses
  **Magnifier(+)** (box-zoom-in), the group's **selected/armed icon auto-switches to Magnifier(−)** (zoom-out),
  and **vice-versa** — so the visible armed icon always reflects the *next logical action*. *(behavioral —
  needs design)*
- **D — Actions cluster.** Combine **Delete-all** with **Undo** and **Redo** into one actions cluster/group
  (item 7). *(pure-layout for the grouping; Delete-all itself needs E)*
- **E — 3-choice delete dialog.** **Delete-all** must open a dialog offering **THREE choices** —
  **"Delete last" / "Delete all" / "Cancel"** — NOT the old single **"Confirm delete"** button. "Delete all"
  still **spares locked drawings**; "Delete last" removes the most recently placed/selected/moved (unlocked)
  drawing. *(behavioral — needs design)*
- **F — Drawing while hidden auto-shows.** When the user activates/uses **any drawing tool while drawings are
  HIDDEN** (eye = hide mode), the chart automatically **switches back to SHOW mode** so the tool they place is
  visible. *(behavioral — needs design)*

### Built vs. unbuilt tools (per group, as of 2026-07-21)
- **Built:** Arrow, Rectangle, Ellipse, Freehand/Brush, Text, Comment, Long/Short/Manual (Position), Fib
  retracement, Measure, Magnifier(+), Magnifier(−), Undo, Redo, Eye (show/hide).
- **Unbuilt / deferred:** Highlighter, Price-label, Circle, Rotated-rect, Triangle, Arc, Magnet, Delete-all.

### Pure-layout vs. behavioral (of the eight changes)
- **Pure-layout (buildable now, no new design):** the grouping/reorder itself — Fib→Position, Arrow→Shapes
  (first), Drawing+Text→**Draw** (after Position), Measure+Magnifier grouped, Undo/Redo/Delete-all grouped
  (the *cluster*, not the E dialog).
- **Behavioral (need a design pass):** **A** (scrollable rail), **B** (dynamic sizing), **C** (magnifier
  auto-switch state), **E** (3-choice delete dialog), **F** (hide-mode auto-show). **D** is pure-layout for
  the grouping but pulls in E for the Delete-all action.
- **First build (being implemented immediately):** the pure **layout** move only — **Fib → Position**,
  **Arrow → Shapes (first)**, **Drawing + Text → Draw (after Position)**. The rest (A–F behaviors) await a
  **council-reviewed implementation plan** (see "## Implementation plan (for council review)" at the end).

## Tool behaviors
- **Magnifier** — drag a box → zoom the viewport to it (X = offset+count, Y = manual range). **Disables
  auto-fit** on use.
- **Magnet** (harder) — a persistent SNAP **mode** (not a one-shot tool; a 3-state toggle **off / weak /
  strong**, its own rail button). While ON, placing/dragging a drawing anchor **snaps to the nearest
  significant price of the nearest candle** — wick **high**, wick **low**, **open**, or **close** (snapping
  the anchor's time to that candle too):
  - **Weak** — snaps only when the cursor is **within a small threshold** of an OHLC point; otherwise the
    anchor follows the cursor freely.
  - **Strong** — **always** snaps to the nearest OHLC point of the candle under/near the cursor.
  - **Not near any candle** → anchor is placed at the **cursor** (no snap) in both modes.
  Applies to every anchor-placing tool (lines, shapes, position legs, etc.). Implemented as a
  pixel→(candle, nearest-OHLC) snap the placement/drag path routes through when Magnet is active.
- **Rectangle / Ellipse** — 2-corner shape; **border** color/thickness/dash + **fill** color + opacity.
- **Text** — anchor + string; needs a text-entry affordance in the pen panel.
- **VLine** — vertical line at a time; shows its **time on the x-axis**.
- **Brush / Highlighter** (Drawing group) — drag → a free path, but rendered as a **smoothed spline**
  (Catmull-Rom / Bézier through the captured points), NOT the exact jittery pointer trace, for natural
  curves. Highlighter = brush at high transparency. A **Smoothing** slider (spline tension = how hard it
  curves, e.g. 0 = follow points exactly → high = very rounded) is a per-tool setting in the panel.
  Points are decimated + persisted like a polyline; the spline is re-evaluated at draw time.
- **Position (long/short)** — TradingView-style box with THREE horizontal lines + two shaded zones and
  draggable handles (drag each leg to reprice; drag the whole box to move):
  - **Entry** line (middle) — label: **PnL** (live/closed) + **Qty** + **Risk/reward ratio**.
  - **Target** line (top) — a **green** profit zone from entry→target; label: **target price · (% move) · Amount** (profit).
  - **Stop** line (bottom) — a **red** loss zone from entry→stop; label: **stop price · (% move) · Amount** (loss/risk).
  - Long = green above / red below entry; Short = mirrored. Amounts/% derive from entry, qty and the leg prices.
  - **When selected**, its settings panel shows/edits EVERY detail: **quantity (shares)**, **entry / target /
    stop prices**, **risk %**, computed **amounts** ($ profit/loss per leg), and the **R:R** — all editable,
    kept in sync with the draggable handles.
- **Alert line** — horizontal line that fires a notification when price crosses (ties into notifications).
- **Arrow marker** — points at a candle, optional short label.

## Top toolbar
- Convert the chart-type / volume / scale togglers into **compact named dropdowns** (Bars / Candles /
  Hollow candles / Line / Area / Heikin-Ashi …), like the TradingView chart-type menu. Keep them small.
- **Snapshot** button at the **top-right** (captures the chart image).
- **Price-scale settings menu** (right-click the price axis / a gear): scale mode
  **Regular / Percent / Indexed-to-100 / Logarithmic** + **Auto-fit** + Lock-price-to-bar-ratio +
  Invert scale + Move scale to left. Retire the standalone scale/auto-fit buttons into this menu.

## Cross-cutting behaviors
- **Undo `Ctrl+Z` / Redo `Ctrl+Y`** — bounded stack over the drawings (add / move / delete). **Skips locked
  items** — undo will not remove or alter a locked drawing.
- **Lock** — each drawing's settings panel has a **Lock** toggle. A locked drawing is protected: it can't be
  moved, its style/values can't be edited, and it can't be deleted (`Delete` key / ✕ glyph / right-click
  remove / delete-last all skip it) — nor undone away by `Ctrl+Z`. A small lock icon marks it; unlock to
  modify. (`DrawingObject += bool Locked`, trailing default false, back-compat.)
- **Deletion paths (locked-down):** ONLY via (a) the tool's **settings-panel Delete button** and (b) the
  **`Delete` key** on the selected drawing — plus the **delete-last** button and **delete-all**. **REMOVE the
  on-chart ✕ close glyph** on drawings (drop the `DrawCloseGlyph` render + the `DrawingHitPart.Close` hit
  path). **Right-click no longer deletes** either — it deselects a selected drawing / disarms the tool on
  empty chart. (Supersedes the earlier right-click-removes-unselected behavior.)
- **Delete-last-touched** button (+ `Delete` key) — removes the most recently placed/selected/moved drawing.
- **Label/gutter gating** — trend/ray price + %-change labels show **only on hover or when selected**
  (clean otherwise); on select, endpoint prices show in the **right gutter**; HLine **always** shows its
  price in the gutter; VLine shows its **time on the x-axis**.
- **`Escape`** exits the line settings (closes the pen panel + deselects + disarms the tool).
- **Eye toggle** hides/shows all drawings.
- **Active-tool highlight** — the currently armed rail tool (and any active top-row toggle) shows a clear
  selected-state highlight (background/border/accent) so it's obvious what's active.
- **Crosshair cursor** — in **Cursor mode** the OS mouse pointer becomes a **crosshair (cross)** while over
  the chart (WinUI `ProtectedCursor` = `InputSystemCursorShape.Cross`); reverts to the arrow off-chart. (A
  drawing tool can show a crosshair/pen cursor too — confirm on eyeball.)
- Pen panel gains, for **Rectangle + Ellipse**, a **fill colour** picker + an **opacity slider (bar)** to
  set fill transparency; and a **text field** for the Text tool.

## Axis polish
- **X-axis time labels are too sparse** — display time labels more frequently along the bottom axis
  (tighter tick spacing / more gridline labels).
- **Auto-scale padding too big** — Y-autofit uses a fixed `YPaddingPercent = 0.06` (6%), which reads as too
  much empty space. Add a **`+` / `−`** control **next to the auto-scale (Y-Auto) button** to tighten/loosen
  the Y-padding (adjust `YPaddingPercent`, persisted; sensible clamp e.g. 0–15%). Lower the default too.

## Storage — DECIDED: server-side, per user (own ULTRAPLAN)
Drawings move to the **server**, scoped to the user's account (cross-device, survives reinstall, no
cross-user leak). This is a separate, well-bounded feature = its **own ultraplan** (server DB + client
persistence swap), distinct from the interactive client drawing-tools build:
- **Server:** a `UserDrawings` table (userId, stockId, currency, JSON payload of the drawings list, updatedAt)
  + EF migration + CRUD endpoints (get/save/delete per user+stock+currency), following the existing DB/API
  patterns. The `DrawingObject`/`DrawStyle` JSON is the wire contract.
- **Client:** replace the direct `Preferences` calls (`PersistDrawings`/`LoadDrawingsForSelected`) with an
  `IDrawingStore` abstraction — server-backed, with a local cache for offline/perf and last-write-wins on
  reconnect. Loads on stock switch + auth.
- **Batched writes (owner):** never write per-change, and there's **no haste** (drawings aren't time-critical).
  - **Client:** debounce + coalesce edits into one dirty-set per user+stock+currency; POST the whole set
    (not per-shape). Flush triggers = debounce timer + stock-switch + app-background/logout.
  - **Server:** the endpoint just enqueues into an **in-memory buffer/dirty-set**; a **background flush loop
    on a relaxed timer** (generous interval — seconds→minutes, configurable, no rush) drains the queue and
    **batch-upserts** all pending users' changes in one transaction. Same pattern as the existing
    `CandleService.FlushLoopAsync` (buffer → periodic drain → batch persist). Flush on shutdown too, so the
    last batch isn't lost.
- Current local `Preferences` blobs = legacy; optional one-time migrate-up on first login.
The drawing-TOOLS build (rail, shapes, panels, undo) is independent of the backend and proceeds phased.

## Settings reorg
- **Moving-average panel** — tidy it to MA-only. Remove the **candle-colour** controls that currently sit
  below it; move candle colours into the **bottom-right chart settings** (the general settings gear/menu).

## Phased build (each phase testable on its own)
1. **Rail foundation** — left icon rail + hover-expand names; relocate existing line tools + Measure;
   Magnifier (zoom-box, disables auto-fit); eye toggle; snapshot button top-right; `Escape` exits settings.
2. **Shapes & text** — Rectangle, Ellipse, Text, VLine, Freehand + fill/opacity + text input; VLine
   x-axis tag; trend labels + endpoint gutter prices only on hover/select.
3. **Undo / redo / delete** — `Ctrl+Z`/`Ctrl+Y`, delete-last-touched, `Delete` key.
4. **Top toolbar** — chart-type/volume/scale → compact named dropdowns; price-scale settings menu
   (+ Indexed-to-100 mode, lock ratio, invert, move-left).
5. **Trading tools** — Long/Short position, Alert line, Arrow marker, Snapshot capture.

## Implementation plan (for council review)
Concrete approach for the SIX behavioral items **A–F** from the 2026-07-21 revision. The pure-layout move
(Fib→Position, Arrow→Shapes-first, Drawing+Text→Draw-after-Position) ships first and is out of scope here —
it is XAML/predicate edits in `ChartToolRailView.xaml`, `ChartView.xaml` flyouts, and
`ChartDrawingViewModel.Rail.cs` (group predicates + `PickGroupTool` + `ToolIcon`), following the pattern in
`CHART_ROUND2_SPEC.md` §5. Files each behavioral item touches are listed inline.

**Ordering / dependencies.** Do **B before A** (dynamic scale shrinks first; scroll is only the floor
fallback), and they should land together since A is B's overflow path. **D** (grouping) precedes **E** (its
dialog). **C** and **F** are independent VM-state hooks and can land anytime.

- **A — Scrollable rail** *(files: `ChartToolRailView.xaml`)*
  Wrap the rail's root `VerticalStackLayout` of group tiles in a `ScrollView` (`Orientation="Vertical"`,
  `VerticalScrollBarVisibility="Auto"`). The `ScrollView` fills the chart's left column (fixed width, star
  height). Because B shrinks content first, the scroll bar only appears once the 0.6 floor is hit and the list
  still overflows. Keep the eye tile inside the same scroll content (owner wants *every* feature reachable) —
  do not pin it outside the scroll.

- **B — Dynamic sizing factor [0.6, 1.0]** *(files: `ChartToolRailView.xaml`, `ChartDrawingViewModel.Rail.cs`)*
  Drive a single VM scalar `RailScale` (double, clamped `[0.6, 1.0]`). Measure available height from the rail
  container's `SizeChanged` (bind the `ScrollView`/host `HeightChanged` → a code-behind or behavior that calls
  `vm.Drawing.SetRailAvailableHeight(h)`), and compute `RailScale = Clamp(availableH / NaturalFullListH, 0.6,
  1.0)` where `NaturalFullListH` = groupCount × (tileHeight + spacing) at scale 1.0 (a const derived from the
  8 tiles). Apply the scale by binding each tile's `HeightRequest`/`WidthRequest` and the icon `Image`
  `Scale` (or `HeightRequest`/`WidthRequest`) to `RailScale` via an `IValueConverter` (`ScaleToSize`,
  base-size × factor) so icons + inter-tile spacing shrink together. Spacing: bind the `StackLayout.Spacing`
  through the same converter. Recompute on every `SizeChanged`. Rationale for VM-owned scalar over pure XAML:
  the natural-height baseline and the min-clamp are easier to reason about (and unit-checkable) in the VM than
  in a triggers/converters-only XAML expression, and it keeps one source of truth the scroll fallback reads.

- **C — Magnifier +/- auto-switch** *(files: `ChartDrawingViewModel.Rail.cs`, `ChartToolRailView.xaml`,
  `ChartView.Windows.cs` zoom-commit)*
  The Measure+Magnifier group already tracks a "last-used tool" for its tile icon (the `*GroupTool` pattern).
  Add the group's armed magnifier direction as VM state (`MagnifierGroupTool` ∈ {Measure, MagnifierIn,
  MagnifierOut}). In the zoom **commit** hook (where a box-zoom completes, `ChartView.Windows.cs`), after a
  successful `MagnifierIn` zoom set `MagnifierGroupTool = MagnifierOut` (and vice-versa) — so the *armed* icon
  the group tile shows, and the next single-click action, is the opposite direction. The tile icon binds
  `MagnifierGroupIcon` (already the pattern); flipping `MagnifierGroupTool` re-notifies it. Measure use does
  not flip. This is pure client VM state, no persistence.

- **D — Actions cluster** *(files: `ChartToolRailView.xaml`, `ChartDrawingViewModel.Rail.cs`)*
  Group Undo · Redo · Delete-all as one rail cluster (three command tiles under one group header, or a small
  flyout mirroring the Lines/Shapes group `Grid`). Undo/Redo bind existing `UndoCommand`/`RedoCommand`;
  Delete-all binds a new `DeleteAllCommand` (see E). No new drawing-tool arming — these are commands, so no
  pen panel (per `CHART_TOOL_PANELS_DESIGN.md` §1 "commands → no panel").

- **E — 3-choice delete dialog** *(files: `ChartViewModel` (or `ChartDrawingViewModel.Rail.cs`),
  `ChartView.xaml.cs` code-behind for the `DisplayActionSheet`)*
  Replace the old confirm-delete flow. `DeleteAllCommand` raises a request the View answers with MAUI
  `Page.DisplayActionSheet("Delete drawings?", "Cancel", null, "Delete last", "Delete all")` (Cancel is the
  action-sheet's built-in cancel; no destructive-arg needed, or pass "Delete all" as the destruction arg for
  red styling). MVVM seam: the VM exposes an `async` command that awaits an injected
  `IChartDialogService.PromptDeleteAsync()` returning an enum `{DeleteLast, DeleteAll, Cancel}` (keeps the
  `DisplayActionSheet` call in a thin View-layer service, VM stays testable) — or, matching the repo's
  existing pattern, a `WeakReferenceMessenger` request handled in `ChartView.xaml.cs`. On `DeleteLast` →
  existing delete-last-touched path (skips locked); on `DeleteAll` → clear all **except locked** (existing
  delete-all body); on `Cancel` → no-op. Both destructive branches push onto the undo stack per existing
  behavior.

- **F — Drawing-while-hidden auto-show** *(files: `ChartDrawingViewModel.Rail.cs`, and wherever a draw tool is
  armed / a drawing is committed)*
  Central hook in the tool-arming path (`SelectDrawTool` / `PickGroupTool` in `ChartDrawingViewModel.Rail.cs`):
  when a *drawing* tool (anything that places a `DrawingObject` — i.e. not Cursor / Measure / Magnifier /
  eye / actions) is armed **and** `DrawingsHidden` is true, set `DrawingsHidden = false` (flip the eye back to
  SHOW) before the placement begins, so the placed shape is visible. Belt-and-suspenders: also guard the
  placement-commit path (`ChartView.Windows.cs`) to un-hide on commit. Single source of truth = the existing
  eye-toggle `DrawingsHidden` bool; the drawable already keys its render off it, so no drawable change beyond
  reading the flag it already reads.

**File touch summary:** `ChartToolRailView.xaml` (A scroll wrap, B size bindings, C/D tiles),
`ChartView.xaml` flyouts (Draw/Position/Measure+Magnifier group flyouts from the layout move),
`ChartDrawingViewModel.Rail.cs` (`RailScale` + `SetRailAvailableHeight`, `MagnifierGroupTool` flip,
`DeleteAllCommand`, F auto-show hook, group predicates), `ChartViewModel` (E dialog seam / messenger),
`ChartView.xaml.cs` (SizeChanged → available-height, `DisplayActionSheet`), and the drawable (read-only —
already keys off `DrawingsHidden`; no change expected).

**File-name note:** the rail/group logic (`*GroupTool` predicates, `PickGroupTool`, `ToolIcon`) currently
lives in **`ChartDrawingViewModel.Pen.cs`**, not a `.Rail.cs` — this plan uses "`.Rail.cs`" as shorthand for
"the rail partial." A clean move would split those members into a new `ChartDrawingViewModel.Rail.cs` partial
(the VM is already partial across `.cs`/`.Pen.cs`/`.Undo.cs`/`.Colors.cs`/`.Persistence.cs`); Undo/Redo
commands live in **`ChartDrawingViewModel.Undo.cs`**.

## Build status (2026-07-21, autonomous batch)
BUILT + committed this session (branch feature/bot-market-realism-v2, tip after `6b0f6c3`):
- Rail regroup v2 (Fib→Position, Arrow→Shapes-first, Drawing+Text→Draw), Alert→top toolbar.
- A scrollable rail; C Measure+Magnifier combine (−(zoom-out) standalone, pops up on CanZoomOut);
  D+E Delete-all rail button → 3-choice action sheet (Delete last/all/Cancel, spares locked, undoable);
  F draw-while-hidden auto-show.
- New tools: Text rework (plain + font-size), Comment callout, Position Long/Short/Manual + panel,
  Cross line (H+V, Lines group), Circle + Triangle (Shapes group).
- Rail order Lines·Shapes·Position·Draw | Measure·(−) | Delete·Undo·Redo·Hide; both dividers width 42.
- Shared ChartGeometry.ShapeRect(square,...) so render + hit agree (Circle=square, others=bbox).

REMAINING (unbuilt tools + behaviors, roughly easiest→hardest):
- Shapes: Rotated-rect (2pt + width), Arc (curved). Draw: Highlighter (Freehand at high alpha), Price label (Text variant).
- Magnet (snap mode off/weak/strong — placement-path snap to nearest OHLC). Lock toggle in panel + enforce.
- B dynamic rail sizing [0.6,1.0] (LAST/optional; guard the SizeChanged loop per the council plan).
- Alert-as-message: persisted server Message + centered popup + chart deep-link (SPANS client+server — owner-gated).
- Icon pass: tool_crossline / tool_circle / tool_triangle / tool_delete / tool_comment .png (placeholders reuse siblings).
- Axis polish (denser x time labels, smaller Y-autofit padding + a +/- control), remove ✕ close glyph, Escape/Delete keys, crosshair cursor.

## 2026-07-21 — text-drawing rework + Circle (owner ask + COUNCIL verdict)
**Circle (DONE this session):** now CENTRE + RADIUS — anchor1 = centre, anchor2 = a ring point; drawn round on
screen (r = pixel dist), same fill/opacity+stroke as ellipse. (Was a square bbox.) Dedicated render + hit
branches in DrawingRenderer/ChartHitTester; ShapeRect(square) param now always false (Rect/Ellipse/Triangle bbox).

**Owner requirements (Text label / Comment / Price label) — LEFT IN THE PLAN (harder, council-guided):**
- Text label: place a DOT at the anchor, text starts to the RIGHT of the dot (offset); type INLINE on the chart
  (no modal popup); auto-delete if empty.
- Comment: rounded bubble (colour border + fill+opacity + text, same text settings as Text) with a DYNAMIC tail
  ("<") that auto-positions toward the anchor and adjusts as you type; auto-forms from an origin + a second point.
- Price label: same bubble look but auto-DISPLAYS the price at its anchor (not typed text).

**COUNCIL VERDICT (4 advisors — Contrarian/First-Principles/Executor/Outsider, near-unanimous):**
1. DROP the user-placed "second point / opposite corner" — it contradicts grow-to-fit text. The ANCHOR (dot) is
   the one real input; the box AUTO-SIZES to the text; the "second point" is at most a DIRECTION/QUADRANT hint
   (which way the bubble opens off the anchor), derived from the release direction — NOT a dragged corner.
2. UNIFY into ONE model — "AnchoredText" { Anchor, Offset/quadrant, Text, RenderMode∈{Plain,Bubble,PriceBubble},
   ShowTail, Style }. Text label = Plain + right-offset; Comment = Bubble + tail; Price label = PriceBubble (text =
   formatted price at anchor.Y, computed at render). Three rail tools arm the same model via presets.
3. INLINE typing = a transparent/chromeless Entry overlay at the anchor owning focus+IME+caret; its TextChanged
   pushes into the draft DrawingObject.Text and Invalidates the GraphicsView (Entry = pure keystroke/caret sink).
   Commit on Enter/focus-loss; discard draft on Escape/empty. SPIKE the WinUI transparent-Entry caret/IME FIRST
   (Contrarian: riskiest piece; if it janks, fall back to a "modal-lite" inline editor).
4. DON'T jitter: LOCK the bubble position once typing starts; the dynamic tail SNAPS to the box edge nearest the
   anchor, re-solved on settle (NOT per-keystroke).
5. AUTO-DELETE only on explicit commit with zero chars (Enter/Escape/focus-loss) — NEVER on stray blur/mid-edit
   (data-loss trap).
6. BUILD ORDER: Text-label inline FIRST (the overlay + focus/commit lifecycle is the reusable core) → Comment
   (bubble+tail) → Price label (computed text).
**Divergence from the owner's literal spec to confirm:** (a) second point = direction hint, not a dragged corner;
(b) tail snaps + settles, not per-keystroke jitter; (c) auto-delete only on explicit commit, never on blur.

**Placement (when built):** the AnchoredText model = DrawStyle/DrawingObject (append-only new fields RenderMode/
ShowTail); render/hit in DrawingRenderer/ChartHitTester; inline-Entry overlay = a NEW control hosted in ChartView
(+ ChartView.Windows.cs for placement/focus/commit); rail/draft state in Rail.cs. Replaces the modal
PromptTextLabelAsync path.
