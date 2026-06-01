using System.Text;
using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string PositionCols = @"""PositionId"",""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt""";
    private const string FundCols = @"""FundId"",""UserId"",""TotalBalance"",""ReservedBalance"",""Currency"",""CreatedAt"",""UpdatedAt""";
    private const string FundTxCols = @"""FundTransactionId"",""UserId"",""Currency"",""Amount"",""Kind"",""Note"",""CreatedAt""";

    #region Position operations
    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<PositionRow>($@"SELECT {PositionCols} FROM ""Positions""");
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task<(List<Position> Items, int Total)> GetPositionsPageAsync(
        int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var clauses = new List<string> { @"""StockId"" = @stockId" };
        var dp = new DynamicParameters();
        dp.Add("stockId", stockId);
        if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var userId))
        {
            clauses.Add(@"""UserId"" = @uid");
            dp.Add("uid", userId);
        }
        var where = "WHERE " + string.Join(" AND ", clauses);
        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Positions"" {where}", dp);

        var orderCol = sortKey switch
        {
            "Quantity" => "\"Quantity\"",
            "Reserved" => "\"ReservedQuantity\"",
            _          => "\"UserId\"",
        };
        var dir = desc ? "DESC" : "ASC";
        dp.Add("skip", skip);
        dp.Add("take", take);
        var rows = await c.QueryAsync<PositionRow>($@"
            SELECT {PositionCols} FROM ""Positions"" {where}
            ORDER BY {orderCol} {dir}
            OFFSET @skip LIMIT @take", dp);
        return (rows.Select(PositionMapper.ToDomain).ToList(), total);
    }

    public async Task<Position?> GetPositionById(int positionId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<PositionRow>(
            $@"SELECT {PositionCols} FROM ""Positions"" WHERE ""PositionId"" = @positionId",
            new { positionId });
        return row is null ? null : PositionMapper.ToDomain(row);
    }

    public async Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<PositionRow>(
            $@"SELECT {PositionCols} FROM ""Positions"" WHERE ""UserId"" = @userId", new { userId });
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<PositionRow>(
            $@"SELECT {PositionCols} FROM ""Positions"" WHERE ""UserId"" = @userId AND ""StockId"" = @stockId",
            new { userId, stockId });
        return row is null ? null : PositionMapper.ToDomain(row);
    }

    public async Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Position>();
        await using var c = await OpenAsync(ct);
        var ids = userIds.Distinct().ToArray();
        var rows = await c.QueryAsync<PositionRow>(
            $@"SELECT {PositionCols} FROM ""Positions"" WHERE ""UserId"" = ANY(@ids)", new { ids });
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task CreatePosition(Position position, CancellationToken ct = default)
    {
        if (!position.IsValid()) throw new ArgumentException("Position entity is not valid", nameof(position));
        await using var c = await OpenAsync(ct);
        var row = PositionMapper.ToRow(position);
        row.PositionId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Positions"" (""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@StockId,@Quantity,@ReservedQuantity,@CreatedAt,@UpdatedAt)
            RETURNING ""PositionId""", row);
        position.PositionId = row.PositionId;
    }

    // Engine-driven path; CHECK constraint enforces the invariant.
    public async Task UpdatePosition(Position position, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Positions"" SET ""UserId"" = @UserId, ""StockId"" = @StockId,
              ""Quantity"" = @Quantity, ""ReservedQuantity"" = @ReservedQuantity,
              ""CreatedAt"" = @CreatedAt, ""UpdatedAt"" = @UpdatedAt
            WHERE ""PositionId"" = @PositionId", PositionMapper.ToRow(position));
    }

    public async Task DeletePosition(Position position, CancellationToken ct = default)
    {
        if (position.PositionId == 0)
            throw new ArgumentException("Position entity must have a valid PositionId", nameof(position));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Positions"" WHERE ""PositionId"" = @PositionId",
            new { position.PositionId });
    }

    // ON CONFLICT on the (UserId, StockId) unique index — atomic, replaces the
    // existing SELECT-then-INSERT/UPDATE pattern.
    public async Task UpsertPosition(Position position, CancellationToken ct = default)
    {
        if (!position.IsValid()) throw new ArgumentException("Position entity is not valid", nameof(position));
        await using var c = await OpenAsync(ct);
        var row = PositionMapper.ToRow(position);
        row.PositionId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Positions"" (""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@StockId,@Quantity,@ReservedQuantity,@CreatedAt,@UpdatedAt)
            ON CONFLICT (""UserId"",""StockId"") DO UPDATE SET
              ""Quantity"" = EXCLUDED.""Quantity"", ""ReservedQuantity"" = EXCLUDED.""ReservedQuantity"",
              ""UpdatedAt"" = EXCLUDED.""UpdatedAt""
            RETURNING ""PositionId""", row);
        position.PositionId = row.PositionId;
    }
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<FundRow>($@"SELECT {FundCols} FROM ""Funds""");
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    public async Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(
        int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var knownCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "USD", "EUR", "GBP", "JPY", "CHF", "AUD" };

        if (knownCurrencies.Contains(sortKey))
        {
            // Sort users by their TotalBalance in this currency.
            var code = sortKey.ToUpperInvariant();
            var dp = new DynamicParameters();
            dp.Add("code", code);
            var clauses = new List<string> { @"""Currency"" = @code" };
            if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var uid))
            {
                clauses.Add(@"""UserId"" = @uid");
                dp.Add("uid", uid);
            }
            var where = "WHERE " + string.Join(" AND ", clauses);
            var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Funds"" {where}", dp);
            var dir = desc ? "DESC" : "ASC";
            dp.Add("skip", skip); dp.Add("take", take);
            var ids = await c.QueryAsync<int>(
                $@"SELECT ""UserId"" FROM ""Funds"" {where}
                   ORDER BY ""TotalBalance"" {dir}
                   OFFSET @skip LIMIT @take", dp);
            return (ids.ToList(), total);
        }
        if (string.Equals(sortKey, "Reserved", StringComparison.Ordinal))
        {
            // Sort users by ReservedBalance of their USD fund — matches DBService.
            var dp = new DynamicParameters();
            var clauses = new List<string> { @"""Currency"" = 'USD'" };
            if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var uid))
            {
                clauses.Add(@"""UserId"" = @uid");
                dp.Add("uid", uid);
            }
            var where = "WHERE " + string.Join(" AND ", clauses);
            var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Funds"" {where}", dp);
            var dir = desc ? "DESC" : "ASC";
            dp.Add("skip", skip); dp.Add("take", take);
            var ids = await c.QueryAsync<int>(
                $@"SELECT ""UserId"" FROM ""Funds"" {where}
                   ORDER BY ""ReservedBalance"" {dir}
                   OFFSET @skip LIMIT @take", dp);
            return (ids.ToList(), total);
        }
        // Default: sort by UserId from Users table.
        {
            var dp = new DynamicParameters();
            var where = "";
            if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var uid))
            {
                where = @"WHERE ""UserId"" = @uid";
                dp.Add("uid", uid);
            }
            var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Users"" {where}", dp);
            var dir = desc ? "DESC" : "ASC";
            dp.Add("skip", skip); dp.Add("take", take);
            var ids = await c.QueryAsync<int>(
                $@"SELECT ""UserId"" FROM ""Users"" {where}
                   ORDER BY ""UserId"" {dir}
                   OFFSET @skip LIMIT @take", dp);
            return (ids.ToList(), total);
        }
    }

    public async Task<(List<Fund> Items, int Total)> GetFundsPageAsync(
        int skip, int take, string sortKey, bool desc,
        int? userIdFilter = null, bool hasNonZero = false, bool hasReserved = false,
        string? currencyFilter = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var clauses = new List<string>();
        var dp = new DynamicParameters();
        if (userIdFilter is int uid) { clauses.Add(@"""UserId"" = @uid"); dp.Add("uid", uid); }
        if (hasNonZero)  clauses.Add(@"""TotalBalance"" > 0");
        if (hasReserved) clauses.Add(@"""ReservedBalance"" > 0");
        if (!string.IsNullOrWhiteSpace(currencyFilter))
        {
            clauses.Add(@"""Currency"" = @cf");
            dp.Add("cf", currencyFilter.ToUpperInvariant());
        }
        var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Funds"" {where}", dp);

        var orderCol = sortKey switch
        {
            "TotalBalance"    => "\"TotalBalance\"",
            "ReservedBalance" => "\"ReservedBalance\"",
            "Currency"        => "\"Currency\"",
            "UpdatedAt"       => "\"UpdatedAt\"",
            _                 => "\"UserId\"",
        };
        var dir = desc ? "DESC" : "ASC";
        dp.Add("skip", skip); dp.Add("take", take);
        var rows = await c.QueryAsync<FundRow>($@"
            SELECT {FundCols} FROM ""Funds"" {where}
            ORDER BY {orderCol} {dir}
            OFFSET @skip LIMIT @take", dp);
        return (rows.Select(FundMapper.ToDomain).ToList(), total);
    }

    public async Task<Fund?> GetFundById(int fundId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<FundRow>(
            $@"SELECT {FundCols} FROM ""Funds"" WHERE ""FundId"" = @fundId", new { fundId });
        return row is null ? null : FundMapper.ToDomain(row);
    }

    public async Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<FundRow>(
            $@"SELECT {FundCols} FROM ""Funds"" WHERE ""UserId"" = @userId", new { userId });
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    public async Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<FundRow>(
            $@"SELECT {FundCols} FROM ""Funds"" WHERE ""UserId"" = @userId AND ""Currency"" = @currency",
            new { userId, currency = currency.ToString() });
        return row is null ? null : FundMapper.ToDomain(row);
    }

    public async Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Fund>();
        await using var c = await OpenAsync(ct);
        var ids = userIds.Distinct().ToArray();
        var rows = await c.QueryAsync<FundRow>(
            $@"SELECT {FundCols} FROM ""Funds"" WHERE ""UserId"" = ANY(@ids)", new { ids });
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    public async Task CreateFund(Fund fund, CancellationToken ct = default)
    {
        if (!fund.IsValid()) throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await using var c = await OpenAsync(ct);
        var row = FundMapper.ToRow(fund);
        row.FundId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Funds"" (""UserId"",""TotalBalance"",""ReservedBalance"",""Currency"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@TotalBalance,@ReservedBalance,@Currency,@CreatedAt,@UpdatedAt)
            RETURNING ""FundId""", row);
        fund.FundId = row.FundId;
    }

    // Engine-driven path; CHECK constraint enforces the invariant.
    public async Task UpdateFund(Fund fund, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"
            UPDATE ""Funds"" SET ""UserId"" = @UserId, ""TotalBalance"" = @TotalBalance,
              ""ReservedBalance"" = @ReservedBalance, ""Currency"" = @Currency,
              ""CreatedAt"" = @CreatedAt, ""UpdatedAt"" = @UpdatedAt
            WHERE ""FundId"" = @FundId", FundMapper.ToRow(fund));
    }

    public async Task DeleteFund(Fund fund, CancellationToken ct = default)
    {
        if (fund.FundId == 0)
            throw new ArgumentException("Fund entity must have a valid FundId", nameof(fund));
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Funds"" WHERE ""FundId"" = @FundId", new { fund.FundId });
    }

    // ON CONFLICT on the (UserId, Currency) unique index — atomic.
    public async Task UpsertFund(Fund fund, CancellationToken ct = default)
    {
        if (!fund.IsValid()) throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await using var c = await OpenAsync(ct);
        var row = FundMapper.ToRow(fund);
        row.FundId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Funds"" (""UserId"",""TotalBalance"",""ReservedBalance"",""Currency"",""CreatedAt"",""UpdatedAt"")
            VALUES (@UserId,@TotalBalance,@ReservedBalance,@Currency,@CreatedAt,@UpdatedAt)
            ON CONFLICT (""UserId"",""Currency"") DO UPDATE SET
              ""TotalBalance"" = EXCLUDED.""TotalBalance"",
              ""ReservedBalance"" = EXCLUDED.""ReservedBalance"",
              ""UpdatedAt"" = EXCLUDED.""UpdatedAt""
            RETURNING ""FundId""", row);
        fund.FundId = row.FundId;
    }
    #endregion

    #region FundTransaction operations
    public async Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<FundTransactionRow>(
            $@"SELECT {FundTxCols} FROM ""FundTransactions""
               WHERE ""UserId"" = @userId
               ORDER BY ""CreatedAt"" DESC", new { userId });
        return rows.Select(FundTransactionMapper.ToDomain).ToList();
    }

    public async Task<(List<FundTransaction> Items, int Total)> GetFundTransactionsPageAsync(
        int skip, int take, string sortKey, bool desc, int? userIdFilter = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var dp = new DynamicParameters();
        var where = "";
        if (userIdFilter is int uid) { where = @"WHERE ""UserId"" = @uid"; dp.Add("uid", uid); }

        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""FundTransactions"" {where}", dp);

        var orderCol = sortKey switch
        {
            "FundTransactionId" => "\"FundTransactionId\"",
            "UserId"            => "\"UserId\"",
            "Amount"            => "\"Amount\"",
            "Currency"          => "\"Currency\"",
            "Kind"              => "\"Kind\"",
            _                   => "\"CreatedAt\"",
        };
        var dir = desc ? "DESC" : "ASC";
        dp.Add("skip", skip); dp.Add("take", take);
        var rows = await c.QueryAsync<FundTransactionRow>($@"
            SELECT {FundTxCols} FROM ""FundTransactions"" {where}
            ORDER BY {orderCol} {dir}, ""FundTransactionId"" DESC
            OFFSET @skip LIMIT @take", dp);
        return (rows.Select(FundTransactionMapper.ToDomain).ToList(), total);
    }

    public async Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default)
    {
        if (!tx.IsValid()) throw new ArgumentException("FundTransaction entity is not valid", nameof(tx));
        await using var c = await OpenAsync(ct);
        var row = FundTransactionMapper.ToRow(tx);
        row.FundTransactionId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""FundTransactions"" (""UserId"",""Currency"",""Amount"",""Kind"",""Note"",""CreatedAt"")
            VALUES (@UserId,@Currency,@Amount,@Kind,@Note,@CreatedAt)
            RETURNING ""FundTransactionId""", row);
        tx.FundTransactionId = row.FundTransactionId;
    }
    #endregion

    #region Batched hot-type writes (P1)
    // Multi-row INSERT/UPDATE for Position / Fund / FundTransaction. Same
    // per-row SQL as CreatePosition/UpdatePosition/UpdateFund/CreateFundTransaction,
    // unrolled into one VALUES statement so each bot trade group settles in
    // ~5 round-trips instead of ~20.
    private async Task InsertPositionsBatchAsync(IReadOnlyList<Position> positions, CancellationToken ct)
    {
        for (int i = 0; i < positions.Count; i++)
            if (!positions[i].IsValid())
                throw new ArgumentException("Position entity is not valid", nameof(positions));

        var sql = new StringBuilder(@"
            INSERT INTO ""Positions"" (""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt"")
            VALUES ", capacity: 192 + positions.Count * 64);
        var p = new DynamicParameters();
        for (int i = 0; i < positions.Count; i++)
        {
            if (i > 0) sql.Append(',');
            sql.Append("(@UserId_").Append(i)
               .Append(",@StockId_").Append(i)
               .Append(",@Quantity_").Append(i)
               .Append(",@ReservedQuantity_").Append(i)
               .Append(",@CreatedAt_").Append(i)
               .Append(",@UpdatedAt_").Append(i)
               .Append(')');

            var r = PositionMapper.ToRow(positions[i]);
            p.Add($"UserId_{i}",           r.UserId);
            p.Add($"StockId_{i}",          r.StockId);
            p.Add($"Quantity_{i}",         r.Quantity);
            p.Add($"ReservedQuantity_{i}", r.ReservedQuantity);
            p.Add($"CreatedAt_{i}",        r.CreatedAt);
            p.Add($"UpdatedAt_{i}",        r.UpdatedAt);
        }
        sql.Append(@" RETURNING ""PositionId""");

        await using var c = await OpenAsync(ct);
        var ids = (await c.QueryAsync<int>(sql.ToString(), p)).ToList();
        if (ids.Count != positions.Count)
            throw new InvalidOperationException(
                $"InsertPositionsBatch: RETURNING produced {ids.Count} ids for {positions.Count} rows.");
        for (int i = 0; i < positions.Count; i++) positions[i].PositionId = ids[i];
    }

    // No IsValid() — engine-driven; CHECK constraint enforces the invariant.
    private async Task UpdatePositionsBatchAsync(IReadOnlyList<Position> positions, CancellationToken ct)
    {
        var sql = new StringBuilder(@"
            UPDATE ""Positions"" SET
              ""UserId"" = data.""UserId"", ""StockId"" = data.""StockId"",
              ""Quantity"" = data.""Quantity"", ""ReservedQuantity"" = data.""ReservedQuantity"",
              ""CreatedAt"" = data.""CreatedAt"", ""UpdatedAt"" = data.""UpdatedAt""
            FROM (VALUES ", capacity: 384 + positions.Count * 96);
        var p = new DynamicParameters();
        for (int i = 0; i < positions.Count; i++)
        {
            if (i > 0) sql.Append(',');
            if (i == 0)
                sql.Append("(@PositionId_0::int,@UserId_0::int,@StockId_0::int,@Quantity_0::int,@ReservedQuantity_0::int,@CreatedAt_0::timestamptz,@UpdatedAt_0::timestamptz)");
            else
                sql.Append("(@PositionId_").Append(i)
                   .Append(",@UserId_").Append(i)
                   .Append(",@StockId_").Append(i)
                   .Append(",@Quantity_").Append(i)
                   .Append(",@ReservedQuantity_").Append(i)
                   .Append(",@CreatedAt_").Append(i)
                   .Append(",@UpdatedAt_").Append(i)
                   .Append(')');

            var r = PositionMapper.ToRow(positions[i]);
            p.Add($"PositionId_{i}",       r.PositionId);
            p.Add($"UserId_{i}",           r.UserId);
            p.Add($"StockId_{i}",          r.StockId);
            p.Add($"Quantity_{i}",         r.Quantity);
            p.Add($"ReservedQuantity_{i}", r.ReservedQuantity);
            p.Add($"CreatedAt_{i}",        r.CreatedAt);
            p.Add($"UpdatedAt_{i}",        r.UpdatedAt);
        }
        sql.Append(@") AS data(""PositionId"",""UserId"",""StockId"",""Quantity"",""ReservedQuantity"",""CreatedAt"",""UpdatedAt"") WHERE ""Positions"".""PositionId"" = data.""PositionId""");

        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(sql.ToString(), p);
    }

    // No IsValid() — engine-driven; CHECK constraint enforces the invariant.
    private async Task UpdateFundsBatchAsync(IReadOnlyList<Fund> funds, CancellationToken ct)
    {
        var sql = new StringBuilder(@"
            UPDATE ""Funds"" SET
              ""UserId"" = data.""UserId"", ""TotalBalance"" = data.""TotalBalance"",
              ""ReservedBalance"" = data.""ReservedBalance"", ""Currency"" = data.""Currency"",
              ""CreatedAt"" = data.""CreatedAt"", ""UpdatedAt"" = data.""UpdatedAt""
            FROM (VALUES ", capacity: 384 + funds.Count * 96);
        var p = new DynamicParameters();
        for (int i = 0; i < funds.Count; i++)
        {
            if (i > 0) sql.Append(',');
            if (i == 0)
                sql.Append("(@FundId_0::int,@UserId_0::int,@TotalBalance_0::numeric,@ReservedBalance_0::numeric,@Currency_0::text,@CreatedAt_0::timestamptz,@UpdatedAt_0::timestamptz)");
            else
                sql.Append("(@FundId_").Append(i)
                   .Append(",@UserId_").Append(i)
                   .Append(",@TotalBalance_").Append(i)
                   .Append(",@ReservedBalance_").Append(i)
                   .Append(",@Currency_").Append(i)
                   .Append(",@CreatedAt_").Append(i)
                   .Append(",@UpdatedAt_").Append(i)
                   .Append(')');

            var r = FundMapper.ToRow(funds[i]);
            p.Add($"FundId_{i}",          r.FundId);
            p.Add($"UserId_{i}",          r.UserId);
            p.Add($"TotalBalance_{i}",    r.TotalBalance);
            p.Add($"ReservedBalance_{i}", r.ReservedBalance);
            p.Add($"Currency_{i}",        r.Currency);
            p.Add($"CreatedAt_{i}",       r.CreatedAt);
            p.Add($"UpdatedAt_{i}",       r.UpdatedAt);
        }
        sql.Append(@") AS data(""FundId"",""UserId"",""TotalBalance"",""ReservedBalance"",""Currency"",""CreatedAt"",""UpdatedAt"") WHERE ""Funds"".""FundId"" = data.""FundId""");

        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(sql.ToString(), p);
    }

    private async Task InsertFundTransactionsBatchAsync(IReadOnlyList<FundTransaction> txs, CancellationToken ct)
    {
        for (int i = 0; i < txs.Count; i++)
            if (!txs[i].IsValid())
                throw new ArgumentException("FundTransaction entity is not valid", nameof(txs));

        var sql = new StringBuilder(@"
            INSERT INTO ""FundTransactions"" (""UserId"",""Currency"",""Amount"",""Kind"",""Note"",""CreatedAt"")
            VALUES ", capacity: 192 + txs.Count * 64);
        var p = new DynamicParameters();
        for (int i = 0; i < txs.Count; i++)
        {
            if (i > 0) sql.Append(',');
            sql.Append("(@UserId_").Append(i)
               .Append(",@Currency_").Append(i)
               .Append(",@Amount_").Append(i)
               .Append(",@Kind_").Append(i)
               .Append(",@Note_").Append(i)
               .Append(",@CreatedAt_").Append(i)
               .Append(')');

            var r = FundTransactionMapper.ToRow(txs[i]);
            p.Add($"UserId_{i}",    r.UserId);
            p.Add($"Currency_{i}",  r.Currency);
            p.Add($"Amount_{i}",    r.Amount);
            p.Add($"Kind_{i}",      r.Kind);
            p.Add($"Note_{i}",      r.Note);
            p.Add($"CreatedAt_{i}", r.CreatedAt);
        }
        sql.Append(@" RETURNING ""FundTransactionId""");

        await using var c = await OpenAsync(ct);
        var ids = (await c.QueryAsync<int>(sql.ToString(), p)).ToList();
        if (ids.Count != txs.Count)
            throw new InvalidOperationException(
                $"InsertFundTransactionsBatch: RETURNING produced {ids.Count} ids for {txs.Count} rows.");
        for (int i = 0; i < txs.Count; i++) txs[i].FundTransactionId = ids[i];
    }
    #endregion
}
