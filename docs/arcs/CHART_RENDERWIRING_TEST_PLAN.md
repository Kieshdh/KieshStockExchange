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

<!-- feature commits append below: Text, Fibonacci, Position, Bollinger/VWAP, RSI -->
