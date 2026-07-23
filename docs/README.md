# docs/ — index

Documentation is sorted into folders **by what a file actually is** (read for content, not titled by name).
Working docs for an in-flight effort live in `arcs/`; everything else is by type.

| Folder | Charter — what's in it |
|---|---|
| **[reference/](reference/)** ★ | **Settings & Configuration Reference + reusable Methods/Ideas.** The stable, look-it-up layer: every config KEY / appsettings block / prod env override / tuning knob / target scorecard, plus the project's standing ways-of-working (ultradesign, council, the autonomous timers, the soak/gate discipline). First-class — see the contents list below. |
| **[explainers/](explainers/)** | Curated, human-facing explainers — architecture, engine mechanics, data layer, API reference, client structure, the systems/probes lab notebook, the Claude setup playbook, slides. Start here to understand the system. |
| **[status/](status/)** | Forward-looking project trackers — `PROJECT_STATUS`, `ROADMAP`, the `RESTRUCTURE_ARCS_OVERVIEW` hub. "Where the project is and what's next." |
| **[arcs/](arcs/)** | Living working-docs for the current restructure / dedup / chart arc — handoffs, inventories, per-arc plans, triage verdicts, click-test checklists. The recovery anchors for ongoing work. |
| **[plans/](plans/)** | Design & build plans — `*_PLAN` / `*_DESIGN` / `*_SPEC` / `*_BRIEF` and the `bot-*` design specs. What we intended to build and how. Includes the prune proposals/manifests. |
| **[ultraplans/](ultraplans/)** | Ultraplan fire-prompts (`ultraplan-*`, `*_ULTRAPLAN`) — self-contained implementation briefs handed to an executor (see `reference/METHOD_ultradesign.md`). |
| **[ultradesigns/](ultradesigns/)** | Ultradesign fire-prompt(s) — the design-then-build brief variant. *(Single file today; candidate to fold into `ultraplans/`.)* |
| **[research/](research/)** | Findings, soak/bake results, investigations, staging/verification reports, tuning notes (`*_FINDINGS` / `*_RESULTS` / `*_REPORT`, the R4/R5 rounds, analysis-log `.txt`/`.png`). What we learned. |
| **[council/](council/)** | LLM-council decision records (`COUNCIL_DECISION_*`). See `reference/METHOD_llm_council.md`. |
| **[runbooks/](runbooks/)** | Operational procedures & checklists — reseed, ship, prod-test, verification, the disk-usage troubleshooting notes. |

---

## ★ reference/ — the Settings & Configuration Reference bucket (contents)

This bucket is deliberately first-class: it is the project's settings + how-we-work memory. Membership test:
**"does it document config keys / target settings / how to tune / how we work?"**

**Settings & configuration**
| File | What it is |
|---|---|
| [MARKET_BALANCING_CONFIG.md](reference/MARKET_BALANCING_CONFIG.md) | The master `Bots:*` config-constant lookup — every lever by function, value, and BAKED/OFF state. |
| [BOT_MECHANICS.md](reference/BOT_MECHANICS.md) | The bot-systems reference: mechanisms + the `Bots:*` config-key index + **§1 the market-realism target scorecard**. |
| [FINE_TUNING_TARGETS.md](reference/FINE_TUNING_TARGETS.md) | The named fine-tuning scorecard / soak gate-set (regression bounds + owner's headline targets). |
| [FLAG_REGISTER.md](reference/FLAG_REGISTER.md) | The `Bots:*` flag lifecycle register — each flag's WINNER/INTERIM/LOSER/PROBE state + kill/keep condition. |
| [SERVER_HOST_AND_OPS.md](reference/SERVER_HOST_AND_OPS.md) | Deploy-host + config-layering reference: appsettings→env `Section__Key` overrides, `Db:*`/`Auth:*`/`Retention:*` blocks, Docker/compose env, deploy/rollback. |

**Reusable methods / ideas (how we work)**
| File | What it is |
|---|---|
| [METHOD_ultradesign.md](reference/METHOD_ultradesign.md) | The multi-agent design pipeline: feasibility probe → 3 architects → council teardown → fire prompt. |
| [METHOD_llm_council.md](reference/METHOD_llm_council.md) | The 5-advisor + anonymous peer-review + chairman decision ritual and its cadence. |
| [METHOD_autonomous_run_timers.md](reference/METHOD_autonomous_run_timers.md) | The two cron chains: the 5h2m token-continuity chain + the 2–5 min context-freshness ("fresh-session-is-context-wipe") chain. |
| [METHOD_ab_soak_and_gates.md](reference/METHOD_ab_soak_and_gates.md) | The A/B soak protocol, the CK=0 hard gate, default-off/env-override reversibility, candle-CSV pipeline, disk-frugal build gating. |

*Files were relocated with `git mv`, so history is preserved (use `git log --follow <file>`). Some older
inter-doc links may still point at pre-reorg paths.*
