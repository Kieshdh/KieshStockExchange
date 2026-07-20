# DEDUP ARC — BATCH TEST PLAN (for Kiesh)

Per Kiesh's 2026-07-20 call: the NON-CK dedup items are done autonomously and committed on
`feature/bot-market-realism-v2`; **Kiesh test-drives the whole batch here at the end**, then says merge-to-master
(via the normal flow) or flags anything to revert. Each autonomous session appends its item below.

**How to test:** build + run the client (`dotnet run --project KieshStockExchange/KieshStockExchange.csproj -f
net9.0-windows10.0.19041.0`), then walk each row. Money/CK items are deliberately NOT here (they need a soak).

---

## 1. P2-1 — popup `CloseRequested` leak fix  (commit `3700d78`)
**What changed:** all 12 popups routed through one `Popup.WireCloseAndDispose(vm)` helper; the 11 leaking VMs got an
idempotent `Dispose()`. Close behavior is unchanged; the fix is that each popup now unsubscribes + disposes its VM on close.
**Test (full checklist in `P2-1_POPUP_LEAK_CLICKTEST.md`):** open each popup → interact → close every way (Save / Cancel /
X / navigate) → reopen. Highest-signal = **ConvertCurrencyPage** (if the helper were wrong, it regresses first).
**Status:** Kiesh reported "popups working, no errors" — pending final sign-off with the rest of the batch.

## 2. P2-4 #13 — PortfolioTableViewModelBase extraction  (branch `dedup/p2-4-portfolio-table-base`)
**What changed:** the 3 portfolio table VMs (Open Orders / Order History / Transactions) were whole-class near-clones;
lifted their shared skeleton (pager, refresh template, StockId>0 newest-first rebuild, UI-thread marshal, dispose)
into `PortfolioTableViewModelBase<TRow,TSource>`, each VM now overrides small per-table hooks. Behavior-preserving.
**Test (own branch — merge/test separately):** on the Portfolio page, open each of the **Open Orders / Order History /
Transactions** tabs → **pull-to-refresh** works and rows show (right stocks, newest first); on **Open Orders** the
row **Modify** and **Cancel** buttons still act on that row; switch tabs + reopen the page (no stale/duplicated rows).
**Status:** client build 0 errors + adversarial review = PRESERVED; awaiting Kiesh click-test. **On its own branch**
`dedup/p2-4-portfolio-table-base` (pushed) so it can be tested/merged independently.

## 3. P2-4 #14 — DateRangeTableViewModel&lt;T&gt; extraction  (branch `dedup/p2-4-daterange-table-base`)
**What changed:** the Order + Transaction **admin** tables (Admin page → Orders / Transactions tabs) shared ~90 lines
of date/time-range filter code. Lifted the shared skeleton — the From/To date+time pickers + their change hooks, the
stock picker (`PickerStocks` / `SelectedStockFilter`), the username search + `HideAiBots` toggle, the stock/AI-user
lazy load, the username→id resolver, and the **5m / 15m / 1h / 1d** quick-range buttons — into a new intermediate base
`DateRangeTableViewModel<TItem>`. Each concrete table keeps only its own filters (Orders: Status / Side / Type;
Transactions: Currency) and its row loading. Behavior-preserving.
**Test (own branch — merge/test separately):** on the **Admin** page, open the **Orders** tab and the **Transactions**
tab and for each: the **From/To date+time pickers** filter the rows; the **5m / 15m / 1h / 1d** buttons set the range and
refresh; the **Symbol picker** defaults to "Any" and filtering by a stock works; the **username/id search** box filters;
the **Hide AI** switch works; sorting by column headers + paging still work. On Orders also check the **Status / Side /
Type** segment filters; on Transactions the **Currency** picker. Open a row's **Details** popup on each (Orders popup can
cancel an order → list refreshes after close).
**Status:** client build 0 errors + isolated adversarial diff review = PRESERVED; awaiting Kiesh click-test. **On its own
branch** `dedup/p2-4-daterange-table-base` (pushed) so it can be tested/merged independently.

## 4. P2-4 #16 — ModalFormViewModel base extraction  (branch `dedup/p2-4-modalform-base`)
**What changed:** the 7 modal-form popup VMs shared the same boilerplate — a `CloseRequested` event, the
`ErrorMessage`/`HasError` error state, a `Cancel` command, and the close+dispose skeleton. Lifted all of it into a new
`ModalFormViewModel` base; each form keeps only its own fields, validation and `Save`. The 4 admin edit popups keep their
`Saved` event (external code subscribes to it). Close/cancel/error/dispose behavior is unchanged. Affected popups: **Change
Password / Change Email / Change Username** (Account page) and **Edit User / Edit Stock / Edit Position / Adjust Funds**
(Admin page edit dialogs).
**Test (own branch — merge/test separately):** open each of the 7 popups and confirm: it opens with the right title;
**Cancel / X** closes it; a **validation error** (e.g. blank fields, mismatched passwords, bad amount) shows the red error
text and does NOT close; a **valid Save** persists and closes, and the underlying admin table refreshes (Edit User/Stock/
Position/Adjust Funds → the row reflects the change). Open→close→reopen each once (no stale error text or leaked state).
**Status:** client build 0 errors + isolated adversarial diff review = PRESERVED; awaiting Kiesh click-test. **On its own
branch** `dedup/p2-4-modalform-base` (pushed) so it can be tested/merged independently. NOTE: builds on the P2-1 popup-leak
fix (row #1) — those two touch the same popups, so if merging both, merge/test them together.

<!-- autonomous sessions append the next item (P2-4 #17, P2-6, …) below in the same shape, each on its OWN branch -->

