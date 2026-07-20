# EXAMPLE (not portable): one project's problem-finders — KieshStockExchange

> **This is a worked example, not a drop-in.** It's the lab notebook for *this* project — a real-time market
> **simulator** with bot traders, money conservation, and a MAUI charting client. **The reusable thing is the
> pattern, not these tools:** *find the one invariant that must never break, build a cheap check that proves it,
> and gate autonomy on that check* (see `CLAUDE_SETUP_PLAYBOOK.md` in this folder → "The one idea"). Everything in section A
> below is one instantiation of that pattern. Read it for *how the pattern plays out*, then build the equivalent
> for your own domain.

**Jargon used here** (project-specific): **CK=0** = "conservation check equals zero" — total money in the system
is unchanged by trading (our invariant). **soak** = a long real-time run of the built server used to catch bugs
that only appear over time or under concurrency. **candle** = an OHLC price bar. **arc** = a phase of this
project's work.

Grouped as: (A) problem-finding / correctness systems, (B) domain tooling, (C) techniques that generalize.

---

## A. Problem-finding & correctness systems (the heart of it)

### A1. ConservationProbe — the "CK=0" money invariant  ★ the single most valuable defect-finder
The whole simulator rests on one invariant: **money is conserved** — total cash + reserved + position value can't
be created or destroyed by trading, only moved. `ConservationProbe` checks this continuously; a non-zero delta
("CK≠0") means a settlement bug. It caught a **parallel-group race** on shared Fund/Position state that the unit
tests never saw (a reservation-reordering bug — real money bugs live in concurrency + ordering, exactly where
tests are weakest). **Generic lesson:** find your system's *conservation law* (money, inventory, reference
counts, row counts in ↔ out) and assert it continuously — it's a far stronger oracle than example-based tests.

### A2. ReservationAuditor — the secondary reconcile check
Reconciles Σ(reservations) against held balances. Its mismatch warnings turned out to be **benign self-healing
rounding**, so it's warn-gated by a threshold — the real signal to watch is A1 (ConservationProbe / CK), not the
auditor's reconcile noise. **Lesson:** know which of your probes is authoritative vs. advisory, and don't chase
the advisory one's noise.

### A3. The soak harness (`scripts/kse-balance-soak.ps1`)
Resets a `kse_soak` Postgres DB from a template, launches the **built** server (the real wall-clock bot loop),
and samples conservation / drift / book-depth every N seconds into CSVs. Health = the **`CK=0 CONS=0` log lines**,
NOT the process exit code (exit 255 at the end is a benign server-stop cleanup — a false "failure" that once
caused a partial harvest). Runs as `KieshStockExchange.Server.exe` (not `dotnet`) — check liveness via the
CSV/DB/notifications, not `Get-Process dotnet`. **Tiered durations:** 15 min smoke, 45 min A/B, 2 h bake-validation.

### A4. Parallel A/B soak workflow
Soaks are **non-deterministic and noisy** (wall-clock timing + parallel bots), so you can't compare a before-run
to an after-run. Instead run **both arms in parallel** (two servers, two ports), and point the live client at the
experimental arm so a human can eyeball it. Cap at 2 servers. **Lesson:** for stochastic systems, compare
*concurrent* arms, never sequential runs — and never trust a single run's numbers.

### A5. The golden-image RenderHarness gate
For the chart-drawable refactor (a deterministic subsystem): a `capture` mode writes golden PNGs, a `verify` mode
re-renders and does a **byte-exact compare + hit-test probes**, exit-code gated. Determinism is pinned with a
fixed clock (`TimeHelper.NowUtc` overridden) + `InvariantCulture`. This is the "diff before/after" idea done
right — **for a part of the system that IS reproducible.**

### A6. The shadow-run differ — and why it was INFEASIBLE here (important cautionary tale)
The plan was: a fixed-seed short sim → CSV of candle closes + fund/position totals, diffed before vs. after a
change. **It doesn't work for the live engine** — the tick loop is wall-clock-paced (`dt` = real elapsed time)
and settlement runs bots in parallel groups with jittered-backoff retry, so a fixed seed can't reproduce *itself*
run-to-run. The differ can only be an oracle where the system is deterministic (see A5, the render harness).
**Lesson: verify determinism BEFORE building a golden/diff harness.** Where the system isn't reproducible, fall
back to compiler/textual proof + characterization tests (portable note §5/§4). This finding turned a "scary CK
unification" into a safe path.

### A7. Characterization tests around the CK/order code
(Portable-note §4, but the highest-value instance was here.) Pinning `ReservationMath` and `OrderValidator`
behaviour surfaced **5 latent defects** nobody was hunting: a `ProjectedBuyReservation` asymmetry (StopMarketBuy
reserves 0 there vs. full budget elsewhere) and **4 `ValidateInput` vs `ValidateNew` divergences** (the two
order-validation entry points silently disagree on limit-buy-with-budget, true-market-sell-with-budget,
currency checks, and slippage range). Left documented, not "fixed" — owner decides.

### A8. The realism scorer (`r4_realism_score.py` / 16-stock scorer)
Quantifies how "real" the synthetic tape looks — chiefly `ret_acf_lag1` (1-minute return autocorrelation) and a
composite score. Turned "does the market feel real?" from eyeballing into a number, which is what let dozens of
tuning experiments be compared objectively. **Lesson:** for any subjective quality target, build a scalar metric
early — you can't tune what you can't measure.

## B. Domain tooling

### B1. Candle-CSV export pipeline
Every soak auto-exports 1-minute candles to `logs/candles-<Db>-<ts>.csv` (durable, comparable across soaks);
charted with `scripts/candle_plot.py --csv … --bucket-sec N` (aggregates up to any timeframe), never from the
live DB. **Lesson:** dump the observable to a stable, diffable artifact per run, and analyze the artifact — not
the live system.

### B2. Reseed / re-anchor tooling (candle-preserving)
Reseeding the market **keeps existing Candles** (chart continuity) and **re-anchors** each stock's SeedPrice to
its last candle close, so a reseed doesn't teleport the chart. Bot personas/probabilities are generated by the
`Tools/` pipeline into `AIUserData.xlsx`. (`Tools/` is out-of-scope for normal feature edits.)

### B3. The Flag Register (`docs/FLAG_REGISTER.md`) — ship-first, flip-later
Every performance or behaviour lever is a **default-OFF feature flag**, shipped dark, validated by soak, then
flipped. Nothing risky becomes live by merging. **Lesson:** decouple "merge the code" from "turn on the
behaviour" — a default-off flag makes autonomous merging of even risky code safe, because turning it on stays a
deliberate human act.

### B4. Telemetry log-viewer + economy telemetry
Per-category 24 h ring buffers with 1/5/15/60-min timeframes; `FxDeskTelemetry` / `BotEconomyTelemetry` /
`BotStatsLogger` for money-flow observability. **Lesson:** build the observability *for the invariant you care
about* (here, money flow), not generic logging.

## C. Project techniques that generalize (worth lifting)

### C1. The "moves-only git diff" proof (byte-identical splits)
When splitting an oversized class into `partial` files, the change is safe **iff `git diff --color-moved` shows
ONLY moved lines** — no additions/deletions. The diff itself is the proof; no test needed. Generalizes to any
pure code-relocation refactor.

### C2. The enhanced gate for CK-adjacent splits
For splits near money code, the plain gate was hardened: assert **zero field/ctor declarations** move to new
files (cross-file field-initializer order is compiler-dependent), keep an exact `using` block per file, and grep
out order-sensitive attributes (`[StructLayout]`, serialization, reflection). "Attended = blast radius, not
merely body edits" — some edits are trivial in isolation but dangerous by *what depends on them*.

### C3. Attended vs. Auto tiering
Three giant CK-critical services + the bracket coordinator were marked **Attended** (owner-gated) up front, so
the autonomous run could churn through everything else without ever risking the crown jewels. **Lesson:** name
your untouchable-without-a-human set explicitly *before* starting an autonomous run.

---

*The pattern behind all of A: pick the invariant that must never break (money conservation), make it a
continuously-checked probe, and treat everything else — soaks, golden images, characterization tests — as ways
to exercise the system hard enough that the probe fires when something's wrong.*
