# OrderValidator: `ValidateInput` vs `ValidateNew` divergences

**Scope:** read-only investigation. No code changed. Branch `feature/bot-market-realism-v2`.
**Source:** `KieshStockExchange.Server/Services/MarketEngineServices/Helpers/OrderValidator.cs`
**Characterization fence:** `KieshStockExchange.Tests/OrderValidatorCharacterizationTests.cs` (commit `93d2ed2`)

> **★ UPDATE (2026-07-20): divergence #3 RECONCILED (`b48d002`).** Per Kiesh + a full council (Option B), the
> shared core checks were extracted into one private `CheckCore` that BOTH `ValidateInput` and `ValidateNew` call —
> which adds the currency-support guard to `ValidateNew` (closing #3's gap) in the safe additive-reject direction,
> and adds a differential drift-guard test so the two can't silently diverge again. #1/#2/#4 left as-is (benign).
> 730/730 tests, adversarial review = SAFE (ValidateInput byte-identical; ValidateNew rejects more, never less).

## Summary — benign, not a gap

The four pinned divergences are **benign belt-and-suspenders, not a real acceptance gap.** Two independent facts make them harmless:

1. **Every `ValidateInput` call site is downstream-gated by `ValidateNew`** on the same request path — there is no production path that reaches `ValidateInput` without also reaching `ValidateNew` (either immediately in `OrderEntryService`, or inside the engine batch method it calls). So even where `ValidateInput` is looser, `ValidateNew` re-rejects before anything is reserved or matched.
2. **`CreateOrder` sanitizes the two "looseness" fields anyway** (`OrderEntryService.cs:52-54`): it drops `BuyBudget` for any non-(market-buy) order. So the divergent inputs (limit+budget, sell+budget) can never survive into the built `Order` that `ValidateNew` inspects — and, separately, **no real caller ever supplies those inputs to `ValidateInput` in the first place** (the public `Place*` methods and the batch routes hardcode `buyBudget: null` for every limit/sell path).

Crucially, on the **two divergences where `ValidateInput` is the *stricter* side** (currency support, slippage-range), `ValidateInput` runs **first** on the only path that produces those inputs, and its extra strictness is **load-bearing** (it converts a would-be unhandled exception / phantom currency into a clean `OrderResult`). "Reconciling `ValidateInput` *toward* `ValidateNew`" in the naive direction (relaxing `ValidateInput`) would be a **regression** for those two, not a cleanup.

---

## Call-graph finding

`IOrderValidator.ValidateInput` has exactly **four** production call sites (all in `OrderEntryService`); the client `ValidateInputs()` methods in `PlaceOrderViewModel.cs:654` / `ModifyOrderViewModel.cs:404` are unrelated local UI pre-checks, **not** this interface. Every `ValidateInput` site is followed by a `ValidateNew` gate on the same request:

| # | `ValidateInput` call site | Then builds order via | Reaches `ValidateNew`? | Where |
|---|---|---|---|---|
| 1 | `OrderEntryService.Orders.cs:64` (`PlaceOrderAsync` — the single-order public API: limit/market/slippage buy+sell) | `CreateOrder` (`Orders.cs:69`) | **Yes, immediately** | `Orders.cs:73`, then again in engine at `OrderExecutionService.cs:162` (`PlaceAndMatchAsync`) |
| 2 | `OrderEntryService.Brackets.cs:290` (`PlaceMarketShortBatchAsync` — bot market-short batch) | `CreateOrder` (`Brackets.cs:293`) | **Yes, in engine** | `OrderExecutionService.cs:2181` (`PlaceMarketShortBatchAsync`, engine) |
| 3 | `OrderEntryService.Brackets.cs:325` (`PlaceTrueMarketBuyBatchAsync` — arb leg-1 batch) | `CreateOrder` (`Brackets.cs:328`) | **Yes, in engine** | `OrderExecutionService.cs:621` (`PlaceAndMatchBatchAsync`, engine) |
| 4 | `OrderEntryService.Brackets.cs:357` (`PlaceTrueMarketSellBatchAsync` — arb leg-2 batch) | `CreateOrder` (`Brackets.cs:360`) | **Yes, in engine** | `OrderExecutionService.cs:621` (`PlaceAndMatchBatchAsync`, engine) |

`ValidateNew` additionally guards every other order-entry route (bracket legs at `Brackets.cs:114/118/123/261/265/270`, batch stop-arm at `OrderExecutionService.cs:1706/1878`, etc.), but those routes never call `ValidateInput` — they build `Order` objects directly and only run `ValidateNew`. So `ValidateNew` is the universal gate; `ValidateInput` is an *additional, earlier* param-level check on four of those routes.

**Conclusion:** there is **no path gated by `ValidateInput` alone.** The looseness cannot let an order through that `ValidateNew` would reject.

---

## Divergence 1 — `ValidateInput` accepts LimitBuy + `BuyBudget`; `ValidateNew` rejects it

- **What differs:** `ValidateInput` never inspects `buyBudget` on the limit branch (`OrderValidator.cs:73-79`), so a limit order carrying a budget is accepted. `ValidateNew` rejects it at `OrderValidator.cs:127-128` → `"Limit buy order cannot have BuyBudget."` (pinned: `OrderValidatorCharacterizationTests.cs:467-478`).
- **Authoritative / stricter side:** `ValidateNew` (correctly rejects — a limit order funds from `Price×Qty`, not a budget).
- **Reachable without `ValidateNew`? NO.** The only `ValidateInput` site that ever sees `limitOrder: true` is `Orders.cs:64`, and it runs `ValidateNew` at `Orders.cs:73` on the same call. Moreover the input itself is unreachable in production: `PlaceLimitBuyOrderAsync` (`Orders.cs:18-21`) passes `buyBudget: null`, and the batch routes never use the limit branch. Even if a budget were passed, `CreateOrder` (`OrderEntryService.cs:52-54`) sets `budget = null` for any non-market-buy, so the built `Order.BuyBudget` is `null` and `ValidateNew` sees a clean limit order.
- **What reconciling would newly reject:** nothing real. Adding the `BuyBudget` check to `ValidateInput`'s limit branch rejects only a synthetic input no caller produces.
- **Recommendation: LEAVE** (optionally tighten `ValidateInput` for cosmetic parity, zero behavior change). The divergent input is doubly-unreachable: no caller supplies it and `CreateOrder` strips it.

## Divergence 2 — `ValidateInput` accepts TrueMarket-SELL + `BuyBudget`; `ValidateNew` rejects it

- **What differs:** `ValidateInput`'s budget guard is gated on `buyOrder` only (`OrderValidator.cs:87-92`) — a TrueMarket **sell** carrying a budget is silently accepted. `ValidateNew` rejects it at `OrderValidator.cs:140-141` → `"Sell TrueMarket orders cannot have BuyBudget."` (pinned: `OrderValidatorCharacterizationTests.cs:480-491`).
- **Authoritative / stricter side:** `ValidateNew`.
- **Reachable without `ValidateNew`? NO.** The `ValidateInput` sell sites are `Orders.cs:64` (→ `ValidateNew` at `Orders.cs:73`) and `Brackets.cs:290/357` (→ engine `ValidateNew` at `OrderExecutionService.cs:2181`/`621`). The input is also unreachable: `PlaceTrueMarketSellOrderAsync` (`Orders.cs:43-46`) and both sell batch routes (`Brackets.cs:291`, `358`) hardcode `buyBudget: null`; and `CreateOrder` (`OrderEntryService.cs:52-54`) forces `budget = null` for sells regardless.
- **What reconciling would newly reject:** nothing real (same as Divergence 1).
- **Recommendation: LEAVE** (optionally add the symmetric sell-budget guard to `ValidateInput` for parity; zero behavior change).

## Divergence 3 — `ValidateInput` checks currency support; `ValidateNew` has no currency check

- **What differs:** `ValidateInput` rejects an unsupported currency at `OrderValidator.cs:65-66` → `"Unsupported currency."` (via `CurrencyHelper.IsSupported`). `ValidateNew` has **no** currency-support check at all (pinned: `OrderValidatorCharacterizationTests.cs:493-502`). Note both methods *do* share the phantom-listing check (`IsListedIn`, `:68` / `:116`); only the enum-support guard is `ValidateInput`-only.
- **Authoritative / stricter side:** **`ValidateInput`** (this is the reversed-direction divergence). It is the only guard against an out-of-range `(CurrencyType)999` cast reaching the engine.
- **Reachable without `ValidateNew`? N/A — `ValidateInput` is the strict side here.** It runs first on every path, so a bad currency is caught before `ValidateNew`. `ValidateNew` never needs the check because a built `Order` normally only holds a declared enum — but that is an assumption about model binding, not an enforced invariant.
- **What reconciling would newly reject:** if reconciled in the naive direction (**remove** the currency check from `ValidateInput` to match `ValidateNew`), it would newly *accept* an out-of-range currency cast and let a phantom-currency order reach `CreateOrder`/the engine — a **regression**, not a cleanup. The safe reconciliation direction is the reverse: **port the `IsSupported` guard into `ValidateNew`** so the universal gate also covers it.
- **Recommendation: NEEDS-OWNER-CALL (lean LEAVE).** Do not relax `ValidateInput`. If parity is wanted, add `CurrencyHelper.IsSupported` to `ValidateNew` (CK-adjacent — touches the universal acceptance gate, so owner sign-off). Lowest-risk default is LEAVE: the guard already fires first on every real path.

## Divergence 4 — `ValidateInput` can surface a slippage-range message; `ValidateNew`'s equivalent is unreachable

- **What differs:** `ValidateInput` takes `slippagePercent` as a raw `decimal?` param and range-checks it at `OrderValidator.cs:98-99` → `"Slippage percent must be between 0 and 100%."`, gracefully reachable for e.g. `150` (pinned: `OrderValidatorCharacterizationTests.cs:257-268`). `ValidateNew`'s equivalent checks (`:149-152`, `:201-202`) are **dead code**: `Order.SlippagePercent`'s setter throws `ArgumentException` for any value `<0` or `>100` **before** `ValidateNew` runs (`KieshStockExchange.Shared/Models/Trading/Order.cs:100-101`), so a built `Order` can never carry an out-of-range slippage.
- **Authoritative / stricter side:** **`ValidateInput`** (again the reversed-direction divergence). It is the **only place** an out-of-range slippage is turned into a clean rejection instead of a thrown exception.
- **Reachable without `ValidateNew`? N/A — `ValidateInput` is the protective side.** On the real path (`Orders.cs:64`), `ValidateInput` runs *before* `CreateOrder`. It catches `slippage = 150` and returns an `OrderResult`, so the setter-throw at `Order.cs:100` is never hit. The `ValidateNew` range checks are unreachable precisely because `ValidateInput` (and the setter) guard upstream.
- **What reconciling would newly reject:** if reconciled naively (**remove** `ValidateInput`'s range check to match the dead-code `ValidateNew` version), an out-of-range slippage would flow into `CreateOrder` → `Order.SlippagePercent = 150` → **unhandled `ArgumentException` → HTTP 500** instead of a clean 4xx `OrderResult`. That is a **regression**. `ValidateInput`'s check is load-bearing.
- **Recommendation: LEAVE.** `ValidateInput`'s range check is the safety net that keeps a bad slippage from becoming a 500. `ValidateNew`'s dead range checks may be left as harmless defense-in-depth (or deleted as cleanup, separate low-risk arc — they never fire).

---

## For Kiesh — bottom line

**The single decision:** these four are cosmetic/parity items, not a correctness gap — do you want the two validators made *symmetric*, or leave the intentional asymmetry?

- **Divergences 1 & 2** (`ValidateInput` looser on `BuyBudget`): fully benign — the divergent input is unreachable from every caller **and** `CreateOrder` strips it **and** `ValidateNew` re-gates it. Tightening `ValidateInput` is a zero-risk no-op you can take purely for readability, or skip.
- **Divergences 3 & 4** (`ValidateInput` *stricter* on currency-support and slippage-range): **do not relax `ValidateInput`** — its extra strictness is the actual guard (phantom-currency rejection; converting a slippage-overflow 500 into a clean 4xx). The only "reconciliation" worth considering is the reverse: porting the currency-support check into `ValidateNew` (CK-adjacent, wants your sign-off).

**Safest default: LEAVE all four as-is.** No production path is gated by `ValidateInput` alone, so nothing is getting through today that `ValidateNew` would reject. If you want tidiness, the only zero-risk edits are (a) add the two `BuyBudget` guards to `ValidateInput` for parity, and (b) optionally delete `ValidateNew`'s dead slippage-range checks — neither changes any observed behavior.

### Items flagged / not fully determinable from code alone
- Whether ASP.NET model binding can actually deliver an out-of-range `(CurrencyType)999` to `OrderController` was **not** traced end-to-end; Divergence 3's value rests on that being *possible*. If enum binding is strictly validated at the controller, Divergence 3's guard is redundant even in `ValidateInput`. Worth a one-line confirmation before any change to the currency checks.
