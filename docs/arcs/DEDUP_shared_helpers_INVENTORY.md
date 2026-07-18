# DEDUP + Simplification Inventory — Cross-cutting / Helper / Shared layer

READ-ONLY discovery (2026-07-18). No code touched. Scope: `KieshStockExchange.Shared/` (Models,
Service interfaces, Helpers) + every `Helpers/` folder in client & server, with special attention to
CLIENT↔SERVER cross-cutting duplication (formatters, validators, rounding, parsing, time, math, the
client `ApiDataBaseService` vs server persistence surface).

Safety classes (per `DEDUP_ARC_PLAN.md`):
- **PROVABLY-SAFE** — exact duplicate / pure function / dead code / mechanical / trivial. Pass-1 eligible.
- **NEEDS-CARE** — subtle differences in rounding / error-handling / edge-cases / culture / ordering; must
  diff logic. Pass-2 proposal.
- **CK-TOUCHING** — money / reservation / settlement. Owner-gated; propose only, never autonomous.

Headline: the **canonical money surfaces are already well-centralized.** Currency *formatting*
(`CurrencyHelper.Format/FormatCompact/FormatForEdit`) and money *rounding*
(`RoundMoney/RoundPrice/Notional`) each have effectively one source of truth — inline `"{0:C}"` /
`Math.Round(x,2)` money code does **not** exist in the app paths (only in tests / telemetry). The real
targets are: a **drifted twin `ReservationMath`**, **repeated percent formatters**, **duplicated
cost-basis lot math**, **single-culture parsing that bypasses `ParsingHelper`**, and internal overlap
inside `OrderValidator`.

---

## Prioritized candidate table

| # | Candidate | Safety | CK-adj? | Est. value |
|---|-----------|--------|---------|-----------|
| 1 | `ReservationMath` duplicated client+server, **drifted** | CK-TOUCHING | yes (reservations) | High — real drift-risk on stop-order reservations |
| 2 | Repeated **signed-percent formatter** (4–5 copies) → `CurrencyHelper`/`PercentHelper` | PROVABLY-SAFE | no | Med — ~10 LOC + consistency |
| 3 | `AverageCostBasis`/`PositionPnl` lot-math re-implemented in `AccountViewModel` | NEEDS-CARE | no | Med — ~40 LOC, twin drift |
| 4 | `OrderValidator.ValidateInput` vs `ValidateNew` overlapping rule blocks | NEEDS-CARE | yes (order rules) | Med — ~60 LOC, rule-drift |
| 5 | Single-culture `int.TryParse` bypassing `ParsingHelper.TryToInt` (~15 client files) | NEEDS-CARE | no | Med — behaviour divergence (no invariant fallback) |
| 6 | Admin date-range logic **byte-identical** in two table VMs | PROVABLY-SAFE | no | Low-Med — exact-dup extract |
| 7 | Server telemetry money formatters hand-roll `$`+`N2` (3 sites) | NEEDS-CARE | no | Low — JPY-wrong, telemetry-only |
| 8 | `ChartGeometry.AlignToStep` re-derives `TimeHelper.FloorToBucketUtc` | NEEDS-CARE | no | Low — one flooring algorithm, two copies |
| 9 | Order-type **string→enum**: already done; residual = `Status` validation list duplicated | PROVABLY-SAFE (residual only) | no | Low — see full assessment |
| 10 | `ParsingHelper` is `class` with only static members → `static class` | PROVABLY-SAFE | no | Trivial |
| 11 | Two currency-conversion systems (`CurrencyHelper.Convert` vs `IFxRateService`) | NEEDS-CARE | no | Low — inconsistency note |
| 12 | `BracketGeometryValidator` (pure) lives server-side; client re-checks a subset | NEEDS-CARE | yes (order geometry) | Low-Med — move to Shared |
| 13 | `ApiDataBaseService` per-entity writeback lambda repeated ~14× | PROVABLY-SAFE | no | Low — cosmetic |
| 14 | `DateTime.UtcNow` bypassing `TimeHelper.NowUtc()` (prod paths) | NEEDS-CARE | no | Low — test-mockability |
| 15 | `InvertedBoolConverter` duplicates a toolkit built-in | PROVABLY-SAFE (pending dep check) | no | Trivial |

Count by class (primary): **PROVABLY-SAFE 5** (#2, #6, #9-residual, #10, #13; #15 conditional) ·
**NEEDS-CARE 8** (#3, #4, #5, #7, #8, #11, #12, #14) · **CK-TOUCHING 1** (#1).

---

## Full findings

### 1. `ReservationMath` — duplicated client + server, DRIFTED  · CK-TOUCHING · CK-adjacent
- Client: `KieshStockExchange\Services\MarketEngineServices\Settlement\ReservationMath.cs`
- Server: `KieshStockExchange.Server\Services\MarketEngineServices\Settlement\ReservationMath.cs`

Same namespace + class name + core signatures (`ReservationPerUnit`, `InitialBuyReservation`,
`RemainingBuyReservation`, `ProjectedBuyReservation`); both delegate cash rounding to
`CurrencyHelper.Notional` (rounding stays centralized — good). **They have diverged:** the server copy
is a superset — adds `IsBudgetBuy` (treats `StopMarketBuy` like `TrueMarketBuy`, server ~L13-14/30/38),
special-cases `StopLimitBuy` in `ReservationPerUnit`/`ProjectedBuyReservation` (server ~L21/74), and adds
`ShortCollateralForFill`/`ShortCollateralForResting` (server ~L49-60) the client lacks. Client
`ProjectedBuyReservation` guards only `IsTrueMarketBuy`; server also handles `StopLimitBuy`.

**Safe change:** hoist the *authoritative server* `ReservationMath` into `KieshStockExchange.Shared\Helpers\`
(pure, `CurrencyHelper`-only deps — no I/O/lock/clock), delete the client copy, repoint both. **Value:**
kills a genuine reservation-math drift between the two projects. **Why CK-TOUCHING:** it computes the
reservation amounts mirrored against `Fund.ReservedBalance`/`Position.ReservedQuantity`; the client
version is a *stale subset*, so "unify to server semantics" is a behaviour change on the client and must
be owner-reviewed with a CK soak — NOT a blind extract. Propose-only.

### 2. Repeated signed-percent formatter · PROVABLY-SAFE
Four to five near-identical "signed percent" formatters, no shared helper:
- `KieshStockExchange.Shared\Services\MarketDataServices\Helpers\LiveQuote.cs:161` — `ChangePct >= 0 ? $"+{ChangePct:F2}%" : $"{ChangePct:F2}%"`
- `KieshStockExchange\ViewModels\PortfolioViewModels\PortfolioViewModel.cs:318` — `$"{sign}{Math.Abs(value).ToString("N2",…)}%"`
- `KieshStockExchange\Services\MarketDataServices\Helpers\Drawing\OverlayRenderer.cs:126` — `{pos.UnrealizedPct:0.00}%`
- `KieshStockExchange\ViewModels\AdminViewModels\BotDashboardViewModel.Panels.cs:210` — `{s.PnlPercent:0.0}%`
- (also `Candle.PriceChangePercentDisplay` uses a format-string variant, `Candle.cs:227`)

Behaviourally close but inconsistent precision (F2 / N2 / 0.00 / 0.0). **Safe change:** add
`CurrencyHelper.FormatSignedPercent(decimal value, int decimals = 2)` (or a tiny `PercentHelper`),
adopt at each site. Pure formatting; property-test-equivalent per call-site if the decimals arg matches
the current literal. **Value:** ~10 LOC + one consistent percent style. Note: adopting where the current
precision differs is a *display* behaviour change — keep each site's existing precision to stay Pass-1.

### 3. Cost-basis lot math re-implemented · NEEDS-CARE
- Canonical: `KieshStockExchange\Helpers\ChartMath.cs:26` (`AverageCostBasis`) + `:54` (`PositionPnl`);
  sole legit callers `ChartViewModel.Overlays.cs:285,307`.
- Twin: `KieshStockExchange\ViewModels\AccountViewModels\AccountViewModel.cs:234-274` (`RefreshPnL`)
  re-walks the transaction tape oldest-first with the identical running weighted-average-cost method and
  the same zero-crossing rebase.

**Subtle difference:** ChartMath returns one `(signed qty, avg)` for UNREALIZED P&L vs live price;
AccountViewModel accumulates REALIZED P&L per currency by booking `(sellPrice − avgCost)·qty` on each
sell. Same *lot-walk core*, two copies, both flagged "best-effort on shorts." **Safe change:** extract
the shared lot-walk (build running `(qty, avg)` lots from a tape) into `ChartMath` / a `CostBasisMath`
helper; have both realized and unrealized consumers drive off it. **Why NEEDS-CARE:** the realized-vs-
unrealized booking and short handling differ — must diff carefully + pin with tests before merging.

### 4. `OrderValidator.ValidateInput` vs `ValidateNew` overlap · NEEDS-CARE · CK-adjacent
`KieshStockExchange.Server\Services\MarketEngineServices\Helpers\OrderValidator.cs` — `ValidateInput`
(L52-103) and `ValidateNew` (L105-220) carry near-parallel limit / true-market / slippage-market rule
blocks (price>0 for limits, TrueMarket price==0 + budget, slippage 0-100 + anchor>0), with the same
`OrderResultFactory.InvalidParams` messages and the shared `MaxOrderQuantity`/`NotionalOverflows` guards.
**Safe change:** extract the per-entry-kind rule set into private helpers
(`ValidateLimitShape`/`ValidateTrueMarketShape`/`ValidateSlippageShape`) called by both. **Why
NEEDS-CARE:** the two entry points differ in *inputs* (loose params vs a built `Order` that also has
`IsStopOrder`/bracket paths) and in message wording; message text + short-circuit order are part of the
contract. Order validation gates the money path → CK-adjacent, review the diff for changed rejection
ordering. Pass-2 proposal.

### 5. Single-culture parsing bypassing `ParsingHelper.TryToInt` · NEEDS-CARE
`ParsingHelper` (`KieshStockExchange.Shared\Helpers\ParsingHelper.cs:12,33`) does current-culture-then-
invariant fallback. `PlaceOrderViewModel` uses it (L110/309/319/370/664), but many spots bypass it with a
weaker single-culture `int.TryParse`/`int.Parse`:
- `ModifyOrderViewModel.cs:437,465`, `BracketLegRow.cs:47`, `RegisterViewModel.cs:43,45,123,128` (raw
  throwing `int.Parse`), and admin filters: `UserDetailsViewModel.cs:155`, `TransactionTableViewModel.cs:183`,
  `PositionTableViewModel.cs:196,210`, `OrderTableViewModel.cs:205`, `FundTransactionTableViewModel.cs:56`,
  `FundTableViewModel.cs:89`, `BotDashboardViewModel.Controls.cs:185,205`, `PositionEditViewModel.cs:60,65`.
- Server-side single-culture parses (`PgDBService.Users.cs:30`, `PgDBService.Portfolio.cs:31,166,186,205`,
  `ExcelSeedService.Seeds.cs:115,171`, `ClaimsExtensions.cs:16`, `OrderExecutionService.cs:2709`,
  `AIUser.cs:265`) are machine-input → leave as-is.

**Safe change:** adopt `ParsingHelper.TryToInt` at the client UI-input sites; convert `RegisterViewModel`'s
throwing `int.Parse` to the try-form. **Why NEEDS-CARE:** it's a real behaviour change (adds invariant
fallback → accepts inputs the single-culture parser rejected), so it's a fix, not a no-op — must be
intentional per-site. (Decimal/price parsing is already centralized via `CurrencyHelper.Parse`.)

### 6. Admin date-range logic byte-identical twin · PROVABLY-SAFE
`KieshStockExchange\ViewModels\AdminViewModels\Tables\TransactionTableViewModel.cs:19-22,114-116` and
`OrderTableViewModel.cs:23-26,139-141` contain byte-identical field initializers
(`DateTime.UtcNow.AddMinutes(-5)` / `.TimeOfDay`) and range assembly
(`(FromDate.Date + FromTime).ToUniversalTime()` etc.). Two exact copies. **Safe change:** extract to a
shared admin base/helper (e.g. `DateRangeFilter`) — exact-duplicate → one helper, diff shows only
call-site substitution. Pass-1 eligible.

### 7. Server telemetry money formatters · NEEDS-CARE
Hand-rolled `$`/`N2` money strings instead of `CurrencyHelper.Format`:
- `KieshStockExchange.Server\Services\PortfolioServices\Helpers\FxDeskTelemetry.cs:126` — `Money(v) => v.ToString("N2", InvariantCulture)` (used L121)
- `KieshStockExchange.Server\Services\BackgroundServices\Helpers\BotEconomyTelemetry.cs:266-268,307-308`
- `KieshStockExchange.Server\Services\BackgroundServices\Helpers\BotStatsLogger.cs:106`

Divergent from `CurrencyHelper`: hardcoded `$`, always 2 decimals (wrong for JPY where
`DecimalPlaces`=0), invariant grouping. **Safe change:** route through `CurrencyHelper.Format` /
`FormatCompact`. **Why NEEDS-CARE:** these are log/telemetry lines where a stable invariant `N2` may be
*intentional* (grep-friendly, currency-agnostic aggregate) — changing them alters log output. Low value;
propose, don't auto-adopt.

### 8. `ChartGeometry.AlignToStep` re-derives `TimeHelper.FloorToBucketUtc` · NEEDS-CARE
`KieshStockExchange\Helpers\ChartGeometry.cs:144-152` floors/ceils a `DateTime` to a `TimeSpan` step via
`t.Ticks / ticks * ticks` — the same algorithm as `TimeHelper.FloorToBucketUtc`
(`TimeHelper.cs:69-76`). **Difference:** `AlignToStep` also supports a `forward` (ceiling) mode and does
NOT call `EnsureUtc`. **Safe change:** have the floor branch call `TimeHelper.FloorToBucketUtc`; keep the
ceiling branch (or add `TimeHelper.CeilToBucketUtc`). **Why NEEDS-CARE:** the missing `EnsureUtc` +
ceiling mode mean it's not a byte-for-byte swap; verify Kind handling on the chart's DateTimes.

### 9. Order-type string constants → enum — ASSESSMENT · (residual) PROVABLY-SAFE
**The migration the model-rules flag is already effectively done.** `Order.cs` (L5-8) decomposes the type
into three orthogonal enums — `OrderSide`, `EntryType`, `StopKind` — which are the **authoritative source
of truth**; the 10-value `Order.Types.*` string block (L12-27) is a *derived, read-only* projection
(`OrderType`, L181-195) kept only for logs/telemetry/notifications, and `IsX` helpers (L352-374) already
run off the enums. So there is no flat-string switchboard left to migrate — the risky part is behind us.

**Verdict: do NOT touch the type strings.** `DEDUP_ARC_PLAN.md` explicitly FORBIDS string→enum on order
types (CLAUDE.md mandates the string constants; they cross DB serialization + comparisons). A smart-enum
*wrapper* keeping DB strings byte-identical would be Pass-2/owner only, and given the enums already exist
it's low value.

**Residual (Pass-1 safe):** the 5-value **`Status`** vocabulary is validated in TWO places with an
identical `== Open || == Filled || == Cancelled || == Pending || == Attached` list —
`Order.Status` setter (L204-207) and `IsValidStatus()` (L259-262). Extract to one
`private static bool IsKnownStatus(string)` and call from both. Token-identical predicate → PROVABLY-SAFE.
(The same 5-string set is re-listed again in `OrderValidator`; leaving those alone is fine.)

### 10. `ParsingHelper` should be `static class` · PROVABLY-SAFE (trivial)
`KieshStockExchange.Shared\Helpers\ParsingHelper.cs:10` is `public class ParsingHelper` but every member
is `static`. Mark `static class`. Mechanical; no call-site changes.

### 11. Two currency-conversion systems · NEEDS-CARE (inconsistency note)
`CurrencyHelper.Convert`/`ConvertMoney` (`CurrencyHelper.cs:150,167`) use a hardcoded static
`RatesPerBase` table; `IFxRateService.GetMidRate` (`IFxRateService.cs:9`) is the live AR(1)-drifting rate
source. Portfolio math correctly uses the live service (`PortfolioTotalsHelper.ConvertViaFx` →
`fx.GetMidRate`), but admin tables convert via the static table
(`FundTableViewModel.cs`, `PositionTableObject.cs`). **Not a pure dup** (static fallback vs live rates,
different by design) but a real *inconsistency*: two answers for "convert A→B" depending on call site.
**Safe change:** decide one authority for display conversion (likely `IFxRateService`) and note
`CurrencyHelper.Convert` as the offline/seed fallback only. Owner call; propose.

### 12. `BracketGeometryValidator` is pure but server-only · NEEDS-CARE · CK-adjacent
`KieshStockExchange.Server\Services\MarketEngineServices\Helpers\BracketGeometryValidator.cs` is a pure
static validator (SL/TP ordering by side, Σqty ≤ parent) but lives in the **server** project, so the
client can't reuse it — `PlaceOrderViewModel.ValidateInputs` (L685-695) and
`ModifyOrderViewModel.ValidateBracketLegs` re-check a lighter subset client-side. **Safe change:** move
`BracketGeometryValidator` to `KieshStockExchange.Shared\Helpers\` (its only deps are `CurrencyHelper` +
`OrderResult`, both Shared) so both sides share one geometry rule set. **Why NEEDS-CARE:** it returns an
`OrderResult` (server DTO) — moving it means the client adopts the full geometry check (a stricter
client UX than today), so it's a behaviour change to review, plus confirm `OrderResult` is Shared-visible.

### 13. `ApiDataBaseService` per-entity writeback lambda repeated ~14× · PROVABLY-SAFE (cosmetic)
`KieshStockExchange\Services\DataServices\ApiDataBaseService*.cs` (6 partials, 602 LOC total) is **already
DRY** — HTTP mechanics are factored into 6 private generics + a `Q` builder, so per-entity methods are
one-line delegations. The only residual repetition is the writeback lambda
`(d,r) => { if (d.XId == 0) d.XId = r.XId; }` + literal URL, repeated across ~14 entity types. **Safe
change:** fold the writeback into a helper taking a PK getter/setter. **Value: low** — genuine per-entity
irregularities remain (server-only `NotSupportedException` throwers, bespoke `*PageAsync` filters,
non-uniform PK names like `PriceId`/`ListingId`/`AiUserId`), so a full generic collapse is not available.
**The server `PgDBService` (2556 LOC) is NOT a target:** its per-entity SQL only *looks* uniform (distinct
column sets, `ON CONFLICT` keys, mappers, hot-path batch inserts), and `Order`/`Transaction`/`Fund`/
`FundTransaction`/`Position` writes are settlement-path → **CK-TOUCHING**, off-limits for extract-to-generic.

### 14. `DateTime.UtcNow` bypassing `TimeHelper.NowUtc()` · NEEDS-CARE
50 hits across 15 files. Many are legitimate "timestamp now" (notifications, probes, migration/tests).
Some in prod paths (`OrderBook.cs`, `StopTriggerWatcher.cs`, `OrderBookBroadcaster.cs`,
`UserPortfolioService.cs`, `ServerNotificationService.cs`) bypass the centralized, injectable
`TimeHelper.NowUtc` clock. **Safe change:** route production clock reads through `TimeHelper.NowUtc()`.
**Why NEEDS-CARE:** behaviourally identical *today* but affects test-time mockability; do per-site, not a
blind replace-all (some are inside timing-sensitive loops). Low value.

### 15. `InvertedBoolConverter` duplicates a toolkit built-in · PROVABLY-SAFE (pending dep check)
`KieshStockExchange\Helpers\InvertedBoolConverter.cs` (12 lines) re-implements the standard
CommunityToolkit inverted-bool converter. If `CommunityToolkit.Maui` is referenced, delete + repoint XAML;
else keep. `DepthRatioToWidthConverter.cs` is bespoke (no built-in) — keep. Trivial; verify the package
reference first.

---

## Explicitly CLEAN (verified, not candidates)
- **Currency formatting** — only `CurrencyHelper` defines `"{0:C}"`/`ToString("C")`/`FormatCompact`; app
  code funnels through it. Only inline exceptions are the telemetry sites in #7.
- **Money/price rounding** — no inline `Math.Round(price*qty, 2)` in app paths; all settlement/reservation/
  seed rounding uses `RoundMoney`/`RoundPrice`/`Notional`. (The `Math.Round` hits elsewhere are chart
  geometry, pixel math, color scaling, tick-snapping — distinct concepts.)
- **Decimal/price parsing** — centralized via `CurrencyHelper.Parse` everywhere prices are read.
- **Candle aggregation** — `Shared\...\CandleAggregator` (live bar) and `Server\...\CandleAggregationMath`
  (historical roll-up) share the `Candle` model + `TimeHelper` bucket math; no OHLC-merge duplication.
- **Indicator calculators** (`RsiCalculator`, `VwapCalculator`, `BollingerCalculator`,
  `MovingAverageCalculator`) — client-only, no server/shared twin.
- **`ChartMath.ZoomOffset`/`ReconstructMood`, `ChartGeometry` render geometry** — client-only, no twin
  (except `AlignToStep`, #8, and `AverageCostBasis`/`PositionPnl`, #3).

## Suggested Pass-1 pull order (PROVABLY-SAFE only)
#10 (ParsingHelper static) → #9-residual (Status predicate) → #6 (admin date-range exact-dup) →
#2 (signed-percent helper, at matched precision) → #15 (converter, after dep check) → #13 (writeback
lambda). Everything else (#1, #3, #4, #5, #7, #8, #11, #12, #14) → Pass-2 proposals; **#1 ReservationMath
is owner-gated + needs a CK soak.**
