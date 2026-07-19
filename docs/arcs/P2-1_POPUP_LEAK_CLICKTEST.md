# P2-1 popup `CloseRequested` leak fix — CLICK-TEST CHECKLIST (PREPARE — HOLD FOR KIESH)

**Status:** implemented on `feature/bot-market-realism-v2`, client build clean, adversarial review = SAFE-FIX.
**NOT merged/final** — Kiesh click-tests the popups below before this is considered done. If any popup misbehaves,
revert the commit (single, self-contained) or file the specific popup.

## What changed (and why it's safe)
Every popup view (`CommunityToolkit.Maui.Popup`) used to do `vm.CloseRequested += OnCloseRequested` in its ctor and
**never unsubscribe / never dispose the VM** — so the VM's event pinned the popup in memory after it closed (a leak).
Only `ConvertCurrencyPage` did it correctly. Fix: one shared helper `Helpers/PopupLifecycle.cs`
(`this.WireCloseAndDispose(vm)`) that reproduces the correct pattern — on `CloseRequested` it closes on the main
thread (identical to the old per-view code); on the popup's `Closed` event it unsubscribes both handlers and calls
`vm.Dispose()`. Each VM gained an **idempotent** `Dispose()` that nulls its own events (`CloseRequested`, and where
present `Saved` / `NavigateTo*`). The close behavior is byte-for-byte the same; the only new behavior is the
cleanup on close. `ConvertCurrencyPage` was rerouted through the same helper (behavior identical).

## What to verify (the click-test)
For EACH popup: **open it, interact, then close it every way it can close** — and confirm (a) it closes cleanly, no
freeze/crash, (b) reopening it works (proves nothing was left half-torn-down), (c) any "after-close" action
(navigation, list refresh) still happens.

### Admin → Edit popups (`Views/AdminPageViews/EditPopups/`)
- [ ] **UserEditPopup** — open from the users table; edit + **Save** (row refreshes); reopen; also open + **Cancel/X**.
- [ ] **StockEditPopup** — open; **Save** (stock row updates); reopen; **Cancel/X**.
- [ ] **PositionEditPopup** — open; **Save**; reopen; **Cancel/X**.
- [ ] **FundAdjustPopup** — open; **Save** an adjustment (fund updates); reopen; **Cancel/X**.
- [ ] **OrderDetailsPopup** — open; click **View Buyer / View Seller / View Order** (each should navigate AND close);
      reopen a fresh one afterwards; also plain **Close/X**.
- [ ] **TransactionDetailsPopup** — open; click **View Buyer / View Seller / View Buy Order / View Sell Order**
      (each navigates AND closes); reopen; plain **Close/X**.

### Account popups (`Views/AccountPageViews/`)
- [ ] **DepositWithdrawPage** — open; submit a deposit/withdraw; reopen; **Cancel/X**.
- [ ] **ChangeUsernamePage** — open; submit; reopen; **Cancel/X**.
- [ ] **ChangePasswordPage** — open; submit; reopen; **Cancel/X**.
- [ ] **ChangeEmailPage** — open; submit; reopen; **Cancel/X**.
- [ ] **FundTransactionHistoryPage** — open (loads history); close; reopen.
- [ ] **ConvertCurrencyPage** ⚠ *regression-check only* — this one already worked; it was rerouted through the new
      helper. Convert a currency; confirm the rate updates while open and it closes cleanly. If this one regressed,
      the helper itself is wrong (highest-signal check).

## Notes for the reviewer
- The multi-action popups (OrderDetails/TransactionDetails) fire their navigate event **then** close on the same
  click — verified the navigate is dispatched before the VM is disposed, so navigation must still land.
- Out-of-scope (NOT touched here, flagged for a possible later pass): the openers' own `Saved`/`NavigateTo*`
  subscriptions (they already unsubscribe in a `finally` after the popup returns); and the pre-existing
  `async void`-shaped close lambda (if `CloseAsync` ever throws it's unobserved — carried over verbatim, not new).
