# Project Status — KieshStockExchange (consolidated, 2026-06-23 audit)

Single source of truth for "what's done vs what's left," compiled from a full sweep of `docs/` + the plan files.
**Realism is parked ("mostly fine for now").** The big near-term item is the **bounce-mid prod deploy** (deploy-ready).

---

## ✅ DONE + SHIPPED + TESTED (no action)
- **Advanced orders (P1–P6 + decomposition)** — shorts, stop-loss (market/limit), Side/Entry/Stop decomposition,
  stop manageability (chart line + modify), long brackets (+TP-only), trailing-stop (market), short brackets, and
  bot shorts/stops/brackets (P6a/b/c). **Merged to master `f7c5d6f` (2026-06-08), soak-validated, CK=CONS=ERR=0.**
- **Order/chart client UI** — PlaceOrder/Modify revamp (3-tab type, TP stepper, SL toggle, currency format,
  per-leg bracket edit, dormant-leg visibility), chart stop-lines, fill markers, viewport persistence, table filters,
  session-lifecycle logging. Shipped + manual-tested (29/29 unit, P3/P4 checklist).
- **Arbitrage bots + pure-profit FX house** — merged master `4eea513`, **deployed to prod** (2026-06-09), conservation clean.
- **Market-balancing core** — merged master `f7c5d6f`, **deployed to prod**; median |drift| ~5%, 0 escapes beyond ±100%;
  session-start down-dive fixed+deployed (`c5b8a16`).
- **Batch-H resting shorts** — implemented + self-tested (29/29 unit, 38/38 smoke, cold-load survival), CK clean.
- **Perf/scaling (this round)** — realism foundation + System-A (`f70070c`), maintenance offload (`ec3cf81`, prod, cap
  3.8k→13.7k), BatchArms (`adc2f63`), staggering Slots=2 (`2ea9e78`), injection 30m, Gate-0 fsync metric, #141 EF
  migration fix, #142 HTTP-cancel fix, order-engine smoke 37/37. All shipped + validated.
- **Bug sweep 1 & 2** — async-void crash handlers, hub lifecycle/replay, IDOR, paging, notional overflow, exception
  nets, docker healthcheck, silent host-stop restart loop — all fixed.
- **Realism arc (headline)** — bounce-mid baked (`124853d`, CLOSE ret_acf −0.43→−0.17), round-wall fix (`e4f5465`),
  sensitivity-tuning + down-drift fix baked. (See `REALISM_RETACF_CLOSEOUT.md`.)

---

## ⏳ PENDING YOUR ACTION (gated on you — not autonomously doable)
1. **Bounce-mid → PROD DEPLOY.** Branch `feature/bot-market-realism-v2` is deploy-ready: off-prod pre-flight GREEN
   (clean FF to master, migration `AddTransactionMidPrice` committed, config correct, 274/274 tests). Council 5/5 = go.
   Runbook: pg_dump → merge → migrate (verify MidPrice col) → nuke+reseed (`BounceReference=mid`, desync off) →
   **hard gate: CK=0 on live prod post-reseed** → 2h scorer confirm; rollback = redeploy master + restore dump.
   ⚠️ Re-validate ON PROD (branch numbers don't transfer through a reseed).
2. **`synchronous_commit=off` → PROD** — 1 command (`ALTER SYSTEM SET synchronous_commit='off'; SELECT pg_reload_conf();`),
   already approved; the biggest unrealized perf win.
3. **EUR seed-bot rebalancing → `/Tools` task** — `GenerateAIUsers.py` currency/watchlist rebalance + reseed (fixes thin
   EUR books cheaply vs structural sharding).
4. **UI tests #131–140 (WAVE10) — full A–J clean run** (NOT just F–J). Testing was paused mid-stream after 3 defects
   were found in the A–E range and FIXED (`26bbd17`/`359e3f4`/`8c33fac`); the doc's resume plan is a clean re-run from
   section A, and all of #131–140 are still `pending`. Item 44 = one full clean pass, zero debug noise. **← your manual
   desktop pass.** (A–E should come up clean now that the 3 fixes are in the build.)

---

## 🧪 VALIDATED-SAFE, DEFAULT-OFF (bake-on-prod or leave — your call, low stakes)
- **BracketBatch + arb-leg entry batching** — conservation-clean + determinism-validated; flat perf locally (win scales
  with adv/tick); bake on prod or defer.
- **Group-commit Slice-1** — safe but coalescing not firing (savepoint-nesting root-caused); pivot to Slice-2 if pursued.
- **Per-currency gate** — safe, no perf win; leave off (real EUR fix = seed rebalance, item above).
- **Multiplier→excel cleanup** (the long-standing Q3) — fold `DecisionDistanceMult=0.2` + `MarketProbMult=1.5` into the
  Tools seed; behaviorally-neutral source-of-truth hygiene; recipe ready (`ultraplan-prompt-multiplier-to-excel.md`).
  Attended (population-replacing regen).

---

## 🐛 OPEN BUGS (low priority, deferred)
- **D4** — deeper connection/transaction-leak audit (`PgDBService` `await using` scope). Low.
- **E4** — review remaining DTO binds (raw-entity binds already admin-only). Low.
- **B1** — add 401 handler + re-login (ties to Phase-6 refresh tokens). Feature, not a defect.

---

## 🗄️ DEFERRED FEATURE WORK (design-locked / future rounds — not blocking)
- **Advanced-order variants** — long→short flip (single split-fill order), limit-sell-to-open-short (currently
  rejected), trailing-stop LIMIT. All design-locked + conservation-critical; each wants its own pass. Market shorts
  + trailing-market already shipped.
- **Bot bracket-flip eligibility** — designed + ultraplan handoff ready, NOT shipped. ⚠️ NOTE: R4 later *disabled* bot
  brackets in `Tools` (`8fb220a`, gap 37.7→9.6pp) as the resolution to bracket-drift — so this is likely **shelved**,
  not active. Confirm before pursuing.
- **Per-currency / multi-engine sharding** — the next structural perf lever (within-tick software levers spent).
  Recommend: deploy sc=off + staggering first, measure, pursue only if prod still misses volume (prod already hit 13.7k).
- **Market-balancing §4.1–4.3** — stop-promotion circuit-breaker, tiered limit orders, smart prune. Designed-not-built;
  would close the last 1–2 rare stock escapes.

---

## 💤 PARKED — market realism ("mostly fine for now", your call)
Headline goal met (CLOSE ret_acf −0.17, real range). Residual flow/VWAP ~−0.18 = **structural** (confirmed by the
deepest lever, desync). Everything below is **default-off or designed-not-implemented**, deferred to a future round:
- sentiment-dynamics slope model, variable-volume "Pillar B" activity field, imbalance pillars A–C (inertia/herding/
  momentum), aggression-balance, weighted-week anchor + cap-from-seed, price-memory daily-anchor (needs a CapFromSeed
  fix before it can ship — currently ratchets), taker-flow asymmetry (concluded "known & bounded", BuyStopFraction=0).
- Tools (default-off levers): chaser v1/v2, market-maker cohort, desync, impact-decouple, touch-tighten, age-expiry.
- **Future idea (council Expansionist):** the per-bot perceived-state machinery (built for desync) is a platform for a
  *heterogeneous-horizon* realism round (latency/info-asymmetry tiers) — the first real crack in the LLN-averaging ceiling.

---

## 📝 STALE DOCS (flagged; corrected here so they don't mislead)
- `bot-market-realism-v2-plan.md` reads "5 pillars, design, not implemented" — **SUPERSEDED**: realism shipped via a
  different route (bounce-mid + anchors + sentiment + staggering + default-off tools). Not "critical open."
- `random-walk-sentiment-plan.md` reads RegimeDrift "not implemented" — actually **BAKED** (in the shipped config).
- Memory `project_advanced_orders_shorts_p1` "live soak pending" — P1 is **merged to master + validated**.
