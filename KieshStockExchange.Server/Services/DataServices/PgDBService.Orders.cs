using System.Text;
using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string OrderCols = @"
        ""OrderId"",""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",""StopPrice"",
        ""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt""";

    private const string TransactionCols = @"
        ""TransactionId"",""StockId"",""BuyOrderId"",""SellOrderId"",""BuyerId"",""SellerId"",
        ""Quantity"",""Price"",""Currency"",""Timestamp""";

    #region Order operations
    public async Task<List<Order>> GetOrdersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<OrderRow>($@"SELECT {OrderCols} FROM ""Orders""");
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<(List<Order> Items, int Total)> GetOrdersPageAsync(
        int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc,
        string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null,
        string? sideFilter = null, string? typeFilter = null,
        IList<int>? excludeUserIds = null, CancellationToken ct = default)
    {
        (skip, take) = ClampPage(skip, take);
        await using var c = await OpenAsync(ct);

        var clauses = new List<string> { @"""CreatedAt"" >= @fromUtc", @"""CreatedAt"" <= @toUtc" };
        var dp = new DynamicParameters();
        dp.Add("fromUtc", fromUtc);
        dp.Add("toUtc", toUtc);

        if (excludeUserIds is { Count: > 0 })
        {
            clauses.Add(@"NOT (""UserId"" = ANY(@excl))");
            dp.Add("excl", excludeUserIds.ToArray());
        }
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            clauses.Add(@"""Status"" = @status");
            dp.Add("status", statusFilter);
        }
        if (userIdFilter is int uid) { clauses.Add(@"""UserId"" = @uid"); dp.Add("uid", uid); }
        if (stockIdFilter is int sid) { clauses.Add(@"""StockId"" = @sid"); dp.Add("sid", sid); }

        if (!string.IsNullOrWhiteSpace(sideFilter))
        {
            if (string.Equals(sideFilter, "Buy", StringComparison.OrdinalIgnoreCase))
            {
                clauses.Add(@"""OrderType"" = ANY(@buyTypes)");
                dp.Add("buyTypes", new[] { Order.Types.LimitBuy, Order.Types.TrueMarketBuy, Order.Types.SlippageMarketBuy });
            }
            else if (string.Equals(sideFilter, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                clauses.Add(@"""OrderType"" = ANY(@sellTypes)");
                dp.Add("sellTypes", new[] { Order.Types.LimitSell, Order.Types.TrueMarketSell, Order.Types.SlippageMarketSell });
            }
        }
        if (!string.IsNullOrWhiteSpace(typeFilter))
        {
            if (string.Equals(typeFilter, "Limit", StringComparison.OrdinalIgnoreCase))
            {
                clauses.Add(@"""OrderType"" = ANY(@limitTypes)");
                dp.Add("limitTypes", new[] { Order.Types.LimitBuy, Order.Types.LimitSell });
            }
            else if (string.Equals(typeFilter, "Market", StringComparison.OrdinalIgnoreCase))
            {
                clauses.Add(@"""OrderType"" = ANY(@marketTypes)");
                dp.Add("marketTypes", new[] {
                    Order.Types.TrueMarketBuy, Order.Types.TrueMarketSell,
                    Order.Types.SlippageMarketBuy, Order.Types.SlippageMarketSell });
            }
        }

        var where = "WHERE " + string.Join(" AND ", clauses);
        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Orders"" {where}", dp);

        var orderCol = sortKey switch
        {
            "OrderId"  => "\"OrderId\"",
            "UserId"   => "\"UserId\"",
            "StockId"  => "\"StockId\"",
            "Quantity" => "\"Quantity\"",
            _          => "\"CreatedAt\"",
        };
        var dir = desc ? "DESC" : "ASC";

        dp.Add("skip", skip);
        dp.Add("take", take);
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders"" {where}
            ORDER BY {orderCol} {dir}
            OFFSET @skip LIMIT @take", dp);
        return (rows.Select(OrderMapper.ToDomain).ToList(), total);
    }

    public async Task<Order?> GetOrderById(int orderId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<OrderRow>(
            $@"SELECT {OrderCols} FROM ""Orders"" WHERE ""OrderId"" = @orderId",
            new { orderId });
        return row is null ? null : OrderMapper.ToDomain(row);
    }

    public async Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default)
    {
        if (orderIds is null || orderIds.Count == 0) return new List<Order>();
        await using var c = await OpenAsync(ct);
        var ids = orderIds.Distinct().ToArray();
        var rows = await c.QueryAsync<OrderRow>(
            $@"SELECT {OrderCols} FROM ""Orders"" WHERE ""OrderId"" = ANY(@ids)", new { ids });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<OrderRow>(
            $@"SELECT {OrderCols} FROM ""Orders"" WHERE ""UserId"" = @userId", new { userId });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<OrderRow>(
            $@"SELECT {OrderCols} FROM ""Orders"" WHERE ""StockId"" = @stockId", new { stockId });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders""
            WHERE ""StockId"" = @stockId AND ""Currency"" = @currency AND ""Status"" = @open
              AND ""OrderType"" = ANY(@limitTypes)
            ORDER BY ""CreatedAt""",
            new
            {
                stockId,
                currency = currency.ToString(),
                open = Order.Statuses.Open,
                limitTypes = new[] { Order.Types.LimitBuy, Order.Types.LimitSell },
            });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Order>();
        await using var c = await OpenAsync(ct);
        // Open limit orders hold a reservation on the book; armed (Pending) stops hold a
        // reservation off-book (shares for a sell-stop, cash for a buy-stop). Both must come
        // back so AccountsCache re-seeds their reservations on cold-load. (The book itself
        // still loads only Open limit orders via GetOpenLimitOrders — Pending never enters it.)
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders""
            WHERE ""UserId"" = ANY(@ids)
              AND ( (""Status"" = @open AND ""OrderType"" = ANY(@limitTypes))
                 OR (""Status"" = @pending AND ""OrderType"" = ANY(@stopTypes)) )",
            new
            {
                ids = userIds.Distinct().ToArray(),
                open = Order.Statuses.Open,
                pending = Order.Statuses.Pending,
                limitTypes = new[] { Order.Types.LimitBuy, Order.Types.LimitSell },
                stopTypes = new[] { Order.Types.StopMarketBuy, Order.Types.StopMarketSell,
                                    Order.Types.StopLimitBuy, Order.Types.StopLimitSell },
            });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    // §3.6 P2: all armed stops across every user, for the StopTriggerWatcher's cold-load
    // index rebuild on server start.
    public async Task<List<Order>> GetAllArmedStopsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders"" WHERE ""Status"" = @pending AND ""OrderType"" = ANY(@stopTypes)",
            new
            {
                pending = Order.Statuses.Pending,
                stopTypes = new[] { Order.Types.StopMarketBuy, Order.Types.StopMarketSell,
                                    Order.Types.StopLimitBuy, Order.Types.StopLimitSell },
            });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task CreateOrder(Order order, CancellationToken ct = default)
    {
        if (!order.IsValid()) throw new ArgumentException("Order entity is not valid", nameof(order));
        await using var c = await OpenAsync(ct);
        var row = OrderMapper.ToRow(order);
        row.OrderId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Orders"" (""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",""StopPrice"",
                                   ""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@StockId,@Quantity,@Price,@SlippagePercent,@BuyBudget,@StopPrice,
                    @Currency,@OrderType,@Status,@AmountFilled,@CreatedAt,@UpdatedAt)
            RETURNING ""OrderId""", row);
        order.OrderId = row.OrderId;
    }

    // No IsValid() — engine-driven updates legitimately produce states the
    // single-shot domain check would reject (e.g. BuyBudget = 0 after a
    // TrueMarketBuy fully fills). CreateOrder still validates at the entry
    // point. Matches OLD DBService.UpdateAllAsync's row-bulk semantics.
    public async Task UpdateOrder(Order order, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Orders"" SET
              ""UserId"" = @UserId, ""StockId"" = @StockId, ""Quantity"" = @Quantity, ""Price"" = @Price,
              ""SlippagePercent"" = @SlippagePercent, ""BuyBudget"" = @BuyBudget, ""StopPrice"" = @StopPrice,
              ""Currency"" = @Currency, ""OrderType"" = @OrderType, ""Status"" = @Status, ""AmountFilled"" = @AmountFilled,
              ""CreatedAt"" = @CreatedAt, ""UpdatedAt"" = @UpdatedAt
            WHERE ""OrderId"" = @OrderId", OrderMapper.ToRow(order));
    }

    public async Task DeleteOrder(Order order, CancellationToken ct = default)
    {
        if (order.OrderId == 0)
            throw new ArgumentException("Order entity must have a valid OrderId", nameof(order));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Orders"" WHERE ""OrderId"" = @OrderId", new { order.OrderId });
    }
    #endregion

    #region Transaction operations
    public async Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<TransactionRow>($@"SELECT {TransactionCols} FROM ""Transactions""");
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(
        int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc,
        int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null,
        IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default)
    {
        (skip, take) = ClampPage(skip, take);
        await using var c = await OpenAsync(ct);
        var clauses = new List<string> { @"""Timestamp"" >= @fromUtc", @"""Timestamp"" <= @toUtc" };
        var dp = new DynamicParameters();
        dp.Add("fromUtc", fromUtc);
        dp.Add("toUtc", toUtc);

        if (excludeBuyerOrSellerIds is { Count: > 0 })
        {
            clauses.Add(@"NOT (""BuyerId"" = ANY(@excl)) AND NOT (""SellerId"" = ANY(@excl))");
            dp.Add("excl", excludeBuyerOrSellerIds.ToArray());
        }
        if (userIdFilter is int uid)
        {
            clauses.Add(@"(""BuyerId"" = @uid OR ""SellerId"" = @uid)");
            dp.Add("uid", uid);
        }
        if (stockIdFilter is int sid) { clauses.Add(@"""StockId"" = @sid"); dp.Add("sid", sid); }
        if (!string.IsNullOrWhiteSpace(currencyFilter))
        {
            clauses.Add(@"""Currency"" = @cf");
            dp.Add("cf", currencyFilter.ToUpperInvariant());
        }

        var where = "WHERE " + string.Join(" AND ", clauses);
        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Transactions"" {where}", dp);

        var orderCol = sortKey switch
        {
            "TransactionId" => "\"TransactionId\"",
            "StockId"       => "\"StockId\"",
            "Quantity"      => "\"Quantity\"",
            "Price"         => "\"Price\"",
            _               => "\"Timestamp\"",
        };
        var dir = desc ? "DESC" : "ASC";

        dp.Add("skip", skip);
        dp.Add("take", take);
        var rows = await c.QueryAsync<TransactionRow>($@"
            SELECT {TransactionCols} FROM ""Transactions"" {where}
            ORDER BY {orderCol} {dir}
            OFFSET @skip LIMIT @take", dp);
        return (rows.Select(TransactionMapper.ToDomain).ToList(), total);
    }

    public async Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions"" WHERE ""TransactionId"" = @transactionId",
            new { transactionId });
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions"" WHERE ""BuyerId"" = @userId OR ""SellerId"" = @userId",
            new { userId });
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions""
               WHERE ""BuyOrderId"" = @orderId OR ""SellOrderId"" = @orderId
               ORDER BY ""Timestamp""",
            new { orderId });
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(
        int stockId, CurrencyType currency, DateTime from, DateTime to, int? maxRows = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        // maxRows caps the public endpoint — a wide window would otherwise stream the whole
        // tape into memory. Internal candle/backfill callers pass null for the full window.
        var cap = maxRows is int n && n > 0 ? $@" ORDER BY ""Timestamp"" DESC LIMIT {n}" : "";
        var rows = await c.QueryAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
                 AND ""Timestamp"" >= @from AND ""Timestamp"" < @to{cap}",
            new { stockId, currency = currency.ToString(), from, to });
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, int? limit = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var now = TimeHelper.NowUtc();
        var sql = limit is int n
            ? $@"SELECT {TransactionCols} FROM ""Transactions""
                 WHERE ""Timestamp"" >= @since AND ""Timestamp"" <= @now LIMIT {n}"
            : $@"SELECT {TransactionCols} FROM ""Transactions""
                 WHERE ""Timestamp"" >= @since AND ""Timestamp"" <= @now";
        var rows = await c.QueryAsync<TransactionRow>(sql, new { since, now });
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
               ORDER BY ""Timestamp"" DESC LIMIT 1",
            new { stockId, currency = currency.ToString() });
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency AND ""Timestamp"" <= @time
               ORDER BY ""Timestamp"" DESC LIMIT 1",
            new { stockId, currency = currency.ToString(), time });
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task CreateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        if (!transaction.IsValid()) throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await using var c = await OpenAsync(ct);
        var row = TransactionMapper.ToRow(transaction);
        row.TransactionId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Transactions"" (""StockId"",""BuyOrderId"",""SellOrderId"",""BuyerId"",""SellerId"",
                                          ""Quantity"",""Price"",""Currency"",""Timestamp"")
            VALUES (@StockId,@BuyOrderId,@SellOrderId,@BuyerId,@SellerId,@Quantity,@Price,@Currency,@Timestamp)
            RETURNING ""TransactionId""", row);
        transaction.TransactionId = row.TransactionId;
    }

    public async Task UpdateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        if (!transaction.IsValid()) throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Transactions"" SET
              ""StockId"" = @StockId, ""BuyOrderId"" = @BuyOrderId, ""SellOrderId"" = @SellOrderId,
              ""BuyerId"" = @BuyerId, ""SellerId"" = @SellerId, ""Quantity"" = @Quantity,
              ""Price"" = @Price, ""Currency"" = @Currency, ""Timestamp"" = @Timestamp
            WHERE ""TransactionId"" = @TransactionId", TransactionMapper.ToRow(transaction));
    }

    public async Task DeleteTransaction(Transaction transaction, CancellationToken ct = default)
    {
        if (transaction.TransactionId == 0)
            throw new ArgumentException("Transaction entity must have a valid TransactionId", nameof(transaction));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Transactions"" WHERE ""TransactionId"" = @TransactionId",
            new { transaction.TransactionId });
    }
    #endregion

    #region Batched hot-type writes (P1)
    // Multi-row INSERT/UPDATE for Order + Transaction. Same per-row SQL as
    // CreateOrder/UpdateOrder/CreateTransaction, just unrolled across all rows
    // in a single statement so the bot trade group's settle phase issues one
    // round-trip per call instead of N. Postgres preserves VALUES order in
    // RETURNING within a single statement, so PK writeback is index-aligned.
    private async Task InsertOrdersBatchAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        for (int i = 0; i < orders.Count; i++)
            if (!orders[i].IsValid())
                throw new ArgumentException("Order entity is not valid", nameof(orders));

        var sql = new StringBuilder(@"
            INSERT INTO ""Orders"" (""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",""StopPrice"",
                                   ""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt"")
            VALUES ", capacity: 256 + orders.Count * 96);
        var p = new DynamicParameters();
        for (int i = 0; i < orders.Count; i++)
        {
            if (i > 0) sql.Append(',');
            sql.Append("(@UserId_").Append(i)
               .Append(",@StockId_").Append(i)
               .Append(",@Quantity_").Append(i)
               .Append(",@Price_").Append(i)
               .Append(",@SlippagePercent_").Append(i)
               .Append(",@BuyBudget_").Append(i)
               .Append(",@StopPrice_").Append(i)
               .Append(",@Currency_").Append(i)
               .Append(",@OrderType_").Append(i)
               .Append(",@Status_").Append(i)
               .Append(",@AmountFilled_").Append(i)
               .Append(",@CreatedAt_").Append(i)
               .Append(",@UpdatedAt_").Append(i)
               .Append(')');

            var r = OrderMapper.ToRow(orders[i]);
            p.Add($"UserId_{i}",          r.UserId);
            p.Add($"StockId_{i}",         r.StockId);
            p.Add($"Quantity_{i}",        r.Quantity);
            p.Add($"Price_{i}",           r.Price);
            p.Add($"SlippagePercent_{i}", r.SlippagePercent);
            p.Add($"BuyBudget_{i}",       r.BuyBudget);
            p.Add($"StopPrice_{i}",       r.StopPrice);
            p.Add($"Currency_{i}",        r.Currency);
            p.Add($"OrderType_{i}",       r.OrderType);
            p.Add($"Status_{i}",          r.Status);
            p.Add($"AmountFilled_{i}",    r.AmountFilled);
            p.Add($"CreatedAt_{i}",       r.CreatedAt);
            p.Add($"UpdatedAt_{i}",       r.UpdatedAt);
        }
        sql.Append(@" RETURNING ""OrderId""");

        await using var c = await OpenAsync(ct);
        var ids = (await c.QueryAsync<int>(sql.ToString(), p)).ToList();
        if (ids.Count != orders.Count)
            throw new InvalidOperationException(
                $"InsertOrdersBatch: RETURNING produced {ids.Count} ids for {orders.Count} rows.");
        for (int i = 0; i < orders.Count; i++) orders[i].OrderId = ids[i];
    }

    // No IsValid() — same reason as per-row UpdateOrder (engine-driven
    // updates legitimately produce states the single-shot validators reject).
    private async Task UpdateOrdersBatchAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var sql = new StringBuilder(@"
            UPDATE ""Orders"" SET
              ""UserId"" = data.""UserId"", ""StockId"" = data.""StockId"", ""Quantity"" = data.""Quantity"",
              ""Price"" = data.""Price"", ""SlippagePercent"" = data.""SlippagePercent"", ""BuyBudget"" = data.""BuyBudget"",
              ""StopPrice"" = data.""StopPrice"", ""Currency"" = data.""Currency"", ""OrderType"" = data.""OrderType"",
              ""Status"" = data.""Status"", ""AmountFilled"" = data.""AmountFilled"", ""CreatedAt"" = data.""CreatedAt"",
              ""UpdatedAt"" = data.""UpdatedAt""
            FROM (VALUES ", capacity: 512 + orders.Count * 128);
        var p = new DynamicParameters();
        for (int i = 0; i < orders.Count; i++)
        {
            if (i > 0) sql.Append(',');
            // First row carries explicit casts; Postgres infers subsequent rows.
            if (i == 0)
                sql.Append("(@OrderId_0::int,@UserId_0::int,@StockId_0::int,@Quantity_0::int,@Price_0::numeric,@SlippagePercent_0::numeric,@BuyBudget_0::numeric,@StopPrice_0::numeric,@Currency_0::text,@OrderType_0::text,@Status_0::text,@AmountFilled_0::int,@CreatedAt_0::timestamptz,@UpdatedAt_0::timestamptz)");
            else
                sql.Append("(@OrderId_").Append(i)
                   .Append(",@UserId_").Append(i)
                   .Append(",@StockId_").Append(i)
                   .Append(",@Quantity_").Append(i)
                   .Append(",@Price_").Append(i)
                   .Append(",@SlippagePercent_").Append(i)
                   .Append(",@BuyBudget_").Append(i)
                   .Append(",@StopPrice_").Append(i)
                   .Append(",@Currency_").Append(i)
                   .Append(",@OrderType_").Append(i)
                   .Append(",@Status_").Append(i)
                   .Append(",@AmountFilled_").Append(i)
                   .Append(",@CreatedAt_").Append(i)
                   .Append(",@UpdatedAt_").Append(i)
                   .Append(')');

            var r = OrderMapper.ToRow(orders[i]);
            p.Add($"OrderId_{i}",         r.OrderId);
            p.Add($"UserId_{i}",          r.UserId);
            p.Add($"StockId_{i}",         r.StockId);
            p.Add($"Quantity_{i}",        r.Quantity);
            p.Add($"Price_{i}",           r.Price);
            p.Add($"SlippagePercent_{i}", r.SlippagePercent);
            p.Add($"BuyBudget_{i}",       r.BuyBudget);
            p.Add($"StopPrice_{i}",       r.StopPrice);
            p.Add($"Currency_{i}",        r.Currency);
            p.Add($"OrderType_{i}",       r.OrderType);
            p.Add($"Status_{i}",          r.Status);
            p.Add($"AmountFilled_{i}",    r.AmountFilled);
            p.Add($"CreatedAt_{i}",       r.CreatedAt);
            p.Add($"UpdatedAt_{i}",       r.UpdatedAt);
        }
        sql.Append(@") AS data(""OrderId"",""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",""StopPrice"",""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt"") WHERE ""Orders"".""OrderId"" = data.""OrderId""");

        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(sql.ToString(), p);
    }

    private async Task InsertTransactionsBatchAsync(IReadOnlyList<Transaction> txs, CancellationToken ct)
    {
        for (int i = 0; i < txs.Count; i++)
            if (!txs[i].IsValid())
                throw new ArgumentException("Transaction entity is not valid", nameof(txs));

        var sql = new StringBuilder(@"
            INSERT INTO ""Transactions"" (""StockId"",""BuyOrderId"",""SellOrderId"",""BuyerId"",""SellerId"",
                                          ""Quantity"",""Price"",""Currency"",""Timestamp"")
            VALUES ", capacity: 256 + txs.Count * 80);
        var p = new DynamicParameters();
        for (int i = 0; i < txs.Count; i++)
        {
            if (i > 0) sql.Append(',');
            sql.Append("(@StockId_").Append(i)
               .Append(",@BuyOrderId_").Append(i)
               .Append(",@SellOrderId_").Append(i)
               .Append(",@BuyerId_").Append(i)
               .Append(",@SellerId_").Append(i)
               .Append(",@Quantity_").Append(i)
               .Append(",@Price_").Append(i)
               .Append(",@Currency_").Append(i)
               .Append(",@Timestamp_").Append(i)
               .Append(')');

            var r = TransactionMapper.ToRow(txs[i]);
            p.Add($"StockId_{i}",     r.StockId);
            p.Add($"BuyOrderId_{i}",  r.BuyOrderId);
            p.Add($"SellOrderId_{i}", r.SellOrderId);
            p.Add($"BuyerId_{i}",     r.BuyerId);
            p.Add($"SellerId_{i}",    r.SellerId);
            p.Add($"Quantity_{i}",    r.Quantity);
            p.Add($"Price_{i}",       r.Price);
            p.Add($"Currency_{i}",    r.Currency);
            p.Add($"Timestamp_{i}",   r.Timestamp);
        }
        sql.Append(@" RETURNING ""TransactionId""");

        await using var c = await OpenAsync(ct);
        var ids = (await c.QueryAsync<int>(sql.ToString(), p)).ToList();
        if (ids.Count != txs.Count)
            throw new InvalidOperationException(
                $"InsertTransactionsBatch: RETURNING produced {ids.Count} ids for {txs.Count} rows.");
        for (int i = 0; i < txs.Count; i++) txs[i].TransactionId = ids[i];
    }
    #endregion
}
