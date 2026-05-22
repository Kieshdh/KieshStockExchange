using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// Bulk-passthrough endpoints for IDataBaseService's generic InsertAllAsync / UpdateAllAsync /
/// ResetTableAsync. Type is baked into the URL (one action per persisted entity) so OpenAPI
/// documents each shape cleanly and the model binder gives us typed List&lt;T&gt; without a
/// JsonElement-then-redeserialize dance. The server's DBService already dispatches by
/// type internally; these endpoints just route the wire payload to the right T.
///
/// For InsertAll: the server mutates the items list in-place with auto-assigned PKs (via
/// DBService.InsertViaMapper's writeback callback). The action returns Ok(items) so the
/// client can copy the PKs back onto its original collection — same semantics as the
/// in-process path, just with the writeback running on the deserialised server-side copy.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IDataBaseService _db;
    public AdminController(IDataBaseService db) => _db = db;

    private async Task<IActionResult> DoInsertAll<T>(IEnumerable<T> items, CancellationToken ct)
    {
        var list = items as IList<T> ?? items.ToList();
        await _db.InsertAllAsync(list, ct);
        return Ok(list);
    }

    private async Task<IActionResult> DoUpdateAll<T>(IEnumerable<T> items, CancellationToken ct)
    {
        await _db.UpdateAllAsync(items, ct);
        return NoContent();
    }

    private async Task<IActionResult> DoReset<T>(CancellationToken ct) where T : new()
    {
        await _db.ResetTableAsync<T>(ct);
        return NoContent();
    }

    [HttpPost("drop-recreate")]
    public async Task<IActionResult> DropRecreate([FromQuery] bool keepBackup, CancellationToken ct)
    {
        await _db.DropAndRecreateAsync(keepBackup, ct);
        return NoContent();
    }

    #region InsertAll
    [HttpPost("insert-all/users")]              public Task<IActionResult> InsertAllUsers([FromBody] List<User> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/stocks")]             public Task<IActionResult> InsertAllStocks([FromBody] List<Stock> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/stock-listings")]     public Task<IActionResult> InsertAllStockListings([FromBody] List<StockListing> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/stock-prices")]       public Task<IActionResult> InsertAllStockPrices([FromBody] List<StockPrice> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/orders")]             public Task<IActionResult> InsertAllOrders([FromBody] List<Order> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/transactions")]       public Task<IActionResult> InsertAllTransactions([FromBody] List<Transaction> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/positions")]          public Task<IActionResult> InsertAllPositions([FromBody] List<Position> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/funds")]              public Task<IActionResult> InsertAllFunds([FromBody] List<Fund> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/fund-transactions")]  public Task<IActionResult> InsertAllFundTransactions([FromBody] List<FundTransaction> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/candles")]            public Task<IActionResult> InsertAllCandles([FromBody] List<Candle> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/messages")]           public Task<IActionResult> InsertAllMessages([FromBody] List<Message> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/user-preferences")]   public Task<IActionResult> InsertAllUserPreferences([FromBody] List<UserPreferences> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/user-watchlist")]     public Task<IActionResult> InsertAllUserWatchlist([FromBody] List<UserWatchlistEntry> items, CancellationToken ct) => DoInsertAll(items, ct);
    [HttpPost("insert-all/ai-users")]           public Task<IActionResult> InsertAllAIUsers([FromBody] List<AIUser> items, CancellationToken ct) => DoInsertAll(items, ct);
    #endregion

    #region UpdateAll
    [HttpPost("update-all/users")]              public Task<IActionResult> UpdateAllUsers([FromBody] List<User> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/stocks")]             public Task<IActionResult> UpdateAllStocks([FromBody] List<Stock> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/stock-listings")]     public Task<IActionResult> UpdateAllStockListings([FromBody] List<StockListing> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/stock-prices")]       public Task<IActionResult> UpdateAllStockPrices([FromBody] List<StockPrice> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/orders")]             public Task<IActionResult> UpdateAllOrders([FromBody] List<Order> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/transactions")]       public Task<IActionResult> UpdateAllTransactions([FromBody] List<Transaction> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/positions")]          public Task<IActionResult> UpdateAllPositions([FromBody] List<Position> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/funds")]              public Task<IActionResult> UpdateAllFunds([FromBody] List<Fund> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/fund-transactions")]  public Task<IActionResult> UpdateAllFundTransactions([FromBody] List<FundTransaction> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/candles")]            public Task<IActionResult> UpdateAllCandles([FromBody] List<Candle> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/messages")]           public Task<IActionResult> UpdateAllMessages([FromBody] List<Message> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/user-preferences")]   public Task<IActionResult> UpdateAllUserPreferences([FromBody] List<UserPreferences> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/user-watchlist")]     public Task<IActionResult> UpdateAllUserWatchlist([FromBody] List<UserWatchlistEntry> items, CancellationToken ct) => DoUpdateAll(items, ct);
    [HttpPost("update-all/ai-users")]           public Task<IActionResult> UpdateAllAIUsers([FromBody] List<AIUser> items, CancellationToken ct) => DoUpdateAll(items, ct);
    #endregion

    #region Reset
    [HttpPost("reset/users")]              public Task<IActionResult> ResetUsers(CancellationToken ct) => DoReset<User>(ct);
    [HttpPost("reset/stocks")]             public Task<IActionResult> ResetStocks(CancellationToken ct) => DoReset<Stock>(ct);
    [HttpPost("reset/stock-listings")]     public Task<IActionResult> ResetStockListings(CancellationToken ct) => DoReset<StockListing>(ct);
    [HttpPost("reset/stock-prices")]       public Task<IActionResult> ResetStockPrices(CancellationToken ct) => DoReset<StockPrice>(ct);
    [HttpPost("reset/orders")]             public Task<IActionResult> ResetOrders(CancellationToken ct) => DoReset<Order>(ct);
    [HttpPost("reset/transactions")]       public Task<IActionResult> ResetTransactions(CancellationToken ct) => DoReset<Transaction>(ct);
    [HttpPost("reset/positions")]          public Task<IActionResult> ResetPositions(CancellationToken ct) => DoReset<Position>(ct);
    [HttpPost("reset/funds")]              public Task<IActionResult> ResetFunds(CancellationToken ct) => DoReset<Fund>(ct);
    [HttpPost("reset/fund-transactions")]  public Task<IActionResult> ResetFundTransactions(CancellationToken ct) => DoReset<FundTransaction>(ct);
    [HttpPost("reset/candles")]            public Task<IActionResult> ResetCandles(CancellationToken ct) => DoReset<Candle>(ct);
    [HttpPost("reset/messages")]           public Task<IActionResult> ResetMessages(CancellationToken ct) => DoReset<Message>(ct);
    [HttpPost("reset/user-preferences")]   public Task<IActionResult> ResetUserPreferences(CancellationToken ct) => DoReset<UserPreferences>(ct);
    [HttpPost("reset/user-watchlist")]     public Task<IActionResult> ResetUserWatchlist(CancellationToken ct) => DoReset<UserWatchlistEntry>(ct);
    [HttpPost("reset/ai-users")]           public Task<IActionResult> ResetAIUsers(CancellationToken ct) => DoReset<AIUser>(ct);
    #endregion
}
