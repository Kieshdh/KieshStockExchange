using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.OtherServices;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
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
    private readonly IServerNotificationService _notifications;
    private readonly ILogger<OrderController> _logger;

    public OrderController(IDataBaseService db, IOrderEntryService entry,
        IOrderExecutionService execution, IServerNotificationService notifications,
        ILogger<OrderController> logger)
    {
        _db = db;
        _entry = entry;
        _execution = execution;
        _notifications = notifications;
        _logger = logger;
    }

    // Read endpoints below expose orders across all users (admin tables) or write raw
    // rows bypassing the engine. They carry no per-user authorization of their own, so
    // gate the cross-user ones to admins; per-user reads use an ownership check instead.
    [HttpGet]
    [Authorize(Roles = "admin")]
    public Task<List<Order>> GetAll(CancellationToken ct) => _db.GetOrdersAsync(ct);

    [HttpGet("page")]
    [Authorize(Roles = "admin")]
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
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken ct)
        => await _db.GetOrderById(id, ct) is { } o ? Ok(o) : NotFound();

    [HttpPost("by-ids")]
    [Authorize(Roles = "admin")]
    public Task<List<Order>> GetByIds([FromBody] List<int> ids, CancellationToken ct)
        => _db.GetOrdersByIds(ids, ct);

    [HttpGet("by-user/{userId:int}")]
    public async Task<ActionResult<List<Order>>> GetByUserId(int userId, CancellationToken ct)
    {
        if (!User.CanAccessUser(userId)) return Forbid();
        return Ok(await _db.GetOrdersByUserId(userId, ct));
    }

    [HttpGet("by-stock/{stockId:int}")]
    [Authorize(Roles = "admin")]
    public Task<List<Order>> GetByStockId(int stockId, CancellationToken ct)
        => _db.GetOrdersByStockId(stockId, ct);

    [HttpGet("open-limit/{stockId:int}/{currency}")]
    [Authorize(Roles = "admin")]
    public Task<List<Order>> GetOpenLimit(int stockId, CurrencyType currency, CancellationToken ct)
        => _db.GetOpenLimitOrders(stockId, currency, ct);

    [HttpPost("open-for-users")]
    [Authorize(Roles = "admin")]
    public Task<List<Order>> GetOpenForUsers([FromBody] List<int> userIds, CancellationToken ct)
        => _db.GetOpenOrdersForUsersAsync(userIds, ct);

    // Raw CRUD writes bypass the engine (no validation/matching/reservation), so they must
    // never be reachable by a normal client — admin-only. Order placement goes through
    // /place, /modify, /cancel below.
    [HttpPost]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<Order>> Create([FromBody] Order order, CancellationToken ct)
    { await _db.CreateOrder(order, ct); return Ok(order); }

    [HttpPut]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update([FromBody] Order order, CancellationToken ct)
    { await _db.UpdateOrder(order, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "admin")]
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
        // §3.6 decomposition: map the (Stop, Entry, Side) combination — plus a slippage cap on a
        // market entry — to the matching named entry method. Trailing (Stop == Trailing) is P3.
        var result = (req.Stop, req.Entry, req.Side) switch
        {
            (StopKind.None, EntryType.Limit, OrderSide.Buy)
                => await _entry.PlaceLimitBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.Price ?? 0m, req.Currency, ct),
            (StopKind.None, EntryType.Limit, OrderSide.Sell)
                => await _entry.PlaceLimitSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.Price ?? 0m, req.Currency, ct),
            (StopKind.None, EntryType.Market, OrderSide.Buy)
                => req.SlippagePct.HasValue
                    ? await _entry.PlaceSlippageMarketBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.SlippagePct.Value, req.Currency, ct)
                    : await _entry.PlaceTrueMarketBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.BuyBudget ?? 0m, req.Currency, ct),
            (StopKind.None, EntryType.Market, OrderSide.Sell)
                => req.SlippagePct.HasValue
                    ? await _entry.PlaceSlippageMarketSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.SlippagePct.Value, req.Currency, ct)
                    : await _entry.PlaceTrueMarketSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.Currency, ct),
            (StopKind.Stop, EntryType.Market, OrderSide.Buy)
                => await _entry.PlaceStopMarketBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.StopPrice ?? 0m, req.BuyBudget ?? 0m, req.Currency, ct),
            (StopKind.Stop, EntryType.Market, OrderSide.Sell)
                => await _entry.PlaceStopMarketSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.StopPrice ?? 0m, req.Currency, req.SlippagePct, ct),
            (StopKind.Stop, EntryType.Limit, OrderSide.Buy)
                => await _entry.PlaceStopLimitBuyOrderAsync(req.UserId, req.StockId, req.Quantity, req.StopPrice ?? 0m, req.Price ?? 0m, req.Currency, ct),
            (StopKind.Stop, EntryType.Limit, OrderSide.Sell)
                => await _entry.PlaceStopLimitSellOrderAsync(req.UserId, req.StockId, req.Quantity, req.StopPrice ?? 0m, req.Price ?? 0m, req.Currency, ct),
            _ => null!
        };
        if (result is null)
        {
            _logger.LogWarning("Place: unsupported order ({Side},{Entry},{Stop})", req.Side, req.Entry, req.Stop);
            return BadRequest($"Unsupported order combination: {req.Side}/{req.Entry}/{req.Stop}");
        }

        // Resting/failed placement notification (fills are notified by the engine's
        // OnFillsAsync). Human-gated inside the service; bots produce nothing.
        await _notifications.OnOrderResultAsync(result, caller, ct);
        return Ok(result);
    }

    // §3.6 P4: place a (long) bracket — entry + protective stop-loss + up to 3 take-profit legs.
    [HttpPost("place-bracket")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> PlaceBracket([FromBody] PlaceBracketRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest();
        if (User.GetUserId() is not int caller) return Forbid();
        if (req.UserId != caller) return Forbid();
        var tps = (req.TakeProfits ?? Array.Empty<BracketLeg>())
            .Select(l => (l.Price, l.Quantity)).ToList();
        var result = await _entry.PlaceBracketAsync(req.UserId, req.StockId, req.Quantity, req.Entry,
            req.Currency, req.Price, req.BuyBudget, req.StopPrice, req.StopLimitPrice, req.StopSlippagePct,
            tps, ct);
        await _notifications.OnOrderResultAsync(result, caller, ct);
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

    // §3.6 P3: modify an armed stop's trigger / stop-limit price / quantity (off-book).
    [HttpPost("{id:int}/modify-stop")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> ModifyStop(int id, [FromBody] ModifyStopRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest();
        if (User.GetUserId() is not int caller) return Forbid();
        if (req.UserId != caller) return Forbid();
        return Ok(await _entry.ModifyStopAsync(caller, id, req.Quantity, req.StopPrice, req.LimitPrice, ct));
    }

    [HttpPost("{id:int}/cancel")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<OrderResult>> Cancel(int id, [FromQuery] int userId, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        if (userId != caller) return Forbid();
        return Ok(await _entry.CancelOrderAsync(caller, id, ct));
    }

    // Upper bound on a single user-initiated cancel batch — a person clearing their
    // open orders never needs more, and it caps the ownership lookup below.
    private const int MaxCancelBatch = 500;

    [HttpPost("cancel-batch")]
    [EnableRateLimiting("orders")]
    public async Task<ActionResult<IReadOnlyList<OrderResult>>> CancelBatch([FromBody] CancelBatchRequest req, CancellationToken ct)
    {
        if (req?.OrderIds is null) return BadRequest();
        if (User.GetUserId() is not int caller) return Forbid();
        if (req.OrderIds.Count == 0) return Ok(Array.Empty<OrderResult>());
        if (req.OrderIds.Count > MaxCancelBatch)
            return BadRequest($"Cannot cancel more than {MaxCancelBatch} orders at once.");

        // The engine batch path is shared with the bot pruner and takes no caller, so
        // enforce ownership here: every requested id must be one of the caller's orders.
        // Reject (not silently drop) so a user can't cancel another user's resting
        // orders by guessing ids.
        var ids = req.OrderIds.Distinct().ToList();
        var orders = await _db.GetOrdersByIds(ids, ct);
        if (orders.Count != ids.Count || orders.Any(o => o.UserId != caller))
            return Forbid();

        return Ok(await _execution.CancelOrdersBatchAsync(ids, ct));
    }
}
