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
}
