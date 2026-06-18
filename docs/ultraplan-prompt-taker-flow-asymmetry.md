# ULTRAPLAN HANDOFF — taker-flow asymmetry / residual down-drift (sensitivity report §b)

**Prompt to feed the Ultraplan planner. Deliverable: a `git apply`-clean PATCH FILE + a ready-to-paste bake prompt
for local Claude (apply→build→test→soak→bake). Branch `feature/bot-market-realism-v2`.** This closes the LAST open
ultraplan issue from `docs/bot-sensitivity-tuning-report.md` §(b). [[project_sensitivity_tuning_overnight]],
[[project_market_balancing_value_anchor]].

## The issue (now ROOT-CAUSED with data — supersedes the report's 3 hypotheses)
The market shows a persistent ~**−2.3%/150m** average down-drift (was −3.15% pre-tuning; bounded, conservation-
clean, NON-blocking but unwanted — the user wants price to "hug the seed"). Root cause, confirmed on a 533k-trade
soak DB (`kse_soak_bake`, baked config):
- **Plain MARKET (taker) orders are 50/50 balanced** (Buy 166,184 / Sell 165,971) — the plain decision path is
  symmetric (verified in code: `isBuy` and `isMarket` are independent draws; `effectiveUseMarket` has no side
  branch).
- **ALL the taker sell-skew is the protective-stop population: 80,512 stop orders, 100% SELL, ZERO buy-stops.**
- Code reason: `BotAdvancedKind` only has `StopMarketSell` / `TrailingStopSell` ("protect a held long") +
  `ShortOpen` (market sell). **There is NO buy-stop kind to protect a held SHORT.** So every protective stop is a
  sell; bots run net-long (plain limits are buy-heavy: 397k buy / 364k sell), and their sell-stops fire market
  sells on dips → one-sided downward taker pressure with no symmetric buy-stop counterforce → the down-drift.

## Scope (the fix) — symmetric short-side protective stops
Add the buy-side mirror of the protective-stop feature so a held SHORT can be protected by a **StopMarketBuy /
TrailingStopBuy** that fires a market BUY on a rally, exactly mirroring the long-side `StopMarketSell /
TrailingStopSell`. This restores taker-flow symmetry (the down-drift's cause) WITHOUT damping the feature. Touch
points (mirror the existing sell-stop path):
- `AiBotDecisionService`: new `BotAdvancedKind.StopMarketBuy` + `TrailingStopBuy`; the `StopProb`/`TrailingProb`
  gate should produce a buy-stop when the bot holds a NET SHORT (mirror of the "held long → sell-stop" branch at
  ~L505-L645). Reuse the slippage-cap + trigger-distance geometry symmetrically across `isBuy`.
- The arm/fire path (`AiTradeService` BatchArms route + `StopTriggerWatcher` / wherever sell-stops arm and trigger)
  must support buy-stops (trigger when price RISES through the stop, fire a market buy).
- `MatchingEngine` / `OrderExecutionService`: confirm buy-stop trigger geometry + the market-buy fire is symmetric
  to the sell-stop path; reservation model for a buy-stop (reserves CASH to cover the cover-buy, mirroring the
  sell-stop reserving SHARES).
- Per-bot probs / persistence: the existing `StopProb`/`TrailingProb` can gate both sides (pick side by current
  inventory sign), so NO new `/Tools` seed column is required (keep `/Tools` untouched).

Flag-gated default-OFF (`Bots:Advanced:ShortProtectiveStops` or similar), byte-identical when off. CAUTION: this
adds order-engine behaviour on the conservation-critical path — the buy-stop's cash reservation + fire must keep
ConservationProbe=0 / CK=0 / ReservationAuditor in tolerance.

## ALTERNATIVE (cheaper, if the full feature is too big for one round)
If symmetric buy-stops are too large: a **config damping** of the one-sided sell-stop pressure — e.g. scale
`StopProb`/`TrailingProb` by inventory side, or a small symmetric buy-pressure offset — could shrink the skew as an
interim. Lower realism value (treats the symptom), but flag-gated + soakable. The council should weigh full-symmetry
(correct, structural) vs damping (cheap, partial).

## Hard constraints / invariants
- Conservation is sacred: ConservationProbe=0, CK_Funds/CK_Positions=0, ReservationAuditor in tolerance.
- Lock order book → per-user gates (sorted keys) → DB tx. The buy-stop fire goes through the same engine path.
- Flag-gated, default-OFF, byte-identical when off. `Bots:Advanced:MaxPerTick` stays the fallback.
- Determinism: seed reproducibility (ascending-aiUserId, no RNG perturbation) — the new buy-stop branch must draw
  from the per-bot RNG in a flag-gated way that keeps the flag-OFF draw sequence byte-identical.
- Touch nothing in `/Tools`.

## Deliverable contract
ONE `git apply --check`-clean patch (one shot), flag default-off + byte-identical off, ships equivalence +
conservation tests (buy-stop arms/fires/settles conserved; sell-stop path unchanged) + a determinism test. PLUS a
ready-to-paste **bake prompt for local Claude**: apply→build (fix any compile gaps)→`dotnet test`→**A/B soak**
(flag off vs on, baked-realism env, lowercase DB, absolute script path) measuring the **avg-drift delta** (does the
down-drift shrink toward 0?) + **market-order buy/sell taker balance** (should move from ~40/60 toward 50/50) + the
full conservation battery → **bake default-on only if conservation-clean AND the drift/taker-balance measurably
improves** (re-run the winner once; trust deltas that clear the run-to-run noise).

## IMPLEMENTED + EMPIRICAL FINDING (2026-06-19) — buy-stops shipped, but inert without short inventory
Bidirectional protective stops were IMPLEMENTED (flag `Bots:Advanced:ShortProtectiveStops`, default-off, commits
`5c6fd78`/`af46d5e`): a bot's protective stop now protects its LARGEST exposure — long ⇒ sell-stop, short ⇒
buy-stop — routed through the engine's existing conservation-tested buy-stop arm path. 191/191 + full-stack build,
flag-off byte-identical. **BUT the A/B soak produced ~0 buy-stops because the bot population is ~98% long-only:**
on a 20k-active soak, **20,000 bots have a dominant long and only 400 hold ANY short** (none dominant-short). A
protective buy-stop requires a held short, so with almost no shorts the feature is INERT against the down-drift.
⇒ **The down-drift's true substrate is the net-long population (tiny per-bot `ShortProb`), not the stop mechanism.**
The buy-stop is the correct, necessary PRIMITIVE but only bites when shorts exist. **Real fix = PAIR it with more
short inventory: raise per-bot `ShortProb` (+ ShortBracket) in `Tools/Person.py` and reseed (a /Tools task), then
flip `ShortProtectiveStops` on so the new shorts are protected symmetrically.** Bake decision: **keep
`ShortProtectiveStops` default-off** (no measured drift win possible until shorts exist); shipped + available.
Alternatively, if the bounded drift (gym soak: −0.67%/90m, within the ≤5%/4h budget, beyond50=0) is acceptable,
no further action is needed.

## Verification queries (local Claude, post-soak)
- Taker balance: `SELECT "Stop","Side",count(*) FROM "Orders" WHERE "Entry"='Market' OR "Stop"<>'None' GROUP BY 1,2`
  — buy-stops should now appear (Stop=Buy > 0) and the market buy/sell split should rebalance toward 50/50.
- Drift: `scripts/balance-drift.sql` avg% over the steady tail (should trend toward 0 vs the OFF arm).
