# Marketcap + Shares-Outstanding + 20k-total-bots — implementation plan (2026-06-23)

Folds three of Kiesh's client-test asks into one **DB/seed feature**:
- **Marketcap column** on the market page (I4's deferred part).
- **`SharesOutstanding` per stock** in the DB, set to a **round number**, with the **house holding the rounding
  remainder** (house doesn't trade — just absorbs the round-up so marketcap is a clean number).
- **20,000 bots total** = 19,995 normal + 5 arbitrage; **house is NOT a bot** (no Profile row).
- **Share-conservation test**: Σ(Position.Quantity for a stock) == Stock.SharesOutstanding, and settlement never
  creates/destroys shares. *(The settlement half is already built + committed: `ShareConservationTests.cs`.)*

Why staged (not done in the autonomous burst): it spans server (dual-persistence model + EF migration) + `/Tools`
seed-gen (with an **id-shift**) + client + a **population-replacing reseed** that feeds prod and that I can't
runtime-verify (the client marketcap column). Best executed in one pass, ideally with Kiesh able to eyeball the column.

## Data-model facts (from the explore pass)
- Shares = `Position.Quantity` (int), **one pool per Stock**, currency-agnostic. META-USD and META-EUR share the
  same StockId → same share pool.
- `Stock` has no shares field today. House (seed) starts with **0 shares**. Arbitrage cohort starts with **0 shares**.
- `ConservationProbe` already asserts per-batch net share delta = 0 per stock (no creation/destruction in trading).
- ⚠️ **House/arb ids are computed from `NUM_PEOPLE`** (`house_id = NUM_PEOPLE + HOUSE_USER_ID_OFFSET` = 20002 today)
  and referenced in **5 server files** (AiTradeService, AiBotStateService, BotEconomyTelemetry, UserPortfolioService,
  FxDeskTelemetry). Changing `NUM_PEOPLE` 20000→19995 shifts house→19997 + arb→19998..20002. **Verify those 5 refs
  read the id from config (offset), not a hard-coded 20002, before reseeding** — else they break.

## Build steps
### 1. Server model (additive, safe)
- `Stock.cs` (Shared): `public int SharesOutstanding { get; set; }` (clamp ≥0).
- `StockRow.cs` (persistence): `[Column("SharesOutstanding")] public int SharesOutstanding { get; set; }` + both
  `StockMapper.ToDomain`/`ToRow`.
- `KseDbContext.cs`: convention maps `int` — no explicit config needed.
- EF migration `AddSharesOutstanding` (int, default 0) via `dotnet ef migrations add` (mirror `AddTransactionMidPrice`).
  ⚠️ run EF tooling when NO server process is holding `Server/bin` (it rebuilds + locks).

### 2. `/Tools` seed-gen
- `GenerateAIUsers.py`: `NUM_PEOPLE = 19995` (→ 19,995 normal + 5 arb = 20,000 bots; house separate).
- After all bot+arb holdings are generated, per stock: `bot_total = Σ holdings`; pick a **round target ≥ bot_total**
  (e.g. round up to a clean 100k/1M); `SharesOutstanding[sid] = round_target`; **house holding[sid] = round_target −
  bot_total** (house absorbs the remainder → Σ all holdings == SharesOutstanding exactly).
- Write `SharesOutstanding` into the Stocks sheet (`ExcelLayout.py`) + the server seed loader reads it into `Stocks`.
- Keep determinism notes in mind (Person.py uses unseeded random → regen replaces the population; validate distributionally).

### 3. Client marketcap column
- `LiveQuote.cs` (Shared): `[ObservableProperty] int _sharesOutstanding;` + computed `MarketCap = LastPrice ×
  SharesOutstanding` (+ a `MarketCapDisplay`). Populate `SharesOutstanding` where the server builds the quote
  (stock metadata path) so it flows to the client.
- `MarketRow` (MarketViewModels.cs): `MarketCap` numeric + `MarketCapDisplay`; set in FromQuote/UpdateFrom.
- `MarketSortColumn`: add `MarketCap`; `SortDesired` case `a.MarketCap.CompareTo(b.MarketCap)`; add a
  `MarketCapHeader`; add the MARKETCAP column + tappable header to `MarketPage.xaml` (extend the column grid).

### 4. Test (settlement half DONE; add the seed-invariant half)
- ✅ `ShareConservationTests.cs` — settlement moves shares without creating/destroying (committed).
- TODO after reseed: a test/probe asserting Σ(Position.Quantity per stock) == Stock.SharesOutstanding on the seeded DB.

### 5. Reseed + validate (the high-stakes step — Kiesh's nod)
- Regenerate `AIUserData.xlsx` (Tools) → rebuild `kse_soak_seed` → run a short soak: **CK=0**, share-conservation
  test passes, drift within budget, marketcap column populates in the client (eyeball).
- Sequencing vs the bounce-mid prod deploy: decide whether to bundle (deploy carries bounce-mid + new 20k/marketcap
  seed) or ship bounce-mid first then this. The reseed shifts ids — re-confirm the 5 server refs.
