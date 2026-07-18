# Codebase Restructure — Arcs Overview & Roadmap

**The single roadmap for the ongoing restructure of KieshStockExchange (.NET MAUI client +
ASP.NET Core server exchange sim).** This file is the durable hub: methodology, conventions, and the
ordered arc backlog. Per-arc *detail* lives in its own plan file, created as each arc runs. Open this
cold to know what the restructure is, how an arc is run, and what comes next.

**Sources this file distills** — do not re-litigate them here:
- `docs/CODEBASE_RESTRUCTURE_PLAN.md` — the 5-explorer + 4-lens council AUDIT (LOC map, real-extraction
  set, do-NOT-extract list, target folder trees, phased plan). Cited throughout; not duplicated wholesale.
- `.claude/plans/snuggly-baking-nova.md` — the council-refined per-arc pipeline, the hard 2-arc rule, the
  compounding harness, and the A–E splitter conventions codified from the completed CHART arc.

---

## 1. Purpose & principles

Split each oversized responsibility group into clean, focused files (**~500-line max**), MVVM-pure, with
behaviour **byte-identical** to before the move. The scarce resource is not advice — it is **VERIFICATION**.
Any number of planners can argue about seams; what decides an arc is the **ORACLE**: `dotnet build` green +
FULL `dotnet test` green + a **moves-only** `git diff` (no logic edits hidden in the move) + a **human
eyeball** for UI/XAML/gesture code that tests don't cover behaviourally. The oracle adjudicates, not
consensus. An arc is done when the oracle passes, not when the agents agree.

---

## 2. The two-arc rule (load-bearing)

Every responsibility group is restructured as **two separate arcs, never mixed in one commit/diff**:

1. **STRUCTURAL arc** — pure cut/paste moves, byte-identical, gated by the oracle. Zero renames, zero
   comment edits, zero simplification.
2. **POLISH arc** — a *separate* pass afterward: comment-compaction (compact format + remove journals/the
   "useful-later" file), compactor simplification, naming, and regions (≤3–5 methods/region). Same
   build+test gate.

"Byte-identical" and "rewrite for simplicity" cannot both be true in one diff, and a rename buried inside a
move makes the diff un-reviewable. This supersedes the old "commenter folds into exploration" idea and
reconciles the standing **comments-LAST** rule: comments/polish are the last arc, not interleaved.

> **The pending "naming-conventions" work is a POLISH arc** — it runs *after* the structural moves and the
> chart drawing tools are finished, never as part of a structural move.

---

## 3. The per-arc pipeline (council-refined 2026-07-17)

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
  Regionor/Naming-invertor/Commenter/Compactor are NOT structural agents — they belong to the polish arc.)
- **Phase 2 — COUNCIL (Fable-5), ONLY if the planners actually conflict.** Narrow job: resolve overlaps
  into ONE ordered change-set. Skip it when the planners agree.
- **Phase 3 — EXECUTOR: MECHANICAL MOVES ONLY.** Cut/paste members into new files, zero logic edits.
  **Runs on Opus 4.8 (`claude-opus-4-8`)** (owner preference — spawn the executor agent with
  `model: opus`). GATE = `git diff` moves-only + `dotnet build` + FULL `dotnet test` green (+ human
  eyeball for UI/XAML/gesture behaviour). The orchestrator verifies the gate independently before commit.

---

## 4. Splitter conventions — the worked spec (A–E)

Codified from the CHART precedent, Kiesh-approved. Every future Splittor + Executor follows these.

**A. Finding the class cut (Splittor)**
1. **Cut on ONE discriminating question**, not a vibe. Chart's was *"does it need candles?"* — that one
   axis cleanly partitioned a 2240-line class. State the question explicitly and VERIFY empirically: count
   how many members cross it (chart = exactly ONE, the live price). Few crossings = clean cut; many = wrong
   axis, pick another.
2. **Enumerate the SEAM explicitly** = the short list of things that cross the boundary. Each becomes an
   INJECTED dependency, never a back-pointer: `Func<T>` for a pulled value, `Action`/callback for a pushed
   signal, a `LoadFor(...)` method the parent calls on lifecycle events, `PropertyChanged` subscription for
   a reverse-direction reaction. **Dependency stays ONE-WAY: parent knows child, child NEVER knows parent**
   (the child takes `Attach(currentPrice, requestRedraw)`, not a reference to the parent VM).
3. **Shared single-instance machinery stays on the parent.** A coalescer/cache/timer/CTS that must be one
   instance (the 16 ms redraw coalescer) stays put; the child gets an injected delegate to it, never a
   duplicated second copy.
4. **An extracted child VM is a PLAIN `ObservableObject`** — do NOT inherit a heavy base
   (`StockAwareViewModel`) that re-subscribes a pipeline (creates a second racing subscription). The parent
   stays the single pipeline owner and drives the child.

**B. Carving to the ~500 cap (Splittor + Executor)**
5. **Partial-class by concern**, file named `X.Concern.cs` with a guessable noun
   (`ChartViewModel.Viewport.cs`, `.Stream.cs`, `.Display.cs`; `ChartDrawingViewModel.Undo.cs`, `.Pen.cs`).
   ONE spine file (`X.cs`) holds ctor + services + lifecycle + the redraw pump. Never `.Part2.cs`.
6. **Pure, stateless math → `Helpers/` static** (`ChartMath` = ZoomOffset/AverageCostBasis/PositionPnl).
   Leave instance-coupled helpers (buffer upserts, binary search) in the VM.
7. **Inheritance ONLY where it removes REAL duplication** (`PenTile → PenSpecimenTile → the 5 tiles`
   collapsed 5 near-identical classes). Do NOT force a base where types share only a convention, not
   behaviour.

> **★ OWNER PREFERENCE (Kiesh, 2026-07-17): prefer COMPOSED COLLABORATOR CLASSES over partial-class
> sharding where a clean seam allows it.** Kiesh's default lean is real standalone classes the parent
> composes/delegates to (e.g. a renderer, a hit-tester) rather than `X.Concern.cs` partials of one god-class.
> This does NOT override convention 5 blindly — the **per-arc council/oracle decides the best move for each
> file**, weighing this preference against the byte-identical verification cost (composition injects state ⇒
> changes member bodies ⇒ loses the sorted-line-diff proof, needs the full human eyeball battery instead).
> Where state genuinely flows through every helper (an immediate-mode renderer sharing a per-frame cache),
> the honest path is: byte-identical partial split FIRST (safe, verified), THEN a separate arc that reifies
> the shared cache into an explicit context object and peels off real collaborators one at a time. Where a
> clean pure/stateless seam exists, extract a composed class directly. Record the per-file decision in that
> arc's plan file.

**C. Folder move (Executor)**
8. **Folder ≠ namespace: keep namespaces FLAT on the move** (files go into `Chart/` but keep the existing
   `namespace` and `x:Class`) ⇒ ZERO using-churn in referencing files. Namespace alignment is a SEPARATE
   later pass (the "big restructure", Phase 5), never mixed with a logic move. `git mv` tracked files;
   verify no csproj edit is needed (default globbing; stale `MauiXaml Update` paths are silent no-ops).

**D. MVVM/MAUI view-extraction specifics (Splittor + Executor)**
9. Child views get BindingContext from the parent: either INHERITED, or `BindingContext="{Binding SubVm}"`
   on the host tag — then strip the `SubVm.` prefix off the moved bindings.
10. **Handlers that need parent internals BUBBLE as events (event-relay)** — the sub-view exposes
    `public event EventHandler? XRequested`, the parent wires `XRequested="OnX"` and its handler BODY is
    unchanged.
11. **Overlay sub-view roots need `InputTransparent="True" CascadeInputTransparent="False"`** or they eat
    gestures across their cell; **child declaration order = Z-order**; source-compiled bindings need per-view
    `x:DataType` + a per-binding `x:DataType` on any `Source`/`x:Reference`.

**E. Gates (Executor)**
12. Build + full test after each step. **UI/XAML/gesture behaviour is NOT covered by tests → human eyeball
    is a REQUIRED gate** (pan/zoom/crosshair under overlays, panel drag/auto-open, mutual-close). **No
    comment/rename/compaction in a structural diff** — that is the separate polish arc.

---

## 5. Reference examples

- **Server MarketEngine split** — already cleanly split by responsibility
  (`Execution/` · `Brackets/` · `Matching/` · `Settlement/`; see `docs/CODEBASE_RESTRUCTURE_PLAN.md §6`).
  The model of a clean, responsibility-partitioned area — revisit it when unsure what "good" looks like.
- **The COMPLETED CHART arc (commit `61687fd`)** — the hand-crafted PRECEDENT. `ChartView` + `ChartViewModel`
  moved into `Chart/` views + VMs; `ChartDrawingViewModel` extracted as a real MVVM child VM; both VMs
  partial-carved to ≤~490 lines; **619 tests green**. This is the worked example every convention above was
  distilled from — few-shot it for arc N.

---

## 6. Arc backlog (ordered, lowest-risk-first)

Ordered roughly per `docs/CODEBASE_RESTRUCTURE_PLAN.md §7` phasing. **Attended** = CK-critical, run with
Kiesh present + a mandatory soak; **Auto** = autonomous-safe (compiler/test-verified). LOC from the audit's
§1 map.

| Arc | Target group | ~LOC | Layer | Cut axis / shape | Gate |
|---|---|---|---|---|---|
| Multi-class-per-file cheap wins | Row/DTO classes trailing in `UserDetailsViewModel`, `AccountViewModel`, `PositionTableViewModel`, `ModifyOrderViewModel`, `OrderBookViewModel`, `SegmentedTabView`; `MarketViewModels.cs`, `TradeViewModels.cs` | — | Client VM | One-class-per-file; row DTOs → `Tables/Rows/` | build + full suite (Auto) |
| **CandleChartDrawable** | client chart renderer | 1987 | Client | Discriminating axis: render / hit-test / **PriceScale math → `Helpers/`**. The immediate CHART follow-up. | build + full suite + human eyeball (Auto) |
| CandleService | server candle aggregator | 1051 | Server | Partial `.Maintenance`; real-extract `CandleAggregationMath` (static, stateless) | build + suite + 15m smoke, CK=0 (Auto) |
| AccountsCache | server fund/position cache | 1014 | Server | **Partials ONLY** (`.Hydration`/`.Reconcile`) — hot mutation + cold hydration share dicts under one lock; **NOT** a reconcile service (§4) | build + suite (Auto) |
| BracketCoordinator | server bracket lifecycle | 1216 | Server | `.Long.cs` / `.Short.cs` partials + shared `.Core.cs` holding reservation-release + `LegState`; **keep the release invariant in one type** (§4) | build + suite + smoke, CK=0 (Auto→Attended if seam moves) |
| **AiTradeService** | server bot tick loop | 2447 | Server | Real-extract `BatchSubmissionService` only; keep the loop + timers whole (§4) | build + suite + **multi-hr CK soak** (Attended) |
| **OrderExecutionService** | server order orchestrator | 2712 | Server | `.Batch` partial; real-extract `GroupCommitCoordinator` as ONE whole tx-unit (never fragment tx-body from post-commit apply); `RejectedFillRollback` static. **CK-critical.** | build + suite + **multi-hr CK soak, ONE extraction per soak** (Attended) |
| **AiBotDecisionService** | server bot decision brain | 3063 | Server | **`BotDecisionConfig` FIRST** (immutable options record — removes the ~320-field ctor coupling that blocks every other bot split; §3/§4), then `AiBotDecisionMath` static + partial-carve | **config-equivalence dump MUST pass**, then 45m behaviour soak (Attended) |

Deferred / do-NOT-real-extract (partial or method-extract only, per `§4`): **TradeSettler**
(reserve→settle→release CK invariant stays visible in one type), **OES group-commit tx-body** (whole-unit
only), **BracketCoordinator release seam**, **OrderBook** single `_gate` lock (`.Admin.cs` partial only),
**AccountsCache** (partials, not a reconcile service). These stay cohesive by design.

---

## 7. Status

- **CHART arcs: ALL DONE** (branch `feature/bot-market-realism-v2`, tip `da91346`):
  - VM/View split (`61687fd`) — `ChartViewModel`+`ChartView` → `Chart/` folders, `ChartDrawingViewModel` extracted.
  - `CandleChartDrawable` byte-identical partial split (`2d4491f`) → 9 files.
  - `CandleChartDrawable` **Arc-2 COMPOSITION** — reified `RenderFrame`/`ChartTheme`/`ChartGeometry` + 8 collaborator
    classes (Axis/Candle/Indicator/Overlay/Drawing/Crosshair/Measure renderers + ChartHitTester), each step gated by
    the `KieshStockExchange.RenderHarness` golden-image tool (byte-exact PNG + hit-test probe dumps). Spine ~544 lines.
  - Chart tool fixes + Fib/Alert/indicator (RSI/Bollinger/VWAP) calculators shipped alongside.
- **★ NOW (2026-07-18): the SERVER structural arcs, run FULLY AUTONOMOUSLY for multiple days on Kiesh's GREEN LIGHT**
  (see memory `feedback_autonomous_restructure_mandate`). **The council decides the arc order** from §6 (and any
  owner-level call); executor on **Opus 4.8**, heavy problems on **Fable 5**; **isolated agents** to keep context low.
- **Auto vs Attended:** run only **Auto** arcs unattended (multi-class cheap wins, CandleService, AccountsCache,
  BracketCoordinator). The CK-critical **AiTradeService / OrderExecutionService / AiBotDecisionService** are
  **Attended** — never soak-gate or flip a bake unattended; STOP and flag them for Kiesh.

---

## 8. Compounding artifacts

The restructure is an engine, not a sequence of one-off splits. Each arc feeds four durable assets:

- **Living codebase map** — the Phase-0 "useful-later" file grows into a standing map (responsibilities +
  dependencies + a dedup backlog that surfaces the *next* arc's target).
- **The convention spec** — this file (§4 A–E + §2 two-arc rule). Every future executor is graded against it.
- **The precedent corpus** — each finished arc (+ MarketEngine + CHART) becomes a few-shot example, so arc
  N+1 is better-primed than arc N.
- **The byte-identical characterization gate** — the FROZEN surface + moves-only diff + full test suite is a
  reusable golden-master gate, not a per-arc promise.
