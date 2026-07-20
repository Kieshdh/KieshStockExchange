# Chart Overhaul — Build Strategy & Execution Plan

**Decision: HYBRID SPLIT — two ultraplans (blind-patchable core) + five local eyeball-gated phases.** All five council members converged here; the only disagreement (Alert defer vs. ship-minimal) breaks 4–1 for ship-minimal, resolved below.

The split axis is the **verification oracle**, not the phase plan: work a machine can attest (compile, unit test, round-trip, geometry math) goes in an ultraplan; work whose only acceptance test is Kiesh's eyeball on a running build stays local. The exe-lock (can't link while the client runs) makes eyeball work serial-with-owner regardless, so forcing feel-critical UI into a blind mega-patch buys nothing and risks burning the one confident shot on flyout hover-timers.

---

## 1) THE SPLIT

| Component | Track | Seam | One-line reason |
|---|---|---|---|
| Data-model additions (enums, trailing fields, preset table) | **UP-CORE** | `ChartTypes.cs` types | Append-only, back-compat by construction, headless |
| `ColorJsonConverter` null→blue bug (`ChartTypes.cs:142`) | **UP-CORE** | serializer | Deterministic, testable headless |
| `EditingKind` `.First`→`FirstOrDefault` | **UP-CORE** | VM property | Pure correctness fix |
| `EditingKind` + 8 `Show*` bools + `MutateSelectedDrawing` + setters | **UP-CORE** | VM logic members | Follows exact `CanEditHead` precedent; logic-only |
| Undo/lock state machine (M3 contract) | **UP-CORE** | pure `UndoStack` class | State-machine rules, unit-testable |
| Magnet snap math (M7 + C5 candidate-set) | **UP-CORE** | pure `MagnetSnapper.Snap()` | O(1) geometry, headless-testable, no wiring |
| Brush/Highlighter spline eval + decimation | **UP-CORE** | pure `SplineSmoother` | Catmull-Rom geometry |
| Snapshot offscreen render (M6) | **UP-CORE** | `ChartSnapshotRenderer`→PNG | `SkiaBitmapExportContext`, deterministic, ~30 lines |
| Price→pixel scale transform (Regular impl) | **UP-CORE** | `IScaleTransform` seam | Sleeper: without the seam, P4 log/percent rewires every renderer under eyeball pressure |
| Geometry renderers + hit-tests (ExtendedLine, Rect/Ellipse fill paths) | **UP-CORE** | drawable | Deterministic; renderers get cheap eyeball touch-up in LP2 |
| Remove `DrawCloseGlyph` / `DrawingHitPart.Close` | **UP-CORE** | hit-test | Deletion-path lockdown, mechanical |
| `UserDrawings` table + EF migration + CRUD + flush loop | **UP-STORE** | mirrors `CandleService.FlushLoopAsync` | Different process/deploy/soak regime; standard backend patterns |
| Client `IDrawingStore` (server + local cache, debounce/coalesce, migrate-up) | **UP-STORE** | replaces `PersistDrawings`/`LoadDrawingsForSelected` | Swaps invisibly behind existing call sites; zero UI diff |
| Rail + hover flyout, active-highlight, crosshair, hint strip, sticky arming, Snapshot button | **LOCAL LP1** | consumes UP-CORE | Feel-critical, no native control, overlay Grid z-order over GraphicsView |
| Panel section composition, sliders, Brush/Smoothing UI, Magnet feel | **LOCAL LP2** | consumes UP-CORE | Visual composition + "haunted magnet" feel |
| Undo/lock UI wiring, Delete-key, double-click-open | **LOCAL LP3** | wires `UndoStack` | Thin — logic already certified |
| Toolbar dropdowns, price-scale menu, scale-transform routing audit | **LOCAL LP4** | adds log/percent impls to `IScaleTransform` | Must eyeball on real candles (log bends lines) |
| Position box, Arrow, minimal Alert, prefill-order | **LOCAL LP5** | consumes UP-CORE fields | Heavy interactive drag↔panel sync |

**Verdict: hybrid = 2 ultraplans + local UI.** Not one (half the acceptance criteria are "feels right live"; a failed mega-patch stalls the deterministic half too). Not several core ultraplans (all items converge on `ChartTypes.cs` + the chart VM — splitting is merge churn with zero verification gain). Not all-local (the core is ~2,500 lines of dense contract-heavy headless work — exactly what one confident patch beats ten edit-cycles at).

---

## 2) ULTRAPLAN SCOPES

### UP-CORE — `ultraplan-chart-drawing-core.md` (author + fire FIRST; MUST land on branch before any local phase)
**Hard rule: touches models, converters, pure helpers, logic-only VM members. Never touches XAML, pointer/gesture handlers, timers, cursor, or layout. Must compile app-closed, pass Shared unit tests, and render identically to today.** That identical-render guarantee is the handoff contract.

**★ DECISION RESOLVED (Kiesh, 2026-07-16) — OPTION A: keep the model CLIENT-side + opaque server blob.** repo-facts found `KieshStockExchange.Shared` does NOT reference `Microsoft.Maui.Graphics`, and `DrawStyle`/`DrawingObject`/`ColorJsonConverter` all use MAUI `Color`. Kiesh chose **(A)**: the drawing model stays entirely CLIENT-side (reorganized into grouped folders on the client), and the **server's `UserDrawings` row stores the drawings-list JSON as an OPAQUE string blob it never deserializes**. No new dependency; no MAUI-graphics on the headless server; the drawing model is inherently a client rendering concern. The `"v":1` JSON schema is the wire contract; server = dumb per-(user,stock,currency) sync store. (Rejected: (B) move model to Shared + add Maui.Graphics — puts a graphics dep on the headless server, was the owner's earlier "in Shared" guess; (C) enums-only to Shared — splits one logical model across two projects.)

0. **Code organization (owner):** the drawing types live in ONE flat client file today (`KieshStockExchange/Services/MarketDataServices/Helpers/ChartTypes.cs`). Under **Option A** they ALL stay in the CLIENT project — just split from the one flat file into grouped client folders under `KieshStockExchange/Models/ChartDrawing/`:
   - **`KieshStockExchange/Models/ChartDrawing/Tools/`** — `DrawTool` enum + the Kind→preset table.
   - **`KieshStockExchange/Models/ChartDrawing/Style/`** — `DrawStyle`, `DashKind`, `LineEnding`, `ArrowHeadStyle`, `SizeKind`, `ColorJsonConverter`.
   - **`KieshStockExchange/Models/ChartDrawing/Objects/`** — `DrawingObject`, `DrawPoint`, `AlertCondition`.
   - **Also client-side** (own folders under the chart helpers, transient/non-persisted): render state (`MeasureState`, `CrosshairState`, `DrawingHitPart`) + the pure helpers (`MagnetSnapper`, `SplineSmoother`, `UndoStack`, `ChartSnapshotRenderer`, `IScaleTransform`) + the drawable.
   Update namespaces/usings across the client accordingly (mechanical, compile-verifiable). This is a good FIRST move in UP-CORE so the new fields land in their final home. **Nothing moves to `KieshStockExchange.Shared`** — the server never sees these types; UP-STORE persists the serialized `"v":1` JSON string.
   - **Namespace convention (verified):** `RootNamespace=KieshStockExchange` (both client and Shared). Client types today live in `KieshStockExchange.Services.MarketDataServices.Helpers`; the new home is folder-derived **`KieshStockExchange.Models.ChartDrawing.Tools` / `.Style` / `.Objects`** (physical path `KieshStockExchange/Models/ChartDrawing/`). Moving them changes their namespace ⇒ update all `using`s (blast radius = the repo-facts cross-ref list: `CandleChartDrawable.cs`, `StylePreviewDrawable.cs`, `ChartViewModel.cs`, `PenTiles.cs`, `ChartView.xaml.cs`, and `ChartView.xaml`'s `clr-namespace` if it references any moved type as a XAML element).

**LOW-PRIORITY (owner: no haste, do LAST / when idle — NOT part of UP-CORE):** tidy the existing flat `KieshStockExchange.Shared/Models/` (~24 files) into grouped subfolders (Trading/, MarketData/, Users/, …). Files carry EXPLICIT namespace declarations, so moving them into subfolders is namespace-neutral (zero using churn) — a safe cosmetic reorg to shorten the folder list. Separate from the chart-drawing split above.

1. **Model (M2/M4/M5):** tool=Kind+preset static table (load-bearing); new Kind members **only** `RotatedRect`, `Triangle`, `Arc` **+ reserve `FibRetracement`** (member only, no impl); enums `SizeKind{Small,Medium,Large}`, `AlertCondition{CrossAny,CrossUp,CrossDown}`. Trailing-default fields: `DrawStyle += Fill(null)/FillOpacity(0.15f)/Size(Medium)`; `DrawingObject += Text(null)/P3(0m)/Qty(0m)/Locked(false)/Smoothing/Direction`. M4: explicit `Direction` storage (don't infer from target>entry — flips box mid-type), Risk% cut, PnL live-only, document P1=Entry/P2=Target/P3=Stop.
2. **Correctness:** `ColorJsonConverter` write null (not coalesce-to-blue) at `ChartTypes.cs:142` — `Colors.Transparent` sentinel or teach converter; `EditingKind` `.FirstOrDefault`; `"v":1` versioned drawings-JSON payload (this **is** UP-STORE's wire contract).
3. **Headless primitives as named, unit-tested seams:** `MagnetSnapper.Snap(px, mode, axisMask) → SnapResult` (8px weak / cursor-candle-slot strong / candidate-set = OHLC + order-lines + MA points, body-move + Brush/Highlighter/Measure/Magnifier exempt); `SplineSmoother` (Catmull-Rom + decimation); `ChartSnapshotRenderer` (offscreen `SkiaBitmapExportContext` → `drawable.Draw` → PNG bytes); `UndoStack` (add/move/delete-only, one entry per gesture, cap 50, purge-on-lock, lock not undoable, clear-on-stock-switch); `IScaleTransform` (Regular impl only — every drawing render routes price→pixel through it).
4. **VM plumbing seam:** `EditingKind`, 8 `Show*` bools (split `ShowFillColor` + `ShowOpacity` per M5), `MutateSelectedDrawing`, all setters, `RefreshPenTiles`/`PenPanelHeader` extension. Remove `DrawCloseGlyph`/`DrawingHitPart.Close`.
5. **Renderers/hit-tests** for ExtendedLine + Rect/Ellipse fill-opacity paths. **Position-box geometry EXCLUDED** — too interaction-entangled, goes to LP5.

**Fire-Contract footer must list verbatim seam signatures** (`IDrawingStore`, `MagnetSnapper`, `SplineSmoother`, `ChartSnapshotRenderer`, `UndoStack`, `IScaleTransform`, `EditingKind` + `Show*`) so the local agent and UP-STORE bind against them, not reinvent them.

### UP-STORE — `ultraplan-drawing-persistence.md` (author in parallel; fire once UP-CORE's fields/`"v":1` exist)
Server: `UserDrawings` table (userId, stockId, currency, JSON `"v":1` payload, updatedAt) + EF migration + CRUD endpoints; in-memory buffer + relaxed background flush loop batch-upserting in one tx (mirror `CandleService.FlushLoopAsync`, flush-on-shutdown). Client: `IDrawingStore` replacing `PersistDrawings`/`LoadDrawingsForSelected` — server-backed + local cache, debounce/coalesce dirty-set, flush on timer/stock-switch/background/logout, last-write-wins, one-time `Preferences` migrate-up. Validation: server unit tests + API smoke (kse-order-smoke pattern) + a mid soak. **Separate ultraplan because it's a different process, deploy surface, and soak regime — jamming it into UP-CORE creates one patch with two unrelated failure domains.** Its collision surface with local work is ~one DI registration.

---

## 3) LOCAL PHASE ORDER (one owner eyeball session per phase; build app-closed, batch feel-fixes — hard cap 2 iteration builds/phase, residuals to a punch list)

- **LP1 — Rail foundation.** Icon rail + hover-expand flyout (overlay Grid above GraphicsView + hover-close timer; group button shows last-used tool); relocate line tools + Measure; Magnifier (zoom-box, **disables auto-fit**); eye toggle; **Snapshot full UI** (button + clipboard + auto-named file + toast, on UP-CORE's renderer — M6's P5→P1 move); **M8 Escape ladder** (cancel-placement → deselect+close → disarm) + accelerator focus-gating (Delete/Ctrl+Z/Y dead while an Entry is focused, registered page-level); crosshair via cached-reflection `ProtectedCursor` + reset-on-exit (M10); active-tool highlight; axis-label density; **status hint strip (C4** — "single biggest feels-pro item"**)**; **sticky arming (C8)**; per-tool shortcuts (**C10 only if <1hr, else silently drop**).
- **LP2 — Shapes/text/brush + Magnet feel.** Panel sections show/hide (HIDE don't grey); fill-color grid + opacity slider (`ValueChanged` preview / `DragCompleted` commit / debounce, M10) + text Entry; Brush(=Freehand)/Highlighter(=low-alpha, no new field) + Smoothing slider on UP-CORE's spline; wire UP-CORE snap math + snap ring + **Ctrl-momentary-invert (C3)** + **Shift-constrain (C1)**; Delete-all (confirm dialog, skips locked); eye-off + arm auto-re-enables eye. **P2b (default DEFER post-ship): Triangle/Arc/RotatedRect renderers.**
- **LP3 — Undo/redo/delete + Lock UX.** Buttons, lock icon, Delete-key (selected-first-else-last), deletion-path lockdown verify, **double-click-to-select+open (C7)** — all thin wiring on UP-CORE's certified `UndoStack`. *(Land UP-STORE before or during LP3: clear-on-stock-switch interacts with load-on-switch.)*
- **LP4 — Toolbar + scales.** Chart-type/volume/scale compact dropdowns; price-scale menu (Regular/Percent/Indexed-100/Log + Auto-fit/Lock-ratio/Invert/Move-left) — **add Log/Percent impls to `IScaleTransform`**; **route every drawing's price→pixel through the active transform** (eyeball-critical on real candles: log bends straight lines, mis-maps Magnifier Y-box); MA panel → MA-only + relocate candle-colour to bottom-right settings gear; Y-padding +/− control (persisted, clamp ~0–15%, lower default). **Must precede LP5** (Position labels use price mapping).
- **LP5 — Trading tools.** Position Long/Short/"Position (by price)" box (draggable handles ↔ panel-entry sync, shaded zones, live PnL/%/amount labels, direction from stored field); Arrow marker; **Alert minimal (M9 option 1):** fires only while its stock's chart loaded, one-shot, dimmed "triggered @ time" + re-arm-by-drag, persisted, top-row button sets `DrawTool=Alert` — **+ real toast/bell/badge in `TopNavBarViewModel` (C2)**; **Position→Prefill `PlaceOrderViewModel` (C6**, no server change**)**; Convert HLine/PriceLabel→Alert (C9, first cut if LP5 slips).

---

## 4) OPEN SCOPE CALLS (accept wholesale)

- **Alert → SHIP MINIMAL in LP5** (not deferred). 4–1 council majority. Rationale: C2 (toast/bell) + C6 (prefill) make LP5 the phase where drawings finally touch the trading loop — the payoff. Model fields (`AlertCondition`) ship free in UP-CORE; runtime is small once fields exist. Do **not** ship a decorative line. *If LP5 slips, the cut line is Alert+C2+C9 together — keep Long/Short/Prefill.*
- **Exotic shapes (Triangle/Arc/RotatedRect) → P2b, effectively post-ship.** Enum members reserved in UP-CORE (free); zero UI built. Near-zero value in a trading sim, hardest 3-pt UX.
- **Manual → RENAME "Position (by price)".** Same panel, typed values, preset not Kind. Not hollow once renamed.
- **Comment variant → CUT** (fold into plain Text; no behavior was ever specified).
- **Risk% → CUT from v1** (no equity hookup; M4).
- **Right-click context menu → CUT** (collides with right-click-deselect).
- **Fib → RESERVE now, build FIRST post-ship.** Enum member + rail slot + preset row in UP-CORE (free). #1 TradingView-parity hole per C14; out-ranks Arc/Triangle/RotatedRect combined.
- **Creative items IN v1:** C1 Shift-constrain (LP2), C2 Alert toast/bell (LP5), C3 Ctrl-magnet + snap ring (LP2), C4 hint strip (LP1), C5 magnet candidate-set (shape in UP-CORE, feel in LP2), C6 Position→prefill (LP5), C7 double-click-open (LP3), C8 sticky arming (LP1), C10 per-tool shortcuts (LP1, only if cheap).
- **DEFERRED post-ship queue (in order):** Fib → exotic shapes → C9 convert-to-alert → C11 Ctrl-drag duplicate → C12 measure-copy-stats. (C13 server-sync IS UP-STORE, shipping in v1.)

**Net v1:** rail (10 groups) + lines/Rect/Ellipse/brush/highlighter/text/price-label + magnet + measure/magnifier + undo/lock/delete-path lockdown + snapshot + toolbar/scale menu + Long/Short/By-price positions + prefill + minimal alerts + server persistence.

---

## 5) IMMEDIATE NEXT 3 STEPS

1. **Author `ultraplan-chart-drawing-core.md`** via the standard workflow (feasibility-probe → 3 architects → council teardown → fire prompt with Repo-Facts appendix + the verbatim seam-signature Fire-Contract). This blocks everything.
2. **Fire UP-CORE, validate headless** (compile client csproj app-closed + Shared unit tests on `MagnetSnapper`/`UndoStack`/`SplineSmoother`/`ColorJsonConverter` round-trip), then **merge to `feature/bot-market-realism-v2`** before touching any XAML — the merge conflict on `ChartViewModel` costs more than any parallelism gained.
3. **In parallel, author `ultraplan-drawing-persistence.md`** (its wire contract = UP-CORE's `"v":1` payload) and fire it as soon as UP-CORE's model fields exist; then start **LP1** the moment UP-CORE merges. UP-STORE's `IDrawingStore` swap slots into any phase ≥LP1, target before LP3.

**Dependency spine:** UP-CORE blocks everything → local LP1→LP5 sequential (one eyeball each) → LP4 scale-transform must precede LP5 → UP-STORE blocks nothing UI-side (seam-compatible) but land it by LP3.