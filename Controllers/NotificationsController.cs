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

        var notifications = logs.Select(n =>
        {
            var data = ExtractData(n.DataJson);
            var (title, body) = HasRenderParams(n.Type, data)
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
                ActorAvatarUrl = n.ActorAvatarUrl,
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

    private static bool HasRenderParams(string type, Dictionary<string, string> data) => type switch
    {
        "member_joined" => data.ContainsKey("memberName"),
        "new_activity" => data.ContainsKey("actorName"),
        "new_hidden_sidequest" => true,
        "sidequest_revealed" => true,
        "chat" => data.ContainsKey("senderName"),
        "expense" => data.ContainsKey("expenseTitle"),
        "support_reply" => true,
        _ => false,
    };

    private static (string Title, string Body) RenderText(string type, Dictionary<string, string> data, string language) => type switch
    {
        "member_joined" => PushNotificationTexts.MemberJoined(
            language,
            data.GetValueOrDefault("memberName", ""),
            data.GetValueOrDefault("tripTitle", ""),
            int.TryParse(data.GetValueOrDefault("memberCount"), out var mc) ? mc : 0),
        "new_activity" => PushNotificationTexts.NewActivity(
            language,
            data.GetValueOrDefault("actorName", ""),
            data.GetValueOrDefault("activityTitle", "")),
        "new_hidden_sidequest" => PushNotificationTexts.NewHiddenSideQuest(language),
        "sidequest_revealed" => PushNotificationTexts.SideQuestRevealed(language),
        "chat" => PushNotificationTexts.Chat(
            language,
            data.GetValueOrDefault("senderName", ""),
            int.TryParse(data.GetValueOrDefault("count"), out var cnt) ? cnt : 1),
        "expense" => PushNotificationTexts.Expense(
            language,
            data.GetValueOrDefault("expenseTitle", ""),
            data.GetValueOrDefault("amount", "")),
        "support_reply" => PushNotificationTexts.SupportReply(language),
        _ => ("", ""),
    };
}
