# CHART render-wiring — BATCH TEST PLAN (for Kiesh)

The 5 deferred chart tools are being wired up autonomously via the **ultradesign method** (fire prompt at
`docs/ultradesigns/ultradesign-chart-renderwiring.md`), one feature per commit on `feature/bot-market-realism-v2`. Each is build-gated + adversarially reviewed (11 existing
tools confirmed untouched). **Kiesh visually tests each below**, then says keep / tweak / revert. Run the client:
`dotnet run --project KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`, open a stock's chart.

---

## 1. Alert line  (commit `<pending>`)
**What:** a new **Alert** tool in the drawing rail's *lines* group — a one-click horizontal price level with a 🔔 bell
at the left edge and a right-gutter price tag. When the live price reaches/crosses the level it renders "fired"
(solid + thicker + warning-tinted); otherwise it's a dashed "armed" line. Persists per stock like any drawing.
**Test:** pick the Alert tool → click at a price → a dashed bell line appears; select it (grab handle) and drag it
up/down; reopen the chart → it persisted. Watch a level the price crosses → it should switch to the solid "fired" look.
**Known v1 limits (by design, not bugs):** the "fired" check is directional + non-latching — an alert placed *below*
the current price shows fired immediately, and one placed above then crossed *downward* won't flip. A proper
cross-latch + alert-condition UI is deferred to v2. **Also eyeball:** the 🔔 emoji may render as a plain box on the
canvas depending on font fallback — if so, say the word and I'll swap it for a drawn vector bell.

## 2. Text tool  (commit `<pending>`)
**What:** a new **Text** annotation tool in the drawing rail's *lines* group — one click drops an anchor and a modal
prompts for the label; it renders as a readable dark pill with white text anchored at that point. Persists per stock.
**Test:** pick Text → click on the chart → type a label in the prompt → it appears as a pill; drag it around
(moves freely in time+price); select it and re-edit the text in the pen panel; reopen the chart → it persisted.
Cancel or leave the prompt blank → nothing is left behind (no empty label). In-place (type-on-canvas) editing is
deferred to v2 — v1 uses the modal prompt.

## 3. Fibonacci retracement  (commit `<pending>`)
**What:** a new **Fib retracement** tool in the drawing rail's *lines* group. Drag between two price points (a swing
low→high or high→low) and it draws the retracement grid — 0 / 23.6 / 38.2 / 50 / 61.8 / 78.6 / 100% plus extensions
to 261.8% — as horizontal lines inside the two-anchor box, each labelled with its ratio + price; the 0 / 50 / 100%
lines are a touch thicker. Persists per stock.
**Test:** pick Fib → drag between two points → the labelled grid appears; click a level line to select it → two
anchor handles show; drag a handle to re-anchor, drag the body to move the whole grid; reopen the chart → persisted.
Zone fills + custom ratios are deferred to v2 (lines + labels only for now).

## 4. Position tool  (commit `<pending>`)
**What:** a new **Position** tool in the drawing rail's *shapes* group — a TradingView-style long/short box. Drag
from an entry price to a target and it fills a translucent **green target zone** (entry→target) and a **red stop
zone** (a mirror of the target across entry) with an entry line, thin target/stop borders, and one **R:R + live-PnL
pill** (PnL updates as the price moves). Long vs short is fixed by the drag direction at creation (never flips
after — by design). Persists per stock.
**Test:** pick Position → drag from an entry up to a target → green-up / red-down box + the R:R pill; watch the
pill's PnL change as the price ticks; select it → three handles (entry / target / stop); drag the entry or target
handle to re-anchor; drag the body to move the whole box; open the pen panel to set **Qty** (defaults to 1) and
edit the stop; reopen the chart → persisted.
**Known v1 (by design):** the stop handle is visual-only — edit the stop via the pen panel (dragging near it moves
the whole box); dragging the target does NOT re-mirror the stop (direction is set once at creation). Qty=1 default,
so set a real share count in the panel for a meaningful PnL figure. Multi-target / risk-% are deferred to v2.

## 5. Bollinger Bands + VWAP  (commit `<pending>`)
**What:** two price-plot indicator overlays (like the moving averages), toggled from the chart's **MA/settings
overlay** under a new **"Indicators"** heading. Bollinger = SMA-20 middle ± 2σ envelope (three lines, the envelope
a touch lighter); VWAP = cumulative volume-weighted average price line (magenta). Both **off by default**,
session-only (no persistence).
**Test:** open the chart settings/MA overlay → flip **Bollinger Bands** on → the middle + upper/lower envelope draw
over the candles; flip **VWAP** on → the magenta VWAP line draws; toggle each off → it disappears. Pan/zoom → the
lines track the candles (warmup for Bollinger is the first 20 bars). Band-fill + intraday VWAP session-reset are v2.

## 6. RSI  (commit `<pending>`)
**What:** the RSI momentum indicator in its own **opt-in sub-pane** below the chart (like the Fear/Greed strip) —
a fixed 0–100 scale with **30 / 70 guide lines** + oversold(<30)/overbought(>70) zone washes, a gold RSI line, and
a current-value pill. **Off by default;** toggle it in the chart settings "Indicators" section (next to Bollinger/VWAP).
**Test:** open chart settings → flip **RSI** on → a new pane appears at the bottom and the price plot shrinks to make
room; the RSI line rides 0–100 with the 30/70 bands. **Verify (the risky part):** the crosshair still tracks over the
candles and does NOT draw into the RSI pane; clicks in the RSI pane don't select candles/drawings (they still work in
the price/volume area). Toggle off → the pane disappears and the plot returns to full height. Needs >14 candles to warm up.

---
**★ ALL 6 render-wiring features shipped.** A follow-up "round 2" (per Kiesh) reworks Text (plain text + font-size
control), adds a **Comment** tool, realigns the rail groups, and moves Alert to a top-row button — tracked separately.
