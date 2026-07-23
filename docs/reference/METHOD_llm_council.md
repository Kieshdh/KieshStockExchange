# METHOD — LLM Council (5 advisors + peer review + chairman)

**What it is.** A structured decision ritual (after Karpathy's "LLM Council") used to pressure-test any
real decision or design with genuine stakes and tradeoffs. Five AI advisors independently analyze the
question, anonymously peer-review each other's answers, and a chairman synthesizes a final verdict.

**How it works.**
1. **Independent pass** — 5 advisors each answer the question cold, without seeing each other.
2. **Anonymous peer review** — each advisor critiques the others' (de-identified) answers.
3. **Chairman synthesis** — a final verdict folds in the survivors: what to do, what to skip, what to
   change, and the dissent worth keeping.

**When to use.**
- At the **teardown step of [ultradesign](METHOD_ultradesign.md)** — the adversarial filter on an
  architects' draft (concrete changes / SKIP / different approach).
- In **[autonomous research-loop mode](METHOD_ab_soak_and_gates.md)**, convene it **frequently** — not
  only at hard forks (about to bake, a result contradicts the hypothesis, a side-metric regression, ≥3
  live directions) but **regularly after each experiment, to let the council pick the next move** (owner
  pref 2026-07-06). It is pure reasoning ⇒ safe to run CONCURRENT with a soak, so the cost is low.
- Standalone, for any decision with stakes + multiple options the owner wants stress-tested.

**Discipline.** Log every verdict in the run's plan file. An irreversible or genuinely ambiguous pick is
escalated as a plan-file Question for the owner, never auto-executed. Council records worth keeping are
committed under [`../council/`](../council/) as `COUNCIL_DECISION_*`.

**Source of truth:** the `llm-council` skill; cadence rules in
`~/.claude/.../memory/feedback_autonomous_research_loop.md`; decision records in [`../council/`](../council/).
