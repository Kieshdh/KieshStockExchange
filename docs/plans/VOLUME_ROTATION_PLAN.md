# VOLUME / HOT-STOCK ROTATION PLAN — different hot stocks every day / 4h window

**Status: DESIGN ONLY — awaiting Kiesh's say + a council-advice pass. Do NOT build yet.** Normal design method
(Kiesh's lean; the change is a single bounded lever, not architectural → no full ultradesign). Owner ask:
"rotate the volume in different days / 4-hour windows so the market has different hot stocks every day."

---

## 1. The problem + why it's the right lever
Today per-stock character is **STATIC**: `StockProfileService.Get(stockId)` assigns each stock a fixed volatility
class (Calm/Normal/Volatile/Meme) from a stable hash of its id → fixed `SentimentAmplitudeMult` (0.65…2.0),
`FundamentalSigmaMult`, `OverheatCapMult`. So the SAME names are always the hot/quiet ones — the market's
"leaders" never change. Kiesh wants the hot set to **rotate** so each day/window feels alive with new movers.

## 2. Feasibility facts (Repo Facts — verbatim)
- **Global activity:** `BotActivityService` (`_gNormCache`, line 55 = "baseline-removed G, median 1 — the composition seam's global factor"; computed :117-120 `exp(_g − ½σ²)`). ONE market-wide activity level, not per-stock.
- **Per-stock hotness = the composition seam:** `AiBotDecisionService.cs:857 var act = _activity.CompositionActivity(stockId);` then `act = clamp(gNorm^GExp · S, Floor, Cap)` (Composition `_comment`) drives the per-stock taker-upgrade (hot names limits→slippage, prob `1−act^−TakerExp`), the limit-tier band (`:1957 CompositionActivity(stockId)^−DistExp`), and the size coupling (`:2075 CompositionSizeMult`). Config `Bots:Activity:Composition:{TakerExp 0.5, GExp 0.5, Floor 0.4, Cap 3.0, SizeExp 0}`. **This is the volume/hot channel: a stock with higher `act` trades bigger/more-taker/more-bursty.**
- **Per-stock character:** `StockProfileService.Get(int stockId) → StockProfile(Class, SentimentAmplitudeMult, FundamentalSigmaMult, OverheatCapMult)`, deterministic from a stable id hash; feeds `BotSentimentService` amplitude, `FundamentalService` sigma, `AiBotDecisionService` overheat veto.
- **CK:** the composition seam is CK=0 by construction (it changes order COMPOSITION/SIZE, not conservation; downstream cash/room/depth clamps hold). Volume rotation inherits this — it only scales an existing multiplier.

## 3. Design — a rotating per-stock "hotness" multiplier `H(stockId, t)`
Add a deterministic, time-varying hotness `H(stockId, now) ∈ [1/Boost, Boost]` (median 1, so it REDISTRIBUTES hotness, not inflates the whole market), that rotates which stocks are boosted each window.

**Rotation function (pure, RNG-free, reproducible):**
- Window index `w = floor(epoch / WindowSec)` (WindowSec = 14400 = 4h; optionally layer a slow daily `w_day = floor(epoch/86400)`).
- Per-window per-stock rank: `rank(stockId, w) = avalanche_hash(stockId, w)` → uniform in [0,1). The top `HotFraction` (e.g. 20%) by rank are the window's HOT set (`H = Boost`, e.g. 2.0), the bottom `HotFraction` are COOL (`H = 1/Boost`), the middle baseline (`H = 1`). Because the hash mixes `w`, the ranking reshuffles every window → a different hot set each 4h/day.
- **Smooth transitions (no hard jump at window edges):** blend `H` across the boundary over `BlendSec` (e.g. 1800s) via a cosine ease between window `w` and `w+1` values — a stock ramps up as it becomes hot, cools gradually. This is what makes it look organic, not a step.
- Deterministic + reproducible (same t → same H, no RNG stream) so it's soak-comparable and CK-clean.

**★ CORE PRINCIPLE (Kiesh 2026-07-23): VOLUME ≠ PRICE-MOVE. Decouple them.** Higher volume must NOT imply a
price move — a hot name should often churn at HIGH VOLUME while staying roughly FLAT (heavy balanced two-sided
trading), and MOVE only when a directional driver (sentiment/regime/news imbalance) coincides. The composition
`act` feeds THREE channels: **TakerExp** (limits→slippage takers = DIRECTIONAL, crosses the spread, MOVES price),
**SizeExp** (bigger orders = VOLUME/notional, direction-neutral), **DistExp** (limit band). Boosting the WHOLE
`act` (the naïve "Hook A") raises the directional TakerExp too ⇒ volume welded to move — exactly what Kiesh says
is wrong. **So the hotness boost must drive the VOLUME channels, not the directional one.**

**Where it hooks (revised — volume-only, direction-neutral):**
- **★ A′ (recommended): boost the composition SIZE coupling only.** Apply `H` to the `SizeExp` path (`CompositionSizeMult`, `AiBotDecisionService.cs:2075`) so hot names trade BIGGER (more volume/notional) — but do NOT scale the `TakerExp` taker-upgrade, so a hot name's buy/sell taker MIX and spread-crossing rate are unchanged. Result: high volume, direction-neutral. Price moves only if the existing sentiment/regime flow is imbalanced at that moment → "high volume, sometimes flat, sometimes moving." (Note: `SizeExp` currently ships OFF (0) — this feature both turns it on for hot names AND makes it rotate; the load-scaler pins order COUNT, so SIZE is the intended volume-CV lever per its own `_size_comment`.) Runaway-safe: `WSelf 0` severs the fills→size Hawkes loop; `SizeCap` bounds one order; cash/room/depth clamps hold CK=0. Balanced size ⇒ balanced notional ⇒ flat.
- **A (rejected as the primary): boost the whole `act`** — raises TakerExp too ⇒ couples volume to directional move (the thing Kiesh is correcting). Only acceptable if we WANT hot names to also be directionally punchier, which is a separate choice (see B).
- **B. (deferred) rotate the StockProfile amplitude / TakerExp** — makes hot names also more VOLATILE/directional, not just higher-volume. A different claim; layer on top later behind its own flag if desired.

**Config** `Bots:Activity:HotRotation:{ Enabled(false), WindowSec(14400), HotFraction(0.2), Boost(2.0), BlendSec(1800), DailyLayer(false) }`. `Enabled=false ⇒ H≡1 ⇒ byte-identical` (early-return 1.0, no hash drawn). `Boost=1 ⇒ H≡1` too.

## 4. Why median-1 / redistributive (not additive)
A market-wide volume boost would just inflate everything (and stress the load-scaler + CK clamps). Rotation should REDISTRIBUTE: the total activity budget stays ~constant, but concentrates on a rotating subset. So `H` is centered at 1 (equal hot/cool counts), keeping aggregate volume, drift, and ret_acf stable while the CROSS-SECTIONAL leaders rotate. That also keeps the §1 metrics (drift ~0-positive, ret_acf ≈ −0.1, calm typical moves) unchanged in aggregate — the rotation is a texture/variety win, not a regime change.

## 5. CK-safety + acceptance
- **CK=0 by construction:** `H` only scales the composition activity multiplier, which changes order composition/size within existing Floor/Cap and downstream cash/room/depth clamps — no orders/balances created. Same class as the shipped composition seam (CK-clean).
- **Acceptance (A/B soak, autonomous):** ON vs OFF arms; verify (a) the top-VOLUME stock SET differs across 4h windows on the ON arm, static on OFF; (b) rotation smooth (no volume discontinuity at window edges); (c) aggregate drift/ret_acf/move-freq ≈ unchanged (redistributive — MEASURED not assumed); (d) CK=0; **(e) ★ VOLUME↔MOVE DECOUPLING: hot (high-volume) stocks show high-volume-FLAT occasions — the per-stock correlation between (volume) and (|ret|) stays LOOSE (a hot name can 2×+ its volume with |ret| ≈ baseline), NOT tight.** If boosting a hot name reliably moves it, the size-only hook leaked into direction — investigate. Owner eyeballs the client: does a different name LEAD (in volume/activity) each day, and does a busy name sometimes sit flat?

## 6. ★ COUNCIL ADVICE (5-lens, 2026-07-23) — AGREE, ship a NARROWER v1
- **Hook A ONLY, not both.** Hook A (composition activity) *is* the volume channel — it delivers "hot = high-volume/bursty" directly. Hook B (profile amplitude) couples hotness to price VOLATILITY = a different claim and the main threat to drift/ret_acf/§1. Keep them separable; **ship A first, defer B** to its own flag once A's soak proves CK=0 + targets hold.
- **Rotation model: DISCRETE windows + cosine blend for v1** (trivially reproducible, soak-comparable, easy to assert "leader set differs across windows"). BUT the honest "most natural" answer is an **OU/organic drift** where leadership *emerges and fades* — discrete has a tell (every window edge the SAME 20% flip in lockstep). ⇒ **build discrete v1 but put `H(stockId,now)` behind a clean function boundary so an OU variant drops in as v2** without touching the hook.
- **Median-1 redistributive: KEEP — it matters.** It makes "aggregate ≈ unchanged" TESTABLE + keeps this orthogonal to the global-activity/load work. A net lift would silently re-tune move-freq + collide with the load-scaler. Don't blend two effects in one flag.
- **★ Biggest risk = NOT CK (that's clean) — it's:** (a) **"aggregate unchanged" is EMPIRICAL, not given** — `act` enters via `gNorm^GExp` and the nonlinear taker-prob `1−act^−TakerExp`, so boosting/cutting activity is ASYMMETRIC in volume + ret_acf ⇒ **MEASURE move-freq + ret_acf on the ON arm, don't assume redistribution holds**; (b) **personality collision** — a "Calm blue-chip" becoming the window's hottest mover contradicts the static class intent ⇒ **clamp per-class Boost** (Calm caps at a smaller H) so rotation modulates WITHIN character, not erases it.
- **Missing (add to v1):** a load-scaler interaction test (do H + global activity compose or fight?); a restart-determinism check (epoch-based `w` must survive process restart — no wall-clock drift); the class-aware Boost clamp above.
- **★ COUNCIL VERDICT — build v1:** Hook A only · discrete windows + cosine blend behind a swappable `H()` · median-1 · per-class Boost clamp · default-off. **Defer:** Hook B, OU/organic drift (v2), any net lift. **Gate on CK=0 + MEASURED (not assumed) drift/ret_acf/move-freq parity.**

## 7. Rejected / deferred
- Rotating by REASSIGNING StockProfile CLASSES per window (heavier, disrupts the "stable personality" intent — layering H is cleaner).
- Random (RNG-stream) rotation (breaks reproducibility/soak-comparability — use a pure hash of (stockId, window)).
- A market-wide volume increase (inflates everything, not rotation — the ask is different LEADERS, redistributive).

**Fire-contract notes (when approved):** branch `perf/admin-table-time-indexes`; default-OFF flag `Bots:Activity:HotRotation:Enabled`, byte-identical off; CK=0 (composition-seam class); pure hash (no RNG stream); scope = `BotActivityService`/`StockProfileService` + `AiBotDecisionService` consume point + appsettings; never `/Tools`. Acceptance = §5.
