# Arbitrage bots + platform profit account — implementation plan

Status: PLANNED (branch `feature/arbitrage-bots`). Global-planning entry: CLAUDE_NOTES.md Wave 4
item 18 / §3.7. This doc is the implementation-ready expansion.

---

## 1. Goal

Add a small cohort (~3–8) of bots running a new `AiStrategy.Arbitrage` whose entire edge is
**cross-currency arbitrage**: each stock trades in both a USD and a EUR book (engine already keys by
`(StockId, CurrencyType)`), and the two prices drift out of FX parity. An arbitrage bot buys a stock
in whichever currency is cheap (after converting at the live FX rate) and sells it in the expensive
one, pocketing the gap. Secondary effects we want:

1. **Cross-listing parity realism** — arbitrage pressure keeps `price_USD · fx ≈ price_EUR`, which is
   how real cross-listed instruments behave.
2. **A self-funding participant** — these bots never need cash injection; they compound their own
   profit.
3. **Platform profit** — every currency conversion pays an FX spread; that spread is routed to a
   dedicated **house account**, which becomes the platform's P/L line.

Non-goals: latency/queue modeling, triangular arbitrage (only 2 currencies exist), statistical
arbitrage between different stocks.

---

## 2. Existing infrastructure this builds on

| Piece | Where | Notes |
|---|---|---|
| Multi-currency books | engine keys by `(StockId, CurrencyType)` | §3.2 DONE. Each stock has independent USD + EUR order books. |
| Fundamental/listing | `StockListing` (`CurrencyType`, `SeedPrice`, `IsPrimary`) via `IStockService.GetListings(stockId)` | Not every stock is listed in both currencies — must check. |
| FX rate | `FxRateService` (`IFxRateService`) | AR(1) mid-walker, deterministic. `ConvertSpread = 0.001`. `GetBidAsk()` → `(mid·(1−spread), mid·(1+spread))`. `Tick(now)` walks it. |
| Currency convert | `CurrencyHelper.Convert`, `ConvertInternalCommand` / `/api/portfolio/convert-internal` | Client computes `converted = amount · bid`. **The spread is currently implicit/lost — no account is credited.** This is the hook for platform profit. |
| Bot loop | `AiTradeService.RunLoopAsync` → per-tick `ProcessBotsAsync` → `AiBotDecisionService` | `TradeInterval` ≈ 1s. Bots decide every `DecisionIntervalSeconds`. |
| Strategy enum | `AiStrategy { MarketMaker=0, TrendFollower=1, MeanReversion=2, Random=3, Scalper=4 }` (AIUser.cs) | Add `Arbitrage = 5`. |
| Conservation | `ConservationProbe` / `ReservationAuditor` | Total cash + shares must be conserved each pass; reservation ledger nets to zero. |
| Cash injection | `BotCashInjector` (hourly) | Must exclude Arbitrage + house account. |

---

## 3. The arbitrage math

For a stock `S` listed in both USD and EUR, with FX mid rate `r` (USD per 1 EUR — confirm direction
against `CurrencyHelper.Convert`), and books exposing best bid/ask per currency:

```
askUSD = best ask in the USD book   (price to BUY 1 share in USD)
bidUSD = best bid in the USD book   (price to SELL 1 share in USD)
askEUR, bidEUR = same for the EUR book
```

**Direction A — buy USD, sell EUR** (profit realized in EUR):
```
costEUR  = askUSD  converted USD→EUR  (what the USD spent is worth in EUR, at the conversion rate)
gainEUR  = bidEUR                      (what we receive selling in EUR)
profitEUR_per_share = gainEUR − costEUR
```

**Direction B — buy EUR, sell USD** (symmetric, profit in USD).

The **conversion uses the spread** (`GetBidAsk`), so `costEUR` already includes the platform's cut.
That means the arbitrage rate is only positive when the raw book gap **exceeds the FX spread** — i.e.
the bot only acts on opportunities that survive the platform's fee. The platform always wins the
spread; the bot wins the residual.

```
arbRate = profit_per_share / notional_per_share      (a small fraction, e.g. 0.004 = 0.4%)
```

Act only when `arbRate ≥ MinArbRate` (per-bot threshold, ~0.1–0.5% with jitter).

### Quantity
`qty = min( affordable in the buy currency, available depth at askBuy within a slippage cap,
            available depth at bidSell within a cap, per-bot MaxArbQty )`.
Cap by the **thinner** of the two books so neither market leg moves price past the edge. Use the
slippage cap so a multi-level sweep doesn't eat the whole profit.

### Cash flow & rebalancing
Buy leg consumes the buy-currency balance; sell leg credits the sell-currency balance. Repeatedly
arbing one direction drains one currency and accumulates the other. To re-arm, the bot **converts**
the surplus currency back via `convert-internal`, paying the spread → **house account credited**.
Net of conversions, the bot's total wealth (in a reference currency) rises by the realized edge.

---

## 4. Resolved design decisions

These were the open questions in §3.7; recommended resolutions:

1. **Dedicated decision service, routed from the existing loop** (not a separate background loop).
   Add `ArbitrageDecisionService`; in `AiTradeService.ProcessBotsAsync`, route users with
   `Strategy == AiStrategy.Arbitrage` into it and `continue` (skip the normal sentiment/decision path).
   Reuses scheduling, settlement, conservation, telemetry. Lower risk than a parallel loop.

2. **House account = a flagged reserved `User` + `Fund`s**, not a magic id. Add an `IsPlatform`
   (bool) column to `Users` (default false), one row seeded as the house. It has USD + EUR `Fund`s,
   no `Position`s, no `AIUser` row (so it's excluded from the bot fleet and from human-only features).
   Excluded from: bot decisioning, `BotCashInjector`, retention prune, leaderboards.

3. **Atomic round-trip per decision, with an optional inventory hold.** Default: both legs
   (market buy + market sell) in one decision so no directional risk is carried. If the sell leg
   can't fully fill at a profitable bid (book moved), the bot **holds** the residual shares as
   inventory and retries the close on later ticks when `arbRate` turns favorable again (the
   "hold and wait" behavior). Bounded by `MaxInventoryQty` per (stock,currency).

4. **FX spread is credited to the house on every conversion.** Today `convert-internal` gives the
   user `amount · bid` and the `(mid − bid)` difference vanishes. Change settlement so that difference
   (in the target currency, valued consistently) is credited to the house `Fund`. This is the single
   money-flow change that makes platform profit real and keeps conservation exact.

---

## 5. Data-model changes

### 5.1 `AiStrategy`
`KieshStockExchange.Shared/Models/AIUser.cs`: add `Arbitrage = 5`. Audit every `switch (Strategy)`
(decision service, sentiment bias, extreme-reaction style, scaler) — Arbitrage must hit a no-op/default
in all of them since arbitrage bots don't take the normal path. Grep: `AiStrategy.`.

### 5.2 House account
- `Users` table: add `IsPlatform BOOLEAN NOT NULL DEFAULT FALSE`. EF migration `AddPlatformAccount`.
- Seed one platform user (e.g. username `__house__`, `IsPlatform = true`) with USD + EUR `Fund`s at
  zero balance. Seed path: `ExcelSeedService` (or a dedicated seeder step) — does **not** come from
  the AI workbook; it's infrastructure, seed it directly so it exists even on a fresh DB.
- Accessor: `IAccountsService`/`PgDBService` helper `GetPlatformUserId()` (cached).

### 5.3 Arbitrage bot per-bot params (on `AIUser`)
New `decimal`/`int` fields (RequiredPrc where 0..1), seeded with light jitter:
- `MinArbRatePrc` — min edge to act (e.g. 0.001–0.005).
- `MaxArbQty` — per-round-trip share cap.
- `MaxInventoryQty` — max held-while-waiting inventory per stock.
- `ArbConvertThresholdPrc` — surplus-currency fraction that triggers a rebalance conversion.
Add to: `AIUser` model + `IsValidPercentages`, `AIUserRow` DTO + map, `PgDBService.Misc` columns,
`KseDbContext`, EF migration, `ExcelSeedService` readers (defensive defaults). Mirror exactly the
per-bot-prob pipeline already used for `StopProb`/etc. (see CLAUDE_NOTES §3.6 / the advanced-probs work).

---

## 6. Components to add / modify

### Add
- `Services/BackgroundServices/Helpers/ArbitrageDecisionService.cs` — the core. Per arbitrage bot per
  decision: scan listed-in-both stocks, compute `arbRate` both directions using book bid/ask + FX,
  pick a stock weighted by edge size (higher diff → higher probability), size the round-trip, submit
  two market orders, manage inventory, and trigger rebalance conversions.
- `Services/PortfolioServices/...` — extend the convert path to credit the house `Fund` with the spread.
- EF migrations: `AddPlatformAccount`, `AddArbitrageBotParams`.
- `docs/` telemetry: arbitrage P/L + house P/L columns (optional, mirror `BotEconomyTelemetry`).

### Modify
- `AIUser.cs` (enum + fields + validation).
- `AiTradeService.cs` — route Arbitrage users to `ArbitrageDecisionService`; construct it with config;
  exclude Arbitrage + house from the normal path.
- `BotCashInjector` — skip `Strategy == Arbitrage` and the house account.
- `RetentionService` — never prune the house account's rows (it's not human, but it's permanent).
- `AIUserRow` / `PgDBService.Misc` / `KseDbContext` / `ExcelSeedService` — the new per-bot fields.
- `convert-internal` controller + settlement — spread → house.
- Admin: surface platform P/L (house fund balances) + arbitrage-cohort stats.

### Tools (generation)
- `Tools/Config.py` — an `ARBITRAGE_COHORT` block: count (e.g. 5), and ranges for the new params with
  light jitter. Arbitrage bots are a **fixed appended cohort**, not part of `STRATEGY_CHOICES (1–4)`.
- `Tools/Person.py` — emit arbitrage users (strategy=5, cash-injection off, both-currency seed balances).
- `Tools/ExcelLayout.py` — columns for the 4 new params (same order as the row writer).
- Regenerate `AIUserData.xlsx` (both client + server copies) with the skip-layout/skip-userinfo fast
  mode the user flagged.

---

## 7. Per-tick arbitrage algorithm (ArbitrageDecisionService)

```
for each arbitrage bot due to decide this tick:
  # 1. close pending inventory first (patience)
  for each held inventory lot:
     if closing now yields arbRate >= 0 (or >= small floor): market-sell to close; book profit
     else keep holding (bounded by age/inventory cap)

  # 2. find the best fresh opportunity
  candidates = []
  for each stock listed in BOTH currencies on this bot's watchlist (or all):
     compute arbRate_A (buy USD/sell EUR) and arbRate_B (buy EUR/sell USD) from live bid/ask + FX
     best = max(arbRate_A, arbRate_B)
     if best >= bot.MinArbRatePrc: candidates.add((stock, direction, best))
  if candidates empty: maybe rebalance currencies (step 4); return

  # 3. pick weighted by edge (higher diff -> higher probability), light per-bot jitter
  pick = weighted_choice(candidates, weight = edge^k)
  qty  = min(affordable, depthBuy@cap, depthSell@cap, bot.MaxArbQty)
  submit market BUY in cheap currency for qty
  submit market SELL in expensive currency for the filled qty
  if sell underfills: hold residual as inventory (step 1 will retry)

  # 4. rebalance: if surplus currency fraction > ArbConvertThreshold, convert surplus back
  #    (pays spread -> house account credited)
```

Weighting: `weight = edge^k` with `k≈2–3` strongly favors the biggest gaps while still occasionally
taking smaller ones (matches "higher probability of higher diff stocks").

---

## 8. Conservation analysis (must hold)

The whole point is no money is minted. Per the existing invariants:

- **Each market leg** settles through the normal engine: shares move buyer↔seller, cash moves
  seller↔buyer at the trade price. Zero-sum per fill. The arbitrage profit is a **real transfer**
  from the counterparties who posted the mispriced book orders to the arbitrage bot. ✓
- **The conversion** moves `amount` out of the bot's surplus `Fund` and `amount·bid` into its deficit
  `Fund`; the `(mid−bid)` residual is credited to the **house** `Fund`. Total currency-value
  conserved (the spread is a transfer, not a loss). ✓ — this is the one new transfer to wire.
- **No cash injection** for these bots, so they don't add nominal money. ✓
- **Inventory** held is just a normal long `Position` (reserved/owned shares) — already covered by
  `Position`/`Fund` invariants. ✓

Validation: run `ConservationProbe` + `ReservationAuditor` over a soak with arbitrage bots on; assert
total cash (summing the house account) + shares conserved, ledger nets to zero, no negative balances.
Add a unit test that a single round-trip + rebalance conserves total value and credits the house by
exactly the spread.

---

## 9. Generation details

- Count small (3–8). They need **both-currency starting balances** (so they can arb in either
  direction immediately) — set seed USD + EUR funds in Person.py for the cohort.
- Light jitter only (user: "a bit of jitter, not much needed") on the 4 params + decision interval.
- Cash injection **off**.
- Give them a **broad watchlist** (or all stocks listed in both currencies) so they can scan the
  whole cross-listed universe, unlike normal bots that focus a small watchlist.

---

## 10. Testing & rollout

1. Unit: round-trip + rebalance conservation; arbRate sign/threshold; weighted selection distribution.
2. Integration: seed house + a few arbitrage bots on a scratch DB, run a short soak, assert:
   - cross-currency price gaps shrink over time (parity pressure),
   - house USD+EUR balances grow monotonically (platform profit),
   - arbitrage bots' total value (ref currency) grows without injection,
   - ConservationProbe clean, reconcile clean.
3. Feature flag: `Bots:Arbitrage:Enabled` (default off) so the cohort can be toggled independently of
   the main fleet. `Bots:Arbitrage:Count`, weighting `k`, and floors as config.
4. Rollout: land behind the flag, soak on scratch, then enable.

---

## 11. Remaining open questions

- FX rate direction convention (`r` USD-per-EUR vs EUR-per-USD) — pin against `CurrencyHelper.Convert`
  before coding the math.
- Should the house also take a tiny per-trade fee, or only the FX spread? (Start: spread only.)
- Atomicity of the two market legs under concurrency — submit both within one settlement batch, or
  accept the inventory-hold fallback as the safety net? (Start: inventory-hold fallback; revisit if
  conservation/parity needs tighter coupling.)
- Do we want arbitrage bots to also post passive limit walls at parity (provide liquidity) or stay
  purely taker? (Start: taker-only per the spec; could add a passive parity-maker mode later.)
