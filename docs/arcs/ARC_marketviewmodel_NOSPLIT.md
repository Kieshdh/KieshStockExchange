# Arc: MarketViewModel — DECISION: NO SPLIT (leave as-is)

**Target:** `KieshStockExchange/ViewModels/MarketViewModels/MarketViewModels.cs` (612 LOC,
`public partial class MarketViewModel : BaseViewModel, IDisposable`). Phase-0 explored 2026-07-18.

## Verdict: do NOT partial-split. Cohesive file below the "oversized responsibility group" bar.
Rationale (Phase-0 + council "don't manufacture work" guidance):
1. **Under cap in substance** — 612 physical LOC but ~40% is comments/attributes/blank; the class is cohesive
   and already `#region`-organized. The ~500 partial-split trigger targets genuinely oversized god-classes.
2. **Tight cross-concern coupling** — the POLL / FILTER / PAGINATION concerns are interwoven through private
   call chains (`Poll`→`ApplyFilter`→`SortDesired`→`SyncRows`; `OnWatchlistChanged`→`Poll`+`ApplyFilter`+
   `RebuildPagedStocks`). A `.Concern.cs` split would scatter ONE logical flow across 3-4 files, HURTING
   readability — the opposite of the restructure's goal.
3. **Shared statics/fields** (`SyncRows` static util; `_byStockId`; the `_quoteSubscribed`/`_pollTimer`/
   `_disposed` subscription guard flags) have no clean single-partial home.
4. Existing regions already give the navigational grouping a split would.

MarketRow + MarketSortColumn were already extracted to sibling files in arc 1, so the one-class-per-file
win here is already banked.

## Deferred (separate polish task, NOT this arc)
Rename the misnamed file `MarketViewModels.cs` (plural) → `MarketViewModel.cs` (singular), alongside the
other arc-1 deferred renames (TradeViewModels.cs, BaseAdminTableViewModel.cs). Renames are a polish arc
per the two-arc rule.
