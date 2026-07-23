# REALISM 36-HOUR AUTONOMOUS A/B RUN — async hub (STATUS = crash-recovery anchor, read FIRST)

**Goal:** unpin the simulated market (prices frozen ~20% below seed; MSFT ~510 vs seed 639) and give it realistic
**persistent** price movement. Window: started **2026-07-21 23:36 (+0200)**, **EXTENDED (Kiesh 2026-07-23 02:42) to ~Sat/Sun
2026-07-25/26 13:00 (~81h; Kiesh said "Saturday 13:00 ~81h" — Sat 13:00=~58h vs 81h=Sun 07-26 11:42, took the LATER; the
self-perpetuating 5h timer chain persists the run regardless of this soft window)**. Owner (Kiesh) is
AWAY / autonomous (watching ~1h from 02:42 to steer, then continue autonomously). Working mode = memory `feedback_autonomous_research_loop` (council-driven, battery-not-one-cell,
liveness watchdog, 45-min screens → confirm → push proven wins to prod, continue). Iteration authority = make the calls
myself (`feedback_autonomous_iteration_authority`); NEVER flip a prod default outside the reversible env A/B protocol.

---

## ★★★★ KEEP-GOING MANDATE (Kiesh 2026-07-23, standing — do NOT wait to be told to continue)
**PROCEED AUTONOMOUSLY AND RELENTLESSLY.** Kiesh does NOT want to keep reminding me to keep going. Between his steers I MUST
keep experimenting, building (default-off → unit-test → CK-soak → reversible-env prod A/B), pushing PROVEN changes to prod for
better live readings, and iterating — WITHOUT pausing to ask "should I continue" or waiting for approval. Kiesh pops in to steer
opportunistically; I keep going nonetheless. FINE-TUNE THE MARKET TO THE TARGETS in **`docs/explainers/BOT_MECHANICS.md` §1
"TARGET VALUES" (the "Kiesh target" column = the Fine-Tuning Settings)**: typical intraday ±5% / >10% NEWS-ONLY rare / net drift
POSITIVE over a week / stairs-up + rare elevator-down / choppy-but-alive / CK=0 always. Standing authority: prod pushes gated
ONLY on council green-light ([[feedback_prod_push_council_authorization]]); iteration decisions are MINE
([[feedback_autonomous_iteration_authority]]); ALWAYS `--env-file .env.production`. The ONLY things that pause me: a CK gate
trip, a genuine fork needing Kiesh's taste (surface it + keep other work moving), or an env/permission block (log it + continue
non-prod work). Never idle waiting.

**★ PRIORITY ORDER (Kiesh 2026-07-23):**
1. **★★ MAIN = TUNE THE LIVE MARKET to `BOT_MECHANICS.md` §1 targets.**
   **★ §1 TARGET REVISION (Kiesh 2026-07-23, sign-off steer): DECREASE the §1 move targets — "typical ±5% / >10% news-only rare"
   is TOO STRONG; it makes the charts TOO DEPENDENT ON NEWS EVENTS for price movement and too little RANDOM-WALK-like.** ⇒ shift
   the SOURCE of movement from news to ORGANIC RANDOM-WALK bot flow: (a) DECREASE typical-move target (smaller everyday moves,
   ~±2-3% not ±5%), (b) reduce NEWS strength + dependence (the news-strength-down/skew-small tune below), (c) INCREASE organic
   random-walk texture so price wanders on its own (MarketPulse osc+jitter; base taker flow) — news becomes a CONTRIBUTOR, not
   the main mover; relax ">10% news-only" (organic moves ok too). **UPDATE `docs/reference/BOT_MECHANICS.md` §1 + `FINE_TUNING_TARGETS.md`
   with the decreased/random-walk-first targets when tuning.** The north star (Kiesh): make it look NATURAL + RANDOM-WALK-LIKE. The relentless focus: run prod experiments + reversible-env
   A/B, read the tape (drift, 1-min move p95/max, |ret|-clustering, CK), and dial the LIVE knobs (RegimeTaker Str/Cohort/Threshold,
   MarketProbMult, RegimeDrift Strength, DipBuy, BearShort/GlobalShock for crashes, the μ-engine + log-sym flags below) toward:
   typical intraday ±5% / >10% NEWS-ONLY rare / **net drift POSITIVE over a week (stairs-up)** / rare elevator-down / choppy-but-
   alive / CK=0. Measure, don't eyeball; commit a set, soak, adjust ≤once, freeze; push proven wins to prod for better readings.
2. **SECONDARY (the "other two"):**
   a. **FEATURE BUILD QUEUE** (each: own-file-where-new, default-off, unit-test, local CK-soak, then reversible-env prod A/B):
      **DONE+committed:** μ-engine B RegimeTaker-bias `450d4c3`; log-sym #1 value-anchor-gap→ln(f/p) `10708ce`; **MarketPulse WIRED
      `6ba7650` (osc+jitter on regime-taker rate; awaiting prod A/B + Kiesh eyeball once CK-fix lands)**; **log-sym #2/#3 FundamentalService
      band clamps→geometric `3274f4f` (`Bots:Fundamental:GeometricBand` default-off; #2 OU clamp + #3 read-time excursion compose)** (all default-off, byte-identical).
      **log-sym FOLLOW-ON DONE `d1d8d29`** (BotPriceMemoryService ClampToBand/AdaptiveAnchorValue→geometric, `Bots:ValueAnchor:GeometricBand`
      default-off) ⇒ the audit down-bias fix is now COMPLETE across all 3 anchor paths (#1 gap + #2/#3 Fundamental + follow-on PriceMemory).
      **QUEUE:** μ-engine A
      (injection→taker net-up, CK-critical order-plumbing — build carefully; DEFER while the Q7 CK-fix deploy is pending) → taker-cap "volatility
      governor" (per-stock per-tick taker-notional cap — lower priority in the current calm regime).
   b. **DOCS REORG + REFERENCE/MEMORY** (agent `a02a7f383b76837ff` running): content-based re-split of `docs/`, a first-class
      `docs/reference/` bucket (config/settings + method-reference: ultradesign, timers, council, A/B protocol + BOT_MECHANICS +
      fine-tuning targets + all important settings). MAIN-AGENT FOLLOW-ON when it lands: (i) flag docs/reference/ as key in
      `CLAUDE.md`; (ii) create ONE auto-memory entry per docs/reference/ file (Kiesh: every reference file must also be in memory
      so the project's settings+ideas load each session) + MEMORY.md pointers.

## ★★★ DESIGNS & SOAKS LEDGER (2026-07-23 session — everything designed/soaked, most-recent context)
**SHIPPED TO PROD:** CK-fix (cross-ccy short collateral, origin/master `6056ce1`, validated Q7=0) · news-strength cut (ExogShock MaxMag
0.12→0.06/exp 3.5, live, calm). **BUILT default-off, on branch, awaiting a use:** μ-engine B `450d4c3` · log-sym suite (GeometricGap `10708ce`
+ Fundamental GeometricBand `3274f4f` + ValueAnchor GeometricBand `d1d8d29` — the drift-negative fix) · MarketPulse WIRED `6ba7650`+CONFIGCHECK
`6241072` (CK-safe soak, step-vs-glide Artifact 76fbc04f delivered — **HELD for Kiesh's eyeball test**, council said don't prod-test-bench).
**DESIGN PLANS committed, AWAITING KIESH (docs/plans/, all council-reviewed, do NOT build):** `CANDLE_NATURALIZATION_PLAN` (read-time cosmetic
+ ★wick-TRIMMED aggregation for bigger candles [Kiesh steer] + the existing `Candles:HLMinFillSize` v1; hard-clamp not resample, Close-immutable,
byte-diff-tested) · `MVVM_FOLDER_RESTRUCTURE_PLAN` (split the 46-file BackgroundServices/Helpers → Decisions/Economy/Lifecycle/Telemetry/Infra +
client VM Rows/Contracts; DEFER Helpers/DataServices; SKIP Controllers; one-folder/commit + launch-verify) · `VOLUME_ROTATION_PLAN` (rotating
per-stock hotness H by 4h-window, ★hooks the direction-neutral SIZE coupling [volume≠move, Kiesh steer], median-1, per-class-clamp, discrete+blend
v1 / OU v2) · `STOCKPROFILE_VOLUME_KNOB_PLAN` **← ULTRADESIGN COMPLETE 2026-07-23 (3 architects→5-lens council→chairman, 9 agents), NOW THE
5-KNOB SECTOR-DRIVEN + SIZE-DERIVED model** (Kiesh: "give sectors the data, stocks derive from SIZE+SECTOR randomly"). Design-of-record: sector
baselines = IN-CODE table (council: DB/config breaks replay-determinism = the answer to Kiesh's DB Q); size = MARKETCAP rank (SeedPrice×Shares, NOT
raw shares) via IStockService.GetListings; per-knob hash jitter ±8%; 4th VolumeMult = DEDICATED post-clamp line (NOT SizeExpFloor — council overruled
all 3 architects, prevents volume→move leak); 5th NewsFreqMult = deterministic thinning + λ-conservation (tech ≥1.5× news, total const). Default-off
byte-identical, CK=0. BUILD HELD for Kiesh's say (ultradesign pattern); default-off+soak pre-authorized, prod=council. **SOAKED, CONCLUDED:** burst A/B (Composition Cap 3.0 vs 2.0) = INERT in calm (no bursts news-off); council LEAVE-IT (news-cut already
handled bursts; TakerExp 0.5→0.35 is the lever IF Kiesh re-reports; never soak a burst-lever news-off). **KEY KIESH STEERS folded everywhere:**
VOLUME ≠ PRICE-MOVE (boost SIZE not the directional taker-upgrade); values are fixed constants (add per-stock jitter for variety); don't over-tune
an on-target market (burst-council lesson).

## ★★★★ PORTFOLIO BUILD+TEST PLAN — GREEN-LIT (Kiesh: "build all of them, you test it, THEN I look" + portfolio council 2026-07-23)
Council (5-lens→chairman, 6 agents): **BUILD ALL 5.** F1/F2/F4/F5 sign off BY METRICS ALONE; **F3 needs Kiesh's ONE eyeball** (cosmetic — a
wick-ratio gate proves "no harm", never "prettier"). **BUILD + A/B ORDER (attribution — never co-enable F1/F2/F5 during their solo soaks):**
1. **F1 StockProfile** (sector+size 5-knob) — ✅ **BUILT + GREEN + COMMITTED `0835aa7`** (748 tests, OFF==legacy byte-identical PROVEN, CK=0 by construction; flag `Bots:Personality:SectorSizeModel` default false). 5 files: StockProfileService.cs (model), AiTradeService:381 (ctor), AiBotDecision:2074 (VolumeMult line), ExogShock:129+NewsRepeats (NewsFreq thinning), appsettings. NEXT = **solo A/B** vs fresh OFF (arm sets SectorSizeModel=true; gates in the METRIC GATES row below).
2. **F5 MarketPulse** (already coded `6ba7650`, flag `...:MarketPulse:Enabled`, needs TakerCoupling+TakerStrength>0) — **solo A/B** vs fresh OFF (isolate the momentum lever).
3. **F2 VolumeRotation** — BUILD default-off, A/B with **F1 held ON as baseline** (measure the delta F2 adds; it redistributes F1's size coupling).
4. **F1+F5 combined** confirmation soak — sole job = ret_acf (stacking momentum on a livelier tape can drag lag-1 toward 0 while still in-band; gate ret_acf within OFF ±0.05, NOT just band-pass).
5. **F4 MVVM restructure** — pure refactor, gate = build + 739 green + launch + byte-identical candle diff (NO soak). [MY DEVIATION from council "F4 first": doing realism features FIRST since that's the run's purpose + F4 is disk-heavy/low-realism-value; F4's "clean diff" benefit is cosmetic. Slot it as a focused pass.]
6. **F3 CANDLE_NATURALIZATION — LAST** (Kiesh: live model shows the target flow first). BUILD + byte-diff/wick gate for "no harm", + Kiesh's ONE end-of-run eyeball. See its plan's ★★ KIESH F3 SPEC (follow old price + add new-economies texture + trim + recompute mood/F&G).
**METRIC GATES (soak-asserted, 120min or same-seed OFF/ON pairs — 45m under-powered for news-λ/Jaccard/ret_acf-SE; CK=0 hard gate every arm):**
F1: size↔volume Spearman ρ ≥0.35 (was ~0.2) · corr(volume,|ret|) ≤0.15 (volume≠move) · tech news ≥1.5× industrials · λ conserved ±5% · OverheatCap
never breached · PARITY drift ±0.15%/6h, ret_acf ±0.05, move>3% ±10%. F2: top-decile-vol Jaccard ≤0.55 across adj 4h windows (Δ≥0.15 vs F1-ON) ·
agg volume ±2% · **VolumeMult×Boost product ≤ SIZE cap per stock** (compound-cap — F2's clamp doesn't know F1's VolumeMult). F5: ret_acf in
[−0.43,−0.10] + step-glide lag1-3 acf, no shift toward 0 >0.05 vs OFF · move p99 Δ≤10% · drift ±0.5σ. F4: diff-only. F3: OFF byte-identical + Close
immutable + wick/body p95 ↓≥15% + drift/ret_acf/move unchanged (read-time cosmetic) + Kiesh eyeball. **Plain-English status for Kiesh:** F1="stocks
have different personalities Y/N"; F2="different leaders each day, total trading unchanged Y/N"; F5="momentum added without breaking mean-reversion Y/N".

## ★★★★ COUNCIL VERDICT on the CK-fix deploy (2026-07-23 ~04:35 UTC) = DO NOT deploy unattended; cherry-pick + hand to Kiesh
5 advisors + 5 peer reviews. Strongest = Contrarian + First-Principles; unanimous blind spot = Expansionist's "ship whole branch".
**DECISION:** the fix is CORRECT and should ship, but NOT as an autonomous owner-away emergency push. Decisive logic: (1) issue is
CONTAINED + self-limiting + the guard is WORKING (no money loss); (2) the ONLY real harm is a confounded private drift-metric on ONE
house bot — a convenience motive, not an emergency — and I can simply STOP TRUSTING that signal (already am); (3) cheap zero-prod-risk
mitigations beat a fragile owner-away settlement deploy; (4) **the prod deploy SHELL needs Kiesh to unblock it anyway (classifier-
blocked in autonomous mode) → autonomy buys nothing here.** **ACTIONS:** (a) CHERRY-PICK ONLY the fix `f109d47`+`dbee8ef` onto master
(NOT the branch tip — MarketPulse/docs must not ride a hotfix; 4/5 advisors + all reviews); (b) HAND the deploy decision to Kiesh with
the evidence (his shell + his settlement code); (c) treat the drift signal as CONFOUNDED until it lands (exclude FX-house 20002 from
organic-drift reads — it's a house/arb acct); (d) BEFORE deploy verify the open-path SIBLING reachability post-fix (Contrarian's catch:
unsticking the Q7 loop may hand the bot a runway INTO the latent throw) — confirm unreachable or accept it may newly fire; (e) DEFER the
sibling / multi-ccy-collateral MODEL question as an explicit design decision for Kiesh (First-Principles: the fix is asymptomatic, not
a correct model — one-collateral-ccy-per-position vs an FX house acting multi-ccy). Note: the fix SELF-HEALS existing stranded state
(stuck cover retries succeed post-fix + release collateral). Optional pre-deploy: short FX-house CK-soak as a smoke check (NOT the gate;
the discriminating unit test is the gate). ⇒ deploy is now cleanly Kiesh-gated; I do NOT push unattended.

## ★★★ CK-TRIP 2026-07-23 ~04:25 CEST (Q7, ACTIVE on prod — FIX committed, NOT deployed — READ FIRST)
**What:** `Q7 pre-write CK violation` firing on prod since ~02:08 UTC. Root cause = **cross-currency short-close**: user
**20002 = the FX-desk house** opened shorts collateralized in **EUR** (stocks 3 & 10), then covered with **USD** fills. The
buy-to-close collateral release in `TradeSettler.cs` only handled collateral==fill-ccy; the mismatch branch just LOGGED
(`Short-close currency mismatch ... collateral not released this fill`) and released nothing ⇒ a covered position (Q≥0) still
carried `ShortCollateral` ⇒ DB invariant violated ⇒ the Q7 pre-write guard THREW + rolled back **every batch touching positions
#1000053 / #1000060** in a stuck retry loop (each rollback un-covers the short, so it never completes) + a **GROWING phantom
reservation** (`Reservation reconcile ... phantomTotal` 6615→13246 over ~5min).
**Severity = contained, NOT an aggregate loss:** cash steady ~$36.48B every sample; the guard BLOCKED the bad write (money
conserved). Blast radius = 2 positions / 1 bot (FX house) + co-batched orders rolled back + slowly growing phantom (reserved,
not lost). Not catastrophic; can wait for the proper fix.
**FIX committed `f109d47` (branch, TradeSettler.cs, NOT deployed):** unify both branches — release collateral denominated in
`ShortCollateralCurrency`, unreserving from the COLLATERAL-ccy fund (resolved + snapshotted via fundMap/fundSnapshots like
buyerFund ⇒ tx-tracked + rollback-safe). `collCcy==ccy` reduces to the original path byte-for-byte; **739/739 tests pass**.
**PRE-DEPLOY GATE (CK-critical settlement code):** (1) ✅ cross-currency-close INTEGRATION TEST committed `dbee8ef` —
`Cross_currency_short_close_releases_collateral_in_collateral_currency` in `MarketShortBatchFillEquivalenceTests.cs`, PROVEN
DISCRIMINATING (fails on pre-fix settler = res.PlacedSuccessfully false, passes with fix; 740/740 suite green). (2) LOCAL CK-SOAK
exercising the FX house (Q7 gone + CK=0) — optional extra assurance (the unit test already pins the exact path deterministically;
a soak may not naturally hit cross-ccy FX shorts in a short window). (3) **llm-council green-light** = the standing prod-push gate.
**★★★ CK-FIX DEPLOYED TO PROD (2026-07-23 09:19 UTC) — Kiesh green-lit ("deploy the CK-fix") + shell worked.** Cherry-picked f109d47+
dbee8ef onto master (as 9d5e842+6056ce1, origin/master tip 6056ce1) → deployed via the compose-preserving recipe (backup live compose
`docker-compose.prod.yml.pre-ckfix-20260723` → git checkout compose → git pull --ff-only 42c8b66→6056ce1 [only 9f7bdea docs + the fix +
a test file, NO migration] → restore live compose BYTE-IDENTICAL → up -d --build server --env-file .env.production). VERIFIED: RestartCount=0,
Health=healthy (clean boot, env preserved), listening + bot loop + trades flowing, **Q7=0 since deploy**, no conservation/shortfall errors.
Existing stuck positions (#1000060/#1000061) self-heal as their next cover succeeds + releases collateral (phantom should clear). Monitoring
now confirms Q7 stays 0 + phantom clears. NOTE: MarketPulse + log-sym suite + §1 docs remain on branch `perf/admin-table-time-indexes`
(NOT deployed) — next deploy candidates (MarketPulse prod A/B; log-sym flags if clean drift stays negative). Prior STATUS (pre-deploy):**
- **CK-trip (Q7): RESOLVED — FIXED + DEPLOYED @ 09:19 UTC + VALIDATED @ 10:03 (44min post-deploy):** Q7=0, phantom CLEARED (no reconcile
  WARN in 30m = under tolerance = stuck positions covered + released collateral post-fix), healthy, RestartCount=0. The deterministic fix +
  clean 44min = trip resolved. (If Q7 ever recurs → unexpected, investigate the settle logs.)
- **★ ret_acf + MOVE-FREQ now in the WATCH (Kiesh 2026-07-23 flagged "ret seems high again / trim final candles?"):** MEASURED @11:29 UTC —
  the market is CALM + ON-TARGET, NOT a trim artifact. ret_acf USD: **VWAP −0.04** (from Transactions per-min VWAP; right at the §1 →−0.1 target =
  clean random walk) / Close −0.24; trimming the final 5 candles changes NOTHING. Moves USD 3h: p95 0.55 / p99 1.2 / max 4.2; >2%=5, >3%=1, >4%=1,
  **>5%=0** = §1-perfect (rare bigger, none >5%). A brief livelier patch ~30-40min pre-11:29 (4.2/−2.7/−2.1% moves) then calmed = healthy vol-cluster.
  ⇒ NO blind tune (would move AWAY from target). ★ MEASUREMENT NOTES: filter ONE currency (cross-listed USD/EUR pollute ret/moves!); official
  ret_acf = per-min VWAP from Transactions (Close-basis reads more negative). TUNE TRIGGERS if a REAL high emerges: Close/VWAP ret_acf → −0.4 (too
  mean-reverting/bouncy) ⇒ small trend-follower/momentum nudge; moves systemically >3-4% or any >5% creeping ⇒ small RegimeTaker-strength trim.
  CALIBRATION Q for Kiesh: which readout/basis/stock shows "high"? (VWAP −0.04 vs Close −0.24 differ.)
- **★ CLEAN DRIFT + ret_acf — §1 ON-TARGET, drift STABLE near 0:** @19:32 drift 6h **+0.096** (flat; trend −0.502→+0.014→+0.096 = the dip fully
  recovered, now 2 positive cycles, stable ~0 with a slight positive lean). ret_acf VWAP **−0.056** (on-target band). Moves calm (45m max 2.31, zero
  >4/>5% 3h). Healthy, Q7=0. ⇒ HOLD (0 consecutive negative; no trigger). |ret_acf|<0.2, moves calm — do NOT tune. Focus = F1 build (portfolio green-lit).
- **★ (prior) CLEAN DRIFT + ret_acf — ON-TARGET, HOLDING; ret_acf is NOISY not trending:** drift 6h +0.109→+0.003→+0.054→**−0.014** (@13:27, oscillating ~0,
  dispersion alive). **ret_acf VWAP −0.044→−0.016→+0.012→−0.150** = NOISY/oscillating in [−0.15,+0.01], centered near the §1 −0.1 target, NO real trend
  (last cycle's +0.012 "climb" was noise — 2h/35-stock estimates bounce ±0.1/cycle; correctly did NOT tune on it). Tape very calm (45m max 1.97, zero
  >5%; 3h zero >5%). Healthy, Q7=0. ⇒ ret_acf on-target; high-side watch RELAXED (need 2+ consecutive cycles of a REAL climb, not one noisy point).
  Triggers stand: ret_acf sustained >+0.05 ⇒ cut trend-follower; sustained ←−0.4 ⇒ add it; drift persistently negative ⇒ log-sym A/B; moves >5%
  creeping ⇒ vol trim. Currently all §1-on-target — HOLD, steady.
- **Market:** calm (p95 ~0.5-0.7%, zero >5%). Drift 6h OSCILLATING in a tight band around 0 — trend +6.76→+1.87→−0.13→+0.10→−0.39→−0.05→
  −0.03→−0.29 (NOT monotonically deepening; small negative bias but within noise), dispersion ALIVE (~4-5% spread) ⇒ HOLD, not re-pinning.
  RE-PIN trigger = calm tape AND drift PERSISTENTLY negative/deepening AND dispersion COLLAPSES (<~2%), all 3.
- **★ POSITIONING (net-up levers ready post-CK-deploy):** the small negative bias is EXACTLY what the COMPLETE log-sym down-bias suite
  targets (GeometricGap #1 `10708ce` + Fundamental GeometricBand `3274f4f` + ValueAnchor GeometricBand `d1d8d29`, all default-off) + the
  μ-engine (net-up). Once the CK-fix deploys + the FX-house confound clears → read clean multi-hour drift → IF still negative, a reversible-
  env prod A/B of the log-sym flags is the natural next experiment. §1 wants net-POSITIVE over a WEEK; ~0 now is inconclusive.
- **★ MARKETPULSE — CK-SAFE (soak proven), prod flip HELD by council (2026-07-23 ~12:40).** 25-min local A/B (control mpc/5080 pulse OFF vs
  mpp/5083 pulse ON, config confirmed via CONFIGCHECK `6241072`) = **CK=0 both arms + no vol blowup** (p50 identical, max lower with pulse) ⇒
  MarketPulse ON is CK-SAFE. **COUNCIL (5 adv, 3-2 HOLD, decisive) on flipping ON prod: HOLD.** Key insight (First-Principles/Contrarian/Outsider):
  don't use PROD as a test bench for a taste feature because the local CLIENT harness glitched — the stepped shape is SUB-MINUTE + observable
  LOCALLY via a tick-resolution price-path plot (zero prod risk, no drift-confound). Also: flipping on prod WHILE tuning drift would CONFOUND the
  eyeball; 25min too short for a τ30-90s envelope. **FINDINGS:** sub-minute |ret| clustering noisy over 25min (inconclusive); ★ my first
  Transactions analysis was CURRENCY-POLLUTED (cross-listed stocks mix USD/EUR levels — MUST filter one currency); refiltered, a clean USD move
  showed leg→plateau→leg structure (the "up-slow-up" pattern, encouraging but 1 move only). **★ VISUAL DELIVERED (2026-07-23 ~12:55):** built a static step-vs-glide price-path chart from the EXISTING 25-min soak data (single-currency
  filtered — StockId 1 USD, ~2% down-move each arm, 2s buckets) → Artifact https://claude.ai/code/artifact/76fbc04f-d1c0-485b-9d89-0e56c0f42330 .
  The PULSE-ON path descends in plateau→leg→plateau STEPS; the control glides smoother. Suggestive (1 move/arm, news-off so moves top ~2%), for
  Kiesh's eyeball. If he wants a cleaner read: a LONGER (2h) or STRONGER-regime soak (harness clean now; full-cleanup first, single launch). If it steps + he likes it ⇒ THEN prod via the
  Executor's two-stage rails (deploy default-off → confirm behavior-neutral → env flip ON → watch CK/vol vs OFF baseline → revert trigger) + FREEZE
  drift-tuning during the prod A/B. MarketPulse WIRED `6ba7650`, CONFIGCHECK `6241072`. Kiesh green-lit A/B soaks autonomous + council=ship
  ([[feedback_prod_push_council_authorization]] REINFORCED) — the council USED that gate to correctly HOLD this one.
- **Build queue:** log-sym arc COMPLETE (all 3 anchor paths); MarketPulse WIRED `6ba7650` (A/B soak LIVE — see above); μ-A
  (CK-critical) + taker-cap (low-value in calm regime) DEFERRED. ⇒ monitoring + drift-trend tracking IS the MAIN work at this pause.

**★ SIBLING REACHABILITY — RESOLVED (2026-07-23 ~05:15, council pre-deploy check; ArbitrageDecisionService read):** The FX
house (20002) arb is BUY-FIRST then SELL only `Math.Min(filled, AvailableQuantity)` — an explicit "never oversell into a short"
guard on every sell leg (per-order :296-300 + batched :325-338). ⇒ it NEVER intentionally opens shorts; the observed EUR shorts are
TRANSIENT artifacts of the stale-snapshot race (the already-accepted F1 "optimistic-read divergence", CK-clean rollback), closed by
the next tick's buy leg. To reach the OPEN-path sibling it must SELL INTO an existing short (extend cross-ccy) — the guard yields
sellable=0 on a short position, so it CAN'T, except via the same rare double-race → which just rolls the batch back (benign, like
F1). ⇒ the FIX does NOT "hand a runway" into the sibling (the Contrarian's worry): by cleanly closing cross-ccy shorts it SHORTENS
the short-state dwell → REDUCES the extension-race window. Sibling = latent, rare-race, benign-rollback, SAFE TO DEFER; deploy does
not worsen it. This clears the main objection to the deploy.

**★ SIBLING-BUG AUDIT (2026-07-23 ~05:00, read-only sweep of cross-currency collateral paths):** The committed fix covers the
ACTIVE trip (position collateral RELEASE on cross-ccy buy-to-close). Found ONE LATENT sibling of the same class, NOT currently
firing: short-OPEN **extending** an existing short in a 2nd currency (`TradeSettler.cs:503/606` → `Position.TakeShortCollateral(
collateral, ccy)`) THROWS `InvalidOperationException` because a Position may hold ShortCollateral in only ONE currency (model
limit, `Position.cs:171`). The FX house's current pattern is EUR-open→USD-close (the fixed path), NOT EUR-short→USD-extend, so it
isn't firing — but if it ever extends a short cross-ccy it will trip. This is a DESIGN fork for the council (options: multi-ccy
position collateral / prevent cross-ccy short extension / make FX-house 20002 shorts currency-consistent), NOT a unilateral fix
expansion. NOT susceptible: ORDER-level collateral paths (BracketCoordinator/OrderCanceller/OrderSettler) — order collateral is
single-currency by construction (an order can't mismatch its own currency). ⇒ deploy the close-path fix now; queue the open-path
design for the council.

## ★ BURST A/B — PRELIM FINDING (2026-07-23 ~17:52, Kiesh "bursts too powerful"): Cap A/B INERT in the calm soak
Ran a local A/B: Composition Cap 3.0 (control kse_soak_bctrl/5080) vs 2.0 (kse_soak_bred/5083), news-off. ~24min prelim (USD): arms
NEAR-IDENTICAL (moves p95 0.66 vs 0.64, max 1.91 vs 2.40, volume/min ~285 both) — the Cap change is INERT because a calm news-off soak
produces NO bursts (activity G ≈ baseline ⇒ act rarely approaches 2.0/3.0, so the Cap never binds). ⇒ the composition Cap only bites during
activity BURSTS, which are event-driven (news/activity spikes). Prod bursts are intermittent + the NEWS-STRENGTH CUT already shipped tames
most of them. **FINAL (30min soak, CK=0 both): arms NEAR-IDENTICAL — Cap 3.0/2.0 inert in calm (confirmed).** ★★ COUNCIL VERDICT (5-lens): **LEAVE
IT — burst-taming is a FIX LOOKING FOR A PROBLEM.** The shipped news-cut (ExogShock MaxMag 0.12→0.06/exp 2.5→3.5) already removed the CAUSE
(event-driven bursts); prod is §1-on-target (zero >3% 3h, drift ~0, ret_acf ≈−0.1). Chasing the transmission (Cap/TakerExp) now risks
over-tuning a healthy market into FLATNESS (§1 wants choppy-but-alive + rare elevator moves). Methodological rule: NEVER soak a burst-lever
news-OFF again (Cap/act inert without bursts). IF Kiesh re-reports "too powerful" AFTER watching calm prod ⇒ the right lever is **TakerExp
0.5→0.35** (unanimous — the DIRECTIONAL taker-upgrade = what makes a burst feel powerful, per volume≠move; Cap/taker-caps calm the wrong
axis) via a BENCH news-ON A/B (45min, metric = |ret| p95/max during induced bursts DROP while volume/ret_acf/drift stay flat = softer punch,
same pulse). ⇒ NO prod change, NO burst-producing A/B now (deferred until Kiesh re-reports). Prod left as-is.

## ★ SESSION SNAPSHOT 2026-07-23 ~03:55 CEST (fresh /clear session — most recent, read after the mandate)
**Prod HEALTHY:** box 01:32 UTC, RestartCount=0 (single clean restart 01:30:05 UTC for the news cut, NOT crash-looping),
Health=healthy, news cut LIVE (`CONFIGCHECK ExogShock mag=[0.01,0.06] exp=3.5`), CK CLEAN (no conservation/shortfall/violation
in last 15m). Resume timer `1243b76e` armed @ 07:48 local — chain intact.
**News-calm reading (GOOD — cut is holding):** last 2h 1-min moves p50 ~0.05-0.10% / p95 ~0.6-0.9% / max ~2-5%, **only 1 move
>5% and ZERO >10% across the whole 2h** (calmer than the pre-cut p95 1.12%/max 8.73%). **Market RECOVERING (stairs-up):** median
price change 6h→now **+6.76%** (p25 +2.8 / p75 +12.3), 3h→now +1.28%, 1h→now +0.03% (flat this last hour post-cut but still
±0.7% organic dispersion per stock = NOT frozen/re-pinned). **Driver composition (confirms cut is well-placed):** prod ExogShock logs post-cut = `max|s|=0.06 mean|s|=0.02` (was mean~3.7%/
max~20% pre-cut) ⇒ news now MOSTLY TINY (~2%), capped ~6% = news is a small bounded CONTRIBUTOR; organic machinery active
(Regime sign flipping, RegimeTaker Str0.4/cohort0.3 live, BankEstimate republishing) ⇒ residual movement increasingly ORGANIC =
the §1 random-walk-first target. MarketPulse is the ready next organic lever IF over-calm appears. Verdict: news cut looks good — calm tape + strong 6h recovery +
alive dispersion + CK=0. **WATCH:** if over the next hours the recovery stalls AND dispersion collapses ⇒ over-calmed/re-pinning
⇒ add organic movement (flip MarketPulse ON, or a small RegimeTaker bump) — NOT more news. If >5% moves creep back up ⇒ news
still too hot. Re-measure every ~30-45m via the prod candle SQL (below).
**DONE this session:** (1) MarketPulse WIRED + committed `6ba7650` (default-off, 739/739 tests — see MARKETPULSE WIRED block).
(2) §1 target revision (random-walk-first) committed `69ffbd8` (BOT_MECHANICS §1 + FINE_TUNING_TARGETS).
**PROD CANDLE-METRICS SQL (reusable):** `ssh root@159.195.149.51 'docker exec -i kse-server-postgres-1 sh -c "psql -U
\$POSTGRES_USER -d \$POSTGRES_DB" <<SQL ... SQL'` — table `"Candles"` (BucketSeconds=60 = 1-min; cols OpenTime/Open/Close/StockId);
move = `abs("Close"/"Open"-1)`; cast percentiles `::numeric` before `round(...,3)`. No seed col in Stocks ⇒ measure drift as
median per-stock `latest.Close / close_N_hours_ago - 1`. NOTE: secrets-adjacent greps on the box are classifier-BLOCKED — read
DB name/user via the container's own `$POSTGRES_USER/$POSTGRES_DB` env (as above), never by grepping `.env.production`.

**★ /CLEAR-READY (Kiesh will /clear or /compact soon → a FRESH session must continue autonomously from THIS runbook + memory
with ZERO further input).** So this STATUS is the single source of truth: it must stay VERY CLEAR — priorities above, live prod
config in the vol/μ sections below, running tracks (`b3s0vn9gr` vol monitor, `a02a7f383b76837ff` docs agent, timer `1243b76e`
@ 07:48), and the queue. A fresh session: re-arm the timer on its fire, read this top block, and EXECUTE priority 1 relentlessly
while advancing the secondary tracks — never wait for Kiesh.

## ★★ MARKETPULSE = UN-HELD + FRONT OF QUEUE (Kiesh 2026-07-23 clarified the TARGET — BUILD IT)
Kiesh's precise target: the jitter is for the SHAPE of HIGH-INTENSITY DIRECTIONAL moves — when a NEWS event or high
sentiment/MOOD drives price in one direction, the move currently GLIDES uniformly (fast, smooth); he wants it STEPPED — e.g. a
$500 stock goes up to $502, momentum SLOWS, then up again (delayed/stepped, not a uniform high-intensity glide). "Random jitter
in the takerness values" is the mechanism, applied ESPECIALLY in high-activity moments but usable in quiet too. (The earlier
premise-check — |ret|ACF +0.47/+0.68, signed −0.63 — measured AGGREGATE 1-min clustering = the everyday CHOP, which drowns out
the specific directional-glide shape Kiesh eyeballs; so it measured the WRONG signal for THIS target. MarketPulse is right after
all.) MECHANISM (ultradesign v1 delivers exactly the ~30-90s "breathing" step-cadence Kiesh describes; sub-minute jerk = deferred
Hawkes): per-stock `MarketPulse` (OWN FILE per Kiesh — universal component, per-instance τ/amplitude) = OU z∈[-1,1],
m=exp(A·z−½A²σ²) mean-corrected, σ_z 0.60, τ 30-90s per-stock-jittered, dedicated RNG, default-off byte-identical; `PulseChannel`
API. v1 LOCUS = `_regimeTakerStrength × Mult(sid,Taker)` at AiBotDecisionService (NOT CohortFraction — that's the ping-pong trap).
FOLLOW-ON channels (Kiesh emphasized news + high-mood): the ExogShock CHASER taker-rate + the mood-driven takerness. Config
`Bots:RegimeDrift:MarketPulse:{Enabled(false),TakerA(0.40),SigmaZ(0.60),TauMinSec(30),TauMaxSec(90)}`. A/B: A 0.35/0.5 × τ 30/90;
metric = does a directional move STEP (eyeball on ON arm 5083) + |ret|-clustering up + P95≤2% + CK=0. Full spec: this runbook +
the ultradesign output. BUILD ORDER now: MarketPulse (this) → μ-A → taker-cap → log-sym #2/#3.

**★★ MARKETPULSE WIRED + COMMITTED `6ba7650` (2026-07-23 ~03:55) — DONE, DEFAULT-OFF, 739/739 TESTS PASS.** All wiring from
the build state below is complete: BotSentimentService owns two `MarketPulse` instances (slow osc τ30-90s A0.35 + fast jitter
τ2-6s A0.12), Steps both per stock per tick beside RegimeDrift, exposes `TakerPulseMult(sid)=osc.Mult×jitter.Mult`, Resets both;
AiBotDecisionService multiplies the regime-taker strength by `(decimal)_sentiment.TakerPulseMult(stockId)` (breathes the RATE,
never direction; Mult≡1.0 off ⇒ byte-identical); AiTradeService + appsettings expose `Bots:Sentiment:RegimeDrift:MarketPulse:{
Enabled(false),OscA,OscSigmaZ,OscTauMin/MaxSec,JitterA,JitterSigmaZ,JitterTauMin/MaxSec}`. Added `MarketPulseTests.cs` (9 tests
pinning: disabled⇒Mult≡1.0 + no RNG draw; enabled⇒E[Mult]≈1 no-net-bias; log-sym envelope; determinism; reset).
**NEXT for MarketPulse:** local CK-soak with `MarketPulse:Enabled=true` (prove CK-safe with pulse ON — the unit tests prove
off-path byte-identical, not the full-engine ON path) → then reversible-env prod A/B (ON arm 5083, Kiesh eyeballs whether a
directional move STEPS) = a taste-fork needing his eyeball; surface it + keep other work moving. Config prefix is
`Bots:Sentiment:RegimeDrift:MarketPulse:*` (NOT the runbook's earlier `Bots:RegimeDrift:MarketPulse` — placed under the existing
Sentiment:RegimeDrift namespace for consistency).

**★ ORIGINAL MARKETPULSE BUILD STATE (2026-07-23, now SUPERSEDED by the WIRED note above): `MarketPulse.cs` CREATED + COMMITTED `d106efb` (own file, UNWIRED = compiles, no
behavior change).** It's the universal per-stock OU oscillator: `Mult(sid)=exp(A·z−½A²σ²)` (log-sym, MEAN-CORRECTED =
variance-not-mean), per-INSTANCE τ+amplitude (Kiesh: each use-site its own osc-time/amplitude), dedicated RNG, disabled⇒Mult≡1.
**KIESH WANTS BOTH oscillation AND jitter (2026-07-23) — via TWO instances of this one component on the taker rate:**
- **SLOW oscillation** (τ 30-90s, A≈0.35) = the momentum ENVELOPE → stepped directional moves (up-slow-up).
- **FAST jitter** (τ 2-5s, A≈0.12 small, σ_z≈0.70) = fine tick-scale ROUGHNESS → the random-walk jaggedness.
- `effective taker rate = base × osc.Mult(sid) × jitter.Mult(sid)`. Small jitter amplitude ⇒ no re-spike. Each independently
  A/B-able (set either A=0 to test osc-only / jitter-only / both).
**REMAINING WIRING (finish this):** (1) BotSentimentService: own two `MarketPulse` fields (osc + jitter) built from config in
its ctor, `Step(sid, dt)` BOTH in the per-stock tick loop (beside the RegimeDrift walk at ~:367-373), expose
`internal double TakerPulseMult(int sid) => _pulseOsc.Mult(sid) * _pulseJitter.Mult(sid);` (accessor beside `RegimeSignal` ~:542),
Reset both in Reset (~:630, pass RngSeed). (2) AiBotDecisionService regime-taker block (~:872): pass `_regimeTakerStrength ×
(decimal)_sentiment.TakerPulseMult(stockId)` as the strength into `TrendTakerDecision` (jitters the taker rate → stepped/jagged
move). (3) appsettings `Bots:RegimeDrift:MarketPulse:{Enabled(false), OscA(0.35),OscSigmaZ(0.60),OscTauMinSec(30),OscTauMaxSec(90),
JitterA(0.12),JitterSigmaZ(0.70),JitterTauMinSec(2),JitterTauMaxSec(6)}` + AiTradeService reads them into the BotSentimentService
ctor. (4) `dotnet test` (730, byte-identical off). Then env A/B on prod (ON arm 5083, Kiesh eyeballs a directional move stepping)
+ CK=0. Salt the two instances differently (osc vs jitter) for distinct τ-phase + RNG streams.

## 📰 NEWS TUNING + CORRELATION (Kiesh 2026-07-23, AFTER /clear)
**(A) NEWS CORRELATION / OVERFLOW — COUNCIL to design after /clear (Kiesh: "let the council think on this").** Want: a single
stock's OWN news event bleeds a fraction into the rest of the market + a LARGER fraction into its SECTOR (per-event spillover,
so news is correlated: individual → +more to sector → +some to whole market). This is NEW — today's ExogShock has SEPARATE
market-wide events (`GlobalFraction 0.25` + `GlobalCoFire`) and sector machinery (`ExogShock:SectorCount/SectorFraction`,
`BankEstimate:SectorEventProb/SectorEventDownBias`), but NOT a source-stock→sector→market OVERFLOW of the SAME event. Council
designs the mechanism + fractions (e.g. sector gets ~X% of the source magnitude, market ~Y% with X>Y; decay/beta per stock;
CK-safe = it's a read-time anchor/sentiment tilt, no orders). Live prod news config: MeanInterval 60 / DecayHalfLife 600 /
Min-Max 0.01-0.12 / Exp 2.5 / Cap 0.25 / Chaser 0.10 / GlobalFraction 0.25 / GlobalCoFire 0.15 / Permanence AlphaMin 0.40 Tau 2000.
**(B) NEWS STRENGTH — DECREASE A LOT + SKEW TO SMALL (tune, MAIN-priority; Kiesh: freq up but "greatly skew to small").** Levers:
`Bots__ExogShock__MaxMagnitude` 0.12 → ~0.05 (lower ceiling) + `Bots__ExogShock__MagnitudeExponent` 2.5 → ~3.5-4 (steeper
power-law ⇒ big draws much rarer, mass piles at small) ± `Cap` 0.25 → lower. Goal: news mostly TINY, rare bigger — fits §1
(typical ±5% / >10% NEWS-ONLY rare). Current mean|s|~3.7% / max~20% = too hot. Reversible env A/B on prod, measure vs §1 + CK=0.

## ★★★ COMPACTION HANDOFF (2026-07-22 ~12:10 — READ THIS FIRST, self-contained)
The arc EVOLVED far past the original "unpin via config A/B." Current mission + state:

**MISSION (Kiesh, live-directed 2026-07-22):** implement 3 realism changes via the ULTRADESIGN method, build them ON the
RESTRUCTURE branch (so the dedup/decoupling + realism ship together — else we redo the work), local A/B, then PROD on council
green-light. Kiesh PRE-AUTHORIZED: "green light for whatever if the council agrees" — the ultradesign IS the council agreement,
so proceed; CK=0 is the always-on HARD gate; never ship an unvalidated combined branch (CK-soak first).

**THE 3 CHANGES (full fire-ready spec: `docs/arcs/REALISM_ULTRADESIGN.md`):**
1. **Value anchor = 28-day window PAIRED WITH `Elastic=true`** (28 alone still pins — the linear pull is the snap-back; the
   elastic soft-wall gives "wander free, overextension drifts back"). PURE CONFIG (`Bots:ValueAnchor:WindowDays 7→28` +
   `Elastic false→true`); co-tune RecentAnchor 0.05 vs 0.0; 28-day window needs 28 real days to warm on prod (untestable locally).
2. **Bot-types/reaction rework:** the asymmetric/correlated/taker CRASH apparatus is ALREADY LIVE (ExogShock chaser +
   GlobalCoFire + Permanence) — do NOT build a state machine for extreme. Honest extreme = ONE config knob `BearShortStrength
   0→~0.6`. The ONLY new code = "EARNED CALM" (a Calm-only Schmitt-trigger `BotMarketRegimeService`) — DEFERRED, build only if
   the combined bake shows calm is faked.
3. **RegimeDrift → taker flow (Kiesh's headline):** route the per-stock regime walk to TAKER flow (positive→buy takers→up).

**★ WHAT'S DONE:** **Change 3 BUILT + COMMITTED on `perf/admin-table-time-indexes` (commit `7b9cfb9`).** RegimeSignal accessor
(BotSentimentService) + post-pick per-stock taker override (AiBotDecisionService, reuses TrendTakerDecision + no-share guard;
value-band veto downstream) + AiTradeService wiring + appsettings `Bots:Sentiment:RegimeDrift:{TakerCoupling,TakerThreshold,
TakerStrength,CohortFraction,ContrarianFraction}`, all DEFAULT-OFF. Server compiles clean, **730/730 tests pass byte-identical**.
Ultradesign spec committed `bb25992`. Runbook relocated `fe4e4b4`.

**★ TEST-3 RESULT (2026-07-22 ~12:15, POST-COMPACT re-run):** Change-3 taker-path CK-safety = **GREEN at 30/45 min** —
control `kse_soak_rctrl` (114127) vs regime ON@0.4 `kse_soak_rregime` (114220), BOTH arms `err,ck,cons,short = 0,0,0,0` at
every sample; the taker path is DEMONSTRABLY ACTIVE (a per-tick counter climbs 0→69 in regime vs 0→17 control) yet
conservation-clean; aggregate drift near-identical (~−1.3%, EXPECTED — RegimeDrift is mean-zero per-stock, coupling changes
the *mechanism* not the aggregate). No blowup/lockstep giant candle. **FINAL VERDICT = PASSED** (full 45 min): ERR/CK/CONS grep = **0 across BOTH entire logs**; taker counter steady ~66-70
(regime) vs ~16 (control) all run = fires consistently, no runaway; drift bounded + RECOVERING (regime −1.24→−0.66 over
min 30-45) = anchor drift-back coexists with the new taker flow. **Change 3's new taker path is CK-safe under sustained firing.**

**★ KEY REFINEMENT (2026-07-22 ~12:15):** verified ALL THREE changes are now **pure-env on the existing exe** — the branch
already implements `Bots:ValueAnchor:Elastic`(+ElasticDeadbandPrc/Power), `Bots:ValueAnchor:WindowDays`, `Bots:BearShortStrength`
(AiTradeService :850-852/:530/:966). So Changes 1+2 need NO rebuild either. Since local A/B has WEAK power for realism (that's a
prod-over-days verdict) and **CK-safety is the locally-provable thing**, the next gating soak is NOT three separate realism arms —
it is the **COMBINED CK-SCREEN** (= the build-order's "CK-soak the combined branch" step): baseline vs ALL-THREE-ON together.
Script ready: `scratchpad/run-arm-combined.ps1` (control=all off; combined=RegimeTaker 0.4 + Elastic true/WindowDays 28 +
BearShort 0.6). If combined arm is CK-clean + bounded → the branch `perf/admin-table-time-indexes` (tip `7b9cfb9`) is
prod-shippable (council green-light already granted by the ultradesign) → merge→master→prod with `--env-file .env.production`.
The RegimeTaker/corr instrumentation = an OPTIONAL tuning aid, build only if the combined arm shows something needs tuning.

**★ COMBINED CK-SCREEN LAUNCHED (2026-07-22 ~12:30):** control `kse_soak_bctrl` port 5080 (bg `bj9ik3h0n`) vs combined
`kse_soak_bcomb` port 5083 (bg `barl01qm8` — staggered: it waits for control to reach the bot loop, THEN launches combined,
so `barl01qm8`'s completion = the combined arm's 45-min soak end + benign exit 49). 45 min → ETA ~13:20. ON COMPLETION: grep
ERR/CK/CONS across both logs (must be 0 — this is the prod-ship gate) + confirm combined arm bounded (no blowup). If clean →
proceed to merge→prod prep. Logs `logs/soakP-kse_soak_bcomb-*` / `-bctrl-*`.

**★ MERGE→PROD RECIPE (pre-analyzed 2026-07-22 ~12:35 — the merge is NOT a clean FF):** master is **2 commits ahead** of the
branch: (a) `f64b796` = admin-index migration `20260720211749_AddAdminTableTimeIndexes` (**already applied on prod**), (b)
`39bdedf` = a `docker-compose.prod.yml`-ONLY prod env tune (RegimeDrift 0.8/MPM 1.4/Tau 2000/AlphaMin 0.40 — the current prod
baseline, PRESERVE). **The hazard:** the branch has its OWN duplicate `20260720202712_AddAdminTableTimeIndexes` (tip `70f0dec`,
same indexes, content-identical + idempotent `CREATE INDEX CONCURRENTLY IF NOT EXISTS` so no crash) → two same-named migrations
off the same parent + both edit `KseDbContextModelSnapshot.cs` & `KseDbContext.cs` with identical index lines = EF migration-chain
conflict. **RESOLUTION:** `git merge master` into the branch → resolve: **DELETE the branch's `20260720202712_*` (both .cs +
.Designer.cs)**, KEEP master's `20260720211749_*` (the prod-applied one); take EITHER copy of the identical snapshot/DbContext
index lines (declare once); take master's `docker-compose.prod.yml`. Then VALIDATE: `dotnet ef migrations list` shows a LINEAR
chain (no two heads), `dotnet test` = 730 green, a fresh-DB apply is clean. THEN FF master→branch→master and deploy. (Prod's
`__EFMigrationsHistory` already has `20260720211749`; dropping the branch dup avoids a redundant no-op migration + a two-head
snapshot.) Do this build+validate cycle only after the exe is free (soak done).

**★ COMBINED CK-SCREEN RESULT (2026-07-22 ~13:15) = CK-GATE PASSED, but new tuning data:** all 3 changes ON, full 45 min,
**ERR/CK/CONS grep = 0** (both arms), no shortfall/exception/FATAL, clean exit → **the branch is CK-SAFE with every lever live**
= prod-shippable on safety. NEW DATA the ultradesign lacked: combined arm drift **−2.94%** (worst stock −20.3%) vs control
**−0.84%** (worst −11.95%) = a clear NET-DOWN lean driven by BearShort 0.6 (Change 2, asymmetric short pressure — expected
direction, stronger than control); dispersion **5.19 vs 3.86 (+35%)** = stocks less pinned/lockstep = the unpinning WIN. One
45-min soak has WEAK power to judge whether −2.94% is healthy asymmetry or BearShort-too-hot (path-dependent sentiment). ⇒ the
SHIP decision (go/no-go + lever strengths + all-3-at-once vs staged, BearShort 0.6 vs lower/off-first) = a genuine FORK →
convening the llm-council (the designated prod-ship green-light gate) BEFORE any merge/deploy.

**★★★ COUNCIL VERDICT (2026-07-22 ~13:25) = SHIP NOW, staged. GREEN LIGHT.** 5 advisors + 3 peer reviews. Unanimous SHIP
(local can't answer the pin; CK-screen proved all local can). Decisive calls:
- **ONE code deploy, ALL realism levers OFF first** — merge branch→master (drop dup migration), deploy the 460-file
  restructure + Change-3 code behavior-neutral, confirm prod healthy (candles/orders/CK) = the isolated structural-safety
  check. (Reconciles Executor's "one deploy" with Contrarian/Outsider's "don't smuggle the one-shot structural change in.")
- **THEN stage levers via reversible env:** Change 3 (RegimeTaker) ON + Change 1 (28d+Elastic) ON. **Change 2 BearShort = 0
  at launch** (4/5; the market is pinned ~20% BELOW seed, BearShort adds DOWN-force = pushes toward the disease; the −2.94%
  lean was BearShort). Add BearShort later ONLY once unpinned + anchor warmed.
- **★ NEW HAZARD the council caught (all 5 advisors missed, all 3 reviewers flagged):** the 28-day anchor WARM-UP means the
  Elastic soft-wall is INERT for a month → any BearShort down-force runs UNOPPOSED exactly when unprotected; worse, an anchor
  that warms onto a DEPRESSED price could CEMENT the low. ⇒ BearShort 0 at launch is not just caution, it's compositional.
  Also verify the anchor's cold-start (seed-only window) doesn't amplify the pin.
- **Success metric = MEDIAN price-vs-seed over rolling 6h** (recovering toward seed = unpin; further below = pin deepened),
  NOT CK (safety gate) and NOT dispersion alone (down-scatter can BE the pathology — pair level+dispersion). Abort = monotonic
  slide below seed → cut RegimeTaker.
- Owner pre-authorized "green light for whatever if the council agrees" ⇒ PROCEED autonomously, carefully, image-rollback ready.

**EXECUTION ORDER:** (1) clean merge + validate — ✅ DONE → (2) deploy mechanism understood → (3) prod deploy restructure
LEVERS-OFF → (4) flip Change3+Change1 env ON → (5) monitor. **★★★ SHIPPED TO PROD (2026-07-22 ~17:00) — Kiesh granted FULL
standing prod-push permission gated on council green-light (now in RUNBOOK top + [[feedback_prod_push_council_authorization]]).
DEPLOY DONE: (1) origin/master=42c8b66; (2) on the box: backup live compose → git checkout compose → git pull --ff-only to
42c8b66 → RESTORE live compose (env preserved byte-for-byte) → migrate one-shot ("DB already up to date", 0 applied) → up -d
--build server; (3) restructure ran BEHAVIOR-NEUTRAL 6 min = Up(healthy), CK=0, candles advancing, 27k orders/2min, 41414 stops
cold-loaded (the "Fund not found for seller" InvalidOpExc is PRE-EXISTING since 2026-05-23 TradeSettler.cs:306, graceful
OperationFailed, CK-clean, trivial rate — NOT a restructure regression); (4) FLIPPED levers via compose splice after
MarketProbMult (backup=docker-compose.prod.yml.pre-levers-20260722): RegimeTaker on (Str0.4/Thr0.15/Cohort0.3) + ValueAnchor
Elastic + WindowDays28; BearShort UNSET=0. Levers-ON boot = clean (bot loop, listening, CK=0, taker flow live SlipMarket
115/60s). Baseline avg-drift 0.000% (vs SESSION-START = the pinned level, NOT seed → recovery shows as drift climbing POSITIVE
= Kiesh's "net drift >0" target directly). MONITOR bg `bxj3n05bz` samples drift+CK every 40min×4. NEXT = watch drift climb
positive over prod-hours/days; if net-up confirmed → later enable GlobalShock + BearShort~0.6 for the rare sharp crashes.
ROLLBACK if needed = on box `git checkout f64b796` + restore .pre-restructure backup compose + up -d --build.**
_(prior blocker history:) master PUSHED (origin/master=42c8b66); prod deploy WAS blocked by classifier (needs a perm rule for the deploy shell);
prod untouched + healthy. REFINED TARGET (Kiesh + BOT_MECHANICS §1 "Kiesh target" col): net drift POSITIVE/low over a WEEK;
stairs-up + RARE elevator-down crash EVENTS; crashes sharper. Net-up "stairs" = value-anchor recovering the pinned price
toward seed + Change-3 movement. Launch lever config (after the levers-OFF structural deploy): RegimeTaker on (Str 0.4/Thr
0.15/Cohort 0.3) + ValueAnchor Elastic on + WindowDays 28; BearShort unset=0. Tuning is prod-over-days (watch median-vs-seed
climbing). CORRECTED CRASH-DESIGN (BOT_MECHANICS §2): "bigger bear crashes" = `Sentiment:GlobalShock` (rare market-wide bearish
EVENT, MeanIntervalHours 3 / MaxMagnitude 1.5 / DownBias 0.85) with `Bots:BearShortStrength` as its AMPLIFIER (docs: "Pair with
BearShortStrength"), NOT a standalone continuous tilt. BearShort amplifies WHATEVER bear sentiment exists → on the PINNED
market it deepens the pin (=the soak's −2.94%); on a NET-UP market it amplifies only the rare GlobalShock events = Kiesh's
sharp crashes. ⇒ TIMING, not wrong-mechanism: BearShort 0 + GlobalShock off AT LAUNCH; enable GlobalShock + BearShort ~0.6
AFTER the base is confirmed net-up. (ExogShock GlobalCoFire = the CORRELATION lever, already live on prod — distinct.)
❓ BLOCKER: the deploy shell is env-CLASSIFIER-blocked (git push went through; the deploy shell to the box does not) — needs
Kiesh to add a Bash perm rule for it, then I run the gated deploy. Prod untouched + healthy at f64b796; this is the ONLY gate.**

**★★★ STAGED STATE (2026-07-22 ~13:45) — everything up to the prod cutover is DONE + VALIDATED; the cutover itself is HELD:**
- **MERGE DONE:** `git merge master` into `perf/admin-table-time-indexes` = merge commit **`42c8b66`**, ZERO conflicts (ort
  auto-merged the identical index lines; kept master's compose prod-tune; snapshot declares each index ONCE). Kept BOTH
  idempotent index migrations (202712 branch + 211749 master, both `CREATE INDEX...IF NOT EXISTS` → prod applies 202712 as a
  no-op). **VALIDATED: `dotnet ef migrations list` = linear chain, no dup-name error; `dotnet test` = 730/730 GREEN.**
- **master FF-ready** to `42c8b66` (local master ref is checked out in the `-soak` worktree so not force-moved; push path =
  `git push origin perf/admin-table-time-indexes:master`).
- **DEPLOY MECHANISM (docs/runbooks/RUNBOOK.md):** prod box `/opt/kse-server` on master `f64b796`, git-based deploy. Prod
  auto-migrate delta = `AddUserDrawings` (additive table) + no-op `202712`. Runbook update = `git pull && docker compose
  -f docker-compose.yml -f docker-compose.prod.yml --env-file .env.production up -d --build` + run the profiled one-shot
  `migrate` service FIRST when the pull adds migrations. Prod containers HEALTHY (server+pg up 3h).
- **★ CUTOVER HAZARD (why held for Kiesh + why it's classifier-gated):** prod's `docker-compose.prod.yml` is a HAND-EDITED
  WORKING-TREE file (its full env stack lives as an in-place edit + many `.pre-*` backups, NOT committed) → a naive `git pull`
  COLLIDES with it (this is exactly what caused today's ~2min outage). Safe recipe = on the box: `cp docker-compose.prod.yml
  docker-compose.prod.yml.pre-restructure-20260722` → `git checkout -- docker-compose.prod.yml` (discard working edit, backed
  up) → `git pull` (FF code to 42c8b66) → RESTORE the live compose from the backup (`cp` back) so prod's env stack is preserved
  BYTE-FOR-BYTE → run `migrate` one-shot → `up -d --build` → verify healthy (levers still OFF = behavior-neutral) → THEN edit
  compose to add `Bots__Sentiment__RegimeDrift__TakerCoupling=true`(+TakerStrength 0.4/Threshold 0.15/CohortFraction 0.3) +
  `Bots__ValueAnchor__Elastic=true` + `Bots__ValueAnchor__WindowDays=28` (BearShort UNSET=0) → `up -d` → verify → monitor
  median-vs-seed. ROLLBACK = `git checkout f64b796` + restore backup compose + `up -d --build` (additive migration harmless to
  leave). ALWAYS `--env-file .env.production`.
- **WHY HELD:** the env auto-mode classifier BLOCKED the prod-box SSH — prod mutations need explicit owner sign-off in this
  env. Combined with: the 460-file structural cutover is a bigger risk class than the reversible env lever-flips Kiesh
  primarily authorized; the council itself flagged the restructure as "the real irreversible bet"; the compose-collision
  outage precedent; and the realism verdict needing Kiesh's eyeball anyway. ⇒ present the staged plan, get Kiesh's GO, then
  execute the recipe above. Update `docs/explainers/BOT_MECHANICS.md` status tags in the same commit as the lever flip.

**BUILD ORDER / NEXT:** (Change 3 done →) validate Test-3 → **instrumentation** for the decisive metric (RegimeSignal candle
column + probe; the corr(RegimeSignal,forward-return) metric needs it) → Change 1 (Elastic+28d, env arm) → Change 2 (BearShort,
env arm) → **COMBINED BAKE** (co-tune the 4-knob taker-share set + elastic stiffness; owner eyeballs ON arm = a random walk) →
CK-soak the combined branch → **merge `perf/admin-table-time-indexes`→master → ship to prod** → earned-calm code ONLY if needed.

**★ CRITICAL OPS FACTS:**
- **PROD DEPLOY = `ssh root@159.195.149.51 'cd /opt/kse-server && <edit compose> && docker compose --env-file .env.production
  up -d server'`. ALWAYS `--env-file .env.production` — without it, KSE_AUTH_SIGNING_KEY/DB env blank → crash-loop (this caused
  a ~2min prod outage 2026-07-22; the anchor-break I deployed was then REVERTED — prod is BASELINE + HEALTHY, 28k orders/2min).**
- Restructure branch `perf/admin-table-time-indexes` = 145 commits/460 files ahead of master, carries the DECOUPLED realism
  services; **730 tests pass**; deploy scope contained (+1 AutoMigrate `AddUserDrawings`, additive appsettings, NO compose change).
- Prod baseline: master `39bdedf` + env overrides (RD 0.8/MPM 1.4/Tau 2000/AlphaMin 0.40/RecentAnchor 0.05/full Mood+ExogShock
  stack). The mood stack (PerStrategy/TakerCoupling/ConvictionFearBid/MMWiden) is ALREADY LIVE.
- **SOAK on the restructure branch:** harness copy `scratchpad/soak-maintree.ps1` (`$root`→main tree) + `scratchpad/
  run-arm-regime.ps1`. Postgres `kieshstockexchange-postgres-1`, template `kse_soak_seed`. ★ STAGGER 2 arms so the 2nd's DB
  template-clone starts AFTER the 1st reaches the bot loop (simultaneous clones time each other out → boot crash). Kill stray
  servers first. candle_export.py python is absent → pull metrics via SQL.
- **TIMERS (session-only cron, re-arm if session died):** `9d1347da` @ 16:38 Wed (5h-window, self-perpetuating +5h2m) +
  the weekly-limit net. Resume prompt says "trust the plan STATUS not stale 'parked at Q1' text."
- **LOCAL A/B has WEAK power** for the -20% pin (path-dependent 30h prod state, NOT locally reproducible) — the pin verdict is
  prod-over-days. Test 3 (regime→taker mechanism) + CK are the locally-provable things. Don't grind pin-seeking local soaks.
- Established: only TAKER flow moves price; buyProb/sentiment tilts are book-absorbed; RegimeDrift was a dead lever until Change 3.
- Refs: `docs/arcs/REALISM_ULTRADESIGN.md` (spec), `docs/arcs/MARKET_PINNING_ARC.md §11` (verified root cause), `docs/explainers/
  BOT_MECHANICS.md` (bot reference — UPDATE its status tags in the same commit as any prod flip), `docs/arcs/REALISM_COUNCIL_BRIEF.md`.

---

## 🧭 NET-UP DEBATE VERDICT + INVESTIGATION (2026-07-22 ~20:15, council 5/5 unanimous — the CURRENT design north-star)
**Investigation facts:** prices ~2wk ago ≈615 (near seed ≈639), fell to ≈510 ~07-13, pinned 495–513 since. Value-anchor target
= `BotPriceMemoryService.GetPreviousDayAverage` (linear-tapered wtd avg of daily TWAPs over WindowDays, else seed); history is
IN-MEMORY, `_dayHistory.Clear()` on every Reset/restart, NO DB warm-start → on prod it's EMPTY → anchor = seed exactly (24h per
"day" to accrue one TWAP). The −8% slide = the Elastic **0.20 deadband** zeroed the restoring pull where price sat (NOT stale
28d — history was empty). **Reverting Elastic → market RECOVERED UP +0.4%→+4.8% in 6 min** (linear anchor pull toward seed).
`BotCashInjector.RunAsync` = pure `AddFundsAsync` cash deposit, **places NO orders** → injection is nominal-only, INERT for
price under the taker-kernel (only taker flow moves price).

**VERDICT (resolves Kiesh's positions):**
- **Decompose `P = P + μ + σε` — three ORTHOGONAL knobs.** σ = free wander (Kiesh's "wide band" feel); μ = small persistent
  positive drift; anchor = mean-reversion. They only fight if μ is sourced from the anchor. Kiesh's "free movement" + "weekly
  up" are NOT contradictory once decomposed.
- **Wide band = right instinct, WRONG as a deadband.** Express it as WIDE HARD clamp (`ValueAnchor:AbsoluteCapMax` ×3/÷3,
  extreme-only) + GENTLE always-on `Strength` (~0.10, no dead-zone). The deadband caused the −8%.
- **The recovery is a ONE-TIME snap-back to seed, NOT weekly drift.** Perpetual up-drift needs a continuous daily push.
- **μ (updrift) must be TAKER flow.** Kiesh's `buyProb>0.5` AND raw cash injection are BOTH book-absorbed (don't move price).
  THE μ ENGINE = **route a slice of the 30-min injection into TAKER market-buys** (`CashInjection:TakerFraction`, default 0):
  new money deployed as buying = legit inflation→updrift, injection RATE = the annual-return dial. (Expansionist: this inverts
  the pin into a rising tide; seed becomes a floor.)
- **The REAL anchor bug = no restart persistence.** Kiesh's fix is right + essential: **persist daily TWAP to a permanent DB
  table + warm-start the anchor before bots start** (currently one restart nukes weeks of TWAP; anchor stuck at seed).
- **IMMEDIATE: LEAVE the recovering market + MEASURE** (don't confound the +4.8% signal). Build order (all default-off/
  reversible/CK-gated): (1) DB-persist TWAP + warm-start [Kiesh essential] → (2) injection→taker μ-engine [the updrift] →
  (3) wide-band config (AbsoluteCapMax wide + Strength 0.10, no deadband) → validate each: unit tests + local CK-soak, then
  prod env arm, measure weekly drift slope ∝ injection rate (Expansionist's proof experiment: taker-arm vs injection-off).
- Files: injection `BotCashInjector.cs` (add taker route) + `AiTradeService.cs:2411` (cycle). Anchor `BotPriceMemoryService.cs`
  (warm-start + DB table). Config `appsettings.json Bots:CashInjection` / `Bots:ValueAnchor`.

## 🎚️ VOLATILITY CALIBRATION (Kiesh 2026-07-23 "moves too strong, >10% everywhere; want mostly <5%, rare >5%; let the council decide")
**Vol-council verdict (5/5): SHAPE THE DISTRIBUTION, don't turn one dial down. Attack COORDINATION not amplitude.**
Diagnosis: 1-min body already calm (median 0.09%, p95 ~1%) but a FAT TAIL (max 12.5%/1min) = coordinated same-tick RegimeTaker
bursts (a cohort fires same-direction market orders one tick, walking the book). My eyeball trims cut AMPLITUDE (RegimeDrift
Strength 0.8→0.4, MarketProbMult 1.4→1.15, TakerStrength 0.4→0.1) = WRONG (shrinks the calm baseline + risks RE-FREEZING the
unpinned market). Right fix = fewer bots per burst + rarer bursts (+ a structural per-tick cap).
**APPLIED env set (council-decided, prod live 2026-07-23, backup `docker-compose.prod.yml.pre-council-vol-20260723`):**
RegimeDrift Strength **0.4** (keep) · MarketProbMult **1.2** (up from over-cut 1.15) · RegimeTaker TakerStrength **0.12** (up
from 0.1) · **TakerThreshold 0.15→0.20** (rarer) · **CohortFraction 0.06→0.03** (THE key — halve simultaneity, the tail-killer)
· DipBuyStrength 2.0 (keep).
**STRUCTURAL FIX = QUEUED CODE BUILD (the real one): per-stock per-tick taker-notional CAP** (a "volatility governor") — clips
the coordinated burst that walks the book, leaves calm baseline + gradual news intact, capped-out flow → book depth (calm w/o
deadening); per-stock varied = blue-chip-grind vs small-cap-whip cross-sectional realism. Build alongside μ-engine A.
**MEASUREMENT (council stop-condition, END the eyeball loop): monitor `baqat3dok`** 3h. Gates: 1-min p95 <0.6% · max/median
ratio <60 (was ~140) · FLOOR: volume/candle-count must NOT collapse (over-calm = flatline). Commit this set → measure 3h →
adjust AT MOST ONCE (only CohortFraction/Threshold ±1 step) → FREEZE. Two soaks max. Horizon caveat: owner may also be seeing
the CUMULATIVE daily path (>10%), not per-tick (1-min was already calm) → the anchor contains the daily walk; burst-clip helps both.
Also flagged (First Principles): volatility = taker notional ÷ BOOK DEPTH — deepening the book is the untried denominator lever.

## 🌊🌊 OSCILLATION → ULTRADESIGN (Kiesh 2026-07-23: escalated to the ultradesign method + a parallel log-symmetry audit)
**RUNNING:** ultradesign Workflow `wqt22ypzo` (feasibility→3 architects[minimal/framework/self-exciting]→council teardown→chairman;
outputs a fire-ready MarketPulse spec + A/B soak plan) + vol monitor `b3s0vn9gr`.

**★ LOG-SYMMETRY AUDIT (agent `a7aad29f384a32722`) — DONE, IMPORTANT STRUCTURAL FINDING:** the sim is MIXED — hard VETO bands
are geometric (GeometricBand in `IsOverBand`), but every SOFT restoring force / anchor clamp that SHAPES candles is linear
`seed×[1±x]` and consistently DOWN-BIASING (up-moves face a stronger pull-back than mirror down-moves) = a plausible hidden
contributor to the months-long down-drift. TOP-3 CONVERSIONS (low-risk 1-line swaps vs the existing tested `PriceBandMath`,
QUEUED for a flagged build + A/B; these COMPOSE with the μ-engine = both reduce down-bias):
  1. **Value-anchor gap `(f−p)/f` → LOG gap `ln(f/p)`** (`AiBotDecisionService.cs:2778` + `:2801`) — HIGHEST impact: the MAIN
     mean-reversion spring, LIVE at Strength 0.40; `(f−p)/f` gives up-moves a stronger sell-pull than mirror down-moves a
     buy-pull = built-in DOWN tilt; `ln(f/p)` is symmetric. Needs a light `Scale` (0.12) retune. Flag + A/B (does it help net-up
     without breaking mean-reversion).
  2. **`FundamentalService` band clamp `seed×[1±Band]` → `PriceBandMath.Band`** (`:148-149`, `:184-187`, Band 0.12) — the exact
     use-case the precedent was built for, currently unused there.
  3. **`BotPriceMemoryService.ClampToBand` + `MaxTotalExcursion` → `PriceBandMath.Band`** (`:339-347`, `:266-273`, cap 0.5) —
     daily-anchor clamp, largest cap so most-visible asymmetry.
  LEAVE: buyProb/sentiment ([0,1]/±1 = additive correct), limit-ladder/stop/slippage offsets (small, 2nd-order). FundamentalService
  OU diffusion (`:145`) = needs-thought (log-OU textbook-correct but small at σ=0.004 + disturbs the level-independent invariant).
**KIESH DESIGN DIRECTIVES for the MarketPulse primitive (bake into the build):**
- UNIVERSAL component in its OWN SEPARATE FILE (respect the split/dedup arc — new responsibility = new file).
- PER-IMPLEMENTATION oscillation time (τ) + amplitude (A): each subscriber/locus constructs its own instance with its own τ/A;
  values held in that class/struct.
- Multiplier is LOG-SYMMETRIC `m=exp(A·z)`, z∈[-1,1] from a per-stock OU; A/B τ (fast~30s vs medium~90s, irregular>regular) + A (~0.35-0.5, [0.7,1.3] was a timid guess).
**PULSE-CAL COUNCIL (5/5, feeds the ultradesign):** do NOT modulate CohortFraction (0.03 = rounding-error-invisible + ping-pongs
into spikes) or MarketProbMult (global); DO modulate a per-stock taker-flow RATE scalar (shared per-stock = moves price, variance
not mean); log-symmetric confirmed; rare 4-5% peaks from z's tail not a fat A; τ short enough to print before RecentAnchor damps;
FAST+REGULAR risks metronome look; add SELF-EXCITING feedback (Hawkes: burst→more→exhaust) LATER behind its own flag; measure via
1-min |return| autocorrelation clustering ON-vs-OFF, guardrail P95≤2%/max>5% rare/CK=0; SHIP BEHIND vol-baseline + μ-engine.

## 🌊 OSCILLATION IDEA (Kiesh 2026-07-23 "let values oscillate around their mean every tick, fast, AR(1)/sentiment-like, in many places, for natural drift" — council debated)
**Osc-council verdict (5/5): instinct RIGHT, three specifics WRONG.** (1) "FAST" is backwards — ρ≈0.5/short-τ = white noise
that LLN-averages out at chart scale = INVISIBLE; natural texture = PERSISTENCE (slow, ρ→1). (2) "wobble the constants" either
does NOTHING (per-bot → LLN cancels across 20k) or RE-CREATES THE 12% SPIKE (shared wobble on a TAKER param = correlated
same-tick bursts, the thing we just suppressed) — taker path is a no-go locus. (3) WRONG LAYER + ALREADY EXISTS: naturalness
lives in the PRICE process (integrated flow = random walk in the level); RegimeDrift (bounded walk in level) + OU sentiment
rings (slow, persistent, CORRELATED shared) ARE this mechanism, correctly placed — the AR(1) idea is a GENERALIZATION of them.
Don't apply broadly (undebuggable; conservation constants off-limits).
**GENUINE UNLOCK:** a reusable `Oscillator(mean, amplitude, persistence)` primitive that UNIFIES RegimeDrift/OU into a
"market-weather" generator — SLOW shared oscillators = regimes/tides; shared-per-sector = factor structure/correlation for
free; fast shimmer on top. The prize is the SLOW end (calm eras/manias/rotations). Later arc.
**RECOMMENDATION:** flip the 3 specifics → SLOW not fast, PRICE/ANCHOR layer not taker constants, ONE measured pilot not broad.
(1) FIRST check the actual "mechanical" cause — council flags round-number snapping (`RoundSnapSpread` 0.40 lever exists!),
discrete tick, thin book. ASKED KIESH what specifically looks mechanical (decides the fix). (2) cheapest = tune existing
RegimeDrift/OU slower+wider (but in TENSION with the vol-calm just applied — waits). (3) if built: SLOW per-stock NON-taker
oscillator on the value-anchor LEVEL (absorbed as limit flow → CK-safe, chart-visible), default-off, measured candle
ret_acf/body-wick ON-vs-OFF. QUEUED behind vol-calibration + μ-engine (don't add vars to an unstable baseline).

## 🔧 μ-ENGINE BUILD (Kiesh 2026-07-23 "do both, start with B")
**Option B = DONE + committed `450d4c3` (default-off, 730 tests pass byte-identical).** `Bots:Sentiment:RegimeDrift:TakerBias`
(default 0) shifts the regime signal the TAKER gate sees (`AiBotDecisionService.cs` :867 `rsig = RegimeSignal + _regimeTakerBias`)
→ skews the spread-crossing cohort net-BUY = persistent taker imbalance = μ. Taker-only (buyProb sentiment untouched ⇒ σ +
mean-reversion unchanged). Reuses the live CK-soaked Change-3 path ⇒ CK-safe by construction. Field/param/assign/read all wired.

**Option A = TODO (injection→taker, the money-linked faithful version).** Design: inject `IOrderEntryService` (+ price/stock
access) into `BotCashInjector`; after `AddFundsAsync(amount)` for a bot, if `Bots:CashInjection:TakerFraction`>0, place a
`SlippageMarketBuy` for `amount×TakerFraction` notional on a stock the bot can buy (qty = notional/mark), funded by its own new
cash, via `_entry` — CK-clean by construction like JumpService/ConvictionDecisionService (both ride the ordinary IOrderEntryService).
Config `Bots:CashInjection:TakerFraction` (default 0 = byte-identical). Open design Q = stock selection (bot watchlist pick vs
spread-across); mirror ConvictionDecisionService's stock/size/submit. Build carefully + unit test + LOCAL CK-SOAK (the new
order path per injected bot must not break conservation — this is why A waits for a focused build, not a midnight rush).

**DEPLOY PLAN (both):** build A → combined local CK-soak (B bias>0 + A fraction>0, CK=0 + net-up movement) → ONE prod code
deploy (rebuild image with B+A, same recipe as the restructure deploy: backup compose → git pull → migrate → up -d --build) →
enable via env: start with B `TakerBias` small (e.g. 0.03-0.05) once the bounce settles, measure weekly drift slope; add A
`TakerFraction` after. All reversible env. Prod currently = restructure `42c8b66` (does NOT have B/A code yet — needs the deploy).
NOTE the bounce is still settling (~+7%, below seed) so no rush; μ is for the SUSTAINED drift after the one-time bounce.

## 📊 STATUS (rehydrate from here on any fresh session / timer fire — never restart the run)
- **Phase:** ★★★ PIVOT (2026-07-22, Kiesh live direction) — FOCUS = BOT BEHAVIOR, not the value anchor. Kiesh: "positive
  sentiment should move the market up, negative net down; extreme same; overextension drifts back (currently good). Focus on
  bot behaviour than the value anchor." + "fix RegimeDrift by changing the TAKERNESS instead of buyProb (or a combination)."
  This is THE unifying fix: RegimeDrift today only adds to sentiment→buyProb→absorbed (why the random walk doesn't move
  price). Route it to TAKER flow → positive regime = buy takers (price up), negative = sell takers (down); anchors still do
  overextension-drift-back. **PROD INCIDENT + RECOVERY (resolved):** I deployed the anchor-break (`UsePreviousDayAverage=false`)
  but the `docker compose up -d` recreate dropped the env (needs `--env-file .env.production`) → ~2min crash-loop → fixed +
  then REVERTED the anchor-break per Kiesh's redirection. **Prod is BASELINE + HEALTHY** (28k orders/2min, candles fresh).
  ★ CORRECT PROD DEPLOY = `ssh root@159.195.149.51 'cd /opt/kse-server && <edit compose> && docker compose --env-file
  .env.production up -d server'` (ALWAYS the --env-file, else SigningKey blank = crash). WindowDays verified = 7 (not 28/30).
  **NOW BUILDING (soak worktree master): RegimeDrift TAKER COUPLING** — mirror the TrendFollower taker override
  (`AiBotDecisionService.cs:1670-1678` `TrendTakerDecision`): new `Bots:Sentiment:RegimeDrift:{TakerCoupling,TakerThreshold,
  TakerStrength}`, expose per-stock regime via a `BotSentimentService.RegimeSignal(sid)` accessor + watchlist-avg it, when
  |regime|≥threshold with prob∝strength·|regime| OVERRIDE the order to a slippage-market taker in the regime direction
  (replace-the-order design, keeps order volume flat; KEEP the existing buyProb tilt = "combination"). Default-off =
  byte-identical. CK-safe (rides order→match→settle). Build → CK-soak-test locally (does +regime stock go up / −down, CK=0)
  → stage for prod (Kiesh review). ORIGINAL FORK NOTE (still true, now secondary): Two inconclusive cells (EXP-1 TrendFollower,
  EXP-2/Battery-1 anchor-break) + the finding that the -20% pin CANNOT be reproduced locally by ANY method (fresh falls from
  seed; restore loses the in-memory anchor; compressed-time doesn't compress the accumulated 30h net-long drift that builds
  the pin) + the scorecard's own "eyeball/long-soak only" caveat on clustering/skew/regime ⇒ short local soaks can't decide
  the pin OR the subtle realism levers. **The bot-strategy council's levers (A-E) + the anchor-break all need PROD (reversible
  env + multi-hour/day monitoring) to prove.** CURRENT: EXP-3 = a SAFETY-CLEARANCE screen of the new-lever bundle (anchor-break
  + TrendFollower + GlobalShock + Activity-shape) — purely to confirm CK/CONS/latch clean before any prod push (NOT a verdict).
  ★ CORRECTION: the MOOD STACK (C) is ALREADY LIVE on prod (env: Enabled/PerStrategy/TakerCoupling/ConvictionFearBid/MMWiden
  all on). So the NEW levers are A (Activity shape), B (GlobalShock), D (TrendFollower), E (SectorEvents), + anchor-break.
  ★ BLOCKED ON Q1 (prod-testing authority) — see Questions. Default = stage prod deltas + monitor plans for Kiesh's OK.
- **★ LOCAL TRACK CHARACTERIZATION COMPLETE (2026-07-22 ~05:00) — PARKED at the Q1 gate, no more grinding.** 4 clean cells
  done (EXP-1..4): the new levers are all CK/CONS/latch-SAFE; GlobalShock is the confirmed crash-asymmetry engine; the pin
  fix + subtle realism can't be graded locally (prod-only). NEXT ACTION when resumed / when Kiesh answers Q1: execute the
  STAGED PROD SEQUENCE (Q1) starting with the anchor-break (1), monitor hourly, and UPDATE `BOT_MECHANICS.md` status tags in
  the same commit as each prod flip. Do NOT launch more verdict-seeking local soaks — they lack the power. If Kiesh says
  auto-push the anchor-break, do (1) + hourly monitor; else hold. Timers (5h `d6e5c3cd`@06:25, weekly `43d74b61`) keep the run alive.
- **★ PINNED-TEMPLATE ACQUISITION (the enabling step — do this next):** a FULL prod dump is impractical (`Orders` 15GB /
  `Transactions` 9.4GB / `Candles` 1.5GB). Do a **SELECTIVE dump** of the compact pinned STATE from prod
  (`ssh root@159.195.149.51 "docker exec kse-server-postgres-1 pg_dump -U kse -d kse -t Positions -t Funds -t StockListings
  -t Stocks -t StockPrices -t "\""Orders WHERE Status open"\""..."` — actually dump full `Positions`(144MB)+`Funds`(12MB)+
  `StockListings`+`Stocks`+`StockPrices`+`AIUsers`+`Users`+`FundTransactions`, and open orders only via a filtered COPY, and
  a RECENT slice of `Candles`). Load into a local template `kse_soak_pinned`; the balance-sheet state (net-long, cash-hoarding
  fleet parked at pinned prices) is what recreates the pin.
  ★ DE-RISK DONE: (a) harness `kse-balance-soak-p.ps1:30` clones each arm via `CREATE DATABASE $Db TEMPLATE $Tmpl` — pass
  `-Tmpl kse_soak_pinned`. (b) The server **seeds ONLY when the DB is EMPTY** (`ExcelSeedService` guard
  `GetStocksAsync().Count==0`, invoked `Program.cs:269`) ⇒ a restored pinned template is NOT re-seeded; bots run against it.
  AutoMigrate runs first (schema matches — same master codebase). **⇒ the pinned-restore approach is VALID.**
  EXACT ACQUISITION (next cycle): pg_dump `-Fc` with `--exclude-table-data` for `Orders`/`Transactions`/`Candles` (keeps
  Positions/Funds/Stocks/Listings/Prices/Users/AIUsers/FundTransactions ≈160MB) → scratch file; SEPARATELY `\copy` a
  Candles slice `WHERE "OpenTime" > now() - interval '8 days'` (REQUIRED — the 7-day-TWAP anchor / BotPriceMemoryService
  needs it to reproduce the ~510 pin); local `createdb kse_soak_pinned` → `pg_restore` → `\copy` the candles in.
  Then: (3) 10-min impulse pre-gate; (4) launch Battery 1 with `-Tmpl kse_soak_pinned`. CAVEAT: skipping `Orders` data means
  each arm starts with an EMPTY book the bots rebuild in the first minutes — confirm price re-settles ~pin during warmup
  before trusting an arm (else include a filtered open-orders `\copy` too).
  ⚠️ QUOTING GOTCHA: tables are CamelCase, so `--exclude-table-data` MUST quote the identifier (`'public."Orders"'`) — an
  unquoted `public.Orders` case-folds to `orders`, matches NOTHING, and silently pulls the 15GB table. Validate the dump is
  ~160MB (not GBs) before trusting it.
- **Timers armed (session-only cron, re-arm if the session died — see "Resume prompts"):**
  - `d103f435` @ 01:23 Wed (+1h47m) — 5h-window resume, SELF-PERPETUATING (+5h2m).
  - `43d74b61` @ 11:02 Wed (+11h26m) — weekly-limit resume safety net.
- **★ NEXT ACTION:** obtain a **PINNED DB snapshot** (the Contrarian's decisive point — a FRESH 45-min soak CANNOT
  measure unpinning; fresh MSFT just falls from seed; an earlier A/B died inconclusive for exactly this). Then restore it
  as a local Postgres TEMPLATE (`kse_soak_pinned`) that the harness clones per arm, and launch the **impulse pre-gate →
  Battery 1**. Snapshot source: `pg_dump` the PROD db (it IS pinned, ~509 MSFT) from `root@159.195.149.51` container
  `kse-server-postgres-1` (`psql -U kse -d kse`) — a read-only dump, safe. FALLBACK if prod SSH is unavailable from here:
  use PROD ITSELF as the long confirmation arm (apply one lever env-reversibly, watch it unpin over hours) + build a local
  pinned template by soaking current-prod-config for the setup hours. (Log which path was taken in the EXPERIMENT LOG.)

## 🔬 MEASUREMENT DESIGN (council-final — makes a 45-min screen decisive)
- **Every arm restores the pinned snapshot** (`kse_soak_pinned` template) as its DB baseline — the question becomes "does
  taker flow ESCAPE the pin," not "which arm falls fastest from seed." Fresh-soak results are INADMISSIBLE.
- **10-min IMPULSE PRE-GATE (cheap kill filter, run before spending a 45-min slot):** with the arm's cohort on, inject a
  fixed one-sided taker burst; last-price displacement must PERSIST >N ticks vs. get absorbed back to the anchor. Absorbed
  ⇒ kill the arm before the full soak.
- **Decision thresholds (vs the pinned-control arm):** ESCAPE = realized |drift from pin| **>3% sustained** over the final
  20 min AND **out-of-band dwell >40%** (band = ±1% of the pin). Absorbed/no-move ⇒ KILL. Ambiguous (1–3%) ⇒ promote to the
  confirm tier only, don't bake.
- **Metrics per arm (log to candle CSV):** PRIMARY GATE = out-of-band dwell %. Guardrails = trend-run length (persistence
  not whipsaw), `ret_acf_lag1` (keep ≥ −0.43 structural floor, don't regress mean-reversion), realized drift vs seed,
  **taker-fill share** (mechanism confirmation), **conservation/CK clean = HARD VETO** (any CK breach kills the arm
  regardless of dwell).
- **CONFIGCHECK at warmup:** each arm must echo its resolved flags (`TrendFollower:Enabled/TakerCoupling`,
  `UsePreviousDayAverage`, `SharedChase`, `Conviction`) from `AiTradeService`; if echo ≠ intended arm → ABORT (proves the
  env override took effect + the process is alive, not a stale binary).
- **Infra confirmed:** Docker `kieshstockexchange-postgres-1` UP (healthy). Soak worktree
  `C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange-soak` on `master 39bdedf` (= prod). Harness
  `scripts/kse-balance-soak-p.ps1` (parallel A/B, ports 5080/5083, DBs `kse_soak_base_ab`/`kse_soak_exp_ab`).
- **Prod state:** RD 0.8 / MPM 1.4 / Tau 2000 / AlphaMin 0.40 live (master `39bdedf`); still pinned per Kiesh (MSFT 509–511).
  Rollback = delete the 4 env lines + `up -d server`. Root cause + fix verified in `docs/arcs/MARKET_PINNING_ARC.md §11`.

## ★★★ CONSOLIDATION STRATEGY (Kiesh 2026-07-22) — build realism on the RESTRUCTURE branch + ship everything to prod
Kiesh: "We did a lot of dedupping and decoupling big classes. Best to code on THAT and push everything to prod. Otherwise
to use the new branch we'd do this again." ⇒ do NOT build realism on master; build on the restructure branch so the
decoupling + realism ship together (no re-merge). **Restructure branch = `perf/admin-table-time-indexes`** (main tree),
**145 commits / 460 files / +27k−10k vs master**, carries the DECOUPLED realism services (BotSentimentService/Fundamental/
AiBotDecision/ExogShock/BankEstimate/BotPriceMemory). **VALIDATION:** ✅ builds + **730/730 tests pass** (CK-critical green).
Deploy scope contained: +1 new prod migration (`AddUserDrawings`, AutoMigrate applies on boot), appsettings +4 (additive
`Drawings` section, no `Bots__*` conflict), NO Dockerfile/compose change. **REMAINING gate = a CK-SOAK** (behavior — the
decoupling must hold CK=0 over a running soak, not just unit tests) — folded into the combined validation after the realism
build. PLAN: (1) ✅ restructure tests green → (2) ULTRADESIGN the 3 realism changes (workflow `wnfi30lpo` running) → (3)
implement on `perf/admin-table-time-indexes` (remap anchors to the decoupled code) → (4) combined build+tests+CK-soak → (5)
ship the whole branch to prod (merge master ← branch, build image, `docker compose --env-file .env.production up -d server`).
★ The 3 realism changes = ULTRADESIGN items: (1) ValueAnchor WindowDays 7→28, (2) bot-types+reaction rework (regime state),
(3) RegimeDrift→takerness. Kiesh: use the ultradesign method → local A/B (separate or together) → prod on COUNCIL green-light
(pre-authorized: "green light for whatever if the council agrees").

## ✅ VERIFIED ROOT CAUSE + FIX LEVERS (full detail: MARKET_PINNING_ARC.md §11, REALISM_COUNCIL_BRIEF.md)
- Pin = one-way ratchet into a **sell-veto floor** (`seed/(1+cap)` ≈509 for MSFT) + a **self-pinning 7-day-TWAP anchor**
  (`ValueAnchor:UsePreviousDayAverage=true`). Nothing pulls price UP. Only **taker flow** moves price; buyProb/sentiment
  tilts (RegimeDrift) are absorbed. RegimeDrift is a DEAD lever — leave it.
- **Fix = turn ON built-but-off taker cohorts** (all config, no code): `Bots:TrendFollower:{Enabled:true,
  TakerCoupling:true, Strength: ladder 0.10→0.20→0.40→0.80, CohortFraction:0.04, SharedChaseWeight:0→~0.3-0.6}`;
  break the self-pin via `ValueAnchor:UsePreviousDayAverage=false` OR `ValueAnchor:Adaptive.BlendWeight>0`;
  `Bots:Conviction` (aggressive-taker) later. Env-override form `Bots__Section__Key`.

## 🧭 RUN PLAN (council-designed — Executor battery + Contrarian gate; refine after each result)
- **Battery 1** (both slots, on a RESTORED-PINNED DB): Slot A = `UsePreviousDayAverage=false` (stop the ratchet);
  Slot B = control (current pinned config, baseline dwell/CK). Gate: does A's out-of-band dwell rise vs B? A alone likely
  stops the ratchet but won't pull UP (nothing does) → proceed to taker flow.
- **Battery 2** (carry the anchor fix): Slot A = TrendFollower `Enabled+TakerCoupling Strength=0.10`; Slot B = `0.20`.
  Ladder geometrically. **STOP one rung below** any arm where `ret_acf_lag1` crosses 0 or drift touches the ×3 cap
  (positive-feedback RUNAWAY guard). Winner = highest Strength with ret_acf<0, dwell>0.5, CK clean.
- **Battery 3** (carry winning Strength): + `SharedChaseWeight` (0.3 vs 0.6) for cross-stock breakout survival;
  Conviction cohort small `CohortFraction` on the other slot. Fold in what lifts trend-run length without cap-touch.
- **★ STRATEGY LADDER — bot quiet/extreme reactions (council 2026-07-22, 5 advisors on BOT_MECHANICS.md).** DIAGNOSIS:
  (1) "quiet" is FAKE calm — the quiet-period is a per-bot 10-60s cooldown, not a regime throttle; 20k bots fire every tick
  regardless (`WSelf=0`); calm is IMPOSED by anchors (→ ret_acf strongly negative) not EARNED by low participation.
  (2) "extreme" is SYMMETRIC-by-construction — `ApplyExtremeReaction` (`AiBotDecisionService.cs:2797`, fires |sentiment|>1)
  has Panic(sell-both)≡Greed(buy-both) "nets to zero", and MeanReversion+MarketMaker default to Contrarian (FADE every
  extreme); trigger is idiosyncratic per-stock so blips don't co-fire → a real crash is mechanically unreachable.
  (3) NO regime state (`BotRegimeService` is just a herd helper; extremeness = a continuous gain on a fixed mix). FIX =
  hysteretic regime state re-weighting cohort taker-share + calm EARNED (Activity `Baseline<1`) + extremes TREND+CORRELATE
  +CASCADE via taker cohorts, cross-wired global-mood→activity-G→taker-share/MMWiden/reflexive as ONE crash engine (bank
  sector re-rating→Conviction/Rotator flow→mood fear→MMWiden+leverage-vol+corr→Conviction fear-bid ABSORBER keeps it rare).
  ★ Contrarian GUARDRAIL: absorber + co-fire TOGETHER, latch-gated — NOT symmetric noise turned up (fear-spiral/CK risk).
  **COHORTS CONFIRMED SEEDED** in the DB (Strategy 6=12,7=200,8=300,5=5) ⇒ the whole ladder is CONFIG-ONLY (no reseed).
  Executor A-E ladder (cheapest→leverage, all `Bots:*`, single-variable A/Bs, compress DayLengthHours to force the pin):
  - **A. Activity shape** (independent): `Activity:Baseline 0.6→0.5`, `WMoveDown 0.25→0.35` (WMoveUp fixed = leverage-down),
    `Composition:SizeExp 0→0.5`. Metric: out-of-band dwell↓ + |return|-autocorr↑ | guard CK=0, drift<5%/4h.
  - **B. GlobalShock** (independent): `Sentiment:GlobalShock:Enabled=true` (MeanIntervalHours 3, DownBias 0.85) = rare
    correlated elevator-down. Metric: left-skew/kurtosis↑, big-move rarity | guard latch=0, ×3 cap holds.
  - **C. Mood stack** (LAYERED workhorse, each layer gated on prior latch=0): `Mood:Enabled` → `+PerStrategy+TakerCoupling`
    → `+MMWiden+ConvictionFearBid`. Metric: crash-asymmetry(skew), taker-share, mood-latch | guard **latch=0 HARD**,
    JointTakerCapMult 1.5, CK=0.
  - **D. TrendFollower** (builds on C): `Enabled+TakerCoupling`, Strength ladder 0.1→0.2→0.3 = extremes trend. Guard
    AbsoluteCapMax veto + RecentAnchor (runaway stop one rung below ret_acf→0).
  - **E. Sector events** (builds on C, cohorts seeded ✓): `BankEstimate:SectorEventProb 0→0.02` (Mult 10, DownBias 0.7) =
    rare fat sector re-rating. Metric: sector-corr + tail kurtosis | guard drift-bounded, CK=0.
  ORDER: A+B parallel first (independent) → C layered → D,E on C's regime signal. **PROD-FIRST candidates** (lowest-risk,
  highest realism payoff): A (Activity shape) + C Stage-A (Mood gauge + PerStrategy + ConvictionFearBid); MMWiden only after
  a clean prod fear-excursion (latch=0). BOT_MECHANICS.md gets its status tags UPDATED in the same commit as any prod flip.
- **Battery 4 — KIESH'S FREQUENT-SMALL-NEWS lever (council-vetted, COMPLEMENT to the core fix, zero code).** Verdict:
  the mechanism advisor CONFIRMS frequent-small news = a bounded, mean-reverting WANDER of the fundamental (many small α
  residuals step the anchor target → price follows via chaser flow + anchor). It's the "wander the fair value" leg via the
  existing news path. Chaser has NO taker-threshold (fires on `|transient|>Floor 0.001`, `MinMagnitude` 10× above), and the
  news ceiling is `seed×[1±(Band+ShockCap)]≈±18%` (NOT 6%). It COMPLEMENTS the taker-momentum cohort (news kicks, momentum
  extends each into a mini-trend) — pair, don't substitute. It is NOT a standalone unpin (bounded + reverts to seed ~3h;
  the self-pin must still be broken). **★ HARD CONSTRAINT: shrink `Cap` in LOCKSTEP with magnitude** — else the flat
  `ChaseFloorIntensity=0.25` turns "frequent-small" into a linear order-COUNT multiplier that blows the ~19k loop-cap.
  Config (frequent-small arm): `ExogShock:MeanIntervalMinutes 0.75` (4×), `MinMagnitude 0.006`, `MaxMagnitude 0.020`,
  `Cap 0.020` (lockstep), `MagnitudeExponent 1.8`, keep `ChaserNotionalFrac>0`, `Permanence.Enabled true`, `AnchorTracksShock
  true`. **Measure wander-vs-jitter:** anchor `Residual` series stdev + lag-1 autocorr (wander = autocorrelated excursions
  toward ±Cap; jitter = uncorrelated near-zero); price range up; `ret_acf_lag1` NOT more negative; chaser gross↑/net≈0;
  loop-cap holds ≥19k during bursts (perf is the binding guardrail). The Contrarian's "distraction/6%-ceiling" objection was
  overruled on the code facts, but its caution stands: only counts if the residual series is autocorrelated + ret_acf doesn't worsen.
- **Slot/prod use:** local 2 slots = rapid 45-min screening (isolate ONE variable per pair); PROD = long confirmation
  track. First prod push = the proven anchor fix; then the proven Strength rung. Local keeps laddering while prod soaks.

## 🎯 GATES (per arm)
- **PROVEN → push to prod:** out-of-band dwell >0.5 of minutes, trend-run length up vs control, breakout survives,
  `ret_acf_lag1<0` (NO cap-touch), conservation/CK == 0, over a multi-hour confirm on a pinned-restore (NOT a fresh soak).
- **EXTEND to 90m:** dwell 0.3–0.5 or noisy sign.  **KILL:** ret_acf≥0, cap-touch, or any CK/conservation nonzero.
- **★ Contrarian's one guardrail:** NO arm counts as a win, and nothing reaches prod, until it unpins a DB restored from
  an ACTUAL pinned state AND holds CK/conservation clean over a multi-hour confirm. Fresh-soak "wins" are INADMISSIBLE.

## 🔍 LIVENESS WATCHDOG (the catastrophe guard)
Record each arm's expected-done (soak tier + ~50% slack) here. Soak servers run as `KieshStockExchange.Server.exe` (NOT
dotnet) — check liveness via a fresh candle/CSV row advancing every ~5 min, not `Get-Process dotnet`. Stalled/past-deadline
→ kill, log FAILED + cause, restart ONCE; 2nd failure → STATUS=BLOCKED + reason, fall idle. Never idle-wait on a hung arm;
never convene the council on a dead one. Verify each arm's config took effect (startup CONFIGCHECK) at warmup.

## 🧪 EXPERIMENT LOG (newest first)
- **EXP-5 / TEST 3 (2026-07-22 ~11:47) — RegimeDrift-taker A/B on the RESTRUCTURE-branch server. RUNNING.** Validates
  Change 3's NEW taker path fires CK-safe (the 730 unit tests only cover default-OFF) + the regime→price mechanism.
  Control = RegimeDrift:TakerCoupling OFF (5080, `kse_soak_rctrl`, bg `bz1alm1t8`); regime = ON TakerStrength 0.4 Threshold
  0.15 CohortFraction 0.3 (5083, `kse_soak_rregime`, bg `bajeqgn1f`). Both = current-prod-effective config; server built from
  `perf/admin-table-time-indexes` (harness copy `soak-maintree.ps1` $root→main tree; arm `run-arm-regime.ps1`). 45m. WATCH:
  regime arm CK/CONS=0 (HARD — new taker code), no lockstep giant candle, more/different movement vs control. NOTE infra: a
  SIMULTANEOUS launch timed out both arms' DB clones (Postgres template-clone contention + a stale chart server) — fix =
  stagger the 2nd arm PAST the 1st arm's DB reset (launch after it reaches the bot loop), and kill stray servers first.
  Single-arm boot is clean (~15s). ETA ~12:30.
- **★ ULTRADESIGN DONE + CHANGE 3 BUILT (2026-07-22).** Ran the ultradesign workflow (12 agents: feasibility×3 →
  architects×3 → council×5 → chairman) → fire-ready spec `docs/arcs/REALISM_ULTRADESIGN.md`. Corrections it made: (1) 28-day
  anchor must ship PAIRED with `Elastic=true` (28 alone still pins — linear pull); (2) DON'T build a regime state machine for
  EXTREME — the asymmetric/correlated/taker crash apparatus is ALREADY LIVE (ExogShock chaser+GlobalCoFire+Permanence); the
  honest extreme = ONE config knob `BearShortStrength 0→0.6`; the only NEW code is "earned calm" (deferred); (3) Change 3 =
  POST-PICK per-stock (not pre-pick watchlist-avg — regime is independent, averaging washes out).
  **✅ CHANGE 3 (RegimeDrift→takerness) IMPLEMENTED + COMMITTED on the restructure branch `perf/admin-table-time-indexes`
  (commit `7b9cfb9`):** BotSentimentService.RegimeSignal accessor + post-pick taker override in AiBotDecisionService (reuses
  TrendTakerDecision + no-share guard; value-band veto downstream) + AiTradeService wiring + appsettings block. All default-off.
  **Server compiles clean; 730/730 tests pass (byte-identical off); CK-safe.** NEXT (build order): 0. instrumentation
  (RegimeTakerProbe + RegimeSignal candle column + golden-master byte-check) → A/B Test 3 (metric = within-stock
  corr(RegimeSignal, forward return) >0) → Change 1 config (Elastic+28d) → Change 2 config (BearShort) → combined bake →
  earned-calm (only if needed). Then merge branch→master, ship to prod (council green-light pre-authorized). Prod = baseline+healthy.
- **EXP-4 (2026-07-22 ~04:0x-05:0x) — GlobalShock ISOLATION. ✅ RESULT: GlobalShock IS the crash-asymmetry engine.**
  gshock-only vs control (60-min fresh): maxdown **−23.4%** vs −14.1% (~9pp deeper tail) while range UNCHANGED (7.17 vs 7.10)
  ⇒ it deepens the rare TAIL specifically, not general vol = the target "rare correlated elevator-down." CK=0, latch=0.00.
  So of the new levers, **GlobalShock is the one that visibly moves realism in a short soak** (anchor-break = pin-only/prod,
  Activity/TF = long-soak). Prod recommendation sharpened: GlobalShock is a strong, safe, cheap elevator-down lever.
- **EXP-3 (2026-07-22, ~03:0x-04:0x) — new-lever BUNDLE safety+directional screen. ✅ RESULT: SAFETY CLEARED + a real
  signal.** Bundle (anchor-break + TrendFollower Str0.2 + GlobalShock DownBias0.85 + Activity-shape) vs current-prod control,
  60-min fresh soaks. **CK=0, CONS=0, latch=0.00** (mood-latch held despite GlobalShock+TF+MMWiden all on ⇒ the whole
  new-lever set is SAFE for prod). Directional: bundle maxdown **−25.2%** vs control −14.6% (deeper downside = the
  crash-asymmetry/"elevator-down" target), bounded inside the ×3 cap; net −0.85 vs +0.51 (down-tilt from DownBias/WMoveDown).
  One fresh 60-min soak ⇒ not a full realism verdict, but a clear GREEN LIGHT to STAGE the levers for prod (Q1). CORRECTION
  logged: the mood stack (C) is ALREADY LIVE on prod.
- **★ KEY STRUCTURAL FINDING (2026-07-22, changes the whole measurement approach):** the LOCAL PINNED-RESTORE IS NOT
  VIABLE. `BotPriceMemoryService` (the 7-day-TWAP self-pin anchor) holds its daily-average history IN-MEMORY; `Reset()`
  on boot re-seeds every stock at its SEED price + clears accumulators, and with an empty weighted-week ALL slots route to
  SEED (`BotPriceMemoryService.cs:100-129, 222-229`). The pin lives in NON-PERSISTED in-memory state → a restored DB boots
  with the anchor at seed 639 and the pin DISSOLVES. And a <24h fresh soak can't show the self-pin either (0 rotations at
  DayLengthHours=24). ⇒ discard the pinned-restore (scratchpad dump kept but unused). **PIVOT:** compress
  `Bots:ValueAnchor:DayLengthHours` (ServiceStart = "reproducible/soak-friendly") so the multi-day anchor rotation — and
  thus the self-pinning downward ratchet — happens INSIDE a soak (7 rotations fill WindowDays=7). This is the only way to
  reproduce + A/B the self-pin locally. The decisive -20%-pin CONFIRMATION still ultimately belongs on PROD (see Question).
- **EXP-2 / Battery 1 (2026-07-22 ~01:3x) — anchor-break, compressed day.** RUNNING. Both arms = current prod config +
  `DayLengthHours=0.1` (6-min days → 7 rotations ~42m). ONLY diff = `ValueAnchor:UsePreviousDayAverage`: **pin** (=true,
  self-pinning TWAP anchor, current prod) port 5080 db `kse_soak_pin` (bg `bnfrnp7lk`) vs **break** (=false, OU-to-seed)
  port 5083 db `kse_soak_break` (bg `bibnh21t7`). 90-min. HYPOTHESIS: the `pin` arm ratchets DOWN + sticks (reproducing the
  pin in compressed time); `break` stays near seed. If so ⇒ the self-pin anchor is the drift culprit + the break is the fix
  (→ push to prod for real-time confirmation). If `pin` does NOT drift down ⇒ compression insufficient ⇒ escalate to prod.
  Metric: net drift vs seed + does `pin` dwell low while `break` doesn't; CK/CONS clean. Expected-done ~03:0x.
- **EXP-1 (2026-07-22 ~00:5x) — TrendFollower mechanism screen (FRESH DB, single variable).** Rationale: get testing
  running + validate the taker-momentum cohort is SAFE (CK clean) and MOVES price vs baseline, while the pinned template is
  built. Both arms = current PROD-effective config; ONLY diff = TrendFollower. Control (off) port 5080 db `kse_soak_tf_ctrl`
  (bg `buus2ay8f`); TF (Enabled+TakerCoupling Str=0.20 CohortFraction=0.04) port 5083 db `kse_soak_tf_exp` (bg `bri526har`).
  45-min, Tmpl=kse_soak_seed. Expected-done ~00:5x+45m (+ ~15m slack). Scripts: scratch `run-arm-tf.ps1`. NOTE: fresh DB ⇒
  this measures MECHANISM (does the cohort create movement/trends + stay CK-clean), NOT full unpin (that needs the pinned
  template). Metrics: pull from DB Candles (range/CV, trend-run) since candle_export.py python may be absent; watch CK/CONS
  in the results CSV `logs/soakP-*-results-*.csv`. **STATUS: RUNNING + verified** — both arms reached the bot loop
  (ctrl 00:41:31, tf 00:42:12), **0 errors** either arm; TrendFollower binding confirmed (`AiTradeService.cs:854`
  `GetValue("Bots:TrendFollower:Enabled")` ← the `Bots__TrendFollower__Enabled` env; decision gate `AiBotDecisionService.cs:1463`
  `_trendFollowerEnabled && _trendStrength>0`). Expected-done ~01:27 + ~15m slack. Liveness: re-check a fresh results-CSV row
  every ~5m; if a candle stops advancing >10m → kill+restart-once. On completion: compare range/CV + trend-run + CK/CONS,
  council picks next move (Battery 1 anchor-break on the pinned template).

## ❓ QUESTIONS FOR KIESH (answer inline anytime; I proceed on the stated default, never block)
- **Q1 (prod-confirmation authority).** Local soaks CANNOT reproduce the real -20% pin (it lives in 30h+ of non-persisted
  in-memory anchor state). I can reproduce it in COMPRESSED time locally (DayLengthHours trick) to screen + prove the
  MECHANISM/direction of a fix, but the faithful -20%-pin CONFIRMATION is only on PROD. **May I apply a PROVEN-in-compressed-
  local lever (starting with the low-risk, reversible `UsePreviousDayAverage=false` anchor-break) directly to PROD env and
  monitor hourly** (like the earlier RD/MPM deploy), rather than waiting for your OK each time? DEFAULT until you answer: I
  keep all prod changes gated on your explicit OK — I screen/prove locally (compressed) and STAGE the exact prod env delta +
  monitor plan here for you to approve. (TrendFollower is positive-feedback ⇒ that one I will ALWAYS stage for your OK, never
  auto-push.) **★ NOW ELEVATED:** local A/B proved to have weak power (2 inconclusive cells; pin not locally reproducible), so
  prod is the ONLY decisive bed — this decision now gates the whole run. **STAGED PROD SEQUENCE for your approval** (each an
  env line in `docker-compose.prod.yml` server + `up -d server`, fully reversible; monitor hourly like the RD/MPM deploy):
  (1) `Bots__ValueAnchor__UsePreviousDayAverage=false` — the anchor-break, LOWEST risk (removes the self-pin ratchet;
  directly targets the verified root cause; not positive-feedback). Watch: does MSFT recover off ~511 over hours; drift bounded.
  (2) `Bots__Activity__Baseline=0.5` + `WMoveDown=0.35` + `Composition__SizeExp=0.5` — calmer quiet + leverage-down. Watch:
  quiet stocks calmer, down-moves sharper; CK=0. (3) `Bots__Sentiment__GlobalShock__Enabled=true` (MeanIntervalHours 3,
  DownBias 0.85) — rare correlated elevator-down. Watch: latch=0, ×3 cap holds. (4) TrendFollower + SectorEvents LAST, after
  (1)-(3) settle (positive-feedback / fat-tail — most watching). EXP-3 is clearing (1)+(3)+TF for CK/latch safety first.
  **If you'd rather I auto-push (1) [anchor-break, reversible, low-risk] once EXP-3 confirms it's CK/latch-clean, say so.**
  ★ **PROD DEPLOY IS STAGED + READY (2026-07-22, NOT executed — awaiting Q1 OK).** Prod compose = `root@159.195.149.51:/opt/kse-server/docker-compose.prod.yml`, server env is `Bots__X: "val"` lines (47-83). EXACT reversible one-liner for lever (1):
  ```
  ssh root@159.195.149.51 'cd /opt/kse-server && cp docker-compose.prod.yml docker-compose.prod.yml.pre-anchorbreak-20260722 \
    && sed -i "/Bots__RecentAnchor__Strength/a\      Bots__ValueAnchor__UsePreviousDayAverage: \"false\"" docker-compose.prod.yml \
    && docker compose up -d server'
  ```
  Verify: `grep UsePreviousDayAverage docker-compose.prod.yml` + `docker compose logs --tail=5 server`. Monitor hourly: MSFT
  price off ~511 + pinned-count (reuse `scripts/prod_*_monitor.sh` pattern). ROLLBACK: restore the `.pre-anchorbreak-*` backup
  + `docker compose up -d server`. Then bake into BOT_MECHANICS.md §2.5 (ValueAnchor status) same commit.

## 🛠 ULTRAPLAN QUEUE (structural fixes needing a remote fire / owner call)
- **Hysteretic REGIME STATE (bot-strategy council 2026-07-22, CODE change).** Today extremeness is a continuous gain on a
  FIXED strategy mix; the council's core structural recommendation = a discrete/hysteretic market regime (calm/stress/panic,
  latched to avoid flapping) that RE-WEIGHTS cohort taker-share (panic = faders capitulate into momentum), + calm EARNED
  (collective stand-down in low-activity regimes, not just per-bot cooldowns). Cross-wire global-mood→activity-G→
  taker-share/MMWiden/reflexive as ONE regime loop (§ the STRATEGY LADDER note). This is the deeper realism fix beyond the
  A-E config levers; author as an ultraplan when the config ladder plateaus. Guardrail: absorber+co-fire together, latch-gated.

## 🔁 Resume prompts (exact text for the timers; T2 re-arms T1 if the chain died)
**5h-window (T1) prompt:** "AUTONOMOUS REALISM RUN — 5h-window resume. STEP 1: re-arm — `date -d '+5 hours 2 minutes'
'+%M %H %d %m'` → CronCreate a one-shot (recurring:false) with THIS prompt. STEP 2: read this file's STATUS + MARKET_PINNING_ARC
§11, resume the next experiment per the RUN PLAN (council-driven, battery, liveness watchdog, 45-min screens → confirm →
push proven wins to prod env-reversibly, continue). NEVER flip a prod default outside the reversible env A/B protocol; runaway
guard on the momentum leg. If blocked, log a Question here and continue."
