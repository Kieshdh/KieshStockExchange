"""R4 realism scorer.

Extends scripts/candle_realism.py with the rest of Cont's stylized facts so a config
experiment can be ranked on a single number. Higher = closer to real-market behavior.

Usage:
    python scripts/r4_realism_score.py [--db kse_soak] [--bucket-sec 60]
       [--window-min 180] [--stocks 1,5,12] [--label EXP_NAME]

Output: per-stock + aggregate "stylized facts" table + composite realism score.
"""
import argparse, math, random, subprocess, sys
import statistics
from collections import defaultdict
from pathlib import Path

PG = "kieshstockexchange-postgres-1"
ROOT = Path(__file__).resolve().parent.parent


# ---------- DB ----------
def load_candles(db, since_epoch, bucket):
    sql = (
        'SELECT t."StockId", '
        f'floor(extract(epoch from t."Timestamp")/{bucket})*{bucket} AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" ASC))[1]  AS o, '
        'max(t."Price") AS h, min(t."Price") AS l, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS c, '
        'sum(t."Quantity") AS vol, count(*) AS trades '
        'FROM "Transactions" t '
        'JOIN "StockListings" sl ON sl."StockId"=t."StockId" '
        '  AND sl."Currency"=t."Currency" AND sl."IsPrimary"=true '
        f'WHERE extract(epoch from t."Timestamp") >= {since_epoch:.0f} '
        'GROUP BY t."StockId", b ORDER BY t."StockId", b;'
    )
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    series = defaultdict(list)
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 8:
            continue
        sid = int(p[0])
        o, h, l, c = float(p[2]), float(p[3]), float(p[4]), float(p[5])
        vol, trades = float(p[6]), int(p[7])
        series[sid].append((o, h, l, c, vol, trades))
    return series


def stock_class(sid):
    if 1 <= sid <= 5:
        return "Calm"
    M = (1 << 64) - 1
    h = (sid * 0x9E3779B97F4A7C15 + 0x165667B19E3779F9) & M
    h ^= h >> 33
    h = (h * 0xFF51AFD7ED558CCD) & M
    h ^= h >> 33
    b = h % 100
    return "Calm" if b < 35 else "Normal" if b < 75 else "Volatile" if b < 93 else "Meme"


# ---------- stats helpers ----------
def pearson(xs, ys):
    n = len(xs)
    if n < 3:
        return None
    mx, my = sum(xs)/n, sum(ys)/n
    sxx = sum((x-mx)**2 for x in xs)
    syy = sum((y-my)**2 for y in ys)
    sxy = sum((x-mx)*(y-my) for x, y in zip(xs, ys))
    if sxx <= 0 or syy <= 0: return None
    return sxy / (sxx**0.5 * syy**0.5)


def acf(xs, lag):
    if len(xs) <= lag + 2:
        return None
    return pearson(xs[:-lag], xs[lag:])


def excess_kurtosis(xs):
    n = len(xs)
    if n < 4:
        return None
    m = sum(xs)/n
    s2 = sum((x-m)**2 for x in xs) / n
    if s2 <= 0:
        return None
    m4 = sum((x-m)**4 for x in xs) / n
    return m4/(s2**2) - 3.0


def skewness(xs):
    n = len(xs)
    if n < 3:
        return None
    m = sum(xs)/n
    s2 = sum((x-m)**2 for x in xs) / n
    if s2 <= 0:
        return None
    m3 = sum((x-m)**3 for x in xs) / n
    return m3 / (s2 ** 1.5)


def hill_tail_index(abs_xs, k_frac=0.05):
    """Hill estimator for tail index alpha; lower = fatter tail. k = top k_frac of order stats."""
    n = len(abs_xs)
    if n < 50: return None
    k = max(10, int(k_frac * n))
    if k >= n: return None
    s = sorted(abs_xs, reverse=True)
    threshold = s[k]
    if threshold <= 0: return None
    lnratios = [math.log(s[i] / threshold) for i in range(k) if s[i] > 0]
    if not lnratios: return None
    return 1.0 / (sum(lnratios) / len(lnratios))


def returns(closes):
    """1-bar log returns."""
    out = []
    for i in range(1, len(closes)):
        if closes[i-1] > 0 and closes[i] > 0:
            out.append(math.log(closes[i] / closes[i-1]))
    return out


# ---------- per-stock metrics ----------
def stock_metrics(candles):
    if len(candles) < 30:
        return None
    closes = [c[3] for c in candles]
    rng = []
    body_ratio = []
    has_wick = 0
    flat = 0
    vols = []
    for o, h, l, c, vol, _t in candles:
        if o <= 0: continue
        rng_pct = (h - l) / o
        rng.append(rng_pct)
        vols.append(vol)
        if h == l:
            flat += 1
            body_ratio.append(1.0)
            continue
        br = abs(c - o) / (h - l)
        body_ratio.append(br)
        if h > max(o, c) or l < min(o, c):
            has_wick += 1

    rets = returns(closes)
    abs_rets = [abs(r) for r in rets]
    n = len(rng)

    return {
        "n_candles": n,
        # Candle shape (existing)
        "range_pct_mean": sum(rng)/n if n else None,
        "range_cv": statistics.stdev(rng)/statistics.mean(rng) if n > 1 and statistics.mean(rng) > 0 else None,
        "body_ratio_mean": sum(body_ratio)/n if n else None,
        "has_wick_pct": 100.0*has_wick/n if n else None,
        "flat_pct": 100.0*flat/n if n else None,
        "range_vol_corr": pearson(rng, vols),
        # Return distribution (stylized facts)
        "n_returns": len(rets),
        "return_skew": skewness(rets) if rets else None,
        "return_kurt_excess": excess_kurtosis(rets) if rets else None,
        # Linear unpredictability — should be ~0
        "ret_acf_lag1": acf(rets, 1),
        "ret_acf_lag5": acf(rets, 5),
        # Volatility clustering — should decay slowly
        "absret_acf_lag1": acf(abs_rets, 1),
        "absret_acf_lag5": acf(abs_rets, 5),
        "absret_acf_lag20": acf(abs_rets, 20),
        # Heavy tail
        "tail_alpha": hill_tail_index(abs_rets),
        # Volume-volatility — should be > 0
        "absret_vol_corr": pearson(abs_rets[:len(vols)-1], vols[1:len(abs_rets)+1]) if len(vols) > 1 else None,
    }


# ---------- realism scoring ----------
TARGETS = {
    # Each is (target_value, tolerance, direction)
    # direction: "near" = closer to target is better; "above" = > target is better; "below" = < target is better
    "body_ratio_mean":   (0.50, 0.15, "near"),     # 0.30-0.65 ideal
    "has_wick_pct":      (85,   15,   "above"),    # >= 85% has wick
    "flat_pct":          (5,    5,    "below"),    # <= 10% flat
    "range_vol_corr":    (0.30, 0.30, "above"),    # positive correlation
    "return_kurt_excess":(5,    20,   "above"),    # excess kurtosis > 3 (target 5)
    "tail_alpha":        (3.5,  1.5,  "near"),     # tail index [2, 5]
    "ret_acf_lag1":      (0,    0.10, "near"),     # near zero
    "ret_acf_lag5":      (0,    0.05, "near"),     # near zero
    "absret_acf_lag1":   (0.20, 0.15, "above"),    # volatility clustering > 0
    "absret_acf_lag5":   (0.10, 0.10, "above"),    # decays slowly
    "absret_acf_lag20":  (0.05, 0.05, "above"),    # still positive at lag 20
}


def score_metric(value, target, tol, direction):
    if value is None:
        return 0.0
    if direction == "near":
        dist = abs(value - target)
        return max(0.0, 1.0 - dist / tol) if tol > 0 else 0.0
    if direction == "above":
        if value >= target: return 1.0
        diff = target - value
        return max(0.0, 1.0 - diff / tol) if tol > 0 else 0.0
    if direction == "below":
        if value <= target: return 1.0
        diff = value - target
        return max(0.0, 1.0 - diff / tol) if tol > 0 else 0.0
    return 0.0


def composite_score(metrics):
    scores = {}
    total = 0.0
    weight_sum = 0.0
    weights = {
        "body_ratio_mean":   2.0,
        "has_wick_pct":      2.0,
        "flat_pct":          1.0,
        "range_vol_corr":    1.0,
        "return_kurt_excess":1.5,
        "tail_alpha":        1.5,
        "ret_acf_lag1":      1.0,
        "ret_acf_lag5":      1.0,
        "absret_acf_lag1":   1.5,
        "absret_acf_lag5":   1.5,
        "absret_acf_lag20":  1.0,
    }
    for key, (tgt, tol, dir) in TARGETS.items():
        s = score_metric(metrics.get(key), tgt, tol, dir)
        scores[key] = s
        total += s * weights.get(key, 1.0)
        weight_sum += weights.get(key, 1.0)
    return scores, total / weight_sum * 100.0 if weight_sum else 0.0


# ---------- main ----------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--bucket-sec", type=int, default=60)
    ap.add_argument("--window-min", type=float, default=180.0)
    ap.add_argument("--stocks", default="")
    ap.add_argument("--label", default="")
    ap.add_argument("--since-epoch", type=float, default=0.0,
                    help="Fixed UTC epoch start (overrides --window-min)")
    ap.add_argument("--per-class", type=int, default=4,
                    help="Stocks sampled per volatility class (default 4 = 16 total). Higher = less noise.")
    args = ap.parse_args()

    import datetime
    if args.since_epoch > 0:
        since = args.since_epoch
    else:
        since = datetime.datetime.now(datetime.timezone.utc).timestamp() - args.window_min * 60
    series = load_candles(args.db, since, args.bucket_sec)
    if not series:
        sys.exit("no candles in window")

    if args.stocks:
        sids = [int(x) for x in args.stocks.split(",") if x.strip()]
    else:
        # auto: pick most-active per class
        # Sample N most-active stocks per class (default 4 = 16 total) so a single trending
        # name can't swing the composite — the 1-per-class default was too noisy (±20 pts).
        by_class = defaultdict(list)
        for sid, cs in series.items():
            by_class[stock_class(sid)].append((len(cs), sid))
        sids = []
        for cls in ("Calm", "Normal", "Volatile", "Meme"):
            top = sorted(by_class.get(cls, []), reverse=True)[:args.per_class]
            sids.extend(s for _, s in top)

    label = f"  [{args.label}]" if args.label else ""
    print(f"=== R4 Realism Score {label} db={args.db} window={args.window_min}m bucket={args.bucket_sec}s ===")
    print(f"Stocks measured: {sids}")
    print()

    all_metrics = []
    for sid in sids:
        cs = series.get(sid)
        if not cs or len(cs) < 30:
            print(f"  Stock {sid} [{stock_class(sid)}]: skipped (only {len(cs) if cs else 0} candles)")
            continue
        m = stock_metrics(cs)
        if not m: continue
        m["stock"] = sid
        m["class"] = stock_class(sid)
        all_metrics.append(m)

    if not all_metrics:
        sys.exit("no stocks had enough candles")

    # Aggregate
    keys = list(TARGETS.keys())
    agg = {}
    for k in keys:
        vals = [m[k] for m in all_metrics if m[k] is not None]
        agg[k] = sum(vals)/len(vals) if vals else None

    # Per-stock table
    print(f"{'sid/cls':<14} {'br_mean':>8} {'wick%':>7} {'flat%':>6} {'rv_r':>6} {'rt_kurt':>8} {'tail_a':>7} {'rAC1':>6} {'rAC5':>6} {'absAC1':>7} {'absAC5':>7} {'absAC20':>8}")
    for m in all_metrics:
        line = f"{m['stock']:>4} {m['class']:<8}"
        for k in ["body_ratio_mean","has_wick_pct","flat_pct","range_vol_corr","return_kurt_excess","tail_alpha","ret_acf_lag1","ret_acf_lag5","absret_acf_lag1","absret_acf_lag5","absret_acf_lag20"]:
            v = m[k]
            if v is None: line += "    n/a"
            elif k in ("has_wick_pct","flat_pct"): line += f" {v:>6.1f}"
            elif k == "return_kurt_excess": line += f" {v:>7.2f}"
            else: line += f" {v:>6.3f}"
        print(line)

    print()
    print("Aggregate (mean across stocks):")
    scores, composite = composite_score(agg)
    print(f"  {'metric':<22} {'target':>9} {'actual':>10} {'score':>7}  notes")
    print(f"  {'-'*22:<22} {'-'*9:>9} {'-'*10:>10} {'-'*7:>7}")
    notes = {
        "body_ratio_mean":    "0.30-0.65 ideal (typical 1min)",
        "has_wick_pct":       ">= 85% bars have a wick",
        "flat_pct":           "<= 5% (h==l) ideal",
        "range_vol_corr":     "positive correlation > 0.3",
        "return_kurt_excess": "fat tails: excess >> 0",
        "tail_alpha":         "Hill index ~ 2-5 (cont 2001)",
        "ret_acf_lag1":       "should be ~ 0 (no autocorr)",
        "ret_acf_lag5":       "should be ~ 0",
        "absret_acf_lag1":    "volatility clustering, > 0.15",
        "absret_acf_lag5":    "decays slowly, > 0.05",
        "absret_acf_lag20":   "still > 0 at lag 20",
    }
    for k in keys:
        tgt, tol, dir = TARGETS[k]
        actual = agg[k]
        actual_str = f"{actual:>10.3f}" if actual is not None else "       n/a"
        tgt_str = f"{tgt:>9.2f}" if dir != "near" else f"~{tgt:>8.2f}"
        score_str = f"{scores[k]*100:>6.1f}%"
        print(f"  {k:<22} {tgt_str:>9} {actual_str:>10} {score_str:>7}  {notes.get(k,'')}")

    print()
    print(f"  Composite realism score:  {composite:>5.1f} / 100")
    print()
    print("  Higher is better. <40 = unrealistic, 40-70 = okay, 70-90 = good, >90 = excellent.")


if __name__ == "__main__":
    main()
