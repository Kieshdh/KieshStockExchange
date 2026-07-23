# METHOD — A/B soaks, the CK=0 gate, and the build/validation discipline

The reusable "how we validate a market/perf change" toolkit. Every realism or performance lever is proven
(or killed) through this pipeline before it is baked.

## Reversible, default-OFF flags
Every new lever ships **flag-gated and default-OFF**, and **byte-identical when off** — disabled, it adds
exactly 0 and draws 0 RNG, so default runs stay reproducible and the change is trivially reversible. On prod
the lever is enabled via **env override** (`docker-compose.prod.yml`, `Section__Key` form), never by editing
base `appsettings.json`. This is what makes a prod A/B safe: flip an env var, not the build. (See the
`FLAG_REGISTER` for each flag's lifecycle state.)

## A/B soak protocol
- A **soak** is a timed local run of the real server+bot fleet. Tiers: **15m smoke** (screen), **45m** (the
  A/B realism/perf workhorse), **2h** (bake-validation). Never bake off a single screen — screen cheap to
  find a winner, then one ~2h soak to confirm.
- Run arms **in parallel**, **max 2 servers** at once (fill both slots with a 2–3 arm battery spanning the
  live hypothesis; stagger launches so the `CREATE DATABASE … TEMPLATE` clone doesn't race). Soaks are
  **non-deterministic + noisy**, so compare arms that ran concurrently, not across time.
- Point the **live client at the experimental (ON) arm** (build-time port 5083) so the owner can eyeball the
  variable of interest, unless told otherwise.
- Verify the arm's config actually took effect at warmup (startup CONFIGCHECK) before trusting its metrics.

## The CK=0 hard gate
**CK** = conservation check: no money or shares are ever created or destroyed. `CK=0` is a **HARD gate on
every soak** — verified live by `ConservationProbe`. A change that moves its target metric but breaks
conservation is rejected outright. Judge the full end-state on **ground-truth output (fills, not
intent-proxies)** and watch side metrics (clustering, liquidity, tails, drift) that only show over time — a
change can hit its target yet regress a side metric.

## The candle-CSV pipeline
Each soak auto-exports 1-min candles to `logs/candles-<Db>-<ts>.csv` (durable, comparable across soaks).
Chart via `candle_plot.py --csv … --bucket-sec N` (aggregates up to any bucket), **not** from the DB.

## Disk-frugal build gating
The dev box pegs disk **I/O** (not space) during builds (VS + Docker + Defender contend). So: gate via
`dotnet test` alone for server/shared changes; scope client builds narrowly; pre-flight `% Disk Time` and
wait if ≥70%; build at Idle priority + `-maxcpucount:1`; **never** `dotnet clean`; keep cadence low.

## Local validation ladder (fail fast — never burn a soak on a compile gap)
`apply → dotnet build → unit/equivalence tests → ~5-min flag-OFF soak asserting CK=0 + byte-identical
baseline → THEN the flag-ON CK-gated soak (45m default / 2h bake) → commit/push.`

**Source of truth:** `~/.claude/.../memory/feedback_autonomous_research_loop.md`,
`feedback_soak_duration.md`, `feedback_candle_csv_pipeline.md`, `feedback_disk_frugal_gating.md`,
`feedback_ab_client_pointing.md`; the CK proof is walked in [`../explainers/ENGINE_MECHANICS.md`](../explainers/ENGINE_MECHANICS.md).
