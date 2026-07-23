# MVVM FOLDER RESTRUCTURE PLAN — even + coherent file distribution

**★ STATUS (2026-07-24): DESIGNED → AUTHORIZED to build+test+council+PROD (Kiesh full authorization; F4 is NOT F3 so prod push OK
after council).** Pure behavior-neutral refactor; gate = build + full test suite (749) green + app launches + byte-identical candle
diff on a fixed seed (NO market soak). Build order = one folder per commit, launch-verify each. Slot AFTER the realism features
(F2, F1+F5 combined) per the runbook build order — F4 is disk-heavy + low-realism-value, so it clears the deck last-ish, not first.
Produced by an independent MVVM-structure specialist + a 5-advisor council review. Owner ask: files are unevenly distributed (some
folders near-empty, some 10+); make it more even AND idiomatic. Nothing under `/Tools` is touched.

---

## ★ COUNCIL RECOMMENDATION (decisive — read first)
**Coherence is the real goal; even file counts are a *symptom detector*, not a target.** Do only the moves that
add semantic meaning; a big folder that is one cohesive concept is correct, not a problem.
- **DO (in this order):**
  1. **Client VM extractions first (Moves 2 → 3)** — separate true ViewModels from row/DTO/interface noise. Low-risk, and serves as the **rehearsal** to prove the namespace-fix workflow before the big one.
  2. **★ FLAGSHIP: split the 46-file `Server/Services/BackgroundServices/Helpers`** into `{Decisions, Economy, Lifecycle, Telemetry, Infra}` — the real prize; it's an *unnamed subsystem*, not just an uneven folder. Do it in 5 sub-commits (one target folder each).
- **DEFER:** Move 4 (client `Helpers` split) + Move 5 (client `DataServices/Drawings`) — low value; do only if the rehearsal proves the workflow cheap.
- **SKIP:** Move 6 (group `Controllers`) — flat is idiomatic ASP.NET (attribute routing is folder-independent); pure churn, zero navigational gain.
- **LEAVE ALONE** (the specialist's list is correct): `DataServices/Persistence`(15), `Settlement`(11), `Themes`(16), `Drawing` renderers(15), all `Views/*` `.xaml`+`.xaml.cs` PAIR folders, and every partial-class cluster.
- **Guardrail:** proceed ONLY if the flagship split is the real motivation. If it's just tidiness, the namespace/XAML churn isn't worth it.

## ★ EXECUTION RULES (non-negotiable per council)
- **One folder per commit; `dotnet build` between each.** Never one big PR.
- **XAML breakage is runtime-invisible.** File-scoped namespaces track folder path; a moved ViewModel's namespace change can break `clr-namespace:` / `x:Class` / the `GlobalXmlns.cs` alias at RUNTIME, which `dotnet build` will NOT catch. So after any VM move: **launch the app and open the affected Trade/Admin views** (Kiesh's disk-frugal gating still applies — scope the client build; see `feedback_disk_frugal_gating`).
- Use `git mv` (history follows). Let the compiler enumerate broken `using`s from the error list. SDK-style csproj globs `**/*.cs` + auto-globs XAML → no `<Compile Include>` to chase (verify first). Partial-class clusters MUST land together in one folder + namespace.

---

## 1. Current distribution (code folders; docs/scripts/tests/Migrations excluded)
**⚠️ overloaded (10+) · 🕳 near-empty (0–2) · ✅ healthy/cohesive**

### Server (`KieshStockExchange.Server/`)
| Folder | Files | |
|---|---|---|
| `Services/BackgroundServices/Helpers` | **46** | ⚠️ **god-folder = the whole bot/market-sim subsystem** |
| `Controllers` | **29** | ⚠️ (flat = idiomatic; SKIP) |
| `Services/DataServices/Persistence` | 15 | ✅ row DTOs, cohesive |
| `Services/DataServices` | 14 | ⚠️ partials + stores |
| `Services/MarketEngineServices` | 12 | ⚠️ |
| `Services/MarketEngineServices/Settlement` | 11 | ✅ cohesive |
| `Services/BackgroundServices` (parent) | **1** | 🕳 parent of the 46-file child |
| `Hubs`, `HealthChecks`, `Services/*/Interfaces` | 1 each | 🕳 |

### Client (`KieshStockExchange/`)
| Folder | Files | |
|---|---|---|
| `ViewModels/TradeViewModels` | **21** | ⚠️ VMs + row DTOs + interfaces mixed |
| `ViewModels/AdminViewModels/Tables` | **21** | ⚠️ VMs + TableObjects + rows mixed |
| `Views/AdminPageViews/Tables` | 20 (10 pairs) | ✅ view pairs |
| `Views/TradePageViews` | 16 (8 pairs) | ✅ |
| `Resources/Styles/Themes` | 16 | ✅ theme dictionaries |
| `Services/MarketDataServices/Helpers/Drawing` | 15 | ✅ renderers |
| `Services/DataServices` | 11 | ⚠️ Api partials + drawing stores |
| `Helpers` | 10 | ⚠️ misc catch-all |
| `Models/ChartDrawing` | 0 direct | 🕳 hollow parent (only Objects/ + Style/) |
| `Views/MarketPageViews`, `ViewModels/UserViewModels`, `Services/BackgroundServices` | 2 each | 🕳 |
| `Services/SignalR`, `*/Interfaces`, `Behaviors` | 1 each | 🕳 |

### Shared (`KieshStockExchange.Shared/`) — well balanced (max 7). **No rework recommended.**

## 2. Diagnosis
1. **The "Helpers" anti-pattern (the one that matters).** `BackgroundServices/Helpers` (46) is not helpers — it's the bot/market-sim subsystem (decision strategies, economy/signals, population lifecycle, telemetry probes) dumped one level too deep under a generic name, starving its 1-file parent. The client `Helpers` (10) is the milder same disease.
2. **"Table" folders mix three kinds:** `TradeViewModels`(21) and `AdminViewModels/Tables`(21) interleave true ViewModels + row/DTO records (`PositionRow`, `FundTableObject`, `UserDetailsFundRow`…) + small interfaces (`ISideRow`, `ILazyTab`). The DTOs are ~half the files and are data shapes, not VMs.
3. `Controllers` (29) is large but idiomatic ASP.NET — cosmetic, not architectural.
4. Legitimately-large cohesive folders (Persistence, Settlement, Themes, Drawing, `.Chart`/partial clusters) must stay whole — splitting LOWERS coherence.
5. `Views/*` look big but are `.xaml`+`.xaml.cs` PAIRS — real unit count is half; healthy.

## 3. Proposed moves (council-prioritized)

### ★ FLAGSHIP (DO) — split `Server/Services/BackgroundServices/Helpers` 46 → 5
```
Server/Services/BackgroundServices/
├── AiTradeService.cs                (unchanged orchestrator)
├── Decisions/   AiBotDecisionService · AiBotContext · Conviction*(.cs/.Math/.Run/.TradeBook) ·
│               MarketMakerDecisionService · MarketMakerMath · ArbitrageDecisionService ·
│               RotatorDecisionService · DecisionFillRecorder
├── Economy/     MarketMoodService · MarketPulse · BotRegimeService · BotSentimentService ·
│               BotPriceMemoryService · StockProfileService · FundamentalService ·
│               ExogenousShockService/IShockSource · JumpService/IJumpSource
├── Lifecycle/   AiBotStateService(.cs/.Pruning) · BotActivityService · BotScalerService ·
│               BotFailureTracker/FailureRecord · BotCashInjector · BankEstimateService
├── Telemetry/   *Probe (Activity/ArmedStopCap/BotDecision/Chaser/ImpactHold/Jumps/MarketMaker/
│               RefillThrottle) · ReservationAuditor · BotEconomyTelemetry · BotStatsLogger ·
│               BotTelemetryCache · EngineCommitMetrics
└── Infra/       BotMath · RingBufferStore · RefillThrottleGate
```
46-in-one → ~8-10 per folder; parent 1 → healthy. Namespaces currently `KieshStockExchange.Services.BackgroundServices.Helpers;` (server does NOT use a `.Server.` segment — verify per file); each moved file's final segment changes `…Helpers` → `…Decisions`/`.Economy`/etc. **Keep partial clusters (Conviction*, AiBotStateService.*) intact.** Do in 5 sub-commits. Smoke-test the app after (heavily DI-wired in `Program.cs`).

### DO — Move 2: client `ViewModels/TradeViewModels` 21 → VMs + `Rows/` + `Contracts/`
Keep VMs (`PlaceOrder/ModifyOrder/OpenOrders/OrderBook/OrderHistory/TransactionHistory/UserPositions` VMs, `TradeTableViewModelBase`, `StockAwareViewModel`) at parent; `Rows/` ← `BracketLegRow·ClosedOrderRow·LevelRow·OpenOrderRow·PositionRow·TransactionRow·TradingPair·BucketSizeOption`; `Contracts/` ← `ISideRow·IStockNav·LevelSide`.

### DO — Move 3: client `ViewModels/AdminViewModels/Tables` 21 → VMs + `TableObjects/` + `Rows/`
`TableObjects/` ← the 6 `*TableObject.cs`; `Rows/` ← `UserDetails{Fund,Order,Position,Transaction}Row.cs`; VMs + `BaseAdminTableViewModel` + `ILazyTab` stay.

### DEFER — Move 4 (client `Helpers` 10 → `Converters/ Math/ Drawables/ Lifecycle/`), Move 5 (client `Services/DataServices` → extract `Drawings/` stores from the `ApiDataBaseService.*` partials).
### SKIP — Move 6 (group `Controllers` by domain). ### Cosmetic/last — fill hollow `Models/ChartDrawing`.

## 4. Risks / cost (ordered by blast radius)
1. **Namespace churn (biggest).** File-scoped namespaces track folder → moved file's `namespace` + every consumer `using` change; compiler enumerates. Do the 46-file split in 5 bounded-error sub-commits.
2. **Partial clusters must stay together** (Conviction*, AiBotStateService.*, ApiDataBaseService.*, OrderEntryService.*, PgDBService.*, ChartViewModel.*, CandleService.*).
3. **XAML `clr-namespace:` / `x:Class` / `GlobalXmlns.cs` alias** break at RUNTIME on VM namespace change — grep `.xaml` for old namespace segments after each VM move; **launch-and-click verify**.
4. **DI registration** in `MauiProgram.cs` / `Program.cs` — `using`-only (types unchanged), build catches; smoke-test after flagship.
5. **`.csproj` low** — SDK-glob picks up moves without edits (verify no `<Compile Include>`).
6. `git mv` for history; moves + namespace edits in the same commit per folder.

**Recommendation restated:** flagship split (impact) + client VM extractions (coherence), incrementally, launch-verified per commit; defer 4–5; skip 6; leave the cohesive-large folders alone. Owner has final say before any move.
