using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;

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

        var notifications = logs.Select(n => new NotificationLogDto
        {
            Id = n.Id,
            Type = n.Type,
            Title = n.Title,
            Body = n.Body,
            TripId = n.TripId,
            Route = ExtractRoute(n.DataJson),
            ActorName = n.ActorName,
            ActorAvatarUrl = n.ActorAvatarUrl,
            CreatedAt = n.CreatedAt,
        });

        return Ok(notifications);
    }

    private static string? ExtractRoute(string dataJson)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(dataJson);
            return data != null && data.TryGetValue("route", out var route) ? route : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
