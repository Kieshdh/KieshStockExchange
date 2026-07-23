# CANDLE NATURALIZATION PLAN — make the candle history flow more naturally

**Status: DESIGN ONLY — awaiting Kiesh's final say. Do NOT build yet.** Produced via the ultradesign
pipeline (feasibility → 3 architects → 5-advisor council). Owner ask: some instances in the candle history
look "ugly"; clean old candle data + make new candles born cleaner; specifically "don't aggregate the bigger
candles fully — use a different aggregation method." An existing wick-trimmer (v1) already ships.

---

## 0. What "ugly" is (measured on prod, 1-min USD candles, 24h, 50,094 candles — single-currency, so NOT the FX-merge artifact)
- **Big gaps** (Open vs previous Close): p95 0.15% / p99 0.31% but **max 6.39%**; ~25 candles >1%, 8 >2%. Discontinuities.
- **Huge wicks** (High−Low range / Open): p99 2.43% but **max 21.14%**. Spike-and-revert candles.
- **Flat candles** (O=H=L=C): **803 (1.6%)** — dead spots (illiquid minutes).
- Acceptance = re-measure these three numbers post-change; they should drop, WITHOUT flattening genuine moves.

## 1. Feasibility facts (Repo Facts — verbatim, transcribe don't invent)
- Table `"Candles"` (Postgres): `CandleId, StockId, Currency, BucketSeconds(60/300/900/3600/14400/86400 = 1m/5m/15m/1h/4h/1d), OpenTime, Open, High, Low, Close, Volume, TradeCount, MinTransactionId, MaxTransactionId, MarketMood, MoodMid, MoodSlow`.
- **Existing wick-trimmer (v1, SHIPPED, default-off):** `Candle.HLMinFillSize` static, set in `KieshStockExchange.Server/Program.cs:63-64` from `Candles:HLMinFillSize` (default **0 ⇒ byte-identical**) — "fills below this size are [excluded from the candle H/L]" (odd-lot-analog wick policy, commit `59db664`; wick filter baked ON in prod `ddfff19`). This filters spurious odd-lot wicks at 1-min BUILD time.
- **Aggregation (the lever for Kiesh's steer):** `CandleService.Aggregation.cs:42` `public Candle AggregateCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, bool requireFullCoverage = true)`. Line 75: `High = ordered.Max(c => c.High), Low = ordered.Min(c => c.Low)` — **raw min/max**: the aggregate carries the WORST child wick, so one ugly 1-min wick pollutes 5m/15m/1h/… `Open = ordered[0].Open; Close = Candle.VwapClose ? WeightedClose(ordered) : ordered[^1].Close; Volume/TradeCount = Sum`. `AggregateMultipleCandles` (:91) groups + calls it; `BackfillUpwardAsync` (`CandleService.Maintenance.cs:18`) aggregates UP + `UpsertCandlesAsync`.
- **Read/serve:** `CandleService.Read.cs:18` `GetHistoricalCandlesAsync(stockId, currency, resolution, fromUtc, toUtc, ct, bool fillGaps=false)` — serves hot ring `_recent` (line 32) → DB (:43) → **rebuild-from-Transactions if a resolution is missing** (:47-58, `ReplayTicksBuildClosed` + `PersistAndPublishAsync`) → `FillGaps` (:63). Served via `CandleController` + `SignalRCandleService`; rendered client-side by `CandleRenderer`.
- **Source of truth = Transactions.** Conservation/CK is tied to Transactions/orders, **NOT candles** — candles are display data. Reseed preserves candles; retention prunes old fine candles (higher-res inherits ugliness). `e82bcd9` already fixed a dual-listed USD+EUR merge that faked ~7% FX-gap wicks (so filter one currency in any measurement).

## 2. Council verdict (5 advisors on 3 architect drafts)
- **Read-time, never destructive — UNANIMOUS.** A destructive stored rewrite is reverted the moment `GetHistoricalCandlesAsync`'s rebuild-from-Transactions fires for a pruned/missing range, and must re-aggregate the whole hierarchy. Rejected.
- **Close is immutable — UNANIMOUS.** The Close series IS the real trajectory; only Open/High/Low may move, always keeping `Low ≤ Open,Close ≤ High`.
- **HARD-CLAMP, not resample.** 4/5 reject Architect-2's "signature-match resample" (Uniform·cap to a clean distribution) — it *fabricates intra-bar structure that never traded*, harder to eyeball and to defend. Cap/clamp, don't invent.
- **Determinism from a CONTENT-derived seed** `hash(StockId, bucket-start, resolution)`, **NOT CandleId** (a rebuilt candle has no stable Id → flicker between fetches). For flats' micro-jitter.
- **Prove byte-identical-off with a TEST**, not a claim: OFF = early-return before any copy/transform; a diff/hash harness asserts OFF output ≡ raw across ring/DB/rebuild. Add a persistence-guard assert that the naturalizer output is NEVER passed to Upsert/Persist.
- **Honesty:** cap-don't-erase (preserve the move's direction/sign); **flats with zero real trades STAY dead** (jittering them fabricates volume-less action). A genuine 6.4% news gap should be softened, not hidden.
- **The `?naturalize=0|1` read toggle** (Architect-1) is the killer A/B: instant same-client raw-vs-smoothed, zero redeploy — keep it.
- **Split verdict on cross-resolution coherence:** per-resolution-independent clamping (A1/A3) can make a 5m wick contradict its 1m children on zoom. Architect-2 solved it by aggregating up from the naturalized 1m. **★ Kiesh's steer solves it more permanently at the source (§3.B).**

## 3. Recommended design — THREE layers (Kiesh picks which to build; recommended order B → A → C)

### A. Tune/extend the EXISTING v1 — `Candles:HLMinFillSize` filtered-tape H/L (already shipped, generation-time)
The 1-min wick filter already exists and is baked ON in prod. It kills odd-lot spike wicks at build time. **Action:** verify its live value on prod, and consider raising it / making it size-relative (a fill counts toward H/L only if ≥ max(HLMinFillSize, k·median-fill)) so thin-book sweeps stop printing 21% wicks. Low-risk, generation-side, already default-off-safe.

### B. ★ HEADLINE (Kiesh's steer) — a WICK-TRIMMED AGGREGATION method for bigger candles
Today `AggregateCandles` takes raw `Max(High)/Min(Low)` of children, so one ugly 1-min wick dominates every higher timeframe. Replace with a **trimmed** policy under a flag `Candles:AggregateWickTrim` (default off ⇒ raw max/min ⇒ byte-identical):
- Keep `Open = first child Open`, `Close = last child Close` (real trajectory) and `Volume/TradeCount = Sum` (honest).
- Compute the aggregate body `[min(O,C), max(O,C)]`, then set aggregate `High = min(Max(child.High), bodyHi + WickCap)` and `Low = max(Min(child.Low), bodyLo − WickCap)`, where `WickCap = max(WickBodyMult · bodyHeight, WickPctFloor · Open)` — i.e., the aggregate wick is capped to a multiple of its OWN body (a quant-defensible "no wick longer than N× the body"), not the worst child extreme. (Alternative to evaluate in the A/B: a high **percentile** of child highs/lows instead of the absolute max/min — e.g. 98th pctile — which is simpler and still lets one child spike through slightly.)
- **Why this is the right permanent fix:** it makes every higher timeframe born-clean AND cross-resolution-coherent by construction (a 5m never shows a wick its own body can't justify), directly answering the council's zoom-contradiction risk — without the read-time cost or the "fabricate structure" objection. It's generation-side, so it also cleans LEGACY higher-res via a one-time `BackfillUpwardAsync` re-run (re-aggregating derived candles is safe — they're not source-of-truth).
- Files: `CandleService.Aggregation.cs` (the trim in `AggregateCandles`; a new `CandleAggregationMath.TrimmedHighLow(...)` pure helper — unit-test it); flag + knobs in appsettings `Candles:{AggregateWickTrim(false), WickBodyMult(~2.5), WickPctFloor(~0.006)}`; a one-time re-aggregate is just `BackfillUpwardAsync` with the flag on.

### C. Read-time cosmetic naturalizer — for LEGACY 1-min history that predates A/B (the shrinking patch)
Layers A+B clean NEW candles and re-aggregated higher-res, but legacy STORED 1-min candles keep their gaps/wicks/flats. For those, a read-time cosmetic (council's A1 spine):
- ONE choke point: a pure `NaturalizeForDisplay(list, resolution)` called inside `GetHistoricalCandlesAsync` AFTER source-resolution (ring/DB/rebuild all converge) and — resolve the council's ordering note — coordinate with `FillGaps` (naturalize real candles; don't jitter synthesized gap-fillers). Operates on **defensive copies**; never feeds Upsert/Persist.
- HARD-CLAMP: gap → pull Open toward prevClose, capped (keep sign); wick → cap excess beyond body to `max(WickBodyMult·body, rolling-median-range·price)`; flat with TradeCount>0 → deterministic micro-range from the **content seed**; flat with TradeCount=0 → **leave dead**. Re-assert OHLC invariant per candle; fall back to raw on any violation.
- Flag `Candles:NaturalizeHistory:Enabled` (default false) + `?naturalize=0|1` read override for instant A/B. Byte-identical off (early return) + a diff test.
- Note: apply per-resolution independently is acceptable ONCE B is in (B makes the stored higher-res coherent; C only touches legacy pre-B ranges).

**REJECTED:** destructive stored candle rewrite (rebuild-revert + hierarchy re-agg + irreversible); Architect-2 signature-match resample (fabricates structure); jittering zero-trade flats (fakes activity). **CONSIDERED (Architect-4):** doing C client-side in `CandleRenderer` instead of the server — keeps the server perfectly faithful, but loses the single-source `?naturalize` server A/B toggle and duplicates the logic per client; **server chosen** for C, but this is a real alternative if Kiesh prefers the server stay byte-faithful.

## 4. How Kiesh A/B-eyeballs before committing
Build the branch, enable on the ON arm (port 5083) / or hit `?naturalize=1`; open the same USD stock at the known-ugly timestamps (the ~25 gap candles, the 21% wick, the flat runs) across 1m/5m/1h; compare against OFF (byte-identical). For B, zoom between 1m↔1h and confirm a big candle's wick matches its children. Ship default-OFF; flip per-arm.

## 5. Fire-contract notes (for the eventual build, when approved)
Branch `perf/admin-table-time-indexes` (current dev tip). Default-OFF flags, byte-identical when off (proven by a diff/hash test). CK-invariant: candles are display-only — Transactions/conservation untouched; naturalizer output never persisted (guard-assert). `file:line` anchors above. Scope fence: `CandleService.*` + `CandleAggregationMath` + appsettings + one client toggle; **never `/Tools`**. Acceptance = §0 numbers drop + no genuine-move flattening + 730+ tests green + owner eyeball.
