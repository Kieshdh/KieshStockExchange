# ReservationMath client↔server DRIFT — diagnostic (read-only; for Kiesh)

**Council item P2-5, "one thing first" (2026-07-19).** Read-only diff of the two `ReservationMath` copies.
NO app code was changed. **Headline: the client copy is STALE *dead code* — this is a safe DELETE, not a
CK-risky unification.**

## The two copies
- Client: `KieshStockExchange/Services/MarketEngineServices/Settlement/ReservationMath.cs`
- Server: `KieshStockExchange.Server/Services/MarketEngineServices/Settlement/ReservationMath.cs`

Both are `public static class ReservationMath` in namespace `KieshStockExchange.Services.MarketEngineServices`
(same full name, but two DISTINCT types in two DIFFERENT assemblies), all methods `internal static`, both
delegate cash rounding to `CurrencyHelper.Notional` (rounding stays centralized — good).

## ★ Key finding: the CLIENT copy has NO callers → dead code
A repo-wide search for `ReservationMath` shows the client project (`KieshStockExchange/`) contains exactly ONE
reference: the definition file itself. **No other client file calls `ReservationMath.*`, and there is no
`using static …ReservationMath`.** Its methods are `internal static`, so only the client assembly could call
them — and it never does. Every real caller is server-side (`TradeSettler`, `OrderExecutionService`,
`OrderSettler`, `StopModifier`, `OrderModifier`, `AccountsCache.Hydration`) or the test project (which
references the server). **The client copy is unreachable.**

## Authority + user-facing impact
- **Server is the single source of truth** — it owns settlement and the actual `Fund.ReservedBalance` /
  `Position.ReservedQuantity` mutations (conservation / CK). The server `ReservationMath` is what reserves money.
- **User-facing impact of the drift: NONE.** The client never reserves anything via its copy; it isn't called.
  The earlier worry ("is the client under-reserving?") is moot — there is no client reservation path here at all.
  Conservation is NOT at risk from this drift.

## What actually diverged (documented for completeness — none of it fires, since the client copy is dead)
The client copy is a strict *stale subset* of the server (it predates the §3.6 stop/short work):
1. **`IsBudgetBuy` — server only.** Server treats `StopMarketBuy` like `TrueMarketBuy` (flat `BuyBudget`); client has only `IsTrueMarketBuy`.
2. **`ReservationPerUnit` / `InitialBuyReservation` / `RemainingBuyReservation`** — server special-cases
   `StopLimitBuy` (reserve at limit `Price`) and budget-buys (`StopMarketBuy`→`BuyBudget`); client does not →
   *if it were used*, client would compute **0** for `StopMarketBuy` and `StopLimitBuy` (under-reserve).
3. **`ProjectedBuyReservation`** — server special-cases `StopLimitBuy` on modify (client would return 0 and
   release the whole hold on an armed buy-stop-limit modify — the server comment calls this out explicitly).
4. **`ShortCollateralForFill` / `ShortCollateralForResting` — server only** (§3.6 P1/F14 short collateral); client lacks both.

## Recommendation
- **DELETE the client `ReservationMath.cs` (dead-code removal).** This is **Pass-1 provably-safe, NOT a CK change**
  — removing unused *client* code touches no server settlement/money path. The compiler proves it: if the client
  builds after deletion, nothing referenced it. Gate = client build (disk-gated) + adversarial review confirming
  no reference existed. Autonomous-eligible.
  - Fallback (only if the client build surprisingly fails): a caller was missed → then do NOT delete; instead
    hoist the authoritative SERVER copy to `KieshStockExchange.Shared/Helpers/` and repoint both (that path IS a
    client behaviour change → owner + CK soak). But the evidence says there is no caller, so expect a clean delete.
- **Server `ReservationMath` is fine as-is** — authoritative and correct. No change needed. Adding
  **characterization tests** pinning its current behaviour (all order kinds: limit/true-market/stop-market/
  stop-limit/short) is still worthwhile for owner confidence + future refactor safety (council GO-NOW item #2).
- **Owner note:** this reclassifies P2-5's core from "scary CK unification (owner+soak)" to "safe client dead-code
  delete (autonomous) + optional server characterization tests." The diagnostic did its job.
