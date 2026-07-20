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

<!-- autonomous sessions append P2-4, P2-6, etc. below in the same shape:
## N. P2-x — <title>  (commit `<hash>`)
**What changed:** ...
**Test:** ...
**Status:** built + gated + adversarial-reviewed; awaiting Kiesh click-test.
-->
