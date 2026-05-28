using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class PgDBService
{
    public Task<List<User>> GetUsersAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<(List<User> Items, int Total)> GetUsersPageAsync(
        int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<User?> GetUserById(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<User?> GetUserByUsername(string username, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> UserExists(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CreateUser(User user, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateUser(User user, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertUser(User user, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteUser(User user, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DeleteUserById(int userId, CancellationToken ct = default)
        => throw new NotImplementedException();
}
