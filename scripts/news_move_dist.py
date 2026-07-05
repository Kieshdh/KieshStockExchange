# News-move distribution: reads a soak candle CSV (candle_export.py output) and reports the realized move-size
# distribution vs the target band (common 5-15%, big 20-30%). Two views:
#   - per-stock MAX EXCURSION from seed (|high|/|low| vs the first open) = the biggest move each stock made
#   - N-min |close-to-close return| percentiles = the individual news bumps (p90/p99 = the big-news moves)
# Usage: py scripts/news_move_dist.py --csv data/soaks/candles-kse_news_hi-<ts>.csv [--bucket-min 15]
import argparse, math
from collections import defaultdict


def load(path):
    rows = defaultdict(list)  # stock_id -> [(epoch, open, high, low, close), ...]
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or line.startswith("stock_id"):
                continue
            p = line.strip().split(",")
            if len(p) < 6:
                continue
            try:
                rows[int(p[0])].append((int(p[1]), float(p[2]), float(p[3]), float(p[4]), float(p[5])))
            except ValueError:
                continue
    for sid in rows:
        rows[sid].sort()
    return rows


def pct(xs, q):
    xs = sorted(xs)
    if not xs:
        return 0.0
    return xs[min(len(xs) - 1, int(q * len(xs)))]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", required=True)
    ap.add_argument("--bucket-min", type=int, default=15, help="return window in minutes")
    args = ap.parse_args()

    rows = load(args.csv)
    max_exc, win_ret, up_exc, down_exc, up_ret, down_ret = [], [], [], [], [], []
    for sid, cs in rows.items():
        seed = cs[0][1]
        if seed <= 0:
            continue
        me = 0.0
        hi = 0.0   # biggest signed UP excursion from seed (>= 0)
        lo = 0.0   # biggest signed DOWN excursion from seed (<= 0)
        for (_, _o, h, l, _c) in cs:
            me = max(me, abs(h / seed - 1.0), abs(l / seed - 1.0))
            hi = max(hi, h / seed - 1.0)
            lo = min(lo, l / seed - 1.0)
        max_exc.append(me)
        up_exc.append(hi)
        down_exc.append(-lo)   # store the down excursion as a positive magnitude

        bsec = args.bucket_min * 60
        buckets = {}
        for (e, _o, _h, _l, c) in cs:
            buckets[e // bsec] = c  # last close in the bucket
        keys = sorted(buckets)
        for i in range(1, len(keys)):
            prev, cur = buckets[keys[i - 1]], buckets[keys[i]]
            if prev > 0:
                r = cur / prev - 1.0
                win_ret.append(abs(r))
                (up_ret if r >= 0 else down_ret).append(abs(r))

    print(f"stocks={len(max_exc)}  windows={len(win_ret)} ({args.bucket_min}min)")
    print("per-stock MAX EXCURSION from seed  (target: typical stock 5-15%, biggest 20-30%):")
    for q, lbl in [(0.50, "p50"), (0.75, "p75"), (0.90, "p90"), (0.99, "p99"), (1.0, "max")]:
        print(f"   {lbl}: {pct(max_exc, q) * 100:6.1f}%")
    print("UP vs DOWN excursion  (log-symmetry: down/up log-ratio 1.00 = symmetric; <1 = down-tail too shallow):")
    for q, lbl in [(0.50, "p50"), (0.75, "p75"), (0.90, "p90"), (0.99, "p99")]:
        u, d = pct(up_exc, q), pct(down_exc, q)
        lu, ld = math.log(1 + u), math.log(1 + d)
        ratio = (ld / lu) if lu > 1e-9 else 0.0
        print(f"   {lbl}: up +{u * 100:5.1f}%   down -{d * 100:5.1f}%   log-ratio {ratio:4.2f}")
    print(f"{args.bucket_min}-min |return|  (individual news bumps; p90/p99 = big-news moves):")
    for q, lbl in [(0.50, "p50"), (0.90, "p90"), (0.95, "p95"), (0.99, "p99"), (1.0, "max")]:
        print(f"   {lbl}: {pct(win_ret, q) * 100:6.1f}%")
    print(f"{args.bucket_min}-min SIGNED return  (per-move symmetry; up ~= down = the CHART moves are symmetric):")
    for q, lbl in [(0.50, "p50"), (0.90, "p90"), (0.99, "p99")]:
        print(f"   {lbl}: up +{pct(up_ret, q) * 100:5.2f}%   down -{pct(down_ret, q) * 100:5.2f}%")


if __name__ == "__main__":
    main()
