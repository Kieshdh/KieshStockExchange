# Order-wall concentration diagnostic. For each (stock, side) with enough open limit orders, measures how
# concentrated resting volume is on individual price levels — the "wall" signature. Three metrics, averaged:
#   top_level_share : largest single price-level qty / total side qty  (1.0 = one monolithic wall)
#   hhi             : Herfindahl over price levels (sum of squared shares; higher = more concentrated)
#   round_share     : qty sitting exactly on the round-number snap grid / total  (the wall source)
# Lower on all three = volume spread naturally across levels instead of stacked into walls.
import argparse, subprocess, sys
from collections import defaultdict

PG = "kieshstockexchange-postgres-1"

def snap_unit(price):
    if price >= 500: return 5.0
    if price >= 100: return 1.0
    if price >= 20:  return 0.5
    return 0.1

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--min-orders", type=int, default=50)
    args = ap.parse_args()

    sql = ('SELECT "StockId","Side","Price",sum("Quantity"-"AmountFilled") '
           'FROM "Orders" WHERE "Status"=\'Open\' AND "Entry"=\'Limit\' '
           'GROUP BY "StockId","Side","Price";')
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", args.db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")

    # (stock,side) -> list of (price, qty)
    books = defaultdict(list)
    norders = defaultdict(int)
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 4 or not p[3]:
            continue
        books[(int(p[0]), p[1])].append((float(p[2]), float(p[3])))
        norders[(int(p[0]), p[1])] += 1

    tops, hhis, rounds = [], [], []
    for key, levels in books.items():
        total = sum(q for _, q in levels)
        if total <= 0 or norders[key] < args.min_orders:
            continue
        tops.append(max(q for _, q in levels) / total)
        hhis.append(sum((q / total) ** 2 for _, q in levels))
        rq = 0.0
        for price, q in levels:
            u = snap_unit(price)
            if abs(price / u - round(price / u)) < 1e-6:
                rq += q
        rounds.append(rq / total)

    n = len(tops)
    print(f"db={args.db}  (stock,side) books measured={n} (>= {args.min_orders} levels)")
    if n:
        print(f"  top_level_share : {sum(tops)/n:.3f}   (largest single price level / side volume)")
        print(f"  hhi             : {sum(hhis)/n:.4f}   (price-level concentration)")
        print(f"  round_share     : {sum(rounds)/n:.3f}   (volume sitting exactly on the round grid)")

if __name__ == "__main__":
    main()
