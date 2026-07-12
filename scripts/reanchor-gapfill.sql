-- §reseed gap-fill: synthetic flat candles over the reseed downtime hole (Kiesh-approved policy).
-- Run AFTER the post-reseed server has produced its first candles. For every (StockId, Currency)
-- 1-min listing, fills each missing bucket between the pre-cutover last candle and the first
-- post-restart candle with a flat bar at the prior close (O=H=L=C=last close, Volume 0, TradeCount 0)
-- — the honest "market closed" marker, same flat-fill convention as the Wave-8 gap-rebuild.
-- Idempotent: ON CONFLICT DO NOTHING against the unique (StockId,Currency,BucketSeconds,OpenTime) key.
-- 1-minute candles only; higher timeframes aggregate from 1m via the existing cascade/rebuild.

WITH bounds AS (
    SELECT "StockId", "Currency",
           max("OpenTime") FILTER (WHERE "Volume" > 0) AS last_real
    FROM "Candles" WHERE "BucketSeconds" = 60
    GROUP BY 1, 2
),
holes AS (
    SELECT c."StockId", c."Currency", c."OpenTime" AS pre_open, c."Close" AS pre_close,
           lead(c."OpenTime") OVER (PARTITION BY c."StockId", c."Currency" ORDER BY c."OpenTime") AS next_open
    FROM "Candles" c WHERE c."BucketSeconds" = 60
),
gaps AS (
    SELECT "StockId", "Currency", pre_close,
           generate_series(pre_open + interval '60 seconds',
                           next_open - interval '60 seconds',
                           interval '60 seconds') AS bucket
    FROM holes
    WHERE next_open IS NOT NULL AND next_open > pre_open + interval '60 seconds'
      -- only bridge holes that span the reseed cutover (parameterize :cutover, e.g. '2026-07-12T10:16:00Z')
      AND pre_open < :'cutover' AND next_open > :'cutover'
)
INSERT INTO "Candles" ("StockId", "Currency", "BucketSeconds", "OpenTime",
                       "Open", "High", "Low", "Close", "Volume", "TradeCount")
SELECT "StockId", "Currency", 60, bucket, pre_close, pre_close, pre_close, pre_close, 0, 0
FROM gaps
ON CONFLICT ("StockId", "Currency", "BucketSeconds", "OpenTime") DO NOTHING;
