# docs/ — index

Documentation is sorted into folders by kind. Working docs for an in-flight effort live in `arcs/`;
everything else is by type.

| Folder | What's in it |
|---|---|
| **[explainers/](explainers/)** | Curated, human-facing explainers — architecture, engine/bot mechanics, API reference, data layer, client structure, host/ops, slides. Also the portable **`CLAUDE_SETUP_PLAYBOOK.md`** (how to run Claude Code on this repo) and its worked-example companion **`PROJECT_SYSTEMS_AND_PROBES.md`**. Start here to understand the system. |
| **[arcs/](arcs/)** | Living working-docs for the current restructure / dedup arc — handoffs, inventories, per-arc plans, the OrderValidator investigation, the P2-1 click-test checklist. These are the recovery anchors for ongoing work. |
| **[plans/](plans/)** | Design & build plans — `*_PLAN`, `*_DESIGN`, `*_SPEC`, `*_BRIEF`. What we intended to build and how. |
| **[ultraplans/](ultraplans/)** | Ultraplan fire-prompts (`ultraplan-*`) — self-contained implementation briefs handed to an executor (see the playbook's D-2 for the method). |
| **[research/](research/)** | Findings, soak/bake results, investigations, staging reports, tuning notes (`*_FINDINGS`, `*_RESULTS`, `*_REPORT`, the R4/R5 rounds, `bot-*` topic notes, analysis logs). What we learned. |
| **[council/](council/)** | LLM-council decision records (`COUNCIL_DECISION_*`). |
| **[runbooks/](runbooks/)** | Operational procedures & checklists — reseed, ship, prod-test, verification checklists. |
| **[reference/](reference/)** | Stable reference — `FLAG_REGISTER`, `ROADMAP`, `PROJECT_STATUS`, `RESTRUCTURE_ARCS_OVERVIEW`, `DISK_USAGE_NOTES`, prune manifests. |

*Files were relocated with `git mv`, so history is preserved (use `git log --follow <file>`). Some older
inter-doc links may point at pre-reorg paths.*
