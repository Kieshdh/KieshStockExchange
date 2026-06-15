# Pipeline step 2-4: soak DB transactions -> 1-minute OHLCV candles -> CSV (persisted for later review).
# The soak DB is overwritten by the next run, so this CSV is the durable record. candle_plot.py then reads
# the CSV and aggregates 1-min bars up to any higher timeframe for the image (pipeline step 5).
#
# CSV columns: stock_id,bucket_epoch,open,high,low,close,volume   (RAW per-bucket OHLC — open is the first
# trade in the minute; the continuous-candle adjustment open=prev-close is applied at DISPLAY time, so the
# CSV stays ground-truth market data.)
#
# Usage: python scripts/candle_export.py --db kse_soak --out logs/candles-kse_soak-<ts>.csv [--window-min 0]
import argparse, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path

PG = "kieshstockexchange-postgres-1"
ROOT = Path(__file__).resolve().parent.parent

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--out", default="")
    ap.add_argument("--window-min", type=float, default=0.0, help="0 = all data in the DB")
    args = ap.parse_args()

    where = ""
    if args.window_min > 0:
        since = datetime.now(timezone.utc).timestamp() - args.window_min * 60
        where = f'WHERE extract(epoch from t."Timestamp") >= {since:.0f} '

    sql = (
        'SELECT t."StockId", '
        'floor(extract(epoch from t."Timestamp")/60)*60 AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" ASC))[1]  AS o, '
        'max(t."Price") AS h, min(t."Price") AS l, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS c, '
        'sum(t."Quantity") AS vol '
        'FROM "Transactions" t '
        f'{where}'
        'GROUP BY t."StockId", b ORDER BY t."StockId", b;'
    )
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", args.db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")

    rows = out.stdout.splitlines()
    body = [ln for ln in rows[1:] if ln.count(",") >= 6]
    if not body:
        sys.exit("no candles to export")

    ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    outp = Path(args.out) if args.out else (ROOT / "logs" / f"candles-{args.db}-{ts}.csv")
    if not outp.is_absolute():
        outp = ROOT / outp
    outp.parent.mkdir(parents=True, exist_ok=True)
    with open(outp, "w", encoding="utf-8", newline="") as f:
        f.write("stock_id,bucket_epoch,open,high,low,close,volume\n")
        for ln in body:
            p = ln.split(",")
            # stock_id, bucket_epoch (int), o,h,l,c, volume
            f.write(f"{int(p[0])},{int(float(p[1]))},{p[2]},{p[3]},{p[4]},{p[5]},{int(float(p[6]))}\n")
    print(f"wrote {outp}  ({len(body)} 1-min candles)")

if __name__ == "__main__":
    main()
