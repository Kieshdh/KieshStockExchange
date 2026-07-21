# MARKET-PINNING + CHART ARC — SINGLE DIRECTIVE (read FIRST, this is the ONLY file to resume from)

**Purpose:** the ONE living directive for BOTH parallel arcs (Kiesh, 2026-07-21: "make a clear directive for this arc.
No need for multiple files, let it be dictated by one"). A fresh/low-context session reads ONLY this file. It captures
the soak (prod diagnosis, config corrections, A/B plan, live state) AND the chart arc (backlog + progress + anchors).
Other chart docs (`CHART_ROUND2_HANDOFF.md`, `CHART_ROUND2_SPEC.md` in the chart worktree) are REFERENCE DETAIL only —
this file is the authority for what's done and what's next.

## ★ CONTINUATION DIRECTIVE (Kiesh, 2026-07-21)
After /clear (or compaction), run **TWO arcs in PARALLEL**:
1. **A/B SOAK — FIRST PRIORITY.** Set up + run the market-realism A/B soak below (unpin the flat market), report,
   and — only if it validates and Kiesh signs off — ship the reversible env change to prod. (§5 + §8 live state.)
2. **CHART ARC — SECOND PRIORITY.** Implement ALL remaining chart tooling + test today. Full backlog/progress/anchors
   are in §9 of THIS file (self-contained; the worktree's `CHART_ROUND2_SPEC.md` has the fuller per-item detail).
Prod deploy of realism = OWNER-GATED (eyeball + soak first). Prod box = `root@159.195.149.51:/opt/kse-server`,
prod runs `master`, Postgres container `kse-server-postgres-1` (`psql -U kse -d kse`).

---

## 1. THE PROBLEM (measured on PROD, 2026-07-21)
Every stock is pinned in a dead-flat band well below its seed and stuck there for 30+ hours; news "breakouts" snap back.

- **MSFT (StockId 1): seed 639.31 (USD), pinned ~510** for 30+h. 6h range 509.3–512.0 = **0.52%**; **99.4% of 1-min
  closes within ±0.3% of 510.24**. Hourly avg never leaves 510.2–511.3 — a flat line. Rare 1h spikes to 515–516 fully
  collapse within the hour (= the "breaks out, immediately falls back").
- **Global, not MSFT-specific.** 2h coefficient of variation ~**0.1%** across all stocks (NVDA 0.09, AAPL 0.12…).
  Most stocks drifted BELOW seed and froze: MSFT −20%, NVDA −18%, PEP −16%, MRK −12%, AAPL −7.5% (a few above: PG +12%).
- SeedPrice lives in `StockListings.SeedPrice`. Candle cols: Open/High/Low/Close/Volume/TradeCount/MarketMood/MoodMid/MoodSlow.

## 2. ROOT CAUSE — the 2026-07-16 "random-walk" tuning over-damped the market
The intent was "moves stick / random-walk character," but two of the three changes removed the *energy* that moves price:
- **`Bots:Sentiment:RegimeDrift:Strength` 1.0 → 0.5** (prod env) — HALVED the per-stock persistent trend, the main
  engine of sustained direction. Nothing carries a move → price sits.
- **`Bots:MarketProbMult` 1.5 → 1.35** (prod env) — 10% less taker flow + a deeper absorbing limit-order book that
  pins price wherever it drifts. ("only taker flow moves price; limit tilts get absorbed by the book/anchor.")
- **`Bots:RecentAnchor:Strength` 0.05** (prod env) — correctly weak (this one HELPS movement); not the culprit.
- **News is drift-NEUTRAL** (50/50 up/down, `RandomShockSource`) → it adds catchable events but CANNOT create sustained
  direction; and its transient component decays, so a news pop reverts. That's the snapback.

Net: the market found a floor at the drifted level and nothing has enough energy to move it or sustain a breakout.
This sits on top of the known STRUCTURAL ceiling `ret_acf_lag1 ≈ −0.43` (1-min over-mean-reversion) — see memory
[[project_market_realism_v2]] / [[project_sentiment_price_reaction]].

## 3. ★★ TWO CONFIG CORRECTIONS (found reading the code — change Kiesh's requested arm)
Kiesh's requested arm was **RegimeDrift 0.8, MarketProbMult 1.4, DecayHalfLifeSec 1200**, plus "make DecayHalfLifeSec
randomized/variable." Reading `ExogenousShockService.cs` + `IShockSource.cs` (`RandomShockSource`/`NewsPermanenceOptions`):

**(A) `ExogShock:DecayHalfLifeSec` is INERT while Permanence is ON (it is on prod).** When permanence is enabled, EVERY
news event carries its OWN per-event decay half-life `τ` and the service uses `e.TauSec = imp.DecayHalfLifeSec` (the
drawn τ). The service-level `DecayHalfLifeSec` (=600 on prod) is only the fallback for LEGACY sentinel impulses (α=0/τ=0),
which permanence-on never produces. **⇒ changing 600→1200 does ~nothing.** The real transient-decay control is
**`Bots:ExogShock:Permanence:TauMedianSec` (currently 1500)**, range [`TauMinSec` 300, `TauMaxSec` 2400], lognormal
spread `TauSpread` 0.40. So Kiesh's "1200" intent ("news lingers a bit longer") maps to nudging **TauMedianSec** (e.g.
1500→2000), NOT DecayHalfLifeSec.

**(B) Per-event decay is ALREADY randomized + variable.** `RandomShockSource.DrawAlphaTau` draws a per-event τ that is
lognormal in [300s, 2400s] centered at TauMedianSec=1500, AND negatively COUPLED to the permanence fraction α
(`Coupling` β=0.6): clean/high-α news fades FAST; hype/low-α news lingers LONG. So "some news fades faster than others"
is **already implemented.** To make it *more* variable: raise `TauSpread` (0.40→~0.55) and/or widen `TauMaxSec`
(2400→3000). Not required for the first arm.

## 4. WHAT "PERMANENCE FRACTION" IS (Kiesh asked)
`Bots:ExogShock:Permanence` splits each news impulse's price impact into two parts (spec `docs/research/NEWS_PERMANENCE_COUPLING.md`):
- **α · magnitude → PERMANENT residual floor** — a durable re-rating of the fundamental level; bleeds SLOWLY at
  `ResidualHalfLifeSec` (10800s ≈ 3h ⇒ "session-permanent, not eternal").
- **(1−α) · magnitude → TRANSIENT overshoot** — the initial spike; decays FAST at the per-event τ (minutes).
- **α (the "permanence fraction") is drawn per-event in [`AlphaMin` 0.30, `AlphaMax` 0.90]** (median ~0.6), coupled to τ
  (see 3B). α near 0.9 = "quick jump that keeps its gain"; α near 0.3 = "big overshoot that eases back to a small raised base."
- The residual feeds the FundamentalService anchor tilt (via `AnchorTracksShock=true`), so a high-α event permanently
  shifts the anchor target ⇒ price *should* re-rate. It doesn't stick much today because (a) news is drift-neutral+sparse
  (`MeanIntervalMinutes`=60) so residuals cancel over time, and (b) the market is too damped to follow the re-rated anchor.
- **Lever for "breakouts stick more":** raise `AlphaMin`/`AlphaMax` (more of each event is permanent). This is the
  correct knob for Kiesh's "make breakouts stick," NOT the transient τ.

## 5. ★ THE A/B SOAK PLAN (FIRST-PRIORITY task)
Validate locally (parallel arms) BEFORE any prod change. Workflow per memory [[feedback_candle_csv_pipeline]],
[[feedback_ab_client_pointing]], [[feedback_soak_duration]], [[reference_soak_server_process_name]]. Harness scripts:
`scripts/kse-balance-soak-p.ps1` (parallel A/B), `scripts/kse-balance-soak.ps1`, `scripts/prod-soak.ps1` — READ the
harness first to confirm how arms take config (env vars vs appsettings) + ports. Local soak servers run as
`KieshStockExchange.Server.exe` (check liveness via CSV/DB, not `Get-Process dotnet`). Duration = **45-min mid A/B**.
Point the LIVE CLIENT at the EXPERIMENTAL arm (build-time port 5083) so Kiesh eyeballs it. **Disk-gate all builds**
(`% Disk Time`<70%, Idle + `-maxcpucount:1`; PARSE for `error CS`).

**Baseline arm = current PROD effective config** (master appsettings + the `docker-compose.prod.yml` server env overrides):
RegimeDrift 0.5, MarketProbMult 1.35, Permanence ON (TauMedianSec 1500, AlphaMin/Max 0.30/0.90, ResidualHalfLife 10800),
ExogShock MeanInterval 60 / Max 0.12 / Cap 0.25 / AnchorTracksShock true.

**Experimental arm (CORRECTED from Kiesh's ask — see §3):**
| Knob | Baseline | Arm | Note |
|---|---|---|---|
| `Bots:Sentiment:RegimeDrift:Strength` | 0.5 | **0.8** | Kiesh's value; the MAIN lever (restore the trend engine) |
| `Bots:MarketProbMult` | 1.35 | **1.4** | Kiesh's value; modestly more taker flow |
| `Bots:ExogShock:Permanence:TauMedianSec` | 1500 | **2000** | REPLACES the inert "DecayHalfLifeSec 1200" — longer news ease-back |
| `Bots:ExogShock:Permanence:AlphaMin` | 0.30 | **0.40** *(optional)* | more of each news event is permanent ⇒ breakouts re-rate/stick |

(Leave `DecayHalfLifeSec` as-is — it's inert. Optional extra-variability: `TauSpread` 0.40→0.55.) Env-override form:
`Bots__Sentiment__RegimeDrift__Strength`, `Bots__MarketProbMult`, `Bots__ExogShock__Permanence__TauMedianSec`,
`Bots__ExogShock__Permanence__AlphaMin`.

**Compare (candle CSV, `candle_plot.py`):** coefficient of variation (want it UP from ~0.1%), trend persistence
(runs that last), breakout survival (do news pops hold a new level?), and that drift stays BOUNDED (no runaway; stay
within the seed·±band / AbsoluteCapMax ×3÷3 veto). Kiesh eyeballs the experimental arm live. If it looks right + he
signs off ⇒ ship the env deltas to prod (reversible: edit `docker-compose.prod.yml` server env + `up -d server`).
NEVER flip a prod realism default unattended.

## 6. COUNCIL VERDICT — the "price estimation" mechanism (bank-estimate + rotational bots)
Design doc: `docs/plans/BANK_ESTIMATE_ROTATIONAL_BOTS_PLAN.md` (finalized 2026-07-07, NOT built). Kiesh asked the council
whether it would help the pinning. 5 advisors:
- **Do the KNOB-TUNING (§5) FIRST — near-unanimous.** Can't evaluate the estimate idea through a mistuned baseline; the
  knobs are the cheap, reversible, DIRECT test of the flow-vs-book-absorption hypothesis.
- **It's a VALUE/CONVERGENCE mechanism, not MOMENTUM.** "Once price reaches the estimate they stop" = a RE-PIN (magnet
  moves from seed→estimate). Adds flow (#1) but does NOT fix the order-wall absorption (#2) or symmetric drift-neutral
  news (#3). Will make the first 10–20% recovery snappier, then flatline at the new anchor. Satisfying *trends* need
  MOMENTUM/correlated belief (buy because rising), which this isn't.
- **Durable case (First Principles + Expansionist):** the DECOUPLING is structurally right — "what's true" (a drifting
  published fair value) separated from "who enforces it" (a market-order cohort that CAN'T be book-absorbed). And it's
  the sim's first SEMANTIC LAYER: every price gets a reason (over/under-valued) ⇒ screeners, value-vs-momentum, news
  as a gap-injector, emergent sector rotation, analyst UI. One field, generative for years.
- **CRUX/GUARD:** only works if the estimate genuinely DRIFTS directionally; if it mean-reverts inside its ±10% clamp
  (built from the same drift-neutral sentiment) it's just a twitchier pin. Also: the anchor-pivot channel conflates with
  the rotator-flow channel — bisect them.
- **SEQUENCE (Executor):** knobs now → **Bank estimate in SHADOW mode** (read-only signal, NO rotators, NO reseed — log
  estimate-vs-price divergence for days) → rotators + reseed ONLY if the shadow data justifies it. Rotator cohort seeding
  is RESEED-ONLY (heavy op — never bundle with an urgent live fix).

**Chairman recommendation:** (1) ship the §5 knob A/B first — it's the immediate unpin and the honest test. (2) The
estimate is worth building for the semantic/product upside + a one-time value re-rating with a causal "why" — but it is
NOT by itself the cure for "won't trend." Build it estimate-FIRST in shadow, and pair with a momentum leg if persistent
runs are the goal. Do NOT start the rotator+reseed until the tuning is settled and the shadow estimate is validated.

## 7. STATE / anchor knobs reference (appsettings defaults; prod overrides in docker-compose.prod.yml)
- `ValueAnchor`: Strength 0.40, Scale 0.12, ElasticDeadbandPrc 0.20. `RecentAnchor`: Enabled, HalfLifeSec 1800,
  Strength 0.10 (prod 0.05), Scale 0.04. `RegimeDrift`: Enabled, StepSigma 0.03, Cap 0.5, Strength 1.0 (prod 0.5).
  `MarketProbMult` 1.5 (prod 1.35). `ExogShock:Permanence` appsettings Enabled=false BUT prod override Enabled=true.
- Working tree was on `perf/admin-table-time-indexes` at handoff; the realism soak baseline should reflect PROD =
  `master` + the prod env overrides. Realism tuning has NO uncommitted code — it's all env/appsettings.

## 8. STATE / NOT YET DONE (for the continuation)

### ★ LIVE STATE (2026-07-21 16:22, this session)
- **A/B SOAK IS RUNNING** (45-min mid, started 16:19, ETA ~17:05). Prod-matching code built from a dedicated
  **master worktree** `C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange-soak` (HEAD `c90d642` = prod).
  Rationale: the main tree is parked on the restructure branch (`perf/admin-table-time-indexes`), which is +70 files
  vs master and TOUCHES realism code (ExogenousShockService/FundamentalService/BotSentimentService/BankEstimate) —
  so it is NOT prod-matching. Build the soak on master ONLY.
  - **Baseline arm** (prod effective): RD=0.5, MPM=1.35, Tau=1500, AlphaMin=0.30 → port **5080**, DB `kse_soak_base_ab`.
  - **Experimental arm** (§5 corrected): RD=0.8, MPM=1.4, Tau=2000, AlphaMin=0.40 → port **5083**, DB `kse_soak_exp_ab`.
  - Harness: `KieshStockExchange-soak/scripts/kse-balance-soak-p.ps1` (its `$root` edited to the soak worktree).
    Env applied per-arm by scratch `run-arm.ps1` (full prod `Bots__*` set + the 4 deltas; both arms pin AlphaMax=0.90,
    TauSpread=0.40 so the ONLY diff is the 4 intended knobs). Both launched via `run_in_background`; sample every 5 min;
    candle CSVs auto-export to `KieshStockExchange-soak/logs/` on stop.
  - **Live client** (pid launched 16:22) = master client with `Resources/Raw/appsettings.json` BaseUrl→`http://localhost:5083`
    (experimental arm) so Kiesh eyeballs the corrected market live.
- **Postgres**: `kieshstockexchange-postgres-1` (Docker Desktop must be running); template `kse_soak_seed` exists.

### SOAK RESULT + PROD DEPLOY (2026-07-21, DONE)
- **45-min A/B was INCONCLUSIVE**: both arms ~identical (CV 1.82% vs 1.83%; exp actually moved slightly LESS). A fresh
  soak does NOT reproduce the long-horizon pin (fresh MSFT *falls* from seed; prod MSFT is *pinned* at 0.098% CV). Env
  overrides DID apply (ExogShock.Enabled defaults false but both logs show enabled=True). `python`-not-found broke the
  CSV export both arms → metrics pulled straight from the DBs via SQL. Use `py`/full path next time.
- **Kiesh decision: deploy to prod directly + monitor** (prod is the only place the pin exists; env-reversible). ✅ DONE:
  RD 0.8 / MPM 1.4 / Permanence Tau 2000 / AlphaMin 0.40 live on `kse-server-server-1` (env confirmed), committed+pushed
  `master 39bdedf`, backup `docker-compose.prod.yml.pre-pin-20260721` on the box. Hour-0 baseline: 20/49 stocks pinned
  (cv<0.3%), MSFT 511.60 @ 0.098%. Hourly monitor for 3h → scratch `prod-pin-monitor.sh`/`.log`.
- [ ] **If after 3h MSFT still ~511 & pinned count unchanged ⇒ COUNCIL the root cause** (Kiesh's call). Rollback = delete
      the 4 env lines on the box + `up -d server`.
- [x] (Now 1st priority per Kiesh 2026-07-21) chart arc — ALL 5 items DONE + a rail-split refactor (§9). Pending
      Kiesh visual-test. Branch still local.
- [ ] (Later, gated) bank-estimate SHADOW mode — only after tuning is settled.

## 9. CHART ARC — state + remaining (self-contained; supersedes the separate chart handoff as the directive)
**Worktree:** `C:\Users\kjden\source\repos\Kieshdh\KieshStockExchange-chart` on `feature/bot-market-realism-v2`
(base HEAD `b770b33`; all round-1 chart commits verified present). **Build (disk-gated, Idle, -maxcpucount:1, parse
`error CS`):** client csproj `...-chart\KieshStockExchange\KieshStockExchange.csproj -f net9.0-windows10.0.19041.0`.
Client-only (no server/money). Enum/DrawStyle/DrawingObject fields are APPEND-ONLY (old JSON must still load).
Fuller per-item detail (intended design → v1 → gap, with file:line anchors) lives in the worktree's
`docs/arcs/CHART_ROUND2_SPEC.md`.

**DONE + committed (this session, on the worktree branch):**
- ✅ `24bc20e` docs: consolidated round-2 spec.
- ✅ `98a3718` **Fib fix** — uniform-width level tags (ratio left / price right; `DrawingRenderer.DrawFibLevelTag`, const `FibTagW=104`).
- ✅ `05c99cb` **Text rework** — plain coloured text (no pill) + numeric font-size. `DrawStyle.FontSize` (append-only, 0=default);
  render/hit draw plain text in the pen colour sized off FontSize; panel split `ShowWidth` from `ShowStroke` (Text = colour,
  no width) + new SIZE dropdown (8..72) + ▲/▼ steppers (`Pen.cs` `PenFontSize`/`StepFontSizeUp/Down`).
- ✅ `cae95f9` **Rail regroup + Alert→toolbar** — rail groups now Lines / Shapes / Drawing(Freehand·Arrow) / Position /
  Text (+ Measure, Magnifier singles); Alert is a 🔔 top-row button next to MA (`ChartToolbarView.xaml`, arms `DrawTool.Alert`).
  `Pen.cs` added Drawing/Position/Text group state; rail + flyouts split across `ChartToolRailView.xaml` + `ChartView.xaml`.
- ✅ `83427db` **Comment tool** (`DrawTool.Comment`) — Text+rounded-bubble callout with a downward v-tail to its anchor;
  wired at all four dispatch sites (render/hit/placement/drag) + Text-group flyout row + preset; icon reuses `tool_text.png`
  (TODO `tool_comment.png` asset).

- ✅ `bbf6248` **Position → Long/Short/Manual + panel** — `PositionLong/Short/Manual` arming tools all commit
  `Kind=Position`, Direction fixed by the TOOL (drag-inference dropped). Manual = one-click default box then edit.
  Position panel section (numeric Entry/Target/Stop + Risk% + read-only R:R) + Delete-only footer (Set-as-default hidden).
- ✅ `9ce2aa1` **refactor** (Kiesh's split steer) — extracted the left-rail tool-group state out of the overloaded
  `Pen.cs` (~470 lines) into `ChartDrawingViewModel.Rail.cs`; Pen.cs = pen STYLE panel, Rail.cs = tool-group RAIL
  (matches the Colors/Persistence/Undo split). No behaviour change.

**ALL 5 CHART ITEMS DONE + build-clean (7 commits `24bc20e`..`9ce2aa1`).** Non-blocking follow-ups: dedicated
`tool_comment.png` asset; Kiesh visual-test of the reworks (Text, Position) + confirm the two Position defaults —
(a) kept the entry-line stroke colour (read "opacity-only" as no per-zone fill colour); (b) Long/Short direction fixed
by the tool. Numeric-Entry decimal binding may want a converter for partial input. Branch `feature/bot-market-realism-v2`
still LOCAL (Kiesh's history-cleanup force-push outstanding).

## 10. ★ COMPACTION HANDOFF (2026-07-21 ~22:00 — READ THIS FIRST after compaction)
Two arcs ran this session. Working dirs: main tree = `KieshStockExchange` (on restructure branch, DON'T soak here);
`KieshStockExchange-soak` = master (prod-match); `KieshStockExchange-chart` = `feature/bot-market-realism-v2` (ALL
chart work + commits live here, still LOCAL — Kiesh's history force-push outstanding).

**ARC 1 — PROD PIN-UNSTICK: DONE + VALIDATED.** Deployed RD 0.8 / MPM 1.4 / Permanence Tau 2000 / AlphaMin 0.40 to
prod (master `39bdedf`, live on `kse-server-server-1`, backup `docker-compose.prod.yml.pre-pin-20260721` on the box).
3h hourly monitor: pinned stocks 20→7, MSFT unfroze 511.60→513.83 (cv 0.098→0.334). **It worked — NO council needed;
settings finalized (kept). Rollback = delete the 4 env lines + `up -d server`.** Details §8.

**ARC 2 — CHART (now Kiesh's 1st priority).** Source of truth = `KieshStockExchange-chart/docs/plans/CHART_DRAWING_OVERHAUL_PLAN.md`
(has the full grouping + A–F + the text-drawing council verdict). Local test env: master server on **port 5090** vs
DB `kse_soak_exp_ab` (has candles+mood); chart client rebuilt with `Resources/Raw/appsettings.json` BaseUrl→`http://localhost:5090`
(UNCOMMITTED, test-only — committed value is prod duckdns; do NOT commit the localhost BaseUrl). Client relaunch:
kill `KieshStockExchange.exe`, build client csproj (disk-gate <70%, Idle, -maxcpucount:1, `error CS`; kill client first
or the .Shared.dll copy locks), launch the win10-x64 exe.
- **BUILT + committed** (`24bc20e`..`Circle`): Text rework · Comment · Position Long/Short/Manual+panel · Cross line ·
  Circle(centre+radius) · Triangle · rail regroup+Alert→toolbar · scroll(A) · Measure+Magnifier combine(C, − standalone) ·
  Delete-all 3-choice dialog(D/E) · draw-while-hidden auto-show(F) · rail order+dividers · mood-pane-from-candles fix ·
  rail-state split into Rail.cs. Rail order: Lines·Shapes·Position·Draw | Measure·(−) | Delete·Undo·Redo·Hide.
- **REMAINING (easiest→hardest, in the plan):** Highlighter (Freehand@high-alpha), Price label + Comment two-point +
  Text-label INLINE typing (⇐ COUNCIL VERDICT in the plan: drop the user-placed 2nd point→anchor+direction hint+auto-size;
  ONE AnchoredText model {Anchor,Offset,Text,RenderMode∈Plain/Bubble/PriceBubble,ShowTail,Style}; transparent-Entry inline
  typing, SPIKE the WinUI caret/IME first; snap+settle the tail; auto-delete only on explicit commit); Rotated-rect, Arc,
  Magnet(snap), Lock toggle; **B dynamic rail sizing** (last/optional, guard the SizeChanged loop); axis polish; remove ✕
  glyph; Escape/Delete keys; crosshair cursor; icon pass (real PNGs: crossline/circle/triangle/delete/comment — placeholders
  reuse siblings).
- **OWNER DECISIONS PENDING:** (1) **Alert-as-message** = persisted server Message + centered popup + chart deep-link —
  SPANS client+server (needs a small MessageController endpoint; PriceAlertService is client-in-memory today) → owner-gated,
  not started. (2) Confirm the council divergences (2nd-point=direction-hint; tail snap/settle; auto-delete-on-commit-only).
- **WORKING RULES honored:** correct code placement per the split (rail→Rail.cs, delete/dialog→core VM, shared geometry→
  ChartGeometry, render→DrawingRenderer, hit→ChartHitTester) — see memory [[feedback_code_placement_respect_split]]; each new
  tool wired at its 4 dispatch sites; disk-gated builds; commit-per-feature.
- **CHART ARC CLOSED for this session (2026-07-21).** Full status table + duo working protocol + placement rules baked
  into `KieshStockExchange-chart/docs/plans/CHART_DRAWING_OVERHAUL_PLAN.md` §"2026-07-21 SESSION CLOSE" (commit `4901902`).
  Chart tip `b3eca3b` (Text/Comment inline, Price label, Circle, Fib rainbow+bands+de-boxed tags — all committed, still LOCAL).

## 11. ★★ REALISM PIN — COUNCIL ROUND (2026-07-21, brief-informed) — VERIFIED ROOT CAUSE + FIX
Ran 5 code-grounded advisors + a full research sweep of every realism doc/memory (→ `docs/arcs/REALISM_COUNCIL_BRIEF.md`).
The research CORRECTED the round-1 framing. **Config verified against `KieshStockExchange.Server/appsettings.json`.**

### ROOT CAUSE (verified, not theory) — a one-way ratchet to a sell-veto floor
The ~510 pin is NOT the deadband and NOT the seed-reverting OU. It is two live mechanisms:
1. **Sell-veto floor.** `IsOverBand` forbids any SELL below `seed/(1+cap)`; `cap = OverheatCap × ProfileMult`. MSFT is a
   *Calm* stock (StockId ≤5, mult 0.85) ⇒ floor = 639/(1+0.3·0.85) = **639/1.255 ≈ 509**. (`OverheatCap 0.3`,
   `CapFromSeed true` — appsettings:151,158; `PriceBandMath.cs:35`, `StockProfileService.cs:22,37`.) Bots literally cannot sell lower.
2. **Self-pinning TWAP anchor.** `ValueAnchor:UsePreviousDayAverage=true` (`WindowDays 7`, appsettings:153,157) routes the
   long-anchor target to a **7-day weighted average of PRICE ITSELF** (via `BotPriceMemoryService`), NOT the seed. The
   seed-reverting `FundamentalService` OU is bypassed while this is on. So once the fleet's steady down-pressure (net-long,
   cash-hoarding) ratchets price to the veto floor, the anchor re-centers on ~510 and cements it. **Nothing pulls UP to 639.**
- **RED HERRING corrected:** the round-1 "20% ElasticDeadband" theory is INERT — `ValueAnchor:Elastic=false` (appsettings:147),
  so `ElasticDeadbandPrc=0.20` does nothing. (This is why researching first mattered.)

### WHY the random-walk / knob deploy failed (Kiesh's axiom, confirmed IN CODE)
RegimeDrift → sentiment → a **buyProb tilt = absorbed** (rests as limit orders). appsettings:567 says the retired
`ChaserStrength*tanh` buyProb tilt "only shifted the buy/sell ratio and **could not move it**" — only taker VOLUME moves price.
RegimeDrift's `Cap 0.5` can't even reach the |sentiment|>1 threshold that forces market orders. News works because it hits
BOTH the **taker channel** (the chaser submits marketable orders) AND the **anchor** (`AnchorTracksShock`). The random walk
hits neither ⇒ more RegimeDrift = a noisier flat line (the 511.6→513.8→regress blip).

### THE FIX — turn ON the taker-flow cohorts that are BUILT but set to 0 (config-only, no code)
Every taker/momentum leg the value mechanism lacks already exists and is OFF. Cheapest→highest-leverage:
1. **TrendFollower cohort + TakerCoupling** — `Bots:TrendFollower:Enabled=true`, `TakerCoupling=true`,
   `Strength` up a GEOMETRIC ladder from ~0.15 (`CohortFraction 0.04`). A chartist cohort that CROSSES THE SPREAD on
   momentum ⇒ a nascent move becomes sustained one-sided taker flow the book can't absorb = the missing positive-feedback
   momentum leg. **RUNAWAY RISK** — the appsettings comment warns: raise Strength a rung at a time, STOP one rung below any
   ret_acf-crosses-0 / cap-touch; the ×3 `AbsoluteCapMax` + StopBreaker are the guards. (appsettings:131-140)
2. **Break the self-pinning anchor** — `UsePreviousDayAverage=false` (restore a genuine fundamental) OR enable
   `ValueAnchor:Adaptive` (`BlendWeight>0`, appsettings:159) so the cap/target RE-RATES on genuine moves and STICKS instead
   of ratcheting down. Without this, even taker-driven moves get re-absorbed to ~510.
3. **(Optional, correlated "market days")** `TrendFollower:SharedChaseWeight` ~1.5-3 + raised global signal = fleet-wide
   correlated taker flow (appsettings:138). And the **Conviction** cohort (appsettings:267, aggressive-taker, default off) is
   a discretionary taker leg for later.
- This IS the "random walk with persistent power" Kiesh wants — persistence comes from the **taker-momentum cohort + a
  non-pinning anchor**, NOT from cranking RegimeDrift (leave RegimeDrift alone).

### CHEAPEST NEXT TEST (A/B, isolate — do NOT change five things)
Arm A = TrendFollower `Enabled+TakerCoupling`, `Strength` at the first ladder rung, `UsePreviousDayAverage=false`;
Arm B = current prod. **Decision metric:** does price LEAVE the ±0.3% band and DWELL (fraction of minutes out-of-band) +
trend-run length + breakout survival; **guards:** drift bounded inside `AbsoluteCapMax ×3`, conservation/CK clean. Point the
live client at Arm A (5083). NEVER flip a realism default on prod unattended — env-reversible A/B first, owner sign-off, then ship.
**Rollback of the current prod knobs (if wanted):** delete the 4 env lines on the box + `up -d server`.
