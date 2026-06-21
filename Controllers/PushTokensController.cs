using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/push-tokens")]
[Authorize]
public class PushTokensController : ControllerBase
{
    private readonly AppDbContext _db;

    public PushTokensController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ── POST /api/push-tokens ──────────────────────────────────────────────────
    // Registers (or re-activates) an Expo push token for the current user.
    // Safe to call repeatedly — same (user, token) pair just refreshes
    // LastSeenAt instead of duplicating, so app-start "make sure it's
    // registered" calls don't pile up rows.

    [HttpPost]
    public async Task<ActionResult> RegisterToken([FromBody] RegisterPushTokenDto dto)
    {
        var userId = GetUserId();
        var token = dto.Token.Trim();
        if (string.IsNullOrEmpty(token)) return BadRequest("Token is required.");

        var existing = await _db.PushTokens
            .FirstOrDefaultAsync(pt => pt.UserId == userId && pt.Token == token);

        if (existing != null)
        {
            existing.IsActive = true;
            existing.Platform = dto.Platform;
            existing.LastSeenAt = DateTime.UtcNow;
        }
        else
        {
            _db.PushTokens.Add(new PushToken
            {
                UserId = userId,
                Token = token,
                Platform = dto.Platform,
            });
        }

        await _db.SaveChangesAsync();
        return Ok();
    }

    // ── DELETE /api/push-tokens?token=... ─────────────────────────────────────
    // Deactivates a single device's token (used when the user turns push off
    // in-app, or signs out). Tokens are deactivated, not deleted, so the
    // delivery history in NotificationLog stays meaningful.

    [HttpDelete]
    public async Task<ActionResult> DeactivateToken([FromQuery] string token)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(token)) return BadRequest("Token is required.");

        var existing = await _db.PushTokens
            .FirstOrDefaultAsync(pt => pt.UserId == userId && pt.Token == token);

        if (existing == null) return NotFound();

        existing.IsActive = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
