-- Per-stock price deviation vs seed for the sentiment-test stocks (1,2,3 pinned; 4 = unpinned control).
WITH lt AS (
  SELECT DISTINCT ON ("StockId") "StockId", "Price"
  FROM "Transactions" WHERE "StockId" IN (1,2,3,4)
  ORDER BY "StockId", "Timestamp" DESC, "TransactionId" DESC
)
SELECT lt."StockId",
       round(((lt."Price" - sl."SeedPrice") / sl."SeedPrice" * 100)::numeric, 2) AS dev_pct
FROM lt JOIN "StockListings" sl ON sl."StockId" = lt."StockId" AND sl."IsPrimary" = true
ORDER BY lt."StockId";
