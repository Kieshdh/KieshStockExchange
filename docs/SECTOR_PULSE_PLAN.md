# Sector-Subset Co-Fire Pulses — Build Plan (2026-07-04)

## Goal
Extend the validated global co-fire (which pushes ALL 50 stocks on a market-wide pulse) so a pulse can instead target ONE SECTOR's
subset of stocks → **intra-sector-high / cross-sector-low** correlation = the signature of a real market (+ sector rotation for free).
Council-blessed (refinement council 5/5 = the top unlock). Kiesh: light touch — add the mechanism + a short correlation test; a real
`Stock.Sector` column + UI in the stock tables is his long-held idea = the FOLLOW-UP, not this pass.

## Design — extends the co-fire, same 4 layers (all default-off/inert ⇒ byte-identical)
1. **Sector assignment (lightweight, NO reseed):** deterministic map stockId → sectorId. Simplest: `sector = stockId % SectorCount`
   (or a salted hash), SectorCount ~5 ⇒ ~10 stocks/sector for 50. A tiny static helper. (FOLLOW-UP: a real `Stock.Sector` column +
   reseed + stock-table UI — Kiesh's "addition to the Stock tables".)
2. **Source (`RandomShockSource`):** the global stream currently fires ONE shared impulse to EVERY stock. Add: a fraction of global
   pulses (`SectorFraction`) are SECTOR-scoped — draw a random sector (dedicated RNG), apply the impulse to ONLY that sector's stocks,
   and record it. Expose `LastGlobalSector` (the pulsed sector, or -1 for market-wide / none) alongside the existing `LastGlobalSign`.
3. **Service (`ExogenousShockService`):** relay `GlobalCoFireSector` (this tick's pulsed sector, -1 = market-wide) next to `GlobalCoFireSign`.
4. **Chaser (`AiBotDecisionService.CoFireSelect`):** currently picks a hash-spread stock from the whole eligible watchlist. For a SECTOR
   pulse (sector ≥ 0), restrict the pick to `watchlist ∩ that sector` (skip if the bot holds no stock in the sector). ⇒ the co-fire
   cohort pushes ONLY the pulsed sector = intra-sector correlated flow.

## New config knobs (default-off/inert)
- `Bots:ExogShock:SectorCount` — default **1** (no sectors ⇒ byte-identical); e.g. 5.
- `Bots:ExogShock:SectorFraction` — default **0** (all pulses market-wide ⇒ byte-identical); e.g. 0.5 = half the global pulses sector-scoped.
- Byte-identical off: SectorCount 1 OR SectorFraction 0 ⇒ every pulse market-wide (current behavior); the sector RNG draw fires only when
  SectorFraction > 0.

## Test (short — Kiesh: "a short test for the correlation")
- **Sector-aware metric:** extend `scripts/cross_stock_diag.py` (or a new `sector_corr.py`) to report INTRA-sector vs CROSS-sector mean
  correlation, grouping the 50 stocks by `stockId % SectorCount`.
- **A/B (45m smoke is enough — intra-vs-cross is a WITHIN-arm comparison ⇒ far less window-noise-sensitive than absolute corr):**
  Arm A = co-fire market-wide (SectorFraction 0) vs Arm B = co-fire sector-scoped (SectorFraction 0.5, SectorCount 5), both on the
  new structure (GSM2.5 / RD0.2 / FX-damp / co-fire modest dose). **GATE:** Arm B INTRA-sector corr clearly > CROSS-sector corr (the
  real-market signature); overall corr not tanked; drift≈0; CK=0; perf holds (same burst size as the co-fire, already validated).

## Build note
Contained extension of the co-fire (which is already built + validated) — NO separate ultraplan/council needed (the refinement council
already blessed the direction + this design). Build directly like the co-fire; council only if a fork appears. Then soak-test, then this
+ the whole new structure ships to PROD (Kiesh: "after this we ship it to prod").

---

## EXACT EDITS (code-grounded 2026-07-04, ready to apply at harvest — build+test after the soak frees the DLL)

Byte-identical off invariant: **SectorCount default 1 (or SectorFraction 0) ⇒ `_lastGlobalSector` always −1 ⇒ CoFireSelect does no filtering ⇒
identical to current.** The dedicated `_sectorRng` is drawn ONLY inside the global-fire block AND ONLY when `SectorFraction>0 && SectorCount>1`,
so the existing `_rng`/`_globalRng` draw sequence is untouched off.

**① `Helpers/IShockSource.cs`** — interface += `int LastGlobalSector { get; }` (0..N−1 = the pulsed sector, −1 = market-wide/none).
In `RandomShockSource`:
- ctor sig += `int sectorCount = 1, double sectorFraction = 0.0`; fields `_sectorCount = Math.Max(1, sectorCount)`,
  `_sectorFraction = Math.Clamp(sectorFraction,0,1)`, `private Random _sectorRng = new(GlobalRngSeed ^ 0x2C);`, `private int _lastGlobalSector = -1;`.
- `public int LastGlobalSector => _lastGlobalSector;`
- `Reset()` += `_sectorRng = new Random(GlobalRngSeed ^ 0x2C); _lastGlobalSector = -1;`
- `Poll` top += `_lastGlobalSector = -1;` (next to `_lastGlobalSign = 0;`).
- Inside the global-fire block, REPLACE the market-wide fan-out with sector-aware fan-out (after `_lastGlobalSign = sign>0?1:-1;`):
```csharp
int sector = -1;
if (_sectorCount > 1 && _sectorFraction > 0.0 && _sectorRng.NextDouble() < _sectorFraction)
    sector = _sectorRng.Next(_sectorCount);           // §sector pulse: this pulse targets ONE sector
_lastGlobalSector = sector;
impulses ??= new List<ShockImpulse>();
foreach (var sid in _stocks.ById.Keys)
    if (sector < 0 || sid % _sectorCount == sector)   // market-wide (−1) hits all; sector pulse hits its sector only
        impulses.Add(new ShockImpulse(sid, signed));
```

**② `Helpers/ExogenousShockService.cs`** — field `private int _globalCoFireSector = -1;`; in `Tick` after `_globalCoFireSign = _source.LastGlobalSign;`
add `_globalCoFireSector = _source.LastGlobalSector;`; in `Reset` add `_globalCoFireSector = -1;`; expose
`internal int GlobalCoFireSector => _enabled ? _globalCoFireSector : -1;`.

**③ `BackgroundServices/AiTradeService.cs`** — read `var exogSectorCount = _configuration.GetValue("Bots:ExogShock:SectorCount", 1);` +
`var exogSectorFraction = _configuration.GetValue("Bots:ExogShock:SectorFraction", 0.0);`. Pass to `new RandomShockSource(...)`:
`sectorCount: exogSectorCount, sectorFraction: exogEnabled ? exogSectorFraction : 0.0`. Pass to the `AiBotDecisionService` ctor:
`globalCoFireSectorOf: () => _news.GlobalCoFireSector, sectorCount: exogSectorCount`. Extend the CONFIGCHECK line with `SectorCount`/`SectorFraction`.

**④ `Helpers/AiBotDecisionService.cs`** — ctor params += `Func<int>? globalCoFireSectorOf = null, int sectorCount = 1`; fields
`_globalCoFireSectorOf`, `_sectorCount = Math.Max(1, sectorCount)`. In the co-fire branch (after `int pulseId = _globalPulseIdOf();`) read
`int sector = _globalCoFireSectorOf?.Invoke() ?? -1;` and call `CoFireSelect(ctx, user, currency, pulseId, sector)`. Extend `CoFireSelect`:
```csharp
private int CoFireSelect(AiBotContext ctx, AIUser user, CurrencyType currency, int pulseId, int sector)
{
    var wl = EligibleWatchlist(ctx, user, currency);
    if (wl.Length == 0) return 0;
    if (sector >= 0)                                   // §sector pulse: push ONLY this sector ⇒ intra-sector flow
    {
        wl = wl.Where(s => s % _sectorCount == sector).ToArray();
        if (wl.Length == 0) return 0;                  // holds nothing in the pulsed sector ⇒ sits it out
    }
    int idx = (int)(BotMath.HashUnit01(user.AiUserId ^ GlobalCoFireSalt, pulseId) * wl.Length);
    return wl[idx < wl.Length ? idx : wl.Length - 1];
}
```

**⑤ `appsettings.json`** (`Bots:ExogShock`) — add `"SectorCount": 1, "SectorFraction": 0.0` + a one-line `_sector_comment`.

**⑥ `ExogenousShockTests.cs`** — the `StepSource` stub MUST add `public int LastGlobalSector { get; set; } = -1;` (interface now requires it =
the compile gate that makes building the TEST project the real check). Add: (a) RandomShockSource SectorFraction0 ⇒ LastGlobalSector always −1 +
impulse set unchanged (byte-identical); (b) SectorFraction>0/SectorCount5 ⇒ LastGlobalSector ∈ {−1,0..4} and sector-pulse impulses hit ONLY that
sector's stocks; (c) service relays GlobalCoFireSector; disabled ⇒ −1.

**Test metric** (DONE, machine-light): `scripts/cross_stock_diag.py --sectors N` reports INTRA vs CROSS-sector mean corr per horizon
(sector = `stockId % N`, matching the engine). Baseline on the market-wide co-fire data (kse_cf_on): intra−cross ≈ +0.02/+0.03 @5/10min (small,
noise from the 225-vs-1000 pair split) ⇒ the SECTOR arm must clearly BEAT the market-wide arm's intra−cross (the A/B cancels this offset).
