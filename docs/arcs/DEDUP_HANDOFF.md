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
1. **P2-5 READ-ONLY DIAGNOSTIC (disk-free, do FIRST):** diff the client vs server `ReservationMath` (both at
   `.../Services/MarketEngineServices/Settlement/ReservationMath.cs`); write a bug-report-with-repro into
   `DEDUP_PASS2_PROPOSALS.md` (or a new `docs/arcs/RESERVATIONMATH_DRIFT.md`): every diverging method, which side is
   authoritative (server settles → server is truth), what the client mis-estimates, and the user-facing impact
   (likely a client display/pre-validation discrepancy, NOT a conservation breach). NO code change. Commit+push.
2. **CK CHARACTERIZATION TESTS (server/shared → in the test project):** add tests pinning CURRENT behaviour of
   `ReservationMath` (server), `OrderValidator` rule blocks, and cost-basis lot math. Tests ADD coverage, change no
   app behaviour; gate = `dotnet test` alone (disk-gated). One test-area per commit. This de-risks the owner's fix.
3. **P2-3** (LoginPage.OnRegisterClicked → `PageLifecycle.SafeLoad`; true identity; client build via disk gate).
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
