"""R4 §0009 Stage 1+2 probe analysis.

Reads Stage 1 (MatchSymmetryProbe) and Stage 2 (BotDecisionProbe) CSVs and reports
the per-surface attribution stats needed to decide Stage 3.

Five report blocks:
  1. Decision-side buy/sell ratio + per-hour bucket (probe self-consistency vs matcher 1.27x)
  2. Per-(strategy, inventory bucket) component decomposition of buy_prob - 0.5
  3. Advanced bracket cohort (kindPre, bias, kindPost) cross-tab + result success rate
  4. MarketMaker quote-side ratio
  5. Matcher depth context (when depth_ctx rows exist)
"""
import csv, sys, statistics, random
from collections import defaultdict, Counter
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LOG_DIR = ROOT / "KieshStockExchange.Server" / "logs"
MATCHER_CSV = LOG_DIR / "match-symmetry-probe.csv"
BOT_CSV = LOG_DIR / "bot-decision-probe.csv"

# ---------- Stage 1: matcher / settler symmetry (unchanged from earlier version) ----------
matcher_groups = defaultdict(list)
depth_rows = defaultdict(list)  # side -> list of (levelIdx, depth)
if MATCHER_CSV.exists():
    with open(MATCHER_CSV, newline="") as f:
        for row in csv.DictReader(f):
            ctx = row["context"]
            key = (row["surface"], row["side"], ctx)
            val = float(row["value"])
            if ctx == "depth_ctx":
                packed = int(val)
                level = packed // 1_000_000
                depth = packed % 1_000_000
                depth_rows[row["side"]].append((level, depth))
            else:
                matcher_groups[key].append(val)

# ---------- Stage 2: bot decision probe ----------
plain_rows = []     # list of dicts
adv_intent = []     # list of (strategy, kindPre, bias, kindPost)
adv_result = []     # list of (strategy, kindPost, qty, flipQty, success)
mm_rows = []        # list of (botId, buys, sells, choseBuy)

def parse_int(v):
    if v == "" or v is None: return None
    try: return int(v)
    except: return None

def parse_float(v):
    if v == "" or v is None: return None
    try: return float(v)
    except: return None

if BOT_CSV.exists():
    with open(BOT_CSV, newline="") as f:
        for row in csv.DictReader(f):
            s = row["surface"]
            if s == "plain":
                plain_rows.append({
                    "ts": row["timestamp"],
                    "bot_id": parse_int(row["bot_id"]),
                    "strategy": parse_int(row["strategy"]),
                    "cash_prc": parse_float(row["cash_prc"]),
                    "inv_notional": parse_float(row["inv_notional"]),
                    "homeostatic": parse_float(row["homeostatic"]),
                    "directional_eff": parse_float(row["directional_eff"]),
                    "anchor": parse_float(row["anchor"]),
                    "herd": parse_float(row["herd"]),
                    "buy_prob": parse_float(row["buy_prob"]),
                    "is_buy": parse_int(row["is_buy"]),
                    "is_market": parse_int(row["is_market"]),
                })
            elif s == "adv_intent":
                adv_intent.append((parse_int(row["strategy"]), parse_int(row["kind_pre"]),
                                   parse_int(row["bias"]), parse_int(row["kind_post"])))
            elif s == "adv_result":
                adv_result.append((parse_int(row["strategy"]), parse_int(row["kind_post"]),
                                   parse_int(row["qty"]), parse_int(row["flip_qty"]),
                                   parse_int(row["is_buy"])))  # is_buy column repurposed as success
            elif s == "mm":
                mm_rows.append((parse_int(row["bot_id"]), parse_int(row["mm_buys"]),
                                parse_int(row["mm_sells"]), parse_int(row["is_buy"])))

# ---------- helpers ----------
def percentile(xs, p):
    if not xs: return float("nan")
    xs_s = sorted(xs)
    idx = max(0, min(len(xs_s)-1, int(len(xs_s) * p)))
    return xs_s[idx]

def bootstrap_ci(xs, n=1000, alpha=0.05):
    if len(xs) < 30: return (float("nan"), float("nan"))
    rng = random.Random(42)
    means = []
    for _ in range(n):
        sample = [xs[rng.randrange(len(xs))] for _ in range(len(xs))]
        means.append(sum(sample)/len(sample))
    means.sort()
    return (means[int(n*alpha/2)], means[int(n*(1-alpha/2))])

STRAT_NAMES = {0: "Random", 1: "TrendFollower", 2: "MeanReversion", 3: "MarketMaker", 4: "Scalper"}
KIND_NAMES = {0: "LongBracket", 1: "ShortBracket"}

# ---------- Stage 1 quick view ----------
print("=" * 80)
print("STAGE 1 — MatchSymmetryProbe summary")
print("=" * 80)
if matcher_groups:
    hdr = f"{'group':<40} {'count':>8} {'mean':>10} {'median':>10}"
    print(hdr); print("-" * len(hdr))
    for k, vals in sorted(matcher_groups.items(), key=lambda kv: -len(kv[1])):
        if not vals: continue
        n = len(vals)
        print(f"{'/'.join(k):<40} {n:>8} {statistics.mean(vals):>10.2f} {statistics.median(vals):>10.2f}")
    m_buy = matcher_groups.get(("matcher","buy","fill_vs_limit"), [])
    m_sell = matcher_groups.get(("matcher","sell","fill_vs_limit"), [])
    if m_buy and m_sell:
        ratio = len(m_sell)/len(m_buy) if len(m_buy) else float("nan")
        print(f"\nMatcher fill count: sell={len(m_sell)} buy={len(m_buy)} sell/buy={ratio:.3f}x")
else:
    print("(no matcher probe data)")

# ---------- block 1: decision-side buy/sell ratio ----------
print()
print("=" * 80)
print("BLOCK 1 — Decision-side buy/sell ratio (probe self-consistency)")
print("=" * 80)
if plain_rows:
    n_buy = sum(1 for r in plain_rows if r["is_buy"] == 1)
    n_sell = sum(1 for r in plain_rows if r["is_buy"] == 0)
    print(f"Decision rows: buy={n_buy} sell={n_sell} total={len(plain_rows)}")
    if n_buy > 0:
        ratio = n_sell / n_buy
        print(f"Decision sell/buy = {ratio:.3f}x  (matcher Stage 1 was 1.27x)")
        within_5pct = abs(ratio - 1.27) / 1.27 < 0.05 if ratio > 0 else False
        print(f"  -> within 5% of 1.27 matcher signal? {within_5pct}")
    # hourly bucket
    by_hour = defaultdict(lambda: [0, 0])
    for r in plain_rows:
        try:
            hr = datetime.fromisoformat(r["ts"].rstrip("Z")).strftime("%Y-%m-%d %H")
            if r["is_buy"] == 1: by_hour[hr][0] += 1
            elif r["is_buy"] == 0: by_hour[hr][1] += 1
        except: pass
    print(f"\nPer-hour buy/sell stability:")
    print(f"  {'hour':<16} {'buy':>8} {'sell':>8} {'sell/buy':>10}")
    for hr in sorted(by_hour.keys()):
        b, s = by_hour[hr]
        r = s/b if b > 0 else float("nan")
        print(f"  {hr:<16} {b:>8} {s:>8} {r:>10.3f}")
else:
    print("(no plain-path probe rows)")

# ---------- block 2: per-strategy x inventory bucket component decomposition ----------
print()
print("=" * 80)
print("BLOCK 2 — Per-(strategy, inventory bucket) component decomposition")
print("=" * 80)
INV_THRESHOLD_PRC = 0.05
if plain_rows:
    def inv_bucket(r):
        # Without portfolio value we can't compute exact threshold. Use signed notional sign
        # as a proxy: very-positive = heavy-long, very-negative = heavy-short, near-zero = flat.
        v = r["inv_notional"] or 0
        if v > 1000: return "long_heavy"
        if v < -1000: return "short_heavy"
        return "flat"

    by_group = defaultdict(list)
    for r in plain_rows:
        strat = STRAT_NAMES.get(r["strategy"], f"strat{r['strategy']}")
        by_group[(strat, inv_bucket(r))].append(r)

    total = len(plain_rows)
    print(f"{'group':<35} {'n':>8} {'pct':>6} {'mean_buyP':>10} {'mean_homeo':>11} {'mean_dir':>10} {'mean_anch':>10} {'mean_herd':>10}")
    print("-" * 110)
    for key in sorted(by_group.keys()):
        rs = by_group[key]
        n = len(rs)
        if n < 5: continue
        bp = [r["buy_prob"] for r in rs if r["buy_prob"] is not None]
        ho = [r["homeostatic"] for r in rs if r["homeostatic"] is not None]
        di = [r["directional_eff"] for r in rs if r["directional_eff"] is not None]
        an = [r["anchor"] for r in rs if r["anchor"] is not None]
        he = [r["herd"] for r in rs if r["herd"] is not None]
        print(f"{'/'.join(key):<35} {n:>8} {n/total*100:>5.1f}% "
              f"{statistics.mean(bp):>10.3f} {statistics.mean(ho):>11.3f} "
              f"{statistics.mean(di):>10.3f} {statistics.mean(an):>10.3f} {statistics.mean(he):>10.3f}")

    # Overall component contributions to (buy_prob - 0.5)
    print("\nOverall mean contribution to (buy_prob - 0.5):")
    bp_all = [r["buy_prob"] - 0.5 for r in plain_rows if r["buy_prob"] is not None]
    print(f"  mean(buy_prob - 0.5) = {statistics.mean(bp_all):.4f}")
    for comp in ["homeostatic", "directional_eff", "anchor", "herd"]:
        xs = [r[comp] for r in plain_rows if r[comp] is not None]
        if xs:
            mean = statistics.mean(xs)
            lo, hi = bootstrap_ci(xs)
            contrib_pct = abs(mean) / max(abs(statistics.mean(bp_all)), 1e-9) * 100
            print(f"  {comp:<20} mean={mean:>8.4f}  95%CI=({lo:.4f},{hi:.4f})  contrib={contrib_pct:.1f}%")

    # Fire criterion
    print("\nStage 2 fire criterion (>=40% of |mean(buy_prob - 0.5)| with CI lower bound):")
    target = abs(statistics.mean(bp_all))
    fired = False
    for comp in ["homeostatic", "directional_eff", "anchor", "herd"]:
        xs = [r[comp] for r in plain_rows if r[comp] is not None]
        if not xs: continue
        lo, hi = bootstrap_ci(xs)
        ci_lo_abs = min(abs(lo), abs(hi))
        pct = ci_lo_abs / max(target, 1e-9) * 100
        status = "FIRES" if pct >= 40 else "below"
        print(f"  {comp:<20} ci_lower_abs={ci_lo_abs:.4f}  vs target={target:.4f}  ({pct:.1f}% — {status})")
        if pct >= 40: fired = True
    if not fired:
        print("  No single surface fires the 40% gate -> asymmetry is DISTRIBUTED.")
else:
    print("(no plain-path probe rows)")

# ---------- block 3: advanced cohort cross-tab ----------
print()
print("=" * 80)
print("BLOCK 3 — Advanced cohort (kindPre, bias, kindPost) cross-tab")
print("=" * 80)
if adv_intent:
    counts = Counter()
    for s, kp, b, kpo in adv_intent:
        counts[(KIND_NAMES.get(kp,"?"), b, KIND_NAMES.get(kpo,"?"))] += 1
    print(f"{'kindPre':<15} {'bias':>6} {'kindPost':<15} {'count':>10} {'pct':>6}")
    total = sum(counts.values())
    for (kp, b, kpo), n in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"{kp:<15} {b:>6} {kpo:<15} {n:>10} {n/total*100:>5.1f}%")
    flips = sum(n for (kp, b, kpo), n in counts.items() if kp != kpo)
    print(f"\nInversions (kindPre != kindPost): {flips}/{total} = {flips/total*100:.1f}%")

if adv_result:
    by_kind_success = defaultdict(lambda: [0, 0])  # kindPost -> [success, total]
    for s, kpo, q, fq, success in adv_result:
        by_kind_success[KIND_NAMES.get(kpo,"?")][1] += 1
        if success: by_kind_success[KIND_NAMES.get(kpo,"?")][0] += 1
    print(f"\nBracket-build success rate by kindPost:")
    for k, (succ, tot) in by_kind_success.items():
        print(f"  {k:<15} {succ}/{tot} = {succ/tot*100:.1f}% success")
else:
    print("(no advanced result rows)")

# ---------- block 4: MarketMaker quote ratio ----------
print()
print("=" * 80)
print("BLOCK 4 — MarketMaker quote-side ratio")
print("=" * 80)
if mm_rows:
    n_buy = sum(1 for r in mm_rows if r[3] == 1)
    n_sell = sum(1 for r in mm_rows if r[3] == 0)
    print(f"MM quote rows: choseBuy={n_buy} choseSell={n_sell} total={len(mm_rows)}")
    if n_buy + n_sell > 0:
        net = (n_buy - n_sell) / (n_buy + n_sell)
        print(f"Net buy-quote bias: {net:+.3f}  (positive = MMs lean toward buy quote)")
    # Bot-level mean ratio
    per_bot = defaultdict(list)
    for bid, buys, sells, choseBuy in mm_rows:
        if buys is None or sells is None: continue
        denom = max(1, buys + sells)
        per_bot[bid].append((buys - sells) / denom)
    bot_means = [statistics.mean(vs) for vs in per_bot.values() if vs]
    if bot_means:
        print(f"Per-bot mean (buys-sells)/(buys+sells): mean={statistics.mean(bot_means):+.4f} median={statistics.median(bot_means):+.4f}")
else:
    print("(no MM probe rows)")

# ---------- block 5: depth context ----------
print()
print("=" * 80)
print("BLOCK 5 — Matcher depth context")
print("=" * 80)
if depth_rows:
    for side in ("buy", "sell"):
        rs = depth_rows.get(side, [])
        if not rs: continue
        levels = [r[0] for r in rs]
        depths = [r[1] for r in rs]
        print(f"{side}-taker: n={len(rs)} mean_levelIdx={statistics.mean(levels):.2f} "
              f"median_levelIdx={statistics.median(levels)} "
              f"mean_oppositeWallDepth={statistics.mean(depths):.0f} "
              f"median_oppositeWallDepth={statistics.median(depths)}")
else:
    print("(no depth_ctx rows — flag Bots:MatchSymmetryProbeDepthContext was off)")
