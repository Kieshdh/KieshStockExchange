# Client DEDUP + Simplification Inventory

Scope: `KieshStockExchange/` MAUI client only (ViewModels, Views code-behind, client Services).
All client code is **non-CK** (server owns money/conservation), so every candidate below is
unattended-safe behind a `dotnet build` + test-suite gate — **except** items flagged
**XAML/DI-EYEBALL**, which change a binding name, a XAML root type, or a DI registration and
need a human glance first.

Safety classes:
- **PROVABLY-SAFE** — exact duplicate / pure function / dead code / mechanical parameterization / string-const introduction.
- **NEEDS-CARE** — real duplication but with subtle per-site differences; diff carefully, behavior-preserving refactor only.
- **XAML/DI-EYEBALL** — safe in mechanics but alters a bound property name, a `x:Class` base type, or DI wiring.

Discovery: direct reads + three parallel Explore agents (admin tables / trade+portfolio rows / services+views). Line numbers are from the state at authoring time.

---

## TIER 1 — High-value, low-risk quick wins (do these first)

### 1. `GetListAsync<T>` helper for ~25 hand-rolled list-GETs — PROVABLY-SAFE — HIGH
The one-liner `await _http.GetFromJsonAsync<List<T>>($"...", ApiJsonOptions.Default, ct) ?? new();`
is copy-pasted throughout the data-service partials:
- `Services/DataServices/ApiDataBaseService.Users.cs:13`
- `Services/DataServices/ApiDataBaseService.Orders.cs:13,33,36,39,68,85,88,93-95,104-106`
- `Services/DataServices/ApiDataBaseService.Stocks.cs:13,34,37,43,49,58-60`
- `Services/DataServices/ApiDataBaseService.Portfolio.cs:13,26,47,69,90`
- `Services/DataServices/ApiDataBaseService.Misc.cs:13,46,52,89,108,114`

`ApiDataBaseService.cs:28-73` already has a helper cluster (`PostListAsync`, `GetNullableAsync`,
`PostWriteBackAsync`, `PutJsonAsync`, `DeleteUrlAsync`) but **no `GetListAsync`**.
**Change:** add `private Task<List<T>> GetListAsync<T>(string url, CancellationToken ct)` beside the
others; route every list-GET through it. Also normalizes the missing `.ConfigureAwait(false)` at a
few call sites for free.

### 2. `IStockService.SymbolOrDash(int)` extension — PROVABLY-SAFE — MED/HIGH
`if (!_stocks.TryGetSymbol(x.StockId, out string symbol)) symbol = "-";` is duplicated 8×:
- `ViewModels/TradeViewModels/OpenOrdersViewModel.cs:130-131`
- `ViewModels/TradeViewModels/OrderHistoryViewModel.cs:87-88`
- `ViewModels/TradeViewModels/TransactionHistoryViewModel.cs:87-88`
- `ViewModels/TradeViewModels/UserPositionsViewModel.cs:122-123`
- `ViewModels/PortfolioViewModels/PortfolioHoldingsViewModel.cs:141-142`
- `ViewModels/PortfolioViewModels/PortfolioOpenOrdersViewModel.cs:120-121`
- `ViewModels/PortfolioViewModels/PortfolioOrderHistoryViewModel.cs:70-71`
- `ViewModels/PortfolioViewModels/PortfolioTransactionViewModel.cs:71-72`

(Variant fallback `"#" + pos.StockId` at `PortfolioViewModel.cs:240` — leave it or add an overload.)
**Change:** `static string SymbolOrDash(this IStockService s, int stockId)`; pure, no XAML impact.

### 3. Pager math triplicated across three pagers — PROVABLY-SAFE — HIGH
`ComputeVisiblePages()` and `NotifyPagerProperties()` are **byte-identical** in two classes, and the
whole pager-property surface (`TotalPages`, `CurrentPageDisplay`, `CanGoPrev`, `CanGoNext`,
`PagerSummary`, `VisiblePages`) is duplicated:
- `ViewModels/AdminViewModels/Tables/BaseAdminTableViewModel.cs:28-51,172-180` (server-side pager)
- `ViewModels/PortfolioViewModels/ClientPager.cs:23-32,70-91` (client-side pager)

`ComputeVisiblePages` is a pure function → extract to a `static class PagerMath` (or shared partial).
`MarketViewModel` (`ViewModels/MarketViewModels/MarketViewModels.cs:84-87,536-572`) hand-rolls a
**third** inline pager — its page-window math is the same idea (see Tier 3 #24 for that one; it is
NEEDS-CARE because it uses `SyncRows` no-flicker instead of Clear+Add).

### 4. `RunBusyAsync(Func<Task>, string errorMsg)` on the base VM — PROVABLY-SAFE — MED/HIGH
`if (IsBusy) return; IsBusy = true; try { … } catch (Exception ex) { _logger.LogError(ex, "…"); } finally { IsBusy = false; }`
appears ~22× across 17 VMs. Cleanest in the trade/portfolio `RefreshAsync` cluster (9 near-identical bodies):
- `OpenOrdersViewModel.cs:51-65`, `OrderHistoryViewModel.cs:43-57`, `TransactionHistoryViewModel.cs:43-57`, `UserPositionsViewModel.cs:66-80`
- `PortfolioOpenOrdersViewModel.cs:50-65`, `PortfolioOrderHistoryViewModel.cs:40-55`, `PortfolioTransactionViewModel.cs:40-55`, `PortfolioHoldingsViewModel.cs:56-71`, `PortfolioCurrenciesViewModel.cs:57-72`

**Change:** `protected async Task RunBusyAsync(Func<Task> work, string errorMsg)` on `BaseViewModel`
(or `StockAwareViewModel`). Only the message and inner call vary.

### 5. Popup `CloseRequested` wiring base — PROVABLY-SAFE dup — HIGH — **XAML/DI-EYEBALL**
Identical ctor tail + handler in 10–12 popups:
`BindingContext = _vm; _vm.CloseRequested += OnCloseRequested;` +
`OnCloseRequested => MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());`
- Account: `ChangeEmailPage.xaml.cs:15,18-24`, `ChangePasswordPage.xaml.cs:15,18-19`, `ChangeUsernamePage.xaml.cs`, `DepositWithdrawPage.xaml.cs`, `FundTransactionHistoryPage.xaml.cs`, `ConvertCurrencyPage.xaml.cs:16,20-28`
- Admin EditPopups: `UserEditPopup.xaml.cs`, `StockEditPopup.xaml.cs`, `OrderDetailsPopup.xaml.cs`, `PositionEditPopup.xaml.cs`, `FundAdjustPopup.xaml.cs`, `TransactionDetailsPopup.xaml.cs` (all `:15,18-21`)

**Change:** `abstract class VmClosablePopup<TVm> : Popup` (or a `Popup.WireClose(vm)` extension).
**Eyeball:** the base must be the `x:Class` XAML root type; Account popups use field `_vm` while Admin
popups expose `public ViewModel { get; }` (preserve for x:Reference). **Bonus bug:** only
`ConvertCurrencyPage` unsubscribes + disposes on `Closed` (`:23-28`); the other 9 leak the handler —
consolidating onto the ConvertCurrency behavior is the correct fix (verify each VM `Dispose` is idempotent).

### 6. Date+time combine/clamp block — PROVABLY-SAFE — HIGH (small)
Identical 5-line block:
- `ViewModels/AdminViewModels/Tables/OrderTableViewModel.cs:138-143`
- `ViewModels/AdminViewModels/Tables/TransactionTableViewModel.cs:113-118`

**Change:** `(DateTime From, DateTime To) CombineAndClampRange()` on a shared base / static helper.

### 7. Quick-range commands `SetLast5Min/15Min/Hour/1Day` + `SetRange` — PROVABLY-SAFE — HIGH
Four `[RelayCommand]`s + `SetRange(TimeSpan)` are character-identical:
- `OrderTableViewModel.cs:211-224`
- `TransactionTableViewModel.cs:189-202`
(also the four `From/To Date/Time` `[ObservableProperty]`s + their `OnChanged` hooks are duplicated).
**Change:** absorb into a new `DateRangeTableViewModel<T>` base (see #14; this base absorbs #6, #7 and #16 at once).

### 8. `GetUsersByIds` hand-rolls `PostListAsync` — PROVABLY-SAFE — MED
`ApiDataBaseService.Users.cs:29-34` manually does `PostAsJsonAsync + EnsureSuccessStatusCode +
ReadFromJsonAsync<List<User>> ?? new()` — that is exactly `PostListAsync<TBody,TItem>`
(`ApiDataBaseService.cs:28-33`), already used by `GetOrdersByIds`, `GetOpenOrdersForUsersAsync`,
`GetPositionsForUsersAsync`, `GetFundsForUsersAsync`.
**Change:** `=> PostListAsync<IReadOnlyList<int>, User>("api/users/by-ids", userIds, ct);`

### 9. Date-format literals in models — PROVABLY-SAFE — MED
`"dd-MM HH:mm"` (short) and `"dd/MM/yyyy HH:mm:ss"` (long) repeat across model display props consumed
by the row DTOs (`When`/`Opened`/`Closed`/`TimestampShort`):
- short: `Order.cs:344,346`, `Transaction.cs:133`
- long: `Order.cs:343,345`, `Transaction.cs:132`, `Position.cs:108-109`, `FundTransaction.cs:103`

(These live in `KieshStockExchange.Shared/Models` — a shared lib, still non-CK display code.)
**Change:** `DateFormats.Short/.Long` consts + `ToShort()/ToLong()` extension. Output unchanged.

### 10. `OnAppearing` best-effort safe-load helper — PROVABLY-SAFE — MED
`base.OnAppearing(); try { …await… } catch (Exception ex) { Debug.WriteLine($"…failed: {ex}"); }`:
- `MarketPage.xaml.cs:16-27` and `PortfolioPage.xaml.cs:30-40` share the **exact** `RefreshCommand.CanExecute/ExecuteAsync` body
- `AdminPage.xaml.cs:52-58`, `TradePage.xaml.cs:96-103`, `LoginPage.xaml.cs:24-30` (differ only in awaited call + tag)

**Change:** `static Task PageLifecycle.SafeLoad(string tag, Func<Task> load)`. Diagnostic-only, low risk.

### 11. `ResourceHelper.TryGetColor/TryGetStyle` — PROVABLY-SAFE — MED
`Application.Current?.Resources.TryGetValue(key, out var raw) == true && raw is T` reimplemented 3×:
- `BotDashboardPage.xaml.cs:56-65` (`TryGetColor`)
- `SegmentedTabView.xaml.cs:341-344` (inline `"Primary"` lookup)
- `TopNavBarView.xaml.cs:54-58` (`SetLinkActive` inline `Style` lookup)

**Change:** `ResourceHelper.TryGetColor/TryGetStyle` in `Helpers/`. `BotDashboardPage.TryGetColor` moves verbatim.

---

## TIER 2 — Structural refactors, higher payoff, review carefully (NEEDS-CARE)

### 12. Trade `BuildRows` "current-stock-first, then all others" triplication — NEEDS-CARE — HIGH
Structurally identical two-phase partition (selected stock first, `if (!ShowAll) yield break;`, then the rest):
- `OpenOrdersViewModel.cs:104-126`
- `OrderHistoryViewModel.cs:61-83`
- `TransactionHistoryViewModel.cs:61-83`

OpenOrders/OrderHistory are byte-identical bar `_cache.OpenOrders`/`ClosedOrders` + factory; Transaction
differs only in `OrderByDescending(t => t.Timestamp)` vs `.UpdatedAt` and `Order` vs `Transaction` accessors.
**Change:** protected generic on `TradeTableViewModelBase<TRow>`:
`BuildCurrentFirst<TModel>(IEnumerable<TModel> src, Func<TModel,int> stockIdOf, Func<TModel,CurrencyType> ccyOf, Func<TModel,DateTime> orderKey, Func<TModel,TRow> factory, int stockId, CurrencyType ccy)`.
`UserPositionsViewModel.cs:85-112` is the odd one out (list-sort + insert-current-at-0, keeps qty==0) — leave separate.

### 13. Portfolio table VMs are whole-class near-clones — NEEDS-CARE — HIGH
`PortfolioOpenOrdersViewModel`, `PortfolioOrderHistoryViewModel`, `PortfolioTransactionViewModel` share
the same skeleton: `ClientPager<TRow> Pager`, `RefreshAsync` (busy-guard→service.RefreshAsync→RebuildView),
`RebuildView` = `src.Where(StockId>0).OrderByDescending(...).Select(CreateRow).ToList()` → `Pager.SetSource`,
`OnXChanged` = `MainThread.BeginInvokeOnMainThread(RebuildView)`, identical `Dispose` unsubscribe.
- OpenOrders: RebuildView `107-116`, CreateRow `118-129`, OnOrdersChanged `131-135`, Dispose `137-143`
- OrderHistory: `57-66`, `68-73`, `75-79`, `81-87`
- Transaction: `57-66`, `68-78`, `80-84`, `86-92`

**Change:** `PortfolioTableViewModelBase<TRow>` holding `Pager`, the `RefreshAsync` template (abstract
`RefreshSourceAsync()` + `BuildRows()`), the `OnChanged→RebuildView` marshal, and `Dispose`.
**Care:** each subclass subscribes a different event source → base needs a subscribe/unsubscribe hook.

### 14. `DateRangeTableViewModel<T>` intermediate base (Order + Transaction admin tables) — NEEDS-CARE — HIGH
`OrderTableViewModel` and `TransactionTableViewModel` share ~90 lines almost verbatim: the four
date/time `[ObservableProperty]`s + `OnChanged` hooks, `PickerStocks`/`SelectedStockFilter`
(`Order:40-53` vs `Transaction:27-40`), `EnsureStocksLoadedAsync` + `_stocksById`/`_aiUserIds` lazy load
(`Order:107-119` vs `Transaction:85-97`), the in-`LoadPageAsync` refetch guard (`Order:124-128` vs
`Transaction:102-106`), plus #6 and #7. One intermediate base between `BaseTableViewModel<T>` and these
two absorbs Tier-1 #6/#7 and this finding together — the single highest-leverage admin refactor.
**Care:** `PickerStocks` carries per-VM state; `AnyStockSentinel` is owned by `OrderTableViewModel:18`
and referenced by Transaction — mind ownership on the move.

### 15. Shared `HttpApiClient` base / `HttpClient` json extensions — NEEDS-CARE — HIGH — **DI-EYEBALL**
`POST/GET → EnsureSuccessStatusCode → ReadFromJsonAsync<T> ?? throw` repeats across the API clients:
- `ApiOrderEntryClient.cs:101-105,126-129,160-164,170-174,179-182,187-190`
- `ApiOrderExecutionService.cs:30-33,95-98,104-108,113-117`
- `ApiPortfolioClient.cs:170-172,197-199`
- `ApiBotAdminClient.cs:24-95` (10 methods)

The `factory.CreateClient("KSE.Server")` construction is itself duplicated across ~9 clients
(126 GET/POST/JSON occurrences across 17 service files; `"KSE.Server"` is a bare magic string).
**Change:** `HttpApiClient` base (or static `HttpClient` extensions) exposing
`GetJsonAsync<T>`, `PostJsonAsync<TReq,TRes>`, `PostAsync`, each wrapping EnsureSuccess +
`ApiJsonOptions.Default` + a null-guard that takes the **operation name** as a param (the
"…returned no body." messages differ per method — preserve them). Extract `"KSE.Server"` to a const.
**Eyeball:** touches several DI-registered services; pick base-class vs extension-method deliberately.

### 16. Modal-form VM family base (`ErrorMessage`/`HasError`/`CloseRequested`/`Saved`/`Cancel`) — mixed — MED/HIGH
Seven form VMs share an identical boilerplate block and a same-shaped `Save`:
- Account: `ChangePasswordViewModel.cs`, `ChangeEmailViewModel.cs`, `ChangeUsernameViewModel.cs` (`CloseRequested` + `Cancel` + `ErrorMessage`/`HasError`, no `Saved`)
- Admin EditPopups: `UserEditViewModel.cs`, `StockEditViewModel.cs`, `PositionEditViewModel.cs`, `FundAdjustViewModel.cs` (add `Saved` + `_original` clone-draft pattern)

The `[ObservableProperty] ErrorMessage` + `[NotifyPropertyChangedFor(HasError)]`,
`HasError => !string.IsNullOrEmpty(ErrorMessage)`, the `CloseRequested`/`Saved` events, and
`Cancel => CloseRequested?.Invoke(...)` are **exact duplicates** (PROVABLY-SAFE to hoist).
The `Save` shape (clear-error → validate → `IsBusy` try/catch(log→ErrorMessage)/finally →
`Saved`+`CloseRequested`) is a NEEDS-CARE template method (validation + persistence differ per VM).
**Change:** `abstract class ModalFormViewModel : BaseViewModel` for the boilerplate; optional
`Task<bool> PersistAsync()` + `string? Validate()` hooks for the `Save` skeleton.

### 17. `ResolveUserId(Filter)Async` username→id resolver — mostly PROVABLY-SAFE — HIGH
Near-identical resolver differing only in the source string property:
- `OrderTableViewModel.cs:200-209`, `TransactionTableViewModel.cs:178-187`, `FundTableViewModel.cs:85-92`, `FundTransactionTableViewModel.cs:52-59` — all `int?` (null on empty, -1 on no-match), byte-identical bar the property.
- `PositionTableViewModel.cs:192-199` — returns `int` (not `int?`) and `-1` on **empty** too.
**Change:** `protected Task<int?> ResolveUserIdAsync(string? search, CancellationToken ct)` on
`BaseTableViewModel`. The four `int?` versions are mechanical; the Position variant differs in the
empty-vs-nomatch contract — do not blindly merge its signature.

### 18. Admin popup page-resolution + Saved-refresh boilerplate — mixed — MED
`var page = Shell.Current?.CurrentPage ?? Application.Current?.Windows?.FirstOrDefault()?.Page; if (page is null) return;`
appears at 8+ sites (13 popup-open sites across 10 files):
- `UserTableViewModel.cs:52-54`, `StockTableViewModel.cs:133-135`, `OrderTableViewModel.cs:178-180`, `TransactionTableViewModel.cs:159-161`, `FundTableViewModel.cs:96-98`, `PositionTableViewModel.cs:257-259`, `UserDetailsViewModel.cs:313-315,348-350`

The subscribe/`ShowPopupAsync`/unsubscribe skeleton around it repeats too.
**Change:** (a) `static Page? ResolveHostPage()` — PROVABLY-SAFE; (b) generic
`ShowSavedPopupAsync<TPopup>(Action<TPopup> init, Func<Task> onSaved)` — NEEDS-CARE (the popups' `Saved`
event has no shared contract; the Order/Transaction **Details** popups use `NavigateTo*` events, not
`Saved` — must NOT be folded in).

### 19. Subscribe + BuildFromHistory + `_subscriptions` HashSet — NEEDS-CARE — MED
Both position-table VMs keep `HashSet<(int,CurrencyType)> _subscriptions` and, on first sight of a key,
fire `SubscribeAsync` + `BuildFromHistoryAsync`:
- `UserPositionsViewModel.cs:19,125-145` (wraps in `Task.Run` + warn-logging, also `Unsubscribe`s on Dispose `:45-56`)
- `PortfolioHoldingsViewModel.cs:20,144-149` (bare `_ =`, no unsubscribe)
**Change:** shared `EnsureSubscribed(stockId, ccy)`; **care** — the two are not byte-identical (Task.Run + unsubscribe differ).

### 20. Admin in-VM `(sortKey, desc)` comparator switches — NEEDS-CARE — MED
Hand-rolled tuple-switches mapping `(sortKey,desc)` → `OrderBy`/`OrderByDescending`, doubling every key:
- `StockTableViewModel.cs:66-80`, `OrderTableViewModel.cs:165-170`, `TransactionTableViewModel.cs:142-151`, `PositionTableViewModel.cs:153-162,179-188`
**Change:** generic `ApplySort<T>(src, key, desc, IReadOnlyDictionary<string,Func<T,IComparable>> selectors)`.
**Care:** selectors + `StringComparer.OrdinalIgnoreCase` flags + default fallbacks differ per VM.

### 21. `GetUsersPageAsync` hand-built query string — NEEDS-CARE — MED — **server-behavior EYEBALL**
`ApiDataBaseService.Users.cs:15-21` concatenates the URL by hand while every other paged endpoint uses
the `Q` builder (`Orders.cs:17-21,72-76`, `Portfolio.cs:17,51,58-60,94-95`). `Q.Add` **skips null values**
(`ApiDataBaseService.cs:82`), so a null `sortKey`/`filter` would be omitted rather than sent empty.
**Change:** rewrite with `Q`; **confirm** the server model-binder treats missing == empty for `sortKey`
(the position/fund paged endpoints already pass `sortKey` through `Q`, which is supporting evidence).

---

## TIER 3 — Lower value: enums/records/consts and single-site hoists (PROVABLY-SAFE unless noted)

### 22. Magic sort-key strings + twice-typed default — const PROVABLY-SAFE / enum XAML-EYEBALL — LOW/MED
Each admin VM repeats its default sort key in the ctor and again as `sortKey ?? "X"` in `LoadPageAsync`
(`UserTableViewModel.cs:33,43`; `OrderTableViewModel.cs:89,145`; `TransactionTableViewModel.cs:65,120`;
`FundTableViewModel.cs:38,55`; `FundTransactionTableViewModel.cs:19,34`; `PositionTableViewModel.cs:81,135`).
A `private const string DefaultSortKey` removes the twice-typed literal — PROVABLY-SAFE.
Full enum conversion is **NEEDS-CARE / XAML-EYEBALL**: the keys are hard-coded in XAML
(`SortableHeader SortKey="StockId"`, e.g. `StockTableView.xaml:18-43`) fed straight into `ToggleSortCommand`.

### 23. `TransactionRow` Side/Type magic strings — NEEDS-CARE — MED
`TransactionRow.cs:26-27`: `Side => IsBuyOrder ? "BUY" : "SELL";` and `Type => "MARKET";` duplicate
`Order.SideDisplay` and disagree with `Order.TypeDisplay` (`"MKT"/"LIMIT"/…`). The `"BUY"/"SELL"` literal
recurs widely (UserDetailsTransactionRow, PlaceOrderViewModel, MatchingEngine, etc.).
**Change:** a `SideText` constants class / `OrderSide`→display extension (const introduction PROVABLY-SAFE).
**Care:** do NOT rename the bound `Side`/`Type` props; reconciling `"MARKET"` vs `"MKT"` is a visible string change.

### 24. `MarketViewModel` inline pager — NEEDS-CARE — LOW/MED
`MarketViewModels.cs:84-87,536-572` reimplements `TotalPages/CanGoPrev/CanGoNext/PageDisplay` + page
slicing. It intentionally uses `SyncRows` (`:575-591`) for no-flicker CollectionView updates rather than
`ClientPager`'s Clear+Add, so it is **not** a drop-in for `ClientPager`. Only the page-window arithmetic
overlaps #3; converting risks reintroducing flicker. `SyncRows` itself is a candidate to promote to a
reusable `ObservableCollection` sync helper if a 2nd consumer appears.

### 25. Sign-bool pairs (`IsBullish/IsBearish`, `IsPositive/IsNegative`) — NEEDS-CARE (XAML) — LOW/MED
Same `>0m`/`<0m` test surfaced as bound bools in `MarketRow.cs:44-45`, `LiveQuote.cs:176-180`,
`Candle.cs:238-239`, `AccountPnLRow.cs:28-29`. Only the *comparison* is dedup-able (`SignHelper.IsPositive(decimal)`);
the property names are XAML-bound and must stay. Low value (one-liners).

### 26. Account row DTO shape → `CurrencyAmountRow` record — NEEDS-CARE (XAML) — MED
`AccountVolumeRow.cs:23-26` and `AccountPnLRow.cs:23-27` are identical (`CurrencyType`, `Amount`,
`Currency => CurrencyType.ToString()`, `AmountDisplay => CurrencyHelper.Format(Amount, CurrencyType)`);
`AccountPnLRow` only adds the sign bools. **Change:** base `record CurrencyAmountRow` (`AccountPnLRow : CurrencyAmountRow`).
Bound names (`Currency`, `AmountDisplay`) must be preserved (AccountPage.xaml).

### 27. Fund / Position row DTO display dup — NEEDS-CARE (XAML) — MED
- `FundTableObject.cs:19-23` vs `UserDetailsFundRow.cs:19-22` — same Fund→display strings, differ only `CurrencyDisplay` vs `Currency` (bound by different views).
- `PositionTableObject.cs:33-34` vs `UserDetailsPositionRow.cs:20-21` — `Quantity == 0 ? "-" : …` is a PROVABLY-SAFE extraction (`PositionFormat.Qty(int)`); the price/value formatting differs in sentinel (`"-"` vs em-dash `"—"`) and currency (native vs hard-coded USD) — NEEDS-CARE.

### 28. `ToString()` vs `CurrencyHelper.GetIsoCode` — PROVABLY-SAFE — LOW
`AccountFundRow.cs:25`, `AccountVolumeRow.cs:25`, `AccountPnLRow.cs:25` use `CurrencyType.ToString()`;
`PortfolioCurrenciesViewModel.cs:85,107` + `PortfolioViewModel.cs:227` use `GetIsoCode` (defined as
`currency.ToString()`, `CurrencyHelper.cs:114`). Route all through `GetIsoCode`.

### 29. `FormatSigned`/`FormatSignedPct` → `CurrencyHelper` — PROVABLY-SAFE — LOW
`PortfolioViewModel.cs:308-319` local statics implement the `+`/`-` sign-prefix pattern; natural home is
`CurrencyHelper.FormatSigned`. Single-use today; safe to hoist when a 2nd caller appears.

### 30. Tuple → `readonly record struct QuoteKey(int StockId, CurrencyType Currency)` — PROVABLY-SAFE — LOW
`HashSet<(int,CurrencyType)>` subscription keys + `_market.Quotes` lookups
(`UserPositionsViewModel.cs:19`, `PortfolioHoldingsViewModel.cs:20`) could share a named key type.

### 31. `PostWriteBackAsync` PK-writeback lambdas — NEEDS-CARE (model contract) — LOW
`(d,r) => { if (d.XId == 0) d.XId = r.XId; }` at ~15 call sites (Orders/Users/Stocks/Portfolio/Misc),
each a different PK. No clean dedup without an `IHasId` interface on the models — a model-contract change.

### 32. `ApiPortfolioClient` POST-bool-with-refresh — NEEDS-CARE — LOW
`ApiPortfolioClient.cs:166-181` (deposit/withdraw) and `:195-208` (convert) share a
`POST→EnsureSuccess→ReadFromJsonAsync<bool>; if(ok) RefreshAsync; catch→LogWarning→return false` shape.
Fold into `PostBoolWithRefreshAsync(url, cmd, refreshUserId, ct, logContext)`. Only 2 sites; messages differ.

---

## Architecture flags — human eyeball, do NOT auto-extract

- **`RegisterPage.xaml.cs:10`** news up its VM by hand (`new RegisterViewModel(Navigation, auth)`) instead
  of DI-injecting it like every other page (`LoginPage.xaml.cs:9-13`). DI inconsistency.
- **SegmentedTabView BindingContext workaround** — the "attach tab 0 before BindingContext propagates"
  block (`AdminPage.xaml.cs:24-32`, `PortfolioPage.xaml.cs:17-23`, `TradePage.xaml.cs:14-18`) is a repeated
  workaround for `SegmentedTabView` not pushing `BindingContext` to tab content. The clean fix is in the
  control (`SegmentedTabView.xaml.cs:391-400` `UpdateContent`), which changes the control contract for all
  consumers — a design item, not a mechanical dedup.
- **`TradePage.xaml.cs:32-94`** holds substantial layout math (70/30 star split, hard-coded chrome px) in
  code-behind. It is genuine presentation logic (acceptable) but solves a *different* problem than
  `AdminPage.xaml.cs:37-50` viewport→page-size sizing — not a safe dedup.
- **Popup handler leak** (see #5): 9 of 10 popups never unsubscribe `CloseRequested`; ConvertCurrencyPage
  is the only correct one. Fix belongs with the #5 base-class consolidation.

## Verified already-good (do not "fix")
- `ApiJsonOptions.Default` is the single shared `JsonSerializerOptions` and is used uniformly — no re-created options anywhere.
- `ClientPager<T>` is the single client pager, reused by all 6 Portfolio VMs; the admin `BaseTableViewModel`
  pager is intentionally distinct (server-side paging). Trade tables intentionally don't page.
- Trivial `InitializeComponent()`-only code-behinds (most `*View`/`*TableView` shells) — nothing to dedup.
