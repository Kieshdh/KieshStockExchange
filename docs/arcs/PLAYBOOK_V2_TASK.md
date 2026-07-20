# PLAYBOOK-V2 task — add the missing (generalized) techniques, council-improved

> **★ DONE (2026-07-19).** Full 5-advisor + 5-peer-review LLM council run; chairman synthesis applied to
> `docs/explainers/CLAUDE_SETUP_PLAYBOOK.md`. Council's key call: this is NOT a "Tier 3" rung — it's a coordinate **second
> track** ("When you can't prove it") selected by the system's *nature* (stochastic/subjective output), gated by a
> self-select sentence + honesty caveat. Applied: (1) reframed the "one idea" to *invariant OR scalar proxy*;
> (2) new second-track section with the 3-move spine (scalar metric / continuous invariant / concurrent A/B) +
> generalized perf + tiered-run guidance; (3) fleshed D-2 ultraplan (5-step pipeline + Fire-Prompt template +
> ultracode-vs-local routing table, model-routing folded in); (4) merged redundancies (#5/#6/#7 → one
> "decouple merge from activation" pattern; tiered durations shrunk); (5) added Fire-Prompt + Research-plan/STATUS
> templates; (6) fixed two dangling cross-refs. 382→~382 lines (under the ~410 cap). The candidate list below is
> the input that was fed to the council; kept for provenance. **NEXT = PRIORITY 2 (dedup arc).**


**Kiesh (2026-07-19):** "We're missing a lot in the playbook. We used a lot of techniques to make sure the bot
runs well — add them, but a GENERALIZED version. E.g. I'm missing the ultraplan method (3 architects & council &
handoff, for ultracode / local-Claude implementation). Let the council improve this."

**Goal:** grow `docs/explainers/CLAUDE_SETUP_PLAYBOOK.md` to cover the *tuning / research / performance* methods we used on the
simulator, **generalized** (portable to any project), without bloating it or breaking its structure (maturity
ladder Tier 0/1/2 + orthogonal decision-tools + copy-paste templates + scoped, honest claims). The project-specific
instances stay in `docs/explainers/PROJECT_SYSTEMS_AND_PROBES.md`; only the *pattern* goes in the playbook.

## How to run this (fresh session)
1. Read `docs/explainers/CLAUDE_SETUP_PLAYBOOK.md` (current v2) + `docs/explainers/PROJECT_SYSTEMS_AND_PROBES.md` + this file.
2. **Run the LLM council** (`/llm-council`, 5 advisors + synthesis; reasoning-only, disk-safe — still disk-pre-flight before spawning) framed as:
   *"Here is the current playbook + this candidate list of tuning/research/perf techniques to ADD, generalized.
   Which to add, how to generalize each cleanly, where they fit the ladder (probably a new 'Tier 3 — tuning/
   researching a stochastic or hard-to-verify system' block + expanding the Ultraplan decision-tool), what to CUT
   to avoid bloat, and how to keep claims honest. Also: is the ultraplan section detailed enough?"*
3. Apply the verdict (edit the playbook; keep ladder + template + honesty style; scope claims). Commit + push.
4. THEN proceed to the dedup arc per `DEDUP_HANDOFF.md` (P2-1 first, then the OrderValidator investigation).

## Candidate techniques to add (GENERALIZED — the council refines/prunes)
1. **Ultraplan — FLESH OUT (Kiesh's explicit ask).** Currently one paragraph; expand it: **feasibility probe →
   3 INDEPENDENT ARCHITECTS** (each proposes a different approach, blind to each other) **→ council teardown**
   (score the 3, find the fatal flaw) **→ one "fire prompt"** (self-contained implementation brief = repo-facts
   appendix [exact files/APIs/constraints] + acceptance contract [what "done+correct" means] + a fire-contract
   footer) **→ handoff.** Cover **the two implementation paths Kiesh named:** run the fire prompt via
   **ultracode** (Claude's big multi-agent Workflow/orchestration mode — many parallel sub-agents, for scale/
   comprehensiveness/adversarial verification) **vs. local/normal Claude** (one agent, sequential) — and WHEN to
   pick which (ultracode for wide fan-out / high-assurance; local for a single focused change). Note it's a
   methodology + templateable prompt, not a skill.
2. **Council-driven autonomous research/tuning loop.** For long unattended experiment/parameter-tuning runs: a
   **plan-file hub** (open Questions / an experiment queue / an experiment log; a STATUS line = the recovery
   anchor) + **invoke the council at forks AND after each experiment** (to pick the next move) + a **liveness
   watchdog** + a **global time/token cap**. Generalized: "for open-ended research, make the plan file the brain
   and let the council choose the next experiment."
3. **A/B parallel-arms testing for stochastic / non-deterministic systems.** (Generalize File 2 §A4.) You can't
   diff a before-run vs an after-run when the system isn't reproducible — run **both arms concurrently** (two
   instances), compare them, point the live view at the experimental arm to eyeball. Cap parallelism.
4. **A scalar metric for a subjective quality target.** (Generalize File 2 §A8.) Build the number early ("does it
   feel real?" → a composite score); you can't tune what you can't measure; it's what makes dozens of experiments
   comparable.
5. **Staged rollout behind default-OFF flags.** (Generalize File 2 §B3.) Decouple "merge the code" from "enable
   the behavior": ship dark → validate by long soak → flip deliberately. Makes merging even risky code safe.
6. **Bake-validate before flipping a default.** Validate a change over a long run (the invariant stays clean +
   the effect stays bounded) BEFORE it becomes the default. Pair with #5.
7. **Performance methodology.** Profile to find the ACTUAL bound (I/O / commit-bound vs CPU) before optimizing;
   tune config before touching the engine; batch the hot path; ship-first-flag-later; measure via parallel A/B
   (#3). "Optimize the bound you measured, not the one you assumed."
8. **Tiered verification durations.** smoke / mid / bake (short / medium / long) — don't default to the longest;
   match the run length to the question.
9. **Expand model routing** (already a bullet) into a short table: cheap tier for mechanical executors, mid for
   most work, top tier for council + hardest reviews; re-check availability; fall back gracefully.

## Notes
- Keep the playbook HONEST + un-bloated (the last council flagged overclaiming + template-shipping — hold that bar).
- Consider whether these want a new **"Tier 3 — tuning / researching a system whose output you can't directly
  verify"** section, with #2–#8 under it, and #1 (ultraplan) expanded under the existing Decision-tools block.
- Optional bigger moves the last council floated (owner may want later): package the whole playbook as its own
  `claude-code-setup` skill + a bootstrap; publish an Artifact web version for sharing.
