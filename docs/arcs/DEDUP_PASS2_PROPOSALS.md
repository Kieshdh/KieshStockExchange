# DEDUP Arc — PASS 2 PROPOSALS (propose-only; Kiesh reviews + merges)

**Status:** Pass-1 autonomous TEXTUAL/COMPILER-identity dedups are DONE (see `DEDUP_HANDOFF.md` DONE list,
commits `f9a009b`…`d7ac996`). Everything below needs OWNER judgment — it changes behaviour, touches XAML
resource resolution / DI wiring (build won't catch a regression), touches money/CK code, or is a real bug fix.
**Nothing here has been merged.** Each item = the change + a "why / what's the risk" argument for review.

Governing rules: `DEDUP_ARC_PLAN.md` (Pass-2 = propose-only). HARD BANS unattended: transaction-scope,
decimal rounding, Fund/Position/reservation mutation, reserve→release order, Order-type→enum, records on
persisted models, the 3 Attended giants. CK=0 is sacred.

---

## P2-1 — `CloseRequested` handler-leak BUG (9 of 10 popups) — REAL FIX — HIGH
**This is a genuine defect, not just a dedup.**

**The correct pattern (only `ConvertCurrencyPage.xaml.cs` does this):**
```csharp
public ConvertCurrencyPage(ConvertCurrencyViewModel vm)
{
    InitializeComponent();
    _vm = vm ?? throw new ArgumentNullException(nameof(vm));
    BindingContext = _vm;
    _vm.CloseRequested += OnCloseRequested;
    Closed += OnPopupClosed;                       // ← wires cleanup
}
private void OnCloseRequested(object? s, EventArgs e) =>
    MainThread.BeginInvokeOnMainThread(async () => await CloseAsync());
private void OnPopupClosed(object? s, PopupClosedEventArgs e)
{
    _vm.CloseRequested -= OnCloseRequested;        // ← unsubscribe
    Closed -= OnPopupClosed;
    _vm.Dispose();                                 // ← dispose the VM
}
```

**The leak (e.g. `ChangeEmailPage.xaml.cs` + 8 others):** subscribe `_vm.CloseRequested += OnCloseRequested`
in the ctor, **no `Closed` handler at all** → the handler is never removed and `_vm.Dispose()` is never
called. The VM→popup event reference keeps the popup alive as long as the VM is reachable, and the VM's own
`Dispose` (subscriptions / timers / cache handles) never runs.

**Affected (subscribe-without-cleanup):** `ChangeEmailPage`, `ChangePasswordPage`, `ChangeUsernamePage`,
`DepositWithdrawPage`, `FundTransactionHistoryPage` (Account popups) + `UserEditPopup`, `StockEditPopup`,
`OrderDetailsPopup`, `PositionEditPopup`, `FundAdjustPopup`, `TransactionDetailsPopup` (Admin EditPopups).
Correct: `ConvertCurrencyPage` only.

**Proposed fix:** consolidate onto ONE base type (`abstract class VmClosablePopup<TVm> : Popup`) or a
`Popup.WireClose(vm)` extension that reproduces the ConvertCurrency pattern (subscribe + `Closed`-unsubscribe +
`Dispose`), and adopt it in every popup. Removes the boilerplate AND fixes the leak in one move.

**Why review is required (not autonomous):**
- Behaviour CHANGE (adds disposal + unsubscribe where there was none) — that's the fix, but it must be intentional.
- **Each VM's `Dispose` must be idempotent + safe to call on popup close** — verify per VM before adopting
  (some may not implement `IDisposable`, or may dispose something still in use).
- **XAML-EYEBALL:** the base class must become the `x:Class` root type in each popup's `.xaml`; Account popups
  use a private `_vm` field while Admin popups expose `public ViewModel { get; }` for `x:Reference` — preserve
  that surface or bindings break at runtime (build won't catch it).

---

## P2-2 — `InvertedBoolConverter` → CommunityToolkit.Maui — RECOMMEND **LEAVE AS-IS**
Dep-check done: `CommunityToolkit.Maui` **9.1.0 IS referenced** (`KieshStockExchange.csproj:74`). The local
converter is declared once (`Resources/Styles/ShellStyles.xaml:12`, `x:Key="InvertedBoolConverter"`) and
consumed by `{StaticResource InvertedBoolConverter}` across 13 XAML files.

Local behaviour (`Helpers/InvertedBoolConverter.cs`):
```csharp
Convert:     value is bool b ? !b : true;    // non-bool / null → true
ConvertBack: value is bool b ? !b : false;   // non-bool / null → false
```
**Not a drop-in:** CommunityToolkit.Maui's `InvertedBoolConverter` is a typed `bool→bool` converter; on
null/non-bool input it does NOT return the local's defensive `true`/`false` (it throws / behaves differently).
Any binding that transiently yields null (e.g. before BindingContext is set) would change behaviour — and a
converter/resource regression is **runtime**, invisible to the build.

**Recommendation:** LEAVE the 8-line local converter. It is harmless and strictly more defensive on edge
inputs. **Low value, real risk.** If Kiesh still wants the swap, the minimal change is: repoint
`ShellStyles.xaml:12` to the toolkit type (add its xmlns) and delete `Helpers/InvertedBoolConverter.cs` — the
13 `{StaticResource}` usages stay — but only after confirming no binding ever feeds it null/non-bool.

---

## P2-3 — `LoginPage.OnRegisterClicked` → `PageLifecycle.SafeLoad` — tiny, optional (Pass-1-ish)
Byte-identical to the `SafeLoad` shape shipped in `d7ac996` (#10), but it's an event handler, not `OnAppearing`:
`try { await Shell.Current.GoToAsync("RegisterPage"); } catch (Exception ex) { Debug.WriteLine($"LoginPage.OnRegisterClicked nav failed: {ex}"); }`
→ `await PageLifecycle.SafeLoad("LoginPage.OnRegisterClicked nav failed", () => Shell.Current.GoToAsync("RegisterPage"));`
Provably safe (same helper, same tag+load pattern). Only reason it's here: single-site, marginal value. Could be
done via the normal Pass-1 pipeline if desired.

---

## P2-4 — Structural client bases (NEEDS-CARE, non-CK) — from `DEDUP_client_INVENTORY.md`
Real duplication, but each needs a template-method base with per-subclass hooks — behaviour-preserving refactor
that the test suite won't fully guard (client isn't in the test project). Owner review + manual click-test.
- **#13 `PortfolioTableViewModelBase<TRow>`** — the 3 Portfolio table VMs are whole-class near-clones (Pager +
  RefreshAsync template + OnChanged→RebuildView marshal + Dispose). Care: each subclass subscribes a different
  event source → base needs a subscribe/unsubscribe hook.
- **#14 `DateRangeTableViewModel<T>`** (Order + Transaction admin tables) — ~90 shared lines (the date/time
  `[ObservableProperty]`s + hooks, PickerStocks, EnsureStocksLoaded, refetch guard). Highest-leverage admin
  refactor; absorbs the already-shipped `DateRangeHelper` call sites. Care: `PickerStocks` per-VM state,
  `AnyStockSentinel` ownership.
- **#16 `ModalFormViewModel`** — 7 form VMs share `ErrorMessage`/`HasError`/`CloseRequested`/`Cancel` boilerplate
  (the pure-boilerplate part is PROVABLY-SAFE to hoist; the `Save` skeleton is a NEEDS-CARE template).
- **#12 `BuildCurrentFirst<TModel>`** trade-row partition (OpenOrders/OrderHistory/Transaction).
- **#17 `ResolveUserIdAsync`** (4 `int?` VMs mechanical; Position variant differs empty-vs-nomatch — do NOT
  blind-merge its signature).
- **#20 `ApplySort<T>`** comparator-switch generalization (selectors + culture flags differ per VM).
- **#15(client) `HttpApiClient` base** — DI-EYEBALL; touches several registered clients.

## P2-5 — Money / CK-adjacent (OWNER-GATED, needs CK soak) — from `DEDUP_shared_helpers_INVENTORY.md`
- **#1 `ReservationMath` client/server DRIFT — CK-TOUCHING.** Client copy is a stale subset of the server
  (missing `IsBudgetBuy`, `StopLimitBuy` cases, short-collateral). Hoist authoritative server copy to
  `Shared/Helpers`, delete client copy, repoint both. **This is a behaviour change on the client** (adopts
  server semantics) → owner review + a multi-hour CK soak, NOT a blind extract. Highest real-risk item.
- **#4 `OrderValidator.ValidateInput` vs `ValidateNew`** overlap (CK-adjacent) — extract per-entry-kind rule
  helpers; message text + short-circuit order are part of the contract → review rejection ordering.
- **#3 cost-basis lot math** re-implemented in `AccountViewModel.RefreshPnL` vs `ChartMath` — extract shared
  lot-walk; realized-vs-unrealized + short handling differ → pin with tests first.
- **#8 `ChartGeometry.AlignToStep`** re-derives `TimeHelper.FloorToBucketUtc` (missing `EnsureUtc` + ceiling
  mode → not byte-swap).
- **#7 server telemetry money formatters** hand-roll `$`+N2 (JPY-wrong) — but stable invariant N2 may be
  intentional for grep-friendly logs; propose, don't auto-adopt.

## P2-6 — Parsing / misc (NEEDS-CARE)
- **#5 `int.TryParse`/`int.Parse` → `ParsingHelper.TryToInt`** (~15 client UI-input sites). Real behaviour
  change: adds current-culture→invariant fallback (accepts inputs the single-culture parser rejected) → it's a
  fix, must be intentional per-site. `RegisterViewModel` uses throwing `int.Parse` (convert to try-form).
- **#9-residual is DONE** (Order.IsKnownStatus shipped `41b600e`). Do NOT touch the Order-type strings (enums
  already exist; string→enum is FORBIDDEN per CLAUDE.md).
- Signed-percent formatters (5 copies): **already assessed + REFUSED** — genuinely different (decimal vs double,
  F2/0.00/N2/%, culture, sign-at-zero); unifying changes numbers. Leave.

---

*Full per-item detail (line refs, exact bodies) lives in `DEDUP_client_INVENTORY.md` +
`DEDUP_shared_helpers_INVENTORY.md`. This doc is the owner-review shortlist.*
