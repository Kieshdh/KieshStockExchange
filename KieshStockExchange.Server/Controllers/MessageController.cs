using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Messages double as the persisted notification inbox (Kind=Fill etc.). Every
// endpoint is already behind the global RequireAuthenticatedUser fallback policy;
// the per-user reads/writes additionally verify the row belongs to the caller's
// claim so one authenticated user can't read or mutate another's inbox.
[Authorize]
[ApiController]
[Route("api/messages")]
public sealed class MessageController : ControllerBase
{
    private readonly IDataBaseService _db;
    public MessageController(IDataBaseService db) => _db = db;

    // NOTE: GetAll / Create / Update / Delete are server-internal/admin surfaces and
    // are not called by the client. Role-gating lands with the Phase-7 admin policy.
    [HttpGet]
    public Task<List<Message>> GetAll(CancellationToken ct) => _db.GetMessagesAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Message>> GetById(int id, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        var m = await _db.GetMessageById(id, ct);
        if (m is null) return NotFound();
        if (m.UserId != caller) return Forbid();
        return Ok(m);
    }

    [HttpGet("by-user/{userId:int}")]
    public async Task<ActionResult<List<Message>>> GetByUserId(int userId, [FromQuery] bool onlyUnread, CancellationToken ct)
    {
        if (User.GetUserId() != userId) return Forbid();
        return Ok(await _db.GetMessagesByUserId(userId, onlyUnread, ct));
    }

    [HttpGet("unread-count/{userId:int}")]
    public async Task<ActionResult<int>> GetUnreadCount(int userId, CancellationToken ct)
    {
        if (User.GetUserId() != userId) return Forbid();
        return Ok(await _db.GetUnreadMessageCount(userId, ct));
    }

    [HttpPost]
    public async Task<ActionResult<Message>> Create([FromBody] Message message, CancellationToken ct)
    { await _db.CreateMessage(message, ct); return Ok(message); }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] Message message, CancellationToken ct)
    { await _db.UpdateMessage(message, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    { await _db.DeleteMessage(new Message { MessageId = id }, ct); return NoContent(); }

    [HttpPost("{id:int}/mark-read")]
    public async Task<ActionResult<bool>> MarkRead(int id, [FromQuery] DateTime? readAtUtc, CancellationToken ct)
    {
        if (User.GetUserId() is not int caller) return Forbid();
        var m = await _db.GetMessageById(id, ct);
        if (m is null) return Ok(false);
        if (m.UserId != caller) return Forbid();
        return Ok(await _db.MarkMessageRead(id, readAtUtc, ct));
    }

    [HttpPost("users/{userId:int}/mark-all-read")]
    public async Task<ActionResult<int>> MarkAllRead(int userId, [FromQuery] DateTime? readAtUtc, CancellationToken ct)
    {
        if (User.GetUserId() != userId) return Forbid();
        return Ok(await _db.MarkAllMessagesRead(userId, readAtUtc, ct));
    }
}
