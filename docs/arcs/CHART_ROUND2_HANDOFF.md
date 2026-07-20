# CHART round-2 — HANDOFF (design-alignment + reworks; paused by Kiesh 2026-07-20)

Round 1 (the 6 render-wiring features) is DONE + committed on `feature/bot-market-realism-v2`. Round 2 = align the
v1 tools to the DOCUMENTED design + add the missing pieces. **Kiesh's steer:** *"we made a lot of things clear about
the Position (and Text/Comment/grouping) in the docs — look through the docs for the intended design and how, and
align the implementation."* So **step 0 of round 2 is doc-mining, not coding.**

## Round-1 status (DONE — visual-test plan: `docs/arcs/CHART_RENDERWIRING_TEST_PLAN.md`)
Alert `6e3cbe0` · Text `eeb35ca` · Fibonacci `ec24fa4` · Position `52c60da` · Bollinger+VWAP `ee99696` · RSI `74752eb`.
All build-clean + adversarially reviewed (existing tools untouched). All LOCAL until Kiesh runs the pending
`git push --force-with-lease origin feature/bot-market-realism-v2` (a history-cleanup force-push is outstanding).
A test worktree exists at `C:/Users/kjden/source/repos/Kieshdh/KieshStockExchange-test` (pinned at feature-4 `52c60da`).

## ★ STEP 0 — mine the design docs FIRST (don't code from memory)
Read + consolidate the INTENDED design from: `docs/plans/CHART_DRAWING_OVERHAUL_PLAN.md` (the rail grouping + tool
behaviors) and `docs/plans/CHART_TOOL_PANELS_DESIGN.md` (per-tool pen panels), plus skim `CHART_OVERHAUL_INSPECTION.md`
/ `CHART_DRAWING_OVERHAUL_PLAN` behaviors. Produce a consolidated `docs/arcs/CHART_ROUND2_SPEC.md` = for each item:
intended design (per docs) → current v1 impl → the gap to close. THEN implement per that spec (executor → disk-gated
build → adversarial review → commit; one item per commit; existing tools untouched).

## Round-2 backlog (Kiesh's explicit asks + doc alignment)
1. **Fib label boxes (quick fix)** — the level tags are variable-width (price digit-count differs) so they don't
   align → ugly. Make every tag a **uniform/universal width**, ratio **left**-aligned + price **right**-aligned (numbers
   on the right side) so the pills line up in a clean column. File: `DrawingRenderer.DrawFibLevelTag` (fixed width const
   + two DrawString calls, left/right aligned). Small, standalone commit.
2. **Text rework** (Kiesh spec, matches `CHART_TOOL_PANELS_DESIGN` line 48: Text = colour + size + content, **no width**):
   render **plain text ONLY** (no pill/box); text drawn in the **pen colour** (= the text colour, not a border). Pen
   panel for Text: **remove Width**, **add Size** = a **dropdown of the universal standard font sizes**
   (8,9,10,11,12,14,16,18,20,24,28,36,48,72) **+ ▲/▼ steppers** that jump to the next/previous standard size. Needs a
   font-size field on the style (or DrawingObject) + the panel control. (The doc's older "S/M/L 3-tile" is superseded by
   this dropdown+stepper.) This REWORKS the committed Text v1 (currently a pill + width).
3. **Comment tool** (new `DrawTool.Comment`, append to the enum) — a **Text + Rectangle combined**: text inside a
   rounded rectangle with a small **downward "v" tail** at the bottom pointing to its anchor (a callout bubble). Same
   text + size + colour panel as Text. It's a Text-group variant (doc: Text ▸ Plain text · **Comment** · Price label).
4. **Position alignment to the docs** — v1 shipped ONE Position tool (drag Entry→Target, mirror Stop, R:R + live-PnL
   pill, Qty=1, in the Shapes group). The DOCS spec more (mine them): **Position group ▸ Long · Short · Manual**
   (`CHART_DRAWING_OVERHAUL_PLAN` line 26) — Long/Short = drag entry/target/stop on the chart; **Manual** = type
   entry/target/stop/qty directly (same panel/data, no drag). Panel (`CHART_TOOL_PANELS_DESIGN` line 50/68): three
   numeric Entries (Entry/Target/Stop → P1/P2/P3) + **Risk%** + read-only **R:R**, **opacity-only** fill (zones fixed
   bull/bear), **Delete-only** footer. Align the v1 impl to this (Long/Short/Manual + the panel), reusing the existing
   render/hit/commit-hook.
5. **Rail regrouping** — **Kiesh CONFIRMED (AskUserQuestion 2026-07-20): "Yes, regroup existing tools" + "Alert → top-row
   button".** Realign the EXISTING tools to the designed groups (`CHART_DRAWING_OVERHAUL_PLAN` lines 15-50); leave the
   unbuilt tools (Circle/RotatedRect/Triangle/Arc, Brush/Highlighter, Price-label, Magnet, Cursor-as-group) for later:
   - **Lines** ▸ Trend · Ray · ExtendedLine · HLine · HRay · VLine · Polyline  (+ **Fib** here for now)
   - **Shapes** ▸ Rectangle · Ellipse
   - **Drawing** ▸ Freehand(Brush) · Arrow
   - **Position** ▸ Position (→ Long/Short/Manual per #4)
   - **Text** ▸ Text · **Comment**
   - **Measure** (single) · **Magnifier** (single)
   - **Alert** — OFF the rail → a **top-row button next to the moving-average button** (Kiesh confirmed).
   Currently the rail has only `lines`+`shapes` groups (`ChartDrawingViewModel.Pen.cs` `LinesGroupContains` /
   `IsShapesGroupActive` / `PickGroupTool`), with Alert/Text/Fib wrongly in `lines` and Position in `shapes`.

## Suggested order
0. Doc-mine → `CHART_ROUND2_SPEC.md`.  1. Fib fix (quick).  2. Rail regroup + Alert→top-row (foundation).  3. Text
rework.  4. Comment tool.  5. Position → Long/Short/Manual + panel. Each: pipeline (executor → disk-gated build →
adversarial review → own read → 1 commit → test-plan row in a round-2 test plan). Reworks of committed features (Text,
Position) are NOT purely-additive — review carefully + Kiesh visual-tests.

## Guardrails
Client-only, NO server/money/CK. XAML styles over inline (`Resources/Styles`). MVVM. Disk-gate every build (70%, Idle,
`-maxcpucount:1`, PARSE logs, client csproj ABSOLUTE path; the app may be running → benign exe-copy-lock, check for
`error CS`, not the copy step). Append-only enum/DrawStyle/DrawingObject fields (old JSON must still load).
