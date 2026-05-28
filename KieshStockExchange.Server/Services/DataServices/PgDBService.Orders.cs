using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string OrderCols = @"
        ""OrderId"",""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",
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
        var rows = await c.QueryAsync<OrderRow>($@"
            SELECT {OrderCols} FROM ""Orders""
            WHERE ""UserId"" = ANY(@ids) AND ""Status"" = @open AND ""OrderType"" = ANY(@limitTypes)",
            new
            {
                ids = userIds.Distinct().ToArray(),
                open = Order.Statuses.Open,
                limitTypes = new[] { Order.Types.LimitBuy, Order.Types.LimitSell },
            });
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task CreateOrder(Order order, CancellationToken ct = default)
    {
        if (!order.IsValid()) throw new ArgumentException("Order entity is not valid", nameof(order));
        await using var c = await OpenAsync(ct);
        var row = OrderMapper.ToRow(order);
        row.OrderId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Orders"" (""UserId"",""StockId"",""Quantity"",""Price"",""SlippagePercent"",""BuyBudget"",
                                   ""Currency"",""OrderType"",""Status"",""AmountFilled"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@StockId,@Quantity,@Price,@SlippagePercent,@BuyBudget,
                    @Currency,@OrderType,@Status,@AmountFilled,@CreatedAt,@UpdatedAt)
            RETURNING ""OrderId""", row);
        order.OrderId = row.OrderId;
    }

    public async Task UpdateOrder(Order order, CancellationToken ct = default)
    {
        if (!order.IsValid()) throw new ArgumentException("Order entity is not valid", nameof(order));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Orders"" SET
              ""UserId"" = @UserId, ""StockId"" = @StockId, ""Quantity"" = @Quantity, ""Price"" = @Price,
              ""SlippagePercent"" = @SlippagePercent, ""BuyBudget"" = @BuyBudget, ""Currency"" = @Currency,
              ""OrderType"" = @OrderType, ""Status"" = @Status, ""AmountFilled"" = @AmountFilled,
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
        int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<TransactionRow>(
            $@"SELECT {TransactionCols} FROM ""Transactions""
               WHERE ""StockId"" = @stockId AND ""Currency"" = @currency
                 AND ""Timestamp"" >= @from AND ""Timestamp"" < @to",
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
}
