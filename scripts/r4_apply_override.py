"""Apply Bots:* override(s) to appsettings.json, UTF-8 safe.

Usage:
    python scripts/r4_apply_override.py KEY=VAL [KEY=VAL ...]

Keys use dotted notation, e.g. Imbalance.Herding=true or
SentimentDynamics.MomentumConviction=0.15

Values are parsed as bool/int/float/string in that order.
"""
import json, sys
from pathlib import Path

PATH = Path(__file__).resolve().parent.parent / "KieshStockExchange.Server" / "appsettings.json"


def parse_value(raw):
    if raw.lower() == "true": return True
    if raw.lower() == "false": return False
    try: return int(raw)
    except ValueError: pass
    try: return float(raw)
    except ValueError: pass
    return raw


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: r4_apply_override.py KEY=VAL [KEY=VAL ...]")

    with open(PATH, encoding="utf-8") as f:
        data = json.load(f)

    if "Bots" not in data:
        sys.exit("Bots section missing")

    bots = data["Bots"]
    for arg in sys.argv[1:]:
        key, _, raw = arg.partition("=")
        if not key or not raw:
            sys.exit(f"bad arg: {arg!r}")
        parts = key.split(".")
        node = bots
        for p in parts[:-1]:
            node = node.setdefault(p, {})
        node[parts[-1]] = parse_value(raw)
        print(f"applied  Bots.{key} = {node[parts[-1]]!r}")

    with open(PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    print(f"wrote {PATH}")


if __name__ == "__main__":
    main()
