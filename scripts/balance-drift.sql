-- Price drift vs seed, off the most recent trade per (stock, currency).
-- Output: stocks,avg_pct,stddev_pct,medianAbs_pct,min_pct,max_pct,beyond50,beyond100,trades
-- medianAbs + beyond50/100 are tail-robust; avg/stddev/max are dominated by single runaway stocks.
WITH lasttx AS (
  SELECT DISTINCT ON (t."StockId", t."Currency")
         t."StockId", t."Currency", t."Price"
  FROM "Transactions" t
  ORDER BY t."StockId", t."Currency", t."Timestamp" DESC, t."TransactionId" DESC
),
d AS (
  SELECT (lt."Price" - sl."SeedPrice") / sl."SeedPrice" AS drift
  FROM lasttx lt
  JOIN "StockListings" sl
    ON sl."StockId" = lt."StockId" AND sl."Currency" = lt."Currency"
)
SELECT
  count(*)::text || ',' ||
  COALESCE(round(avg(drift)        * 100, 2)::text, 'NA') || ',' ||
  COALESCE(round(stddev_pop(drift) * 100, 2)::text, 'NA') || ',' ||
  COALESCE(round((percentile_cont(0.5) WITHIN GROUP (ORDER BY abs(drift)))::numeric * 100, 2)::text, 'NA') || ',' ||
  COALESCE(round(min(drift)        * 100, 2)::text, 'NA') || ',' ||
  COALESCE(round(max(drift)        * 100, 2)::text, 'NA') || ',' ||
  count(*) FILTER (WHERE abs(drift) >= 0.5)::text  || ',' ||
  count(*) FILTER (WHERE abs(drift) >= 1.0)::text  || ',' ||
  (SELECT count(*) FROM "Transactions")::text
FROM d;
