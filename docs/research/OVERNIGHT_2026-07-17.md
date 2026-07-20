# Overnight autonomous run — 2026-07-17 (Kiesh asleep ~03:38→wake)

Two tracks in parallel, per your instructions: finish the chart arc hands-on; run council-driven
drift A/B soaks + monitor prod in the background. **Nothing was flipped on prod. No prod default /
reseed / cutover was touched.** Client build is GREEN and all soak servers are killed, so the exe
links for your test.

---

## 1. Chart overhaul — what to test (all committed, full build green)

Branch `feature/bot-market-realism-v2`. Commits: `605606e`, `a681156`, `e54e224`.

**Snapshot (your two fixes):**
- Now fills the image **edge-to-edge** (was drawing into the top-left quadrant at 2×).
- Save-As default name carries the **ticker pairing**: `KSE-AAPL-USD-<timestamp>.png`.

**Left rail is now a full drawing suite:**
- Line/shape tools: Cursor · Trend · Ray · **ExtendedLine** · HLine · HRay · Polyline · **Rectangle** · **Ellipse** · Measure
- Actions: **Undo** · **Redo** · **Eye (hide/show all drawings)**
- The redundant pen-tray TOOL row was removed — the pen panel is now a pure **style editor**.

**New behaviors:**
- **Undo/Redo** of add / delete / move — rail buttons (grey out when empty) + **Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y**.
- **Delete / Backspace** removes the selected drawing (undoable).
- **Eye** toggle hides/shows all drawings non-destructively (highlighted while hidden).
- 6 new rail icons in the line-art style you approved.

**Deliberately deferred for your eyeball (NOT done blind — would have risked a build you can't test):**
grouped hover-flyouts, magnet-snap feel, Text / Position tools (need a renderer that doesn't exist yet),
and per-tool panel gating (the shared 2×8 style grid needs a row-swap that's a layout call best made live).

---

## 2. Market drift tuning — council + A/B result

**Goal:** nudge net drift from slightly-negative toward slightly-positive ("stairs up") without
breaking the scorecard. **Council (3 advisors) picked `DipBuyStrength 2.0 → 2.75`** — the scorecard's
designed stairs-up lever (dip-gated so it can't bubble; crashes still override the buy-floor). One
advisor dissented toward HOLD (the drift is a near-neutral structural floor; the market is
"close to perfect"). **All three agreed a 90-min soak cannot resolve a sub-1% drift *sign*.**

**Round-1 A/B (clean, matched paired execution, both full 90 min, CK=0/ERR=0/CONS=0 throughout):**

| Metric | Baseline (DipBuy 2.0) | DipBuy 2.75 | Read |
|---|---|---|---|
| Drift vs seed (final) | −0.97% | **−0.72%** | +0.25 pp, treatment less-negative at **all 18** checkpoints |
| Cross-sectional mean log-ret | −0.668% | −0.590% | +0.078% (but single-run SE ±0.46% ⇒ noise-dominated) |
| 1-min ret_acf (close basis) | −0.34 | −0.39 | slightly more negative = the predicted contrarian side-effect |
| Excess kurtosis | +1.93 | +1.50 | slightly thinner tails |
| Worst-stock drawdown | −18.3% | −17.5% | crashes **preserved** (only mild cushioning) |
| Bubble / ×3-cap pin | none | none | dip-gating held |

**Verdict:** DipBuy 2.75 is **SAFE** (CK-clean, no bubble, crashes intact) and gives a **consistent,
small positive drift lean** at a **mild ret_acf/kurtosis cost** — exactly the tradeoff the council
predicted. The drift *sign* is **not statistically resolvable** at soak scale (confirmed empirically:
per-run dispersion dwarfs the ~0.08–0.25 pp effect).

**Dose-response attempt (DipBuy 3.5):** confounded — run solo it executed ~2× faster (uncontended)
and accrued ~2× the order flow, so it isn't comparable to the paired 2.0/2.75 points. Lesson logged:
run soaks solo, or with a ≥4-min stagger; never near-simultaneous 2-server (mutual startup starvation).

**I stopped soaking here** rather than chase a noise-dominated number across more runs — that's the
"converged-market treadmill" the council explicitly warned against.

### Recommendation (your call — bake is owner-gated)
- **Option A — HOLD.** The market is close-to-perfect; intraday drift is a structural near-neutral
  that cash-injection already tilts positive over a *week*. This is where advisor-2 (and arguably the
  evidence) leans.
- **Option B — owner-gated multi-day prod A/B of `DipBuyStrength 2.75`.** It's safe and directionally
  favorable; only a multi-day prod read (not a soak) can actually confirm the drift flips positive.
  It's a pure config env flip, reversible, no reseed.

I did **not** bake or deploy anything.

---

## 3. Prod
Healthy the entire window — `kse-server-server-1` Up, CK/ERR = 0 at every check. Untouched.

## Artifacts
- Candle CSVs: `data/soaks/candles-kse_drift_{base,dip,d35}-*.csv`
- Soak logs: `logs/soakP-kse_drift_*-*.log`
