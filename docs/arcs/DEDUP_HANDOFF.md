# DEDUP ARC — LIVING HANDOFF (read FIRST; update at every clean stopping point)

**Purpose:** a detailed, current handoff so a fresh LOW-CONTEXT session continues the dedup arc seamlessly.
**Rule:** at your clean stopping point, UPDATE this doc (what you just shipped + the exact next candidate),
commit+push, THEN arm the next +5-min context-freshness timer with the continue-prompt, then STOP producing.

## State (as of commit `d7ac996`, 2026-07-19)
## ⚠ DISK GATE ACTIVE (Kiesh 2026-07-19): his PC pegs disk I/O during builds. ALL builds MUST go through the
## dynamic disk gate — pre-flight `% Disk Time`, WAIT if ≥ **70%** (5-min cap → else pause+report), then run at
## **Idle CPU priority + `-maxcpucount:1`**. Gate via `dotnet test` ALONE for server/shared; scoped client build
## only for client-only changes; NEVER `dotnet clean`; low cadence. See `docs/DISK_USAGE_NOTES.md`.

- Branch `feature/bot-market-realism-v2` = **FEATURE BRANCH — never merge/deploy to master/prod unattended**
  (prod runs `master` on the Netcup VPS; my pushes are branch backups only). Tree clean, all pushed.
- Governing plan: `docs/arcs/DEDUP_ARC_PLAN.md` (two-pass structure, qualifying rule, HARD BANS, gate).
- Candidate inventories: `docs/arcs/DEDUP_{client,server_nonck,shared_helpers}_INVENTORY.md`.

## The PROVEN pipeline (follow exactly, per candidate)
1. **Executor** (isolated agent): implement ONE candidate (or one tight batch). Verify precondition
   BYTE-FOR-BYTE; route only exact-match sites; leave any variant alone + report it. Don't build/commit.
2. **Gate**: build (client `dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`
   and/or server csproj) + FULL suite (`dotnet test KieshStockExchange.Tests/KieshStockExchange.Tests.csproj`
   → must be **661/661**). Diff-scope + field/using sanity.
3. **Adversarial diff review** by a SEPARATE agent (given ONLY the diff, no rationale) → PRESERVED /
   CHANGED / UNSURE; **per-site** for near-dups; UNSURE or CHANGED → revert the candidate.
4. **Commit** (1 per candidate) + **push**.
5. Server/math candidates: also run the **shadow-run differ** per batch (deterministic-seed short sim →
   CSV of candle closes + fund/position totals, before vs after; any drift → bisect+revert).

## DONE (Pass 1, shipped + verified PRESERVED)
- `f9a009b` PagerMath (byte-identical `ComputeVisiblePages` extract).
- `a1878bf` ParsingHelper `class`→`static class` (compiler-proven).
- `d6e9635` GetListAsync<T> — 29 list-GET call sites generalized.
- `483dd5e` SymbolOrDash extension — 8 sites.
- `3b8dfcd` **RunBusyAsync base-VM helper** — 9 byte-identical `RefreshAsync` busy-guards routed through a new
  `protected async Task RunBusyAsync(Func<Task> work, Action<Exception> onError)` on `BaseViewModel`.
  DESIGN NOTE: base VM has NO logger, so the catch/log is passed as an `onError` delegate (each VM keeps its
  own logger + exact message → logs stay byte-identical). 31 variant sites LEFT untouched (ConfigureAwait
  bodies, no-guard, rethrow, DisplayAlert-in-catch, CTS/CancellationToken, return values, code after finally).
  Fable-5 executor + Fable-5 adversarial review (PRESERVED x10) + own read + 661/661.
- `9c7d05c`/`f0f8c97`/`60ba106` **Server-non-CK math** (3 commits): (A) `FundamentalService.Gaussian` → one-line
  forwarder to `BotMath.NextGaussian(_rng)` (byte-identical Box-Muller, same u1/u2 draw order → RNG stream
  unchanged); (B) 3 token-identical `RecordFills` → `DecisionFillRecorder` static helper (pure telemetry,
  Conviction keeps a static forwarder since its calls live in `.TradeBook.cs`); (C) 8 helpers' `MinDtSec=0.05
  /MaxDtSec=60.0` → `BotMath.TickMin/MaxDtSec` const-from-const (same IL; MarketMood's 0.05/10.0 excluded).
  Fable-5 executor + Fable-5 per-candidate adversarial review (PRESERVED x3) + own read + 661/661.
- `41b600e`/`8e595c6`/`b486f80` **3 textual-identity dedups** (2026-07-19): (A) `Order.cs` `IsKnownStatus`
  predicate extract (Status setter + `IsValidStatus` shared the 5-value check; NOT an enum conversion);
  (B) `ApiDataBaseService.Users.cs` `GetUsersByIds` → existing `PostListAsync` helper (operation-identical);
  (C) new `KieshStockExchange/Helpers/DateRangeHelper.cs` — byte-identical date+time combine/clamp block from
  `Order/TransactionTableViewModel` → one pure static. Fable-5 executor + adversarial PRESERVED ×3 + own read +
  client+server build clean + 661/661.
- `edde94b`/`c507acf` **client #28 + #22** (2026-07-19, first run under the DISK GATE): (#28) Account row
  `Currency` getters `CurrencyType.ToString()` → `CurrencyHelper.GetIsoCode` (GetIsoCode IS `ToString()` →
  byte-identical); (#22) per-VM `private const string DefaultSortKey` in the 6 admin table VMs (const-from-literal,
  same value → identical IL; Position's separate in-VM-sort base literal left alone). Opus-4.8 executor +
  adversarial PRESERVED ×2 + own read + client build clean + 661/661. Disk stayed 1% (Idle + `-maxcpucount:1`).
- `d7ac996` **client #10** (2026-07-19, disk-gated): `PageLifecycle.SafeLoad(string tag, Func<Task> load)`
  extracted; ALL 5 OnAppearing sites routed (Market/Portfolio/Admin/Trade/Login) — a Pass-1b near-dup
  generalization, each differing only in tag+awaited-call, log format `$"{tag}: {ex}"` byte-identical to every
  original. PER-SITE adversarial PRESERVED ×5 + helper + own read + client build clean + 661/661. FOLLOW-UP
  (Pass-2/next): `LoginPage.OnRegisterClicked` is a byte-identical SafeLoad match but is an event handler (out
  of #10's OnAppearing scope) — would route cleanly (tag "LoginPage.OnRegisterClicked nav failed", load
  `() => Shell.Current.GoToAsync("RegisterPage")`).
- REFUSED (correctly, do NOT retry as a merge): the 5 signed-percent formatters are genuinely different
  (decimal vs double, F2/0.00/N2/%-specifier, culture, sign-at-zero) — unifying would change numbers.

## ★ FINDING (2026-07-18): the SHADOW-RUN DIFFER IS INFEASIBLE — do NOT try to build it.
The sim engine is NOT byte-reproducible run-to-run even with no code change: the tick loop is WALL-CLOCK paced
(`dt = (now-last).TotalSeconds`, real elapsed time via `TimeHelper.NowUtc`, no fixed-step/accelerated server
mode) AND settlement runs bots in PARALLEL groups with JITTERED-backoff retry (`OrderExecutionService`
`Task.WhenAll` + `RetryBackoffMs` jitter). Baseline ≠ baseline, so a before/after CSV differ can't be an
oracle. CONSEQUENCE: the arc's remaining autonomous runway = **TEXTUAL/COMPILER identities ONLY** (Pass-1
qualifying rule). Any server-math change whose equivalence needs RUNTIME observation (true near-dup
generalization: cohort filter-sort, arrival-prob, non-identical math) is NOT autonomously verifiable →
route it to **Pass 2 propose-only**, do not ship unattended. (Unit-level determinism tests exist —
`*DeterminismTests` drive helper `Tick/Step` with injected fixed dt+seed — usable to LOCK a specific
extraction if ever needed, but that's a per-case test, not a whole-sim differ.)

## ★ PASS-1 AUTONOMOUS TEXTUAL CANDIDATES = EXHAUSTED (2026-07-19). AWAITING KIESH REVIEW of Pass-2.
The cleanly-autonomous textual/compiler-identity dedups are all shipped (see DONE list). **#15 assessed →
LEAVE AS-IS** (CommunityToolkit.Maui IS referenced, but its typed converter throws on null/non-bool where the
local one defensively returns true/false → not a behaviour-preserving drop-in; low value, real risk — details
in the Pass-2 doc). Everything remaining needs OWNER judgment (behaviour change / XAML-eyeball / money-CK / a
real bug) → captured in **`docs/arcs/DEDUP_PASS2_PROPOSALS.md`** (propose-only, NOT merged):
- P2-1 `CloseRequested` handler-leak BUG (9 popups; genuine fix; needs popup base + per-VM idempotent Dispose + XAML-eyeball)
- P2-2 InvertedBoolConverter (recommend leave), P2-3 LoginPage.OnRegisterClicked SafeLoad (tiny optional)
- P2-4 structural client bases (PortfolioTableVMBase, DateRangeTableVM<T>, ModalFormVM, ...), P2-5 money/CK
  (ReservationMath drift — CK soak, OrderValidator overlap, cost-basis lot math), P2-6 int.TryParse→ParsingHelper.

**No more autonomous CODE candidates remain without Kiesh's decisions.** A resumed session should either (a) keep
EXPANDING the Pass-2 doc with more diff-sketches (doc-only, disk-free) if Kiesh wants, or (b) if Kiesh has picked
Pass-2 items to execute, run them via the proven pipeline (many are XAML-eyeball / CK → confirm scope first).
The Attended giants (AiTradeService / OES / AiBotDecision) + BracketCoordinator remain owner-gated as always.

## ★ COUNCIL RAN (2026-07-19) — Pass-2 triage decided; Kiesh green-lit executing GO-NOW-within-safety. Full verdict
in `docs/arcs/DEDUP_PASS2_PROPOSALS.md` (top). **GO-NOW queue (do in this order, agents on Opus 4.8):**
1. ✅ **DONE — P2-5 READ-ONLY DIAGNOSTIC** → `docs/arcs/RESERVATIONMATH_DRIFT.md`. FINDING: the **client
   `ReservationMath` is DEAD CODE** (zero callers in the client project; `internal` methods, never invoked). The
   drift has NO user impact (client never reserves via it); server is authoritative + the only one used. This
   reclassifies P2-5's client side from "CK unification (owner+soak)" to a **safe dead-code DELETE (Pass-1)**.
1b. ✅ **DONE (`ff657bd`) — DELETED the dead client `ReservationMath.cs`.** Client build clean + 661/661 →
   compiler-proven no caller. NOT a CK change (server copy untouched). The client side of the drift is resolved
   safely; the feared unification is off the table.
2. **CK CHARACTERIZATION TESTS** (server/shared → test project; gate `dotnet test` alone, disk-gated, all green + count rising):
   - ✅ (a) SERVER `ReservationMath` — `43c9e05`, 18 tests, 679/679. Pinned the `ProjectedBuyReservation` asymmetry
     (StopMarketBuy → 0 there vs BuyBudget in Initial; documented, not fixed).
   - ✅ (b) `OrderValidator` — `93d2ed2`, 50 tests, 729/729. **★ FOR KIESH: pinned 4 real `ValidateInput` vs
     `ValidateNew` DIVERGENCES** (Input accepts LimitBuy+BuyBudget / TrueMarket-SELL+budget / checks currency /
     can hit slippage-range msg — New differs on each). Documented, NOT fixed. These are the OrderValidator P2-2/P2-5
     "rule-drift" concern, now characterized — owner decides whether/how to reconcile ValidateInput to ValidateNew.
   - ⏭ (c) cost-basis lot math — **SKIPPED (documented).** `KieshStockExchange.Tests.csproj` references only
     Shared+Server and EXPLICITLY forbids referencing the MAUI client ("drags in the workload", csproj:25). So client
     `ChartMath.AverageCostBasis`/`PositionPnl` has **no unit coverage by construction** — to test it, the pure lot-walk
     would need moving to `Shared` (a Pass-2 refactor; noted in DEDUP_PASS2_PROPOSALS.md P2-5). No code change.
2. **CK CHARACTERIZATION TESTS (server/shared → in the test project):** add tests pinning CURRENT behaviour of
   `ReservationMath` (server), `OrderValidator` rule blocks, and cost-basis lot math. Tests ADD coverage, change no
   app behaviour; gate = `dotnet test` alone (disk-gated). One test-area per commit. This de-risks the owner's fix.
3. ✅ **DONE (`2b2d212`) — P2-3** (LoginPage.OnRegisterClicked → `PageLifecycle.SafeLoad`; true identity; 729/729).

## ★★ COUNCIL GO-NOW QUEUE = COMPLETE (2026-07-19 ~04:45). Arc PAUSED for Kiesh's direction.
Shipped autonomously: P2-5 diagnostic (client copy was dead code) → deleted dead client ReservationMath (`ff657bd`)
→ ReservationMath char-tests (`43c9e05`) → OrderValidator char-tests (`93d2ed2`, +4 ValidateInput/ValidateNew
DIVERGENCES found) → cost-basis SKIPPED (client not in test project) → P2-3 (`2b2d212`). Suite 729/729, disk stayed ≤2%.
**REMAINING = the PREPARE-FOR-OWNER items — NOT started autonomously (they're behavior-changing / XAML-eyeball /
no-client-test — the council said HOLD, and Kiesh said he'd advise today/tomorrow).** A resumed session should NOT
build these unattended without Kiesh confirming scope/approach: P2-1 popup CloseRequested base (10 popups, XAML
x:Class root + Dispose idempotency), P2-4 structural client bases (depends on P2-1), P2-6 int.TryParse→invariant
(widens accepted input). DROP P2-2. NEVER autonomous: ReservationMath unification/hoist + any CK merge (owner+soak).
Also open for owner: the 4 OrderValidator divergences (reconcile ValidateInput→ValidateNew?) + the StopMarketBuy
ProjectedBuyReservation asymmetry. **The 2-min chain was NOT re-armed (paused for owner); 5h2m backstop `fea77b65` stays armed.**

## ★★★ BOTH KIESH'S-CALLS DELIVERABLES DONE (2026-07-19) — ARC PAUSED, AWAITING KIESH.
- **P2-1 popup CloseRequested leak = PREPARED + HELD** (`3700d78`, label "PREPARE — HOLD FOR KIESH click-test").
  Design = Kiesh's preferred low-risk EXTENSION `Helpers/PopupLifecycle.cs` (`WireCloseAndDispose` + `IClosablePopupViewModel`),
  NOT a base class → zero XAML `x:Class` changes. All 12 popups rewired (11 leaking + ConvertCurrency reference rerouted
  onto the shared path); 11 VMs got an idempotent `Dispose()` (guard-first, nulls own `CloseRequested`/`Saved`/`NavigateTo*`).
  Gate: CLIENT build clean (0 err) + isolated adversarial diff review = **SAFE-FIX** (close behavior byte-for-byte preserved,
  disposal idempotent + can't throw in `Closed` + no use-after-dispose). **NOT merged** — click-test checklist for Kiesh at
  `docs/arcs/P2-1_POPUP_LEAK_CLICKTEST.md` (per-popup: open → interact → close-every-way → reopen; ConvertCurrency = regression check).
- **OrderValidator divergences = INVESTIGATED** (`5697419`, read-only, no code change) → `docs/arcs/ORDERVALIDATOR_DIVERGENCES.md`.
  Finding: **BENIGN belt-and-suspenders** — all 4 `ValidateInput` call sites are downstream-gated by `ValidateNew` on the same
  request; no path is `ValidateInput`-gated alone; `CreateOrder` strips `BuyBudget` off non-market-buys. Per-divergence:
  #1/#2 LEAVE (unreachable inputs), #3 currency = NEEDS-OWNER-CALL (`ValidateInput` is the *stricter/authoritative* side — do
  NOT relax; port into `ValidateNew` if parity wanted), #4 slippage LEAVE (`ValidateInput`'s check prevents a >100 → HTTP 500).
  Safest default = LEAVE all four. One non-code confirmation flagged (can model binding deliver `(CurrencyType)999`?).
- **FOR KIESH:** (1) click-test P2-1 per the checklist → then it can be treated as merged (or say to revert); (2) decide the
  OrderValidator currency divergence (#3) — the only one with an owner call. **2-min chain NOT re-armed (paused for owner);
  5h2m backstop `28a9e27f` stays armed.** PLAYBOOK-V2 also done earlier this run (`d3a3dcc`).

## ★ KIESH'S CALLS (2026-07-19, via AskUserQuestion) — DEDUP ARC, implement LATER (AFTER the PLAYBOOK-V2 task):
- **P2-1 (popup CloseRequested leak) = do FIRST.** PREPARE-BUT-HOLD (Kiesh click-tests before final). PREFER the LOW-RISK
  design: a `Popup` **extension** `WireCloseAndDispose(popup, vm)` (or tiny helper) reproducing the ConvertCurrencyPage
  pattern (subscribe `CloseRequested` → MainThread close; wire `Closed` → unsubscribe + `vm.Dispose()`) — AVOIDS changing each
  popup's XAML `x:Class` root type (the biggest eyeball risk) vs. a base class. Needs a common interface
  (`IClosablePopupViewModel : IDisposable { event EventHandler CloseRequested; }` — the VMs already have the members) OR a
  delegate form. VERIFY each VM's `Dispose` is idempotent. Adopt across the ~10 leaking popups; leave/route ConvertCurrencyPage.
  Gate = CLIENT build (disk-gated) + adversarial review; commit labelled **"PREPARE — HOLD FOR KIESH click-test"** + a
  per-popup click-test checklist doc. Do NOT treat as merged.
- **OrderValidator divergences = INVESTIGATE-first (read-only, NO code change).** Per divergence, determine whether every path
  that calls `ValidateInput` ALSO calls `ValidateNew` later (if so the looseness is harmless belt-and-suspenders; if not, Input
  lets bad orders through), which side is authoritative, what reconciling would newly reject → write
  `docs/arcs/ORDERVALIDATOR_DIVERGENCES.md` with a per-divergence recommendation. NO reconcile until Kiesh reviews (CK-adjacent).
- **PRIORITY ORDER:** the **PLAYBOOK-V2 task** (`docs/arcs/PLAYBOOK_V2_TASK.md`) comes FIRST (Kiesh 2026-07-19), THEN this dedup work.
**PREPARE-FOR-OWNER (implement+validate on branch, but DO NOT rely on it being merged — flag for Kiesh):** P2-1 popup
base (+ per-popup click-test checklist), P2-4 structural bases (depends on P2-1), P2-6 int-parse (document widened input set).
**DROP:** P2-2. **Still NEVER autonomous:** the actual ReservationMath UNIFICATION / any Fund/Position/reservation/rounding/
transaction merge (CK=0 sacred — needs owner + soak). **MODEL ROUTING: Fable-5
   access window closed 2026-07-18 → default executor + adversarial-review agents to Opus 4.8 (`model` omitted or
   "sonnet"/opus); only try Fable if you have positive evidence access is back.**
2. When the clean textual candidates are exhausted, START `docs/arcs/DEDUP_PASS2_PROPOSALS.md` (propose-only,
   do NOT merge) — begin with the `CloseRequested` handler-leak BUG (real fix) + the NEEDS-CARE server math
   now blocked by the infeasible differ + the rest of the Pass-2 list below.

## MODEL ROUTING (Kiesh steer 2026-07-18): route the heavy executor + adversarial-review agents onto
**Fable 5** (`fable`) while Kiesh's access holds — no model-level retirement (capacity/subscription window
only); resume on Opus 4.8 when access lapses. RunBusyAsync (`3b8dfcd`) was done this way.

## PASS-2 — PROPOSE-ONLY doc for Kiesh (build `docs/arcs/DEDUP_PASS2_PROPOSALS.md`; do NOT merge)
ReservationMath client/server drift (CK), OrderValidator overlap, lot-math sharing, `int.TryParse`→
`ParsingHelper` (~15 sites), popup base class + **the real BUG: `CloseRequested` handler leak in 9 popups**
(only ConvertCurrencyPage disposes — a genuine fix), HttpApiClient base, all "simplify complicated" judgment calls.

## HARD BANS unattended (→ Pass 2 / owner)
transaction-scope (`RunInTransactionAsync`), decimal rounding/MidpointRounding, Fund/Position/reservation
mutation, reserve→release ordering, Order-type→enum (CLAUDE.md), records on persisted models, scar-tissue
guards, the 3 Attended giants + Settlement/Matching/OES. **CK=0 is sacred.**

## Timers (leave the other one alone)
- `50889109`@02:27 — 5h2m TOKEN-window continuity chain (usage reset). Don't disturb.
- 5-min CONTEXT-FRESHNESS chain — arm the next one ONLY at a clean stopping point (see Rule at top).
