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
    private readonly ISupabaseStorageService _storage;
    private readonly IChatImageStorageService _chatImages;
    private readonly ILogger<TripChatController> _logger;

    public TripChatController(
        AppDbContext db,
        LinkPreviewService linkPreviewService,
        INotificationDispatchService notifications,
        ISupabaseStorageService storage,
        IChatImageStorageService chatImages,
        ILogger<TripChatController> logger)
    {
        _db = db;
        _linkPreviewService = linkPreviewService;
        _notifications = notifications;
        _storage = storage;
        _chatImages = chatImages;
        _logger = logger;
    }

    // Same ceiling as the public image endpoint — this change is about where
    // chat photos live, not about what the app accepts.
    private const long MaxImageBytes = 10L * 1024L * 1024L; // 10 MB

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMember(Guid tripId, Guid userId, CancellationToken ct = default)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct);

    // The only place a stored ImageUrl is turned into something a client sees.
    // Legacy rows still hold a public URL and keep rendering as before; private
    // references are replaced by a flag, so no object path ever leaves the API.
    private static (string? ImageUrl, bool HasPrivateImage) MapImage(string? stored)
        => ChatImageReference.IsPrivate(stored)
            ? (null, true)
            : (stored, false);

    // Reactions accept any emoji the client's keyboard produces (the six
    // quick reactions PLUS the "+" keyboard picker). We don't whitelist a
    // fixed set, but we DO guard against arbitrary text/markup being stored:
    // must be short, non-empty, contain a pictographic char, and carry no
    // letters/digits/whitespace (which a real emoji grapheme never does).
    private static bool IsValidReactionEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji) || emoji.Length > 16) return false;
        if (emoji.Any(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))) return false;
        // At least one astral-plane / symbol char — rules out pure ASCII
        // punctuation like ":)" while allowing ZWJ / variation-selector /
        // skin-tone sequences.
        return emoji.Any(c => char.IsSurrogate(c) || c > 0x2000);
    }

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

        // Deletion tombstones only matter on the polling path (they tell an
        // open chat to drop a message) — a fresh load should never see them.
        if (string.IsNullOrEmpty(since))
            query = query.Where(m => m.SystemEventType != "message_deleted");

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
            var (imageUrl, hasPrivateImage) = MapImage(m.ImageUrl);
            result.Add(new ChatMessageDto
            {
                Id = m.Id,
                UserId = m.UserId,
                UserName = m.UserName,
                Text = m.Text,
                ImageUrl = imageUrl,
                HasPrivateImage = hasPrivateImage,
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

        // A private reference must never arrive in the request body. This
        // endpoint stores ImageUrl verbatim, so without this check a member of
        // trip A could post a message carrying trip B's object reference and
        // then read it back through the access endpoint — which only verifies
        // that the caller belongs to the message's trip. Private references are
        // minted exclusively by SendImageMessage below, after a membership
        // check, from a trip id the server itself supplied.
        if (ChatImageReference.IsPrivate(imageUrl))
            return BadRequest("Image references cannot be supplied directly.");

        // Legacy public URLs are still accepted so app builds predating private
        // chat storage keep working. Anything that is neither is rejected
        // rather than stored as an unrenderable value.
        if (imageUrl != null && !ChatImageReference.IsLegacyPublicUrl(imageUrl))
            return BadRequest("Unsupported image reference.");

        if (string.IsNullOrEmpty(text) && imageUrl == null)
            return BadRequest("Message must include text or an image.");

        var user = await _db.Users.FindAsync([userId], ct);

        var msg = new ChatMessage
        {
            TripId = tripId,
            UserId = userId,
            UserName = DisplayNameHelper.OrFallback(user?.Name),
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

        var (mappedImageUrl, hasPrivateImage) = MapImage(msg.ImageUrl);

        return Ok(new ChatMessageDto
        {
            Id = msg.Id,
            UserId = msg.UserId,
            UserName = msg.UserName,
            Text = msg.Text,
            ImageUrl = mappedImageUrl,
            HasPrivateImage = hasPrivateImage,
            IsSystem = msg.IsSystem,
            CreatedAt = msg.CreatedAt,
            LinkPreview = linkPreview,
        });
    }

    // ── POST /api/trips/{tripId}/chat/image ───────────────────────────────────
    // Sends a message WITH a photo. Upload and message creation are one call on
    // purpose: a separate "upload, get a reference, then post it" flow leaves a
    // window where an uploaded object belongs to no message, and it would need
    // the reference to travel through the client — which is exactly what the
    // private scheme exists to avoid.
    //
    // Nothing here trusts the caller beyond their JWT: the user id comes from
    // the token, the trip id from the route is checked against TripMembers
    // before a single byte is stored, and the object path is composed
    // server-side from that verified trip id.

    [HttpPost("image")]
    [RequestSizeLimit(MaxImageBytes + (1L * 1024L * 1024L))]
    public async Task<ActionResult<ChatMessageDto>> SendImageMessage(
        Guid tripId,
        [FromForm] IFormFile file,
        [FromForm] string? text,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        if (file == null || file.Length == 0)
            return BadRequest("No image uploaded.");
        if (file.Length > MaxImageBytes)
            return BadRequest("Image must be smaller than 10 MB.");

        await using var stream = file.OpenReadStream();

        // Content decides the type, not the client's Content-Type header or
        // filename. The detected extension is what goes into the object path,
        // so the stored name is entirely server-derived.
        var detected = await ImageFileValidator.DetectAsync(stream, ct);
        if (detected == null)
            return BadRequest("Only JPEG, PNG, GIF and WebP images are allowed.");

        if (!stream.CanSeek)
            return BadRequest("The upload could not be processed. Please try again.");
        stream.Position = 0;

        var trimmedText = text?.Trim() ?? string.Empty;
        var objectPath = ChatImageReference.BuildObjectPath(tripId, detected.Extension);

        var uploaded = false;
        var msg = new ChatMessage
        {
            TripId = tripId,
            UserId = userId,
            UserName = DisplayNameHelper.OrFallback((await _db.Users.FindAsync([userId], ct))?.Name),
            Text = trimmedText,
            ImageUrl = ChatImageReference.Scheme + objectPath,
            IsSystem = false,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            await _chatImages.UploadAsync(stream, detected.ContentType, objectPath, ct);
            uploaded = true;

            _db.ChatMessages.Add(msg);
            await _db.SaveChangesAsync(ct);
        }
        catch (ChatImageStorageException)
        {
            // Upload failed, so no message is created at all — a message row
            // pointing at bytes that were never stored would render as a
            // permanently broken image with no way to retry.
            return StatusCode(StatusCodes.Status502BadGateway, "Image upload failed. Please try again.");
        }
        catch (OperationCanceledException)
        {
            await RollBackUploadedImageAsync(objectPath, uploaded);
            throw;
        }
        catch (Exception ex)
        {
            // The bytes made it but the row did not. Remove the object so it
            // does not become an orphan nothing in the database points at.
            await RollBackUploadedImageAsync(objectPath, uploaded);
            _logger.LogError(ex, "Chat image message could not be saved after upload.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Message could not be sent. Please try again.");
        }

        try
        {
            await _notifications.SendChatMessageAsync(msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch chat message push for message {MessageId}.", msg.Id);
        }

        return Ok(new ChatMessageDto
        {
            Id = msg.Id,
            UserId = msg.UserId,
            UserName = msg.UserName,
            Text = msg.Text,
            ImageUrl = null,
            HasPrivateImage = true,
            IsSystem = msg.IsSystem,
            CreatedAt = msg.CreatedAt,
            LinkPreview = await GetLinkPreviewAsync(msg.Text, ct),
        });
    }

    private async Task RollBackUploadedImageAsync(string objectPath, bool uploaded)
    {
        if (!uploaded) return;
        // CancellationToken.None: this runs on the failure path, often because
        // the request was cancelled, and the cleanup still has to happen.
        if (!await _chatImages.DeleteAsync(objectPath, CancellationToken.None))
        {
            // No path, no URL, no key — a failed rollback is worth knowing
            // about, its target is not worth writing down.
            _logger.LogError("Chat image upload rollback could not remove its object.");
        }
    }

    // ── GET /api/trips/{tripId}/chat/{messageId}/image ────────────────────────
    // Trades a message id for a short-lived signed URL. The message id is the
    // access key precisely because it is already access-controlled: it is only
    // useful to someone who can prove membership of the trip that owns it.
    //
    // Every failure — unknown message, wrong trip, no image, non-member —
    // answers 404. A 403 on "wrong trip" would confirm the message exists.

    [HttpGet("{messageId}/image")]
    public async Task<ActionResult<ChatImageAccessDto>> GetImageAccess(
        Guid tripId,
        Guid messageId,
        CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";

        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return NotFound();

        var message = await _db.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.TripId == tripId, ct);
        if (message == null) return NotFound();

        // The trip id is passed in so the reference has to name the SAME trip
        // the message belongs to. Membership alone is not enough: it would let
        // any reference that ever reached a row in this trip be signed.
        if (!ChatImageReference.TryGetObjectPath(message.ImageUrl, tripId, out var objectPath))
            return NotFound();

        try
        {
            var signed = await _chatImages.CreateSignedReadUrlAsync(objectPath, ct);
            return Ok(new ChatImageAccessDto
            {
                Url = signed.Url,
                ExpiresAt = signed.ExpiresAt,
            });
        }
        catch (ChatImageStorageException)
        {
            // Status only — the signed URL and the object path stay out of
            // logs and out of the response.
            _logger.LogWarning("Chat image signing failed for a message in trip {TripId}.", tripId);
            return StatusCode(StatusCodes.Status502BadGateway, "Image is temporarily unavailable.");
        }
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
        if (!IsValidReactionEmoji(emoji))
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

    // ── DELETE /api/trips/{tripId}/chat/{messageId} ───────────────────────────
    // Only the sender can delete their own message (never system messages).
    // The row becomes its own tombstone: content stripped, marked
    // "message_deleted" and CreatedAt bumped to NOW so it rides the existing
    // `since` polling cursor — other open chats pick it up on the next poll
    // and drop the message by id. Initial loads filter tombstones out, and
    // the retention scheduler ages them out like any other chat row.

    [HttpDelete("{messageId}")]
    public async Task<ActionResult> DeleteMessage(Guid tripId, Guid messageId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var message = await _db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.TripId == tripId, ct);
        if (message == null) return NotFound();
        if (message.IsSystem || message.UserId != userId) return Forbid();

        // Captured before the row is blanked. Clearing ImageUrl alone only hid
        // the photo in-app: the bytes stayed in storage, and once the column
        // was null nothing in the database pointed at the file any more, so no
        // later cleanup could find it either. Deleting the message has to
        // delete the bytes.
        var imageRef = message.ImageUrl;

        message.Text = string.Empty;
        message.ImageUrl = null;
        message.IsSystem = true;
        message.SystemEventType = "message_deleted";
        message.CreatedAt = DateTime.UtcNow;

        // Reactions used to cascade with the hard delete — remove them
        // explicitly now so they don't linger on the tombstone.
        var reactions = await _db.ChatMessageReactions
            .Where(r => r.ChatMessageId == messageId)
            .ToListAsync(ct);
        _db.ChatMessageReactions.RemoveRange(reactions);

        await _db.SaveChangesAsync(ct);

        // After the commit, and best-effort. The tombstone is what the user
        // asked for; a storage hiccup must not fail the delete or leave the
        // message standing in anyone's UI. Which bucket to talk to is decided
        // by the stored reference, not by when the message was sent.
        if (ChatImageReference.TryGetObjectPathForCleanup(imageRef, out var privatePath))
        {
            if (!await _chatImages.DeleteAsync(privatePath, ct))
            {
                // Status only — never the path.
                _logger.LogWarning("Private chat image could not be deleted for a message in trip {TripId}.", tripId);
            }
        }
        else
        {
            // Legacy public object. Non-bucket URLs are ignored by the service.
            await _storage.DeleteByUrlAsync(imageRef, ct);
        }

        return NoContent();
    }

    // ── PUT /api/trips/{tripId}/chat/presence ─────────────────────────────────
    // Heartbeat — call every ~15 s while chat is open. Tracks presence ONLY.
    // The "X joined." system message is deliberately NOT created here: tying
    // it to presence meant every chat reopen/reconnect after a gap re-announced
    // the user, spamming the chat. It is created exactly once per actual
    // membership creation instead (TripsController: JoinByCode, InviteMember,
    // AcceptInvite).

    // How long a typing stamp counts as "still typing". The client refreshes
    // its stamp every ~1.2 s while the user keeps typing and sends an explicit
    // false after ~1.8 s of inactivity — 5 s tolerates a few dropped
    // refreshes yet clears a crashed/offline client's indicator fast. This
    // window is the SAFETY NET only; normal show/hide latency is driven by
    // the explicit start/stop signals plus the receiver's sub-second poll.
    private static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(5);

    [HttpPut("presence")]
    public async Task<ActionResult> UpdatePresence(
        Guid tripId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] UpdateChatPresenceDto? dto,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsMember(tripId, userId, ct)) return Forbid();

        var user = await _db.Users.FindAsync([userId], ct);
        var userName = DisplayNameHelper.OrFallback(user?.Name);
        var now = DateTime.UtcNow;
        // No body (the plain heartbeat) leaves TypingAt untouched.
        DateTime? typingAt = dto?.IsTyping switch
        {
            true => now,
            false => null,
            null => (DateTime?)null,
        };

        var existing = await _db.ChatPresence
            .FirstOrDefaultAsync(cp => cp.TripId == tripId && cp.UserId == userId, ct);

        if (existing == null)
        {
            _db.ChatPresence.Add(new ChatPresenceEntry
            {
                TripId = tripId,
                UserId = userId,
                UserName = userName,
                AvatarUrl = user?.AvatarUrl,
                LastSeenAt = now,
                TypingAt = typingAt,
            });
        }
        else
        {
            existing.LastSeenAt = now;
            existing.UserName = userName;
            existing.AvatarUrl = user?.AvatarUrl;
            if (dto?.IsTyping != null) existing.TypingAt = typingAt;
            // A heartbeat means the chat is on screen again — undo any
            // earlier explicit leave.
            existing.LeftAt = null;
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
        var typingCutoff = DateTime.UtcNow - TypingWindow;
        var presence = await _db.ChatPresence
            .Where(cp => cp.TripId == tripId && cp.LastSeenAt >= cutoff)
            .OrderBy(cp => cp.UserName)
            .Select(cp => new ChatPresenceDto
            {
                UserId = cp.UserId,
                UserName = cp.UserName,
                AvatarUrl = cp.AvatarUrl,
                IsTyping = cp.TypingAt != null && cp.TypingAt >= typingCutoff,
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
            // Keep the entry and stamp BOTH timestamps. LastSeenAt = now is
            // the honest "last read" anchor (they saw everything up to this
            // moment) that the push unread count builds on — it must never
            // be written backwards. LeftAt = now is the explicit "chat no
            // longer on screen" signal (chat closed OR app backgrounded):
            // push suppression requires LeftAt == null, so the user becomes
            // pushable IMMEDIATELY without waiting out the heartbeat
            // window. Leaving the chat also always ends "typing".
            presence.LastSeenAt = DateTime.UtcNow;
            presence.LeftAt = DateTime.UtcNow;
            presence.TypingAt = null;
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
