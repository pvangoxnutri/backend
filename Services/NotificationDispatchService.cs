using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services;

public interface INotificationDispatchService
{
    Task SendTeaserAsync(TripActivity activity, CancellationToken ct = default);
    Task SendRevealAsync(TripActivity activity, CancellationToken ct = default);
    Task SendActivityAddedAsync(TripActivity activity, Guid actorId, CancellationToken ct = default);
    Task SendTripInviteAsync(TripInvite invite, Guid recipientUserId, CancellationToken ct = default);
    Task SendChatMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task SendMemberJoinedAsync(Trip trip, Guid newMemberId, string newMemberName, CancellationToken ct = default);
    Task SendExpenseAsync(Expense expense, Guid actorId, CancellationToken ct = default);
    Task SendSupportReplyAsync(SupportTicket ticket, CancellationToken ct = default);

    // Scheduler-driven, type-agnostic: retries due attempts and verifies
    // delivery receipts for whatever's outstanding across all notification types.
    Task ProcessPendingAttemptsAsync(CancellationToken ct = default);
    Task CheckReceiptsAsync(CancellationToken ct = default);
}

// Owns "who gets this, what does it say, has it already been claimed, did it
// actually get delivered" for the push notification types SideQuest
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

        await ClaimAndSendAsync("teaser", dedupeKeys, activity.TripId, (_, lang) => PushNotificationTexts.Teaser(lang, activity.Teaser!), data, ct);
    }

    public async Task SendRevealAsync(TripActivity activity, CancellationToken ct = default)
    {
        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, activity.OwnerId, ct);
        if (recipientIds.Count == 0)
        {
            _logger.LogInformation("[NOTIF_DEBUG] sidequest_revealed activity={ActivityId} trip={TripId}: 0 other trip member(s) — nothing to notify.", activity.Id, activity.TripId);
            return;
        }

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"sidequest_revealed:{activity.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "sidequest_revealed",
            ["tripId"] = activity.TripId.ToString(),
            ["activityId"] = activity.Id.ToString(),
            ["route"] = $"/trip/{activity.TripId}/sidequest/{activity.Id}",
        };

        await ClaimAndSendAsync("sidequest_revealed", dedupeKeys, activity.TripId, (_, lang) => PushNotificationTexts.SideQuestRevealed(lang), data, ct);
    }

    public async Task SendActivityAddedAsync(TripActivity activity, Guid actorId, CancellationToken ct = default)
    {
        var recipientIds = await GetTripMemberIdsExcludingAsync(activity.TripId, actorId, ct);
        if (recipientIds.Count == 0)
        {
            _logger.LogInformation("[NOTIF_DEBUG] new_activity/new_hidden_sidequest activity={ActivityId} trip={TripId} actor={ActorId}: 0 other trip member(s) — nothing to notify.", activity.Id, activity.TripId, actorId);
            return;
        }

        // Hidden SideQuests never leak who added them or their title outside
        // the activity's own detail screen — public activities show both.
        var isHidden = activity.Visibility == "hidden";
        var type = isHidden ? "new_hidden_sidequest" : "new_activity";

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"{type}:{activity.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = type,
            ["tripId"] = activity.TripId.ToString(),
            ["route"] = $"/trip/{activity.TripId}",
        };

        string? actorName = null;
        string? actorAvatarUrl = null;
        if (!isHidden)
        {
            var actor = await _db.Users.FindAsync(new object?[] { actorId }, ct);
            actorName = DisplayNameHelper.OrFallback(actor?.Name);
            actorAvatarUrl = actor?.AvatarUrl;
            data["actorName"] = actorName;
            data["activityTitle"] = activity.Title;
        }

        await ClaimAndSendAsync(
            type, dedupeKeys, activity.TripId,
            (_, lang) => isHidden ? PushNotificationTexts.NewHiddenSideQuest(lang) : PushNotificationTexts.NewActivity(lang, actorName!, activity.Title),
            data, ct,
            // Hidden SideQuests must not reveal who added them — no actor,
            // no avatar, the bell falls back to its anonymous lock icon.
            isHidden ? null : (_ => ((Guid?)actorId, actorName, actorAvatarUrl)));
    }

    public async Task SendMemberJoinedAsync(Trip trip, Guid newMemberId, string newMemberName, CancellationToken ct = default)
    {
        var recipientIds = await GetTripMemberIdsExcludingAsync(trip.Id, newMemberId, ct);
        if (recipientIds.Count == 0)
        {
            _logger.LogInformation("[NOTIF_DEBUG] member_joined trip={TripId} newMember={NewMemberId}: 0 other trip member(s) — nothing to notify.", trip.Id, newMemberId);
            return;
        }

        var memberCount = await _db.TripMembers.CountAsync(tm => tm.TripId == trip.Id, ct);
        var newMember = await _db.Users.FindAsync(new object?[] { newMemberId }, ct);

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"member_joined:{trip.Id}:{newMemberId}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "member_joined",
            ["tripId"] = trip.Id.ToString(),
            ["route"] = $"/trip/{trip.Id}",
            ["memberName"] = newMemberName,
            ["tripTitle"] = trip.Title,
            ["memberCount"] = memberCount.ToString(),
        };

        await ClaimAndSendAsync(
            "member_joined", dedupeKeys, trip.Id,
            (_, lang) => PushNotificationTexts.MemberJoined(lang, newMemberName, trip.Title, memberCount),
            data, ct,
            _ => ((Guid?)newMemberId, newMemberName, newMember?.AvatarUrl));
    }

    public async Task SendExpenseAsync(Expense expense, Guid actorId, CancellationToken ct = default)
    {
        // Only the people this expense actually moves money for — payers and
        // participants — ever see a notification about it. Everyone else in
        // the trip is unaffected and shouldn't be pinged.
        var affectedUserIds = expense.Payers.Select(p => p.UserId)
            .Concat(expense.Participants.Select(p => p.UserId))
            .Distinct()
            .Where(id => id != actorId)
            .ToList();
        if (affectedUserIds.Count == 0)
        {
            _logger.LogInformation("[NOTIF_DEBUG] expense expense={ExpenseId} trip={TripId} actor={ActorId}: no other affected payer/participant — nothing to notify.", expense.Id, expense.TripId, actorId);
            return;
        }

        var dedupeKeys = affectedUserIds.ToDictionary(id => id, id => $"expense:{expense.Id}:{id}");
        var amountText = $"{expense.TotalAmount:0.##} {expense.Currency}";
        var data = new Dictionary<string, string>
        {
            ["type"] = "expense",
            ["tripId"] = expense.TripId.ToString(),
            ["route"] = $"/trip/{expense.TripId}/split",
            ["expenseTitle"] = expense.Description,
            ["amount"] = amountText,
        };

        await ClaimAndSendAsync(
            "expense", dedupeKeys, expense.TripId,
            (_, lang) => PushNotificationTexts.Expense(lang, expense.Description, amountText),
            data, ct);
    }

    public async Task SendTripInviteAsync(TripInvite invite, Guid recipientUserId, CancellationToken ct = default)
    {
        var trip = await _db.Trips.FindAsync(new object?[] { invite.TripId }, ct);
        var owner = await _db.Users.FindAsync(new object?[] { invite.InvitedByUserId }, ct);
        var ownerName = DisplayNameHelper.OrFallback(owner?.Name);
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

        await ClaimAndSendAsync("trip_invite", dedupeKeys, invite.TripId, (_, lang) => PushNotificationTexts.TripInvite(lang, ownerName, tripTitle), data, ct);
    }

    public async Task SendChatMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        // TEMPORARY DEBUG (Information-level, local-only edit pending go-ahead
        // to deploy) — pinpoints exactly which stage drops the recipient for a
        // chat push. Remove once the Railway push issue is confirmed fixed.
        if (message.IsSystem || message.UserId == null)
        {
            _logger.LogInformation("[PUSH_DEBUG] chat_message {MessageId}: skipped (system message or no UserId).", message.Id);
            return;
        }

        var memberIds = await GetTripMemberIdsExcludingAsync(message.TripId, message.UserId.Value, ct);
        if (memberIds.Count == 0)
        {
            _logger.LogInformation("[PUSH_DEBUG] chat_message {MessageId}: 0 other trip members — nothing to notify.", message.Id);
            return;
        }

        // Don't push to anyone who currently has this exact trip chat open —
        // they'll see the message arrive live via the chat's own polling.
        var openChatCutoff = DateTime.UtcNow - ChatOpenWindow;
        var currentlyOpenUserIds = await _db.ChatPresence
            .Where(cp => cp.TripId == message.TripId && cp.LastSeenAt >= openChatCutoff)
            .Select(cp => cp.UserId)
            .ToListAsync(ct);

        var recipientIds = memberIds.Except(currentlyOpenUserIds).ToList();
        _logger.LogInformation(
            "[PUSH_DEBUG] chat_message {MessageId}: {MemberCount} other member(s), {OpenCount} excluded as 'chat open' (cutoff {Cutoff:O}), {RecipientCount} recipient(s) left.",
            message.Id, memberIds.Count, currentlyOpenUserIds.Count, openChatCutoff, recipientIds.Count);
        if (recipientIds.Count == 0)
        {
            _logger.LogInformation("[PUSH_DEBUG] chat_message {MessageId}: every other member is currently 'chat open' — no push sent to anyone.", message.Id);
            return;
        }

        // Per-recipient unread count, not the raw message text — chat
        // content is private group conversation, not something to surface in
        // a notification banner. "Unread" = other members' messages newer
        // than this recipient's last chat presence heartbeat (their best
        // proxy for "last time they had this chat open").
        var lastSeenByUserId = await _db.ChatPresence
            .Where(cp => cp.TripId == message.TripId && recipientIds.Contains(cp.UserId))
            .Select(cp => new { cp.UserId, cp.LastSeenAt })
            .ToDictionaryAsync(cp => cp.UserId, cp => cp.LastSeenAt, ct);

        var unreadCountsByUserId = new Dictionary<Guid, int>();
        foreach (var recipientId in recipientIds)
        {
            var since = lastSeenByUserId.TryGetValue(recipientId, out var lastSeen) ? lastSeen : DateTime.MinValue;
            unreadCountsByUserId[recipientId] = await _db.ChatMessages
                .CountAsync(m => m.TripId == message.TripId && !m.IsSystem && m.UserId != recipientId && m.CreatedAt > since, ct);
        }

        var sender = await _db.Users.FindAsync(new object?[] { message.UserId.Value }, ct);

        var dedupeKeys = recipientIds.ToDictionary(id => id, id => $"chat:{message.Id}:{id}");
        var data = new Dictionary<string, string>
        {
            ["type"] = "chat",
            ["tripId"] = message.TripId.ToString(),
            ["route"] = $"/trip/{message.TripId}?openChat=1",
        };

        await ClaimAndSendAsync(
            "chat",
            dedupeKeys,
            message.TripId,
            (userId, lang) => PushNotificationTexts.Chat(lang, message.UserName, unreadCountsByUserId.TryGetValue(userId, out var count) ? count : 1),
            data,
            ct,
            // Naming the sender only makes sense while they're the only
            // unread message — once there's more than one, the bell shows an
            // aggregated count instead, so no single avatar applies either.
            userId => unreadCountsByUserId.TryGetValue(userId, out var count) && count <= 1
                ? (message.UserId, message.UserName, sender?.AvatarUrl)
                : ((Guid?)null, (string?)null, (string?)null),
            userId =>
            {
                var cnt = unreadCountsByUserId.TryGetValue(userId, out var c) ? c : 1;
                return new Dictionary<string, string>
                {
                    ["senderName"] = message.UserName,
                    ["count"] = cnt.ToString(),
                };
            });
    }

    public async Task SendSupportReplyAsync(SupportTicket ticket, CancellationToken ct = default)
    {
        var dedupeKey = $"support_reply:{ticket.Id}:{DateTime.UtcNow:yyyyMMddHHmm}";
        var dedupeKeys = new Dictionary<Guid, string> { [ticket.UserId] = dedupeKey };
        var data = new Dictionary<string, string>
        {
            ["type"] = "support_reply",
            ["ticketId"] = ticket.Id.ToString(),
            ["route"] = $"/support/{ticket.Id}",
        };

        await ClaimAndSendAsync("support_reply", dedupeKeys, null,
            (_, lang) => PushNotificationTexts.SupportReply(lang), data, ct);
    }

    // ── Claim (idempotent) + create per-token attempts + first send ────────

    private async Task ClaimAndSendAsync(
        string type, Dictionary<Guid, string> dedupeKeysByUserId, Guid? tripId,
        Func<Guid, string, (string Title, string Body)> textBuilder, Dictionary<string, string> data, CancellationToken ct,
        Func<Guid, (Guid? ActorId, string? ActorName, string? ActorAvatarUrl)>? actorResolver = null,
        Func<Guid, Dictionary<string, string>?>? perRecipientExtraData = null)
    {
        // NotificationLog is also the in-app notification center's only data
        // source (see NotificationsController) — it must be claimed
        // regardless of Push:Enabled. Only the actual Expo send (further
        // below) is gated by that flag; a disabled push config must never
        // mean "no record of this notification exists at all".
        _logger.LogInformation(
            "[NOTIF_DEBUG] {Type} dispatch starting for recipient(s) [{RecipientIds}] (Push:Enabled={Enabled}).",
            type, string.Join(", ", dedupeKeysByUserId.Keys), _enabled);

        var userIds = dedupeKeysByUserId.Keys.ToList();
        var languagesByUserId = await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Language })
            .ToDictionaryAsync(u => u.Id, u => u.Language, ct);

        var baseDataJson = JsonSerializer.Serialize(data);
        var claimedLogs = new List<NotificationLog>();

        foreach (var (userId, dedupeKey) in dedupeKeysByUserId)
        {
            var language = languagesByUserId.TryGetValue(userId, out var lang) ? lang : "en";
            var (title, body) = textBuilder(userId, language);
            var (actorId, actorName, actorAvatarUrl) = actorResolver?.Invoke(userId) ?? (null, null, null);

            var extra = perRecipientExtraData?.Invoke(userId);
            var dataJson = extra != null
                ? JsonSerializer.Serialize(data.Concat(extra).ToDictionary(kv => kv.Key, kv => kv.Value))
                : baseDataJson;

            var log = new NotificationLog
            {
                Type = type,
                DedupeKey = dedupeKey,
                RecipientUserId = userId,
                TripId = tripId,
                Title = title,
                Body = body,
                DataJson = dataJson,
                ActorName = actorName,
                ActorAvatarUrl = actorAvatarUrl,
                ActorId = actorId,
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

        _logger.LogInformation(
            "[NOTIF_DEBUG] {Type}: inserted {ClaimedCount}/{AttemptedCount} NotificationLog row(s) (the gap is dedupe — already claimed for that recipient).",
            type, claimedLogs.Count, dedupeKeysByUserId.Count);

        if (claimedLogs.Count == 0) return;

        if (!_enabled)
        {
            // TEMPORARY DEBUG: was LogDebug, invisible at the app's default
            // "Information" log level — bumped so Railway logs actually show
            // this, the single most likely reason push goes silent. Revert
            // to LogDebug once the Railway push issue is confirmed fixed.
            _logger.LogInformation("[PUSH_DEBUG] Push:Enabled=false — {Type} claimed ({Count} NotificationLog row(s)) but skipping actual Expo send.", type, claimedLogs.Count);
            return;
        }

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
        // TEMPORARY DEBUG: this case had no logging at all before — a
        // recipient with no active token failed permanently completely
        // silently. Remove once the Railway push issue is confirmed fixed.
        _logger.LogInformation("[PUSH_DEBUG] CreateAttemptsAsync: {LogCount} claimed log(s), {TokenCount} active token(s) found across {UserCount} recipient(s).", claimedLogs.Count, tokens.Count, userIds.Count);

        var attempts = new List<PushDeliveryAttempt>();
        foreach (var log in claimedLogs)
        {
            if (!tokensByUser.TryGetValue(log.RecipientUserId, out var userTokens) || userTokens.Count == 0)
            {
                // No registered device — most commonly because the user
                // hasn't granted permission yet. Terminal: nothing to retry.
                _logger.LogInformation("[PUSH_DEBUG] Recipient {UserId} has NO active push token — log {LogId} marked failed_permanent, no Expo call made.", log.RecipientUserId, log.Id);
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
        // TEMPORARY DEBUG: per-token result straight from Expo's API — the
        // token itself is masked (never log a full push token: it's a live
        // credential anyone could use to spam that device). Remove once the
        // Railway push issue is confirmed fixed.
        _logger.LogInformation(
            "[PUSH_DEBUG] Expo SendAsync: {Results}",
            string.Join("; ", results.Select(r => $"{MaskPushToken(r.To)}={(r.Success ? "ok ticket=" + r.TicketId : "FAILED " + r.ErrorCode + " " + r.ErrorMessage)}")));
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

    // TEMPORARY DEBUG helper — never log a full Expo push token (it's a live
    // credential anyone could use to send that device fake notifications).
    // Keeps just enough of the prefix to distinguish entries in a log line.
    // Remove together with the [PUSH_DEBUG] call sites once done.
    private static string MaskPushToken(string token)
        => token.Length <= 16 ? "***" : $"{token[..16]}…(masked)";
}
