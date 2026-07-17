using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

// UP-STORE — the buffered write-behind store. Drive FlushOnceAsync directly (internal, reachable via
// the Server csproj's InternalsVisibleTo) against a Mock<IUserDrawingQueries> so the coalescing /
// read-your-writes / retry / delete-tombstone / shutdown-drain behaviour is covered without a DB.
public sealed class UserDrawingStoreTests
{
    private static UserDrawingStore Make(IUserDrawingQueries queries) =>
        new(queries, new ConfigurationBuilder().Build(), NullLogger<UserDrawingStore>.Instance);

    [Fact] // (a) two saves to the same key coalesce to ONE upsert carrying the latest Json.
    public async Task Enqueue_SameKeyTwice_FlushesOneUpsertWithLatestJson()
    {
        var q = new Mock<IUserDrawingQueries>();
        var store = Make(q.Object);

        store.Enqueue(1, 2, "USD", "{\"v\":1,\"a\":1}");
        store.Enqueue(1, 2, "USD", "{\"v\":1,\"b\":2}");
        await store.FlushOnceAsync();

        q.Verify(x => x.UpsertUserDrawingAsync(
            It.Is<UserDrawingRow>(r => r.UserId == 1 && r.StockId == 2 && r.Currency == "USD"
                                       && r.Json == "{\"v\":1,\"b\":2}"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // (b) a buffered value is returned before the flush lands (read-your-writes) — no DB read.
    public async Task GetAsync_ReturnsBufferedValueBeforeFlush()
    {
        var q = new Mock<IUserDrawingQueries>();
        var store = Make(q.Object);

        store.Enqueue(1, 2, "USD", "{\"v\":1}");
        var got = await store.GetAsync(1, 2, "USD", CancellationToken.None);

        Assert.Equal("{\"v\":1}", got);
        q.Verify(x => x.GetUserDrawingAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // (c) a successful drain clears the key.
    public async Task FlushOnceAsync_Success_ClearsKey()
    {
        var q = new Mock<IUserDrawingQueries>();
        var store = Make(q.Object);

        store.Enqueue(1, 2, "USD", "{\"v\":1}");
        await store.FlushOnceAsync();

        Assert.Equal(0, store.DirtyCount);
    }

    [Fact] // (d) a failing write does not throw and the key is RETAINED (write-then-remove, retried next tick).
    public async Task FlushOnceAsync_UpsertThrows_KeyRetainedAndNoThrow()
    {
        var q = new Mock<IUserDrawingQueries>();
        q.Setup(x => x.UpsertUserDrawingAsync(It.IsAny<UserDrawingRow>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var store = Make(q.Object);

        store.Enqueue(1, 2, "USD", "{\"v\":1}");
        await store.FlushOnceAsync();   // must not throw

        Assert.Equal(1, store.DirtyCount);
    }

    [Fact] // (e) a delete drains as a DELETE, not an upsert (tombstone in the same last-write-wins stream).
    public async Task Delete_FlushesDeleteNotUpsert()
    {
        var q = new Mock<IUserDrawingQueries>();
        var store = Make(q.Object);

        store.Delete(1, 2, "USD");
        await store.FlushOnceAsync();

        q.Verify(x => x.DeleteUserDrawingAsync(1, 2, "USD", It.IsAny<CancellationToken>()), Times.Once);
        q.Verify(x => x.UpsertUserDrawingAsync(It.IsAny<UserDrawingRow>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact] // (f) shutdown final-drain: Start → enqueue → Stop persists the buffered write (not lost on restart).
    public async Task ShutdownDrain_PersistsBufferedWrite()
    {
        var q = new Mock<IUserDrawingQueries>();
        var store = Make(q.Object);

        await store.StartAsync(CancellationToken.None);
        store.Enqueue(1, 2, "USD", "{\"v\":1}");
        await store.StopAsync(CancellationToken.None);   // cancels the loop → final drain

        q.Verify(x => x.UpsertUserDrawingAsync(
            It.Is<UserDrawingRow>(r => r.Json == "{\"v\":1}"), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
