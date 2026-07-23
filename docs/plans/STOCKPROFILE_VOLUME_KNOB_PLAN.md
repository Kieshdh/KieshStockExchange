# STOCKPROFILE — SECTOR-DRIVEN + SIZE-DERIVED personality (5 knobs) — ULTRADESIGN fire prompt

**Status: ULTRADESIGN COMPLETE (feasibility → 3 architects → 5-lens council → chairman, 2026-07-23). Design-of-record below.
BUILD held for Kiesh's final say (he invoked `/ultradesign` = design first). Default-OFF + local A/B soak are pre-authorized
("design it, default off, soaktest it"); PROD needs a council green-light. This SUPERSEDES the earlier hash-only 4th-knob draft.**

Owner intent (Kiesh, 2026-07-23): *"Give sectors some of this data, then let individual stocks derive their value randomly
based on SIZE and SECTOR."* + earlier: add a 4th **VolumeMult** (volume ≠ price-move) and a 5th **NewsFreqMult** (tech = more news).

---

## 0. THE ONE ANSWER TO KIESH'S DB QUESTION
He asked: *"Is this better in the database instead of the service?"* **Council verdict = CODE, not DB (unanimous).** An
env-file-/DB-editable sector table silently **moves the tape mid-run and breaks replay determinism** — the exact property the
whole sim depends on. So sector baselines live as an **in-code `static readonly` table** (ships with the binary, deterministic,
zero migration, never touches `/Tools`). A NEW stock still auto-inherits character — from its **Sector + size**, computed at
construction — so the "new stock isn't implemented" worry is solved *without* a DB column. No per-stock DB override (rejected:
it resurrects the brittle per-id hardcode this feature exists to kill).

---

## 1. DESIGN-OF-RECORD (chairman synthesis — BUILD THIS)

### 1.1 Record shape (5 fields — 2 new)
```csharp
internal readonly record struct StockProfile(
    string  Class,
    decimal SentimentAmplitudeMult,   // 1 SWING   (existing → BotSentimentService:190)
    decimal FundamentalSigmaMult,     // 2 TREND   (existing → Fundamental:107 / BankEstimate:181 / ExogShock:74)
    decimal OverheatCapMult,          // 3 LEASH   (existing → AiBotDecisionService:2333)
    decimal VolumeMult,               // 4 NEW  notional churn  → AiBotDecisionService:2074-76
    decimal NewsFreqMult);            // 5 NEW  news arrival rate → ExogenousShockService:129
// static Neutral/Calm/Normal/Volatile/Meme all get VolumeMult=1m, NewsFreqMult=1m (byte-identical legacy path)
```

### 1.2 Sector baseline table (ONE place: `static readonly StockProfile[] _sectorBase` indexed by `(int)Sector`)
Enum order is replay-critical — index by ordinal, NEVER reorder. `Unknown(0)` = identity.

| Sector | Swing | Trend | Leash | Vol | News |
|---|---|---|---|---|---|
| Semiconductors | 1.40 | 1.50 | 1.25 | 1.20 | 1.60 |
| SoftwareIT | 1.30 | 1.40 | 1.20 | 1.15 | 1.50 |
| CommunicationInternet | 1.20 | 1.25 | 1.15 | 1.20 | 1.40 |
| ConsumerDiscretionary | 1.10 | 1.05 | 1.05 | 1.05 | 1.10 |
| ConsumerStaples | 0.70 | 0.65 | 0.88 | 0.85 | 0.65 |
| HealthCare | 1.00 | 1.08 | 1.00 | 0.95 | 1.07 |
| Financials | 0.85 | 0.85 | 0.92 | 1.10 | 0.82 |
| EnergyIndustrials | 1.08 | 1.15 | 1.05 | 1.10 | 1.00 |
| **Unknown (0)** | **1.00** | **1.00** | **1.00** | **1.00** | **1.00** |

### 1.3 Size factor — from MARKETCAP rank (NOT raw shares)
Council blind-spot (3/5 lenses): `SharesOutstanding` is a **count**, not size — a $2 stock with 5B shares would be mislabeled
"big/calm". Rank the right variable: `cap_i = SeedPrice_i × SharesOutstanding_i`.
- **SeedPrice IS reachable** at the construction seam (feasibility-confirmed): `IStockService.GetListings(sid)` yields
  per-currency listings each carrying `SeedPrice` (proven at `FundamentalService.cs:108-112`). Use the **primary** listing
  (`IsPrimary`, else the USD one, else first with `SeedPrice>0`).
- At construction: compute `cap_i` for every catalog stock, sort (ties → `StockId` for determinism), assign percentile
  `p_i ∈ [0,1]` (0=smallest, 1=biggest). Signed signal `s_i = 2·p_i − 1 ∈ [−1,+1]` (big→+1, micro→−1). **Continuous**, not
  bucketed (no bucket-edge cliffs).
- Per-knob size sensitivity (big = calmer / tighter leash / higher volume / more coverage):
```
sizeFactor_Swing = 1 − 0.28·s
sizeFactor_Trend = 1 − 0.22·s
sizeFactor_Leash = 1 − 0.15·s
sizeFactor_Vol   = 1 + 0.35·s
sizeFactor_News  = 1 + 0.28·s
```
- `SeedPrice` missing OR `SharesOutstanding=0` → `s=0` → all sizeFactors = 1 (identity, safe). **Vol RISES with size while
  Swing/Trend FALL from the same `s` → the volume≠move decoupling is STRUCTURAL, not incidental.**

### 1.4 Jitter — per-knob, deterministic, RNG-free
```
j_k = 2·BotMath.HashUnit01(stockId, k) − 1 ∈ [−1,1)     k = 0..4 (knob index)
ε_k = 0.08 for Swing/Trend/Vol/News ;  ε_Leash = 0.05  (wall-sensitive → tight)
jitterFactor_k = 1 + ε_k · j_k
```
Independent 2nd hash arg per knob → orthogonal texture; pure function of `stockId` → identical every replay. (The shared latent
factor the first-principles lens wanted already lives in `base_k × sizeFactor_k`; ±8% is only idiosyncratic noise on top.)

### 1.5 Blend + clamp (bounds strictly INSIDE today's Meme envelope 2.0/2.6/1.7 ⇒ no escape wall ever widens)
```
mult_k = clamp( base_k · sizeFactor_k · jitterFactor_k , lo_k, hi_k )
```
| Knob | lo | hi |
|---|---|---|
| Swing | 0.55 | 2.10 |
| Trend | 0.50 | 2.60 |
| Leash | 0.85 | 1.70 |
| Vol | 0.60 | 1.70 |
| News | 0.50 | 2.00 |

### 1.6 Ctor + Get() (legacy overload PRESERVED)
```csharp
public StockProfileService(bool enabled = true);                       // legacy — untouched callers stay byte-identical
public StockProfileService(bool enabled, bool sectorSizeModel,
    IReadOnlyDictionary<int,Stock> stocks, ISectorMap sectors);        // new — wired at AiTradeService.cs:381
```
Ctor precomputes `_rankById` (marketcap percentile) + a frozen `Dictionary<int,StockProfile> _profileById` ONCE.
`Get(int stockId)` keeps its signature (pure/stateless post-construction):
```
!_enabled                                    → Neutral         (byte-identical today)
!_sectorSizeModel || !sectors.HasRealSectors → legacy id-path  (byte-identical today)
_profileById.TryGetValue(id, out p)          → p (blended)
else                                         → legacy id-path  (mid-run / out-of-universe safe)
```
`Sector==Unknown` under a real map → identity base × sizeFactor × jitter (a blank-sector stock still gets **size** character —
resolves the outsider's fallback objection; it does NOT fall back to the old Avalanche bucket).

### 1.7 VolumeMult wiring — DEDICATED post-clamp line, NO SizeExpFloor
The sharpest council reversal: all 3 architects routed VolumeMult through an `expEff=max(_compSizeExp,floor)` trick that would
**activate the globally-inert composition-size path** — confounding the ON arm and letting a bigger `rawTrade` walk extra book
depth (volume→move leak). REJECTED. Instead, leave the existing `_compSizeExp>0` branch exactly as-is (stays inert), and add ONE
independent line gated only on the sub-flag, applied to notional AFTER the composition branch:
```csharp
if (_sectorSizeOn && !MM)
    rawTrade *= (decimal)profile.VolumeMult;     // notional/quantity ONLY — never TakerExp
```
The enlarged `rawTrade` flows through the **existing downstream cash/room/depth clamps unchanged** → volume rises, directional
price-impact does not. **Compile-uncertainty (must confirm):** that the downstream clamp is a *per-level book-depth* clamp, not
cash-only. If cash-only, add an explicit "cannot cross best-bid/ask level" cap on the VolumeMult-inflated quantity so churn stays
intra-level.

### 1.8 NewsFreqMult — deterministic thinning + aggregate-λ conservation
Magnitude stays at `ExogenousShockService.cs:74` (untouched). At the arrival site (`:129`, `_source.Poll(_simTick, dt)`), apply
per-stock deterministic thinning (RNG-free — Poisson/RNG draws were rejected 4/5 as a replay hazard):
```
r = profile.NewsFreqMult · _lambdaNorm
r ≤ 1 : keep candidate iff HashUnit01(_simTick, stockId)      < r
r > 1 : keep, AND add one extra iff HashUnit01(_simTick, stockId, 1) < (r − 1)
```
**λ-conservation (blind-spot #3):** at construction `_lambdaNorm = N / Σ_i NewsFreqMult_i` so universe-mean arrival rate = OFF
baseline. Tech fires *relatively* more, staples/financials fewer, total news ≈ constant → move-frequency gate protected by
construction.

---

## 2. COUNCIL TEARDOWN (frozen — do not re-litigate)
**Converged (high-confidence):** in-code table (no DB/migration/`Tools`); continuous marketcap-percentile rank `s=2p−1`;
per-knob `HashUnit01(stockId,k)` jitter (no RNG stream); multiplicative clamped blend inside escape walls; CK=0 by construction
(all 5 lenses); `SharesOutstanding`≠size → use marketcap (3/5); news must be RNG-free (4/5).
**Clashed → resolved:** data-model config-vs-code → **CODE** (config reintroduces the tape-move risk B claims to defend);
VolumeMult via SizeExpFloor → **REJECTED**, dedicated post-clamp line (reviewers overruled all 3 architects); Unknown fallback →
**identity+size** (C), not the legacy Avalanche bucket (A); jitter → **per-knob independent** (factor-model deferred).
**Blind spots caught:** (1) count≠size → marketcap rank; (2) SizeExpFloor activates the inert size path → dedicated line;
(3) tech news avg>1 inflates aggregate arrivals → `_lambdaNorm`; (4) size rank is a **construction-time snapshot** (new mid-run
stock → legacy id-path via `Get()`; by-design, bots reconstruct on restart/reseed); (5) no OFF==legacy test → add one.
**Rejected alternatives (frozen):** DB `SectorProfile` table; appsettings `Sectors`/`Overrides` config binding; per-stock DB
override column; `SizeExpFloor`/`expEff` activation; raw-shares size; Poisson/RNG news draws; size buckets/deciles.

---

## 3. CONFIG + DEFAULT-OFF (byte-identical)
- `Bots:Personality:Enabled` — existing master flag (unchanged).
- `Bots:Personality:SectorSizeModel` — **NEW sub-flag, default `false`.**
No `SizeExpFloor`, no `Sectors` table, no `Overrides` (all rejected) — sector/size/jitter/news constants live in code.
**Off-path proof (SectorSizeModel=false):** `Get()` runs the exact existing 3-knob path (`id∈[1,5]→Calm` + `Avalanche%100`
buckets, or Neutral when master off); `_sectorSizeOn=false` → the new `:2075` VolumeMult line never executes + the legacy
`_compSizeExp>0` branch stays inert; NewsFreq thinning skipped → `_source.Poll` arrivals unchanged. Every new path is behind
`_sectorSizeOn` ⇒ zero new branches taken ⇒ identical tape ⇒ CK=0.

## 4. CK + ACCEPTANCE (A/B soak, mid 45m OFF vs ON — autonomous, pre-authorized)
**CK=0 by construction both arms** (HARD gate; any CK≠0 aborts): every knob is a bounded magnitude/notional/rate scaling inside
existing anchors/walls/caps + cash/room/depth clamps; VolumeMult scales notional only (never TakerExp/direction), fenced to
intra-level depth; NewsFreqMult changes event count/timing deterministically, mints nothing.
Gates: (1) aggregate drift ≈ OFF (±1 band); (2) ret_acf ≈ OFF (±0.03 of ~−0.43); (3) move-frequency ≈ OFF (direct test of
λ-conservation); (4) cross-sectional variety WIDENS (per-stock realized-vol dispersion up); (5) size legible: size↔realized-vol
rank-corr ≈ −0.4…−0.6, size↔volume rank-corr ≈ +0.4; (6) **volume≠move**: per-stock corr(volume,|ret|) < 0.2, regress |ret| on
VolumeMult in-soak → ~flat slope (prove, don't assume); tech news-event count ≥ 1.5× staples/financials while gate #3 holds.

## 5. BUILD ORDER + SCOPE FENCE
1. `StockProfileService.cs` — record +2 fields (=1 on all static profiles); `_sectorBase[]`; marketcap-rank precompute
   (via `IStockService.GetListings` primary SeedPrice × SharesOutstanding); jitter; blend; `_lambdaNorm`; new ctor overload;
   `Get()` branch; keep legacy `(bool enabled=true)`. *Compiles, OFF byte-identical.*
2. `AiTradeService.cs:381` — pass `stocks` + injected `ISectorMap` + `cfg.GetValue("Bots:Personality:SectorSizeModel", false)`.
   *OFF byte-identical.* (Confirm `ISectorMap` is injectable into this ctor — facts say DI-available.)
3. **OFF==legacy byte-identical unit test** (new, in existing 739-suite) — `Get()` identical for all ids with sub-flag off; run suite green.
4. `AiBotDecisionService.cs:2074-76` — add the fenced `if (_sectorSizeOn && !MM) rawTrade *= profile.VolumeMult;`. **Soak this knob ALONE** first (biggest lever).
5. `ExogenousShockService.cs:129` — deterministic NewsFreq thinning + `_lambdaNorm`. Then full 45m A/B.
**Scope fence:** never `/Tools`; no DB / EF migration / schema change; no escape-wall/cap widening (clamps inside today's Meme
envelope). **Compile uncertainties:** (a) downstream `:2075` clamp granularity (per-level vs cash-only — add intra-level cap if
cash-only); (b) `HashUnit01(int,int,int)` 3-arg overload for the `r>1` draw — if only 2-arg exists, add it in `BotMath` or
compose. (SeedPrice reachability = RESOLVED via `IStockService.GetListings`.)

---

## 6. REPO FACTS APPENDIX (verbatim — transcribe, don't invent)
- `StockProfileService` (internal sealed, `Server/Services/BackgroundServices/Helpers/StockProfileService.cs`): record
  `StockProfile(string Class, decimal SentimentAmplitudeMult, decimal FundamentalSigmaMult, decimal OverheatCapMult)`; statics
  Calm(0.65,0.50,0.85)/Normal(1,1,1)/Volatile(1.45,1.70,1.30)/Meme(2.0,2.6,1.7); `Get(int stockId)`: !_enabled→Neutral;
  id∈[1,5]→Calm; else `Avalanche(id)%100` → <35 Calm/<75 Normal/<93 Volatile/else Meme; ctor `(bool enabled=true)`; `Avalanche` private.
- `Stock` (`Shared/Models/MarketData/Stock.cs`): `int StockId`, `string Sector` (GICS string, empty→Unknown), `int SharesOutstanding`.
- `Sector` enum + `SectorInfo.Parse(string?)→Sector` (`Shared/Models/MarketData/Sector.cs`): Unknown=0, Semiconductors, SoftwareIT,
  CommunicationInternet, ConsumerDiscretionary, ConsumerStaples, HealthCare, Financials, EnergyIndustrials. Order replay-critical.
- `ISectorMap`/`SectorMap` (`Server/Services/DataServices/SectorMap.cs`): `SectorOf(id)→Sector`, `HasRealSectors`, built lazily
  from `IStockService`; all-Unknown → HasRealSectors=false → modulo fallback (byte-identical). DI-available.
- `IStockService`: `.ById` (`IReadOnlyDictionary<int,Stock>`), `.All`, `.GetListings(int)→listings` each with `decimal SeedPrice`,
  `bool IsPrimary`, `string Currency` (proven at `FundamentalService.cs:105-113`).
- Construction seam `AiTradeService.cs:381`: `_profiles = new StockProfileService(enabled: cfg.GetValue("Bots:Personality:Enabled", true));`
  — `stocks` in scope + already passed to _news/_funds/_sentiment/_bank/_decisions.
- Consumers: BotSentiment:190 (SwingAmp), Fundamental:107 + BankEstimate:181 (TrendSigma), ExogShock:74 (news magnitude),
  AiBotDecision:2333 (leash cap). NEW: AiBotDecision:2074-76 (VolumeMult on `rawTrade`, `CompositionSizeMult` at :2999),
  ExogShock:129 (`_source.Poll(_simTick, dt)` arrivals = NewsFreq hook).
- `BotMath.HashUnit01(int)` + `HashUnit01(int,int)` — RNG-free deterministic [0,1) (3-arg may need adding).

**Fire contract:** branch `perf/admin-table-time-indexes`; default-OFF `Bots:Personality:SectorSizeModel=false` byte-identical;
`file:line` anchors above; CK=0 invariant + §4 acceptance; scope fence = the 4 files in §5, never `/Tools`; no-SDK executors list
compile uncertainties (§5) with `file:line` rather than guess-fixing.
