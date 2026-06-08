-- Resting-book depth snapshot. Output: openOrders,restQty,within1pct,d1to5pct,d5to20pct,beyond20pct
WITH lasttx AS (
  SELECT DISTINCT ON ("StockId","Currency") "StockId","Currency","Price" AS last
  FROM "Transactions" ORDER BY "StockId","Currency","Timestamp" DESC
),
oo AS (
  SELECT (o."Quantity" - o."AmountFilled") AS rem,
         abs(o."Price" - lt.last) / lt.last AS dist
  FROM "Orders" o
  JOIN lasttx lt ON lt."StockId" = o."StockId" AND lt."Currency" = o."Currency"
  WHERE o."Status" = 'Open' AND o."Entry" = 'Limit'
)
SELECT
  count(*)::text || ',' ||
  COALESCE(sum(rem), 0)::text || ',' ||
  count(*) FILTER (WHERE dist <= 0.01)::text || ',' ||
  count(*) FILTER (WHERE dist > 0.01 AND dist <= 0.05)::text || ',' ||
  count(*) FILTER (WHERE dist > 0.05 AND dist <= 0.20)::text || ',' ||
  count(*) FILTER (WHERE dist > 0.20)::text
FROM oo;
