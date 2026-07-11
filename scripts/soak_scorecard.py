#!/usr/bin/env python3
"""One durable scorecard row per soak — the preferred-stats panel in a single command.

Reads the soak DB directly (Transactions) and appends one CSV row to data/soaks/SCORECARD.csv:
ret_acf on last/mid/vwap 1-min closes (VWAP = the OFFICIAL scoring series; last/mid reported for
honesty), demeaned intra-vs-inter sector gap @5/10min (+ placebo p), 1-min sigma + excess kurtosis,
Conviction cohort realized W/L (avg-cost), and trade totals. Pure Python + docker psql, ASCII.

Usage: py scripts/soak_scorecard.py --db kse_bundle2 [--note "..."] [--conviction-lo 19701 --conviction-hi 20000]
"""
import argparse, csv, math, os, subprocess, sys
from collections import defaultdict
from datetime import datetime, timezone

PG = "kieshstockexchange-postgres-1"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "data", "soaks", "SCORECARD.csv")


def psql(db, sql):
    r = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
                       capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("psql failed: " + r.stderr.strip())
    return r.stdout.strip().splitlines()


def acf1(xs):
    n = len(xs)
    if n < 3:
        return None
    m = sum(xs) / n
    den = sum((x - m) ** 2 for x in xs)
    if den <= 0:
        return None
    return sum((xs[i] - m) * (xs[i + 1] - m) for i in range(n - 1)) / den


def logrets(prices):
    out = []
    for i in range(1, len(prices)):
        a, b = prices[i - 1], prices[i]
        if a and b and a > 0 and b > 0:
            out.append(math.log(b / a))
    return out


def median(vals):
    vals = sorted(v for v in vals if v is not None)
    if not vals:
        return None
    n = len(vals)
    return vals[n // 2] if n % 2 else (vals[n // 2 - 1] + vals[n // 2]) / 2


def minute_series(db):
    """Per-stock per-minute last/mid/vwap closes (USD book)."""
    rows = psql(db, '''
SELECT "StockId", floor(EXTRACT(EPOCH FROM "Timestamp")/60)::bigint AS m,
       (array_agg("Price" ORDER BY "Timestamp" DESC, "TransactionId" DESC))[1],
       (array_agg(COALESCE("MidPrice","Price") ORDER BY "Timestamp" DESC, "TransactionId" DESC))[1],
       sum("Price"*"Quantity")/NULLIF(sum("Quantity"),0)
FROM "Transactions" WHERE "Currency"='USD' GROUP BY 1,2 ORDER BY 1,2;''')[1:]
    per = defaultdict(lambda: ([], [], [], []))  # sid -> (minutes, last, mid, vwap)
    for ln in rows:
        sid, m, last, mid, vwap = ln.split(",")
        s = per[sid]
        s[0].append(int(m)); s[1].append(float(last)); s[2].append(float(mid)); s[3].append(float(vwap))
    return per


def ret_stats(per):
    """Median 1-min ret_acf per close basis + median sigma/kurtosis on the vwap basis."""
    a_last, a_mid, a_vwap, sigmas, kurts = [], [], [], [], []
    for sid, (mins, last, mid, vwap) in per.items():
        if len(mins) < 30:
            continue
        a_last.append(acf1(logrets(last))); a_mid.append(acf1(logrets(mid)))
        rv = logrets(vwap)
        a_vwap.append(acf1(rv))
        if len(rv) >= 30:
            mu = sum(rv) / len(rv)
            var = sum((x - mu) ** 2 for x in rv) / len(rv)
            if var > 0:
                sigmas.append(math.sqrt(var))
                kurts.append(sum((x - mu) ** 4 for x in rv) / (len(rv) * var * var) - 3.0)
    return median(a_last), median(a_mid), median(a_vwap), median(sigmas), median(kurts)


def sector_gap(per, horizon):
    """Demeaned intra-vs-inter gap on vwap minute closes + label-shuffle placebo p (B=500)."""
    sys.path.insert(0, os.path.join(ROOT, "Tools"))
    import Config
    smap = {sid: s.get("sector", "?") for sid, s in Config.STOCKS.items()}
    # log-returns over `horizon` minutes on the shared grid, then per-minute cross-stock demean
    grid = defaultdict(dict)
    for sid, (mins, _, _, vwap) in per.items():
        idx = dict(zip(mins, vwap))
        for m, c1 in idx.items():
            c0 = idx.get(m - horizon)
            if c0 and c0 > 0 and c1 > 0:
                grid[sid][m] = math.log(c1 / c0)
    bs, bn = defaultdict(float), defaultdict(int)
    for rr in grid.values():
        for m, r in rr.items():
            bs[m] += r; bn[m] += 1
    for rr in grid.values():
        for m in rr:
            if bn[m] >= 3:
                rr[m] -= bs[m] / bn[m]
    stocks = sorted(grid, key=lambda s: int(s))
    pairs = []
    for i in range(len(stocks)):
        for j in range(i + 1, len(stocks)):
            a, b = stocks[i], stocks[j]
            common = sorted(set(grid[a]) & set(grid[b]))
            if len(common) < 5:
                continue
            xs = [grid[a][m] for m in common]; ys = [grid[b][m] for m in common]
            n = len(xs); mx, my = sum(xs) / n, sum(ys) / n
            sxx = sum((x - mx) ** 2 for x in xs); syy = sum((y - my) ** 2 for y in ys)
            if sxx <= 0 or syy <= 0:
                continue
            r = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / math.sqrt(sxx * syy)
            pairs.append((int(a), int(b), r))
    def gap(label_of):
        intra = [r for a, b, r in pairs if label_of(a) == label_of(b)]
        inter = [r for a, b, r in pairs if label_of(a) != label_of(b)]
        if not intra or not inter:
            return None
        return sum(intra) / len(intra) - sum(inter) / len(inter)
    obs = gap(lambda s: smap.get(s))
    if obs is None:
        return None, None
    import random
    rng = random.Random(42)
    ids = sorted({a for a, _, _ in pairs} | {b for _, b, _ in pairs})
    labels = [smap.get(i) for i in ids]
    ge = 0; B = 500
    for _ in range(B):
        rng.shuffle(labels)
        sh = dict(zip(ids, labels))
        g = gap(lambda s: sh.get(s))
        if g is not None and g >= obs:
            ge += 1
    return obs, ge / B


def conviction_wl(db, lo, hi):
    """Realized W/L on costed round-trips (avg-cost, long side) for the Conviction id range."""
    rows = psql(db, f'''
SELECT "BuyerId","SellerId","StockId","Quantity","Price" FROM "Transactions"
WHERE ("BuyerId" BETWEEN {lo} AND {hi}) OR ("SellerId" BETWEEN {lo} AND {hi})
ORDER BY "Timestamp","TransactionId";''')[1:]
    pos = defaultdict(lambda: [0.0, 0.0])
    w = l = 0; tot = 0.0
    for ln in rows:
        b, s, stk, q, p = ln.split(",")
        b, s, stk, q, p = int(b), int(s), int(stk), float(q), float(p)
        if lo <= b <= hi:
            st = pos[(b, stk)]
            nq = st[0] + q
            st[1] = (st[0] * st[1] + q * p) / nq if nq > 0 else 0.0
            st[0] = nq
        if lo <= s <= hi:
            st = pos[(s, stk)]
            if st[0] > 0:
                pnl = (p - st[1]) * min(q, st[0])
                tot += pnl
                if pnl > 0.005: w += 1
                elif pnl < -0.005: l += 1
            st[0] = max(0.0, st[0] - q)
    return w, l, tot


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", required=True)
    ap.add_argument("--note", default="")
    ap.add_argument("--conviction-lo", type=int, default=19701)
    ap.add_argument("--conviction-hi", type=int, default=20000)
    args = ap.parse_args()

    trades = psql(args.db, 'SELECT count(*) FROM "Transactions";')[1]
    per = minute_series(args.db)
    a_last, a_mid, a_vwap, sigma, kurt = ret_stats(per)
    gap5, p5 = sector_gap(per, 5)
    gap10, p10 = sector_gap(per, 10)
    w, l, pnl = conviction_wl(args.db, args.conviction_lo, args.conviction_hi)

    row = {
        "ts": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%MZ"),
        "db": args.db, "trades": trades,
        "ret_acf_vwap": round(a_vwap, 3) if a_vwap is not None else "",
        "ret_acf_mid": round(a_mid, 3) if a_mid is not None else "",
        "ret_acf_last": round(a_last, 3) if a_last is not None else "",
        "sigma_1m": round(sigma, 5) if sigma is not None else "",
        "kurtosis_1m": round(kurt, 2) if kurt is not None else "",
        "sector_gap5": round(gap5, 4) if gap5 is not None else "",
        "sector_p5": round(p5, 3) if p5 is not None else "",
        "sector_gap10": round(gap10, 4) if gap10 is not None else "",
        "sector_p10": round(p10, 3) if p10 is not None else "",
        "cnv_w": w, "cnv_l": l, "cnv_pnl": round(pnl, 0),
        "note": args.note,
    }
    exists = os.path.exists(OUT)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "a", newline="", encoding="utf-8") as fh:
        wri = csv.DictWriter(fh, fieldnames=list(row.keys()))
        if not exists:
            wri.writeheader()
        wri.writerow(row)
    print("scorecard row appended -> " + OUT)
    for k, v in row.items():
        print("  %-14s %s" % (k, v))


if __name__ == "__main__":
    main()
