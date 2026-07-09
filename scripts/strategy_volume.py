#!/usr/bin/env python3
"""Per-strategy bot volume metrics.

For each AiStrategy: bot count, notional traded (USD; EUR converted ~x1.08), trade-sides,
and the PER-BOT normalisations (notional/bot, trades/bot, avg trade size). Answers
"is strategy X trading more/less volume per bot than the others".

Each transaction has a buyer and a seller, so it contributes TWO participation "sides"
(one to the buyer's strategy, one to the seller's). A bot's trade count = the number of
fills it was a party to; its notional = sum of Quantity*Price over those fills.

Data source: runs psql inside the Postgres container. Defaults to PROD (over ssh) because
the Rotator/BankEstimate cohort is only enabled there; use --local for a local soak DB.

Examples:
  py scripts/strategy_volume.py --minutes 20                 # prod, last 20 min
  py scripts/strategy_volume.py --local --db kse_rebase      # local soak DB, all history
"""
import argparse, subprocess, sys

STRAT = {0: "MarketMaker", 1: "TrendFollower", 2: "MeanReversion", 3: "Random",
         4: "Scalper", 5: "Arbitrage", 6: "MarketMakerHouse", 7: "Rotator"}

def run_psql(sql, args):
    """Pipe SQL via stdin (avoids all shell-quoting) to psql in the container."""
    inner = f'psql -U {args.user} -d {args.db} -F"|" -At'
    if args.local:
        cmd = ["docker", "exec", "-i", args.pg, "sh", "-c", inner]
    else:
        cmd = ["ssh", "-o", "ConnectTimeout=20", args.host,
               f"docker exec -i {args.pg} sh -c '{inner}'"]
    p = subprocess.run(cmd, input=sql, capture_output=True, text=True)
    if p.returncode != 0:
        sys.exit(f"psql failed:\n{p.stderr}")
    return [ln for ln in p.stdout.splitlines() if "|" in ln]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--local", action="store_true", help="query local docker instead of prod-over-ssh")
    ap.add_argument("--host", default="root@159.195.149.51")
    ap.add_argument("--pg", default=None, help="postgres container name")
    ap.add_argument("--user", default="kse")
    ap.add_argument("--db", default=None)
    ap.add_argument("--minutes", type=int, default=0, help="only trades in the last N minutes (0 = all history)")
    args = ap.parse_args()
    if args.pg is None:
        args.pg = "kieshstockexchange-postgres-1" if args.local else "kse-server-postgres-1"
    if args.db is None:
        args.db = "kse_rebase" if args.local else "kse"

    where = f"WHERE \"Timestamp\" > now() - interval '{args.minutes} min'" if args.minutes else ""
    fx = "(CASE WHEN \"Currency\"='EUR' THEN 1.08 ELSE 1 END)"
    vol_sql = f"""
WITH parts AS (
  SELECT "BuyerId" uid, "Quantity"*"Price"*{fx} n FROM "Transactions" {where}
  UNION ALL
  SELECT "SellerId" uid, "Quantity"*"Price"*{fx} n FROM "Transactions" {where}
)
SELECT a."Strategy", COUNT(*), COALESCE(ROUND(SUM(p.n)),0)
FROM parts p JOIN "AIUsers" a ON a."UserId" = p.uid
GROUP BY a."Strategy" ORDER BY a."Strategy";
"""
    cnt_sql = 'SELECT "Strategy", COUNT(*) FROM "AIUsers" GROUP BY "Strategy" ORDER BY "Strategy";'

    vol = {int(r.split("|")[0]): (int(r.split("|")[1]), float(r.split("|")[2])) for r in run_psql(vol_sql, args)}
    cnt = {int(r.split("|")[0]): int(r.split("|")[1]) for r in run_psql(cnt_sql, args)}

    src = f"prod ({args.db})" if not args.local else f"local ({args.db})"
    window = f"last {args.minutes} min" if args.minutes else "all history"
    print(f"\n=== per-strategy volume — {src}, {window} (notional in USD, EUR~x1.08) ===")
    hdr = f"{'strategy':<17}{'bots':>6}{'trades':>10}{'trd/bot':>9}{'notional$':>16}{'$/bot':>13}{'avgTrade$':>11}"
    print(hdr); print("-" * len(hdr))
    rows = []
    for s, bots in sorted(cnt.items()):
        sides, notional = vol.get(s, (0, 0.0))
        rows.append((STRAT.get(s, str(s)), bots, sides, sides/bots if bots else 0,
                     notional, notional/bots if bots else 0, notional/sides if sides else 0))
    for name, bots, sides, tpb, notl, npb, avg in sorted(rows, key=lambda r: -r[5]):
        print(f"{name:<17}{bots:>6}{sides:>10}{tpb:>9.1f}{notl:>16,.0f}{npb:>13,.0f}{avg:>11,.0f}")

if __name__ == "__main__":
    main()
