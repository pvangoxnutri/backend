using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    // The only notification types the in-app notification center shows.
    // Other types (teaser, trip_invite) are still dispatched as push
    // notifications elsewhere, but aren't logged into this feed.
    private static readonly string[] InAppCenterTypes =
    [
        "member_joined",
        "new_activity",
        "new_hidden_sidequest",
        "sidequest_revealed",
        "chat",
        "expense",
        "support_reply",
        "system_message",
    ];

    private readonly AppDbContext _db;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(AppDbContext db, ILogger<NotificationsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── GET /api/notifications ─────────────────────────────────────────────
    // Returns the current user's most recent in-app notifications.

    [HttpGet]
    public async Task<ActionResult<List<NotificationLogDto>>> GetMyNotifications(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Unfiltered count (no type whitelist) alongside the actual result —
        // if these two numbers diverge a lot, the type whitelist is the
        // culprit; if both are 0, nothing was ever claimed for this user.
        var totalForUser = await _db.NotificationLogs.CountAsync(n => n.RecipientUserId == userId, ct);

        var logs = await _db.NotificationLogs
            .Where(n => n.RecipientUserId == userId && InAppCenterTypes.Contains(n.Type))
            .OrderByDescending(n => n.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        _logger.LogInformation(
            "[NOTIF_DEBUG] GET /api/notifications userId={UserId}: {TotalForUser} NotificationLog row(s) total for this user, {InCenterCount} match the in-app-center type whitelist and are returned.",
            userId, totalForUser, logs.Count);

        var language = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Language)
            .FirstOrDefaultAsync(ct) ?? "en";

        // Actor avatars are resolved live: the stored snapshot is often empty
        // because a new member uploads their picture (in onboarding) *after*
        // e.g. their member_joined notification was claimed.
        var actorIds = logs.Where(n => n.ActorId != null).Select(n => n.ActorId!.Value).Distinct().ToList();
        var actorAvatars = actorIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _db.Users
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.AvatarUrl, ct);

        var notifications = logs.Select(n =>
        {
            var data = ExtractData(n.DataJson);
            var (title, body) = InAppCenterTypes.Contains(n.Type)
                ? RenderText(n.Type, data, language)
                : (n.Title, n.Body);

            return new NotificationLogDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = title,
                Body = body,
                TripId = n.TripId,
                Route = data.TryGetValue("route", out var route) ? route : null,
                ActorName = n.ActorName,
                // Current avatar when the actor is known and still exists
                // (even if they removed their picture — null means initials);
                // the claim-time snapshot only for old rows without ActorId.
                ActorAvatarUrl = n.ActorId != null && actorAvatars.TryGetValue(n.ActorId.Value, out var liveAvatar)
                    ? liveAvatar
                    : n.ActorAvatarUrl,
                CreatedAt = n.CreatedAt,
            };
        });

        return Ok(notifications);
    }

    private static Dictionary<string, string> ExtractData(string dataJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(dataJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static (string Title, string Body) RenderText(string type, Dictionary<string, string> data, string language)
    {
        switch (type)
        {
            case "member_joined":
                return PushNotificationTexts.MemberJoined(
                    language,
                    data.GetValueOrDefault("memberName", ""),
                    data.GetValueOrDefault("tripTitle", ""),
                    int.TryParse(data.GetValueOrDefault("memberCount"), out var mc) ? mc : 0);

            case "new_activity":
                if (!data.ContainsKey("actorName"))
                    return language == "sv" ? ("Ny SideQuest tillagd", "") : ("New SideQuest added", "");
                return PushNotificationTexts.NewActivity(language, data["actorName"], data.GetValueOrDefault("activityTitle", ""));

            case "new_hidden_sidequest":
                return PushNotificationTexts.NewHiddenSideQuest(language);

            case "sidequest_revealed":
                return PushNotificationTexts.SideQuestRevealed(language);

            case "chat":
            {
                var hasSender = data.ContainsKey("senderName");
                var hasCount = int.TryParse(data.GetValueOrDefault("count"), out var cnt);
                if (hasSender || (hasCount && cnt > 1))
                    return PushNotificationTexts.Chat(language, data.GetValueOrDefault("senderName", ""), hasCount ? cnt : 1);
                return language == "sv"
                    ? ("Nya chattmeddelanden", "Tryck för att hoppa in i konversationen.")
                    : ("New chat messages", "Tap to jump into the conversation.");
            }

            case "expense":
                if (!data.ContainsKey("expenseTitle"))
                    return language == "sv" ? ("Ny utgift tillagd", "") : ("New expense added", "");
                return PushNotificationTexts.Expense(language, data["expenseTitle"], data.GetValueOrDefault("amount", ""));

            case "support_reply":
                return PushNotificationTexts.SupportReply(language);

            case "system_message":
                // Title/Body are already set verbatim by the admin — pass through as-is.
                return (data.GetValueOrDefault("title", ""), data.GetValueOrDefault("body", ""));

            default:
                return ("", "");
        }
    }
}
