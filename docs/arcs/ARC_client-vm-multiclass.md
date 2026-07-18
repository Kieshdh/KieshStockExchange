# Arc: Multi-class-per-file cheap wins (CLIENT ViewModels)

**Type:** STRUCTURAL (byte-identical moves only). **Lane:** Auto. **Branch:** `feature/bot-market-realism-v2`.
**Baseline oracle:** build green + **661/661** tests pass (confirmed 2026-07-18 04:xx).

## Goal
Lift every trailing non-primary top-level type (row/DTO/enum/record/interface) out of its host
ViewModel file into its own one-class-per-file sibling, **byte-identical body, unchanged namespace**.

## Council + owner-level decisions (2026-07-18)
- **Arc order** (council, unanimous): ViewModels → CandleService → AccountsCache → BracketCoordinator(triage-gated).
- **Folder = FLAT siblings** next to the host VM (NOT `Tables/Rows/`). Rationale: repo precedent is flat
  (`PortfolioViewModels/ClientPager.cs`, `StrategyBreakdownRowVm.cs`); CLAUDE.md says follow existing
  conventions; keeps folder==namespace. Reversible via `git mv`. **FLAG for Kiesh.**
- **NO renames this arc.** The 3 misnamed primary files (`MarketViewModels.cs`, `TradeViewModels.cs`,
  `BaseAdminTableViewModel.cs`) keep their names — renames are a later polish pass (two-arc rule = zero
  renames in a structural diff). **FLAG for Kiesh** as a follow-up.
- **EXCLUDE `SegmentedTabView`/`SegmentedTabItem`** — View control code-behind, not a VM; widest XAML
  surface (9 pages). Out of scope; candidate for a separate view-layer arc. **FLAG for Kiesh.**

## Why byte-identical / XAML-safe (from Phase-0)
- All target files use file-scoped namespaces; no block-namespace churn.
- csproj auto-includes new files (no `EnableDefaultCompileItems=false`); **no csproj edit needed.**
- XAML compiled bindings (`x:DataType`, DataTemplates) resolve by **CLR namespace, not file/folder** →
  same-namespace move is invisible to XAML. **No XAML edit needed.**
- No `partial` type is split across the boundary; row partials' other half is CommunityToolkit
  source-gen (keyed by type+namespace, not file) → safe.
- Field/static-initializer-order risk does NOT apply (whole types move, not one type's fields).
- `PositionRow`/`TransactionRow` have unrelated Server-side namesakes in different assemblies —
  DO NOT global-rename; moves only.

## Move-list (host file → new sibling files, same folder, same namespace)
- `AdminViewModels/Tables/UserDetailsViewModel.cs` → UserDetailsFundRow, UserDetailsPositionRow, UserDetailsOrderRow, UserDetailsTransactionRow
- `TradeViewModels/OrderBookViewModel.cs` → LevelSide, BucketSizeOption, LevelRow
- `AccountViewModels/AccountViewModel.cs` → AccountFundRow, AccountVolumeRow, AccountPnLRow
- `MarketViewModels/MarketViewModels.cs` → MarketSortColumn, MarketRow
- `TradeViewModels/TradeViewModels.cs` → TradingPair
- `TradeViewModels/ModifyOrderViewModel.cs` → BracketLegRow
- `AdminViewModels/Tables/PositionTableViewModel.cs` → PositionTableObject
- `TradeViewModels/UserPositionsViewModel.cs` → PositionRow
- `TradeViewModels/TransactionHistoryViewModel.cs` → TransactionRow
- `TradeViewModels/OrderHistoryViewModel.cs` → ClosedOrderRow
- `TradeViewModels/OpenOrdersViewModel.cs` → OpenOrderRow
- `TradeViewModels/ISideRow.cs` → IStockNav
- `PortfolioViewModels/PortfolioCurrenciesViewModel.cs` → CurrencyRow
- `AdminViewModels/Tables/UserTableViewModel.cs` → UserTableObject
- `AdminViewModels/Tables/StockTableViewModel.cs` → StockTableObject
- `AdminViewModels/Tables/OrderTableViewModel.cs` → OrderTableObject
- `AdminViewModels/Tables/TransactionTableViewModel.cs` → TransactionTableObject
- `AdminViewModels/Tables/FundTableViewModel.cs` → FundTableObject
- `AdminViewModels/Tables/FundTransactionTableViewModel.cs` → FundTransactionTableObject
- `AdminViewModels/EditPopups/OrderDetailsViewModel.cs` → OrderLinkedTransactionRow
- `AdminViewModels/Tables/BaseAdminTableViewModel.cs` → ILazyTab (extract interface; primary file NOT renamed this arc)

## Gate (executor + independent orchestrator re-run)
1. `dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0` green.
2. FULL `dotnet test KieshStockExchange.Tests/KieshStockExchange.Tests.csproj` → **661/661**, 0 fail.
3. **Moves-only diff audit:** every new file's content == the exact block deleted from its host; host
   files lose ONLY the moved type + keep byte-identical remainder; no logic/using/namespace edits
   except a moved type's own required `using`s (verify none needed — same namespace/file usings).
4. No csproj / no XAML changes in the diff.
