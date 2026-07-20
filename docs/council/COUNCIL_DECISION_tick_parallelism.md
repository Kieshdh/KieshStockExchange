# Council decision — dynamic tick + engine∥bots parallelism (2026-07-08)

Kiesh's fork: (1) a ~2 s tick is OK if it speeds throughput; (2) a DYNAMIC tick — run-to-completion, no
fixed idle, cap ~2 s, target ~1 s average; (3) run the ENGINE in PARALLEL with the BOT mechanism; (4) judge
by 15-second chart smoothness. 5-advisor council (First-Principles, Contrarian, Executor, Outsider,
Expansionist). **Verdict: UNANIMOUS.**

## The decision

### ✅ #2 Dynamic tick — YES, but it's the ALREADY-BUILT `SelfCorrectingDelay`, not uncapped back-to-back
`Bots:Scaler:SelfCorrectingDelay` (AiTradeService.cs:1223) already does "subtract elapsed work, delay only
the remainder" — run-to-completion with a min-interval floor and immediate start on overrun = Kiesh's idea,
minus an explicit upper cap. The **fixed 1 s `Task.Delay` is the real waste** (on prod the tick is tens of
ms, so the loop idles most of a second). Ship it + add a small **~2 s ceiling** (a few lines by the delay).
Do NOT ship *uncapped* back-to-back: variable spacing bunches fills → a **choppier** chart, not smoother.

### ❌ #3 Engine ∥ bots parallelism — NO (do not build; do not even soak it)
Unanimous, code-verified:
- **Torn reads on shared state.** `AccountsCache.GetFund/GetPosition` hand back the **live mutable**
  Fund/Position out of a ConcurrentDictionary; the per-account `SemaphoreSlim` gates protect the *settle*
  write path, NOT a bot's plain read. Run bots (tick N+1) concurrently with the engine (tick N) and bots
  read half-updated `TotalBalance`/`Quantity` — and `decimal` is 128-bit, so it's word-tearing, not just
  staleness. This **re-opens `project_money_probe_parallel_group_race.md`** (the parallel-Fund/Position
  conservation race fixed at `853c7e6`) — deliberately un-fixing the sacred CK=0 invariant.
- **Replay dies silently.** The thread interleave isn't clock-pinnable, so a CK bug becomes
  non-reproducible — you lose the debugger for the exact bug class this change increases.
- **The payoff is a docker mirage.** The only reason to overlap CPU with the commit is "the commit is the
  long pole" — but prod ticks are cheap (adv 10-76, batch 18-99 ms) and the batch already commits 13-17-wide.
  The overlap ceiling is `min(CPU, DB)` ≈ the small DB phase. Gold-plating a system that's already 90% a
  single-writer serial engine.
- **If ever revisited:** the ONLY safe shape is an **immutable price/account EPOCH snapshot** (bots read a
  read-only epoch N while the engine builds N+1; swap by reference) — real work that changes replay
  semantics — AND only after a REAL-BOX profile proves the commit phase is the bottleneck *after* de-idling.
  Not now.

### ⚠️ #1 A ~2 s tick — accepted only as the dynamic tick's CEILING
Economically fine (sim is real-time, a "day" = 24 h). But "if it speeds throughput" is false on prod — the
tick isn't delay-bound there. The throughput comes from **de-idling** (SelfCorrectingDelay) + the scaler,
not from stretching the tick. If you want to test "2 s buys throughput," it's `MaxTickMultiple≈2` on the
existing validated knob — not a redesign.

### 🎯 #4 "Smooth 15 s chart" — the metric was MIS-ATTRIBUTED
The chart already renders at **250 ms** (`QuoteRegistry.DrainLoopAsync`), decoupled from the tick — so
smoothness is a market-DATA property, not a settlement-thread one. Choppiness = **bursty per-tick fills**
(a whole tick lands at once, then silence). The direct fix is the **already-built stagger lever** (spread
each tick's bots/fills across sub-tick phases) + a shorter average period. Judge smoothness by **trades/sec
+ inter-trade gap variance + candle continuity**, not eyeball alone — and **CK=0 (a 2 h soak) gates every
tick change BEFORE any eyeball** (a torn-read money leak won't wobble the chart).

## The plan (everything below is ALREADY BUILT + default-off; the win is flipping + validating ON PROD)
1. **`SelfCorrectingDelay` + ~2 s ceiling** — de-idle the loop (recovers the wasted ~1 s idle).
2. **Corrected scaler** (`DutyCycleDenominator` + `MaxTickMultiple≈1`) — raises the cap at a ~1 s cadence
   (on prod, where ticks are cheap, this alone likely fits far more bots than local docker showed).
3. **Stagger** — spread fills across sub-ticks for a smooth tape.
4. **Validate ON PROD, not local docker** (local per-statement latency is a 5-10 ms artifact; any
   parallel-engine ROI computed locally is fiction). CK=0 over a 2 h soak is the hard gate; then eyeball +
   the trades/sec / gap-variance / candle-continuity metrics.
5. **Only if** a prod profile then shows the CPU bot-sweep (not the commit) is the ceiling: parallelize the
   **read-only bot-decision sweep** (embarrassingly parallel, DB-free = the SAFE boundary), never the writer engine.

## The irony the council names
Nearly everything Kiesh wants — continuous trading, a smooth chart, more bots at ~1 s — is reachable by
**flipping three levers that already exist** (SelfCorrectingDelay + corrected scaler + stagger), single-
threaded, CK-clean, replay intact. The parallel engine would spend the two crown-jewel invariants (CK=0 +
deterministic replay) to buy a frame-rate improvement the 250 ms quote drain already delivers.
