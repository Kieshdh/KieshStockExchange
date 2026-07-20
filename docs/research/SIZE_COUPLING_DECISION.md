# Size Coupling Prototype — Decision Doc (2026-07-13, for Kiesh)

**VERDICT: NO-BAKE.** Built + validated as safe, but it does not achieve its purpose and carries a cost.
Stays committed default-off (`02fbd7e`, feature branch, 550 tests) as a documented tool / dead-end.
NOT merged to master. Prod untouched.

## What it is
`Bots:Activity:Composition:SizeExp` — order notional × clamp(act^SizeExp, 1/SizeCap, SizeCap). The council's
one bounded prototype to close the volume-CV gap that composition (taker-share) couldn't: the load-scaler
pins order COUNT and taker-share changes MIX, so per-order SIZE was the hypothesized lever for volume CV
(0.17 shipped vs real ~1.5-2).

## The result (45m screen, SizeExp 1.0 vs off, both composition-on)
| stat | control | SizeExp 1.0 | read |
|---|---|---|---|
| per-min volume MEAN | 40,508 | 57,754 | +43% — raises LEVEL |
| **per-min volume CV** | **0.162** | **0.178** | **~flat — the PURPOSE fails** |
| vol autocorr L1 | +0.24 | +0.45 | modest clustering gain |
| vol~\|ret\| | +0.18 | +0.24 | small gain |
| ret_acf VWAP | +0.054 | **+0.101** | **WORSE — bigger orders on hot names add momentum** |
| kurtosis | 2.11 | 1.58 | worse (thinner tails) |
| CK / band-punch / cap | 0 / 0 / fine | 0 / 0 / fine | SAFE (WSelf 0 severs the runaway loop, as predicted) |

## Why it fails (the structural finding — the valuable part)
Volume CV is the minute-to-minute variance of TOTAL volume. With ~5,000 orders/minute, total ≈
N_orders × mean_size. N is scaler-pinned (low relative variance) and a **bounded, median-1 size
multiplier averages out across thousands of orders (LLN)** — so the minute-level CV barely moves even
at the strong SizeExp 1.0. Raising SizeExp/SizeCap further just shifts the mean, not the variance.

**Real volume CV comes from FAT-TAILED order sizes — occasional whales/block trades that dominate a
minute — not a smooth activity multiplier.** That's a different mechanism (the sim already has a
block-trade lever in the fat-tails code, `_blockTradeProb`/`_blockTradeMultiple`). And per the council's
Contrarian, minute-volume CV is a stat a chart-watching human barely perceives — the visible clustering
(bursts) is already delivered by the shipped composition coupling (vol-autocorr, taker-share bursts).

## Recommendation
Do NOT pursue the CV gap via size coupling. Two honest options if CV is ever wanted:
1. The existing **block-trade / fat-tail size** lever (whales) — the mechanism that actually moves CV — but
   gate it on "does a human notice / want bigger occasional prints," not the CV number.
2. Accept the CV gap: composition already gives the *visible* clustering; CV is low-perceptibility.

Size coupling stays default-off. This closes the composition/volume-clustering arc: the shipped features
(composition + wick) are the win; size coupling is the documented dead-end that proves the CV gap is
structural, not a tuning miss.
