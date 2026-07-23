# Pre-prod PRUNE proposal (#171) — dead-end default-off levers

**Purpose:** before the fresh prod reseed, remove the levers the realism arc conclusively found DEAD-END (null / no-bake) that are
NOT in the ship bundle and NOT a deliberately-held tool — to shrink the config + code surface a fresh prod population carries.

**Kiesh-gated.** This is the *proposed* list for your OK — nothing is removed until you confirm. "Hold" = ambiguous → your call.
Per lever, removal = its `Bots:*` config key(s) + `_comment` in `appsettings.json` · its read + branch in the service code · its
tests. Bundle all OK'd prunes into ONE build (`dotnet test` full suite green + one client build). The 4 excluded working-tree files
stay untouched. Best done WITH the reseed (both are "final alterations" before the fresh population).

---

## KEEP — the ship bundle + baked levers (do not touch)
`Sentiment:GlobalSigmaMult` / `PerStockSigmaMult` (correlation) · `ExogShock` + `GlobalCoFire*` (co-fire) + `SectorCount`/`SectorFraction`
(sector, default-off overlay) · `Fx:*` (FX-damp) · `Sentiment:RegimeDrift` (per-stock character) · `RecentAnchor` (damping, baked 0.35) ·
the baked anchors / caps / cash-homeostasis / dip-buy / buy-stops (`ValueAnchor`, `Fundamental`, `GeometricBand`, `DipBuyStrength`,
`Advanced:BuyStopFraction`) · `MarketMakerQuoting` (strategy-0 two-sided quotes).

## RECOMMEND PRUNE — validated dead-end, default-off, not ship/not-a-tool
1. **`Sentiment:CoMovement`** (+ `CoMoveShift` in `FundamentalService` + its 5 tests) — NULL for cross-stock correlation across 6 soaks
   (full sentiment+fundamental matrix, conclusive); the shared-FLOW co-fire superseded it as the correlation mechanism. The clearest dead-end.
2. **`Sentiment:SlowRingDamp`** — damping attempt that BACKFIRED (damping the slow rings thinned the market → bigger extremes); the
   shipped damper is `RecentAnchor` 0.35. Dead-end for its purpose.
3. **`Imbalance:Inertia:SentimentModulated`** (sentiment-mod-inertia) — NULL / slightly WORSE for correlation (still mean-reverts even
   conditioned on shared sentiment). Explicit prune-candidate in the arc.
4. **`TouchTightenPrc`** (code-default 0) — no-bake (didn't cut the bid-ask bounce; chaser/co-fire flow swamps it). Superseded by the
   baked `BounceReference=mid`.
5. **`RefillThrottle`** (+ its Probe telemetry) — no-bake (the event-gated quote-withdrawal was absorbed across all 3 dials; the 1-min
   ret_acf −0.43 is structural, not a refill problem).

## HOLD — ambiguous, YOUR call (default-off tools / possible-future mechanisms)
- **`Sentiment:GlobalShock`** — perceptual correlated crashes (quant corr capped ~0.08); a candidate for the *future* "elevator down"
  crash process (scorecard: drift-vs-crash = two processes). KEEP if you'll build the crash mechanism; prune if co-fire covers correlation.
- **`ValueAnchor:Elastic`** (+ `ElasticDeadbandPrc`/`ElasticPower`) — modest ret_acf help; your soft elastic-band vision. KEEP if you
  want the deadband+superlinear shape; prune if the geometric ×3/÷3 cap suffices.
- **`TrendFollower:*`** (CohortFraction/Strength/TakerCoupling/SharedChaseWeight/…) — the buyProb version was DEAD, but the taker-MOMENTUM
  version made trends STICK on the chart (you liked it live). Superseded by co-fire for *correlation*, but the momentum-taker is a distinct
  chart-realism tool. KEEP if you want momentum-persistence on the roadmap.
- **`Imbalance:ReactionPersistence`** (+ `TakerCoupling` etc.) — validated a REAL but modest, gain-saturated lever (fast reaction +
  separate persistence + deeper book + tighter downside). Principled; a keep-as-tool candidate.
- **`PerceivedPriceDesync`** — the cleanest lever of the ret_acf arc (drift-free, clustering-safe, direction-right) but sub-gate. Shipped
  default-off. Keep-as-tool candidate.
- **`BearShortStrength`** — modest bulk up/down symmetry, drift-neutral at 1.0. KEEP if you want the symmetry option; prune if the current
  (mild) asymmetry is acceptable.
- **`Jumps:*`** (fat-tail aggressor) — validated, no-bake ALONE, HELD as the igniter for a future jump+anchor combo. KEEP if that combo is
  still on the table (the fat-tail kurtosis gap is real).
- **`MarketMaker:Enabled`** (dedicated MM-house cohort, `RequoteThresholdBps` etc.) — inert-alone + unseeded (the strategy-0 MM-quoting is
  a separate, active path). Keep-as-tool or prune.
- **`Sentiment:PriceReaction`** (+ `Mom*`) — contrarian price→sentiment feedback; verdict never cleanly pinned. Your call.
- **`SmoothedPriceHalfLifeSec`** (default 0) — the R-final time-based EWMA-perception lever; superseded by the bounce/VWAP findings.
  Likely prune — but confirm no kept path reads it first.

---

*Rationale sources: the realism-arc close-outs + `snuggly-baking-nova.md` hub + memory `project_market_realism_v2`. Verdicts are RELATIVE-metric
conclusions (valid at any fleet scale). Nothing here changes prod behavior until removed + reseeded — all are default-off today.*
