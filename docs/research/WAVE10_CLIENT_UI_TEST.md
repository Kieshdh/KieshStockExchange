# Wave 10 — Client UI adversarial test (item 42) + final baseline (item 44)

Status: **IN PROGRESS** — paused 2026-06-16. Tracked as tasks #131–#140. Run the client against a live
soak server (`localhost:5080`) and work the numbered plan; mark each section done at the end. Keep the
debug output visible — binding errors / `MainThread` warnings / unhandled exceptions print there.

> Client server URL: `KieshStockExchange/Resources/Raw/appsettings.json` → `Server:BaseUrl`. Repointed to
> `http://localhost:5080` for local soak testing — **revert to the duckdns prod URL before a prod build.**

## Test plan (tick each)
### A. Sanity & auth (1–5)
1. Connect to 5080 live market (watch startup debug output). 2. Login valid. 3. Logout/login again (no stale
state / dup SignalR). 4. Bad/empty/whitespace creds → clean validation. 5. Register existing/mismatched/empty → clean.
### B. Market + watchlist (6–9)  [sign-off: Watchlist 1.3]
6. Market list loads, no gap/flicker. 7. Add/reorder(▲▼)/remove watchlist persists. 8. Rapid add/remove 10× → no dup/crash. 9. Tap stock → Trade selected.
### C. Trade: chart/orderbook/entry (10–19)
10. Chart timeframe/MA-EMA spam. 11. Rapid stock switch rebinds chart+book+positions. 12. Live orderbook+bucketing. 13. Limit buy → open orders + chart line. 14. Market order fills. 15. Invalid entry (0/neg/huge/empty/non-numeric/many decimals). 16. Double-click place = 1 order. 17. Drag-modify + dialog modify. 18. Cancel removes book+line. 19. Bot fill reflects without refresh.
### D. Portfolio (20–22)
20. Holdings/open-orders/history/funds load, equity+cash correct. 21. Rapid sub-tab switch. 22. Paging/scroll long lists.
### E. Account / funds (23–27)
23. Deposit/withdraw → balance+FundTx, engine sees cash. 24. Convert currency two-sided. 25. Over-balance/neg/zero rejected. 26. Change username/email/password + bad input. 27. Theme+base currency persist across restart.
### F. Admin / bot dashboard (28–31)  [sign-off: Bot graph 2.1]
28. Tables load + page. 29. Sort columns. 30. Resize adjusts pagination (item 34). 31. Bot Dashboard activity graph renders+updates.
### G. Notifications / popups (32–33)
32. Inbox popup open/close 10× → no leak/null-ref. 33. Toast click-to-dismiss.
### H. Adversarial (34–38)
34. Resize-spam 15s/page. 35. Rapid tab switch 20s. 36. Popup/dialog spam. 37. Submit-then-navigate. 38. Select stock then navigate mid-load.
### I. Connection resilience (39–40)
39. Kill server mid-action → clean disconnected state, no crash. 40. Restart server → client reconnects + resumes without restart.
### J. Baseline sign-off (41–42)
41. Record every defect (step#, action, debug lines, recovery). 42. Clean run, zero unexplained debug errors → signs off item 42 + 44.

## Defects found & FIXED so far
1. **TradePage compiled-binding `x:DataType` mismatch** (HeaderRightContent reparented into SegmentedTabView
   dropped the VM context; `BindingContext={Binding Source={x:Reference TradePageRoot},Path=BindingContext}`
   resolved against the ambient `TradeViewModel` instead of `TradePage`). Fixed: annotated that binding with
   `x:DataType="views:TradePage"`. Commit `26bbd17`.
2. **Account-page `TaskCanceledException`/`HttpIOException` noise on every nav** — `AccountViewModel` ctor
   fired two unguarded fire-and-forget remote refreshes; under load a cut read faulted the unobserved-task
   net (noise, not a crash). Fixed: `KickBestEffort` swallows transient transport faults. Commit `359e3f4`.
3. **ChartViewModel same anti-pattern** on stock switch (`_ = _transactions.RefreshAsync`). Fixed:
   `SafeBackgroundTxRefresh`. Sweep confirmed all other `_ = …RefreshAsync()` sites already guard. Commit `8c33fac`.

## Wave-10 re-run fixes — client-test batch 1 (2026-06-23, client repointed to localhost:5000)
From Kiesh's live client test. All client-side; MAUI build clean (0 errors). Detail + root causes in `docs/CLIENT_TEST_FIXES.md`.
- **I1 — candle chart didn't move with the live price** (only updated on candle close). `OnPriceUpdatedAsync` now
  synthesizes the forming candle from the live price each tick; `UpsertCandle` dedups by bucket so the closed candle
  replaces it on close. Commit `82f4cf1`. ⚠️ **eyeball this on the chart** — it's the one fix not runtime-verified.
- **I2 — market page price laggy** (5s poll). Poll 5s→1s; push-driven update still sub-second. `e2ff631`.
- **I3 — EUR order book rows showed `$`**. `LevelRow.Update` refreshes currency on reused rows. `e2ff631`.
- **I4 — market table not sortable**. Tappable Symbol/Name/Change/Volume headers + ▲/▼ indicator, default Volume-desc.
  `abd2969`. (Marketcap column = separate DB feature, in progress.)
- **I5 — Top Losers/Gainers duplicated a stock** (USD+EUR listings). `TrendingService` dedupes by stock. `e2ff631`.
- **I6 — Gainers/Losers ignored the USD/EUR/Watchlist tab**. Now currency/watchlist-scoped. `e2ff631`.
- **I7 — portfolio allocation circle blank on an empty account**. Faint placeholder ring at zero equity (cash +
  holdings already render real slices). `abd2969`.
- **I8 — bot dashboard showed 20000/20000**. "Active / Max" now binds to actual `OnlineBots`, not the scaler cap. `e2ff631`.

In progress (not in this batch): marketcap column + 20k-total-bots (one DB/seed feature: `SharesOutstanding` + house
holds the rounding remainder + a share-conservation test); arbitrage/FxRate effectiveness (S1, harvesting the RC soak).

## Notes
- App did NOT crash on any of the above — all were handled/observed; fixes remove debug-output noise toward
  the item-44 zero-noise baseline.
- Resume by restarting the client (picks up the three fixes) and continuing from section A.
