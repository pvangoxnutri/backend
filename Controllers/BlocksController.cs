using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Authorize]
public class BlocksController : ControllerBase
{
    private readonly AppDbContext _db;

    public BlocksController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/users/blocked-ids
    [HttpGet("api/users/blocked-ids")]
    public async Task<ActionResult<List<string>>> GetBlockedIds(CancellationToken ct)
    {
        var userId = GetUserId();
        var ids = await _db.UserBlocks
            .Where(b => b.BlockerId == userId)
            .Select(b => b.BlockedUserId.ToString())
            .ToListAsync(ct);
        return Ok(ids);
    }

    // POST /api/users/{targetUserId}/block
    [HttpPost("api/users/{targetUserId:guid}/block")]
    public async Task<ActionResult> BlockUser(Guid targetUserId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == targetUserId) return BadRequest("Cannot block yourself.");

        var existing = await _db.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == userId && b.BlockedUserId == targetUserId, ct);
        if (existing != null) return Ok(new { id = existing.Id });

        var block = new UserBlock { BlockerId = userId, BlockedUserId = targetUserId };
        _db.UserBlocks.Add(block);
        await _db.SaveChangesAsync(ct);
        return Created("", new { id = block.Id });
    }

    // DELETE /api/users/{targetUserId}/block
    [HttpDelete("api/users/{targetUserId:guid}/block")]
    public async Task<ActionResult> UnblockUser(Guid targetUserId, CancellationToken ct)
    {
        var userId = GetUserId();
        var block = await _db.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == userId && b.BlockedUserId == targetUserId, ct);
        if (block == null) return NotFound();

        _db.UserBlocks.Remove(block);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
