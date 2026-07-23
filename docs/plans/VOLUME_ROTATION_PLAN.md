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

**Where it hooks (recommended A):**
- **A. Multiply the composition activity:** in `AiBotDecisionService` where `CompositionActivity(stockId)` is consumed (or inside a new `BotActivityService.CompositionActivity(stockId)` wrapper), return `H(stockId, now) · act`. Hot names get higher `act` → more taker upgrades + bigger size + more bursts = more VOLUME; cool names quieter. Directly the "hot stock" channel, and it rides the existing CK-safe composition clamps (Floor/Cap still bound it). **This is the cleanest volume lever.**
- **B. (or additionally) rotate the StockProfile amplitude:** scale `SentimentAmplitudeMult` by `H` so the hot set is also more VOLATILE, not just higher-volume. Layer on top of the static class (a Meme stock stays characterful; rotation picks the current movers). Optional — do A first (volume), add B if Kiesh wants the hot names to also swing more.

**Config** `Bots:Activity:HotRotation:{ Enabled(false), WindowSec(14400), HotFraction(0.2), Boost(2.0), BlendSec(1800), DailyLayer(false) }`. `Enabled=false ⇒ H≡1 ⇒ byte-identical` (early-return 1.0, no hash drawn). `Boost=1 ⇒ H≡1` too.

## 4. Why median-1 / redistributive (not additive)
A market-wide volume boost would just inflate everything (and stress the load-scaler + CK clamps). Rotation should REDISTRIBUTE: the total activity budget stays ~constant, but concentrates on a rotating subset. So `H` is centered at 1 (equal hot/cool counts), keeping aggregate volume, drift, and ret_acf stable while the CROSS-SECTIONAL leaders rotate. That also keeps the §1 metrics (drift ~0-positive, ret_acf ≈ −0.1, calm typical moves) unchanged in aggregate — the rotation is a texture/variety win, not a regime change.

## 5. CK-safety + acceptance
- **CK=0 by construction:** `H` only scales the composition activity multiplier, which changes order composition/size within existing Floor/Cap and downstream cash/room/depth clamps — no orders/balances created. Same class as the shipped composition seam (CK-clean).
- **Acceptance (A/B soak, autonomous):** ON vs OFF arms; verify (a) the top-volume/top-|ret| stock SET differs across 4h windows on the ON arm and is STATIC on OFF; (b) rotation is smooth (no volume discontinuity at window edges); (c) aggregate drift/ret_acf/move-freq ≈ unchanged (redistributive); (d) CK=0. Owner eyeballs the client: does a different name lead each day?

## 6. Council-advice questions (a light council pass, per Kiesh wanting council advice)
- Hook A (composition activity) vs B (profile amplitude) vs both — which best delivers "hot stocks rotate" without over-coupling?
- Window: 4h vs daily vs layered — and is the cosine blend the right smoothing, or does rotation want a slow OU drift of per-stock hotness instead of discrete windows + blend?
- Redistributive (median-1) vs a mild net boost — does keeping aggregate volume fixed matter, or is a small live-market volume lift desirable?
- Interaction with the STATIC StockProfile classes — layer vs replace; does a "Calm" blue-chip ever becoming the hot mover break the personality intent?

## 7. Rejected / deferred
- Rotating by REASSIGNING StockProfile CLASSES per window (heavier, disrupts the "stable personality" intent — layering H is cleaner).
- Random (RNG-stream) rotation (breaks reproducibility/soak-comparability — use a pure hash of (stockId, window)).
- A market-wide volume increase (inflates everything, not rotation — the ask is different LEADERS, redistributive).

**Fire-contract notes (when approved):** branch `perf/admin-table-time-indexes`; default-OFF flag `Bots:Activity:HotRotation:Enabled`, byte-identical off; CK=0 (composition-seam class); pure hash (no RNG stream); scope = `BotActivityService`/`StockProfileService` + `AiBotDecisionService` consume point + appsettings; never `/Tools`. Acceptance = §5.
