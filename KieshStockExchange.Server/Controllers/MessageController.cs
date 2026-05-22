using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

[ApiController]
[Route("api/messages")]
public sealed class MessageController : ControllerBase
{
    private readonly IDataBaseService _db;
    public MessageController(IDataBaseService db) => _db = db;

    [HttpGet]
    public Task<List<Message>> GetAll(CancellationToken ct) => _db.GetMessagesAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Message>> GetById(int id, CancellationToken ct)
        => await _db.GetMessageById(id, ct) is { } m ? Ok(m) : NotFound();

    [HttpGet("by-user/{userId:int}")]
    public Task<List<Message>> GetByUserId(int userId, [FromQuery] bool onlyUnread, CancellationToken ct)
        => _db.GetMessagesByUserId(userId, onlyUnread, ct);

    [HttpGet("unread-count/{userId:int}")]
    public Task<int> GetUnreadCount(int userId, CancellationToken ct)
        => _db.GetUnreadMessageCount(userId, ct);

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
    public Task<bool> MarkRead(int id, [FromQuery] DateTime? readAtUtc, CancellationToken ct)
        => _db.MarkMessageRead(id, readAtUtc, ct);

    [HttpPost("users/{userId:int}/mark-all-read")]
    public Task<int> MarkAllRead(int userId, [FromQuery] DateTime? readAtUtc, CancellationToken ct)
        => _db.MarkAllMessagesRead(userId, readAtUtc, ct);
}
