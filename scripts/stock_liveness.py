# Per-stock liveness / empty-candle probe (P2 gate: every book traded >=1x per 15s).
#
# Empty 15-second candles happen on THIN books (low-volume EUR secondary listings) that go
# silent between trades. The realism/flatness scripts sample the PRIMARY listing only, so they
# can't see the thin-EUR gaps, and a plain GROUP BY drops the fully-silent books entirely --
# yet those are the worst P2 failures. This walks the raw Transactions log for EVERY book
# (all StockId x Currency listings, via a LEFT JOIN from StockListings) and reports, per book
# over a SHARED window anchored to the data (so it works post-hoc):
#   trades       - fills in the window
#   max_gap_s    - longest silence between two consecutive trades (P2 fails if > threshold)
#   empty_15s_%  - fraction of 15s buckets with zero trades (the empty-candle rate)
# Zero- and one-trade books surface at the top (their silence spans the whole window).
# Use it to calibrate the active-bot count: raise activation until max_gap clears 15s on every
# book with margin, then read the currency split to confirm the thin EUR listings are covered.
#
# Usage:
#   py scripts/stock_liveness.py [--db kse_soak] [--bucket-sec 15] [--gap-threshold 15]
#      [--window-min N] [--worst 20] [--primary-only]

import argparse, subprocess, sys
from datetime import datetime, timezone

PG = "kieshstockexchange-postgres-1"

# --- personality class: mirror StockProfileService.Get (annotate the thin offenders) ---
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

def psql(db: str, sql: str):
    out = subprocess.run(
        ["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
        capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    return out.stdout.splitlines()

# --- data span so the window is correct whether run live or post-hoc ---
def data_span(db: str):
    rows = psql(db, 'SELECT min(extract(epoch from "Timestamp")), '
                    'max(extract(epoch from "Timestamp")) FROM "Transactions";')
    p = rows[1].split(",") if len(rows) > 1 else []
    if len(p) < 2 or not p[0] or not p[1]:
        sys.exit("no transactions in this db")
    return float(p[0]), float(p[1])

# --- per-book aggregate over the shared window (every listing, silent ones included) ---
def load_books(db: str, start: float, end: float, bucket: int, primary_only: bool):
    prim = 'WHERE "IsPrimary"=true' if primary_only else ""
    sql = (
        f'WITH prim AS (SELECT "StockId" AS sid, "Currency" AS ccy FROM "StockListings" {prim}), '
        'tx AS ('
        '  SELECT t."StockId" AS sid, t."Currency" AS ccy, extract(epoch from t."Timestamp") AS ts '
        '  FROM "Transactions" t JOIN prim p ON p.sid=t."StockId" AND p.ccy=t."Currency" '
        f'  WHERE extract(epoch from t."Timestamp") BETWEEN {start:.0f} AND {end:.0f}), '
        'g AS (SELECT sid, ccy, ts, ts - LAG(ts) OVER (PARTITION BY sid,ccy ORDER BY ts) AS gap FROM tx), '
        'agg AS (SELECT sid, ccy, count(*) AS trades, max(gap) AS max_gap, avg(gap) AS avg_gap, '
        f'        count(DISTINCT floor((ts-{start:.0f})/{bucket})) AS occ FROM g GROUP BY sid, ccy) '
        'SELECT p.sid, p.ccy, COALESCE(a.trades,0), a.max_gap, a.avg_gap, COALESCE(a.occ,0) '
        'FROM prim p LEFT JOIN agg a ON a.sid=p.sid AND a.ccy=p.ccy '
        'ORDER BY COALESCE(a.trades,0) ASC;'
    )
    rows = []
    for ln in psql(db, sql)[1:]:  # skip header
        c = ln.split(",")
        if len(c) < 6:
            continue
        rows.append({
            "sid": int(c[0]), "ccy": c[1], "trades": int(c[2]),
            "max_gap": float(c[3]) if c[3] else None,
            "avg_gap": float(c[4]) if c[4] else None,
            "occ": int(c[5]),
        })
    return rows

def median(xs):
    xs = sorted(xs)
    n = len(xs)
    return float("nan") if n == 0 else xs[n // 2]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--bucket-sec", type=int, default=15, help="empty-bucket resolution (P2 = 15s)")
    ap.add_argument("--gap-threshold", type=float, default=15.0, help="max silence before a book fails P2")
    ap.add_argument("--window-min", type=float, default=0.0, help="minutes back from data end (0 = whole soak)")
    ap.add_argument("--worst", type=int, default=20, help="how many worst books to list")
    ap.add_argument("--primary-only", action="store_true", help="only primary listings (hides thin EUR books)")
    args = ap.parse_args()

    lo, hi = data_span(args.db)
    end = hi
    start = (end - args.window_min * 60) if args.window_min > 0 else lo
    start = max(start, lo)
    win_min = (end - start) / 60.0
    bucket, thr = args.bucket_sec, args.gap_threshold
    total_buckets = max(1, round((end - start) / bucket))
    iso = lambda t: datetime.fromtimestamp(t, timezone.utc).strftime("%H:%M:%S")
    print(f"db: {args.db}   window: {win_min:.1f}m [{iso(start)}->{iso(end)}]   "
          f"bucket: {bucket}s   gap-threshold: {thr:.0f}s   total buckets: {total_buckets}")

    rows = load_books(args.db, start, end, bucket, args.primary_only)
    if not rows:
        sys.exit("no listings found")

    # effective silence: 0/1-trade books never clear the window, so their gap spans it
    for r in rows:
        if r["trades"] < 2 or r["max_gap"] is None:
            r["eff_gap"] = win_min * 60.0
        else:
            r["eff_gap"] = r["max_gap"]
        r["empty_pct"] = 100.0 * (1 - r["occ"] / total_buckets)
        r["fail"] = r["eff_gap"] > thr

    n = len(rows)
    usd = sum(1 for r in rows if r["ccy"] == "USD")
    eur = sum(1 for r in rows if r["ccy"] == "EUR")
    fails = [r for r in rows if r["fail"]]
    zero = sum(1 for r in rows if r["trades"] == 0)
    print(f"books: {n} ({usd} USD / {eur} EUR / {n - usd - eur} other)")

    print(f"\n=== P2 LIVENESS (every book traded >=1x per {thr:.0f}s) ===")
    print(f"  books failing (>{thr:.0f}s silence) : {len(fails):>3}/{n}   ({100.0*len(fails)/n:.1f}%)"
          + ("   <-- P2 VIOLATED" if fails else "   <-- P2 PASS"))
    print(f"  fully silent (0 trades)         : {zero:>3}")
    print(f"  empty-{bucket}s-bucket rate (median) : {median([r['empty_pct'] for r in rows]):>5.1f}%")
    worst_gap = max(rows, key=lambda r: r["eff_gap"])
    print(f"  longest silence anywhere        : {worst_gap['eff_gap']:>5.0f}s "
          f"(stock {worst_gap['sid']} {worst_gap['ccy']}, {worst_gap['trades']} trades)")

    for ccy in ("USD", "EUR"):
        c = [r for r in rows if r["ccy"] == ccy]
        if not c:
            continue
        print(f"  {ccy}: median max_gap {median([r['eff_gap'] for r in c]):>5.0f}s  "
              f"median empty {median([r['empty_pct'] for r in c]):>5.1f}%  "
              f"failing {sum(1 for r in c if r['fail'])}/{len(c)}")

    w = sorted(rows, key=lambda r: r["eff_gap"], reverse=True)[:args.worst]
    print(f"\n  worst {len(w)} books (longest silence first):")
    print("    stock  ccy  class      trades   max_gap_s  avg_gap_s  empty_%")
    for r in w:
        mg = f"{r['max_gap']:>7.1f}" if r["max_gap"] is not None else "    n/a"
        ag = f"{r['avg_gap']:>6.2f}" if r["avg_gap"] is not None else "   n/a"
        flag = " *" if r["fail"] else ""
        print(f"    {r['sid']:>5}  {r['ccy']:<3}  {stock_class(r['sid']):<8}  "
              f"{r['trades']:>6}   {mg}    {ag}   {r['empty_pct']:>5.1f}{flag}")

    print(f"\nReading it: max_gap_s > {thr:.0f} (starred) = that book had an empty {bucket}s candle. "
          "Calibrate the active-bot count up until this list is empty (or only rare, thin EUR books remain).")

if __name__ == "__main__":
    main()
