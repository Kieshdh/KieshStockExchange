using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/user-watchlist")]
public sealed class UserWatchlistController : ControllerBase
{
    private readonly IDataBaseService _db;
    public UserWatchlistController(IDataBaseService db) => _db = db;

    [HttpGet("by-user/{userId:int}")]
    public Task<List<UserWatchlistEntry>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetWatchlistByUserId(userId, ct);
}
