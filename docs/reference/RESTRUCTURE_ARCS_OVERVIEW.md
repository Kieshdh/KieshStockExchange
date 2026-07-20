# Codebase Overhaul Plan — the single hub

**THE top-level plan for the ongoing overhaul of KieshStockExchange (.NET MAUI client + ASP.NET Core
server exchange sim).** The overhaul runs in three phases:

1. **Phase 1 — Structural restructure** (byte-identical splits of oversized files): **substantially DONE.**
2. **Phase 2 — Dedup + de-complication** (behavior-preserving): **CURRENT / in progress.**
3. **Phase 3 — Polish** (renames, empty-region cleanup, comment compaction): **LAST — after dedup.**

This file is the durable hub: it holds the methodology, conventions, status, and the phase roadmap. Per-arc
*detail* lives in its own referenced doc (created as each arc runs) — this file links them, it does not
duplicate them. Open it cold to know what the overhaul is, how a phase is run, and what remains.

**Sources this file distills** — do not re-litigate them here:
- `docs/CODEBASE_RESTRUCTURE_PLAN.md` — the 5-explorer + 4-lens council AUDIT (LOC map, real-extraction
  set, do-NOT-extract list, target folder trees, phased plan). Cited throughout; not duplicated wholesale.
- `docs/arcs/DEDUP_ARC_PLAN.md` — the Fable-5 safety contract governing Phase 2 (two-pass structure,
  qualifying rule, HARD BANS, per-candidate gate). The Phase-2 detail doc.
- `.claude/plans/snuggly-baking-nova.md` — the council-refined per-arc pipeline, the hard 2-arc rule, the
  compounding harness, and the A–E splitter conventions codified from the completed CHART arc.

---

# Phase 1 — Structural restructure (byte-identical splits) — substantially DONE

Split each oversized responsibility group into clean, focused files (**~500-line max**), MVVM-pure, with
behaviour **byte-identical** to before the move. The scarce resource is not advice — it is **VERIFICATION**.
Any number of planners can argue about seams; what decides an arc is the **ORACLE**: `dotnet build` green +
FULL `dotnet test` green + a **moves-only** `git diff` (no logic edits hidden in the move) + a **human
eyeball** for UI/XAML/gesture code that tests don't cover behaviourally. The oracle adjudicates, not
consensus. An arc is done when the oracle passes, not when the agents agree.

## 1.1 The two-arc rule (load-bearing)

Every responsibility group is restructured as **two separate arcs, never mixed in one commit/diff**:

1. **STRUCTURAL arc** — pure cut/paste moves, byte-identical, gated by the oracle. Zero renames, zero
   comment edits, zero simplification. (This is Phase 1.)
2. **POLISH arc** — a *separate* pass afterward: comment-compaction, simplification, naming, and regions
   (≤3–5 methods/region). Same build+test gate. (This is **Phase 3** — see below.)

"Byte-identical" and "rewrite for simplicity" cannot both be true in one diff, and a rename buried inside a
move makes the diff un-reviewable. This reconciles the standing **comments-LAST** rule: comments/polish are
the last phase, not interleaved. (Phase 2 dedup is a distinct middle case — it *does* change code, so it has
its own safety contract; see Phase 2.)

## 1.2 The per-arc pipeline (council-refined 2026-07-17)

The 11-role fan-out originally drafted was pressure-tested by a Fable-5 council and collapsed to this lean
shape (which the CHART arc validated in flight). **Every role runs in a SEPARATE CONTAINER.**

- **Phase 0 — SHARED GROUND TRUTH (1 explorer/orchestrator, FIRST).** Produce the one artifact every
  planner annotates: file + member map, the **FROZEN public/internal SURFACE** (every member + its
  call-site count = the byte-identical contract), the target file-tree, and duplicate-code hits. Everyone
  plans against the SAME boundaries — this is what stops isolated agents inventing incompatible splits.
- **Phase 1 — 2–3 PARALLEL PLANNERS annotate that artifact:**
  - **Splittor** — method + class + model boundaries fused into one "where do the seams go?" question
    (do NOT split this into 3 agents).
  - **Outsider** — cross-codebase dedup; the only planner reading OUTSIDE the target group.
  - **MVVM-purist** — folder tree + naming of the split.

  (Referencor/Safety are NOT advisors — they are the Phase-0 contract and the Phase-3 gate.
  Regionor/Naming-invertor/Commenter/Compactor are NOT structural agents — they belong to the polish phase.)
- **Phase 2 — COUNCIL.** Resolve planner overlaps into ONE ordered change-set.
  > **★ OWNER PREFERENCE (Kiesh, 2026-07-18): invoke the council LIBERALLY, not just on planner conflict.**
  > Route to the council ANY decision it might solve — arc ordering, owner-level calls, scope (which files to
  > attempt vs queue), split-vs-no-split judgment, methodology, and any fork in the process — rather than
  > deciding solo. **Hard / high-stakes problems → run the council on FABLE 5** (`model: fable` advisors);
  > routine decisions can use the default advisor model. The goal: solve most problems yourself via the
  > council instead of escalating to Kiesh. (Clear-cut, low-ambiguity mechanical calls with proven precedent
  > don't need a council — use judgment, but bias toward councilling when there's genuine ambiguity.)
- **Phase 3 — EXECUTOR: MECHANICAL MOVES ONLY.** Cut/paste members into new files, zero logic edits.
  **Runs on Opus 4.8 (`claude-opus-4-8`)** (owner preference — spawn the executor agent with `model: opus`).
  GATE = `git diff` moves-only + `dotnet build` + FULL `dotnet test` green (+ human eyeball for UI/XAML/
  gesture behaviour). The orchestrator verifies the gate independently before commit.

## 1.3 Splitter conventions — the worked spec (A–E)

Codified from the CHART precedent, Kiesh-approved. Every Splittor + Executor follows these.

**A. Finding the class cut (Splittor)**
1. **Cut on ONE discriminating question**, not a vibe. Chart's was *"does it need candles?"* — that one
   axis cleanly partitioned a 2240-line class. State the question explicitly and VERIFY empirically: count
   how many members cross it (chart = exactly ONE, the live price). Few crossings = clean cut; many = wrong
   axis, pick another.
2. **Enumerate the SEAM explicitly** = the short list of things that cross the boundary. Each becomes an
   INJECTED dependency, never a back-pointer: `Func<T>` for a pulled value, `Action`/callback for a pushed
   signal, a `LoadFor(...)` method the parent calls on lifecycle events, `PropertyChanged` subscription for
   a reverse-direction reaction. **Dependency stays ONE-WAY: parent knows child, child NEVER knows parent.**
3. **Shared single-instance machinery stays on the parent.** A coalescer/cache/timer/CTS that must be one
   instance (the 16 ms redraw coalescer) stays put; the child gets an injected delegate to it, never a
   duplicated second copy.
4. **An extracted child VM is a PLAIN `ObservableObject`** — do NOT inherit a heavy base
   (`StockAwareViewModel`) that re-subscribes a pipeline (creates a second racing subscription). The parent
   stays the single pipeline owner and drives the child.

**B. Carving to the ~500 cap (Splittor + Executor)**
5. **Partial-class by concern**, file named `X.Concern.cs` with a guessable noun
   (`ChartViewModel.Viewport.cs`, `.Stream.cs`; `ChartDrawingViewModel.Undo.cs`, `.Pen.cs`). ONE spine file
   (`X.cs`) holds ctor + services + lifecycle + the redraw pump. Never `.Part2.cs`.
6. **Pure, stateless math → `Helpers/` static** (`ChartMath` = ZoomOffset/AverageCostBasis/PositionPnl).
   Leave instance-coupled helpers (buffer upserts, binary search) in the VM.
7. **Inheritance ONLY where it removes REAL duplication** (`PenTile → PenSpecimenTile → the 5 tiles`
   collapsed 5 near-identical classes). Do NOT force a base where types share only a convention.

> **★ OWNER PREFERENCE (Kiesh, 2026-07-17): prefer COMPOSED COLLABORATOR CLASSES over partial-class
> sharding where a clean seam allows it.** Kiesh's default lean is real standalone classes the parent
> composes/delegates to (a renderer, a hit-tester) rather than `X.Concern.cs` partials of one god-class.
> This does NOT override convention 5 blindly — the **per-arc council/oracle decides the best move for each
> file**, weighing this preference against the byte-identical verification cost (composition injects state ⇒
> changes member bodies ⇒ loses the sorted-line-diff proof, needs the full human eyeball battery instead).
> Where state genuinely flows through every helper, the honest path is: byte-identical partial split FIRST
> (safe, verified), THEN a separate arc that reifies the shared cache into an explicit context object and
> peels off real collaborators one at a time. Where a clean pure/stateless seam exists, extract a composed
> class directly. Record the per-file decision in that arc's plan file.

**C. Folder move (Executor)**
8. **Folder ≠ namespace: keep namespaces FLAT on the move** (files go into `Chart/` but keep the existing
   `namespace` and `x:Class`) ⇒ ZERO using-churn in referencing files. Namespace alignment is a SEPARATE
   later pass (the "big restructure", `CODEBASE_RESTRUCTURE_PLAN.md §5/§6`), never mixed with a logic move.
   `git mv` tracked files; verify no csproj edit is needed (default globbing).

**D. MVVM/MAUI view-extraction specifics (Splittor + Executor)**
9. Child views get BindingContext from the parent: either INHERITED, or `BindingContext="{Binding SubVm}"`
   on the host tag — then strip the `SubVm.` prefix off the moved bindings.
10. **Handlers that need parent internals BUBBLE as events (event-relay)** — the sub-view exposes
    `public event EventHandler? XRequested`, the parent wires `XRequested="OnX"` and its handler BODY is
    unchanged.
11. **Overlay sub-view roots need `InputTransparent="True" CascadeInputTransparent="False"`** or they eat
    gestures; **child declaration order = Z-order**; source-compiled bindings need per-view `x:DataType` +
    a per-binding `x:DataType` on any `Source`/`x:Reference`.

**E. Gates (Executor)**
12. Build + full test after each step. **UI/XAML/gesture behaviour is NOT covered by tests → human eyeball
    is a REQUIRED gate.** **No comment/rename/compaction in a structural diff** — that is Phase 3.

### 1.3bis. CK-ADJACENT partial-split gate + the Attended line (Fable-5 council, 2026-07-18)

A PURE byte-identical partial split (redistribute intact members, add `partial`, ZERO body edits) is
behavior-invariant *by construction*, so CK-adjacent files are eligible to ship UNATTENDED — BUT the
moves-only-diff + full-suite gate is NOT a complete oracle. Before shipping a CK-adjacent split, ALSO:
- **Field/ctor spine check (the #1 hazard):** ALL fields + field-initializers + constructors + any static
  ctor stay in the SPINE. Assert **ZERO field/ctor declarations in the new partial files** (grep). Cross-file
  field-initializer execution order is compiler-dependent → keeping every field in one file eliminates it.
- **Exact using block:** each new partial carries the original file's using block VERBATIM (a different
  per-file using set can silently rebind an extension method / unqualified type while compiling clean).
- **Order-sensitivity grep:** confirm the type has NO `[StructLayout(Sequential/Explicit)]`, NO
  `[CallerFilePath]`/`[CallerLineNumber]`, and is NOT consumed by member-declaration-order-dependent
  reflection/serialization (Newtonsoft order, EF property discovery, DI scanning, golden JSON).
- **moves-only diff** via `git diff --color-moved`; **full suite ×2** for CK files; **CK smoke** sized to
  blast radius.

**The Attended line = BLAST RADIUS, not merely "body edits."** "Attended" flags the code the owner wants
eyes on. Unattended-eligible (with the enhanced gate + CK smoke): OrderBook `.Admin`,
ConvictionDecisionService, order-entry/portfolio-style services. **PREPARE-BUT-HOLD for the owner:**
TradeSettler / the settlement core. **Skip low-ROI splits** that don't even meet the ~500 cap.

## 1.4 Reference examples

- **Server MarketEngine split** — already cleanly split by responsibility
  (`Execution/` · `Brackets/` · `Matching/` · `Settlement/`; see `CODEBASE_RESTRUCTURE_PLAN.md §6`).
  The model of a clean, responsibility-partitioned area — revisit it when unsure what "good" looks like.
- **The COMPLETED CHART arc (commit `61687fd`)** — the hand-crafted PRECEDENT. `ChartView` + `ChartViewModel`
  moved into `Chart/`; `ChartDrawingViewModel` extracted as a real MVVM child VM; both VMs partial-carved to
  ≤~490 lines; **619 tests green**. The worked example every convention above was distilled from.

## 1.5 Phase-1 status — what shipped

### Autonomous server-arc run — 2026-07-18 (branch `feature/bot-market-realism-v2`)
The council-ordered **Auto** backlog is **COMPLETE**. Shipped byte-identical (build + FULL suite + independent
moves-only diff; CK smokes where noted), newest last:
- **Arc 1** `5162397` — client-VM one-class-per-file: 29 trailing row/DTO/enum types out of 21 host VMs.
  Detail: `docs/arcs/ARC_client-vm-multiclass.md`.
- **Arc 2a** `3bd8900` — CandleService 1051→430 spine + `.Read`/`.Maintenance`/`.Aggregation`. **15m CK smoke = 0.**
- **Arc 2b** `927c6bc` — `CandleAggregationMath` pure-static helpers. Detail: `docs/arcs/ARC_candleservice.md`.
- **Arc 3** `a4a25a7` — AccountsCache 1014→203 spine + `.Hydration`/`.Reseed`/`.Reconcile` (all state/gate fields
  + nested release classes stay in spine → invariant intact). **10m CK smoke = 0.** Detail: `ARC_accountscache.md`.
- **De-flake** `f57aa13` — the two known-flaky tests (`SharedScanEquivalence`, `BankEstimateAnchorPivot`) shared
  one root cause: a parallel test stomping process-global `TimeHelper.NowUtc`. Fixed test-only via a
  `[CollectionDefinition("ClockSerial", DisableParallelization=true)]` group. **Suite now reliably 661/661 —
  any failure is a REAL regression.**
- **BotDashboardViewModel** `d9298e9` — 754→175 spine + `.LiveStatus`/`.Panels`/`.Controls`. Detail: `ARC_botdashboardvm.md`.
- **ApiDataBaseService** `3286900` — entity partial-split (byte-identical).
- **OrderBook `.Admin`** `3a1bf1e` — single `_gate` preserved; `.Admin.cs` partial only.
- **ConvictionDecisionService** `159e182` — partial-split (byte-identical).
- **OrderEntryService** `cfa302c` — partial-split (byte-identical).
- **AiBotStateService** `612fcc4` — spine + `.Pruning` partial-split (byte-identical).
- **ExcelSeedService** `704776d` — spine + seed-steps partial-split (byte-identical).
- **Decision docs** `457634d`, `5a4a74a`; **Attended prep maps** `3e5741e`.

### NO-SPLIT decisions (cohesive, correctly declined — recorded in `docs/arcs/`)
- **MarketViewModel** (612) — comment-inflated, concerns interwoven through private call chains; `ARC_marketviewmodel_NOSPLIT.md`.
- **BotSentimentService** (716) — same shape; a split would scatter one flow.
- **AiBotContext** (637) — ~355 real LOC, RNG-draw-order determinism, one ~40-line peelable region only.
- **PgDBService.Orders.cs** (637) / **PgDBService.Portfolio.cs** (567) — already entity-partials; further split low-ROI.
- **Order.cs** (586) — cohesive domain model, invariant/CK-welded setters.
- Full triage: `docs/arcs/REMAINING_CANDIDATES_LEDGER.md`.

### Prior phase: CHART arcs — ALL DONE (tip `da91346`)
- VM/View split (`61687fd`); `CandleChartDrawable` byte-identical partial split (`2d4491f`) → 9 files;
  `CandleChartDrawable` **Arc-2 COMPOSITION** — reified `RenderFrame`/`ChartTheme`/`ChartGeometry` + 8
  collaborator classes (Axis/Candle/Indicator/Overlay/Drawing/Crosshair/Measure renderers + ChartHitTester),
  each step gated by the `RenderHarness` golden-image tool. Spine ~544 lines. Chart tool fixes + Fib/Alert/
  indicator calculators shipped alongside. (History: `docs/OVERNIGHT_2026-07-17.md`.)

## 1.6 Remaining Phase-1 work — OWNER-GATED (Attended), do NOT run unattended

These need Kiesh present + a mandatory soak; each has a staged prep-map doc. **Never soak-gate or flip a bake
unattended — STOP and flag for Kiesh.** LOC from `CODEBASE_RESTRUCTURE_PLAN.md §1`.

| Target | ~LOC | Why Attended | Prep-map doc |
|---|---|---|---|
| **AiBotDecisionService** | 3063 | CK-critical bot brain; needs `BotDecisionConfig` extraction FIRST (removes ~320-field ctor coupling), then `AiBotDecisionMath` static + partial-carve; config-equivalence dump gate | `docs/arcs/ATTENDED_aibotdecisionservice_MAP.md` |
| **OrderExecutionService** | 2712 | CK-critical order orchestrator; `GroupCommitCoordinator` = ONE whole tx-unit (never fragment tx-body from post-commit apply); `RejectedFillRollback` already static; ONE extraction per CK soak | `docs/arcs/ATTENDED_orderexecutionservice_MAP.md` |
| **AiTradeService** | 2447 | CK-critical bot tick loop; real-extract `BatchSubmissionService` only, keep loop + timers whole; design the session-counter seam first | `docs/arcs/ATTENDED_aitradeservice_MAP.md` |
| **BracketCoordinator** | 1216 | reservation-release invariant is inline-welded across long+short handlers → a partial split fragments it (triaged Auto→Attended) | `docs/arcs/ARC_bracketcoordinator_TRIAGE.md` |
| **TradeSettler** | 890 | settlement core (reserve→settle→release); pure partial sheds only ~110 lines, `SettleNoTxAsync` monolith stays whole; only oracle is a multi-hour CK soak | `docs/arcs/HOLD_tradesettler_SPLIT_PLAN.md` |

For the full audit detail (real-extraction set, do-NOT-extract list, target folder trees, the phased plan and
gates), see **`docs/CODEBASE_RESTRUCTURE_PLAN.md`** — not duplicated here.

---

# Phase 2 — Dedup + de-complication (behavior-preserving) — CURRENT / IN PROGRESS

Authorized by Kiesh 2026-07-18: remove duplicate methods (→ helpers), simplify overcomplicated code WITHOUT
changing behaviour/goal or losing readability/safety; add helper classes + enums + dataclasses. **Unlike
Phase 1, this CHANGES code → the moves-only-diff proof is GONE.** The governing safety contract is the
Fable-5 council verdict in **`docs/arcs/DEDUP_ARC_PLAN.md`** — full detail there; the essentials:

## 2.1 Two-pass structure
- **Pass 1 — AUTONOMOUS (provably-safe subset ONLY).** Qualifying rule (hard gate): a change is
  autonomous-eligible **iff you can state in ONE SENTENCE why the COMPILER or a TEXTUAL DIFF — not the test
  suite — guarantees identical behaviour.** Eligible: exact-duplicate method-body extraction → one helper;
  provably-dead-code deletion; renames; pure stateless helper consolidation (no I/O, locks, mutation,
  clock/RNG) that is property-test-equivalent.
- **Pass 1b — NEAR-DUPLICATE GENERALIZATION** (Kiesh: "also look for SIMILAR code"). Similar-but-not-identical
  code → ONE parameterized helper. NEEDS-CARE: diff all N sites, enumerate every difference, route ONLY sites
  that produce byte-identical output, leave/flag any real variant, **adversarial review PER-SITE**; still
  non-CK / non-transaction / non-rounding to run unattended (money-adjacent near-dups → Pass 2).
- **Pass 2 — PROPOSE-ONLY (reviewed diffs for Kiesh, do NOT merge).** Everything needing judgment: money/
  decimal math, rounding, Fund/Position/reservations, transaction-scoped code, reserve→release ordering,
  records/enums on persisted models, Order-type string→enum, and "simplify this complicated code."
  Deliverable = diff + written equivalence argument per item in `docs/arcs/DEDUP_PASS2_PROPOSALS.md`.

## 2.2 HARD BANS unattended (queue to Pass 2 / owner)
Anything inside `RunInTransactionAsync`/savepoint scope; any decimal rounding / `MidpointRounding`;
Fund/Position/reservation mutation and **reserve→release ORDERING** (the P2 race `853c7e6`); **Order-type
strings → enum: FORBIDDEN** (CLAUDE.md mandates string constants); `record` on persisted models; Settlement /
Matching / OrderExecutionService / the 3 Attended giants; **scar tissue** — apparent "overcomplication" that
git-blame shows is a defensive gate from a real past incident. **CK=0 is sacred.**

## 2.3 Per-candidate gate (Pass 1)
1. **Characterize-first** — pin current behaviour with 2-3 focused tests BEFORE editing (skip only for pure
   syntactic dedup of identical bodies). 2. Edit ONE candidate. 3. Build BOTH TFMs + FULL suite (**661**).
4. **Adversarial diff review by a SEPARATE agent** that never saw the rationale: given only `git diff`, it
   answers PRESERVED / CHANGED / UNSURE (per-site for near-dups) — **UNSURE or CHANGED → revert.** 5. One
   candidate = one commit (bisectable). **Batch gate** (every ~5-8 commits): full suite + CK smoke + the
   **shadow-run differ** (fixed-seed short sim before/after → CSV of candle closes + fund/position totals;
   any drift flags the batch even at 661/661 → bisect + revert).

## 2.4 Phase-2 status — what shipped so far
Shipped + verified PRESERVED (Pass 1, branch `feature/bot-market-realism-v2`):
- `f9a009b` **PagerMath** — byte-identical `ComputeVisiblePages` extract.
- `a1878bf` **ParsingHelper** `class`→`static class` (compiler-proven).
- `d6e9635` **GetListAsync<T>** — 29 list-GET call sites generalized.
- `483dd5e` **SymbolOrDash** extension — 8 sites.
- REFUSED (correctly): the 5 signed-percent formatters are genuinely different (decimal vs double, format
  specifier, culture, sign-at-zero) — unifying would change numbers. Do NOT retry as a merge.
- Scaffolding: plan `477eae2`, inventories `2f6f094`, Pass-1b directive `b8acfce`, handoff `ce4060b`.

**The live worklist / handoff is `docs/arcs/DEDUP_HANDOFF.md`** (updated at every clean stopping point — read
it FIRST to continue). Next up there: `RunBusyAsync` base-VM helper (~22 busy-guards), then server-non-CK math
behind the shadow-run differ. **Candidate inventories:** `docs/arcs/DEDUP_{client,server_nonck,shared_helpers}_INVENTORY.md`
(tagged PROVABLY-SAFE / NEEDS-CARE / CK-TOUCHING; Pass 1 pulls only PROVABLY-SAFE).

---

# Phase 3 — Polish — LAST (after dedup)

The separate POLISH arc of the two-arc rule (§1.1), run only after Phase 2 completes — Kiesh's sequencing:
- **File renames** — `MarketViewModels.cs` → `MarketViewModel.cs`, and the other multi-class-file renames left
  from the Phase-1 one-class-per-file arc.
- **Empty `#region` cleanup** — the empty regions left behind by the Phase-1 partial splits.
- **Comment compaction** — compact-format comments, remove journals / "useful-later" scaffolding.
- (Deferred here too, per the audit: the **namespace/folder alignment "big restructure"**,
  `CODEBASE_RESTRUCTURE_PLAN.md §5/§6` — folder = namespace, one area per commit.)

Same build + full-suite gate as every other phase. No behaviour change.

---

# Compounding artifacts

The overhaul is an engine, not a sequence of one-off splits. Each arc feeds four durable assets:
- **Living codebase map** — the Phase-0 "useful-later" file grows into a standing map (responsibilities +
  dependencies + a dedup backlog that surfaces the *next* target).
- **The convention spec** — this file (§1.3 A–E + §1.1 two-arc rule + Phase-2 safety contract). Every future
  executor is graded against it.
- **The precedent corpus** — each finished arc (+ MarketEngine + CHART) becomes a few-shot example, so arc
  N+1 is better-primed than arc N.
- **The byte-identical / behaviour-preserving gate** — the FROZEN surface + moves-only diff + full suite
  (Phase 1) and the adversarial-diff + shadow-run differ (Phase 2) are reusable golden-master gates, not
  per-arc promises.
