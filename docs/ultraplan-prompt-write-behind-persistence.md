# Ultraplan — bounded write-behind / pipelined persistence (take the fsync off the hot loop)

## ⛔ GATE — DO NOT PROCEED UNLESS THE LOCAL PROFILE GREENLIGHTS
Phase 0 (the fsync-vs-compute profile) is measured **locally** — the cloud cannot run a soak/DB. Only design + patch this if the local `PhaseTimingSeconds` profile shows **fsync is a meaningful fraction (≥ ~15–20%) of a FULL (~1 s) tick**. If the tick *idles* (< 1 s wall time → cadence absorbs the slack) or fsync is ~1–2% of the tick, this is **theater** — the real lever is parallelizing bot-decision compute or the Position column-store, and this ultraplan should be shelved. **[Paste the local profile result here before firing: fsync ms/tick, ms/tick vs. 1 s, collect(read-only) ms/tick.]**

**Goal:** remove commit (fsync) **latency** from the tick's critical path via a **bounded-depth write-behind pipeline with scaler back-pressure** — flag-gated, default-off, byte-identical off, CK=0-safe. This is a *different* lever from the shipped group-commit work (which cut fsync **count**, not latency). Conservation is sacrosanct.

## ★ Diagnosis (code-grounded from a sweep — build on this, don't re-derive)
- **The loop awaits every COMMIT synchronously on one thread.** `AiTradeService.RunLoopAsync` (`AiTradeService.cs:998`, loop `:1017`) runs check/collect/batch/adv/arb/… sequentially; DB writes+COMMITs happen in batch/adv/arb via the engine, and `CommitAsync` is `await`-ed on the loop's call stack (`OrderExecutionService.cs:1144` per-group; `RunInTransactionAsync`→`PgDBService.cs:209`). No background committer exists.
- **Group-commit was COUNT-reduction, not overlap.** `Db:GroupCommit:Enabled` coalesces per-`(stock,ccy)` root commits into ~one per-currency root commit (groups become savepoints under one fsync — `RunGroupCommitShardsAsync` `:1264`, `RunCurrencyShardAsync` `:1306`). The single remaining root commit is still awaited synchronously. The 1870→8650 cap gain was fewer fsyncs/tick.
- **The cache is the source of truth; the DB is a durable write-log.** `AccountsCache` (`:17-19`) holds funds/positions in memory; loaded once at startup (`EnsureLoadedAsync` `:54`, funds `:139`, positions `:153`, reservations rebuilt from open orders `:79-119`). **No per-tick SQL reads** — writes only.
- **Tick N+1 depends only on tick N's IN-MEMORY mutations, NOT the fsync.** Settlement mutates the in-memory `Fund`/`Position` synchronously *before* the commit; the loop never reads back from the DB.
- **Conservation is enforced IN-MEMORY, synchronously, BEFORE the DB write.** The Q7 pre-write CK scan (`TradeSettler.cs:718-753` → `FindInvariantViolation :763`, mirroring `Position.cs:97-100`) + `ConservationProbe.Check` (`TradeSettler.cs:703`) run in the settle pass before `InsertAll`/`UpdateAll`. The DB `CK_Positions`/`CK_Funds` CHECK constraints (`KseDbContext.cs:125-129,142-144`) are a *durable double-check*. ⇒ **deferring the commit does not weaken conservation.**
- **Crash-recovery already rebuilds the cache from the last durable commit** (`EnsureLoadedAsync`); the group-commit crash-window comment (`OrderExecutionService.cs:52-56`) confirms "cache == DB on restart." Losing the unfsynced tail is survivable.

## ★★ The design (Kiesh's bounded pipeline + scaler back-pressure — this is the shape to build)
- **Bounded tick-difference cap N (default 1, config e.g. 2):** the loop may run at most **N ticks ahead** of the durable commit. A background committer flushes tick K's tx while the loop computes K+1…K+N. When commit-lag would exceed N, the loop **blocks on the oldest pending flush** — deliberate back-pressure. Depth-1 = the Executor's "block-before-writes" special case (safest); depth-N generalizes it.
- **Scaler back-pressure:** feed **commit-lag (tick difference)** into `BotScalerService` as a load signal → when SQL lags, the scaler **sheds active bots** (less work → fewer/smaller commits → SQL catches up). Fails **soft** (fewer bots), never a freeze — and reuses the existing EWMA back-off machinery.
- **Bounded failure story:** a background commit that fails → **bounded crash-rebuild** (lose ≤ N ticks from the last durable commit via the existing cold-load), **NOT** a cache-revert. This is what makes it safe: the tail is *bounded* (≤ N), not open-ended.

## ★ Council guardrails (READ — they shaped the design)
- **The naive UNBOUNDED write-behind is REJECTED** (Contrarian): a failed commit after the cache has advanced has no clean rollback, and the tail unbounds if the committer falls behind. The **bound (N) + scaler back-pressure** is exactly the fix — keep it central.
- **The DB CK constraints move off the hot path, but conservation does NOT weaken** — the in-memory pre-write CK scan stays synchronous. Preserve that; it's the whole safety argument.
- **HARD gate (Outsider): verify the rebuild under a REAL crash** — `kill -9` the server mid-flush, restart, assert CK-clean rebuild + bounded loss. Clean-shutdown testing is insufficient.
- **Weigh the simpler alternative (First-Principles): commit-every-N-ticks** — coarser durability with the *same* tail-loss bound and **zero concurrency** (no tx handoff, no failed-async recovery). If the win is purely fewer fsyncs (not latency-overlap), this likely *dominates* write-behind. The ultraplan must compare the two and justify the choice.

## What to design (phases — each flag-gated, default-off, byte-identical, CK-soak-gated)
1. **Bounded pipeline primitive.** Detach the built root tx (buffered INSERT/UPDATE batch + Npgsql connection) from the `AsyncLocal` ambient slot (`PgDBService.cs:25`) and hand it to a background committer; a bounded queue (depth N) with the loop blocking when full. Decide which `ApplyGroupPostCommit` (`OrderExecutionService.cs:1200`) side-effects run pre-fsync (safe — cache already assumes success, CK enforced pre-write) vs. post-fsync. Flag `Bots:Advanced:WriteBehind:{Enabled(false),Depth(1)}`.
2. **Scaler integration.** Commit-lag signal → `BotScalerService` back-off (shed active bots when lag ≥ N).
3. **Bounded recovery.** A failed background commit → the bounded crash-rebuild path; verify under `kill -9` mid-flush.

## Constraints + gates
- **CK=0 hard gate** per phase (in-memory conservation unchanged; DB constraints = durable double-check). Flag-gated + byte-identical off (depth 0 / disabled = today's synchronous commit). Seed-determinism preserved.
- **REAL-crash rebuild** verification (`kill -9` mid-flush → CK-clean, ≤ N ticks lost).
- **The cloud cannot soak/build** — deliver DESIGN + PATCH; local Claude validates (build + CK soak + the kill-9 rebuild + a cap/latency A/B via `scripts/phase_harvest.py`). Target: tick-time drops by the overlapped fsync; cap up; bounded tail; CK=0.
- **Land as its own commit** (a co-mingled CK failure is un-attributable).

## Key files
`AiTradeService.cs` (RunLoopAsync :998, loop :1017, phase sequence :1025-1100) · `OrderExecutionService.cs` (RunGroupTxAsync :1092/:1144, RunGroupCommitShardsAsync :1264, RunCurrencyShardAsync :1306, ApplyGroupPostCommit :1200, RunGroupTxAsync catch :1147-1162, RecoverFailedGroupAsync :1373, _groupGate :84) · `PgDBService.cs` (BeginTransactionAsync :175, AsyncLocal TxScope :25, CommitAsync :298/:307, RunInTransactionAsync :202, OpenConnectionAsync :187, DisposeRootAsync :355) · `EngineCommitMetrics.cs` (RecordRootCommit) · `AccountsCache.cs` (dicts :17-19, EnsureLoadedAsync :54-119) · `TradeSettler.cs` (pre-write CK scan :718-753, FindInvariantViolation :763, ConservationProbe.Check :703) · `KseDbContext.cs` (CK constraints :125-129,142-144) · `BotScalerService` (the EWMA active-bot scaler — the back-pressure sink).
