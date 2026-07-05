# Side-by-side candlestick comparison of two soak candle CSVs (A vs B) on the SAME stocks, so an
# A/B difference (e.g. bounce-mid smoothing, or desync) is eyeballable. Reuses candle_plot's CSV
# pipeline + candlestick renderer. Left column = A, right = B; one row per stock.
#
# Usage:
#   python scripts/candle_compare.py --a data/soaks/candles-X.csv --b data/soaks/candles-Y.csv \
#          --labels "OFF|ON" --stocks 1,3,4 --window-min 40 --bucket-sec 60 --out logs/cmp.png --title "..."
import argparse, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from candle_plot import load_csv, aggregate, window_tail, make_continuous, draw, stock_class, pick

ap = argparse.ArgumentParser()
ap.add_argument("--a", required=True)
ap.add_argument("--b", required=True)
ap.add_argument("--labels", default="A|B", help="'Aname|Bname'")
ap.add_argument("--stocks", default="")
ap.add_argument("--bucket-sec", type=int, default=60)
ap.add_argument("--window-min", type=float, default=40.0)
ap.add_argument("--out", default="logs/candle_compare.png")
ap.add_argument("--title", default="")
args = ap.parse_args()

la, lb = (args.labels.split("|") + ["A", "B"])[:2]
A = {s: b for s, b in window_tail(aggregate(load_csv(args.a), args.bucket_sec), args.window_min).items() if b}
B = {s: b for s, b in window_tail(aggregate(load_csv(args.b), args.bucket_sec), args.window_min).items() if b}

if args.stocks:
    stocks = [int(x) for x in args.stocks.split(",") if x.strip()]
else:
    stocks = pick({s: A[s] for s in A if s in B})   # one per class, present in both
stocks = [s for s in stocks if A.get(s) and B.get(s)]
if not stocks:
    sys.exit("no common traded stocks in window")

rows = len(stocks)
fig, axes = plt.subplots(rows, 2, figsize=(13, 3.2 * rows), squeeze=False)
for i, sid in enumerate(stocks):
    # share the y-range across the pair so texture, not level, is what differs visually
    draw(axes[i][0], make_continuous(A[sid]), f"stock {sid} [{stock_class(sid)}] - {la}")
    draw(axes[i][1], make_continuous(B[sid]), f"stock {sid} [{stock_class(sid)}] - {lb}")
sup = f"Candle compare - {args.title}" if args.title else "Candle compare"
fig.suptitle(f"{sup}   (left={la}  right={lb};  last {args.window_min:.0f}m, {args.bucket_sec}s bars)", fontsize=12)
fig.tight_layout(rect=[0, 0, 1, 0.97])
out = Path(args.out)
out.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(out, dpi=110)
print(f"wrote {out}")
