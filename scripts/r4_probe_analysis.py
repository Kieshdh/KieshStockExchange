"""R4 §0009 Stage 1 probe analysis. Reads logs/match-symmetry-probe.csv and reports
the per-(surface, side, context) distribution stats needed to decide Stage 2 surface."""
import csv, sys, statistics
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CSV = ROOT / "KieshStockExchange.Server/logs/match-symmetry-probe.csv"

groups = defaultdict(list)
with open(CSV, newline="") as f:
    r = csv.DictReader(f)
    for row in r:
        key = (row["surface"], row["side"], row["context"])
        groups[key].append(float(row["value"]))

print(f"{'group':<40} {'count':>8} {'mean':>10} {'median':>10} {'p10':>10} {'p90':>10} {'stdev':>10}")
print("-" * 100)
for k, vals in sorted(groups.items(), key=lambda kv: -len(kv[1])):
    label = "/".join(k)
    n = len(vals)
    mean = statistics.mean(vals)
    med = statistics.median(vals)
    vals_s = sorted(vals)
    p10 = vals_s[int(n*0.10)]
    p90 = vals_s[int(n*0.90)]
    sd = statistics.pstdev(vals) if n > 1 else 0.0
    print(f"{label:<40} {n:>8} {mean:>10.2f} {med:>10.2f} {p10:>10.2f} {p90:>10.2f} {sd:>10.2f}")

print()
print("=== ASYMMETRY SUMMARY ===")
m_buy = groups[("matcher", "buy", "fill_vs_limit")]
m_sell = groups[("matcher", "sell", "fill_vs_limit")]
print(f"Matcher fill count: sell={len(m_sell):>6} vs buy={len(m_buy):>6} "
      f"(sell/buy = {len(m_sell)/len(m_buy):.2f}x)")
print(f"Matcher |mean residual bps|: sell={abs(statistics.mean(m_sell)):.2f}  buy={abs(statistics.mean(m_buy)):.2f}  "
      f"(buy improvement is {abs(statistics.mean(m_buy))/abs(statistics.mean(m_sell)):.2f}x sell)")

s_short = groups[("settler", "sell", "short_open")]
s_flip = groups[("settler", "sell", "flip")]
s_long = groups[("settler", "sell", "long_close")]
total_sell = len(s_short) + len(s_flip) + len(s_long)
print(f"Settler sell mix: short_open={len(s_short)} ({len(s_short)/total_sell*100:.1f}%) "
      f"flip={len(s_flip)} ({len(s_flip)/total_sell*100:.1f}%) "
      f"long_close={len(s_long)} ({len(s_long)/total_sell*100:.1f}%)")
print(f"Settler sell mean price: short_open={statistics.mean(s_short):.2f}  "
      f"flip={statistics.mean(s_flip):.2f}  long_close={statistics.mean(s_long):.2f}")

print()
print("=== STAGE 2 GATE ===")
print(f"Per S0009 acceptance: Stage 2 fires when one surface accounts for >=40% of the residual asymmetry.")
print(f"Total sell-side fill count premium over buy: {len(m_sell)-len(m_buy)} extra sells ({(len(m_sell)-len(m_buy))/len(m_buy)*100:.1f}%)")
print(f"Bid-side fill-quality gap: buy takers save {abs(statistics.mean(m_buy))-abs(statistics.mean(m_sell)):.2f} bps more than sell takers gain")
