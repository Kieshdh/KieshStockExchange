using Dapper;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    private const string UserCols =
        @"""UserId"",""Username"",""PasswordHash"",""Email"",""FullName"",""CreatedAt"",""BirthDate"",""IsAdmin""";

    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<UserRow>($@"SELECT {UserCols} FROM ""Users""");
        return rows.Select(UserMapper.ToDomain).ToList();
    }

    public async Task<(List<User> Items, int Total)> GetUsersPageAsync(
        int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        (skip, take) = ClampPage(skip, take);
        await using var c = await OpenAsync(ct);

        var where = "";
        object? param = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            if (int.TryParse(f, out var id))
            {
                where = @"WHERE ""UserId"" = @Id";
                param = new { Id = id };
            }
            else
            {
                where = @"WHERE ""Username"" ILIKE @Pat OR ""Email"" ILIKE @Pat OR ""FullName"" ILIKE @Pat";
                param = new { Pat = "%" + f + "%" };
            }
        }

        var total = await c.ExecuteScalarAsync<int>($@"SELECT COUNT(*) FROM ""Users"" {where}", param);

        var orderCol = sortKey switch
        {
            "Username"  => "\"Username\"",
            "Email"     => "\"Email\"",
            "FullName"  => "\"FullName\"",
            "BirthDate" => "\"BirthDate\"",
            "UserId"    => "\"UserId\"",
            _           => "\"CreatedAt\"",
        };
        var dir = desc ? "DESC" : "ASC";

        var sql = $@"SELECT {UserCols} FROM ""Users"" {where}
                     ORDER BY {orderCol} {dir}
                     OFFSET @Skip LIMIT @Take";
        var p2 = param is null
            ? (object)new { Skip = skip, Take = take }
            : MergeSkipTake(param, skip, take);
        var rows = await c.QueryAsync<UserRow>(sql, p2);
        return (rows.Select(UserMapper.ToDomain).ToList(), total);
    }

    public async Task<User?> GetUserById(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(
            $@"SELECT {UserCols} FROM ""Users"" WHERE ""UserId"" = @userId",
            new { userId });
        return row is null ? null : UserMapper.ToDomain(row);
    }

    public async Task<User?> GetUserByUsername(string username, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<UserRow>(
            $@"SELECT {UserCols} FROM ""Users"" WHERE ""Username"" = @username",
            new { username });
        return row is null ? null : UserMapper.ToDomain(row);
    }

    public async Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<User>();
        await using var c = await OpenAsync(ct);
        var ids = userIds.Distinct().ToArray();
        var rows = await c.QueryAsync<UserRow>(
            $@"SELECT {UserCols} FROM ""Users"" WHERE ""UserId"" = ANY(@ids)",
            new { ids });
        return rows.Select(UserMapper.ToDomain).ToList();
    }

    public async Task<bool> UserExists(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return await c.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(SELECT 1 FROM ""Users"" WHERE ""UserId"" = @userId)",
            new { userId });
    }

    public async Task CreateUser(User user, CancellationToken ct = default)
    {
        if (!user.IsValid()) throw new ArgumentException("User entity is not valid", nameof(user));
        await using var c = await OpenAsync(ct);
        var row = UserMapper.ToRow(user);
        row.UserId = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Users"" (""Username"",""PasswordHash"",""Email"",""FullName"",""CreatedAt"",""BirthDate"",""IsAdmin"")
            VALUES (@Username,@PasswordHash,@Email,@FullName,@CreatedAt,@BirthDate,@IsAdmin)
            RETURNING ""UserId""", row);
        user.UserId = row.UserId;
    }

    public async Task UpdateUser(User user, CancellationToken ct = default)
    {
        if (!user.IsValid()) throw new ArgumentException("User entity is not valid", nameof(user));
        await using var c = await OpenAsync(ct);
        var row = UserMapper.ToRow(user);
        await c.ExecuteAsync(@"
            UPDATE ""Users"" SET
              ""Username"" = @Username, ""PasswordHash"" = @PasswordHash, ""Email"" = @Email,
              ""FullName"" = @FullName, ""CreatedAt"" = @CreatedAt, ""BirthDate"" = @BirthDate,
              ""IsAdmin"" = @IsAdmin
            WHERE ""UserId"" = @UserId", row);
    }

    public async Task UpsertUser(User user, CancellationToken ct = default)
    {
        if (!user.IsValid()) throw new ArgumentException("User entity is not valid", nameof(user));
        await using var c = await OpenAsync(ct);
        var row = UserMapper.ToRow(user);
        var returned = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO ""Users"" (""UserId"",""Username"",""PasswordHash"",""Email"",""FullName"",""CreatedAt"",""BirthDate"",""IsAdmin"")
            VALUES (@UserId,@Username,@PasswordHash,@Email,@FullName,@CreatedAt,@BirthDate,@IsAdmin)
            ON CONFLICT (""UserId"") DO UPDATE SET
              ""Username"" = EXCLUDED.""Username"", ""PasswordHash"" = EXCLUDED.""PasswordHash"",
              ""Email"" = EXCLUDED.""Email"", ""FullName"" = EXCLUDED.""FullName"",
              ""CreatedAt"" = EXCLUDED.""CreatedAt"", ""BirthDate"" = EXCLUDED.""BirthDate"",
              ""IsAdmin"" = EXCLUDED.""IsAdmin""
            RETURNING ""UserId""", row);
        user.UserId = returned;
    }

    public async Task DeleteUser(User user, CancellationToken ct = default)
    {
        if (user.UserId == 0)
            throw new ArgumentException("User entity must have a valid UserId", nameof(user));
        await DeleteUserById(user.UserId, ct);
    }

    public async Task DeleteUserById(int userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync(@"DELETE FROM ""Users"" WHERE ""UserId"" = @userId", new { userId });
    }

    private static object MergeSkipTake(object filter, int skip, int take)
    {
        var dp = new DynamicParameters(filter);
        dp.Add("Skip", skip);
        dp.Add("Take", take);
        return dp;
    }
}
