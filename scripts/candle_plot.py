# Render real candlestick charts from a soak DB so the market can be eyeballed (the ultimate
# "is it realistic" test). Rebuilds 1m OHLC from Postgres Transactions (same source the server
# builds candles from), one panel per stock, chosen across the Calm/Normal/Volatile/Meme classes.
#
# Usage:
#   python scripts/candle_plot.py [--db kse_soak] [--bucket-sec 60] [--window-min 20]
#          [--stocks 1,12,33] [--out logs/candles.png] [--title "A1+A2"]

import argparse, subprocess, sys
from collections import defaultdict
from datetime import datetime, timezone
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from pathlib import Path

PG = "kieshstockexchange-postgres-1"
ROOT = Path(__file__).resolve().parent.parent

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

def load(db, since, bucket):
    # stockId -> list[(t_epoch, o,h,l,c, vol)] in bucket order
    sql = (
        'SELECT t."StockId", '
        f'floor(extract(epoch from t."Timestamp")/{bucket})*{bucket} AS b, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" ASC))[1]  AS o, '
        'max(t."Price") AS h, min(t."Price") AS l, '
        '(array_agg(t."Price" ORDER BY t."Timestamp" DESC))[1] AS c, '
        'sum(t."Quantity") AS vol '
        'FROM "Transactions" t '
        'JOIN "StockListings" sl ON sl."StockId"=t."StockId" '
        '  AND sl."Currency"=t."Currency" AND sl."IsPrimary"=true '
        f'WHERE extract(epoch from t."Timestamp") >= {since:.0f} '
        'GROUP BY t."StockId", b ORDER BY t."StockId", b;'
    )
    out = subprocess.run(["docker", "exec", "-i", PG, "psql", "-U", "kse", "-d", db, "--csv", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit(f"psql failed: {out.stderr.strip()}")
    s = defaultdict(list)
    for ln in out.stdout.splitlines()[1:]:
        p = ln.split(",")
        if len(p) < 7:
            continue
        s[int(p[0])].append((float(p[1]), float(p[2]), float(p[3]), float(p[4]), float(p[5]), float(p[6])))
    return s

def pick(series, n_per_class=1):
    by = defaultdict(list)
    for sid, pts in series.items():
        by[stock_class(sid)].append((len(pts), sid))
    chosen = []
    for cls in ("Calm", "Normal", "Volatile", "Meme"):
        for _, sid in sorted(by.get(cls, []), reverse=True)[:n_per_class]:
            chosen.append(sid)
    return chosen

def make_continuous(candles):
    # Continuous candlesticks: each bar's OPEN is the previous bar's CLOSE (no artificial intrabar gaps
    # for a continuously-traded asset). High/low are widened to include the carried-forward open so the
    # wick still spans the true range. First bar keeps its own first-trade open.
    out = []
    prev_close = None
    for (t, o, h, l, c, v) in candles:
        op = prev_close if prev_close is not None else o
        out.append((t, op, max(h, op), min(l, op), c, v))
        prev_close = c
    return out

def draw(ax, candles, title):
    for i, (_t, o, h, l, c, _v) in enumerate(candles):
        up = c >= o
        color = "#26a69a" if up else "#ef5350"
        ax.plot([i, i], [l, h], color=color, linewidth=0.8, zorder=1)            # wick
        lo, hi = (o, c) if up else (c, o)
        ax.add_patch(plt.Rectangle((i - 0.3, lo), 0.6, max(hi - lo, (h - l) * 1e-3 or 1e-9),
                                   facecolor=color, edgecolor=color, zorder=2))   # body
    ax.set_title(title, fontsize=10)
    ax.margins(x=0.01)
    ax.grid(True, alpha=0.15)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="kse_soak")
    ap.add_argument("--bucket-sec", type=int, default=60)
    ap.add_argument("--window-min", type=float, default=20.0)
    ap.add_argument("--stocks", default="")
    ap.add_argument("--out", default="logs/candles.png")
    ap.add_argument("--title", default="")
    args = ap.parse_args()

    since = datetime.now(timezone.utc).timestamp() - args.window_min * 60
    series = load(args.db, since, args.bucket_sec)
    if not series:
        sys.exit("no candles in window")

    stocks = ([int(x) for x in args.stocks.split(",") if x.strip()] if args.stocks else pick(series))
    stocks = [s for s in stocks if series.get(s)]
    if not stocks:
        sys.exit("no traded stocks")

    n = len(stocks); cols = 2; rows = (n + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(13, 3.3 * rows), squeeze=False)
    for i, sid in enumerate(stocks):
        draw(axes[i // cols][i % cols], make_continuous(series[sid]), f"stock {sid} [{stock_class(sid)}]  ({len(series[sid])} bars)")
    for j in range(n, rows * cols):
        axes[j // cols][j % cols].axis("off")
    sup = f"Candles — {args.title}" if args.title else "Candles"
    fig.suptitle(f"{sup}  ({args.window_min:.0f}m, {args.bucket_sec}s bars)", fontsize=12)
    fig.tight_layout(rect=[0, 0, 1, 0.97])
    out = (ROOT / args.out) if not Path(args.out).is_absolute() else Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out, dpi=110)
    print(f"wrote {out}")

if __name__ == "__main__":
    main()
