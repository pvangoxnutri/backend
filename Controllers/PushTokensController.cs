using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/push-tokens")]
[Authorize]
public class PushTokensController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExpoPushService _pushService;
    private readonly ILogger<PushTokensController> _logger;

    public PushTokensController(AppDbContext db, IExpoPushService pushService, ILogger<PushTokensController> logger)
    {
        _db = db;
        _pushService = pushService;
        _logger = logger;
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

    // ── GET /api/push-tokens/mine ──────────────────────────────────────────────
    // Production smoke test, step 2: confirms the EAS build's registration
    // call actually landed in the database. No raw SQL access needed — just
    // call this with the same bearer token the app uses.

    [HttpGet("mine")]
    public async Task<ActionResult> GetMyTokens()
    {
        var userId = GetUserId();
        var tokens = await _db.PushTokens
            .Where(pt => pt.UserId == userId)
            .OrderByDescending(pt => pt.LastSeenAt)
            .Select(pt => new
            {
                pt.Id,
                pt.Token,
                pt.Platform,
                pt.IsActive,
                pt.CreatedAt,
                pt.LastSeenAt,
            })
            .ToListAsync();

        return Ok(tokens);
    }

    // ── POST /api/push-tokens/test-send ────────────────────────────────────────
    // Production smoke test, step 3: sends one push directly to every active
    // token on the CALLER'S OWN account — bypassing NotificationDispatchService,
    // NotificationLog, PushDeliveryAttempt, and the scheduler entirely. This
    // exists purely to verify the raw "backend → Expo → physical device"
    // path works before trusting the outbox/retry machinery built on top of
    // it. Scoped to the caller's own tokens only — there is no recipient
    // parameter, so this can't be used to push to anyone else.

    [HttpPost("test-send")]
    public async Task<ActionResult> SendTestNotification([FromBody] TestSendDto? dto)
    {
        var userId = GetUserId();
        var tokens = await _db.PushTokens
            .Where(pt => pt.UserId == userId && pt.IsActive)
            .ToListAsync();

        if (tokens.Count == 0)
        {
            return BadRequest("No active push token registered for this account. Open the app, grant notification permission, and call POST /api/push-tokens (or just sign in / join a trip) first.");
        }

        var body = string.IsNullOrWhiteSpace(dto?.Message)
            ? "If you see this, basic push delivery works end-to-end. 🎉"
            : dto!.Message!.Trim();

        var messages = tokens.Select(t => new ExpoPushMessage(
            t.Token,
            "SideQuest test notification",
            body,
            new Dictionary<string, string> { ["type"] = "test", ["route"] = "/" })).ToList();

        var results = await _pushService.SendAsync(messages);

        _logger.LogInformation("Manual test-send for user {UserId}: {Results}", userId,
            string.Join("; ", results.Select(r => $"{r.To}={(r.Success ? "ok" : r.ErrorCode)}")));

        return Ok(results.Select(r => new
        {
            token = r.To,
            success = r.Success,
            ticketId = r.TicketId,
            errorCode = r.ErrorCode,
            errorMessage = r.ErrorMessage,
        }));
    }
}
