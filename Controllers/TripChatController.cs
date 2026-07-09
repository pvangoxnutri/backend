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
    private readonly INotificationDispatchService _notifications;
    private readonly ILogger<TripChatController> _logger;

    public TripChatController(
        AppDbContext db,
        LinkPreviewService linkPreviewService,
        INotificationDispatchService notifications,
        ILogger<TripChatController> logger)
    {
        _db = db;
        _linkPreviewService = linkPreviewService;
        _notifications = notifications;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMember(Guid tripId, Guid userId, CancellationToken ct = default)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct);

    // The reaction picker's fixed set — anything else is rejected so the
    // chips can never render arbitrary strings sent by a modified client.
    private static readonly HashSet<string> AllowedReactionEmojis =
        ["❤️", "😂", "😮", "😢", "👍", "👎"];

    // Per-emoji summary for a set of messages, in first-reaction order.
    private async Task<Dictionary<Guid, List<ChatReactionSummaryDto>>> GetReactionSummaries(
        List<Guid> messageIds, Guid requesterId, CancellationToken ct)
    {
        var reactions = await _db.ChatMessageReactions
            .Where(r => messageIds.Contains(r.ChatMessageId))
            .ToListAsync(ct);

        return reactions
            .GroupBy(r => r.ChatMessageId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.Emoji)
                    .OrderBy(eg => eg.Min(r => r.CreatedAt))
                    .Select(eg => new ChatReactionSummaryDto
                    {
                        Emoji = eg.Key,
                        Count = eg.Count(),
                        ReactedByMe = eg.Any(r => r.UserId == requesterId),
                    })
                    .ToList());
    }

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

        List<ChatMessage> messages;
        if (string.IsNullOrEmpty(since))
        {
            // Initial load (no cursor yet): take the LATEST 80, not the
            // oldest — OrderBy+Take here would otherwise hand back the very
            // first messages ever sent in trips with more than 80. Re-sort
            // ascending afterwards so the client still renders oldest-first.
            messages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Take(80)
                .ToListAsync(ct);
            messages.Reverse();
        }
        else
        {
            // Polling cursor: everything newer than `since`, oldest-first —
            // there's no "too much history" concern on this path.
            messages = await query
                .OrderBy(m => m.CreatedAt)
                .Take(200)
                .ToListAsync(ct);
        }

        // Users who have blocked the current user — their messages should
        // show as a blocked placeholder on the client side.
        var blockedByIds = await _db.UserBlocks
            .Where(ub => ub.BlockedUserId == userId)
            .Select(ub => ub.BlockerId)
            .ToHashSetAsync(ct);

        var reactionsByMessage = await GetReactionSummaries(
            messages.Select(m => m.Id).ToList(), userId, ct);

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
                SystemEventType = m.SystemEventType,
                CreatedAt = m.CreatedAt,
                LinkPreview = linkPreview,
                IsBlockedByAuthor = m.UserId.HasValue && blockedByIds.Contains(m.UserId.Value),
                Reactions = reactionsByMessage.GetValueOrDefault(m.Id) ?? [],
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

        // Push notification failures must never break sending a chat
        // message — log and move on.
        try
        {
            await _notifications.SendChatMessageAsync(msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch chat message push for message {MessageId}.", msg.Id);
        }

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

    // ── POST /api/trips/{tripId}/chat/{messageId}/reactions ───────────────────
    // Toggles the caller's reaction with the given emoji: adds it if absent,
    // removes it if present. Returns the message's updated reaction summary.

    [HttpPost("{messageId}/reactions")]
    public async Task<ActionResult<List<ChatReactionSummaryDto>>> ToggleReaction(
        Guid tripId,
        Guid messageId,
        [FromBody] ToggleChatReactionDto dto,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var emoji = dto.Emoji?.Trim() ?? string.Empty;
        if (!AllowedReactionEmojis.Contains(emoji))
            return BadRequest("Unsupported reaction emoji.");

        // The message must belong to THIS trip — a valid member of trip A
        // must not be able to react into trip B via a foreign message id.
        var message = await _db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.TripId == tripId, ct);
        if (message == null) return NotFound();
        if (message.IsSystem) return BadRequest("System messages cannot be reacted to.");

        var existing = await _db.ChatMessageReactions.FirstOrDefaultAsync(
            r => r.ChatMessageId == messageId && r.UserId == userId && r.Emoji == emoji, ct);

        if (existing != null)
        {
            _db.ChatMessageReactions.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            _db.ChatMessageReactions.Add(new ChatMessageReaction
            {
                ChatMessageId = messageId,
                UserId = userId,
                Emoji = emoji,
                CreatedAt = DateTime.UtcNow,
            });
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two rapid toggles raced past the FirstOrDefault read — the
                // unique (message, user, emoji) index kept the data correct;
                // fall through and return the current summary.
                _db.ChangeTracker.Clear();
            }
        }

        var summaries = await GetReactionSummaries([messageId], userId, ct);
        return Ok(summaries.GetValueOrDefault(messageId) ?? []);
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
                // Text is an English fallback for app builds older than the
                // SystemEventType field — current clients render their own
                // localized string from SystemEventType + UserName instead.
                Text = $"{userName} joined.",
                IsSystem = true,
                SystemEventType = "member_joined",
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
    // Called when the user closes the chat. Does NOT add a "left" system
    // message — see the comment below on why that was deliberately dropped.

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
