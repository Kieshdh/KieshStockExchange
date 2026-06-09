# Bot-loop throughput — keep periodic maintenance from cratering the active-bot cap

Status: PLAN for Ultraplan to harden, then implement (await approval). Self-contained — works from this +
the repo on `master`. Supersedes `docs/BOT_LOOP_BATCH_COMMITS_PLAN.md` (that targeted the advanced-order
phase, which **prod profiling proved is a local-docker artifact, not a real cost** — do NOT pursue it).
Source: [[project_bot_loop_perf]] + `logs/PERF_INVESTIGATION_NOTES.md`.

---

## 1. Problem (measured on PROD, in-network Postgres)

Per-phase tick profiling on prod (`Bots:PhaseTimingSeconds`, `AiTradeService.LogPhaseTiming`):

| phase | prod ms/tick | scales with |
|---|---|---|
| **check** | **395–703 — BOTTLENECK** | dataset size (active bots, open orders, positions) |
| adv | 10–76 | (cheap on prod; was a local round-trip-latency artifact) |
| batch / arb / collect | small | — |

`BotScalerService` holds the active-bot cap so tick-work EWMA ≈ 60% of the 1s `TradeInterval`. The `check`
phase is `AiTradeService.CheckTimers`, which runs **periodic maintenance INSIDE the scaler-measured tick**:
- `BotEconomyTelemetry.LogSnapshot(...)` — walks **all bots × all stocks** (positions) every 60s. O(bots×stocks).
- `_state.PruneWorstOrdersAsync(...)` — every 30s. O(open orders).
- `_state.RefreshAssetsAsync(...)` — every 60s.

At ~6–9k active bots these spike a single tick to ewma ~960ms (load ~100%), so the scaler **craters the cap
(observed 9982 → 5008 → 3810)** and then regrows — a sawtooth. The proof it's only the spikes: NORMAL ticks
run at **load 13–31%** (ewma ~290ms) — the scaler *wants* to grow toward `MaxBotCap` (20000) but the periodic
spikes keep slamming it down. This sawtooth is the user's "used to go 10k+, now a lot less" (it worsened as the
bot population / order book grew over time, making the maintenance walks heavier).

## 2. Goal & success metric

Stop the periodic maintenance from depressing the scaler so the cap stabilizes near its ceiling.
- **Primary:** active-bot cap stops oscillating and climbs back toward `MaxBotCap` (normal-tick load is only
  ~30%, so the headroom is real). Verify via the `Scaler`/`BotPhase` lines on prod.
- **Invariants:** `ConservationProbe`=0, `CK_Funds`/`CK_Positions`=0, reservation reconcile within tolerance,
  63/63 tests, and — critically — **no data races** (see §4: the bot loop is single-threaded by design and
  mutates `AiBotContext`/`AccountsCache` without locks).

## 3. Where it lives (grounded)

- `AiTradeService.CheckTimers` (~`:775`): every tick runs `_fxRates.Tick/_sentiment.Tick/_funds.Tick` (cheap,
  fixed-size — leave alone), then GATED periodic blocks: `_nextPruneTime` → `PruneWorstOrdersAsync`,
  `_nextAssetReload` → `RefreshAssetsAsync`, `_nextStatsLogTime` → `_stats.LogWindow`, `_nextEconomyLogTime` →
  `_economy.LogSnapshot`, sentiment-log, cash-injection.
- The scaler signal: `RecordTickLatency` feeds `_tickWorkMsEwma`; `CheckTimers` is INSIDE the timed region
  (`tickStart` is captured before `CheckTimers`). Note the reconcile pass is already deliberately placed AFTER
  `RecordTickLatency` "so this maintenance pass doesn't skew the scaler EWMA" — the same reasoning applies to
  these periodic blocks.
- `BotEconomyTelemetry.LogSnapshot` (`:76`): nested loop over `_ctx.AiUsersByAiUserId.Values` × `_stocks.ById.Keys`
  reading `_accounts.GetPosition/GetFund` — the O(bots×stocks) walk.

## 4. Approaches (Ultraplan to pick/harden)

### Option B — exclude periodic maintenance from the scaler EWMA (RECOMMENDED first step; smallest, safest)
Keep the maintenance ON the loop thread (no new threading → no races), but don't let it inflate the scaler's
load signal. Mirror exactly what the reconcile pass already does: measure the steady per-tick work
(`CheckTimers`-minus-periodic + collect + batch + adv + arb) for `RecordTickLatency`, and run the periodic
blocks either after the latency record or with their time subtracted. The scaler then holds the cap on the
*steady* per-bot cost (~30% load), not the spikes — so it stops cratering.
- Pro: tiny, no concurrency risk, directly fixes the sawtooth.
- Con: a maintenance tick still blocks the loop ~700ms (a periodic hiccup in trading cadence) — acceptable,
  but Option C reduces it.
- Open Q: should a maintenance tick be fully excluded, or measured on a separate slow-EWMA the scaler ignores?
  Define precisely so the scaler still reacts to *genuine* per-bot overload.

### Option C — make the maintenance itself cheaper (combine with B)
- `LogSnapshot`: it's a telemetry snapshot — sample a subset of bots, or maintain running aggregates
  incrementally instead of an O(bots×stocks) walk every 60s. Biggest single win.
- `PruneWorstOrdersAsync`: bound the work per pass (cap rows scanned/cancelled per cycle) so it can't spike.
- `RefreshAssetsAsync`: confirm it needs a full reload every 60s; widen the interval or make it incremental.

### Option A — move maintenance to a separate background thread/timer (LAST resort; highest risk)
Off the bot-loop tick entirely. ⚠️ The bot loop is **single-threaded by design** and mutates `AiBotContext`
(`StockPrices`, `AiUsersByAiUserId`, burst maps) and `AccountsCache` WITHOUT locks. A second thread reading/
mutating those races. `LogSnapshot` is read-only (could take a consistent snapshot or a brief lock);
`PruneWorstOrdersAsync` MUTATES (cancels orders, touches reservations) and must keep the engine's book→gates→tx
discipline; `RefreshAssetsAsync` already locks `AiUsersByAiUserId`. Only pursue if B+C leave an unacceptable
loop hiccup, and only with an explicit synchronization design.

## 5. Suggested sequence
1. **Option B** (exclude periodic maintenance from the scaler EWMA) — ship + re-profile on prod; expect the
   cap to stop oscillating and climb toward 20k.
2. **Option C** (cheapen `LogSnapshot` first) — reduce the residual loop hiccup.
3. Only if needed: **Option A** with a real concurrency design.

## 6. Validation
- Re-profile ON PROD (deploy master, enable `Bots__PhaseTimingSeconds`, read `docker logs ...-server-1 | grep
  BotPhase|Scaler`). Local-docker is NOT representative for this — its latency throttles the bot count and hides
  the check-phase scaling (that's the whole reason the first plan aimed at the wrong phase).
- Confirm cap climbs + stops sawtoothing; ConservationProbe/CK clean; 63/63 tests; trading cadence smooth.
