-- RE-ANCHOR RESEED (candle-preserving): run with the SERVER STOPPED.
-- Keeps Stocks / StockListings / Candles (chart continuity); re-anchors each listing's SeedPrice
-- (= the value anchor + bank-estimate baseline + bot fundamental) to its LAST candle close so the
-- market re-opens exactly where the chart left off; wipes every population-scoped table so the
-- admin subset-seed endpoints (users / ai-profiles / holdings) can repopulate WITHOUT touching
-- market data. StockPrices is re-anchored too (it is the price-lookup fallback once Transactions
-- are wiped — leaving the original seed rows would serve pre-reseed prices).
BEGIN;

-- 1) Last close per (stock, currency) from the finest retained resolution (fine tier <= 300s, 90d).
CREATE TEMP TABLE last_close ON COMMIT DROP AS
SELECT DISTINCT ON ("StockId", "Currency")
       "StockId", "Currency", "Close" AS px, "OpenTime"
FROM "Candles"
WHERE "BucketSeconds" <= 300 AND "Close" > 0
ORDER BY "StockId", "Currency", "OpenTime" DESC;

-- Sanity: every listing must have an anchor row (abort => fix candles first, do not half-anchor).
DO $$
DECLARE missing int;
BEGIN
  SELECT count(*) INTO missing
  FROM "StockListings" sl LEFT JOIN last_close lc
    ON lc."StockId" = sl."StockId" AND lc."Currency" = sl."Currency"
  WHERE lc."StockId" IS NULL;
  IF missing > 0 THEN
    RAISE EXCEPTION 're-anchor abort: % listing(s) have no candle close to anchor to', missing;
  END IF;
END $$;

-- 2) Re-anchor the listings + the StockPrices fallback.
UPDATE "StockListings" sl
SET "SeedPrice" = lc.px
FROM last_close lc
WHERE lc."StockId" = sl."StockId" AND lc."Currency" = sl."Currency";

UPDATE "StockPrices" sp
SET "Price" = lc.px, "Timestamp" = now() AT TIME ZONE 'utc'
FROM last_close lc
WHERE lc."StockId" = sp."StockId" AND lc."Currency" = sp."Currency";

-- 3) Wipe ONLY the population tables no seed step resets itself. Users/AIUsers/Funds/Positions/
--    Orders/Transactions are truncated (RESTART IDENTITY) by the subset-seed steps — and the OLD
--    admin row must SURVIVE this script so it can authorize those endpoints (the users step then
--    replaces it; the already-issued JWT stays valid statelessly for the remaining steps).
TRUNCATE "FundTransactions", "Messages", "UserPreferences", "UserWatchlist"
RESTART IDENTITY CASCADE;

-- 4) Report the anchor set.
SELECT sl."StockId", sl."Currency", sl."SeedPrice" AS anchored_seed, lc."OpenTime" AS from_candle
FROM "StockListings" sl JOIN last_close lc
  ON lc."StockId" = sl."StockId" AND lc."Currency" = sl."Currency"
ORDER BY sl."StockId", sl."Currency" LIMIT 10;

COMMIT;
