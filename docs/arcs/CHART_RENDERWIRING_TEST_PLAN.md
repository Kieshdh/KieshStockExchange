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

<!-- feature commits append below: Position, Bollinger/VWAP, RSI -->
