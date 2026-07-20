# Chart drawing-tool icons — generation prompt + palette strategy

## The palette-compliance mechanism (do this regardless of who draws the icons)
Generate every icon **monochrome (pure white `#FFFFFF`) on a transparent background**, one uniform
line weight, no color of its own. The **client tints them at runtime** to a theme brush, so a single
icon set covers all cases:
- **Normal** tool → tinted to the muted foreground (e.g. `ChartAxisText` / a mid-gray theme brush).
- **Active/selected** tool → tinted to the accent (e.g. `ChartBull` / the app accent brush).
- **Light vs dark theme** → the tint is a `DynamicResource`, so it swaps automatically.

MAUI wiring: `CommunityToolkit.Maui`'s `IconTintColorBehavior` on the `Image`/`Button`, with
`TintColor="{DynamicResource ...}"`, plus a `DataTrigger` that swaps the tint brush when the tool is
active. (Alternative: bake per-theme copies — worse; avoid.) So the artwork is theme-agnostic white
line-art; **color, theme and active-state live entirely in code.**

## Icon list (name → what it is)
Rail / drawing tools: `cursor` (arrow pointer) · `trend` (segment, dot at each end) · `ray`
(segment from a dot, arrow at the far end) · `extended_line` (line with arrows both ends) ·
`hline` (horizontal line, dot centered) · `hray` (horizontal line from a left dot) · `vline`
(vertical line, dot centered) · `polyline` (zig-zag with vertex dots) · `rectangle` · `rotated_rect`
(tilted rectangle) · `ellipse` · `circle` · `triangle` · `arc` (quarter-arc curve) · `brush`
(paint-brush tip) · `highlighter` (marker tip) · `arrow` (single arrow) · `long_position` (up
arrow in a green-zone box, but drawn white) · `short_position` (down arrow in a box) ·
`position_by_price` (box with numeric ticks) · `text` (a capital "T") · `comment` (speech bubble) ·
`price_label` (a tag/flag) · `fibonacci` (3-4 stacked horizontal levels) · `measure` (vertical
double-headed arrow / ruler) · `magnifier` (magnifying glass with a box) · `magnet` (horseshoe
magnet) · `eye` (show/hide) · `trash` (delete-all).
Toolbar / chart controls: `camera` (snapshot) · `undo` · `redo` · `lock` · `unlock` · `settings`
(gear) · `alert` (bell) · `crosshair`.

## ChatGPT / DALL-E prompt (paste this)
> Create a single reference sheet of **flat, minimalist, single-color line icons** for a trading-chart
> drawing toolbar. **Pure white icons on a fully transparent background.** One consistent stroke weight
> (~2px at 24×24), rounded line caps and joins, no fills except tiny solid dots for line endpoints, and
> **no gradients, shadows, 3D, color, or backgrounds** — think Lucide / Feather / TradingView toolbar
> style. Lay them out in a **neat evenly-spaced grid**, each icon in its own cell **with its name typed
> in small plain white text directly beneath it**. Include exactly these icons, each drawn as a simple
> recognizable glyph: cursor (arrow pointer), trend line (diagonal segment with a dot at each end),
> ray (segment starting at a dot with an arrowhead at the far end), extended line (line with an arrow at
> both ends), horizontal line (with a centered dot), horizontal ray (from a left dot), vertical line,
> polyline (zig-zag with small dots at each vertex), rectangle, rotated rectangle, ellipse, circle,
> triangle, arc, brush, highlighter, arrow, long position (up-arrow inside a box), short position
> (down-arrow inside a box), position by price (box with small tick marks), text (a capital T), comment
> (speech bubble), price label (a tag/flag shape), fibonacci (three or four stacked horizontal levels),
> measure (a vertical double-headed arrow), magnifier (magnifying glass containing a small square),
> magnet (horseshoe magnet), eye, trash, camera, undo, redo, lock, unlock, settings gear, alert bell,
> crosshair. Keep every icon inside the same square bounding box, visually consistent in weight and
> size, and simple enough to read at 20px. Output one high-resolution PNG with a transparent background.

Then, if you want individual assets: ask it to "**export each icon as its own transparent PNG named
`tool_<name>.png` at 96×96**" (or hand the sheet back to me and I'll trace them to crisp SVGs).

## Reality check + recommendation
Diffusion models are great for **style exploration** but rarely emit pixel-crisp, perfectly-aligned,
uniform-stroke icon sets usable *directly* as production assets (stroke widths drift, small glyphs
blur, and packing many labeled icons into one image compounds the error). The reliable production path
is **vector SVG** — crisp at any size, ~0.3 KB each, tintable, diffable. So: use the sheet to pick the
look, then **I convert the chosen style into clean SVGs** in `Resources/Images/` (I already did 7 in
this geometric style — `tool_{cursor,trend,ray,hline,hray,polyline,measure}.svg`). Fastest of all: I
just author the whole set directly as SVGs (no round-trip) and add the runtime tint, and we skip
image-gen entirely — your call.
