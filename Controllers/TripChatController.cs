using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/trips/{tripId}/chat")]
[Authorize]
public class TripChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LinkPreviewService _linkPreviewService;

    public TripChatController(AppDbContext db, LinkPreviewService linkPreviewService)
    {
        _db = db;
        _linkPreviewService = linkPreviewService;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMember(Guid tripId, Guid userId, CancellationToken ct = default)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct);

    // ── GET /api/trips/{tripId}/chat ──────────────────────────────────────────
    // Returns up to 80 recent messages. Pass ?since=ISO_DATETIME to get only
    // messages newer than that timestamp (used for polling).

    [HttpGet]
    public async Task<ActionResult<List<ChatMessageDto>>> GetMessages(
        Guid tripId,
        [FromQuery] string? since,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var query = _db.ChatMessages.Where(m => m.TripId == tripId);

        if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sinceDate))
            query = query.Where(m => m.CreatedAt > sinceDate);

        var messages = await query
            .OrderBy(m => m.CreatedAt)
            .Take(string.IsNullOrEmpty(since) ? 80 : 200)
            .ToListAsync(ct);

        var result = new List<ChatMessageDto>();
        foreach (var m in messages)
        {
            var linkPreview = await GetLinkPreviewAsync(m.Text, ct);
            result.Add(new ChatMessageDto
            {
                Id = m.Id,
                UserId = m.UserId,
                UserName = m.UserName,
                Text = m.Text,
                ImageUrl = m.ImageUrl,
                IsSystem = m.IsSystem,
                CreatedAt = m.CreatedAt,
                LinkPreview = linkPreview,
            });
        }

        return Ok(result);
    }

    // ── POST /api/trips/{tripId}/chat ─────────────────────────────────────────
    // Send a message.

    [HttpPost]
    public async Task<ActionResult<ChatMessageDto>> SendMessage(
        Guid tripId,
        [FromBody] SendChatMessageDto dto,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var text = dto.Text?.Trim() ?? string.Empty;
        var imageUrl = dto.ImageUrl?.Trim();
        if (string.IsNullOrEmpty(imageUrl)) imageUrl = null;

        if (string.IsNullOrEmpty(text) && imageUrl == null)
            return BadRequest("Message must include text or an image.");

        var user = await _db.Users.FindAsync([userId], ct);

        var msg = new ChatMessage
        {
            TripId = tripId,
            UserId = userId,
            UserName = user?.Name ?? "Someone",
            Text = text,
            ImageUrl = imageUrl,
            IsSystem = false,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ChatMessages.Add(msg);
        await _db.SaveChangesAsync(ct);

        var linkPreview = await GetLinkPreviewAsync(text, ct);

        return Ok(new ChatMessageDto
        {
            Id = msg.Id,
            UserId = msg.UserId,
            UserName = msg.UserName,
            Text = msg.Text,
            ImageUrl = msg.ImageUrl,
            IsSystem = msg.IsSystem,
            CreatedAt = msg.CreatedAt,
            LinkPreview = linkPreview,
        });
    }

    // ── PUT /api/trips/{tripId}/chat/presence ─────────────────────────────────
    // Heartbeat — call every ~15 s while chat is open.
    // If the user was previously offline (> 60 s gap or first time), a system
    // message "X joined the chat." is added automatically.

    [HttpPut("presence")]
    public async Task<ActionResult> UpdatePresence(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var user = await _db.Users.FindAsync([userId], ct);
        var userName = user?.Name ?? "Someone";
        var now = DateTime.UtcNow;

        var existing = await _db.ChatPresence
            .FirstOrDefaultAsync(cp => cp.TripId == tripId && cp.UserId == userId, ct);

        // Only announce "X joined." on a genuine return — first time ever, or
        // after the user has been away longer than this window. Otherwise just
        // reopening the chat (or the 15s heartbeat) would spam the message.
        const int rejoinGapSeconds = 300; // 5 minutes

        var shouldAnnounceJoin = existing == null
            || (now - existing.LastSeenAt).TotalSeconds > rejoinGapSeconds;

        if (existing == null)
        {
            _db.ChatPresence.Add(new ChatPresenceEntry
            {
                TripId = tripId,
                UserId = userId,
                UserName = userName,
                AvatarUrl = user?.AvatarUrl,
                LastSeenAt = now,
            });
        }
        else
        {
            existing.LastSeenAt = now;
            existing.UserName = userName;
            existing.AvatarUrl = user?.AvatarUrl;
        }

        if (shouldAnnounceJoin)
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                TripId = tripId,
                UserId = userId,
                UserName = userName,
                Text = $"{userName} joined.",
                IsSystem = true,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    // ── GET /api/trips/{tripId}/chat/presence ─────────────────────────────────
    // Returns users whose last heartbeat was within the past 60 seconds.

    [HttpGet("presence")]
    public async Task<ActionResult<List<ChatPresenceDto>>> GetPresence(
        Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        var presence = await _db.ChatPresence
            .Where(cp => cp.TripId == tripId && cp.LastSeenAt >= cutoff)
            .OrderBy(cp => cp.UserName)
            .Select(cp => new ChatPresenceDto
            {
                UserId = cp.UserId,
                UserName = cp.UserName,
                AvatarUrl = cp.AvatarUrl,
            })
            .ToListAsync(ct);

        return Ok(presence);
    }

    // ── DELETE /api/trips/{tripId}/chat/presence ──────────────────────────────
    // Called when the user closes the chat. Adds "X left the chat." message.

    [HttpDelete("presence")]
    public async Task<ActionResult> LeavePresence(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        var presence = await _db.ChatPresence
            .FirstOrDefaultAsync(cp => cp.TripId == tripId && cp.UserId == userId, ct);

        if (presence != null)
        {
            // Keep the entry and just stamp the leave time. Removing it made the
            // next open look like a first-time join, which re-announced
            // "X joined." every single time. Keeping LastSeenAt lets the rejoin
            // grace window in UpdatePresence work; the user still drops out of
            // the live presence list after the normal 60s window.
            presence.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return Ok();
    }

    private async Task<LinkPreviewDto?> GetLinkPreviewAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var urlPattern = new Regex(@"https?://[^\s]+");
        var matches = urlPattern.Matches(text);

        if (matches.Count == 0)
            return null;

        var firstUrl = matches[0].Value;
        var preview = await _linkPreviewService.GetPreviewAsync(firstUrl, ct);

        if (preview == null)
            return null;

        return new LinkPreviewDto
        {
            Url = firstUrl,
            Title = preview.Title,
            Description = preview.Description,
            ImageUrl = preview.ImageUrl,
        };
    }
}
