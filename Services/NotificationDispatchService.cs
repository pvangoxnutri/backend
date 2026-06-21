using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services;

public interface INotificationDispatchService
{
    Task SendTeaserAsync(TripActivity activity, CancellationToken ct = default);
    Task SendRevealAsync(TripActivity activity, CancellationToken ct = default);
    Task SendTripInviteAsync(TripInvite invite, Guid recipientUserId, CancellationToken ct = default);
    Task SendChatMessageAsync(ChatMessage message, CancellationToken ct = default);
}

// Owns "who gets this, what does it say, has it already been sent" for the
// four push notification types SideQuest sends. Idempotency is enforced by
// claiming a unique DedupeKey row in NotificationLog *before* calling Expo —
// the unique index on DedupeKey means a race (two scheduler ticks, a retry)
// can only ever have one winner at the database level.
public class NotificationDispatchService : INotificationDispatchService
{
    // A device whose heartbeat is newer than this is treated as "the chat is
    // open right now" — the mobile heartbeat fires every 15s, so this gives
    // one missed beat of slack before we start pushing to them again.
    private static readonly TimeSpan ChatOpenWindow = TimeSpan.FromSeconds(25);

    private readonly AppDbContext _db;
    private readonly IExpoPushService _pushService;
    private readonly ILogger<NotificationDispatchService> _logger;

    public NotificationDispatchService(AppDbContext db, IExpoPushService pushService, ILogger<NotificationDispatchService> logger)
    {
        _db = db;
        _pushService = pushService;
        _logger = logger;
    }

    public async Task SendTeaserAsync(TripActivity activity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(activity.Teaser)) return;

        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, activity.OwnerId, ct);
        if (recipientIds.Count == 0) return;

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"teaser:{activity.Id}:{id}");
        var claimed = await ClaimDedupeKeysAsync("teaser", dedupeKeys, activity.TripId, ct);
        if (claimed.Count == 0) return;

        await DispatchToUsersAsync(
            claimed,
            title: "A SideQuest is getting closer 👀",
            body: activity.Teaser!,
            data: new Dictionary<string, string>
            {
                ["type"] = "teaser",
                ["tripId"] = activity.TripId.ToString(),
                ["route"] = $"/trip/{activity.TripId}",
            },
            ct: ct);
    }

    public async Task SendRevealAsync(TripActivity activity, CancellationToken ct = default)
    {
        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, activity.OwnerId, ct);
        if (recipientIds.Count == 0) return;

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"reveal:{activity.Id}:{id}");
        var claimed = await ClaimDedupeKeysAsync("reveal", dedupeKeys, activity.TripId, ct);
        if (claimed.Count == 0) return;

        await DispatchToUsersAsync(
            claimed,
            title: "SideQuest unlocked! 🎉",
            body: $"{activity.Title} is revealed",
            data: new Dictionary<string, string>
            {
                ["type"] = "reveal",
                ["tripId"] = activity.TripId.ToString(),
                ["activityId"] = activity.Id.ToString(),
                ["route"] = $"/trip/{activity.TripId}/sidequest/{activity.Id}",
            },
            ct: ct);
    }

    public async Task SendTripInviteAsync(TripInvite invite, Guid recipientUserId, CancellationToken ct = default)
    {
        var dedupeKey = $"trip_invite:{invite.Id}";
        var claimed = await ClaimDedupeKeysAsync("trip_invite", new Dictionary<Guid, string> { [recipientUserId] = dedupeKey }, invite.TripId, ct);
        if (claimed.Count == 0) return;

        var trip = await _db.Trips.FindAsync(new object?[] { invite.TripId }, ct);
        var owner = await _db.Users.FindAsync(new object?[] { invite.InvitedByUserId }, ct);
        var ownerName = owner?.Name ?? "Someone";
        var tripTitle = trip?.Title ?? "an adventure";

        await DispatchToUsersAsync(
            claimed,
            title: $"{ownerName} invited you",
            body: $"Join {tripTitle}",
            data: new Dictionary<string, string>
            {
                ["type"] = "trip_invite",
                ["tripId"] = invite.TripId.ToString(),
                ["inviteId"] = invite.Id.ToString(),
                ["route"] = "/",
            },
            ct: ct);
    }

    public async Task SendChatMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        if (message.IsSystem || message.UserId == null) return; // "X joined."/"X left." are not in scope for push.

        var memberIds = await GetTripMemberIdsExcludingAsync(message.TripId, message.UserId.Value, ct);
        if (memberIds.Count == 0) return;

        // Don't push to anyone who currently has this exact trip chat open —
        // they'll see the message arrive live via the chat's own polling.
        var openChatCutoff = DateTime.UtcNow - ChatOpenWindow;
        var currentlyOpenUserIds = await _db.ChatPresence
            .Where(cp => cp.TripId == message.TripId && cp.LastSeenAt >= openChatCutoff)
            .Select(cp => cp.UserId)
            .ToListAsync(ct);

        var recipientIds = memberIds.Except(currentlyOpenUserIds).ToList();
        if (recipientIds.Count == 0) return;

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"chat_message:{message.Id}:{id}");
        var claimed = await ClaimDedupeKeysAsync("chat_message", dedupeKeys, message.TripId, ct);
        if (claimed.Count == 0) return;

        var trip = await _db.Trips.FindAsync(new object?[] { message.TripId }, ct);
        var tripTitle = trip?.Title ?? "your trip";

        await DispatchToUsersAsync(
            claimed,
            title: $"{message.UserName} sent a message",
            // Deliberately not the raw message text — chat content is
            // private group conversation, not something to surface in a
            // notification banner that might be glanced at by anyone nearby.
            body: $"In {tripTitle}",
            data: new Dictionary<string, string>
            {
                ["type"] = "chat_message",
                ["tripId"] = message.TripId.ToString(),
                ["route"] = $"/trip/{message.TripId}?openChat=1",
            },
            ct: ct);
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────

    private async Task<List<Guid>> GetTripMemberIdsExcludingAsync(Guid tripId, Guid excludeUserId, CancellationToken ct)
        => await _db.TripMembers
            .Where(tm => tm.TripId == tripId && tm.UserId != excludeUserId)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);

    // Inserts one placeholder NotificationLog row per recipient. The unique
    // index on DedupeKey means only the first caller to reach the database
    // for a given key wins — anyone else (a duplicate scheduler tick, a
    // retried request) gets a DbUpdateException here and is silently
    // excluded from the returned set, so they never get a second send.
    private async Task<Dictionary<Guid, NotificationLog>> ClaimDedupeKeysAsync(
        string type, Dictionary<Guid, string> dedupeKeysByUserId, Guid? tripId, CancellationToken ct)
    {
        var claimed = new Dictionary<Guid, NotificationLog>();

        foreach (var (userId, dedupeKey) in dedupeKeysByUserId)
        {
            var log = new NotificationLog
            {
                Type = type,
                DedupeKey = dedupeKey,
                RecipientUserId = userId,
                TripId = tripId,
                Success = false,
                CreatedAt = DateTime.UtcNow,
            };

            _db.NotificationLogs.Add(log);
            try
            {
                await _db.SaveChangesAsync(ct);
                claimed[userId] = log;
            }
            catch (DbUpdateException)
            {
                // Already claimed by another call — detach and move on.
                _db.Entry(log).State = EntityState.Detached;
            }
        }

        return claimed;
    }

    private async Task DispatchToUsersAsync(
        Dictionary<Guid, NotificationLog> claimedLogsByUserId,
        string title,
        string body,
        Dictionary<string, string> data,
        CancellationToken ct)
    {
        var userIds = claimedLogsByUserId.Keys.ToList();
        var tokens = await _db.PushTokens
            .Where(pt => userIds.Contains(pt.UserId) && pt.IsActive)
            .ToListAsync(ct);

        if (tokens.Count == 0)
        {
            // No registered devices for any of these users — mark the claims
            // as failed so they're visible in the log, but there's nothing to
            // send. Not an error: most users simply haven't granted
            // permission yet.
            foreach (var log in claimedLogsByUserId.Values)
            {
                log.Success = false;
                log.ErrorMessage = "No active push token for recipient.";
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        var messages = tokens.Select(t => new ExpoPushMessage(t.Token, title, body, data)).ToList();
        var results = await _pushService.SendAsync(messages, ct);

        var resultsByToken = results.ToDictionary(r => r.To, r => r);
        var successByUserId = new Dictionary<Guid, bool>();
        var errorByUserId = new Dictionary<Guid, string?>();

        foreach (var token in tokens)
        {
            if (!resultsByToken.TryGetValue(token.Token, out var result)) continue;

            if (!result.Success && result.ErrorCode == "DeviceNotRegistered")
            {
                token.IsActive = false;
                _logger.LogInformation("Deactivated stale Expo push token for user {UserId}.", token.UserId);
            }

            // A recipient with multiple devices counts as "delivered" if any
            // one of their tokens succeeded.
            successByUserId[token.UserId] = successByUserId.GetValueOrDefault(token.UserId) || result.Success;
            if (!result.Success) errorByUserId[token.UserId] = result.ErrorMessage;
        }

        foreach (var (userId, log) in claimedLogsByUserId)
        {
            log.Success = successByUserId.GetValueOrDefault(userId);
            log.ErrorMessage = log.Success ? null : errorByUserId.GetValueOrDefault(userId, "No matching push token result.");
        }

        await _db.SaveChangesAsync(ct);
    }
}
