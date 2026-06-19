# Exogenous-shock diagnostic (§exogenous-information). Reads the durable per-stock shock telemetry
# (data/telemetry/bot_exog_shock.ndjson, one JSON row per stock per ~minute: TimestampUtc, StockId, Shock,
# ShockId, Active) and reports whether the shock process is firing at the intended rate/magnitude:
#   - duty cycle      = fraction of samples with Active == true (target >= ~0.60)
#   - arrivals/hour   = ShockId increments per stock over the window (genuine new-from-rest impulses)
#   - max|shock|      = peak magnitude seen (must be <= Cap)
#   - mean|shock|     = average magnitude over active samples
# This is the 5-minute pre-flight (and post-soak) check that catches a dead/misconfigured ON arm before a
# 90-min soak is spent — the realism scorers read only realized price (Transactions) and cannot see the shock.
import argparse, json, os, sys
from collections import defaultdict

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", default="data/telemetry/bot_exog_shock.ndjson")
    ap.add_argument("--cap", type=float, default=0.06, help="configured Cap, for the max|shock| sanity check")
    ap.add_argument("--last-min", type=float, default=0.0,
                    help="only consider the last N minutes of samples (0 = all)")
    args = ap.parse_args()

    if not os.path.exists(args.file):
        print(f"NO TELEMETRY: {args.file} not found. Is ExogShock:Enabled true and the server running?",
              file=sys.stderr)
        sys.exit(2)

    rows = []
    with open(args.file, "r") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    if not rows:
        print("NO SAMPLES parsed from telemetry.", file=sys.stderr)
        sys.exit(2)

    # Optional time window (timestamps are ISO-8601; lexical compare is monotonic for same offset).
    if args.last_min > 0:
        ts = sorted(r.get("TimestampUtc", "") for r in rows)
        # keep roughly the last fraction by count as a cheap proxy when timestamps aren't parsed
        cutoff_idx = 0  # fall through to all if we can't parse times
        try:
            from datetime import datetime, timedelta, timezone
            def parse(t): return datetime.fromisoformat(t.replace("Z", "+00:00"))
            tmax = max(parse(r["TimestampUtc"]) for r in rows)
            lo = tmax - timedelta(minutes=args.last_min)
            rows = [r for r in rows if parse(r["TimestampUtc"]) >= lo]
        except Exception:
            pass

    total = len(rows)
    active = sum(1 for r in rows if r.get("Active"))
    duty = active / total if total else 0.0

    max_abs = 0.0
    sum_abs_active = 0.0
    n_active = 0
    last_id = {}
    arrivals = defaultdict(int)
    samples_per_stock = defaultdict(int)
    for r in rows:
        sid = r.get("StockId")
        shock = abs(float(r.get("Shock", 0.0)))
        sid_id = int(r.get("ShockId", 0))
        samples_per_stock[sid] += 1
        if shock > max_abs:
            max_abs = shock
        if r.get("Active"):
            sum_abs_active += shock
            n_active += 1
        if sid in last_id and sid_id > last_id[sid]:
            arrivals[sid] += sid_id - last_id[sid]
        last_id[sid] = sid_id

    # Estimate arrivals/hour using the per-stock sample span (samples are ~1/min).
    total_arrivals = sum(arrivals.values())
    stock_count = len(samples_per_stock)
    span_min = max(samples_per_stock.values()) if samples_per_stock else 0  # ~minutes
    arr_per_hr = (total_arrivals / stock_count) / (span_min / 60.0) if stock_count and span_min else 0.0

    print(f"samples={total} stocks={stock_count} span~={span_min}min")
    print(f"duty_cycle      = {duty:.3f}   (target >= ~0.60 for ret_acf to move)")
    print(f"arrivals/hr/stk = {arr_per_hr:.2f}")
    print(f"max|shock|      = {max_abs:.4f}   (Cap = {args.cap:.4f})")
    print(f"mean|shock|act  = {(sum_abs_active / n_active) if n_active else 0.0:.4f}")

    ok = True
    if total_arrivals == 0:
        print("FAIL: zero shock arrivals — source not firing (check Enabled / MeanIntervalMinutes).")
        ok = False
    if max_abs > args.cap + 1e-6:
        print(f"FAIL: max|shock| {max_abs:.4f} exceeds Cap {args.cap:.4f} — accumulator clamp broken.")
        ok = False
    if duty < 0.30:
        print(f"WARN: duty cycle {duty:.3f} is low — raise MeanInterval/HalfLife toward a >=0.60 duty.")
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
