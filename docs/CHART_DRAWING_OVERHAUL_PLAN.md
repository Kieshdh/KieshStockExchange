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
(top → bottom):**

1. **Cursor** — (single)
2. **Lines** ▸ Trend line · Ray · **Extended line** (infinite both ways) · Horizontal line · Horizontal ray ·
   Vertical line · Polyline
3. **Shapes** ▸ Circle (perfect) · Ellipse · Rectangle · **Rotated rectangle** (2 points, then adjust width) ·
   **Triangle** · **Arc**
4. **Drawing** ▸ **Brush** · **Highlighter** (highlighter = brush at high transparency) · **Arrow**
5. **Position** ▸ Long · Short · **Manual** *(all DRAWABLE R:R tools; Long/Short = drag entry/target/stop on
   the chart; **Manual** = type the entry/target/stop/qty directly to place the box — same panel/data, no
   dragging. "Manual" name TBC.)*
6. **Text** ▸ Plain text · **Comment** · **Price label**
7. **Measure** — (single)
8. **Magnifier** — (single)
9. **👁 Show/hide drawings** — (single, pinned bottom)

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

## Tool behaviors
- **Magnifier** — drag a box → zoom the viewport to it (X = offset+count, Y = manual range). **Disables
  auto-fit** on use.
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
- **Undo `Ctrl+Z` / Redo `Ctrl+Y`** — bounded stack over the drawings (add / move / delete).
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
