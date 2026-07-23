# METHOD — Autonomous-run timers (the two cron chains)

This project runs multi-day **autonomous** work (restructure, dedup, realism/perf tuning) across Claude's
usage-window resets and across context limits. Two **distinct, coexisting** self-perpetuating cron chains
make that possible. They are session-only (`CronCreate` jobs live in memory, not durable), so **each firing
re-arms the next one** — and if the session fully exits, the chain dies and must be **re-armed by hand**
(on any fresh start: `CronList`; if the chain is gone, re-create it).

## 1. The 5h2m TOKEN-CONTINUITY chain
**Purpose:** survive **usage-window / token-exhaustion resets** so work resumes after the window reopens.
**Mechanism:** a one-shot cron. Each firing FIRST re-arms the next timer at **`+5 hours 2 minutes`**
(`date -d '+5 hours 2 minutes'` → `CronCreate` one-shot, same prompt), THEN does a slice of work. The 2-minute
offset clears the reset boundary. A recurring cron can't express a 5h2m interval, and the window is ~5h — so a
chained one-shot is the faithful mechanism. This chain is NOT for context management.

## 2. The 2–5 min CONTEXT-FRESHNESS chain ("clear-memory timer" / fresh-session-is-context-wipe)
**Purpose:** keep the orchestrator's context LOW on a long, many-candidate arc. **A fresh session IS a
context wipe** — nothing in the working context carries over — so everything needed to resume must live in
**auto-memory + `docs/arcs/`** (the recovery anchors). **Mechanism:** at a **CLEAN STOPPING POINT**
(everything committed + pushed, tree clean, NO half-done candidate), do ONE candidate / small batch, then
`CronCreate` a one-shot at **`+2` (or `+5`) minutes** with the same continue-prompt and STOP producing. The
next candidate then runs in a **fresh, low-context session** that rehydrates from memory + the arc handoff.

**Hard rule:** only arm this at a clean stopping point. Arming mid-candidate loses the in-flight work on the
restart (the wipe is total). The interval is owner-configurable (5 min and 2 min have both been used).

## Coexistence & safety
- The two chains run at the same time and are independent — when re-arming one, leave the other alone.
- **Never** flip a prod default, run a reseed, or do a prod cutover unattended — these are always owner-gated,
  regardless of what the timer prompt says.
- If only Attended / CK-critical / eyeball-gated work remains, STOP re-arming and report rather than inventing
  risky work for the timer to do.

**Source of truth:** `~/.claude/.../memory/project_autonomous_resume_timer.md` (the two-chain clarification +
re-arm SHAs) and `feedback_autonomous_research_loop.md` (the research-loop that these timers carry).
