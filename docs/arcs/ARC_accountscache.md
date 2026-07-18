# Arc: AccountsCache partial-class split (server)

**Target:** `KieshStockExchange.Server/Services/PortfolioServices/AccountsCache.cs` (1014 LOC,
`public sealed class AccountsCache : IAccountsCache`, namespace `KieshStockExchange.Services.PortfolioServices`).
**Lane:** Auto. **Branch:** `feature/bot-market-realism-v2`. Auto-includes new .cs (no csproj edit).
**Type:** PURE partial-class split, byte-identical. NO real-extraction (mandate: partials ONLY — a reconcile
SERVICE would fragment the shared-state/shared-gate invariant).

## The invariant that must NOT fragment (Phase-0)
No single lock. State integrity = lock-free `ConcurrentDictionary` (`_funds`,`_positions`,`_loadedUsers`)
+ `_loadGate` SemaphoreSlim(1,1) serializing ALL hydration + per-resource gates (`_fundGates`,`_posGates`)
serializing hot reservation mutation. ALL 6 state/gate fields + the ctor + the nested release classes
(`SemaphoreRelease`,`MultiSemaphoreRelease`) stay in the SPINE. A partial split keeps everything one type →
`_loadGate` exclusivity + gate semantics + field init order are byte-for-byte unchanged.

## Init-order (council check): SAFE
6 field initializers, all literal `new(...)`; none references another field/this/method; no static ctor,
no const, no static fields. Every field stays in the spine → init order untouched.

## Split (spine + 3 concern partials; Loading region alone is ~551 LOC so 2 partials can't hit ~500 cap)
- **`AccountsCache.cs`** (spine, ~210): all 6 state fields + 4 service fields + ctor; region Lookups&Mutations
  (GetFund, GetPosition, ApplyExternalFundDeltaAsync [HOT], TrackNewPosition, Clear); region Per-User Gates
  (AcquireFundGateAsync, AcquirePositionGateAsync, AcquireUserGatesAsync, nested SemaphoreRelease,
  MultiSemaphoreRelease). Keeps `: IAccountsCache`.
- **`AccountsCache.Hydration.cs`** (~375): EnsureLoadedAsync x2, CollectMissingUsers, LoadFundsAsync,
  LoadPositionsAsync, GroupOpenOrdersBySide, ClampSellsToPositionQuantity, BackfillShortCollateral,
  BackfillRestingShortCollateral, ClampBuysToFundBalance.
- **`AccountsCache.Reseed.cs`** (~185): ReseedBracketReservations, CancelBracketGroupOverReserve,
  ReseedBracketCashPools.
- **`AccountsCache.Reconcile.cs`** (~265): ReconcileReservationsAsync, ClampFundAsync, ClampPositionAsync.

## Naming traps (do NOT misfile)
- ClampFundAsync / ClampPositionAsync = RECONCILE phase-2 helpers (gated, DB-writing) → Reconcile.cs
  (NOT hydration, despite the "Clamp" name shared with hydration clamps).
- ApplyExternalFundDeltaAsync = HOT mutation → spine (despite living in "Lookups and Mutations" region).
- Nested SemaphoreRelease/MultiSemaphoreRelease → spine (constructed by the gate-acquire methods).
- Do NOT reorder calls inside EnsureLoadedAsync (steps 1–5 ordering contract) — copy body verbatim.

## Gate (orchestrator re-runs independently)
1. Server build green.
2. FULL suite → 661/661 (oracle: ShareConservationTests, CommittedTotalsTests, BracketReconcileTests,
   BracketSettlementTests, ColdLoadReseedTests, *EquivalenceTests). Known flakes: BankEstimateAnchorPivot,
   SharedScanEquivalence — rerun/isolate to confirm if either is the only failure.
3. Moves-only sorted-line diff: spine pure-deletion + `partial` keyword; each partial's members == removed lines.
4. No 15m smoke required by §6 for AccountsCache (build+suite), but it is CK-adjacent → run a short CK smoke
   if time allows before BracketCoordinator.
