# Candle-realism probe: quantify how "flat" the bot market's candles are vs a real market.
#
# A candle's High/Low is just the spread of executed trade prices in its bucket (see
# Candle.ApplyTrade). Flat, consistent candles => every trade that minute printed at almost
# the same price (smooth directional drift + the anti-sweep/slippage caps), the opposite of a
# real market where price wanders within the bar (wicks, varied ranges). This script rebuilds
# OHLCV per bucket from the Postgres Transactions (same source the server builds candles from)
# and reports the shape metrics that expose flatness:
#   range CV     - how much the bar range varies across candles (LOW = all bars look the same)
#   body/range   - |close-open| / (high-low); HIGH (~1) = directional, no wick (the flat look)
#   wick %       - 1 - body/range; LOW = flat
#   has-wick %   - fraction of bars with any wick at all
#   flat %       - fraction with high==low (single-trade / gap-fill dojis)
#   range~vol r  - do big bars carry volume (volatility-volume link, a real stylized fact)
#
# Each metric is printed next to a DRIFTLESS RANDOM-WALK baseline simulated with the same
# per-candle trade counts. The body/range and wick shape metrics are scale-invariant, so the
# RW column is the "looks like a real market" target regardless of price scale. The gap between
# YOURS and RW is the flatness you're trying to close.
#
# Usage:
#   python scripts/candle_realism.py [--db kse_soak] [--bucket-sec 60] [--window-min 180]
#          [--stocks 1,12,33] [--seed 7]

import argparse, random, subprocess, sys
from collections import defaultdict
from datetime import datetime, timezone

PG = "kieshstockexchange-postgres-1"

# --- personality classification: mirror StockProfileService.Get exactly ---
def avalanche(sid: int) -> int:
    M = (1 << 64) - 1
    h = (sid * 0x9E3779B97F4A7C15 + 0x165667B19E3779F9) & M
    h ^= h >> 33; h = (h * 0xFF51AFD7ED558CCD) & M; h ^= h >> 33
    return h

def stock_class(sid: int) -> str:
    if 1 <= sid <= 5:
        return "Calm"
    b = avalanche(sid) % 100
    return "Calm" if b < 35 else "Normal" if b < 75 else "Volatile" if b < 93 else "Meme"

def pearson(xs, ys):
    n = len(xs)
    if n < 3:
        return None
    mx = sum(xs) / n; my = sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs); syy = sum((y - my) ** 2 for y in ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx <= 0 or syy <= 0:
        return None
    return sxy / (sxx ** 0.5 * syy ** 0.5)

def mean(xs):
    return sum(xs) / len(xs) if xs else float("nan")

def std(xs):
    if len(xs) < 2:
        return 0.0
    m = mean(xs)
    return (sum((x - m) ** 2 for x in xs) / (len(xs) - 1)) ** 0.5

def autocorr1(xs):
    # lag-1 autocorrelation of the range series (clustering: do big bars follow big bars)
    if len(xs) < 4:
        return None
    return pearson(xs[:-1], xs[1:])

# --- load OHLCV candles rebuilt from trades (primary-currency listing per stock) ---
def load_candles(db: str, since_epoch: float, bucket: int):
    # stockId -> list of (open, high, low, close, volume, tradecount) in bucket order
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
    out = subprocess.run(
        ["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
        capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    series = defaultdict(list)
    for ln in out.stdout.splitlines()[1:]:  # skip header
        p = ln.split(",")
        if len(p) < 8:
            continue
        sid = int(p[0])
        o, h, l, c = float(p[2]), float(p[3]), float(p[4]), float(p[5])
        vol, trades = float(p[6]), int(p[7])
        series[sid].append((o, h, l, c, vol, trades))
    return series

# --- shape metrics for one list of candles ---
def candle_metrics(candles):
    ranges, bodies_ratio, wick, vols, has_wick, flat = [], [], [], [], 0, 0
    for o, h, l, c, vol, _t in candles:
        if o <= 0:
            continue
        rng = (h - l) / o          # range as a fraction of open (scale-free across stocks)
        ranges.append(rng)
        vols.append(vol)
        if h == l:
            flat += 1
            bodies_ratio.append(1.0)  # degenerate bar: define body/range = 1 (no wick)
            wick.append(0.0)
            continue
        body = abs(c - o)
        br = body / (h - l)
        bodies_ratio.append(br)
        wick.append(1.0 - br)
        if h > max(o, c) or l < min(o, c):
            has_wick += 1
    n = len(ranges)
    if n == 0:
        return None
    mr = mean(ranges)
    return {
        "n": n,
        "range_cv": (std(ranges) / mr) if mr > 0 else 0.0,
        "body_ratio": mean(bodies_ratio),
        "wick_frac": mean(wick),
        "has_wick_pct": 100.0 * has_wick / n,
        "flat_pct": 100.0 * flat / n,
        "range_vol_r": pearson(ranges, vols),
        "range_ac1": autocorr1(ranges),
        "max_over_median": (max(ranges) / sorted(ranges)[n // 2]) if sorted(ranges)[n // 2] > 0 else float("nan"),
    }

# --- driftless random-walk baseline with the same per-candle trade counts ---
def simulate_rw_metrics(candles, rng):
    sim = []
    for _o, _h, _l, _c, vol, t in candles:
        t = max(1, t)
        p = 0.0; lo = hi = 0.0
        for _ in range(t - 1):
            p += rng.gauss(0.0, 1.0)          # unit-sigma steps; shape metrics are scale-free
            lo = min(lo, p); hi = max(hi, p)
        o_s, c_s = 0.0, p
        # shift onto a positive synthetic price so (h-l)/o is well-defined like the real bars
        base = 1000.0
        sim.append((base, base + hi, base + lo, base + c_s, vol, t))
    return candle_metrics(sim)

def fmt(v, nd=3):
    if v is None:
        return "  n/a"
    if isinstance(v, float) and v != v:  # NaN
        return "  n/a"
    return f"{v:>6.{nd}f}"

def print_block(title, m, rw):
    print(f"\n{title}")
    print(f"  candles            {m['n']:>6}")
    print(f"  range CV           {fmt(m['range_cv'])}      RW {fmt(rw['range_cv'])}   (higher = bars vary; flat market is low)")
    print(f"  body/range         {fmt(m['body_ratio'])}      RW {fmt(rw['body_ratio'])}   (lower = more wick; ~1 is the flat look)")
    print(f"  wick fraction      {fmt(m['wick_frac'])}      RW {fmt(rw['wick_frac'])}")
    print(f"  has-wick %         {fmt(m['has_wick_pct'],1)}      RW {fmt(rw['has_wick_pct'],1)}")
    print(f"  flat (H==L) %      {fmt(m['flat_pct'],1)}      RW {fmt(rw['flat_pct'],1)}")
    print(f"  range~volume r     {fmt(m['range_vol_r'])}      RW {fmt(rw['range_vol_r'])}   (volume-volatility link)")
    print(f"  range AC(1)        {fmt(m['range_ac1'])}      RW {fmt(rw['range_ac1'])}   (clustering)")
    print(f"  max/median range   {fmt(m['max_over_median'],2)}      RW {fmt(rw['max_over_median'],2)}   (spike presence)")

# --- magnitude budget: per-stock move over the window, scaled to a 4h reference ---
def magnitude_report(series, window_min, max_move_pct):
    # net move and high-low excursion per stock, scaled to a 4h window so the budget
    # (typical <= ~5% / rare ~20% per 4h) reads regardless of the soak length.
    scale = (240.0 / window_min) if window_min > 0 else 1.0
    nets, excursions, breaches = [], [], []
    for sid, cs in series.items():
        if not cs:
            continue
        first_open = cs[0][0]
        if first_open <= 0:
            continue
        last_close = cs[-1][3]
        hi = max(c[1] for c in cs); lo = min(c[2] for c in cs)
        net_4h = abs(last_close - first_open) / first_open * 100 * scale
        exc_4h = (hi - lo) / first_open * 100 * scale
        nets.append(net_4h); excursions.append((exc_4h, sid))
        if net_4h > max_move_pct:
            breaches.append((net_4h, sid))
    if not nets:
        return
    raws = sorted(x / scale for x in nets)   # un-scaled per-window moves (no 4h projection)
    nets.sort(); excursions.sort(reverse=True)
    n = len(nets)
    p = lambda q: nets[min(n - 1, int(q * n))]
    pr = lambda q: raws[min(n - 1, int(q * n))]
    print(f"\n=== MAGNITUDE BUDGET (per stock; target: typical <= {max_move_pct:.0f}% / 4h, ~20% rare) ===")
    print(f"  |net move| raw  median {pr(0.5):>6.2f}%   p95 {pr(0.95):>6.2f}%   max {raws[-1]:>6.2f}%   (over {window_min:.0f}m, n={n})")
    print(f"  |net move| 4h*  median {p(0.5):>6.2f}%   p95 {p(0.95):>6.2f}%   max {nets[-1]:>6.2f}%   (*linear projection = pessimistic upper bound)")
    print(f"  over budget     {len(breaches):>3} stock(s) > {max_move_pct:.0f}%   "
          f"({100.0*len(breaches)/n:.1f}% of names)")
    if breaches:
        worst = sorted(breaches, reverse=True)[:5]
        print("  worst movers    " + ", ".join(f"{sid}({mv:.1f}%)" for mv, sid in worst))
    print("  Reading it: p95 should sit near the budget; only a thin tail should approach ~20%.")
    print("  Cross-check drift vs seed with scripts/balance-drift.sql (medianAbs/beyond50) — the variance gate.")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--bucket-sec", type=int, default=60, help="candle resolution to rebuild (default 1m)")
    ap.add_argument("--window-min", type=float, default=180.0, help="minutes back from now")
    ap.add_argument("--stocks", default="", help="explicit comma-separated stock ids (else per-class + overall)")
    ap.add_argument("--seed", type=int, default=7, help="RNG seed for the random-walk baseline")
    ap.add_argument("--max-move-pct", type=float, default=5.0, help="per-4h move budget; names above are flagged")
    args = ap.parse_args()

    now = datetime.now(timezone.utc).timestamp()
    since = now - args.window_min * 60
    print(f"window: last {args.window_min:.0f} min   bucket: {args.bucket_sec}s   db: {args.db}")

    series = load_candles(args.db, since, args.bucket_sec)
    if not series:
        sys.exit("no traded candles in the window")
    rng = random.Random(args.seed)

    # magnitude budget first — the hard "don't move too much" constraint
    magnitude_report(series, args.window_min, args.max_move_pct)

    # overall (all stocks pooled)
    allc = [c for cs in series.values() for c in cs]
    m, rw = candle_metrics(allc), simulate_rw_metrics(allc, rng)
    if m:
        print_block("=== ALL STOCKS ===", m, rw)

    # per personality class (so you can see Meme should be wilder than Calm)
    by_class = defaultdict(list)
    for sid, cs in series.items():
        by_class[stock_class(sid)].extend(cs)
    for cls in ("Calm", "Normal", "Volatile", "Meme"):
        cs = by_class.get(cls)
        if not cs:
            continue
        m = candle_metrics(cs)
        if m:
            print_block(f"--- class {cls} ---", m, simulate_rw_metrics(cs, rng))

    # explicit stocks if requested
    for sid in (int(x) for x in args.stocks.split(",") if x.strip()):
        cs = series.get(sid)
        if not cs:
            print(f"\nstock {sid}: no candles in window")
            continue
        m = candle_metrics(cs)
        if m:
            print_block(f"--- stock {sid} [{stock_class(sid)}] ---", m, simulate_rw_metrics(cs, rng))

    print("\nReading it: the bigger the gap between body/range (yours, high) and RW (lower),")
    print("the flatter/more-directional your candles are vs a real-looking market.")

if __name__ == "__main__":
    main()
