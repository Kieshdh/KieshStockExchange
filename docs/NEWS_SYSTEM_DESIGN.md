# News-System Design Plan — 3-Tier Jump-Diffusion (Owner Approval)

**Verdict in one line:** The engine already implements the owner's mental model. News = **Merton jump-diffusion**: keep the existing random walk as the base driver (untouched), layer sparse **jumps** on top as three tiers. Ship a **config-only first cut** on prod-proven machinery now; fund exactly **two small code items** (chaser age-gate, permanent fundamental step) to make the "hard start → sticks → doesn't revert" shape correct. **Cut** the flash-walk, `Bots:Jumps`, Hawkes, and modulo-sector. This plan is decisive on every council fork.

---

## 1) EVENT MODEL — the shape of one news event

Price level is decomposed additively (this is the only structure that satisfies all three owner constraints at once — walk stays primary, events stick, no double-driving):

```
price(t) = WALK(t)  +  Σ permanent_steps(events ≤ t)  +  transient_overlay(t)
           └ untouched ┘  └ sticks forever ┘             └ decays to 0 ┘
```

**One event of signed, fat-tailed magnitude M splits 80/20:**

| Component | Size | Where it lives | Behaviour |
|---|---|---|---|
| **Hard impact** (t=0) | ~M | same-tick taker burst (Chaser/CoFire) scaled to \|M\| | the *print* jumps ~M immediately |
| **Permanent step** `P = α·M` | α ≈ 0.8 | log-step on the `FundamentalService` OU value | level shift; walk resumes **from the new level** — never reverts |
| **Transient overshoot** `T = (1−α)·M` | 20% | existing decaying shock channel (`ExogenousShockService`) | bleeds off over a half-life (10–60 min) |

**How it sticks:** the sticky part is delivered by **re-rating the fundamental**, not by a never-decaying shock. `AnchorTracksShock=true` composes `target = f_new × (1 + T(t))` at read time — the value-anchor now aims at the re-rated `f_new`, so the book holds price at the new level instead of pulling it back to seed. Permanence lives where permanence already lives; the walk is memoryless from the new level = the owner's "does not revert" **by construction**, with zero accumulator bookkeeping.

**The lingering chaser = free PEAD.** After the burst, the cadence-gated chaser (alive while `|T| > floor`) bleeds price into the settled level over ~15–30 min. That drift-then-stop **is** post-earnings-announcement drift — realistic underreaction as a side effect of existing machinery.

**Decay:** only the transient `T` has a decay knob (`DecayHalfLifeSec`). The permanent part has **no decay knob at all — that absence is the feature.**

**Bounding / guards:**
- Log-space symmetric steps `f *= exp(α·m)` with `E[m]=0` per tier ⇒ accumulated news injects **no hidden drift** (except a deliberate mild global down-skew).
- Cumulative re-rate hard-bounded by the **existing geometric cap** (×3 / ÷3 from seed) — the free runaway backstop; startup-refused unless `Band + Cap < AbsoluteCapMax` (same proof that already guards `AnchorTracksShock`).
- **Saturation constraint:** steady-state accumulated `|shock| ≈ λ·E[|J|]·(HL/ln2)` must sit **≤ Cap/3**, or the accumulator rides the wall and later events get soft-walled to nothing ("news stops working").
- All flow rides `OrderEntry → Match → Settle` — CK=0 discipline unchanged, no naked flow.

---

## 2) THE THREE TIERS

Sim runs real-time (a "day" = 24h; owner watches ~1–2h sessions). Frequencies are tuned to the owner's chief complaint — **too frequent** — so events are punctuation, not texture.

| Tier | Frequency | Magnitude (frac of seed) | Scope | Persistence (α) | Maps to lever(s) |
|---|---|---|---|---|---|
| **Individual** (dominant) | ~1 event / stock / **24–36h** (`MeanIntervalMinutes ≈ 1440`) ⇒ ~1–2 events/hr *somewhere* across 50 stocks; watched stock gaps ~once/session | fat-tailed **1–12%**, mostly 1–3%, rare 8%+ (`Min 0.01 / Max 0.12 / Exponent 2.5`) | per-stock | 0.8 | RandomShockSource per-stock Poisson + per-stock Chaser burst |
| **Global** (rare, mid) | every **~3–5 sim-days** (`GlobalFraction ≈ 0.25`) | shared draw, **1–3%** per stock, mildly **down-skewed** (elevator-down arc) | all stocks | 0.7 | `GlobalFraction` + `GlobalCoFire`/`Fraction 0.15`/`NotionalFrac 0.10` (prod-certified dose) |
| **Sector** (ornamental — see §3) | every **~5–10 sim-days** | **≤1%**, smallness from **breadth not size** | one real sector (~6 stocks) | 0.8 | `SectorFraction` scoped via **`ISectorMap`** (code) — never modulo |

Shared: `Cap=0.25`, sign ~55/45 positive-skewed on individual (matches prod stairs-up character), Poisson arrivals only (never cadenced), no same-tick multi-stock individual events. Draw stick-fraction ~U(0.6, 1.0) per event (not a constant α) so events "give a little back" — the #1 anti-rigged tell.

---

## 3) BUILD — config-only first cut vs. the code change

### Reusable today (config only, byte-identical when off, restart to flip)
The entire arrival → decay → hard-taker-impact → anchor-stick pipeline **and** market-wide correlation are already built and prod-proven. The individual + global tiers need **zero code**.

**First-cut env lines (individual + global; sector deferred):**
```
Bots__ExogShock__Enabled=true
Bots__ExogShock__MeanIntervalMinutes=1440
Bots__ExogShock__DecayHalfLifeSec=5400          # 1.5h — see §5 (moderate until age-gate lands)
Bots__ExogShock__MinMagnitude=0.01
Bots__ExogShock__MaxMagnitude=0.12
Bots__ExogShock__MagnitudeExponent=2.5
Bots__ExogShock__Cap=0.25
Bots__ExogShock__AnchorTracksShock=true
Bots__ExogShock__ChaserFraction=0.10
Bots__ExogShock__ChaserNotionalFrac=0.06
Bots__ExogShock__ChaserMaxNotionalFrac=0.10
Bots__ExogShock__ChaserMinIntervalSec=120       # front-loaded: burst, not continuous bleed
Bots__ExogShock__ChaserSellSymFrac=0.5          # negative news needs sell-side teeth (verify)
Bots__ExogShock__GlobalFraction=0.25
Bots__ExogShock__GlobalCoFire=true
Bots__ExogShock__GlobalCoFireFraction=0.15
Bots__ExogShock__GlobalCoFireNotionalFrac=0.10
Bots__Jumps__Enabled=false                       # OFF while news runs (see §5)
```
Accepted v1 limitation: persistence *decays* over ~1.5h (reads as "sticks" for a session, not forever). True never-revert = code item #2 below.

### The CODE changes (ranked — both land on the source/state side the `IShockSource` seam was designed for)

**CODE #1 — Chaser age-gate (impulse/level split). Fund this FIRST — it is the single load-bearing item.**
The chaser currently fires for the shock's **entire life** with no age gate (`AiBotDecisionService.cs:781`). The moment you lengthen the half-life to get real "stick," this becomes **days of continuous directional taker bleed** → price overshoots the intended step and reads as rigged ("why does it keep pushing?"). Fix: gate chaser eligibility on **impulse freshness** (fire only the first N ticks of a new `shockId` — the hysteresis/id plumbing already exists). Result: two time-constants per event — **fast burst = the gap, slow anchor = the stick.** Without this, the individual tier's shape is wrong; everything else is config.

**CODE #2 — Permanent log-step on the fundamental (true "never reverts").**
On a Tier-B arrival, split `α·M` into a bounded log-step on the `FundamentalService` OU value (and its mean); leave only `(1−α)·M` in the decaying shock channel. This gives real permanence AND lets `Cap` drop back to ~0.10 (permanent steps no longer saturate the shock accumulator). Per-tier α: individual 0.8, global 0.7, sector 0.8. The 1.5h half-life buys time — do this second.

**CODE #3 — Real sector via `ISectorMap` (defer as a price mechanism; do the rewire only alongside a news-feed UI).**
Replace the two modulo sites — `IShockSource.cs:126` (`sid % _sectorCount == sector`) and `CoFireSelect` at `AiBotDecisionService.cs:2416` — with `_sectorMap.OrdinalOf(sid) == sector`, driving sector pick/count from `SectorMap`. The infra is already DI'd (consumed by BankEstimate/Conviction); it's ~2 sites + tests. **But at "tiny + rare," sector news is chart-invisible** — its only honest payoff is *narrative*. **Never ship modulo** (`stock #7` and `#57` sharing a bucket is fake economics, worse than absence). Ship real-sector only when a **news-feed/chart label** exists to make it legible.

### Explicit deferrals
- **`ScriptedShockSource`** (earnings calendar / named macro events): the seam is built for it; YAGNI for v1.
- **Optional per-tier magnitude scale** (`SectorMagnitudeScale`, `GlobalMagnitudeScale`): one multiply per stream, only if a soak shows a tier reading too big.

---

## 4) HOW THE RANDOM WALK STAYS DOMINANT

1. **Don't touch the walk.** RegimeDrift + OU sentiment rings stay exactly as baked. News is strictly additive on top.
2. **Keep it minority-share by λ (sparseness), never by damping.** The walk is already a proper unit-root process the owner has validated by eyeball as "the main driver." News is the *punctuation*, the walk the *sentence*.
3. **Variance budget — measured, not assumed.** Target: news = **15–25%** of per-stock return variance, walk keeps 75–85%. Operational gate: over a 4h **no-event window**, the chart must be indistinguishable from today's news-OFF baseline. If news exceeds the budget, **lengthen intervals or cut `ChaserNotionalFrac` — never shrink the walk.**
4. **The flash-walk is CUT (hard, unanimous).** Fast-reverting non-accumulating flashes are stationary OU noise — they'd *pin* price to a level, the opposite of wander. Making them a real walk requires a permanent component per flash, at which point they're a **reparameterization of the diffusion you already have.** The diffusion IS the flash regime, already built and prod-proven. Zero chart difference, real stationarity-bug risk. Build nothing.

---

## 5) RISKS / GUARDS + suggested first prod config

### Risks & guards
| Risk | Guard |
|---|---|
| **Continuous taker bleed** (long half-life × always-on chaser → overshoot, "rigged" look) | v1: `ChaserMinIntervalSec=120` front-loads the burst + keep half-life **moderate (1.5h)** until **CODE #1 (age-gate)** lands, then lengthen. This is the #1 risk — bigger than anchor runaway. |
| **Unbounded drift** from a true-permanent accumulator | Log-space symmetric steps (`E[m]=0`); geometric ×3/÷3 cap; `Band+Cap<AbsoluteCapMax` startup proof; saturation ≤ Cap/3. Do **not** build a naive never-decay mode. |
| **Accumulator saturation** ("news stops working") | Keep steady-state `|shock| ≤ Cap/3`; CODE #2 moves permanence off the shock channel so `Cap` can drop to ~0.10. |
| **Double-driving fat tails** | `Bots:Jumps=OFF` while news runs (JumpService doesn't touch the anchor ⇒ its moves *revert*, contradicting "sticks", and it double-counts the tail against the individual tier). |
| **Muted downside persistence** | Stage-1 `DipBuyStrength` (load-bearing for drift) will buy into negative shocks ⇒ down-news sticks less than up-news. **Observe only** — do NOT weaken DipBuy or pre-compensate with asymmetric magnitudes. |
| **Correlated deep drawdowns** from GlobalCoFire | Watch joint/portfolio drawdown; latch-vigilance; abort >−25% or on latch; don't crank past `NotionalFrac 0.10`. |

### Suggested FIRST PROD CONFIG to eyeball
Ship the **§3 first-cut env block** (individual + global, `Jumps=false`, sector deferred). This is pure config on prod-proven, byte-identical-when-off levers — a **1-restart flip**, no reseed, no migration.

**Validation gate (45-min A/B soak, events-ON vs events-OFF control arm):**
- **CK = 0** throughout (sacred).
- Per-stock 1-min σ ON ≤ **~1.3×** OFF (news = 15–25% of variance). If over: cut `ChaserNotionalFrac` first, then `MaxMagnitude`.
- **Eyeball 2–3 individual events:** a 1–3-candle gap, then the walk wandering **around the new level** — not a V, not a slow ramp. If you can circle it and say "that was news," it works.
- **Global event:** all panels breathe together for ~10–30 min, a shared mid shelf-shift.
- Portfolio drawdown within the co-fire mitigation bounds.

**Ship order:** first-cut config (soak, gate) → **CODE #1 age-gate** (unlocks safe long half-life) → **CODE #2 permanent step** (true never-revert) → sector rewire **only** when a news-feed UI justifies it. Items #1 and #2 are the only code that matters; if the council funds one, it is **#1**.