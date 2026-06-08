# Market balancing — make bot-driven prices lively but bounded

Status: IN PROGRESS on `feature/p6-bot-soak`. This doc is the refined design for Ultraplan to harden,
then implement. It captures the root-cause findings (with data), what's already validated in code,
the planned features, and proposed improvements.

---

## 1. Goal & success metric

The bot fleet must produce **lively but bounded** prices: charts that move and trend interestingly,
but where no stock runs to absurd levels (we have seen +29,000% and −99%).

**Metrics (the only trustworthy ones — see §3 on variance):**
- **median |drift|** across stocks vs fundamental — the *typical* stock. Target ~**±5–10%** over a
  session (the exact band is the user's lively-vs-bounded taste call).
- **escape counts**: `# stocks beyond ±50%` and `# beyond ±100%`. Target: **0 beyond ±100%**, only a
  small handful (realistic movers) beyond ±50%.
- stddev / max are **outlier-dominated and unreliable** — do not tune on them.

Conservation is non-negotiable: every change must keep `ConservationProbe` / `ReservationAuditor`
clean (cash + shares conserved, reservation ledger nets to zero).

---

## 2. Root-cause findings (from experiments E0–E14)

1. **No fundamental anchor (the core bug).** Bots had no notion of fair value — MeanReversion reverts
   to a short EWMA that *tracks the drifting price*; sentiment is zero-mean noise. So price was a
   **driftless momentum walk → unbounded**. Liquidity depth changes the *speed* of drift, not the
   bound (deeper books were the same or worse).
2. **Stop cascades cause ~100% of the extreme escapes.** With advanced orders OFF the market is fully
   bounded (median ~1.5%, max +0.5%, zero escapes); ON, a rising price trips buy-stops → each fires a
   market buy → lifts price → trips the next = a short squeeze to +29,000%. Confirmed by E12.
3. **Stop *fires* were uncapped / loosely capped.** Short-bracket stop-losses are buy-stops firing
   with `BracketSlippagePct = 5%` each — 5% per fire compounds into the squeeze. Lowering to 0.5%
   helped (max +4,786% → +1,963%) but didn't fully stop it; long sell-stops fire uncapped (`slippage
   = null`).
4. **Variance is large and tail-dominated.** The *same* config gave stddev 44 vs 142 across runs
   purely on whether a stock escaped. Escapes are *systematic* (3/3 baseline runs had 6–10 beyond
   ±50%), not rare. Judge by median + escape counts, never stddev/max.
5. **Liquidity tweaks backfired when naive:** uniform-widening thinned the touch (worse); disabling
   the prune let orders persist but on the wrong side and clogged bots (catastrophic, +7,254%);
   concentrating the anchor via stock selection over-pumped (catastrophic, +15,664%). Dilution/damping
   were actually stabilizing.

---

## 3. What is already validated and IN the code (`feature/p6-bot-soak`)

These are committed-as-WIP and tested via the harness (§6):

- **OU-ring sentiment** (`BotSentimentService.cs`, full rewrite): continuous mixture of AR(1) scores,
  each updated every tick, timescale via `α = exp(−Δt/τ)`, amplitude decoupled via `σ·√(1−α²)`.
  Per-stock ring τ = 20s/90s/6m/30m/3h; global ring τ = 10m/1h/6h. Cut dispersion ~3×. Public surface
  unchanged. Replaced the old cadence model that froze slow factors within a session.
- **Value anchor** (`AiBotDecisionService.ChooseOrderType`): a buy/sell-probability tilt toward each
  stock's fundamental (seed listing price via `IStockService.GetListings().SeedPrice`, cached),
  averaged over the watchlist. **This is the fix that bounds the typical stock to ~5%.** Config
  `Bots:ValueAnchor:Strength` (0.40) / `Scale` (0.15). Stronger over-pumps (keep ≤ ~0.45).
  - Gated-off experiment: `TargetSelection` (concentrate via stock selection) — **destabilizing,
    leave false**.
- **Value-band veto** (`ComputeOrderAsync`): refuse to *buy* >`OverheatCap` above fundamental or
  *sell* >cap below (`Bots:ValueAnchor:OverheatCap` = 0.50). Tightens the bulk.
- **No true market orders** (`ChooseOrderType` + `ApplyExtremeReaction`): bots place ONLY
  slippage-capped market orders at a low cap. `EffectiveSlippage(user) = min(per-bot,
  Bots:MarketSlippagePrc=0.003)`. No single decision-path order sweeps far.
- **Bracket stop slippage** lowered 5% → 0.5% (`Bots:Advanced:BracketSlippagePct`).
- **Liquidity multipliers** (`Bots:Liquidity:OffsetMult`/`MaxOpenMult`) — present but `MaxOpenMult` is
  non-binding (bots hold ~29 orders/stock, far below cap); leave at 1.

Net result with all of the above: **typical stock ~1–5%, but 1–2 stocks still escape via residual
stop cascades** (long sell-stops uncapped; chained buy-stops even at 0.5%). That residual is what the
planned work below must close.

---

## 4. Planned features (designed, not yet built) — the heart of the Ultraplan work

### 4.1 Bound ALL stop fires (finish the impact cap)
- Every bot stop (buy-stop *and* sell-stop, bracket and standalone protective/trailing) must fire with
  a **low slippage cap** (e.g. the same `MarketSlippagePrc`), not `null`. Currently long-bracket
  sell-stops and standalone stops fire uncapped → downward cascades. Make `slippage` a low value in
  every bot stop construction in `ComputeAdvancedDecisionAsync` and the protective-stop path.
- **Stop-promotion circuit-breaker (new):** cap how many stops a single stock can promote per short
  window / per tick, so a chain reaction can't fire 50 stops in one cascade even if each is small.
  This is the hard backstop that *guarantees* no squeeze regardless of tuning.

### 4.2 Tiered limit orders: Close / Mid / Far
- Each limit order is categorized by distance-from-price into **Close** (≈0–1%), **Mid** (≈1–5%),
  **Far** (≈5–25%). A bot picks a tier per order (probabilities, light jitter) so the book is dense at
  the touch *and* carries a standing ladder of far walls.
- **Per-tier `Min%`/`Max%` are per-bot Excel-generated** (mirror the existing per-bot-prob pipeline:
  `AIUser` fields → `AIUserRow` → `PgDBService.Misc` cols → `KseDbContext` → EF migration →
  `ExcelSeedService` readers → `Tools/Config.py` ranges + `Person.py` + `ExcelLayout.py`). Regenerate
  `AIUserData.xlsx` (both client + server copies; use the layout/userinfo skip for speed).
- **Stop distance bounded inside the Far walls**: stops get their own per-bot `Min%`/`Max%` config,
  constrained `MaxStopDistance < MinFarLimit`, so a fired stop's (now slippage-capped) order runs into
  the far walls and is absorbed rather than triggering the next stop.

### 4.3 Smart, tier-aware prune (replaces the current harmful prune)
The current `AiBotStateService.PruneWorstOrdersAsync` (3-min stale-age + distance-cull beyond
2×offset) *destroys* far walls. Replace with two rules, run on the existing worst-first sort:
1. **Straggler cull — every sweep, unconditional:** cancel any order whose *current* distance exceeds
   the bot's `FarMax%` (drifted past its own band — dead weight, "terrible" orders).
2. **Far value-budget mass-prune — conditional:** classify orders by *current* distance (so a Mid that
   drifts out is counted as Far). Sum the bot's far-order value `Σ(price×remaining)`. If it exceeds a
   per-bot **`FarBudget`** (fraction of portfolio, Excel-generated), **cancel worst-first (furthest)
   down to ½·FarBudget** (hysteresis so it doesn't re-trigger each cycle).
- **Close is never pruned** (it churns/fills fast). Mid is unbudgeted but counts toward Far once it
  drifts there. This bounds each bot's capital/book (avoids the E5 explosion) while letting walls
  persist.

---

## 5. Proposed improvements (my refinements — for Ultraplan to weigh/harden)

1. **Slowly-drifting fundamental** (instead of a fixed seed). Give each stock a fundamental that
   random-walks *slowly* (e.g. an OU/GBM at a multi-hour-to-day timescale). The value anchor tracks
   `fundamental(t)` rather than the fixed seed. **This adds genuine long-horizon liveliness** (stocks
   can trend over a session) **while staying bounded** (anchored to a slowly-moving center). Likely
   the single biggest liveliness win without reintroducing runaway. The drift must be slow + bounded
   so it can't itself run away.
2. **Per-stock personality profiles.** Assign each stock a volatility/liveliness class (calm
   blue-chip ↔ volatile growth/meme) driving its anchor strength/band, sentiment amplitude, and
   fundamental-drift rate. Makes the *market* look varied and realistic rather than 70 uniform stocks.
3. **Liquidity-aware order sizing.** Cap a market order's size to a fraction of the *currently resting*
   opposite-side depth, so no order can sweep more than N% of the book regardless of slippage settings.
   A structural anti-sweep that complements the slippage cap.
4. **Include some MarketMaker bots.** `STRATEGY_CHOICES` is currently `(1–4)` — no MMs generated. A
   slice of MM bots quoting tight two-sided keeps the touch liquid and the spread sane, strengthening
   the book the cascades exploit.
5. **Fundamental-relative stop placement / de-clustering.** Place protective stops relative to
   fundamental (not just entry) and/or jitter them so they don't pile at the same trigger level and
   chain-fire.

---

## 6. Experimental harness (how to validate)

`scripts/kse-balance-{setup,run}.ps1` + `balance-drift.sql` + `balance-depth.sql`:
- `setup` seeds a **bots-off Postgres template** `kse_soak_seed` once.
- `run -Label X -Minutes N` clones it to `kse_soak` (fast `CREATE DATABASE … TEMPLATE`), launches the
  built server exe (MUST set `KSE_DB_CONNECTION_STRING=…kse_soak` — default is the real `kse`!;
  `-WorkingDirectory` the Server project for appsettings), samples **drift + book depth** every
  interval into `logs/balance-results.csv`.
- Server tick ≈ 1 Hz; warm-up ~10s on the pre-seeded template. Postgres timestamps are UTC; logs are
  local (CEST = UTC+2). DB container `kieshstockexchange-postgres-1` (kse/kse-dev).
- **Judge by median + escape counts over a 15-min run; ideally repeat 3× (variance).**

---

## 7. Open questions / decisions for Ultraplan

- **Drifting fundamental**: process (OU vs slow GBM), timescale, and how the value anchor reads it
  (replace `Fundamental()` seed lookup with a live per-stock fundamental service).
- **Stop circuit-breaker** threshold (stops/stock/window) and whether it halts or just throttles.
- **Tier boundaries** (Close/Mid/Far %), per-tier placement probabilities, and `FarBudget` sizing.
- **Liquidity-aware sizing**: fraction-of-depth cap value, and where to enforce (decision vs engine).
- **Conservation**: ensure the new prune (mass cancel) and bounded stops keep reservations exact;
  re-verify with `ConservationProbe` after each.
- Whether to keep advanced orders' per-bot probabilities as-is or retune given the new stop bounds.

---

## 8. Constraints / invariants (do not break)

- Conservation: cash + shares conserved; reservation ledger nets to zero; no negative balances.
- Per-bot params flow through the Excel pipeline (Config.py → xlsx → DB seed); both xlsx copies.
- Bots place plain orders via `OrderEntryService`; advanced via the entry/arm route. Don't move
  business logic into the loop.
- Keep the public surface of `BotSentimentService` / decision service stable where possible.
- Client `appsettings` must never get committed with localhost/dev values (server appsettings here is
  fine to tune).
