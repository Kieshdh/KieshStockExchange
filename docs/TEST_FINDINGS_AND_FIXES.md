# Post-test findings & fix plan

Collected during the P3/P4 manual test pass. **Nothing here is implemented yet** — this is the
batch plan to execute in one go once the list is complete. Each item: observation → approach →
files → open questions/risks.

Conventions reused:
- Currency parsing/formatting: `CurrencyHelper` (`Format`, `Parse`, `FormatForEdit`, `DecimalPlaces`).
- Theme colour keys (per-theme, e.g. `Theme.ExchangeDark.xaml`): `BuyGreen #22C55E`, `SellRed #EF4444`,
  `TextMuted #6F7A93`, `Divider`. Use `DynamicResource` so the arrows/labels follow the active theme.
- Styles live in `Resources/Styles/` (`TradeStyles.xaml` has the Form*/OrderTotals* styles).

---

## Batch 1 — PlaceOrderView revamp (order ticket)

All items touch `Views/TradePageViews/PlaceOrderView.xaml` and
`ViewModels/TradeViewModels/PlaceOrderViewModel.cs` unless noted. This is a coordinated rework of the
ticket, so implement the VM model change (B1.1) **first**, then the XAML rows on top of it.

### B1.1 — Trigger becomes a third order-type tab: `Market | Limit | Trigger`
**Now:** order-type segment is 2 tabs (`Market`/`Limit`, `SelectedTypeIndex` 0/1) and a separate
"Trigger order" checkbox (`IsStopOrder`). **Want:** a 3-tab segment; Trigger is no longer a checkbox.

**Approach (VM model change — the load-bearing edit of this batch):**
- Add a third `SegmentedTabItem Header="Trigger"` to the order-type `SegmentedTabView` in XAML.
- Re-base the computed selectors on `SelectedTypeIndex` 0=Market, 1=Limit, 2=Trigger:
  - `IsStopOrder` becomes a derived `=> SelectedTypeIndex == 2` (drop the `[ObservableProperty]`
    backing field + `OnIsStopOrderChanged`; fold its side-effects into `OnSelectedTypeIndexChanged`).
  - Introduce `TriggerHasLimit` (see B1.2) to recover the stop-market vs stop-limit choice that the
    old `Market`/`Limit` segment used to provide while Trigger was a modifier.
  - Recompute `IsMarketSelected`/`IsLimitSelected` so the *entry kind* under Trigger is driven by
    `TriggerHasLimit`, not the segment (segment index 2 == Trigger, which has no market/limit of its own).
- `OnSelectedTypeIndexChanged` must re-fire `IsStopOrder`, `ShowSlippageGuard`, `ShowBracket`,
  `IsLimitSelected`, `IsMarketSelected`, and reset bracket if leaving a buy-market.
- `RecomputeUi`/`ValidateInputs`/`PlaceOrderAsync` reference `IsStopOrder`,`IsMarketSelected`,
  `IsLimitSelected` heavily — re-verify each branch still maps to the right `_orders.Place*` call after
  the redefinition (this is where regressions will hide).

**Risk:** brackets are buy+non-stop only (`ShowBracket => IsBuySelected && !IsStopOrder`). Confirm a
Trigger tab selection still hides the bracket block and the bracket/trigger mutual-exclusion holds.

### B1.2 — Under Trigger: "Limit price" checkbox picks stop-market vs stop-limit
**Want:** when Trigger tab is active, show a `Limit price` label + checkbox. Checked ⇒ stop-limit
(reveal the limit-price entry); unchecked ⇒ stop-market.

**Approach:**
- Add `[ObservableProperty] bool _triggerHasLimit`. Under Trigger, `IsLimitSelected` ⇒ `TriggerHasLimit`,
  `IsMarketSelected` ⇒ `!TriggerHasLimit`. (Outside Trigger, keep segment-driven.)
- XAML: a `Grid ColumnDefinitions="*,Auto"` row (label + `CheckBox IsChecked="{Binding TriggerHasLimit}"`),
  `IsVisible="{Binding IsStopOrder}"`. The existing **Limit price** entry block (`IsVisible` currently
  `IsLimitSelected`) already lights up correctly since `IsLimitSelected` now folds in `TriggerHasLimit`.
- Keep the existing trigger-price entry block as-is.

### B1.3 — Stop-loss is its own labelled checkbox (decouple from the single "Bracket" toggle)
**Now:** one `IsBracketOrder` checkbox gates SL + all 3 TP entries together. **Want:** a `Stop loss`
label + checkbox that independently shows/hides just the SL entry.

**Approach:**
- Add `[ObservableProperty] bool _hasStopLoss`. SL entry block `IsVisible="{Binding HasStopLoss}"`.
- "Bracket" is no longer a user toggle; it becomes *implicit*: an order is a bracket when
  `ShowBracket && (HasStopLoss || TpCount > 0)`. Add a computed `IsBracket => ShowBracket && (HasStopLoss || TpCount > 0)`
  and replace the `IsBracketOrder` submit branch with it. Remove the `IsBracketOrder` property +
  `OnIsBracketOrderChanged` (its mutual-exclusion with stop moves to the Trigger-tab logic in B1.1).
- The SL+TP container should only render under `ShowBracket` (buy, non-trigger).

**Open question / RISK (must resolve before coding the submit):** `PlaceBracketAsync(... decimal stopPrice ...)`
takes a **non-nullable** stop price (`IOrderEntryService.cs:38`). If SL is now optional (TP-only bracket),
either (a) the engine must accept "no SL" — inspect `OrderEntryService.PlaceBracketAsync` +
`BracketCoordinator` to see whether `stopPrice <= 0` is treated as "no protective stop", or (b) keep the
rule "a bracket requires a SL" and only allow TP-only if the engine supports it. **Decision needed** —
will read the engine impl first and surface options. Until resolved, conservative path: require
`HasStopLoss` whenever `TpCount > 0` (validate), matching current engine contract.

### B1.4 — Take-profit stepper: `◀ [n] ▶`, 0–3, side-coloured arrows
**Want:** a `Take profit` label + a stepper. Right triangle increments (max 3), left decrements (min 0).
Active arrows coloured by side (buy=green / sell=red); inactive (at bound) greyed, theme-paired. Show
exactly `n` TP rows. Each row prefixed `TP1/TP2/TP3`; qty entry made smaller.

**Approach:**
- VM: `[ObservableProperty] int _tpCount = 0;` with `IncrementTpCommand` (`if (TpCount<3) TpCount++`) /
  `DecrementTpCommand` (`if (TpCount>0) TpCount--`). Computed visibility: `ShowTp1 => TpCount>=1`, etc.
  Computed colours: `TpIncrementColor => TpCount<3 ? sideColor : muted`, `TpDecrementColor => TpCount>0 ? sideColor : muted`
  where `sideColor = IsBuySelected ? BuyGreen : SellRed`. Re-fire these from `OnTpCountChanged` and
  `OnSelectedSideIndexChanged`. (Resolve colours via `Application.Current.Resources` lookups like the
  existing `SubmitButtonColor` pattern, or expose as `Color` props bound in XAML.)
- XAML stepper: `Grid ColumnDefinitions="*,Auto,Auto,Auto"` — `Take profit` label, then two
  `Button`/`Label`-as-glyph using `◀`(U+25C0)/`▶`(U+25B6) with `TextColor="{Binding TpDecrementColor}"`
  /`TpIncrementColor`, `Command` to the de/increment, and a centre `Label Text="{Binding TpCount}"`.
  Prefer Buttons styled flat (transparent bg) so they're tappable; glyph colour via binding.
- TP rows: prefix each `Grid` with a `TPn` label column, shrink the qty column (e.g.
  `ColumnDefinitions="Auto,*,80"` → label, price (wide), qty (narrow)). Bind row `IsVisible` to
  `ShowTp1/2/3`.
- Submit: build the TP list from only the first `TpCount` rows (the existing `AddTp` calls already skip
  blank/zero rows, so this is naturally bounded; just stop adding past `TpCount`).

**Note:** brackets are buy-only today, so the "sell=red" arrow state is mostly academic for now, but wire
it by side anyway so it's correct if/when short brackets land.

### B1.5 — Currency auto-format on price entries (format on blur, strip on focus)
**Want:** price fields show `$10.20` when not being edited; on focus they strip to editable `10.20`
(or `10.2`); on blur they reformat to the currency.

**Approach (view-only behaviour — correct layer, avoids per-field code-behind):**
- New `Behaviors/CurrencyEntryBehavior.cs` (`Behavior<Entry>`): bindable `CurrencyType Currency`.
  - `OnFocused`: set `Entry.Text = CurrencyHelper.FormatForEdit(parsed, Currency)` (digits + decimal sep only).
  - `OnUnfocused`: parse with `CurrencyHelper.Parse(text, Currency)`; if non-null set
    `Entry.Text = CurrencyHelper.Format(value, Currency)`; if blank/invalid leave empty.
- Attach to LimitPrice, TriggerPrice, Stop-loss price, and each TP price entry. Bind
  `Currency="{Binding Selected.Currency}"`.
- **Interaction to fix (important):** the bound VM strings will now sometimes hold `$10.20`. The VM
  currently parses prices via `ParsingHelper.TryToDecimal` which uses `NumberStyles.Float` and will
  **fail** on a currency-symbol string. Switch the price getters (`LimitPrice`, `StopPrice`,
  `BracketStopPrice`, the TP `AddTp` parse) to `CurrencyHelper.Parse(string, Selected.Currency)` so both
  the formatted (resting) and stripped (focused) forms round-trip. Quantity stays integer parsing.
- **Interpretation flag:** I read "auto format … when exiting the entry" as **format-on-blur**, not
  live-format-while-typing (live reformat fights the caret). Confirm if you actually wanted live.

### B1.6 — Ticket scrolls; chart leads the vertical height
**Want:** adding rows must not grow the chart; the order panel scrolls internally. Chart height leads.
**Now:** `PlaceOrderView` already wraps its body in a `ScrollView`, but `TradePage` wraps the whole grid
in an outer `ScrollView`, so the chart row (`*`) collapses to content height and nothing is bounded —
the page grows instead of the panel scrolling.

**Approach:**
- Make the chart the height leader and bind the order panel to it. In `TradePage.xaml` give
  `ChartView` `x:Name="ChartCard"` and bind the PlaceOrder/Modify cell (or the `PlaceOrderView` Border)
  `HeightRequest="{Binding Source={x:Reference ChartCard}, Path=Height}"`. The panel's existing inner
  `ScrollView` then scrolls within that fixed height. Chart already has `MinimumHeightRequest=300`.
- Verify behaviour inside the outer page `ScrollView`: an `x:Reference` to a sibling's runtime `Height`
  is stable regardless of the scroll wrapper, so this holds. If the outer ScrollView still lets the row
  balloon, the fallback is to drop the outer ScrollView around the trading grid (keep page chromeless)
  — but try the x:Reference bind first as the minimal change.
- Apply the same height bind to the OrderBook cell only if it shows the same growth (it doesn't grow, so
  likely leave it).

**Open question:** confirm the desired resting chart height (keep `MinimumHeightRequest=300`, or set a
concrete `HeightRequest`?). x:Reference binding works either way.

### B1.7 — Zero currency values show `$0.00`, not `-`
**Want:** when a currency value is zero (order value, available funds), render `$0.00` not `-`.
**Approach (`RecomputeUi` + initial defaults in PlaceOrderViewModel):**
- `AvailableFundsDisplay`: drop the `> 0 ? … : "-"`; always `CurrencyHelper.Format(UserFund.AvailableBalance, Selected.Currency)`
  (zero → `$0.00`). Keep the "Convert cash first" hint for the no-fund case.
- `OrderValue`: replace `total > 0 ? … : "-"` with `CurrencyHelper.Format(total, Selected.Currency)`.
- Change the field initialisers `_availableFundsDisplay = "-"` and `_orderValue = "-"` to a zero-format
  (or just let the first `RecomputeUi` set them).
- **Leave `AvailableSharesDisplay` as `0/0 SYM`** — shares are a count, not currency, so `$0.00` doesn't
  apply; only the `-` (no stock selected) stays for shares. (User said "like order value or available
  funds" — i.e. the currency ones.)

---

### Batch 1 implementation order (when greenlit)
1. B1.1 + B1.2 VM model change (3-tab + TriggerHasLimit) — re-verify all submit branches.
2. B1.3 stop-loss toggle + resolve the optional-SL engine question.
3. B1.4 TP stepper (VM commands/colours + XAML rows).
4. B1.5 CurrencyEntryBehavior + switch price parsing to CurrencyHelper.Parse.
5. B1.6 chart-leads height bind.
6. B1.7 zero formatting.
7. Build client TFM, manual smoke of every order kind (market/limit/trigger×limit, SL-only, TP-only,
   SL+TP, flip) to catch B1.1 branch regressions.

---

## Batch 1 — IMPLEMENTED (2026-06-05)

All seven items shipped locally. Server + Tests + client (net9.0-windows TFM) build clean; 29/29 tests pass.
Nothing pushed.

Decision on the SL/TP fork: **TP-only allowed (engine change)** — the bracket engine now accepts a
take-profit-only bracket (no protective stop).

Files changed:
- **VM** `PlaceOrderViewModel.cs`: `IsStopOrder` is now computed (`SelectedTypeIndex==2`); added
  `TriggerHasLimit`, `HasStopLoss`, `TpCount`, `IsBracket`, `ShowTp1/2/3`, `TpDecrement/IncrementColor`,
  `Increment/DecrementTpCommand`; price getters + AddTp/ValidTp parse via `CurrencyHelper.Parse`
  (currency-aware, round-trips the formatted/stripped forms); zero currency → `$0.00`; submit passes
  `decimal? slPrice` (null when no SL).
- **XAML** `PlaceOrderView.xaml`: Market|Limit|Trigger segment; under-Trigger "Limit price" checkbox;
  independent "Stop loss" checkbox; `◀ [n] ▶` TP stepper; `TPn` rows with narrow qty; `CurrencyEntryBehavior`
  on every price entry.
- **New** `Behaviors/CurrencyEntryBehavior.cs`: format-on-blur / strip-on-focus (mirrors entry BindingContext).
- **New style** `TradeStyles.xaml` → `StepperArrowButton` (flat, per-arrow bound TextColor).
- **Layout** `TradePage.xaml`: `ChartView x:Name="ChartCard"`; order-panel cells bind
  `HeightRequest` to the chart's `Height` (chart leads, panel scrolls).
- **Engine (TP-only)** — `PlaceBracketRequest.StopPrice`, `IOrderEntryService`/`OrderEntryService`,
  `IOrderExecutionService`/`OrderExecutionService`, `ApiOrderEntryClient`, `ApiOrderExecutionService`
  all take `decimal? stopPrice` / `Order? stopLoss`. `OrderEntryService` requires ≥1 TP when no SL.
  `BracketCoordinator.OnParentFillAsync`/`OnChildFillAsync` gained a no-SL branch.

**Conservation design for the TP-only path (the reviewable part):**
- Two reservation models, selected by SL presence. **SL present:** unchanged Model B — the SL owns the
  whole pool on `Position.ReservedQuantity`, TPs reserve 0. **SL absent:** each armed TP owns its own
  reservation (standard sell-limit), exactly the legs covered by `held` via fill-up-whole-legs.
- Invariant preserved either way: `Σ(order.CurrentSellReservedQty) == Position.ReservedQuantity`.
  Model-B: SL.CSR(=held)+ΣTP.CSR(=0). TP-only: ΣTP.CSR(=Σ armed qty). The unallocated remainder of a
  partial TP-only bracket stays unreserved (correct — no stop protecting it).
- **Reconciler/clamp** (`AccountsCache`) is CSR-summed and already counts open limits → TP-only TPs
  (open limits with CSR=qty) reconcile correctly, no change needed.
- **Cancel path** (`OrderCanceller`) is CSR-driven and *not* bracket-aware → a Model-B TP (CSR=0)
  releases nothing; a TP-only TP (CSR=qty) releases its shares. Single-TP cancel + unfilled-parent
  teardown both correct without coordinator changes (teardown only fires on an unfilled parent, where
  all TPs are still Attached/CSR=0).
- **Fill dispatch** unchanged: parent via `IsBracketParent`, TP via `IsBracketChild && IsLimitOrder`.

**Follow-up (not blocking manual test):** add an xUnit test for `OnParentFillAsync` no-SL arming
(reserves per-TP) and a TP-only `OnChildFillAsync` retire path; both need the engine mocks the existing
bracket tests use. Worth locking down before this goes to prod.

---

## Batch 2 — PlaceOrderView polish (IMPLEMENTED 2026-06-05, client-only)

Client builds clean (0/0); 29/29 tests pass. Nothing pushed. All in `PlaceOrderViewModel.cs`,
`PlaceOrderView.xaml`, `TradePage.xaml`.

- **Stop-loss label morph:** removed the inline SL caption; the toggle-row label is now
  `StopLossLabel` ("Stop-loss" unchecked / "Stop-loss price" checked).
- **Trigger limit morph:** the under-Trigger checkbox label is `TriggerLimitLabel` ("Limit order" /
  "Limit price"); the shared limit entry's inline caption shows only on the plain Limit tab
  (`ShowPlainLimitLabel`).
- **Assets at top:** Available funds + shares (+ hint) moved directly under the Market|Limit|Trigger
  segment, for all tabs.
- **Narrow tabs:** order-type `SegmentedTabView` `MinTabWidth="0"` so buttons hug labels (was clipping
  "Trigger").
- **Slippage slider hides on None:** new `ShowSlippageSlider => ShowSlippageGuard && !NoSlippageGuard`
  gates the slider row.
- **Quantity slider:** `MaxQuantity`/`SliderMax` (affordable for buy, held-available for sell, via
  `ComputeMaxQuantity`), `QuantitySliderValue` snaps to a 0/25/50/75/100% dot when within 3%, else to
  the nearest whole share; writes back to `Quantity`. Re-entrancy guarded by `_suppressSliderSnap`.
  Dots are 5 `BoxView`s overlaid on the slider. `SetQuantityPercent` now reuses `ComputeMaxQuantity`.
- **Trailing row (P5 UI prep):** `HasTrailing` checkbox under the bracket; mutually exclusive with
  `HasStopLoss` (each deselects the other). No runtime logic yet.
- **TP spacing:** stepper + TP rows wrapped in a `Spacing="2"` stack so the stepper→TP1 gap matches the
  label/entry rhythm.
- **Layout / scroll (Option A) — RESOLVED after iteration; confirmed "perfect":** removed the
  page-level `ScrollView` in `TradePage.xaml`; rows are `Auto, Auto, 7*, 3*` (nav, full-width symbol
  bar, chart-area 70%, tables 30%). Chart/orderbook/order-panel sit in the star row (row 2), symbol bar
  is full-width (row 1). Two further fixes were needed to make it actually work:
  1. **Pinned footer:** in `PlaceOrderView.xaml` the order-value + submit CTA live in a fixed `Auto`
     row *below* the ScrollView (only the form scrolls). Right-padding gutter so the scrollbar doesn't
     hover over fields. This makes the button always reachable and stops the show/hide-bracket budge.
  2. **Deterministic panel height (the load-bearing fix):** the panel's inner `ScrollView` reports its
     full content as desired height, so MAUI inflated its star row past the window and clipped the
     pinned footer. A sibling-`Height` binding (`x:Reference` chart) is **circular** (panel←chart←row←
     panel) — it converges on first layout but **overshoots on resize** (bigger window re-clipped).
     Replaced with imperative sizing in `TradePage.xaml.cs` `UpdatePanelHeights()`, hooked to
     `RootGrid`/`TopNav`/`SymbolBar` `SizeChanged`:
     `rowHeight = (RootGrid.Height - 40 - TopNav.Height - SymbolBar.Height) * 0.7 - 4`, applied to
     `PlaceHost`/`ModifyHost`/`OrderBookHost` (redundancy-guarded). Loop-free (never reads the panel's
     own size) → re-settles on every resize. The `40` (Padding 16 + RowSpacing 24) / `-4` safety are
     the tunable constants if a bottom gap or clip ever appears.

### DEFERRED — item 8: stop-loss + take-profits on Sell / Short brackets
Web search **confirms the user**: for a short, the bracket SL is a **buy-stop** (above entry) and the
TPs are **buy-limits** (below entry), OCO-grouped — standard at Schwab / Kraken / IBKR.

Not bundled tonight because it's the conservation-critical one and the current engine **hard-rejects
shorts**: `BracketCoordinator` is documented "long brackets only … short brackets rejected (risk #7)"
and reserves **shares** for sell-side legs. A short bracket inverts the whole reservation model:
- Parent = short sell (opens short, posts cash collateral at fill — interacts with P1 shorts).
- SL = buy-stop, TPs = buy-limits → the protective legs reserve **cash** (to buy back), not shares.
- So `OrderEntryService.PlaceBracketAsync` (parent `Side=Buy`, sell SL/TPs) and `BracketCoordinator`
  (ReserveStock pool) both need a mirrored cash-reserving path; plus short-collateral release as TPs/SL
  buy back. This is the same class of change the long bracket was Ultraplan-hardened for.

**Decision (2026-06-05):** deferred to **after the test pass** and **folded into P5 (trailing)** —
documented in `ADVANCED_ORDERS_PLAN.md` → "Patch 5 … P5 also covers: short brackets". Worth an Ultraplan
pass on the reservation/collateral design. UI prep: flip `ShowBracket` to also allow Sell once the engine
mirror lands; the SL/TP/trailing rows already exist and just need side-aware captions + price-side
validation (SL above / TPs below for a short).

---

## Batch 3 — Quiet the noisy bot logs for order testing (PLANNED, not implemented)

**Goal:** during the manual order test pass, silence the routine chatter from **scaler, Fx, sentiment,
botstats, boteconomy** so the `MarketEngine` / settlement / ConservationProbe lines stand out — and make
it a one-step toggle back.

**How logging works here (verified):** the server uses **Serilog** (`Program.cs:54`
`UseSerilog(... ReadFrom.Configuration ...)`). Base sinks live in `appsettings.json` → `Serilog`
(Console + rolling File). The live BotDashboard / web log viewer is **also a Serilog sink**
(`InMemoryTelemetrySink : ILogEventSink`, attached via `.WriteTo.Sink(...)` at `Program.cs:56`). So
**one** Serilog `MinimumLevel.Override` per source silences console + file + the in-app viewer together —
no separate Microsoft `Logging:LogLevel` changes needed.

**The five sources → their Serilog SourceContext (full type name; all log via `ILogger<T>`):**
| Source | Override key |
|---|---|
| scaler | `KieshStockExchange.Services.BackgroundServices.Helpers.BotScalerService` |
| sentiment | `KieshStockExchange.Services.BackgroundServices.Helpers.BotSentimentService` |
| botstats | `KieshStockExchange.Services.BackgroundServices.Helpers.BotStatsLogger` |
| boteconomy | `KieshStockExchange.Services.BackgroundServices.Helpers.BotEconomyTelemetry` |
| Fx | `KieshStockExchange.Services.MarketDataServices.FxRateService` |

(All five emit their routine lines at **Information**, so `"Warning"` mutes the noise while keeping any
real warnings/errors from them visible. Use `"Error"` for total silence.)

**Where:** put it in **`appsettings.Development.json`** (local only — prod runs the Production env, so this
never reaches the server). That file has no `Serilog` section today; Serilog's `ReadFrom.Configuration`
**merges** it onto the base, so adding just a `MinimumLevel:Override` block layers these keys on top of the
base's existing `Microsoft.AspNetCore` override (base Console/File sinks stay). The .NET JSON config
provider allows `//` comments + trailing commas, so a marker comment is fine.

**Exact block to add to `appsettings.Development.json`** (sibling of the existing `"Logging"` key):
```jsonc
  // === QUIET BOTS (testing) — delete this whole "Serilog" block to restore full bot logging ===
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "KieshStockExchange.Services.BackgroundServices.Helpers.BotScalerService":     "Warning",
        "KieshStockExchange.Services.BackgroundServices.Helpers.BotSentimentService":  "Warning",
        "KieshStockExchange.Services.BackgroundServices.Helpers.BotStatsLogger":       "Warning",
        "KieshStockExchange.Services.BackgroundServices.Helpers.BotEconomyTelemetry":  "Warning",
        "KieshStockExchange.Services.MarketDataServices.FxRateService":                "Warning"
      }
    }
  },
```

**Toggle back on (any one):** delete the `"Serilog"` block, or flip the values `"Warning"` →
`"Information"`, or comment the lines with `//`. Restart the server (overrides are read at startup).

**Notes / caveats:**
- Do **not** silence the whole `...BackgroundServices.Helpers` namespace — that would also mute
  `ReservationAuditor`, `BotFailureTracker`, `BotCashInjector`, etc. that are useful during conservation
  testing. Target the 5 classes explicitly.
- `appsettings.json` (base) is the alternative if you ever want them quiet in prod too — but default to
  the Development file to keep prod logging intact.
- Leave `ConservationProbe` / `ReservationAuditor` / `MarketEngine` at Information — those are exactly
  what the test pass watches.

---

## Batch 4 — Second test-pass findings (PLANNED, not implemented)

Grouped fixes from the 2026-06-05 screenshots. A1–A2, A4–A7, A9–A11 confirmed correct. Implement all in
one go.

### B4.1 — Log deposits / withdrawals of human users
- Flow: `EngineController.DepositWithdraw` → `IUserPortfolioService.DepositAsync` / `WithdrawAsync`
  (`EngineController.cs:23-37`). This endpoint is JWT-authed and self-only (`cmd.UserId == caller`); bots
  top up via `BotCashInjector` calling the service **directly**, never this HTTP route — so logging here
  is **human-only by construction** (verify `BotCashInjector` doesn't route through the controller).
- Add an `ILogger` to `EngineController` (or a `loggerFactory.CreateLogger("Funds")` like
  `OrderController` does for `"MarketEngine"`) and log one Information line per deposit/withdraw:
  user, kind, amount+currency, note, ok/failed. Also consider `ConvertInternal` (same controller) for
  symmetry — but the ask was deposit/withdrawal, so that's the priority.
- Category `"Funds"` keeps it visible during the order pass and easy to filter; it's unaffected by the
  Batch 3 bot-silencing.

### B4.2 — Quantity slider dots: render on top + align (PlaceOrderView.xaml)
- Currently the dots `Grid` is the FIRST child of the slider `Grid`, so the Slider renders **on top**
  and hides them. **Swap order** (Slider first, dots second) and keep `InputTransparent="True"` on the
  dots overlay so drags still reach the slider.
- They sit a few px high → nudge down (the dots overlay `VerticalOptions="Center"` plus a small top
  `Margin`, or match the track centre). Make the dot `BoxView`s a touch wider and the overlay side
  `Margin` narrower so the end dots line up with the track ends. User will fine-align after — just get
  them on-top and close.

### B4.3 — Slippage guard slider: add matching left inset (PlaceOrderView.xaml)
- The quantity slider overlay uses `Margin="10,0"`; the slippage slider row has none, so they don't
  line up. Add the same left inset (`Margin="10,0,0,0"` or a shared value) to the slippage slider grid.

### B4.4 — Scrollbar flush to the right (PlaceOrderView.xaml)
- The gutter works but the bar isn't at the panel edge: the `Border` (`TradingPanelStyle`,
  `Padding="12"`) insets the ScrollView 12px, so the scrollbar sits 12px in. Fix by making the ScrollView
  reach the panel's right edge — local-override the panel `Border` padding to near-zero on the right
  (e.g. `Padding="12,12,2,12"`) and keep the content's right gutter via the scroll VSL padding so labels
  still clear the bar. Finicky; user will eyeball.

### B4.5 — Shared toggle-row style + tighten A3 spacing + checkbox position (PlaceOrderView.xaml + styles)
- Create a shared style/template for the "label + checkbox" rows (Stop-loss, Trailing, Trigger-limit,
  Slippage-None) so spacing + checkbox alignment are consistent. Put it in `TradeStyles.xaml`
  (e.g. a `ToggleRowGrid` style and/or a `ToggleRowLabel` style; checkbox `HorizontalOptions="End"`).
- "Checkmark too far left": the checkbox is in the `Auto` right column but reads left — right-align it
  (HorizontalOptions=End) and/or trim CheckBox default padding in the shared style.
- A3 spacing: under Trigger, the morphing "Limit price" checkbox row → limit-price entry gap is the
  outer VSL `Spacing="12"`. Group the trigger-limit checkbox row + the limit entry in a tight
  `Spacing="2"` stack (matching label→entry rhythm) so the entry hugs its label.

---

## Polish backlog (deferred — good enough for now)

### Quantity slider dot alignment (PlaceOrderView.xaml)
"Good enough" as of 2026-06-05 — revisit when doing UI polish. The dots and the slider thumb travel
aren't pixel-aligned. All the knobs are factored out:
- **Dots** now share the `SliderDot` style in `TradeStyles.xaml` (size = `WidthRequest`/`HeightRequest`,
  `CornerRadius` = half for a circle, `Color`). Tune all five at once there.
- **Dot row position**: the dots overlay `Grid Margin="2,2,2,0"` in the quantity slider — left/right
  value slides the end dots in/out; top value nudges them onto the track centre.
- **Thumb reach**: the quantity `Slider` `Margin="-8,0"` + `WidthRequest="230"` — more-negative margin
  pushes the thumb further toward the ends (past the glow inset); width matches the panel.
- **Snap stickiness**: `PlaceOrderViewModel.OnQuantitySliderValueChanged`, `bestDist <= 0.10` magnet.
- Goal: set the `Slider` `Margin`/`WidthRequest` so the thumb's travel ends sit on the first/last dots,
  then nudge the dot row's side `Margin` so the dot centres match the thumb centres. A more robust fix
  (no manual px) would be to draw the dots in the chart/Graphics layer or compute positions from the
  slider's actual track width rather than an overlay Grid.

### B4.6 — A3: show slippage guard for stop-market (Trigger without limit) (PlaceOrderViewModel.cs)
- Want: Trigger tab with "Limit order" unchecked (= stop-market) shows the slippage guard + slider like
  a plain market order. Currently `ShowSlippageGuard => IsMarketSelected && (!IsStopOrder || IsSellSelected)`
  hides it for a **buy** stop-market.
- Change to `ShowSlippageGuard => IsMarketSelected` (true for plain market AND stop-market, false for any
  limit). `ShowSlippageSlider` already gates on `!NoSlippageGuard`.
- **Engine-wiring flag:** for a SELL stop-market the cap is already passed
  (`PlaceStopMarketSellOrderAsync(..., cap)`). For a BUY stop-market, `PlaceStopMarketBuyOrderAsync`
  currently takes **no** slippage param (uses budget). So showing the guard for a buy-stop is cosmetic
  unless we thread a cap through. Decide: (a) show it for both but only wire sell (buy-stop guard is a
  no-op placeholder), or (b) also add a cap to the buy-stop path. Confirm engine support before wiring
  (b). Default: (a) UI-only now, note the buy-stop wiring as follow-up.

### B4.7 — A8: quantity slider snappiness (decision needed)
- Current: snaps to a 0/25/50/75/100% dot only within 3% of it, else nearest whole share — feels weak.
  Binance feels like the slider has "no in-between" — it's essentially a 5-stop magnet.
- Options:
  - **(C) widen the magnet (recommended, low effort):** raise the snap threshold (0.03 → ~0.10–0.12).
    Still allows in-between with deliberate drag, but dots feel sticky. One-number change.
  - **(B) pure 5-stop:** slider only yields 0/25/50/75/100% (snap to nearest dot always). Most
    Binance-like + simplest logic, but loses fine-grained slider control (type for exact qty).
  - **(A) keep as-is.**
- Recommend **C** first (cheap, big feel improvement); fall back to **B** if you want it fully discrete.

### B4.8 — Tables widen the page when they have data (TradePage layout) — the big one
- Symptom (screenshot): switching to a tab whose table has rows makes the whole page wider than the
  window — order panel / order book / table right edges clip. Empty table = fits.
- Cause: the bottom tables `Border` is `Grid.ColumnSpan="3"`; its `CollectionView` rows use all-`*`
  columns. When the colspan element is measured horizontally unbounded, the `*` columns fall back to
  content width, so the table's desired width balloons and inflates the grid's `Auto` columns (orderbook
  col1, panel col2) → total width > window. (Horizontal twin of the row-inflation height bug; the old
  page-level `ScrollView` masked it by bounding width.)
- Fix (mirror the height solution — deterministic, loop-free): in `TradePage.xaml.cs`, in the same
  `SizeChanged` handler (rename `UpdatePanelHeights` → `UpdateTradeLayout`), also bound the tables
  width: `tablesBorder.WidthRequest = RootGrid.Width - 16` (the `Padding=8` both sides). Name the tables
  `Border` (e.g. `x:Name="TablesCard"`). A definite width on the colspan element stops it from inflating
  the columns; its `*` columns then divide the page width and the rows fit. Recomputes on resize.
- Alternatives to consider while implementing: set `MaximumWidthRequest` instead, or wrap the
  CollectionView so its width can't propagate up; the code-behind WidthRequest is the most reliable and
  matches the existing height fix.
