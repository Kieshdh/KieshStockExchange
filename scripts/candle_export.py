# Pipeline step 2-4: soak DB transactions -> 1-minute OHLCV candles -> CSV (persisted for later review).
# The soak DB is overwritten by the next run, so this CSV is the durable record; candle_plot.py reads it
# (--csv) and aggregates up to any timeframe for the image (step 5).
#
# Each file is self-describing: a '#'-commented metadata header records what produced it (git commit/branch,
# the soak note, the Bots__* experiment overrides auto-captured from the environment) and session stats
# (trades / volume / notional / stocks / time-span / candle count), so soaks are trivially comparable later.
# Files are written to data/soaks/ by default.
#
# CSV body columns: stock_id,bucket_epoch,open,high,low,close,volume   (RAW per-bucket OHLC — open is the
# first trade in the minute; the open=prev-close continuity is applied at DISPLAY time, so the CSV stays
# ground-truth market data.)
#
# Usage: python scripts/candle_export.py --db kse_soak [--note "..."] [--label "..."] [--out path]
import argparse, os, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path

PG = "kieshstockexchange-postgres-1"
ROOT = Path(__file__).resolve().parent.parent

def psql(db, sql):
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    return out.stdout

def git(*args):
    try:
        return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True).stdout.strip()
    except Exception:
        return "?"

def experiment_env():
    # The soak script sets $env:Bots__* before launching; those are inherited here. Capture them as the
    # "what changed / what we tested" record. (Windows env names are case-insensitive; match loosely.)
    return {k: v for k, v in sorted(os.environ.items()) if k.lower().startswith("bots__")}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--note", default="")
    ap.add_argument("--label", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--window-min", type=float, default=0.0, help="0 = all data in the DB")
    ap.add_argument("--close", choices=["last", "vwap"], default="last",
                    help="close source: last trade in bucket (default) or per-bucket VWAP")
    args = ap.parse_args()

    where = ""
    if args.window_min > 0:
        since = datetime.now(timezone.utc).timestamp() - args.window_min * 60
        where = f'WHERE extract(epoch from t."Timestamp") >= {since:.0f} '

    # Restrict to each stock's PRIMARY listing — otherwise a dual-listed stock mixes its USD and EUR
    # trades into one candle, faking a ~FX-gap-wide (~7%) wick every bar. (r4_realism_score uses the same join.)
    join = ('JOIN "StockListings" sl ON sl."StockId"=t."StockId" '
            'AND sl."Currency"=t."Currency" AND sl."IsPrimary"=true ')

    # 1-min OHLCV body. --close vwap swaps the last-trade close for the per-bucket VWAP (de-bounced).
    close_expr = ('sum(t."Price"*t."Quantity")/NULLIF(sum(t."Quantity"),0)' if args.close == "vwap"
                  else '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1]')
    sql = ('SELECT t."StockId", floor(extract(epoch from t."Timestamp")/60)*60 AS b, '
           '(array_agg(t."Price" ORDER BY t."Timestamp" ASC))[1]  AS o, '
           'max(t."Price") AS h, min(t."Price") AS l, '
           f'{close_expr} AS c, '
           'sum(t."Quantity") AS vol '
           f'FROM "Transactions" t {join}{where}GROUP BY t."StockId", b ORDER BY t."StockId", b;')
    body = [ln for ln in psql(args.db, sql).splitlines()[1:] if ln.count(",") >= 6]
    if not body:
        sys.exit("no candles to export")

    # Session stats (whole DB, independent of the candle window).
    stat_sql = ('SELECT count(*), coalesce(sum("Quantity"),0), coalesce(sum("Price"*"Quantity"),0), '
                'count(distinct "StockId"), min("Timestamp"), max("Timestamp") FROM "Transactions";')
    s = psql(args.db, stat_sql).splitlines()[1].split(",")
    trades, volume, notional, stocks, first_ts, last_ts = s[0], s[1], s[2], s[3], s[4], s[5]
    span_min = ""
    try:
        span_min = f"{(datetime.fromisoformat(last_ts) - datetime.fromisoformat(first_ts)).total_seconds()/60:.1f}"
    except Exception:
        pass

    ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    outp = Path(args.out) if args.out else (ROOT / "data" / "soaks" / f"candles-{args.db}-{ts}.csv")
    if not outp.is_absolute():
        outp = ROOT / outp
    outp.parent.mkdir(parents=True, exist_ok=True)

    env = experiment_env()
    with open(outp, "w", encoding="utf-8", newline="") as f:
        f.write("# === KSE soak candle export ===\n")
        f.write(f"# exported_utc: {datetime.now(timezone.utc).isoformat()}\n")
        f.write(f"# db: {args.db}\n")
        if args.note:  f.write(f"# note: {args.note}\n")
        if args.label: f.write(f"# label: {args.label}\n")
        if args.close != "last": f.write(f"# close_mode: {args.close}\n")
        f.write(f"# git_commit: {git('rev-parse','--short','HEAD')}\n")
        f.write(f"# git_branch: {git('rev-parse','--abbrev-ref','HEAD')}\n")
        f.write(f"# experiment_overrides ({len(env)} Bots__* env): "
                + ("; ".join(f"{k}={v}" for k, v in env.items()) if env else "(none — baseline/baked config)") + "\n")
        f.write("# stats:\n")
        f.write(f"#   trades: {trades}\n")
        f.write(f"#   volume: {volume}\n")
        f.write(f"#   notional: {notional}\n")
        f.write(f"#   stocks: {stocks}\n")
        f.write(f"#   candles_1m: {len(body)}\n")
        f.write(f"#   first_trade_utc: {first_ts}\n")
        f.write(f"#   last_trade_utc: {last_ts}\n")
        f.write(f"#   span_min: {span_min}\n")
        f.write("# === candles (1-min OHLCV) ===\n")
        f.write("stock_id,bucket_epoch,open,high,low,close,volume\n")
        for ln in body:
            p = ln.split(",")
            f.write(f"{int(p[0])},{int(float(p[1]))},{p[2]},{p[3]},{p[4]},{p[5]},{int(float(p[6]))}\n")
    print(f"wrote {outp}  ({len(body)} 1-min candles, {trades} trades, {stocks} stocks)")

if __name__ == "__main__":
    main()
