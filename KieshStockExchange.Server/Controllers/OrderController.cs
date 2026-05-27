using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrderController : ControllerBase
{
    private readonly IDataBaseService _db;
    private readonly IOrderEntryService _entry;
    private readonly IOrderExecutionService _execution;
    private readonly ILogger<OrderController> _logger;

    public OrderController(IDataBaseService db, IOrderEntryService entry,
        IOrderExecutionService execution, ILogger<OrderController> logger)
    {
        _db = db;
        _entry = entry;
        _execution = execution;
        _logger = logger;
    }

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

    // Phase 3 Step 3 — engine-driven order entry. Dispatches into the in-process
    // OrderEntryService / OrderExecutionService that moved to the server in Steps 2/4.
    // Replaces the client-side calls to IOrderEntryService.Place*Async, ModifyOrderAsync,
    // CancelOrderAsync, and IOrderExecutionService.CancelOrdersBatchAsync.

    [HttpPost("place")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> Place([FromBody] PlaceOrderRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest();
        if (User.GetUserId() is not int caller) return Forbid();
        if (req.UserId != caller) return Forbid();
        var result = req.Type switch
        {
            "LimitBuy"            => await _entry.PlaceLimitBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.Price ?? 0m, req.Currency, ct),
            "LimitSell"           => await _entry.PlaceLimitSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.Price ?? 0m, req.Currency, ct),
            "SlippageMarketBuy"   => await _entry.PlaceSlippageMarketBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.SlippagePct ?? 0m, req.Currency, ct),
            "SlippageMarketSell"  => await _entry.PlaceSlippageMarketSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.SlippagePct ?? 0m, req.Currency, ct),
            "TrueMarketBuy"       => await _entry.PlaceTrueMarketBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.BuyBudget ?? 0m, req.Currency, ct),
            "TrueMarketSell"      => await _entry.PlaceTrueMarketSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.Currency, ct),
            _ => null!
        };
        if (result is null)
        {
            _logger.LogWarning("Place: unknown order type {Type}", req.Type);
            return BadRequest($"Unknown order type: {req.Type}");
        }
        return Ok(result);
    }

    [HttpPost("{id:int}/modify")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> Modify(int id, [FromBody] ModifyOrderRequest req, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        if (req.UserId != caller) return Forbid();
        return Ok(await _entry.ModifyOrderAsync(caller, id, req.Quantity, req.Price, ct));
    }

    [HttpPost("{id:int}/cancel")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> Cancel(int id, [FromQuery] int userId, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        if (userId != caller) return Forbid();
        return Ok(await _entry.CancelOrderAsync(caller, id, ct));
    }

    [HttpPost("cancel-batch")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<IReadOnlyList<OrderResult>>> CancelBatch([FromBody] CancelBatchRequest req, CancellationToken ct)
        => Ok(await _execution.CancelOrdersBatchAsync(req.OrderIds, ct));
}
