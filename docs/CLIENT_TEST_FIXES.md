# Client UI Test — Fixes Log (Wave 10 re-run, 2026-06-23)

Running log of observations from Kiesh's manual client UI test (full A–J against `http://localhost:5000`,
client repointed there, admin/hallo123). **Workflow:** Kiesh logs observations as he tests → I add each here with a
root-cause hypothesis + proposed fix (researched as it lands) → at the end, **one autonomous FIXING ROUND**
(implement → build → `dotnet test` → commit/push) so Kiesh can re-test clean.

**Fix conventions (per repo CLAUDE.md):** identify the LAYER first (View / ViewModel / Model / Service / Helper /
Data); minimal targeted edits; MVVM-clean (bindings/commands/observable props over code-behind); shared XAML styles;
preserve model invariants + conservation (CK=0); multi-table writes via `RunInTransactionAsync`; do NOT touch `/Tools`.

---

> **STATUS 2026-06-23 eve:** Batch-1 UI fixes **I1–I8 all IMPLEMENTED + BUILT (0 err) + COMMITTED** — I2/I3/I5/I6/I8
> `e2ff631`, I4/I7 `abd2969`, I1 `82f4cf1`. Marketcap (I4 column) + 20k-bots (T1) folded into **one DB/seed feature**
> (`SharesOutstanding` + house holds the rounding remainder + share-conservation test), IN PROGRESS. S1 arb/FxRate =
> harvesting the running RC soak. ⚠️ I1 (chart live candle) is the one fix not runtime-verified — eyeball it.

## 📋 Issues — batch 1 (2026-06-23, Kiesh client test)

### A) UI fixes (the autonomous round)
| # | Area | Symptom | Root-cause hypothesis | Fix (layer) | Status |
|---|---|---|---|---|---|
| **I1** | Trade/chart | Candle chart doesn't update live — only ~every 5s (top-bar price updates multi/sec). | Chart's last-candle/price update is on a slow refresh, not the fast live-price feed the top bar uses. | VM/Service — route chart updates off the same fast price feed as the top bar. | researching (agent A) |
| **I2** | MarketPage + orderbook cadence | MarketPage price doesn't update consistently; the fast top-bar tempo should apply to chart, orderbook, marketpage. (Orderbook ~1s is OK-ish.) | Same root as I1 — a fast price stream consumed only by the top bar; other surfaces poll slower. | VM/Service — unify all price-display surfaces on the fast feed. | researching (agent A) |
| **I3** | Orderbook currency | On a EUR stock, orderbook header says "Price (EUR)" but rows render with "$". Values are EUR, symbol wrong. | Orderbook row price uses a hardcoded/USD formatter instead of the active stock currency. | View/Converter/VM — format with the active stock's currency. | researching (agent C) |
| **I4** | MarketPage table | Want sortable columns: default by volume; sort on symbol, name, change, volume, + NEW **marketcap** column. | Feature — needs column-sort + a marketcap column (shares×price?). | View+VM — sortable headers + MarketCap column. | researching (agent C) |
| **I5** | Top Gainers/Losers | Top Losers shows the SAME stock twice (META 1st & 3rd, identical). User: "two instances of that stock." | Each stock has two StockListings (USD+EUR) → gainers/losers not deduped by stock. | VM/source — dedupe by stock (per active tab/currency). | researching (agent B) |
| **I6** | Top Gainers/Losers | Should react to All/USD/EUR/Watchlist tabs (show only that tab's entries). | Feature — gainers/losers ignore the active tab filter. | VM — filter source by the active tab. | researching (agent B) |
| **I7** | Portfolio | Allocation circle (donut) not showing; worked before. User suspects empty account. | Likely empty/zero-data guard or a binding regression. | TBD. | **AWAITING Kiesh** (trade, then recheck) |
| **I8** | Bot dashboard | Always starts "20000/20000" though the chart shows fewer bots trading. | Count shows the configured max, not the live scaler `ActiveBotCap` (ramps up over ~1min). | VM — bind to live active count + true total. | researching (agent C) |

### B) Soak / measurement (separate from the UI round)
- **S1 — Arbitrage effectiveness + FxRate stability — HARVESTED (RC soak `kse_rc`, 2h).**
  - **FxRate (EUR/USD) swings too much** (the real issue): range 1.0545–1.0959 = 3.84% of base 1.08; std ~1.0%; max
    ±2.37% from base; per-min |Δ| mean 0.248% / max 0.550%. Real EUR/USD moves ~0.25%/DAY → this is ~50–100× too hot.
  - **Cross-currency PARITY is actually GOOD** (arb IS effective): EUR/USD price ratio across 20 dual-listed stocks
    = 0.9345 ± 0.74%, vs ideal 1/FxRate = 0.9285 → only ~0.6% systematic offset (≈ the 0.1%-each-way ConvertSpread).
    FX desk actively converting + capturing spread. So Kiesh's "arb not active enough" doesn't hold — parity is tight;
    the *FxRate jumping* is what makes prices look unaligned. **No arb-size change needed.**
  - **Knob:** `FxRateService.cs` AR(1) walker (mirror `Tools/Config.py` FX_*): `Amplitude=0.005` (per-step noise = the
    volatility), `Alpha=0.92` (mean-rev), `RateBand=0.20` (clamp ±20%, too loose). **Recommend Amplitude→0.0015 (~3×
    damp) + RateBand→0.05**; config + server-restart, NO reseed. AWAITING Kiesh's chosen damping number (Q3).
  - **Bounce-mid DEPLOY BASELINE (bonus from this soak):** CLOSE ret_acf −0.41 / mid −0.20, clustering 0.15, drift
    −1.39%/2h (in budget), CK=0/CONS=0 throughout, 781k trades. The pre-deploy reference for the prod cutover.

### C) Tools / seed (attended — explicitly authorized by Kiesh)
- **T1 — 20,000 bots TOTAL (not 20k + arb + house + MM on top).** Cohorts (arb, house, MM) should be a SLICE of the
  20k, not additive. Fix: `Tools/GenerateAIUsers.py` population composition. Population-replacing → attended + reseed
  + distributional parity check. Bundle with the EUR seed-rebalance Tools task. Related to I5 data-model question.

---

## 🗂 Known-open candidates (pre-existing low bugs — include in the round only if you want)
Pre-researched from the doc audit; each needs a quick code-confirm during the round.
- **D4 — connection/transaction-leak audit** (Data). `PgDBService` runtime path: confirm every `NpgsqlConnection` /
  transaction is in an `await using` scope (none leaked on early-return/exception). Low risk, mechanical.
- **E4 — DTO-bind review** (Server controllers / Shared DTOs). Raw-entity model-binds are already admin-only; sweep
  remaining endpoints/views that bind raw entities from input → introduce DTOs. Low risk.
- **B1 — 401-handler / re-login** (client Services + server). Token expiry currently yields a non-graceful state.
  Fix: a `DelegatingHandler` on the `KSE.Server` HttpClient that on 401 attempts a refresh-token exchange (Phase-6)
  or routes to re-login. Medium effort (ties to refresh-token infra) — likely its own task, not this round.

---

## 🔧 Fixing-round runbook (when Kiesh says "go")
1. Group issues by file/layer; order low-risk → high-risk.
2. Implement each (minimal, MVVM-clean, correct layer).
3. Build server + client (`net9.0-windows`) + tests; run `dotnet test` (gate: green).
4. Commit (focused per-fix or sensibly grouped) + push.
5. If any server-side fix: restart the client-test server (:5000, kse_clienttest). Tell Kiesh to rebuild the client.
6. Update each issue's Status → DONE (commit ref). Hand back for re-test.
