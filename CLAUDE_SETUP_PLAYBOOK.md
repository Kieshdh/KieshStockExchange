# Claude Setup Playbook — reusable methods for any project (PORTABLE)

Distilled from a long, mostly-autonomous refactor/research project (KieshStockExchange). These are the
techniques that carried the most weight and **generalize to any codebase or research project**. Copy the ones
that fit; skip the rest. Each entry: **what it is → when to use it → how to set it up.**

> **This is the PORTABLE note** — everything here drops straight into any project. Its companion,
> **`docs/PROJECT_SYSTEMS_AND_PROBES.md`**, covers the problem-finding + tooling systems that were specific to
> *this* project (a market simulator): the money-conservation probe, the soak harness, the golden-image gate,
> reseed tooling, etc. — useful as *inspiration* for similar domains, not as drop-ins.

> **Problem-finding systems (generic, in this note):** the three that repeatedly found *real* defects here are
> the **LLM Council** (§1), the **adversarial diff review** (§3), and **characterization tests** (§4). If you
> adopt nothing else, adopt those three.

Two kinds of thing live here:
- **Skills** — droppable folders under `.claude/skills/<name>/` (a `SKILL.md` + assets). Claude auto-loads the
  one-line description and reads the full file when relevant, or you invoke it with `/<name>`.
- **Methods/conventions** — ways of working you bake into `CLAUDE.md`, a handoff doc, or a reusable prompt.

---

## 1. The LLM Council — for expensive decisions
**What:** one question → 5 independent advisors with *different thinking lenses* (Contrarian, First-Principles,
Expansionist, Outsider, Executor) → anonymized peer review → a chairman synthesis (agreements / clashes /
blind-spots / recommendation / one-thing-first). It's Karpathy's LLM Council done with sub-agents.
**When:** any decision where being wrong is expensive and there's genuine uncertainty — architecture direction,
"should I refactor X or leave it," prioritizing a backlog, pricing, positioning. **Not** for questions with one
right answer.
**Why it works:** the peer-review round (anonymized) is the magic — reviewers catch each advisor's blind spot
without deferring to a "senior" voice, and the chairman is allowed to side with a lone dissenter.
**Setup:** put the skill at `.claude/skills/llm-council/` (we have it). Invoke with "council this: <decision>"
or `/llm-council`. Runs ~11 sub-agents (5 advisors + 5 reviewers + chairman) — reasoning-only, no builds, cheap
on disk. Real example from this project: it took a "scary" money-math refactor and, via a blind-spot the peer
review surfaced, reframed it into a zero-risk read-only diagnostic — which then proved the risky code was
*dead code*. That reframe alone paid for the whole council.

## 2. The Safe-Autonomous-Change Pipeline — behavior-preserving edits without a human in the loop
**What:** a fixed per-change loop that let an agent ship 20+ code changes unattended with zero regressions:
> **isolated executor** (implements ONE candidate, doesn't build/commit) → **gate** (build + FULL test suite)
> → **separate adversarial diff review** (a *different* agent sees ONLY the diff, no rationale) → **your own
> read** → **1 commit per candidate** (bisectable) → push.
**When:** dedup, mechanical refactors, dead-code removal, framework migrations — anything meant to preserve
behavior. Also research: one experiment per loop.
**How to set up:** encode the loop as a reusable prompt (or a skill). Key rules that made it safe:
- **One candidate = one commit** (so `git bisect` works and a bad change is trivially reverted).
- The executor is a *fresh agent* each time → the orchestrator's context stays small (see §11).
- Gate on the **full** test suite every time, not a subset.

## 3. Adversarial diff review — the quality gate that actually catches things
**What:** after a change builds+passes, a **separate** agent is handed *only the git diff* (no author rationale)
and told to **default to skepticism**: reconstruct old-vs-new semantics and hunt for changed evaluation order,
short-circuits, decimal-vs-double, culture-sensitive parsing, exception type/timing, lock scope, async ordering
→ verdict **PRESERVED / CHANGED / UNSURE**. UNSURE or CHANGED ⇒ revert.
**When:** every behavior-preserving change; especially where tests are an imperfect oracle (error paths,
concurrency, rounding — where real bugs live).
**Why:** "it compiles and tests pass" misses semantic drift. A skeptical reader with no stake, seeing only the
diff, catches what the author (human or AI) rationalized away. For a near-duplicate generalization, make the
review **per-site**, not per-helper.

## 4. Characterization tests before touching risky code
**What:** before refactoring money/critical logic, add tests that **pin the CURRENT behavior** (not the ideal
behavior) across all input classes. If a test surfaces something surprising, **document it, don't "fix" it.**
**When:** any refactor near correctness-critical code you can't fully reason about; also as a safety net you
hand the owner before *they* do the risky change.
**Payoff seen here:** writing characterization tests around two "boring" money helpers surfaced **5 real latent
defects** (a reservation asymmetry + 4 validator divergences) that no one was looking for — the test-writing
doubled as a defect-discovery pass.

## 5. Risk-tiering: "autonomous only if the compiler or a textual diff proves it"
**What:** a one-sentence gate for what an agent may do unattended vs must escalate.
> **Autonomous-eligible iff you can state in ONE sentence why the COMPILER or a TEXTUAL DIFF — not the test
> suite — guarantees identical behavior.** Everything else → **propose-only** (a diff + a written "why this is
> equivalent / what's the risk" for the owner to review and merge).
**Plus a HARD-BANS list** the agent never touches unattended (for us: money/conservation code, DB
transaction scope, anything owner-flagged "attended"). And a **prepare-but-hold** move: implement + fully
validate a risky change, but label the commit "HOLD FOR OWNER" and never treat it as final.
**When:** any long autonomous run. This single rule is what kept "helpful autonomy" from becoming "unsupervised
money bug." Tests are a *weak* oracle exactly where it matters most — so the bar is the compiler/diff, not green
tests.

## 6. The living HANDOFF doc — the recovery anchor
**What:** one `HANDOFF.md` that is *always* the current source of truth: what's DONE (with commit hashes), the
EXACT next candidate, the safety rules, and any open decisions. Updated at every clean stopping point, before
stopping.
**When:** any multi-session or multi-day effort. It's what lets a fresh, low-context session (or a different
person) pick up seamlessly, and what lets you trust an autonomous chain not to lose the plot.
**Rule:** a "clean stopping point" = everything committed+pushed, tree clean, handoff updated. Only stop/hand
off there.

## 7. Two self-perpetuating cron chains — for long unattended runs
**What:** Claude Code can schedule prompts to itself (session-only crons). Two distinct chains, different jobs:
- **Context-freshness chain** (short interval, e.g. 2–5 min): do ONE unit of work at a clean stopping point,
  then re-arm a short timer with a detailed continue-prompt and STOP → the next unit runs with **low context**.
  Keeps quality high on long runs by never letting one session's context balloon.
- **Token-continuity backstop** (long interval, e.g. ~5 h): survives usage-window/token resets so work resumes
  after the window reopens. Built as a **watchdog**: on firing it re-arms itself, reads the handoff, and only
  acts if there's genuinely safe work — it won't invent risky changes.
**Honest caveats:** these crons are **session-only** (in-memory) — if the session fully exits, the chain dies and
must be re-armed by hand; and they fire *within* the same live session when it's idle, so "fresh context" only
truly resets across an actual session restart. The continue-prompt must be self-contained (point at the handoff,
re-state the safety rules) so any firing is stateless.
**When:** overnight/multi-day autonomous work. Pair with §6 (handoff) so each firing is recoverable.

## 8. The disk gate — for machines that hit 100% disk during builds
**What:** on Windows, "100% disk" is usually **I/O activity** (Task-Manager active-time), not space. Repeated
`dotnet`/build churn on top of an IDE + antivirus + Docker saturates it. Fix = throttle *your* builds and remove
the wasteful I/O:
- **Pre-flight** the disk before each build (`\PhysicalDisk(_Total)\% Disk Time`); if ≥ a limit (we used 70%),
  wait until it drops, then build. → your work never stacks onto a spike.
- Run builds at **Idle CPU priority** + **single-threaded** (`-maxcpucount:1`) so they yield the disk.
- **Never `dotnet clean`** (forces a full rebuild); gate via the narrowest build that proves the change (e.g.
  `dotnet test` alone for server/shared; only build the client project for client-only changes).
- Owner-side levers (huge): **Defender exclusions** for the repo + package cache, quieting the IDE's background
  analysis, and quitting Docker when idle.
**When:** any dev box where builds peg the disk. We captured the full diagnosis + a ready `disk-gated-build.ps1`
reference in a `DISK_USAGE_NOTES.md` — worth copying that pattern.

## 9. Memory discipline — file-based, one fact per file
**What:** Claude Code's persistent memory works best as one fact per file with typed frontmatter
(`user` / `feedback` / `project` / `reference`) + a one-line pointer in a `MEMORY.md` index. `feedback` entries
(how you want Claude to work, with the *why*) are the highest-leverage — they make corrections stick across
sessions.
**When:** always. The discipline that mattered: **don't** store what the repo/git already records; **do** store
non-obvious preferences, project goals not derivable from code, and confirmed working approaches. Update the
existing file instead of duplicating; delete memories that turn out wrong.

## 10. `CLAUDE.md` + `CLAUDE.local.md` — project constitution
**What:** `CLAUDE.md` (checked in) states architecture rules, layering, naming/folder conventions, key flows,
out-of-scope areas, and a "response format." `CLAUDE.local.md` (git-ignored) holds personal working preferences
("teach me, don't just dump code"; "minimal targeted edits over big refactors"; review priorities).
**When:** every repo. This is the single biggest quality lever for a coding agent — it front-loads the context
that otherwise gets re-explained every session. Keep it *frozen and specific* (see §12).

## 11. Isolated executor agents — keep the orchestrator's context small
**What:** the orchestrating session delegates each heavy implement/investigate task to a **fresh sub-agent**,
then only **gates, reviews, and commits** the result. The sub-agent's huge tool-output (file reads, edits) never
enters the orchestrator's context — only its short final report does.
**When:** any long session that would otherwise accumulate hundreds of file reads. This is what makes a
multi-hour autonomous run stay coherent instead of drowning in its own scrollback.

## 12. Small but real gotchas worth stealing
- **Parse build/test logs, not exit codes.** PowerShell `Start-Process -PassThru` can return a **null**
  `ExitCode` even on success → detect results by grepping the log (`0 Error(s)`, `Passed! - Failed: 0 …`).
- **Prompt-cache hygiene:** keep `CLAUDE.md`/system prompts *frozen* (no timestamps/UUIDs) so the cached prefix
  stays valid; inject volatile context later, not into the frozen prefix.
- **Deterministic-verification only where the system is deterministic.** We designed a "shadow-run differ"
  (fixed-seed run, diff before/after CSVs) — then discovered the engine was wall-clock-paced + parallel, so it
  couldn't reproduce *itself*. Lesson: verify determinism *before* relying on a golden/diff harness; where the
  system isn't reproducible, fall back to compiler/textual proof + characterization tests.
- **Model routing:** cheap/fast tier for mechanical executors, the most capable tier for the council and the
  hardest reviews. Re-check availability (model windows change) and fall back gracefully.

## 13. The "ultraplan" pattern — de-risk a big change before writing it
**What:** for a large or high-risk change, don't jump to code. Run a planning pipeline:
**feasibility probe** (is this even possible / where does it touch?) → **3 independent architects** propose
approaches → **council teardown** (§1) scores them and finds the holes → distill into a single **fire prompt**
with a *Repo-Facts appendix* (the exact files/APIs/constraints, so the implementer doesn't re-discover them) and
a *contract/acceptance footer* (what "done + correct" means). Then implement in **one blind patch** for
low-risk, or **stage** the high-risk/statistical parts behind a flag; validate fail-fast locally, then a long
soak/verification for anything correctness-critical.
**When:** anything too big to hold in one head — a subsystem rewrite, a migration, a new feature that crosses
layers. The upfront cost pays for itself by catching the fatal approach before you've written 2,000 lines of it.
**Setup:** it's a methodology, not a skill — but you can template the "fire prompt" (Repo-Facts + Contract
footer) and reuse it. We have many worked examples as `docs/ultraplan-prompt-*.md`.

---

## Minimal starter setup for a new repo
1. `CLAUDE.md` (architecture rules + key flows + out-of-scope) and `CLAUDE.local.md` (your working prefs). §10
2. Copy the **llm-council** skill into `.claude/skills/llm-council/`. §1
3. Adopt the **risk-tier rule** (§5) + **adversarial diff review** (§3) as your standing rule for any
   behavior-preserving change — even without full automation, they catch real bugs.
4. Start a `HANDOFF.md` the moment a task spans more than one session. §6
5. If builds hammer your disk, add the **disk gate** + a `DISK_USAGE_NOTES.md`. §8
6. Only reach for the **cron chains** (§7) + **isolated-executor pipeline** (§2/§11) when you actually want
   long unattended runs — they're powerful but need the handoff + risk-tier discipline underneath them.

*The through-line: give the agent (a) a frozen constitution, (b) a one-sentence rule for what it may do alone,
(c) an adversarial second set of eyes on every change, and (d) a durable handoff — then automation is safe.
Everything else here is an amplifier on those four.*
