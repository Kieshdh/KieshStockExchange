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

## The tool set (LEFT RAIL)
A slim vertical icon rail pinned to the chart's left edge. **Hover expands the rail to show each tool's
name**; click arms it. Below the rail: an **eye toggle** to show/hide all drawings.
Tools, in order:
cursor · HLine · VLine · Trend · Ray · HRay · Polyline · Freehand · Rectangle · Ellipse · Text ·
Measure · Magnifier · Position (long/short) · Alert · Arrow.
(Snapshot is NOT on the rail — it's a top-right toolbar button; see Top toolbar.)

## Tool behaviors
- **Magnifier** — drag a box → zoom the viewport to it (X = offset+count, Y = manual range). **Disables
  auto-fit** on use.
- **Rectangle / Ellipse** — 2-corner shape; **border** color/thickness/dash + **fill** color + opacity.
- **Text** — anchor + string; needs a text-entry affordance in the pen panel.
- **VLine** — vertical line at a time; shows its **time on the x-axis**.
- **Freehand** — drag → smoothed free path (persisted like a polyline).
- **Position (long/short)** — entry → target → stop; shows risk/reward, R-multiple, %/$ to each leg.
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
- Pen panel gains **fill color + opacity** (shapes) and a **text field** (text tool).

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
