# Manual test checklist — P3, P4 & PlaceOrderView revamp

Tick items as you go. Covers: PlaceOrderView UI revamp, P3 (stop manageability), P4 (brackets incl.
TP-only), long→short flip, fill markers, last-watched stock. Companion to
`TEST_FINDINGS_AND_FIXES.md` (what was implemented) and `ADVANCED_ORDERS_PLAN.md` (roadmap).

For any failure note: **step id + Reserved before/after + the `MarketEngine` log line** — that triple
usually localizes it to the exact settler/coordinator branch.

---

## 0. Setup & invariants (keep visible the whole run)

- [ ] Account with known **cash** (note it) and a known **long** (e.g. 100 shares). Stay on one stock.
- [ ] Funds card (Available/Reserved/Total), Position (Qty/Reserved/Available), and server log filtered
      to `MarketEngine` + `ConservationProbe` + `ReservationAuditor` all visible.
- [ ] Golden rule understood: after anything settles/cancels, Reserved must return to an explainable
      value; ConservationProbe net-delta stays 0 per batch.

---

## A. PlaceOrderView — UI revamp

- [ ] A1 — Segment shows **Market | Limit | Trigger**, all readable; switching tabs never freezes or budges.
- [ ] A2 — Market: no price field. Limit: caption + entry; marketable-limit hint when it crosses market.
- [ ] A3 — Trigger: trigger price + morphing checkbox **"Limit order"** (stop-market) ↔ **"Limit price"**
      (stop-limit reveals limit entry); "fills at this price or better" only in stop-limit.
- [ ] A4 — Stop-loss label morphs **"Stop-loss"** ↔ **"Stop-loss price"**; entry only when checked.
- [ ] A5 — TP stepper `◀ [n] ▶` 0→3; arrows green when actionable, grey at bounds; exactly n rows
      (TP1/TP2/TP3), narrow qty; tight spacing to TP1.
- [ ] A6 — Trailing stop checkbox mutually exclusive with Stop-loss (no runtime behavior yet).
- [ ] A7 — Currency auto-format: `$10.20` at rest, `10.2` while focused, reformat on blur; submit still
      parses; check a non-USD currency.
- [ ] A8 — Quantity slider: dots at 0/25/50/75/100%, snaps to dot when near else nearest whole share,
      updates qty field; no freeze when max changes.
- [ ] A9 — Slippage guard (plain Market only); **None** hides the slider; sell stop-market shows the cap,
      buy-stop doesn't.
- [ ] A10 — Assets at top for every tab/side; zero currency = `$0.00` (funds, order value); shares `0/0 SYM`.
- [ ] A11 — Symbol bar full-width; chart/orderbook/panel in 70% band, tables 30%; panel scrolls
      internally; order value + Buy/Sell button **pinned** & always fully visible; scrollbar in its
      gutter. **Resize bigger AND smaller** → no clip, no budge.

---

## B. P3 — Stop manageability

- [ ] B1 — Sell trigger (stop) below market → stop line on chart (distinct styling); Reserved shares rise.
- [ ] B2 — Sell stop-limit → renders correctly (not a plain limit).
- [ ] B3 — Modify armed stop trigger price → line moves; Reserved **unchanged**; `MarketEngine` logs it.
- [ ] B4 — Modify qty up/down → Reserved tracks delta; Available = Qty − Reserved.
- [ ] B5 — Price crosses trigger → fires & fills; Reserved → baseline; line disappears.
- [ ] B6 — Cancel armed stop → line gone, Reserved released, no orphan in audit.

---

## C. P4 — Brackets (buy/long)

- [ ] C1 — SL-only (entry + SL, 0 TPs): after fill, SL reserves the **full filled qty**; Reserved = filled qty.
- [ ] C2 — SL + TPs: children Attached (reserve 0) until entry fills; after fill SL + covered TPs arm off
      **actual filled qty**; **total Reserved = filled qty** (NOT qty×(1+#TPs)).
- [ ] C3 — One TP fills → position drops, **SL shrinks** by same qty; other TPs rest; Reserved = remaining.
- [ ] C4 — All TPs fill → position flat, SL auto-cancels, Reserved → baseline, no orphan SL.
- [ ] C5 — SL fires → sells remaining qty, **all TPs cancel**, Reserved → baseline, no orphan TP lines.
- [ ] C6 — Cancel group / SL / unfilled parent → all members gone, Reserved released.
- [ ] C7 — Reject paths: SL above entry / TP below entry / Σ TP qty > bracket qty → clean reject, no
      partial reservation.

### C8. TP-only brackets (new engine path — exercise hard)

- [ ] C8a — Buy entry + 2 TPs, **no SL**: after fill, **Reserved = Σ TP qty** (each TP reserves its own
      shares, NOT the whole position); TPs rest on book.
- [ ] C8b — Fill one TP → position drops, that TP's reservation releases; sibling still rests; no SL.
- [ ] C8c — Fill last covered TP → bracket retires; Reserved → baseline.
- [ ] C8d — Partial TP-only (buy 100, TPs=30): only 30 reserved; other 70 freely sellable.
- [ ] C8e — Cancel a single armed TP → its shares release, sibling stays.
- [ ] C8f — Cancel unfilled parent → dormant TPs torn down, nothing leaks.

---

## D. Long→short flip

- [ ] D1 — Hold N, no reservations, market sell > N → closes long + opens short for excess; collateral
      posted; position negative.
- [ ] D2 — With shares reserved (armed stop / bracket SL), same flip → **rejected** ("shares reserved…").
- [ ] D3 — Limit sell beyond holdings → still rejected (no shorting via limit).

---

## E. Fill markers on chart

- [ ] E1 — Triangle on fill: buy = up/green/below price, sell = down/red/above; snapped to candle;
      theme-aware outline.
- [ ] E2 — Multiple fills of one order in a candle bucket → one **VWAP** marker.
- [ ] E3 — Switch resolution → markers re-bucket onto correct candles.

---

## F. Last-watched stock

- [ ] F1 — Select a stock, leave Trade page, return → restores last-watched stock (not the default).

---

## G. Closing invariant sweep

- [ ] G1 — Funds Reserved = baseline; Position Reserved = baseline.
- [ ] G2 — No ConservationProbe non-zero net-delta; no ReservationAuditor clamp warnings this session.
- [ ] G3 — Restart server once → armed stops + bracket children reload with correct Reserved amounts.

---

## H. Later-batch fixes (verify these too)

Shipped after the original checklist. Re-confirm none regressed.

- [ ] H1 — Armed **trigger** shows Modify/Cancel in the Open Orders table and cancels cleanly (released, no orphan).
- [ ] H2 — Placing a trigger fires a **"Trigger armed"** notification (was silent).
- [ ] H3 — Modify panel shows **Cancel | Remove** (row 1) + full-width **Confirm modification** (row 2); Remove cancels the order.
- [ ] H4 — Modify a trigger/limit price **across the market** → it goes through + acts like a market order, with a **non-blocking warning** (NOT rejected).
- [ ] H5 — Human deposit/withdrawal logs one **`Funds`** line each; bot top-ups do not.
- [ ] H6 — Bot logs (scaler/Fx/sentiment/botstats/boteconomy) silent; `MarketEngine`/`ConservationProbe`/`ReservationAuditor` still at Information.

### H7. Trade-table layout pass (newest changes)

- [ ] H7a — Switching bottom tabs (empty ↔ populated) does **not** resize the chart.
- [ ] H7b — Each table **scrolls** with the header pinned; 4-5 rows visible before scroll.
- [ ] H7c — Rows are compact and **all four tables line up** in height (Positions no taller than the rest).
- [ ] H7d — `↗` next to the symbol in **all four** tables selects that stock (chart + order book update).
- [ ] H7e — Positions table has **no Action column** (nav is the symbol `↗`).
- [ ] H7f — Tables card left edge aligns with the chart; right edge / panel card not clipped. Resize bigger + smaller.
