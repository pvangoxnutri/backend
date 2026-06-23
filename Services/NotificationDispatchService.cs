using System.Text.Json;
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

    // Scheduler-driven, type-agnostic: retries due attempts and verifies
    // delivery receipts for whatever's outstanding across all four types.
    Task ProcessPendingAttemptsAsync(CancellationToken ct = default);
    Task CheckReceiptsAsync(CancellationToken ct = default);
}

// Owns "who gets this, what does it say, has it already been claimed, did it
// actually get delivered" for the four push notification types SideQuest
// sends.
//
// Outbox model:
//   NotificationLog       — claimed once per (notification, recipient) via a
//                            unique DedupeKey. This is the "never intentionally
//                            send the same logical notification twice" guarantee.
//                            Holds the rendered payload (Title/Body/DataJson)
//                            so retries don't need to re-derive it.
//   PushDeliveryAttempt   — one per (NotificationLog, PushToken) — a user with
//                            3 devices gets 3 independent attempts. Each one
//                            has its own retry/backoff state, so a dead token
//                            on device A never blocks device B.
//
// A temporary send failure (network blip, 429, 5xx) moves an attempt to
// retry_pending with exponential backoff — it is NOT marked failed, so the
// scheduler's ProcessPendingAttemptsAsync will pick it back up. Only
// DeviceNotRegistered (from either the initial ticket or, more often, the
// later receipt) deactivates that one PushToken; nothing else does.
public class NotificationDispatchService : INotificationDispatchService
{
    // A device whose heartbeat is newer than this is treated as "the chat is
    // open right now" — the mobile heartbeat fires every 15s, so this gives
    // one missed beat of slack before we start pushing to them again.
    private static readonly TimeSpan ChatOpenWindow = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(30);
    private const int MaxAttemptCount = 8; // ~ a few hours of backoff before giving up

    // Expo recommends not checking immediately (give it time to reach
    // APNs/FCM); receipts are only kept for ~a day, after which we assume
    // delivered rather than leaving an attempt stuck forever.
    private static readonly TimeSpan ReceiptCheckMinAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReceiptCheckGiveUpAge = TimeSpan.FromHours(26);

    private readonly AppDbContext _db;
    private readonly IExpoPushService _pushService;
    private readonly ILogger<NotificationDispatchService> _logger;
    private readonly bool _enabled;

    public NotificationDispatchService(AppDbContext db, IExpoPushService pushService, ILogger<NotificationDispatchService> logger, IConfiguration configuration)
    {
        _db = db;
        _pushService = pushService;
        _logger = logger;
        // Same Push:Enabled flag that gates the scheduler in Program.cs — the
        // manual POST /api/push-tokens/test-send endpoint calls
        // IExpoPushService directly and is deliberately NOT behind this flag.
        _enabled = configuration.GetValue<bool>("Push:Enabled");
    }

    // ── Public: claim + immediate first attempt, one per notification type ──

    public async Task SendTeaserAsync(TripActivity activity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(activity.Teaser)) return;

        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, activity.OwnerId, ct);
        if (recipientIds.Count == 0) return;

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"teaser:{activity.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "teaser",
            ["tripId"] = activity.TripId.ToString(),
            ["route"] = $"/trip/{activity.TripId}",
        };

        await ClaimAndSendAsync("teaser", dedupeKeys, activity.TripId, lang => PushNotificationTexts.Teaser(lang, activity.Teaser!), data, ct);
    }

    public async Task SendRevealAsync(TripActivity activity, CancellationToken ct = default)
    {
        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, activity.OwnerId, ct);
        if (recipientIds.Count == 0) return;

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"reveal:{activity.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "reveal",
            ["tripId"] = activity.TripId.ToString(),
            ["activityId"] = activity.Id.ToString(),
            ["route"] = $"/trip/{activity.TripId}/sidequest/{activity.Id}",
        };

        await ClaimAndSendAsync("reveal", dedupeKeys, activity.TripId, lang => PushNotificationTexts.Reveal(lang, activity.Title), data, ct);
    }

    public async Task SendTripInviteAsync(TripInvite invite, Guid recipientUserId, CancellationToken ct = default)
    {
        var trip = await _db.Trips.FindAsync(new object?[] { invite.TripId }, ct);
        var owner = await _db.Users.FindAsync(new object?[] { invite.InvitedByUserId }, ct);
        var ownerName = owner?.Name ?? "Someone";
        var tripTitle = trip?.Title ?? "an adventure";

        var dedupeKeys = new Dictionary<Guid, string> { [recipientUserId] = $"trip_invite:{invite.Id}" };
        var data = new Dictionary<string, string>
        {
            ["type"] = "trip_invite",
            ["tripId"] = invite.TripId.ToString(),
            ["inviteId"] = invite.Id.ToString(),
            // Home tab renders pending invites near the top and highlights
            // the one matching inviteId, so this lands the user on the
            // actual invite rather than just "somewhere on the home screen".
            ["route"] = $"/?inviteId={invite.Id}",
        };

        await ClaimAndSendAsync("trip_invite", dedupeKeys, invite.TripId, lang => PushNotificationTexts.TripInvite(lang, ownerName, tripTitle), data, ct);
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

        var trip = await _db.Trips.FindAsync(new object?[] { message.TripId }, ct);
        var tripTitle = trip?.Title ?? "your trip";

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"chat_message:{message.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "chat_message",
            ["tripId"] = message.TripId.ToString(),
            ["route"] = $"/trip/{message.TripId}?openChat=1",
        };

        await ClaimAndSendAsync(
            "chat_message",
            dedupeKeys,
            message.TripId,
            // Deliberately not the raw message text — chat content is
            // private group conversation, not something to surface in a
            // notification banner.
            lang => PushNotificationTexts.ChatMessage(lang, message.UserName, tripTitle),
            data,
            ct);
    }

    // ── Claim (idempotent) + create per-token attempts + first send ────────

    private async Task ClaimAndSendAsync(
        string type, Dictionary<Guid, string> dedupeKeysByUserId, Guid? tripId,
        Func<string, (string Title, string Body)> textBuilder, Dictionary<string, string> data, CancellationToken ct)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Push notifications disabled (Push:Enabled=false) — skipping {Type} dispatch.", type);
            return;
        }

        var userIds = dedupeKeysByUserId.Keys.ToList();
        var languagesByUserId = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Language })
            .ToDictionaryAsync(u => u.Id, u => u.Language, ct);

        var dataJson = JsonSerializer.Serialize(data);
        var claimedLogs = new List<NotificationLog>();

        foreach (var (userId, dedupeKey) in dedupeKeysByUserId)
        {
            var language = languagesByUserId.TryGetValue(userId, out var lang) ? lang : "en";
            var (title, body) = textBuilder(language);

            var log = new NotificationLog
            {
                Type = type,
                DedupeKey = dedupeKey,
                RecipientUserId = userId,
                TripId = tripId,
                Title = title,
                Body = body,
                DataJson = dataJson,
                Status = "pending",
            };

            _db.NotificationLogs.Add(log);
            try
            {
                await _db.SaveChangesAsync(ct);
                claimedLogs.Add(log);
            }
            catch (DbUpdateException)
            {
                // Already claimed by another call (race or retry) — detach
                // and move on. This is the actual "never send twice" guard.
                _db.Entry(log).State = EntityState.Detached;
            }
        }

        if (claimedLogs.Count == 0) return;

        var attempts = await CreateAttemptsAsync(claimedLogs, ct);
        if (attempts.Count > 0)
        {
            await SendAttemptBatchAsync(attempts, ct);
        }
    }

    private async Task<List<PushDeliveryAttempt>> CreateAttemptsAsync(List<NotificationLog> claimedLogs, CancellationToken ct)
    {
        var userIds = claimedLogs.Select(l => l.RecipientUserId).Distinct().ToList();
        var tokens = await _db.PushTokens.Where(t => userIds.Contains(t.UserId) && t.IsActive).ToListAsync(ct);
        var tokensByUser = tokens.GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var attempts = new List<PushDeliveryAttempt>();
        foreach (var log in claimedLogs)
        {
            if (!tokensByUser.TryGetValue(log.RecipientUserId, out var userTokens) || userTokens.Count == 0)
            {
                // No registered device — most commonly because the user
                // hasn't granted permission yet. Terminal: nothing to retry.
                log.Status = "failed_permanent";
                continue;
            }

            foreach (var token in userTokens)
            {
                var attempt = new PushDeliveryAttempt
                {
                    NotificationLogId = log.Id,
                    PushTokenId = token.Id,
                    Status = "pending",
                };
                _db.PushDeliveryAttempts.Add(attempt);
                attempts.Add(attempt);
            }
        }

        await _db.SaveChangesAsync(ct);
        return attempts;
    }

    // ── Sending a batch of attempts — used for both first-send and retries ──

    private async Task SendAttemptBatchAsync(List<PushDeliveryAttempt> attempts, CancellationToken ct)
    {
        if (attempts.Count == 0) return;

        var attemptIds = attempts.Select(a => a.Id).ToList();
        var withContext = await _db.PushDeliveryAttempts
            .Where(a => attemptIds.Contains(a.Id))
            .Include(a => a.PushToken)
            .Include(a => a.NotificationLog)
            .ToListAsync(ct);

        var messages = withContext.Select(a => new ExpoPushMessage(
            a.PushToken.Token,
            a.NotificationLog.Title,
            a.NotificationLog.Body,
            JsonSerializer.Deserialize<Dictionary<string, string>>(a.NotificationLog.DataJson) ?? new())).ToList();

        var results = await _pushService.SendAsync(messages, ct);
        var resultsByToken = results.ToDictionary(r => r.To, r => r);
        var now = DateTime.UtcNow;

        foreach (var attempt in withContext)
        {
            attempt.AttemptCount += 1;
            attempt.LastAttemptAt = now;

            if (!resultsByToken.TryGetValue(attempt.PushToken.Token, out var result))
            {
                ApplyRetryOrGiveUp(attempt, "no_result", "Expo returned no result for this token.");
                continue;
            }

            if (result.Success)
            {
                attempt.Status = "accepted_by_expo";
                attempt.ExpoTicketId = result.TicketId;
                attempt.NextAttemptAt = null;
                attempt.LastErrorCode = null;
            }
            else if (result.ErrorCode == "DeviceNotRegistered")
            {
                attempt.Status = "failed_permanent";
                attempt.LastErrorCode = result.ErrorCode;
                attempt.PushToken.IsActive = false;
                _logger.LogInformation("Deactivated stale Expo push token {TokenId} for user {UserId} (ticket error).", attempt.PushToken.Id, attempt.PushToken.UserId);
            }
            else if (IsTemporaryError(result.ErrorCode))
            {
                ApplyRetryOrGiveUp(attempt, result.ErrorCode, result.ErrorMessage);
            }
            else
            {
                // A permanent-looking error with no specific handling (e.g.
                // InvalidCredentials, MessageTooBig) — retrying won't fix it.
                attempt.Status = "failed_permanent";
                attempt.LastErrorCode = result.ErrorCode;
            }
        }

        await _db.SaveChangesAsync(ct);
        await RollUpNotificationLogsAsync(withContext.Select(a => a.NotificationLogId).Distinct().ToList(), ct);
    }

    private void ApplyRetryOrGiveUp(PushDeliveryAttempt attempt, string? errorCode, string? errorMessage)
    {
        attempt.LastErrorCode = errorCode;

        if (attempt.AttemptCount >= MaxAttemptCount)
        {
            attempt.Status = "failed_permanent";
            attempt.NextAttemptAt = null;
            _logger.LogWarning(
                "Push delivery attempt {AttemptId} permanently failed after {Count} attempts. Last error: {Error} — {Message}",
                attempt.Id, attempt.AttemptCount, errorCode, errorMessage);
            return;
        }

        attempt.Status = "retry_pending";
        var backoffSeconds = Math.Min(RetryMaxDelay.TotalSeconds, RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt.AttemptCount - 1));
        attempt.NextAttemptAt = DateTime.UtcNow.AddSeconds(backoffSeconds);
    }

    // Errors worth retrying: transport/server-side hiccups. Everything else
    // (bad credentials, malformed token, message too big) won't be fixed by
    // trying again.
    private static bool IsTemporaryError(string? errorCode) => errorCode switch
    {
        "http_retryable" => true, // Expo returned 429 or 5xx
        "exception" => true,      // network-level failure calling Expo
        "no_ticket" => true,
        "MessageRateExceeded" => true,
        _ => false,
    };

    // ── Scheduler-driven, type-agnostic passes ──────────────────────────────

    public async Task ProcessPendingAttemptsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Covers due retries AND attempts that never got a first try (e.g.
        // the process crashed between claim and send).
        var dueIds = await _db.PushDeliveryAttempts
            .Where(a => (a.Status == "pending" || a.Status == "retry_pending")
                && (a.NextAttemptAt == null || a.NextAttemptAt <= now))
            .Select(a => a.Id)
            .Take(200)
            .ToListAsync(ct);

        if (dueIds.Count == 0) return;

        // Atomic claim: if another backend instance's scheduler tick beats us
        // to one of these rows, our WHERE no longer matches it and it's
        // excluded from claimedCount — no double-send across instances.
        var claimedCount = await _db.PushDeliveryAttempts
            .Where(a => dueIds.Contains(a.Id) && (a.Status == "pending" || a.Status == "retry_pending"))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "sending"), ct);

        if (claimedCount == 0) return;

        var claimedAttempts = await _db.PushDeliveryAttempts
            .Where(a => dueIds.Contains(a.Id) && a.Status == "sending")
            .ToListAsync(ct);

        _logger.LogInformation("ProcessPendingAttemptsAsync: sending {Count} due attempt(s).", claimedAttempts.Count);
        await SendAttemptBatchAsync(claimedAttempts, ct);
    }

    public async Task CheckReceiptsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var minAgeCutoff = now - ReceiptCheckMinAge;
        var giveUpCutoff = now - ReceiptCheckGiveUpAge;

        var candidates = await _db.PushDeliveryAttempts
            .Where(a => a.Status == "accepted_by_expo" && a.ExpoTicketId != null && a.LastAttemptAt <= minAgeCutoff)
            .Take(300)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var expired = candidates.Where(a => a.LastAttemptAt <= giveUpCutoff).ToList();
        var checkable = candidates.Except(expired).ToList();

        foreach (var attempt in expired)
        {
            // The ticket was accepted by Expo and we never got a definitive
            // receipt before it aged out — assume delivered rather than
            // leaving it stuck in "accepted_by_expo" forever.
            attempt.Status = "delivered";
            attempt.DeliveredAt = now;
        }

        if (checkable.Count > 0)
        {
            var ticketIds = checkable.Select(a => a.ExpoTicketId!).ToList();
            var receipts = await _pushService.GetReceiptsAsync(ticketIds, ct);
            var receiptsByTicket = receipts.ToDictionary(r => r.TicketId, r => r);

            foreach (var attempt in checkable)
            {
                if (!receiptsByTicket.TryGetValue(attempt.ExpoTicketId!, out var receipt)) continue; // no receipt yet — check again next tick

                if (receipt.Success)
                {
                    attempt.Status = "delivered";
                    attempt.DeliveredAt = now;
                }
                else if (receipt.ErrorCode == "DeviceNotRegistered")
                {
                    attempt.Status = "failed_permanent";
                    attempt.LastErrorCode = receipt.ErrorCode;
                    var token = await _db.PushTokens.FindAsync(new object?[] { attempt.PushTokenId }, ct);
                    if (token != null)
                    {
                        token.IsActive = false;
                        _logger.LogInformation("Deactivated stale Expo push token {TokenId} for user {UserId} (via receipt).", token.Id, token.UserId);
                    }
                }
                else
                {
                    attempt.Status = "failed_permanent";
                    attempt.LastErrorCode = receipt.ErrorCode;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        await RollUpNotificationLogsAsync(candidates.Select(a => a.NotificationLogId).Distinct().ToList(), ct);
    }

    private async Task RollUpNotificationLogsAsync(List<Guid> notificationLogIds, CancellationToken ct)
    {
        if (notificationLogIds.Count == 0) return;

        var logs = await _db.NotificationLogs
            .Where(n => notificationLogIds.Contains(n.Id))
            .Include(n => n.Attempts)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var log in logs)
        {
            if (log.Attempts.Any(a => a.Status == "delivered"))
            {
                log.Status = "delivered";
                log.DeliveredAt ??= log.Attempts.Where(a => a.Status == "delivered" && a.DeliveredAt != null).Select(a => a.DeliveredAt!.Value).DefaultIfEmpty(now).Min();
            }
            else if (log.Attempts.Count > 0 && log.Attempts.All(a => a.Status == "failed_permanent"))
            {
                log.Status = "failed_permanent";
            }
            else
            {
                log.Status = "pending";
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<List<Guid>> GetTripMemberIdsExcludingAsync(Guid tripId, Guid excludeUserId, CancellationToken ct)
        => await _db.TripMembers
            .Where(tm => tm.TripId == tripId && tm.UserId != excludeUserId)
            .Select(tm => tm.UserId)
            .ToListAsync(ct);
}
