# Arc: Codebase-wide DEDUP + DE-COMPLICATION (behavior-preserving)

Authorized by Kiesh 2026-07-18: remove duplicate methods (→ helpers), simplify overcomplicated code
WITHOUT changing behavior/goal or losing readability/safety, add helper classes + enums + dataclasses.
**Unlike the prior split arc, this CHANGES code → the moves-only-diff proof is GONE.** Methodology below
is the Fable-5 council verdict (2026-07-18) — the governing safety contract for this arc.

## The two-pass structure

### Pass 1 — AUTONOMOUS (provably-safe subset ONLY)
**Qualifying rule (hard gate):** a change is autonomous-eligible **iff you can state in ONE SENTENCE why
the COMPILER or a TEXTUAL DIFF — not the test suite — guarantees identical behaviour.** Tests are an
imperfect oracle exactly where money bugs live (error paths, concurrency, rounding). Eligible classes:
- **Exact-duplicate extraction:** ≥2 token-identical method bodies → ONE helper; the diff shows only
  call-site substitution and the new helper is byte-for-byte each original.
- **Provably-dead / unreachable code** deletion.
- **Renames.**
- **Pure, stateless helper consolidation** (formatting / parsing / display / ViewModel plumbing / pure
  math with NO I/O, NO locks, NO mutation, NO clock/RNG) where old-vs-new is property-test-equivalent.

### Pass 2 — PROPOSE-ONLY (reviewed diffs for Kiesh, do NOT merge unattended)
Everything requiring judgment: money/decimal math, rounding, anything touching Fund/Position/reservations,
transaction-scoped code, reserve→release ordering, records/enums on persisted models, Order-type
string→enum, and "simplify this complicated code". Deliverable = a diff + a written "why this is
equivalent" argument per item in a proposals doc. Kiesh reviews + merges.

## HARD BANS unattended (queue to Pass 2 / owner)
- Anything inside `RunInTransactionAsync` / savepoint scope (extraction to an awaiting helper can break
  AsyncLocal savepoint nesting).
- Any decimal rounding / `MidpointRounding` (banker's-vs-away drift = fractions of a cent over hours).
- Fund / Position / reservation mutation; **reserve→release ORDERING** (this was the P2 parallel-group
  race `853c7e6` — a reordering bug tests didn't catch).
- **Order-type strings → enum: FORBIDDEN** (CLAUDE.md mandates string constants; an enum touches DB
  serialization + comparisons). A smart-enum *wrapper* keeping DB strings byte-identical is Pass-2 only.
- **`record` on persisted models** (value-equality + `with` change dict/dedup + SQLite materialization).
- Settlement / Matching / OrderExecutionService / the 3 Attended giants — owner-gated (already).
- **Scar tissue:** apparent "overcomplication" is often a defensive gate / ordering constraint from a real
  past incident (P2 race, Q7 interleaving). If git-blame/history explains a guard, do NOT remove it — propose.

## Per-candidate gate (Pass 1)
1. **Characterize-first**: pin current behaviour with 2-3 focused tests (edge inputs, rounding, null paths)
   BEFORE editing — skip only for pure syntactic dedup of identical bodies.
2. Edit ONE candidate.
3. Build BOTH TFMs (client `net9.0-windows10.0.19041.0` + server) + FULL suite (661).
4. **Adversarial diff review by a SEPARATE agent** that never saw the rationale: given only `git diff`, it
   reconstructs old-vs-new semantics and hunts changed eval-order / short-circuit / decimal-vs-double /
   culture-sensitive ToString/Parse / exception type+timing / lock scope / async ordering → answers
   PRESERVED / CHANGED / UNSURE. **UNSURE or CHANGED → revert.**
5. One candidate = one commit (bisectable).

## Batch gate
After every ~5-8 commits: full suite + a **CK smoke**. Plus the **shadow-run differ** — a deterministic
fixed-seed short sim run BEFORE and AFTER the batch, dumping per-stock candle closes + fund/position totals
to CSV; `diff` them. Byte-equal ≈ behaviour preserved; ANY drift flags the batch even when 661 tests stay
green → bisect, revert the culprit, requeue. (Money-adjacent work, if ever done, needs a MULTI-HOUR soak,
not a smoke — and it's Pass-2/owner anyway.)

## Order (safest + highest-value first)
1. Shared/client formatting + string + display helpers; client ViewModel plumbing dedup (non-CK).
2. Client ViewModels / converters (non-CK).
3. Server NON-CK pure math (candle math, decision/sentiment pure math) — consolidate ONLY token-identical.
4. Cross-cutting client↔server duplicates — autonomous ONLY where token-identical; else Pass-2.
5. Groundwork (safe, compounding): characterization tests pinning settlement/matching on fixed seeds;
   the duplicate-inventory docs; pure helpers extracted BESIDE gated code w/ equivalence tests + a one-line
   adoption diff for Kiesh.

## Discovery inputs
`docs/arcs/DEDUP_shared_helpers_INVENTORY.md`, `DEDUP_client_INVENTORY.md`, `DEDUP_server_nonck_INVENTORY.md`
(prioritized candidates tagged PROVABLY-SAFE / NEEDS-CARE / CK-TOUCHING). Pass 1 pulls only PROVABLY-SAFE;
NEEDS-CARE + CK-TOUCHING → Pass 2 proposals.

## Then: the POLISH pass (LAST, after this arc — Kiesh's sequencing)
File renames (MarketViewModels.cs→MarketViewModel.cs etc.), empty-#region cleanup from the split arc,
comment compaction.
