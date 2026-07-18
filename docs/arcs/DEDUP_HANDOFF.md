# DEDUP ARC — LIVING HANDOFF (read FIRST; update at every clean stopping point)

**Purpose:** a detailed, current handoff so a fresh LOW-CONTEXT session continues the dedup arc seamlessly.
**Rule:** at your clean stopping point, UPDATE this doc (what you just shipped + the exact next candidate),
commit+push, THEN arm the next +5-min context-freshness timer with the continue-prompt, then STOP producing.

## State (as of commit `3b8dfcd`, 2026-07-18 ~23:10)
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
- REFUSED (correctly, do NOT retry as a merge): the 5 signed-percent formatters are genuinely different
  (decimal vs double, F2/0.00/N2/%-specifier, culture, sign-at-zero) — unifying would change numbers.

## NEXT UP (in order)
1. **Server-non-CK math** (RNG-order-sensitive → BUILD THE SHADOW-RUN DIFFER FIRST): FundamentalService
   `.Gaussian`→`BotMath.NextGaussian` (preserve exact 2-draw order/count), `RecordFills`×3 → shared helper,
   `MinDtSec/MaxDtSec` literals → BotMath const.
2. NEEDS-CARE server math (careful per-site + shadow-run differ): cohort filter-sort, dt-clamp, arrival-prob.

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
