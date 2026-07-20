# Order test pass — findings & fix plan (started 2026-06-05)

Live log for the full manual test pass (P3 stop manageability, P4 brackets incl. TP-only, every
order type, long→short flip, fill markers, the trade-table layout pass). **NOTHING here is
implemented yet** — this is the batch to execute once the pass is complete.

Companion docs:
- `P3_P4_TEST_CHECKLIST.md` — the checklist being worked through (sections 0–H).
- `TEST_FINDINGS_AND_FIXES.md` — prior (already-implemented) batches, for reference.

## Conventions (how Claude logs each finding)
One bullet per finding. Capture: **what's wrong** → **repro** (checklist step id if any) → **expected** →
*(layer/where the fix belongs, filled in when triaged)*. For engine/reservation bugs, also note
**Reserved before/after + the `MarketEngine` log line**.

---

## Findings

### Order placement / engine
- **F1 — Limit-trigger (stop-limit) shows "market" type, should show "limit".** When placing a
  limit trigger, the resulting/second order reads as a **market** order in the table; it's a limit
  order and should read **LIMIT**. Repro: place a Trigger order with "Limit price" checked, then look
  at the Type column. Expected: Type = limit (the promoted order is a limit, not a market).
  *Triage (where to look): `Order.TypeDisplay` / order-type decomposition (Side/Entry/Stop computed
  type), and the stop-limit promotion path in the engine — confirm the fired order keeps its Limit
  entry kind rather than defaulting to market.*
  - **CLOSED 2026-06-11 — verified-correct, regression net in place.** Full top-to-bottom audit
    found no defect in any code path. Verification:
    1. `StopOrderModelTests.Promotion_preserves_Entry_kind_in_TypeDisplay` (4-case theory)
       asserts the armed→promoted display flip across {Buy,Sell}×{Limit,Market} on the model.
    2. `Order.PromoteStop()` only clears `Stop`; `Entry` is untouched.
    3. Server `OrderEntryService.BuildStopOrderAsync` sets `Entry = limitStop ? Limit : Market`
       from the client's `IsLimitSelected` flag; client `PlaceOrderViewModel`'s Trigger+Limit
       branch correctly routes to `PlaceStopLimit{Buy,Sell}OrderAsync`.
    4. Engine post-promote path: `OrderExecutionService.PromoteStopAsync` calls
       `_db.UpdateOrder` which writes `Entry` + `Stop` in the SQL UPDATE column list (verified —
       `PgDBService.Orders.cs:230`). The slippage-cap re-anchor branch is gated on
       `IsSlippageOrder && IsSellOrder` (Market+capped only) so a stop-limit promotion never
       enters it.
    5. `Entry` is never mutated post-construction anywhere in the server (`grep .Entry = EntryType`
       only matches the BUILD methods).
    6. Round-trip: `OrderMapper.ToRow/ToDomain` preserves Entry; `GetOrdersByUserId` SELECT
       reads the column.
    7. UI: both `OpenOrderRow` and `ClosedOrderRow` expose `Type => Order.TypeDisplay`;
       `TradeTableViewModelBase.UpdateFromCache` REPLACES `CurrentView` with fresh rows on
       every cache refresh (no INPC dependency).
    Likely root cause of the original observation: pre-`737a3e4` (Side/Entry/Stop decomposition
    refactor) the legacy single-string OrderType might have routed differently; the refactor
    swept it. Will reopen with specific repro if observed again — the theory test will trip on
    the way down to confirm.

- **F13 — D1: confirmation popup before opening a short (market sell beyond holdings).** A market sell
  that exceeds available holdings (closes long + opens short) should pop an **extra confirmation**
  ("You're about to go short — confirm?") before placing. *Layer: client pre-submit in
  `PlaceOrderViewModel` (and the flip path) — detect sell qty > available shares ⇒ would open/extend a
  short ⇒ `DisplayAlert` confirm; only submit on confirm. Reuse the same gate for F14.*
- **F14 — D3: ALLOW limit sell beyond holdings to open a short (behavior change), gated by the same
  popup.** Currently a limit sell beyond holdings is **rejected** ("no shorting via limit"). User wants
  it **allowed**, with the F13 confirmation popup. *Layer: engine — the limit-sell-beyond-holdings
  reject path must instead open a cash-collateralized short (ties into P1 shorts / the long→short flip
  reservation model); confirm collateral is reserved correctly for a resting limit short. Plus the
  F13 client confirm. This is the bigger of the two — flag for careful triage (reservation/collateral).*

### PlaceOrderView (order ticket) UI
- **F3 — Reorder: TP + Stop-loss go BELOW the quantity.** Current order has SL/Trailing/TP above the
  quantity slider; move the Stop-loss / Trailing / Take-profit block to sit **below** the quantity.
  *Layer: `PlaceOrderView.xaml` row order (+ keep the bracket block's `ShowBracket` visibility intact).*
- **F4 — Thin divider lines between regions.** Group the ticket into visually separated regions with a
  thin line between each (copy the existing line above **Order value** — that one already exists):
  1. Available funds / shares
  2. Limit price / slippage / quantity
  3. Stop-loss / Trailing stop / Take-profit
  4. Order value (existing divider — reuse its style)
  **Important:** the divider *after* the Stop/TP section must **not** show on the **Trigger** tab (no
  bracket section there) — make that divider conditional on `ShowBracket`.
  *Layer: `PlaceOrderView.xaml`; find the existing Order-value separator and reuse its style/key.*

### Modify order panel
- **F5 — Edit existing SL/TP legs in the modify panel (CORRECTED scope).** When editing a bracket
  order, the user should be able to cancel/modify its stop-loss and TP legs too — modify screen looks
  like the place-order screen (SL checkmark + TP entries), keeping the three buttons (Cancel | Remove |
  Confirm). **Correction (supersedes "all orders"):** only surface the legs the order **already has** —
  an order with no stop-loss shows no stop-loss in modify; no TPs → no TP rows. **Keep the order's
  type fixed in modify** (don't let the user add legs an order never had).
  *Layer: `ModifyOrderView.xaml` + `ModifyOrderViewModel` — reuse PlaceOrderView's SL/TP rows but gate
  each on whether the edited order actually has that leg; triage per-leg modify/cancel against the
  engine. Related: F12 (modify the unfilled parent's SL without touching TPs).*
  - **SHIPPED 2026-06-11 (commit f490272).** ModifyOrderViewModel now populates `BracketLegs` from
    `_cache.AllOrders` filtered by ParentOrderId + IsActive (auto-skips Filled/Cancelled legs); a
    new `BracketLegRow` carries label/price/qty/originals/parsed-diff. Confirm dispatches per-leg
    work after the parent modify: removals first (frees reservation), then per-leg
    `ModifyBracketLegAsync` against any price/qty diff. Cancel-of-leg via `CancelOrderAsync`.
    ModifyOrderView.xaml adds a gated `Bracket legs` CollectionView with one row per leg, qty
    visible on TPs only (SL takes from the held pool). Per-leg validation prefixes the offending
    leg label; engine geometry validator (Batch F 1/3) re-checks ordering server-side. Client TFM
    builds clean, 110/110 tests pass.
- **F6 — Auto-close the modify panel when the edited order fills.** Can't modify a filled order; if the
  order being edited transitions to Filled, exit modify mode automatically.
  *Layer: `ModifyOrderViewModel` / `IOrderEditService` — watch the edited order's status (OrdersChanged)
  and end the edit when it's no longer active.*

### Tables / TradePage layout
- **F2 — Persist & surface trigger orders (history + chart marker).** Larger feature, several parts:
  - **Persistence:** keep track of trigger orders so they survive/are queryable. User's lean: **keep
    them as an Order in the existing SQL Orders table** (rather than a separate Triggers table) —
    decide during triage whether the current schema already records armed triggers or whether a new
    table/column is warranted.
  - **Order History display:** limit triggers should appear in **Order History** styled like they are
    in the **OpenOrdersTable** (same Side/Type/status styling).
  - **Chart — activation point marker:** the point where a trigger *activates* should render as a
    **separate colour (blue, theme-dependent)** and **point in the direction the trigger order is
    placed** (i.e. an arrow indicating buy/sell side / above-or-below).
  *Triage: overlaps the previously-deferred "chart trio" (trigger line numbering / on-line cancel).
  Confirm what's already persisted for armed triggers before adding schema. The history-styling reuse
  should lean on the existing `TradeTableSideLabel` / status-pill styles.*

- **Confirmed working:** Trigger orders (place / arm / modify / cancel / fire) — "work perfectly."
  Brackets next.

- **F10 — PlaceOrderContainer still clips off the right edge of the screen (CONFIRMED).** The order
  panel card isn't fully shown — its right side runs past the window; needs the **full card visible
  with a bit of outer spacing**. (This is the right-edge overflow suspected earlier — now confirmed
  real.) *Triage: likely the column budget — chart col0 (`*`) can't shrink below the chart toolbar's
  intrinsic min width, so col1 (orderbook 230) + col2 (panel 260) get pushed past the window. Options:
  let the chart column shrink (cap the toolbar min width / allow wrap), and/or guarantee a right gutter
  so the panel card's right border + ~8px always show. Files: `TradePage.xaml` columns +
  `TradePage.xaml.cs UpdateTradeLayout`.*

- **F11 — "Current stock only" filter checkbox, top-right of the tables card.** A single checkbox in
  the tables card header (top-right) that, when checked, filters **Open Orders / Order History /
  Transaction History** to the current stock only (**leave Positions unaffected**). One shared toggle
  for all three. Unchecked = all stocks, chronological by time. Label is stock-dependent, e.g.
  `GOOG only [x]`, and updates on stock change. Persist the checkbox state in the session.
  *Layer: maps onto the existing `TradeTableViewModelBase.SetShowAll(bool)` / `ShowAll` (checked ⇒
  ShowAll=false). Wire one bound bool on `TradeViewModel` that calls SetShowAll on the three table VMs
  (not Positions), label from `Selected.Symbol`, persisted via the session/selected-stock service.
  Host the checkbox in the SegmentedTabView's existing `HeaderRightContent` / `RightSlot`.*

### Notifications
- _(none yet)_

### Chart
- **F7 — Persist candle resolution + chart viewport in the user session.** Store the selected candle
  time (resolution) AND the chart's current position so returning to the Trade page restores the same
  view. Likely persist **min/max time** (x window) and **low/high** (y window) in user-session storage.
  *Layer: `ChartViewModel` + the session/selected-stock service; mirror how last-watched stock is
  already persisted (see F restore). Confirm whether autofit should be suppressed on restore so the
  saved y-window sticks.*
- **F12 — Show dormant bracket children (pre-parent-fill) on the chart + edit them as one order.**
  Bracket SL/TP legs are currently only visible/editable once the parent fills, so **C8f is not
  testable** (cancel unfilled parent → dormant TPs torn down). Render the **attached/armed legs as
  lines on the chart** (ties into F2 trigger-line work) so they're visible while the parent is still
  unfilled, AND make the bracket modifiable as a single order — e.g. modifying the **unfilled parent's
  stop-loss leaves the TPs unchanged**. Not possible today; should fall out of the F5 modify rework
  plus chart-line rendering.
  *Layer: chart line rendering for Attached children + `ModifyOrderViewModel`/engine per-leg modify;
  re-test C8f once this lands.*
  - **SHIPPED 2026-06-11 (commit 980cc64).** `OpenOrderLine` gains an `IsDormant` flag.
    `ChartViewModel.SyncOpenOrderLines` emits one line per `IsAttached` leg in addition to
    `OpenOrders`. `CandleChartDrawable.DrawOpenOrderLines` picks `IsDormant` up via
    `baseColor.WithAlpha(0.45f)` and swaps the inline label (STOP/B/S → SL/TP) so the
    bracket-child role reads at a glance. `ModifyOrderViewModel.PopulateBracketLegs` widened from
    `o.IsActive` to `(o.IsActive || o.IsAttached)` so the modify panel of an unfilled parent shows
    its SL+TPs (F5's per-leg dispatch routes each through `ModifyBracketLegAsync` — modify the
    parent's SL without touching TPs falls out naturally). `OnCacheOrdersChanged` no longer
    auto-ends edit on `!IsActive` alone; must also be `!IsAttached` (otherwise dragging a dormant
    leg opens the panel and the very next refresh kicks the user out). `ConfirmAsync` routes a
    directly-targeted bracket child through `ModifyBracketLegAsync`. C8f is now testable.

### App lifecycle / logging
- **F8 — Log "logged out" when the MAUI app closes.** On client shutdown, emit a session logout line
  to match the existing "Session: user #X logged in (client MAUI)" line.
  *Layer: client shutdown path (App/MainPage `OnDestroying`/window-closed) → call the same session/
  notification logging route used at login; ensure it actually flushes before exit.*
- **F9 — Swallow TaskCanceledException on shutdown (transactions refresh).** On app close a
  `TaskCanceledException` bubbled from `ApiDataBaseService.GetTransactionsByUserId`
  (`ApiDataBaseService.cs:309`) via `TransactionService.RefreshAsync` (`TransactionService.cs:80`) — an
  in-flight HTTP GET cancelled mid-shutdown. Process still exited 0, but it's noisy. Treat
  cancellation as benign on shutdown (catch `OperationCanceledException`/`TaskCanceledException` and
  log-debug, don't surface), same spirit as the candle-flush shutdown fix.
  *Layer: `TransactionService.RefreshAsync` and/or `ApiDataBaseService` — catch cancellation when the
  app's lifetime token is the one that fired.*

### Other
- **Confirmed working (no changes needed):**
  - Trigger orders (place / arm / modify / cancel / fire).
  - **Brackets** + Stop-loss / Take-profit legs (P4 / C) — working correctly.
  - **TP-only brackets (C8)** — place/fill (SL null, TPs 2 → Filled), **per-leg TP modify** and
    **per-leg TP cancel** (releases that leg's shares) all confirmed in the 00:10–00:25 session.
  - Selling beyond available (reserved by a resting limit) → `InsufficientStocks` — correct reject
    (same family as D2; distinct from the F13/F14 go-short-on-purpose flow).
  - Section **E** — fill markers on chart.
  - Section **F** — last-watched stock restore.
  - **D2** — flip rejected when shares are reserved; got the notification too.
- **Observation to verify (not yet a confirmed bug):** session log shows a bracket place
  `BRACKET Limit qty 20 SL 121.00 TPs 3 → InvalidParameters`, then an immediate retry of the same →
  `PlacedOnBook`. Confirm the first rejection was a legitimate validation (e.g. price relationship /
  marketable at that instant) and not a transient/false reject.

---

## Triage / implementation order

All P3/P4/E/F/D2 behavior confirmed working; the items below are the changes from the pass. Grouped
by area + risk, sequenced so the cheap independent work lands first and the engine-heavy / design-heavy
work (which wants an Ultraplan pass) is isolated at the end. Dependencies called out per batch.

### Batch A — Order-ticket layout (client XAML only, low risk)
- **F3** move Stop-loss / Trailing / Take-profit block **below** the quantity.
- **F4** thin region dividers (funds/shares · limit+slippage+quantity · SL/trailing/TP · order-value),
  reuse the existing order-value separator style; SL/TP divider conditional on `ShowBracket` (hidden on
  Trigger tab).
- Files: `Views/TradePageViews/PlaceOrderView.xaml` (+ a divider style key if one isn't already shared).
- No VM/engine changes. No dependencies.

### Batch B — TradePage layout & table filter (client, low/medium risk)
- **F10** fix PlaceOrderContainer right-edge clip — full panel card visible + outer gutter. Likely the
  chart toolbar's intrinsic min width stops col0 (`*`) shrinking, pushing col1(230)+col2(260) past the
  window. Fix in `TradePage.xaml` columns + `TradePage.xaml.cs UpdateTradeLayout` (cap chart min width
  / guarantee a right gutter).
- **F11** "current stock only" checkbox, top-right of tables card (SegmentedTabView `RightSlot`/
  `HeaderRightContent`). One shared bool ⇒ `TradeTableViewModelBase.SetShowAll(false)` on OpenOrders /
  OrderHistory / Transactions (NOT Positions). Label `"{Selected.Symbol} only"`, updates on stock
  change, state persisted in session (mirror last-watched-stock persistence).
- Files: `TradePage.xaml(.cs)`, `TradeViewModel`, the three table VMs (already have `SetShowAll`),
  session/selected-stock service.
- Dependency: none, but touches the same `TradePage.xaml` as Batch A — do A then B.

### Batch C — Lifecycle / robustness (client, low risk)
- **F6** auto-close the modify panel when the edited order fills (watch OrdersChanged; end edit when no
  longer active). `ModifyOrderViewModel` / `IOrderEditService`.
- **F8** log a "logged out" session line on MAUI app close (mirror the login line; flush before exit).
- **F9** swallow `TaskCanceledException`/`OperationCanceledException` on shutdown in
  `TransactionService.RefreshAsync` → `ApiDataBaseService.GetTransactionsByUserId:309`.
- Independent; can land anytime.

### Batch D — Order-type display bug (small, investigate first)
- **F1** limit-trigger (stop-limit) shows "market" instead of "limit". Check `Order.TypeDisplay` /
  Side-Entry-Stop decomposition and the stop-limit promotion path. Small, but do before Batch F since
  trigger rows will be shown in Order History there.

### Batch E — Chart/session persistence (client, medium)
- **F7** persist candle resolution + chart viewport (min/max time, low/high) in session; restore on
  return; decide whether to suppress autofit on restore. `ChartViewModel` + session service.
- Independent.

### Batch F — Modify rework (client + engine, medium — DESIGN)
- **F5** modify panel edits the **existing** SL/TP legs only (no SL ⇒ no SL row; no TPs ⇒ no TP rows),
  order type stays fixed; keep Cancel | Remove | Confirm. Reuse PlaceOrderView SL/TP rows gated per
  leg. Needs engine per-leg modify/cancel semantics confirmed.
- **F12** (depends on F5 **and** Batch G-chart-lines) show dormant bracket children (Attached, before
  parent fills) as chart lines so they're visible/editable; modify the unfilled parent's SL without
  touching TPs; **re-test C8f** afterwards.
- Files: `ModifyOrderView.xaml`, `ModifyOrderViewModel`, engine modify path; chart rendering for F12.

### Batch G — Trigger/bracket visibility: history + chart lines (client + maybe schema — DESIGN)
- **F2** persist trigger orders (lean: keep as an Order row — confirm current schema already records
  armed triggers before adding a table/column); show limit triggers in **Order History** with
  OpenOrders-style formatting (reuse `TradeTableSideLabel` / status pills); chart **directional
  activation marker** in a separate theme-dependent colour (blue) pointing toward the trigger side.
- Overlaps the previously-deferred "chart trio" (trigger line numbering / on-line cancel) and provides
  the chart-line infra F12 needs. Sequence: G's chart-line rendering before/with F12.

### Batch H — Shorting via confirmation (client + engine — ULTRAPLAN / careful)
- **F13** market sell beyond holdings ⇒ client confirm popup ("go short?") before submit.
- **F14** ALLOW limit sell beyond holdings to open a cash-collateralized short (currently rejected),
  gated by the same popup. Engine reservation/collateral change — ties into P1 shorts + long→short flip
  model. **Highest risk; reservation-conservation critical — wants its own Ultraplan/design pass.**

### Verify (not a code change yet)
- Bracket log showed `BRACKET Limit … SL 121.00 TPs 3 → InvalidParameters` then an identical retry →
  `PlacedOnBook`. Confirm the first reject was legitimate (price relationship / marketable at that
  instant) vs a transient false reject. Check `OrderEntryService.PlaceBracketAsync` validation.

### Suggested sequence
A → B → C → D → E (all low/medium, independent) → F (modify) → G (history + chart lines) → H (shorts).
F12 sits at the F/G seam (needs both). H is the isolated engine-risk batch.

### Notes for the Ultraplan pass
- Heaviest design needed: **F14** (limit-short collateral/reservation), **F2+F12** (trigger/bracket
  persistence + chart-line rendering + per-leg modify). The rest are mostly mechanical.
- Reuse existing infra wherever possible: `SetShowAll`/`ShowAll`, `TradeTableSideLabel`/status pills,
  last-watched-stock session persistence, the candle-flush shutdown-cancellation pattern, the
  PlaceOrderView SL/TP rows, `IStockNav`/`GoToStock` (just added).
- Conservation invariant to preserve in any engine change:
  `Σ(order.CurrentSellReservedQty) == Position.ReservedQuantity`.
