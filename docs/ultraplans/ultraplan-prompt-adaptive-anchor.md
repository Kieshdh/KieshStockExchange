# ULTRAPLAN HANDOFF — adaptive (path-dependent) anchor: make moves STICK

**Paste the block below to the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE (default-off,
byte-identical off) + a ready-to-paste bake prompt for local Claude (apply → build → test → soak → bake).**
Branch `feature/bot-market-realism-v2`. This is the council's UNANIMOUS root-cause lever (5/5, 2026-06-27) — the
fix the owner's 6 ideas were all missing. [[project_market_realism_v2]]

---

## PROMPT (paste this)

Design an **adaptive (path-dependent) value-anchor** for the AI-bot stock-exchange sim so genuine price moves
**RE-RATE the reference and STICK** (bigger candle BODIES, smaller WICKS) instead of mean-reverting to a fixed seed.

### The problem (council-diagnosed, 5/5)
Owner's complaint watching live charts: wicks too big, no large *sustained* moves, flat/stale volume. Root cause:
the value-anchor + the hard **±20% per-move cap** pull every stock back to a **fixed seed/fundamental** — a "spring
with no memory." Every push reverts → spikes-that-snap-back (wicks), no travel (small bodies), nothing trends so
volume never concentrates. 1-min return autocorr `ret_acf_lag1 ≈ −0.17` is the spring's signature. **Adding volume
(whales, stops, cash) through this spring just makes FATTER WICKS.** The only fix is letting the anchor MOVE.

### The mechanism to design — a TWO-TIMESCALE anchor
The anchor must follow price on the **intraday** scale (so a real imbalance re-rates the level and the move sticks)
while a **slow** scale stays the hard **drift/runaway bound** (so it can't run away — the months of anchor-tuning
stability must survive).
- **Fast (re-rate):** blend a **slow EMA of the TRADED price** (half-life ~10–30 min — TUNE) into the value-anchor
  TARGET, so the anchor tracks where price has actually been, not the static seed. The **±20% cap re-centers on
  this moving anchor** instead of the fixed seed — BUT the TOTAL excursion from the original seed must still be
  hard-bounded (see runaway guard) so the market can't walk to infinity.
- **Slow (bound):** keep the seed (or the existing weighted-week TWAP, `ValueAnchor:UsePreviousDayAverage` /
  `BotPriceMemoryService`) as the ultimate restoring force + a hard band (e.g. seed × [1 ± MaxTotalExcursion],
  MaxTotalExcursion ~0.30–0.50) so drift stays ≤5%/4h over the long run even though intraday re-rates freely.
- **The single most important dial = the blend weight / fast-EMA half-life** (how hard the anchor follows price):
  too high → no anchor (random-walk/runaway); too low → today (reverts). The bake target is the weight where moves
  persist for minutes (sticky bodies) yet the slow bound still caps multi-hour drift.

### Reuse, don't bolt on
- `AiBotDecisionService.Fundamental()` is the anchor target the value-anchor tilts toward; `FundamentalService`
  (OU walk → seed) + `BotPriceMemoryService` (the TWAP/recent-EWMA, already powers `UsePreviousDayAverage` +
  `RecentAnchor`) already maintain per-stock price memory. The adaptive anchor is a **new blend of an
  intraday traded-price EWMA into that target** — extend `BotPriceMemoryService` / the anchor target read, don't
  add a parallel service. `RecentAnchor` (30-min EWMA, Strength .10) is RELATED but it's a *pull toward* the EWMA,
  not a *re-centering of the cap/target* — this lever makes the EWMA the anchor itself.
- `ValueAnchor:AbsoluteCapMax` (0.20) is the per-move hard veto; `CapFromSeed` (true) currently measures the cap
  from the seed. The design must let the cap measure from the **moving anchor** for intraday room while a separate
  **MaxTotalExcursion-from-seed** hard veto prevents runaway. Keep the runaway guard provably binding.

### HARD constraints
- **CK=0 conservation** — this only changes a *reference price bots aim at* (mints/moves nothing) → must stay
  byte-clean. Verify ConservationProbe CK=0.
- **Drift ≤5%/4h** — the slow bound is the guarantee; sweep the blend weight and confirm the long-run drift holds
  even as intraday re-rates. This lever LOOSENS the very stability the market was tuned for — the two-timescale
  (fast re-rate + slow hard bound) is the safety; treat runaway as the primary risk.
- **PERF / 20k cap (owner hard requirement):** config-only, NO added orders, so the server's `MaxBotCap=20000`
  scaler is untouched. (Unlike stops/whales/turnover which add load — this must not.)
- **Default-off ⇒ byte-identical** (the project invariant). New section e.g. `Bots:ValueAnchor:Adaptive:{ Enabled
  =false, FastHalfLifeSec, BlendWeight, MaxTotalExcursion, ... }`. Add determinism tests (mirror
  `CoMovementDeterminismTests`). A liveliness log (the moving anchor vs seed per stock) so a soak confirms it tracks.

### Bake gate (2h soak, both instruments agree) — the metric that defines success
- **`ret_acf_lag1` −0.17 → ~0 or slightly POSITIVE** (momentum, not reversion), AND
- **range-efficiency `|close−open| / (high−low)` RISES** (candle BODIES beat WICKS — the literal "smaller wicks,
  stickier moves"; new scorer in `scripts/cross_stock_diag.py` / `r4_realism_score.py`), AND
- volatility clustering NOT regressed, drift ≤5%/4h, **CK=0**, and the runaway guard holds (no stock escapes the
  total-excursion band over the full soak).

### Open questions for your judgment
1. Fast-EMA half-life vs blend-weight — which is the cleaner single dial, and the safe starting value. 2. Should the
cap re-center fully on the moving anchor, or a blend of seed+anchor (partial re-rate = safer)? 3. Interaction with
`RecentAnchor` + `RegimeDrift` (both already move price) — co-enable or supersede? 4. Per-stock personality scaling
of the blend (Meme names re-rate faster than Calm)?
