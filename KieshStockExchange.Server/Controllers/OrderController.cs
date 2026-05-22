using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrderController : ControllerBase
{
    private readonly IDataBaseService _db;
    public OrderController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Order>> GetAll(CancellationToken ct) => _db.GetOrdersAsync(ct);

    [HttpGet("page")]
    public async Task<PageResponse<Order>> GetPage(
        [FromQuery] int skip, [FromQuery] int take, [FromQuery] string sortKey, [FromQuery] bool desc,
        [FromQuery] DateTime fromUtc, [FromQuery] DateTime toUtc,
        [FromQuery] string? statusFilter,
        [FromQuery] int? userIdFilter, [FromQuery] int? stockIdFilter,
        [FromQuery] string? sideFilter, [FromQuery] string? typeFilter,
        [FromQuery] List<int>? excludeUserIds,
        CancellationToken ct)
    {
        var (items, total) = await _db.GetOrdersPageAsync(skip, take, sortKey, desc, fromUtc, toUtc,
            statusFilter, userIdFilter, stockIdFilter, sideFilter, typeFilter, excludeUserIds, ct);
        return new PageResponse<Order>(items, total);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken ct)
        => await _db.GetOrderById(id, ct) is { } o ? Ok(o) : NotFound();

    [HttpPost("by-ids")]
    public Task<List<Order>> GetByIds([FromBody] List<int> ids, CancellationToken ct)
        => _db.GetOrdersByIds(ids, ct);

    [HttpGet("by-user/{userId:int}")]
    public Task<List<Order>> GetByUserId(int userId, CancellationToken ct)
        => _db.GetOrdersByUserId(userId, ct);

    [HttpGet("by-stock/{stockId:int}")]
    public Task<List<Order>> GetByStockId(int stockId, CancellationToken ct)
        => _db.GetOrdersByStockId(stockId, ct);

    [HttpGet("open-limit/{stockId:int}/{currency}")]
    public Task<List<Order>> GetOpenLimit(int stockId, CurrencyType currency, CancellationToken ct)
        => _db.GetOpenLimitOrders(stockId, currency, ct);

    [HttpPost("open-for-users")]
    public Task<List<Order>> GetOpenForUsers([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetOpenOrdersForUsersAsync(userIds, ct);

    [HttpPost]
    public async Task<ActionResult<Order>> Create([FromBody] Order order, CancellationToken ct)
    { await _db.CreateOrder(order, ct); return Ok(order); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] Order order, CancellationToken ct)
    { await _db.UpdateOrder(order, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteOrder(new Order { OrderId = id }, ct); return NoContent(); }
}
