# Ultraplan — cut fsync off the hot loop: **commit-every-N-ticks (lead)** + write-behind (deferred fallback)

> **Council-revised (2nd plan-review pass).** The naive "write-behind" framing was over-engineered for this bottleneck. A 5-advisor review (grounded in the measured ~19% fsync) unanimously steered to **commit-every-N-ticks** as the lead: same win-target, a fraction of the risk, freeze-safe. Write-behind is demoted to a deferred fallback.

## ⛔ GATE 0 — verify the prerequisite FIRST (cheap code read, not a soak)
The entire win depends on the **scaler raising the cap when tick-time drops.** If the scaler's tick-latency signal EXCLUDES the synchronous commit-wait, then removing fsyncs changes nothing it sees → **no cap gain → the whole lever is fictional** (the loop just idles slightly more).
- Read `AiTradeService.RunLoopAsync` `RecordTickLatency` (`:1069`, measured AFTER the batch/adv/arb commit phases → it DOES include the fsync) and confirm the `BotScalerService` EWMA consumes that full tick-latency (not a commit-excluding subset). The map indicates the gain is **real**; confirm in ~5 lines before building. **If excluded → shelve this; the real lever is compute/sharding.**

## Measured context (local profile, GroupCommit-on)
fsync ≈ **~19% of the scaler-limited ~622 ms tick** (commits/sec 23.8 × ~5 ms ≈ 119 ms/tick); ms/tick held stable by a **tick-time-targeted** scaler. Modest-but-real ~15–20% cap. (For comparison the **arb** phase is ~23% and is optimized separately via lower-risk flag-gated levers — arb is the higher-ROI lever; this is second-tier.)

## ★ Phase 1 — commit-every-N-ticks (THE lever to build)
`Bots:Advanced:CommitEveryNTicks` (default **1** = today's every-tick, byte-identical). Accumulate N ticks' worth of persisted writes (the per-tick INSERT/UPDATE sets) and issue **one root COMMIT every N ticks** instead of per tick → removes ~(1 − 1/N) of fsyncs (N=10 → ~90%, ~107 ms/tick reclaimed).
- **Tick loop stays single-threaded** — no background committer, no `AsyncLocal` handoff across threads, no failed-async recovery. This is the whole reason it wins on risk/reward.
- **CK unchanged:** the in-memory pre-write CK scan + `ConservationProbe` run every tick exactly as today (`TradeSettler.cs:718-753`). Only the *fsync cadence* changes.
- **Durability:** a crash loses ≤ N ticks; recovered by the existing cold-load (`AccountsCache.EnsureLoadedAsync`) — well within the "lose a couple seconds" tolerance. **Verify under `kill -9` mid-window.**
- **The one real design point:** the N-tick accumulation must commit the accumulated write set **atomically** every N ticks, and a mid-window per-order settlement rollback (the existing `RecoverFailedGroupAsync` path) must roll back only *that* order, **not** discard earlier ticks' accumulated writes. Design the accumulation buffer so per-tick failures stay isolated while the fsync is deferred.
- Flag-gated default-off, byte-identical at N=1.

## Phase 2 — measure + pick N
A/B `CommitEveryNTicks` ∈ {1, 5, 10, 20}: cap response (the win, since scaler-on) + commits/sec drop + CK=0 + the ≤N crash-rebuild under `kill -9`. Pick the N sweet spot (durability tolerance vs. fsync reduction). `scripts/phase_harvest.py` (now captures commits/sec).

## Deferred fallback — write-behind pipeline (build ONLY if Phase 1's reduced-freq fsyncs are still the ceiling)
Bounded-depth-N write-behind + scaler back-pressure (loop runs ≤N ticks ahead; background committer; commit-lag → scaler sheds bots; failed commit → bounded crash-rebuild). **If built, it MUST address the council's flagged gaps — none are optional:**
- **DB tick-sentinel:** write a committed-through-tick-K marker atomically with each flush, or "rebuild from last durable commit" is undefined at depth N.
- **AsyncLocal + Npgsql connection ownership:** audit *every* `RunInTransactionAsync` call site (the background committer's `AsyncLocal` is blank → a missed site silently opens a new top-level tx); keep the physical Npgsql connection alive off-pool while the committer holds the tx (Npgsql has no "detach + commit later" primitive).
- **`ApplyGroupPostCommit` split** (`OrderExecutionService.cs:1200`): cache-mutating side-effects MUST run post-commit (else phantom reads on a failed commit).
- **Self-contained WAL records:** each write entry = sequence ID + typed payload snapshot, **not** a closure over mutable tick state — this keeps the primitive reusable as the front-end for a future parallel-tick-worker / DB-as-WAL architecture (compound path) instead of throwaway.

## Constraints + gates
- **CK=0** enforced in-memory per tick (unchanged); flag-gated + byte-identical off; the scaler-includes-fsync verification (Gate 0) is the hard prerequisite.
- Measure the **cap response** (before/after trace) + the `kill -9` bounded-rebuild — "throughput in sim-steps/sec," not just "freed ms" (Outsider).
- The cloud **cannot soak/build** — deliver DESIGN + PATCH; local Claude validates (build + CK soak + kill-9 + cap A/B). Land as its own commit.

## Key files
`AiTradeService.cs` (RunLoopAsync :998, RecordTickLatency :1069, phase seq :1025-1100) · `OrderExecutionService.cs` (group runners, RunGroupTxAsync :1092/:1144, RunCurrencyShardAsync :1306, ApplyGroupPostCommit :1200, RecoverFailedGroupAsync :1373) · `PgDBService.cs` (BeginTransactionAsync :175, AsyncLocal TxScope :25, CommitAsync :298/:307, RunInTransactionAsync :202) · `TradeSettler.cs` (pre-write CK scan :718-753) · `AccountsCache.cs` (EnsureLoadedAsync :54-119) · `BotScalerService` (the EWMA scaler — confirm it consumes the full tick-latency).
