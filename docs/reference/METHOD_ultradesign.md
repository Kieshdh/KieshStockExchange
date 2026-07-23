# METHOD — Ultradesign (how a big change is designed before it's built)

**What it is.** The standing multi-agent pipeline this repo uses to design any large or architectural
change before a line is written. It turns a rough idea into a committed, self-contained implementation
brief ("fire prompt") that an executor (local Claude or a remote `/ultraplan` session) implements blindly.

**The pipeline (kept in FULL on every ultraplan — not tiered by size):**
1. **Feasibility probe FIRST (always).** Grep the 3–5 seams the change hinges on — exact method
   signatures, DI registrations, lock/transaction boundaries, config keys — and write a one-paragraph
   "these seams exist / this one doesn't." Kills or reshapes a plan before an architect+council cycle is
   spent on a false premise (the "assume it works" framing is the #1 source of deep rework).
2. **Three architects draft.** Each independently designs the change as best it can; then converge on the
   best implementation.
3. **Council teardown.** The 5-advisor [llm-council](METHOD_llm_council.md) tears the draft apart:
   concrete changes / SKIP entirely / different approach.
4. **Fire prompt.** Fold the survivors into a committed `docs/plans/*_PLAN.md` (or ultraplan brief) and
   hand over the exact `/ultraplan <plan>` line.

**The committed plan must carry (not just prose intent):**
- a **"Repo Facts" appendix** — verbatim signatures / config keys / helper + enum-string names /
  `file:line` anchors quoted from the repo, so the executor transcribes instead of inventing names (the
  #1 compile-gap cure);
- **acceptance criteria + rejected alternatives frozen**, so a re-fire doesn't re-litigate settled ground;
- for **HIGH-RISK changes only** (CK-conservation, transaction scope, concurrency): red failing tests +
  interface signatures written *into* the doc (contract/test-first) so the executor implements to green.

**Fire Contract footer (every fire prompt).** Exact branch + tip SHA; a no-SDK clause (executor can't
build/test → list compile uncertainties with `file:line`, don't guess-fix); byte-identical-off requirement
+ the exact default-OFF flag key; `file:line` anchors for every insertion; the CK-conservation invariant +
acceptance metric; a scope fence (allowed files; never `/Tools`).

**Continuation vs new.** The full pipeline is for a NEW initiative. The next phase of an initiative an
existing ultraplan session already owns → prefer CONTINUING that session (it carries prior phases,
gates, decisions) over re-running a fresh architects→council pass.

**When to use.** Genuinely large/architectural work, or when the owner invokes `/ultraplan`. Small/medium
changes are solved directly without the ceremony.

**Source of truth:** `~/.claude/.../memory/feedback_ultraplan_workflow.md`; examples of the produced briefs
live in [`../ultraplans/`](../ultraplans/) and the plan docs in [`../plans/`](../plans/).
