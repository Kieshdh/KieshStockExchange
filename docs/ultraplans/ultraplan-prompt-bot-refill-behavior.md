# Ultraplan handoff — bot-behavior REFILL RESPONSE (the real wall)

**Status:** DRAFT, ready to fire IF the chart gap is confirmed real (see Pre-flight). Default-off lever, bot-decision
layer only — NO matching-engine changes.

## Why (the conclusion of the whole realism arc)
Every realism lever to date has been ABSORBED by one constraint: the **fast-refilling order book**. The decisive
proof — fat-tail jump telemetry: a dramatic jump targets **4-7%** but **realizes ~0.1%** price impact (30+ slices,
~2.25M gross notional), and the **adaptive anchor** (which loosened the ±20% veto cap to ±35%) changed nothing because
price never moves far enough to test the cap. Volume can't move price; loosening the cap can't move price.

**The council's unanimous reframe (5/5, 2026-06-27):** this is **not** a matching-engine problem — it's a **bot-behavior**
problem. ~20,000 bots re-quote resting orders at the **old anchor/perceived price every ~1s tick**, with no inventory
memory, no trend-awareness, and globally-uniform refill. So the instant a taker eats the ask, another bot re-posts an
ask at the *old* level → the wall instantly reforms → the mid never moves. **The fix is to make refill RESPOND to the
move**, in the bot-decision layer we already tune (CK-safe by construction; perf-neutral-to-POSITIVE because it means
*fewer* resting orders on movers, which is *less* load on the commit-bound engine).

This also explains why `PerceivedPriceDesync` (already shipped default-off, `9b440d9`) was too weak: it's a **global,
constant** dribble. It needs to be **event-gated** — concentrated exactly where directional pressure exists.

## Pre-flight (do FIRST — cheapest possible disproof, may make this whole lever unnecessary)
At native per-stock scale the soak charts already look realistic (`logs/aa_off_45m.png`): clean held trends, real
consolidations, substantial bodies (range-eff 0.44, in the real 0.3-0.5 band). So **before building anything, confirm
the perceived "tiny moves / big wicks" isn't a CLIENT chart-rendering artifact** (fixed/wide y-axis scaling, coarse
candle bucket, or an aggregate/index view that averages independent stocks to flat). If it's the client, the fix is a
cheap View-layer change, NOT this lever. Only fire this ultraplan if the gap survives at proper client scale.

## What to build (primary lever — Executor's synthesis)
**`Bots:RefillThrottle`** — event-gated quote withdrawal on movers. Default OFF ⇒ byte-identical.
- **Signal:** per-stock short-window **signed order-flow / momentum** (reuse what's already tracked; e.g. a fast EWMA of
  signed traded volume or the existing perceived-price slope). No new heavy per-tick cost.
- **Action when |signal| crosses a threshold on a stock:** the maker/limit cohort on THAT stock transiently (a) widens
  its quote offset on the *resisting* side and/or (b) skips re-posting (raises a skip-repost probability) — i.e. it
  **stops refilling the wall the move is pushing into**, so the next unit of pressure shifts the mid, and the move sticks.
- Reuse the `PerceivedPriceDesync` plumbing (per-bot perceived-price dispersion) but drive its strength from the
  per-stock event gate instead of a global constant.

## Alternative / complementary flavors (council; pick or A/B)
- **Trend-aware reservation (Outsider):** a cohort's resting-quote reference tracks the *fast mid* (chase, don't fade),
  so refilled orders sit at the NEW level. Most directly "sticky."
- **Post-fill quote cooldown (First-Principles):** a maker just filled on the ask does not instantly re-post at that
  level (brief per-bot cooldown).
- **Shared inventory-risk limit (Contrarian):** a fleet slice stops quoting a side when its (shared) inventory passes a
  risk limit — emergent depth withdrawal, like real MMs pulling on risk.
- **Refill-intensity as a platform (Expansionist):** expose per-stock replenishment intensity as a control surface;
  correlating it across stocks via the market factor could finally make co-movement / fat-tails / vol-clustering bite.

## Hard constraints
- **CK=0** must hold (ConservationProbe) — fewer/wider resting orders create/destroy nothing, so this is structurally safe.
- **MaxBotCap=20k at ALL times** — the lever must be perf-neutral or perf-positive (it reduces order volume on movers).
- **Bot-decision layer only** — NO changes to MatchingEngine / SettlementEngine / the order book itself.
- Default-off, byte-identical when off; new determinism tests mirroring `AdaptiveAnchorDeterminismTests` /
  `CoMovementDeterminismTests`.

## Validation (local, after the patch lands)
1. `git apply` → build Server (Debug) → `dotnet test` (expect green + new determinism tests).
2. Commit default-off; reseed not needed (config-only, no new users).
3. **A/B 45m screen, OFF vs ON, max-2-servers**, via `scripts/kse-balance-soak-p.ps1` with `Bots__RefillThrottle__*`.
4. **THE key metric = jump-impact:** run with the dramatic jump ON both arms; harvest the jump probe — does
   `realized` per jump rise from ~0.1% toward the 4-7% target on the ON arm? That's the direct test of "the book now
   yields." Plus: ret_acf toward 0/+ , range-eff↑, clustering preserved, drift ≤5%/4h, CK=0.
5. Contrarian's caution: sanity-check that thinning is *sufficient* (a small/synthetic check) before trusting the 20k soak.
6. If the screen is promising → 2h confirm + dial sweep (threshold × strength) → council the bake.

## Scope note
`/Tools` (bot seed generation) only if the lever needs a new per-bot trait; otherwise pure runtime config + bot-decision
code. Prefer runtime-only (no reseed) for the first cut.
