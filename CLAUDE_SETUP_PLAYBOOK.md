# Claude Code Setup Playbook (PORTABLE)

A field-tested setup for working with **Claude Code** (Anthropic's agentic CLI) on a real codebase — the
methods that let it make many changes safely, including unattended, without breaking things. Distilled from one
long project but written to drop into any repo. Its companion, **`docs/PROJECT_SYSTEMS_AND_PROBES.md`**, is a
*worked example* (a market simulator) — read it for inspiration, not as a drop-in.

> **The one idea everything rests on:** *find the invariant that must never break in your system, build a cheap
> check that proves it, and only let the agent act unattended where a compiler, a textual diff, or that check can
> prove it didn't break anything.* Trust flows from a provable guardrail, not from the agent being careful.

### TL;DR (adopt in this order)
1. Write a **`CLAUDE.md`** (project rules) — biggest single quality lever, in our experience.
2. Add a **build + full-test gate** and make an independent agent **review the diff** before every change lands.
3. Only let it run **unattended** where the *compiler or a textual diff* proves the change is behavior-identical;
   everything else is **propose-only** for a human.
4. For long runs, add a **living `HANDOFF.md`** + the **cron chains**; if builds peg your disk, add the **disk gate**.

### ⏱ Adopt in 30 minutes — do these 4, skip the rest
Copy the four templates in **§Templates** below: **`CLAUDE.md`**, **`HANDOFF.md`**, the **executor→gate→review→commit
pipeline** (as a habit or a prompt), and the **adversarial-review prompt**. Those give ~80% of the value; add the
rest as you scale into automation.

### Preconditions (if these aren't true, most of this is a trap)
- **A build + a test suite exist** and run from the CLI. The autonomy rule below leans on them.
- **A human stays in the loop for judgment.** These methods make an agent *safe to delegate to*, not *safe to
  ignore*. Correctness-critical decisions still get a person.
- **You've named explicit trust boundaries** — what the agent may NEVER touch or merge without you (see §Safety).
- Claude Code specifics used here: **sub-agents** (the agent spawns isolated helper agents — a built-in Task
  tool), **session-only crons** (it can schedule prompts to itself), and **persistent memory**. These are Claude
  Code features, not homegrown scaffolding. A paid plan and the CLI are assumed.

### Mini-glossary (terms used below)
- **candidate** — one self-contained change (= one commit).
- **gate** — the automated check a change must pass. We use it three ways, don't conflate them: the **build/test
  gate** (§T1-2), the **disk gate** (§L3, throttles builds), and a **hardened gate** for risky areas (extra
  asserts). Context says which.
- **propose-only** — the agent implements + validates a change but does **not** merge it; a human reviews the diff.
- **prepare-but-hold** — same, but the change is committed on a branch labelled "HOLD FOR OWNER" so it's ready to
  review, never treated as final.
- **soak** — a long-running end-to-end run of the real system used to catch bugs that only appear over time /
  under concurrency (project-specific; see the example note).
- **invariant / probe** — a property that must always hold (money conserved, row-counts balance) and a cheap
  continuous check that it does.

---

## Tier 0 — every project (no automation required)

### T0-1. `CLAUDE.md` + `CLAUDE.local.md` — the project constitution
`CLAUDE.md` (checked in) front-loads the context you'd otherwise re-explain every session: architecture rules,
layering, naming/folder conventions, key flows, out-of-scope areas, and a preferred response format.
`CLAUDE.local.md` (git-ignored) holds *your* working preferences. Keep it **frozen and specific** — no
timestamps/UUIDs (that also protects prompt-cache reuse). This was the highest-leverage single file for us.
→ template in §Templates.

### T0-2. Memory discipline — one fact per file
Claude Code's persistent memory works best as **one fact per file** with typed frontmatter
(`user` / `feedback` / `project` / `reference`) + a one-line pointer in a `MEMORY.md` index. **`feedback`
entries** (how you want it to work, *with the why*) are the highest-leverage — they make corrections stick across
sessions. Don't store what git already records; update the existing file instead of duplicating; delete memories
that turn out wrong.

### T0-3. The living `HANDOFF.md` — recovery anchor
One doc that is *always* the current source of truth: what's DONE (with commit hashes), the EXACT next step, the
safety rules, and open decisions. Update it at every **clean stopping point** (everything committed+pushed, tree
clean) *before* stopping. It's what lets a fresh low-context session — or another person — pick up seamlessly.
→ template in §Templates.

## Tier 1 — letting the agent change code

### T1-1. Characterization tests before touching risky code
Before refactoring correctness-critical logic, add tests that **pin the CURRENT behavior** (not the ideal
behavior) across all input classes. If a test surfaces something surprising, **document it, don't "fix" it.**
Writing these tests doubles as a defect-discovery pass — around two "boring" money helpers it surfaced several
latent defects nobody was hunting.

### T1-2. The executor → gate → review → commit pipeline
The core loop that let an agent make many code changes with **no regression caught by our gates** (which is not
the same as "provably zero" — undetected regressions remain possible; the pipeline is only as strong as your
gate + invariant):
> **isolated executor** (implements ONE candidate, doesn't build/commit) → **gate** (build + FULL test suite) →
> **independent adversarial diff review** (a *different* agent, sees only the diff) → **your own read** → **1
> commit per candidate** (bisectable) → push.
Rules that made it safe: one candidate = one commit; a **fresh** executor each time (keeps the orchestrator's
context small — see §L4); gate on the **full** suite, not a subset. → review prompt in §Templates.

### T1-3. Adversarial diff review — the check that actually catches things
"It compiles and tests pass" misses semantic drift, and tests are weakest exactly where real bugs live (error
paths, concurrency, rounding). So a **separate** agent gets *only the git diff*, no author rationale, and is told
to **default to skepticism** and hunt changed evaluation order / short-circuits / decimal-vs-double /
culture-sensitive parsing / exception type+timing / lock scope / async ordering → **PRESERVED / CHANGED /
UNSURE**; anything but PRESERVED reverts. For a near-duplicate generalization, review **per call-site**. → prompt
in §Templates.

## Tier 2 — unattended / long runs

### T2-1. The autonomy rule (the load-bearing safety line)
> **Autonomous-eligible iff you can state in ONE sentence why the COMPILER or a TEXTUAL DIFF — not the test
> suite — guarantees identical behavior.** Everything else is **propose-only**.
Plus a **HARD-BANS** list the agent never touches unattended, and **prepare-but-hold** for risky-but-valuable
changes. This one rule is what keeps "helpful autonomy" from becoming "unsupervised production bug." → risk-tier
table in §Templates.

### T2-2. Isolated executor agents — keep the orchestrator small
Delegate each heavy implement/investigate task to a **fresh sub-agent**; the orchestrator only **gates, reviews,
commits**. The sub-agent's huge tool output never enters the orchestrator's context — only its short final
report does. This is what keeps a multi-hour run coherent instead of drowning in scrollback. (Ask the sub-agent
for a **structured final report** — its last message *is* the data you act on.)

### T2-3. The two cron chains — for long unattended runs
Claude Code can schedule prompts to itself. Two chains, different jobs:
- **Context-freshness chain** (short, e.g. 2–5 min): do ONE unit at a clean stopping point, re-arm a short timer
  with a self-contained continue-prompt, then STOP → each unit runs with low context.
- **Token-continuity backstop** (long, e.g. ~5 h — *tune to your plan's usage window*): survives usage/token
  resets; built as a **watchdog** that re-arms itself and only acts on genuinely safe work.
> ⚠ **These crons are session-only (in-memory).** If the session fully exits, the chain **dies silently** and
> must be re-armed by hand, and a run interrupted mid-change can leave work half-committed. They are **not**
> set-and-forget. Always pair with `HANDOFF.md` and make every continue-prompt self-contained (re-state the
> safety rules, point at the handoff) so any single firing is recoverable. → commands in §Templates.

## Decision tools (orthogonal — reach for these at forks, not as steps)

### D-1. The LLM Council — for expensive, uncertain decisions
One question → 5 independent advisors with *different thinking lenses* (Contrarian, First-Principles,
Expansionist, Outsider, Executor) → anonymized peer review → a chairman synthesis (agreements / clashes /
blind-spots / recommendation). The anonymized peer-review round is the magic — reviewers catch each advisor's
blind spot without deferring to a "senior" voice. **Use it** for architecture direction, "refactor X or leave it,"
prioritizing a backlog; **not** for questions with one right answer. Install as a skill at
`.claude/skills/llm-council/`; invoke with "council this: …". (Reasoning-only sub-agents — no builds.) *Observed
once here:* it reframed a scary money-refactor into a read-only diagnostic that then proved the code was dead —
that reframe alone paid for the council. (This very playbook was revised by running the council on it.)

### D-2. The "ultraplan" pattern — de-risk a big change before writing it
For a large/high-risk change, plan first: **feasibility probe** → **3 independent architects** propose approaches
→ **council teardown** scores them → distil into one **fire prompt** (a self-contained implementation brief) with
a *repo-facts appendix* (exact files/APIs/constraints) and an *acceptance contract* (what "done + correct"
means). Then one blind patch for low-risk, or stage the risky parts behind a default-off flag. It's a
methodology, not a skill — template the fire prompt and reuse it.

---

## Safety & secrets (read before you automate)

- **The pipeline is only as safe as your gate + invariant.** Without an equivalent to a strong invariant check
  (T1/§example), do **NOT** let an agent autonomously commit/push **correctness-critical or money-adjacent code.**
  Make that an explicit HARD-BAN, not a soft "risk-tier."
- **Secrets:** agents read files. Ensure `.env`/keys are git-ignored and out of any context you paste; never let a
  secret land in a commit message, a `HANDOFF.md`, or a memory file. Scope push credentials narrowly. Treat
  "commit + push" as a privileged action.
- **Branch discipline:** unattended work goes to a feature branch, never `main`/`master`/prod. Deploys stay
  human.
- **Crons fail silently** (above) — never rely on one for anything time-critical.
- **Autonomy ≠ unsupervised.** Review what landed. The methods lower the odds of a bad change *reaching* you; they
  don't remove your judgment from the loop.

## Other patterns worth stealing
- **Model routing:** cheap/fast tier for mechanical executors, the most capable tier for the council and the
  hardest reviews. Re-check model availability (it changes) and fall back gracefully.
- **`AskUserQuestion` for owner-level forks:** when a choice is genuinely the human's (scope, risk appetite,
  which of two valid designs), ask with explicit options instead of guessing or stalling.
- **Prepare-but-hold** (glossary): implement + fully validate a risky change, commit it labelled "HOLD FOR
  OWNER," never treat it as merged. Gives the human a ready-to-review diff without you making the call.
- **Worktree isolation for parallel edits:** when several agents mutate files at once (e.g. A/B experiments),
  give each its own git worktree so they don't collide.
- **Find-your-invariant (the thesis):** the single most reusable move — see the example note for a worked case.

---

## Templates (copy-paste)

### `CLAUDE.md` skeleton
```markdown
# <Project>
## Overview
<one paragraph: what it is, primary platform/stack, "prefer extending current architecture over replacing">
## Build & run
```<the exact build + run commands>```
## Architecture rules
- Keep <UI> concerns in <Views/VMs>; business logic in <Services>; data access in <the data layer>.
- <key invariant, e.g. "all multi-table writes go through RunInTransactionAsync">.
## Repo structure expectations
- <naming/folder conventions>
## Out-of-scope (do not touch unless asked)
- <generated code, vendored tools, etc.>
## Response format for this repo
1. State the issue. 2. Why it happens. 3. Which layer the fix belongs in. 4. The code. 5. Explain the key parts.
```

### `CLAUDE.local.md` skeleton (git-ignored)
```markdown
# Local preferences
- Teach clearly, not just code dumps. Minimal targeted edits over big rewrites.
- Review priority order: correctness → <layer separation> → <framework> → async/threading → maintainability.
- Do not touch <X> unless explicitly asked.
```

### `HANDOFF.md` template
```markdown
# <TASK> — LIVING HANDOFF (read FIRST; update at every clean stopping point)
## State (as of commit <hash>, <date>)
- Branch <branch> — NEVER merge/deploy to main/prod. Tree clean, all pushed.
- Governing plan: <link>. Safety rules / HARD-BANS: <link or inline>.
## The proven pipeline (per candidate)
1. executor implements ONE candidate  2. gate: build + FULL suite (must be <N/N>)
3. adversarial diff review (separate agent, diff only) → PRESERVED/CHANGED/UNSURE  4. own read  5. 1 commit → push
## DONE (with hashes)
- <hash> <what> — <verification>
## NEXT UP (exact next candidate)
1. <precise next step + the gate it needs>
## HARD BANS unattended
<the untouchable set>
## Timers (if any)  <cron ids + what each does>
```

### Adversarial diff review prompt (the crown jewel — feed to a *separate* agent)
```
You are an adversarial reviewer on a behavior-PRESERVING change. You are given ONLY a git diff — no rationale.
Default to CHANGED/UNSURE unless you can PROVE the change preserves runtime behavior exactly.
Read this diff: <path-or-paste>. For each hunk (and each call-site, for a near-duplicate generalization) check:
evaluation/short-circuit order, decimal-vs-double, culture-sensitive ToString/Parse, exception type+timing,
lock scope, async/await ordering, null/empty handling, and any moved field/initializer.
Output a per-hunk verdict PRESERVED / CHANGED / UNSURE with a one-line reason, then an overall verdict
(PRESERVED only if EVERY hunk is). For anything CHANGED/UNSURE, state exactly what evidence would flip it.
```

### Risk-tier table
| Tier | Rule (may the agent do it unattended?) | Example | Action |
|---|---|---|---|
| **Auto** | The **compiler or a textual diff** proves identical behavior, in one sentence | rename; extract byte-identical method; delete provably-dead code; hoist a value-identical const | executor → gate → adversarial review → commit |
| **Needs-care** | Behavior-preserving but only the **test suite** would catch a slip | near-duplicate generalization; template-method base class | same pipeline **+ per-site** review; if any doubt → propose-only |
| **Propose-only** | Changes behavior, or touches correctness-critical/UI-untested/serialization code | validator reconcile; parser widening; a shared base across many screens | implement + validate, commit "HOLD FOR OWNER", **human merges** |
| **Hard-ban** | Money/data-invariant, transactions, or owner-flagged "attended" | settlement, reservations, rounding, the giant CK services | **never unattended** — owner + soak |

### Disk-gated build (Windows/PowerShell — for machines that peg the disk on builds)
"100% disk" is usually **I/O activity** (Task-Manager active-time), not space. Pre-flight, then build at Idle
priority + single-threaded so it yields the disk. Detect results by **parsing the log**, not the exit code
(`Start-Process -PassThru` can return a null `ExitCode` on success).
```powershell
function DiskAvg { ((Get-Counter '\PhysicalDisk(_Total)\% Disk Time' -SampleInterval 1 -MaxSamples 3).CounterSamples |
  Measure-Object CookedValue -Average).Average }
$limit = 70; $w = 0
while ((DiskAvg) -ge $limit -and $w -lt 300) { Start-Sleep 5; $w += 8 }   # wait out a spike (<=5 min)
if ([math]::Round((DiskAvg)) -ge $limit) { "disk still busy — skip"; exit 3 }
$log = "$env:TEMP\build.log"
$p = Start-Process dotnet -ArgumentList @('build','<proj>','-maxcpucount:1','-v','m') `
       -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
$p.PriorityClass = 'Idle'; $p.WaitForExit()
if ((Select-String $log -Pattern '^\s*0 Error\(s\)')) { "BUILD OK" } else { "BUILD FAILED"; Get-Content "$log.err" -Tail 10 }
```
Owner-side levers (bigger than anything above): **Defender exclusions** for the repo + package cache; quiet the
IDE's background analysis; quit Docker when idle. Also: **never `dotnet clean`** (forces a full rebuild); gate via
the narrowest build that proves the change.

### Cron chains (session-only — re-arm on every fresh session; they die on exit)
```
# context-freshness: do one unit, then at the clean stopping point re-arm the next and STOP
CronCreate  cron="*/3 * * * *" (or a one-shot +N min)  recurring:false  prompt="<self-contained continue-prompt: read HANDOFF.md, do next candidate via the pipeline, re-arm +N, then STOP>"
# token-continuity backstop: self-perpetuating watchdog (tune interval to your usage window)
CronCreate  one-shot at +5h  recurring:false  prompt="(1) re-arm FIRST at +5h with THIS prompt (2) read HANDOFF.md (3) if paused-for-owner: confirm + STOP; else continue one safe candidate"
```

---

## Appendix — small gotchas worth stealing
- **Parse build/test logs, not exit codes** (see the disk-gated script) — `Start-Process -PassThru` `.ExitCode`
  can be null on success.
- **Prompt-cache hygiene:** keep `CLAUDE.md` / system prompts frozen (no timestamps/UUIDs); inject volatile
  context later, not into the cached prefix.
- **Verify determinism BEFORE relying on a golden/diff harness.** A "run it twice, diff the output" check only
  works if the system is reproducible; a wall-clock-paced or parallel system can't reproduce *itself* (we learned
  this the hard way — see the example note). Where it isn't reproducible, fall back to compiler/textual proof +
  characterization tests.

*Through-line: give the agent (a) a frozen constitution, (b) a one-sentence rule for what it may do alone, (c) an
adversarial second set of eyes on every change, and (d) a durable handoff — then, and only then, automation is
safe. Everything else here is an amplifier on those four.*
