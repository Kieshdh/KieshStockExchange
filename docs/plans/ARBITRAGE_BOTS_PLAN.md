# Arbitrage bots + platform profit account (§3.7)

Status: ✅ IMPLEMENTED — cohort + house shipped (`639ed78`), then the house was **redesigned to a
pure-profit account** (no inventory, no FX risk, EUR-depletion eliminated) + FX-desk session
telemetry + `Bots:Arbitrage:Enabled` kill-switch wired (`1117e34`) on branch
`feature/arbitrage-house-account`. Builds clean, 63/63 tests pass, soak-validated (house grows from
0 by spread only). **MERGED to `master` (`4eea513`) and DEPLOYED to prod (2026-06-09, fresh
nuke+reseed — arb cohort 20003-20007 seeded + live, house 20002 zeroed at start).** See
[[project_arbitrage_house_account]] for the full record (incl. the original counterparty-model
FX-risk finding that drove the redesign).

---

## 1. Goal & success metric

A small cohort (~3–8) of bots running a new `AiStrategy.Arbitrage` whose entire edge is balancing the
**same stock across its USD and EUR books**. Purpose:
1. **Realism** — keep the two-currency prices of a cross-listed stock coupled at the live FX rate
   (cross-listing parity), instead of the two books drifting independently.
2. **A self-funding, profit-making participant** — models real arbitrageurs and produces the
   platform's profit via the FX conversion spread.

**Success metrics:**
- Cross-listed stocks' USD vs EUR prices stay within ~the FX conversion spread of parity (the
  arbitrage closes gaps faster than sentiment opens them).
- The platform house account accrues steadily-positive P/L (the FX spread).
- **Conservation holds** (`ConservationProbe` / `ReservationAuditor` clean) — nothing minted/destroyed.
- **Value-drain bounded** — the arbitrage cohort + house account stay a small fraction of total market
  value (see §6); they must not slowly vacuum the market.

---

## 2. What already exists (build on this, don't rebuild)

- **Engine keys by `(StockId, CurrencyType)`** (§3.2 shipped) — separate USD and EUR order books per
  stock; cross-listed stocks have both (`CROSS_LISTED_STOCK_IDS` in `Tools/Config.py`).
- **`FxRateService`** (`KieshStockExchange.Server/Services/MarketDataServices/FxRateService.cs`):
  AR(1) mid-rate walker, `ConvertSpread = 0.001` (±0.1% around mid = 0.2% round-trip),
  `GetBidAsk(from, to) => (mid·(1−spread), mid·(1+spread))`. **The spread is computed but, per the open
  question, likely not yet credited anywhere — that lost spread becomes the house account's revenue.**
- **AIUser generation pipeline** (`Tools/Config.py` → `Person.py` → `ExcelLayout.py` → `AIUserData.xlsx`
  → `AIUserRow` → `PgDBService.Misc` → `KseDbContext` → EF migration → `ExcelSeedService`) — the proven
  path for adding per-bot params (just used for the §P6 tier + TP columns; mirror it).
- **Bot loop** (`AiTradeService`) + decision service (`AiBotDecisionService`) + settlement /
  conservation plumbing (`ConservationProbe`, `ReservationAuditor`, reservation ledger).
- **`AiStrategy` enum** = `{ MarketMaker=0, TrendFollower=1, MeanReversion=2, Random=3, Scalper=4 }`
  (`KieshStockExchange.Shared/Models/AIUser.cs`). Add `Arbitrage = 5`.

---

## 3. The opportunity & math

For each cross-listed stock, compare the cheap-currency **ask** against the expensive-currency **bid**
at the live FX rate. Realizing in EUR, per-share profit ≈ `bidEUR − askUSD · fx(USD→EUR)`, **net of the
FX conversion spread** paid when the bot rebalances its currency mix. Check both directions
(USD→EUR and EUR→USD). The "arbitrage rate" = that profit / notional; **only act when it clears the
spread** (so the trade is genuinely near-riskless).

---

## 4. Design

- **Selection.** Among stocks with a positive arbitrage rate, pick **probabilistically weighted toward
  the larger gap** (bigger diff → higher pick probability), with light per-bot jitter so the cohort
  doesn't all pile into one stock.
- **Execution.** MARKET orders only, both legs: market-buy in the cheap currency, market-sell in the
  expensive one — ideally a **paired round-trip in one decision** so no directional risk is carried
  between legs. (Open question: atomic round-trip vs two legs with bounded inventory risk.)
- **Inventory & patience.** May hold bought shares instead of closing immediately if the FX rate moved
  against the exit mid-flight, and wait for it to turn favorable ("hold and wait"). **Bounded max
  inventory per stock** so a bad hold can't grow unbounded.
- **Self-sustaining.** No cash injection (`BotCashInjector` excludes Arbitrage). Seeded with balances in
  **both** currencies; grows on realized arbitrage profit. As it accumulates one currency and depletes
  the other, it converts via the platform FX to re-arm — which is what funds the house account.
- **Run separately.** Route Arbitrage-strategy users out of the normal sentiment/decision path into a
  dedicated `ArbitrageDecisionService` (recommended over a separate background loop — reuse existing
  scheduling/settlement/conservation). Guard: arbitrage bots never enter the normal decision flow,
  sentiment bias, value anchor, overheat veto, or cash injection.

---

## 5. Platform house account (MANDATORY — the conservation sink)

A reserved/flagged **"house" account** (a `User` + `Fund` in each currency, neither a normal human nor
part of the random AI fleet) that accrues the **FX conversion spread** (and optionally a tiny per-trade
fee) on every conversion.

**This is mandatory, not optional, and must be built FIRST — before any arbitrage bot is enabled.** The
FX-rate profit MUST land in a real account or the conversion settlement won't balance and funds get
**destroyed** (a `CK_Funds` / `ConservationProbe` failure). The spread is a real transfer from the
converting party to the house; the arbitrage profit is a real transfer from the counterparties who
traded at the mispriced book prices — **nothing is minted.**

- Representation (open question): a reserved `UserId` vs a `Platform` flag column on `User`.
- Excluded from: bot decisioning, cash injection, retention prune, and the random AI fleet.
- Surfaced in admin as **platform P/L**.

---

## 6. Monitoring — value-drain guard (REQUIRED)

Arbitrage is a *profit-making* participant, so it must not slowly vacuum the whole market's value into
the cohort + house account.

- **Telemetry:** track the arbitrage cohort's + house account's **combined net worth as a fraction of
  total market value** over time (extend `BotEconomyTelemetry` / the balance harness alongside
  drift+depth).
- **Guard:** alert / throttle if that fraction climbs past a ceiling (target: cohort stays a small
  single-digit %).
- **Levers if it drains too fast:** shrink the cohort, raise the min-arbitrage-rate threshold (act only
  on bigger gaps), cap max inventory, or widen the act-threshold so most micro-gaps are left on the
  table.

---

## 7. Generation (/Tools)

- Add `AiStrategy.Arbitrage = 5` (Shared enum + any switch sites).
- Generate the **small fixed cohort** separately (NOT part of the random strategy draw —
  `STRATEGY_CHOICES` for the general fleet stays `(0–4)`). Modest per-bot stat jitter:
  decision interval, max inventory per stock, min-arbitrage-rate threshold, conversion cadence.
- Seed with **dual-currency balances**, cash-injection **disabled**, into `AIUserData.xlsx` + DB
  (mirror the existing per-bot-column pipeline; both workbook copies — client + server).
- Add the house account row(s) to the seed.

---

## 8. Conservation invariants (do not break)

- Both arbitrage legs settle through the normal engine as market orders → existing `ConservationProbe`
  / `ReservationAuditor` invariants apply unchanged. **Verify the FX spread is actually credited to the
  house account** (today it's likely implicit/lost in `GetBidAsk`) — that crediting is the new money-flow
  and the linchpin of conservation.
- No negative balances; cash + shares conserved across both currencies; reservation ledger nets to zero.
- House account + arbitrage cohort excluded from the random fleet, cash injection, and retention prune.

---

## 9. Open questions for Ultraplan

1. **Dedicated `ArbitrageDecisionService` vs a separate background loop** (recommend the former).
2. **House-account representation**: reserved `UserId` vs a `Platform` flag column.
3. **Atomic paired round-trip vs two legs** with bounded inventory risk.
4. **Where today's FX spread "goes"** before crediting the house — trace the `convert-internal` flow and
   confirm the fix point.
5. **Selection + thresholds**: pick-weighting curve, min-arbitrage-rate, max inventory, conversion
   cadence, cohort size — and the value-drain ceiling for §6.
6. Whether to add a tiny explicit per-trade house fee on top of the FX spread.

---

## 10. Constraints

- Per-bot params flow through the Excel pipeline (Config.py → xlsx → DB seed); regenerate **both**
  xlsx copies.
- Keep arbitrage bots fully out of the normal decision flow (sentiment, anchor, veto, injection).
- Build + verify the house account before enabling any arbitrage bot (else conservation fails).
- Client `appsettings` must never be committed with localhost/dev values; server appsettings is fine.
