using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TripsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISupabaseStorageService _storage;
    private readonly ILogger<TripsController> _logger;
    private readonly INotificationDispatchService _notifications;
    private readonly IEmailSender _emailSender;

    public TripsController(AppDbContext db, ISupabaseStorageService storage, ILogger<TripsController> logger, INotificationDispatchService notifications, IEmailSender emailSender)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _notifications = notifications;
        _emailSender = emailSender;
    }

    // ── Permission helpers ────────────────────────────────────────────────────

    // Invitation email for addresses without a SideQuest account. The link is
    // the same https://sidequesttravel.app/invite/{code} used by the in-app
    // share sheet, so email invitees get the identical landing-page →
    // store/app → join flow.
    private static (string Subject, string HtmlBody, string TextBody) BuildInviteEmail(Trip trip, string? inviterName)
    {
        var title = string.IsNullOrWhiteSpace(trip.Title) ? "an adventure" : $"\"{trip.Title}\"";
        var inviteUrl = $"https://sidequesttravel.app/invite/{trip.InviteCode}";
        var invitedBy = string.IsNullOrWhiteSpace(inviterName) || inviterName.Contains('@')
            ? "A friend"
            : WebUtility.HtmlEncode(inviterName);
        var htmlTitle = WebUtility.HtmlEncode(title);

        var subject = $"You're invited to join {title} on SideQuest";

        var htmlBody = $@"<div style=""font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#F7F3EC;padding:32px 16px;"">
  <div style=""max-width:440px;margin:0 auto;background:#ffffff;border-radius:24px;padding:36px 28px;text-align:center;"">
    <div style=""font-size:26px;font-weight:900;color:#111217;margin-bottom:20px;"">SideQuest<span style=""color:#ff9cab;"">.</span></div>
    <h1 style=""font-size:24px;font-weight:800;color:#14161d;margin:0 0 10px;"">🌍 You're invited!</h1>
    <p style=""font-size:15px;color:#6B7280;line-height:1.6;margin:0 0 24px;"">{invitedBy} has invited you to join {htmlTitle} on SideQuest — plan trips together, split costs and keep every memory in one place.</p>
    <a href=""{inviteUrl}"" style=""display:inline-block;background:#ff4f74;color:#ffffff;border-radius:24px;padding:14px 32px;font-size:16px;font-weight:700;text-decoration:none;"">Join the adventure</a>
    <p style=""font-size:12px;color:#9AA2AE;margin:24px 0 0;"">Or open this link: <a href=""{inviteUrl}"" style=""color:#ff4f74;"">{inviteUrl}</a></p>
    <p style=""font-size:12px;color:#9AA2AE;margin:16px 0 0;"">Not expecting this? You can safely ignore this email.</p>
  </div>
</div>";

        var textBody = $@"🌍 You're invited!

{(invitedBy == "A friend" ? "A friend" : inviterName)} has invited you to join {title} on SideQuest — plan trips together, split costs and keep every memory in one place.

Join here: {inviteUrl}

Not expecting this? You can safely ignore this email.";

        return (subject, htmlBody, textBody);
    }

    // The chat's "X joined." system message. Created exactly once per actual
    // membership creation (join by code, direct add, accepted invite) — never
    // from presence/heartbeat, which used to re-announce on every chat reopen.
    private void AddMemberJoinedChatMessage(Guid tripId, Guid userId, string userName)
    {
        _db.ChatMessages.Add(new ChatMessage
        {
            TripId = tripId,
            UserId = userId,
            UserName = userName,
            // English fallback for app builds older than the SystemEventType
            // field — current clients render their own localized string.
            Text = $"{userName} joined.",
            IsSystem = true,
            SystemEventType = "member_joined",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static bool IsRevealedNow(Trip trip)
        => trip.RevealAt.HasValue && DateTime.UtcNow >= trip.RevealAt.Value;

    private static bool IsActivityRevealedNow(TripActivity activity)
        => activity.RevealAt.HasValue && DateTime.UtcNow >= activity.RevealAt.Value;

    /// canViewFull: owner, OR public visibility, OR already revealed
    private static bool CanViewFull(Guid userId, Trip trip, List<Guid> ownerIds)
        => ownerIds.Contains(userId)
        || trip.Visibility == "public"
        || IsRevealedNow(trip);

    private static bool CanViewActivityFull(Guid userId, TripActivity activity)
        => activity.OwnerId == userId
        || activity.Visibility == "public"
        || IsActivityRevealedNow(activity);

    private static bool IsTeaserVisibleNow(TripActivity activity)
    {
        if (activity.Visibility != "hidden"
            || string.IsNullOrWhiteSpace(activity.Teaser)
            || !activity.RevealAt.HasValue
            || !activity.TeaserOffsetMinutes.HasValue
            || activity.TeaserOffsetMinutes.Value <= 0)
        {
            return false;
        }

        var teaserStart = activity.RevealAt.Value.AddMinutes(-activity.TeaserOffsetMinutes.Value);
        var now = DateTime.UtcNow;
        return now >= teaserStart && now < activity.RevealAt.Value;
    }

    private static string NormalizeVisibility(string? visibility)
        => visibility == "hidden" ? "hidden" : "public";

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidSpotifyUrl(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized == null)
            return true;

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return uri.Scheme is "http" or "https"
            && (host == "open.spotify.com" || host.EndsWith(".spotify.com") || host == "spotify.link" || host.EndsWith(".spotify.link"));
    }

    // True only for activities that are actually a "SideQuest" — tagged
    // category=sidequest, or hidden (a hidden activity is inherently a
    // SideQuest moment regardless of how its category happened to be set
    // before the SideQuest toggle existed). Mirrors the mobile form's own
    // sideQuestMode detection in components/sidequest-form.tsx.
    private static bool IsSideQuest(TripActivity activity)
        => activity.Category == "sidequest" || activity.Visibility == "hidden";

    // SideQuests are creator-only to edit/delete — they're someone's
    // personal surprise, not a shared plan item. Regular (non-SideQuest)
    // activities keep the old collaborative behavior: any member can edit
    // them when the trip's MembersCanEdit is on, or a trip owner always can.
    private static ActivityResponseDto BuildActivityResponse(Guid userId, TripActivity activity, bool membersCanEditTrip)
    {
        var canViewFull = CanViewActivityFull(userId, activity);
        var teaserVisible = !canViewFull && IsTeaserVisibleNow(activity);
        var canEdit = IsSideQuest(activity) ? activity.OwnerId == userId : (membersCanEditTrip || activity.OwnerId == userId);

        return new ActivityResponseDto
        {
            Id = activity.Id,
            TripId = activity.TripId,
            Date = activity.Date,
            Title = canViewFull ? activity.Title : null,
            Description = canViewFull ? activity.Description : null,
            Time = activity.Time,
            SortIndex = activity.SortIndex,
            Category = activity.Category,
            // Category symbol is always sent (drives the fallback icon even
            // for hidden activities); the custom NAME is treated like Title —
            // withheld until the viewer can see the full activity, so it can't
            // spoil a hidden reveal.
            CustomCategoryLabel = canViewFull ? activity.CustomCategoryLabel : null,
            ImageUrl = activity.ImageUrl,
            SpotifyUrl = activity.SpotifyUrl,
            Visibility = activity.Visibility,
            RevealAt = activity.RevealAt,
            RevealedAt = activity.RevealedAt,
            IsRevealed = IsActivityRevealedNow(activity),
            Teaser = canViewFull || teaserVisible ? activity.Teaser : null,
            TeaserOffsetMinutes = activity.TeaserOffsetMinutes,
            IsHiddenForViewer = !canViewFull,
            TeaserVisible = teaserVisible,
            CanEdit = canEdit,
            IsHidden = activity.Visibility == "hidden",
            OwnerId = activity.OwnerId,
            OwnerName = activity.Owner?.Name,
            OwnerAvatarUrl = activity.Owner?.AvatarUrl,
            AssignedToUserId = activity.AssignedToUserId,
            AssignedToName = activity.AssignedTo?.Name,
            CreatedAt = activity.CreatedAt,
        };
    }

    private static string? ValidateActivityPayload(
        string visibility,
        DateTime? revealAt,
        string? teaser,
        int? teaserOffsetMinutes)
    {
        if (visibility == "hidden" && !revealAt.HasValue)
        {
            return "Hidden SideQuests need a reveal date and time.";
        }

        if (visibility != "hidden" && (revealAt.HasValue || !string.IsNullOrWhiteSpace(teaser) || teaserOffsetMinutes.HasValue))
        {
            return "Reveal and teaser settings are only available for hidden SideQuests.";
        }

        if (!string.IsNullOrWhiteSpace(teaser) && !teaserOffsetMinutes.HasValue)
        {
            return "Choose when the teaser should appear before reveal.";
        }

        if (teaserOffsetMinutes.HasValue && teaserOffsetMinutes.Value <= 0)
        {
            return "Teaser timing must be greater than zero.";
        }

        if (teaserOffsetMinutes.HasValue && !revealAt.HasValue)
        {
            return "Teaser timing needs a reveal date and time.";
        }

        return null;
    }

    private static string? ValidateTripDateRange(DateOnly? startDate, DateOnly? endDate)
    {
        if (startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value)
        {
            return "End date must be the same day or later than start date.";
        }

        return null;
    }

    private static string NormalizeInviteCode(string? inviteCode)
    {
        var normalized = (inviteCode ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty);

        return normalized.Length >= 6 ? normalized[..6] : normalized;
    }

    private static string GenerateInviteCode()
        => Convert.ToHexString(Guid.NewGuid().ToByteArray())[..6];

    /// canAdminOverride: user has admin role in their JWT
    private bool IsAdmin()
        => User.FindFirstValue(ClaimTypes.Role) == "admin";

    private async Task<List<Guid>> GetOwnerIds(Guid tripId)
        => await _db.TripMembers
            .Where(tm => tm.TripId == tripId && tm.IsOwner)
            .Select(tm => tm.UserId)
            .ToListAsync();

    private async Task<bool> IsTripMember(Guid tripId, Guid userId)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId);

    // Picks the SortIndex for an activity being created/edited so that —
    // only when it has a Time — it lands chronologically among same-day
    // siblings that also have a Time. Siblings without a Time (and the
    // relative order between timed siblings, once placed) are left alone, so
    // a later drag-to-reorder fully overrides this; it only fires at the
    // moment a Time is set or changed, never on every load.
    // Shifts existing siblings' SortIndex itself (and saves that) — the
    // caller still needs to save the target activity's own new SortIndex.
    private async Task<int> InsertChronologicallyAsync(Guid tripId, DateOnly date, string? time, Guid? excludeActivityId)
    {
        var siblingsQuery = _db.TripActivities
            .Where(a => a.TripId == tripId && a.Date == date);
        if (excludeActivityId.HasValue)
        {
            siblingsQuery = siblingsQuery.Where(a => a.Id != excludeActivityId.Value);
        }

        if (string.IsNullOrEmpty(time))
        {
            // No time to sort by — same as before, just append to the end.
            return (await siblingsQuery.Select(a => (int?)a.SortIndex).MaxAsync() ?? -1) + 1;
        }

        var siblings = await siblingsQuery.OrderBy(a => a.SortIndex).ToListAsync();

        var insertAt = siblings.Count;
        for (var i = 0; i < siblings.Count; i++)
        {
            if (!string.IsNullOrEmpty(siblings[i].Time) && string.CompareOrdinal(siblings[i].Time, time) > 0)
            {
                insertAt = i;
                break;
            }
        }

        for (var i = insertAt; i < siblings.Count; i++)
        {
            siblings[i].SortIndex = i + 1;
        }
        if (siblings.Count > insertAt)
        {
            await _db.SaveChangesAsync();
        }

        return insertAt;
    }

    // A member may add new activities (or reorder existing ones, or edit/
    // delete an existing non-SideQuest activity) when the owner allows
    // collaborative editing (MembersCanEdit), or when the member is itself a
    // trip owner. SideQuests are the exception — editing/deleting THOSE is
    // always creator-only regardless of this; see IsSideQuest,
    // BuildActivityResponse, UpdateActivity and DeleteActivity.
    private async Task<bool> CanEditActivitiesAsync(Guid tripId, Guid userId)
    {
        if (!await IsTripMember(tripId, userId)) return false;
        var trip = await _db.Trips.FindAsync(tripId);
        if (trip == null) return false;
        if (trip.MembersCanEdit) return true;
        var ownerIds = await GetOwnerIds(tripId);
        return ownerIds.Contains(userId);
    }

    private TripResponseDto BuildResponse(Trip trip, List<Guid> ownerIds, bool canViewFull)
        => new()
        {
            Id = trip.Id,
            Visibility = trip.Visibility,
            RevealAt = trip.RevealAt,
            IsRevealed = IsRevealedNow(trip),
            OwnerIds = ownerIds,
            Teaser = trip.Teaser,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            CreatedAt = trip.CreatedAt,
            OwnerId = trip.OwnerId,
            ImageUrl = trip.ImageUrl,        // always (frontend blurs when hidden)
            SpotifyUrl = trip.SpotifyUrl,
            Destination = trip.Destination,  // always (shown in date/location row)
            Countries = new(),
            InviteCode = trip.InviteCode,
            Title = canViewFull ? trip.Title : null,
            Description = canViewFull ? trip.Description : null,
            ShareCode = canViewFull ? trip.ShareCode : null,
            MembersCanEdit = trip.MembersCanEdit,
        };

    // ── GET /api/trips/invites/me ─────────────────────────────────────────────
    // Returns all pending trip invitations for the currently signed-in user.

    [HttpGet("invites/me")]
    [Authorize]
    public async Task<ActionResult<List<PendingInviteDto>>> GetMyPendingInvites(CancellationToken cancellationToken)
    {
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return Ok(new List<PendingInviteDto>());

        var invites = await _db.TripInvites
            .Where(i => i.Email == email && i.Status == "pending")
            .Include(i => i.Trip)
            .Include(i => i.InvitedByUser)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new PendingInviteDto
            {
                Id = i.Id,
                TripId = i.TripId,
                TripTitle = i.Trip.Title,
                TripDestination = i.Trip.Destination,
                TripImageUrl = i.Trip.ImageUrl,
                InvitedByName = i.InvitedByUser.Name,
                CreatedAt = i.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(invites);
    }

    // ── POST /api/trips/join ──────────────────────────────────────────────────
    // Join a trip by entering its invite code.

    [HttpPost("join")]
    [Authorize]
    public async Task<ActionResult> JoinByCode([FromBody] JoinByCodeDto dto, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var code = dto.Code?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(code))
            return BadRequest("Invite code is required.");

        var trip = await _db.Trips
            .FirstOrDefaultAsync(t => t.InviteCode == code, cancellationToken);

        if (trip == null)
            return NotFound("No adventure found with that invite code. Double-check and try again.");

        var alreadyMember = await _db.TripMembers
            .AnyAsync(m => m.TripId == trip.Id && m.UserId == userId, cancellationToken);

        if (alreadyMember)
            return Conflict("You're already part of this adventure.");

        var user = await _db.Users.FindAsync([userId], cancellationToken);
        var actorName = DisplayNameHelper.OrFallback(user?.Name);

        _db.TripMembers.Add(new TripMember { TripId = trip.Id, UserId = userId, IsOwner = false });

        // Mark any pending email invite for this user on this trip as accepted
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email))
        {
            var pendingInvite = await _db.TripInvites
                .FirstOrDefaultAsync(i => i.TripId == trip.Id && i.Email == email && i.Status == "pending", cancellationToken);
            if (pendingInvite != null)
                pendingInvite.Status = "accepted";
        }

        // Emit member_joined event so existing members are notified
        _db.TripEvents.Add(new TripEvent
        {
            TripId = trip.Id,
            ActorId = userId,
            ActorName = actorName,
            Type = "member_joined",
            CreatedAt = DateTime.UtcNow,
        });

        AddMemberJoinedChatMessage(trip.Id, userId, actorName);

        await _db.SaveChangesAsync(cancellationToken);

        // Push notification — failures must never break joining a trip.
        try
        {
            await _notifications.SendMemberJoinedAsync(trip, userId, actorName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch member_joined push for trip {TripId}.", trip.Id);
        }

        return Ok(new { tripId = trip.Id });
    }

    // ── GET /api/trips/events/me ──────────────────────────────────────────────
    // Returns recent events (e.g. member_joined) for all trips the user belongs to,
    // excluding events where the current user was the actor.

    [HttpGet("events/me")]
    [Authorize]
    public async Task<ActionResult<List<TripEventDto>>> GetMyTripEvents(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var cutoff = DateTime.UtcNow.AddDays(-30);

        var memberTripIds = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Select(tm => tm.TripId)
            .ToListAsync(cancellationToken);

        var events = await _db.TripEvents
            .Where(e => memberTripIds.Contains(e.TripId) && e.ActorId != userId && e.CreatedAt >= cutoff)
            .Include(e => e.Trip)
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .Select(e => new TripEventDto
            {
                Id = e.Id,
                TripId = e.TripId,
                TripTitle = e.Trip.Title,
                ActorName = e.ActorName,
                Type = e.Type,
                ActivityId = e.ActivityId,
                IsHidden = e.IsHidden,
                ActivityTitle = e.ActivityTitle,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Ok(events);
    }

    // ── GET /api/trips ────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<List<TripResponseDto>>> GetMyTrips()
    {
        var total = Stopwatch.StartNew();
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var phase = Stopwatch.StartNew();
        var memberTripIds = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Select(tm => tm.TripId)
            .ToListAsync();
        _logger.LogInformation("[TIMING] GET /api/trips phase=memberTripIds count={Count} elapsedMs={Elapsed}", memberTripIds.Count, phase.ElapsedMilliseconds);

        phase.Restart();
        var trips = await _db.Trips
            .Where(t => memberTripIds.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
        _logger.LogInformation("[TIMING] GET /api/trips phase=trips count={Count} elapsedMs={Elapsed}", trips.Count, phase.ElapsedMilliseconds);

        var tripIds = trips.Select(t => t.Id).ToList();
        phase.Restart();
        var ownerMap = await _db.TripMembers
            .Where(tm => tripIds.Contains(tm.TripId) && tm.IsOwner)
            .GroupBy(tm => tm.TripId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(tm => tm.UserId).ToList());
        _logger.LogInformation("[TIMING] GET /api/trips phase=ownerMap elapsedMs={Elapsed}", phase.ElapsedMilliseconds);

        var result = trips.Select(trip =>
        {
            var owners = ownerMap.GetValueOrDefault(trip.Id, new List<Guid>());
            return BuildResponse(trip, owners, CanViewFull(userId, trip, owners));
        }).ToList();

        _logger.LogInformation("[TIMING] GET /api/trips total elapsedMs={Elapsed}", total.ElapsedMilliseconds);
        return Ok(result);
    }

    // ── POST /api/trips ───────────────────────────────────────────────────────

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<TripResponseDto>> CreateTrip([FromBody] CreateTripDto dto)
    {
        var total = Stopwatch.StartNew();
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = new Trip
        {
            Title = dto.Title,
            Description = dto.Description,
            Destination = dto.Destination,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            ImageUrl = dto.ImageUrl,
            InviteCode = NormalizeInviteCode(dto.InviteCode),
            OwnerId = userId,
            Visibility = dto.Visibility == "hidden" ? "hidden" : "public",
            RevealAt = dto.RevealAt,
            Teaser = dto.Teaser,
        };

        if (string.IsNullOrWhiteSpace(trip.InviteCode))
        {
            trip.InviteCode = GenerateInviteCode();
        }

        _db.Trips.Add(trip);
        // Creator is always an owner
        _db.TripMembers.Add(new TripMember { TripId = trip.Id, UserId = userId, IsOwner = true });

        var phase = Stopwatch.StartNew();
        await _db.SaveChangesAsync();
        _logger.LogInformation("[TIMING] POST /api/trips phase=saveChanges elapsedMs={Elapsed}", phase.ElapsedMilliseconds);

        var ownerIds = new List<Guid> { userId };
        _logger.LogInformation("[TIMING] POST /api/trips total elapsedMs={Elapsed}", total.ElapsedMilliseconds);
        return CreatedAtAction(nameof(GetTrip), new { id = trip.Id },
            BuildResponse(trip, ownerIds, canViewFull: true));
    }

    // ── GET /api/trips/{id} ───────────────────────────────────────────────────

    [HttpGet("{id}")]
    [Authorize]
    public async Task<ActionResult<TripResponseDto>> GetTrip(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var isMember = await IsTripMember(id, userId);
        if (!isMember && trip.Visibility != "public" && !IsRevealedNow(trip))
            return Forbid();

        var ownerIds = await GetOwnerIds(id);
        return Ok(BuildResponse(trip, ownerIds, CanViewFull(userId, trip, ownerIds)));
    }

    // ── PATCH /api/trips/{id} ─────────────────────────────────────────────────

    [HttpPatch("{id}")]
    [Authorize]
    public async Task<ActionResult> UpdateTrip(Guid id, [FromBody] UpdateTripDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        bool isOwner = ownerIds.Contains(userId);
        bool revealed = IsRevealedNow(trip);

        if (!isOwner && !isAdmin) return Forbid();

        var nextStartDate = dto.StartDate ?? trip.StartDate;
        var nextEndDate = dto.EndDate ?? trip.EndDate;
        var tripDateError = ValidateTripDateRange(nextStartDate, nextEndDate);
        if (tripDateError != null) return BadRequest(tripDateError);

        var outOfRangeActivities = await _db.TripActivities
            .Where(activity =>
                activity.TripId == id
                && (activity.Date < nextStartDate || activity.Date > nextEndDate))
            .Select(activity => new { activity.Title, activity.Date })
            .ToListAsync();

        if (outOfRangeActivities.Count > 0)
        {
            var details = string.Join(", ", outOfRangeActivities.Select(a =>
                $"'{a.Title}' ({a.Date:yyyy-MM-dd})"));
            return BadRequest($"These SideQuests fall outside the new trip dates: {details}. Move or delete them first.");
        }

        var previousImageUrl = trip.ImageUrl;

        if (isOwner && !revealed)
        {
            // Owners can edit all fields before reveal
            if (dto.Title != null) trip.Title = dto.Title;
            if (dto.Description != null) trip.Description = dto.Description;
            if (dto.Destination != null) trip.Destination = dto.Destination;
            if (dto.ClearImage) trip.ImageUrl = null;
            else if (dto.ImageUrl != null) trip.ImageUrl = dto.ImageUrl;
            if (dto.ClearSpotifyUrl) trip.SpotifyUrl = null;
            else if (dto.SpotifyUrl != null)
            {
                if (!IsValidSpotifyUrl(dto.SpotifyUrl))
                    return BadRequest("Spotify link must be a valid public Spotify URL.");
                trip.SpotifyUrl = NormalizeOptionalText(dto.SpotifyUrl);
            }
            if (dto.Visibility != null) trip.Visibility = dto.Visibility;
            if (dto.Teaser != null) trip.Teaser = dto.Teaser;
            if (dto.StartDate.HasValue) trip.StartDate = dto.StartDate.Value;
            if (dto.EndDate.HasValue) trip.EndDate = dto.EndDate.Value;
            if (dto.ClearRevealAt) trip.RevealAt = null;
            else if (dto.RevealAt.HasValue) trip.RevealAt = dto.RevealAt.Value;
            if (dto.MembersCanEdit.HasValue) trip.MembersCanEdit = dto.MembersCanEdit.Value;
        }
        else if (isAdmin)
        {
            // Admins can always change dates and reveal_at (but not content fields)
            if (dto.StartDate.HasValue) trip.StartDate = dto.StartDate.Value;
            if (dto.EndDate.HasValue) trip.EndDate = dto.EndDate.Value;
            if (dto.ClearRevealAt) trip.RevealAt = null;
            else if (dto.RevealAt.HasValue) trip.RevealAt = dto.RevealAt.Value;
            if (dto.Visibility != null) trip.Visibility = dto.Visibility;
        }
        else
        {
            // Owner trying to edit after reveal
            return BadRequest("Cannot edit after the SideQuest has been revealed.");
        }

        await _db.SaveChangesAsync();

        // If the cover image was replaced or cleared, remove the old file.
        if (!string.IsNullOrWhiteSpace(previousImageUrl) && previousImageUrl != trip.ImageUrl)
        {
            await _storage.DeleteByUrlAsync(previousImageUrl);
        }

        return NoContent();
    }

    [HttpPatch("{id}/spotify")]
    [Authorize]
    public async Task<ActionResult<TripResponseDto>> UpdateTripSpotify(Guid id, [FromBody] UpdateTripSpotifyDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var nextSpotifyUrl = dto.ClearSpotifyUrl ? null : NormalizeOptionalText(dto.SpotifyUrl);
        if (!IsValidSpotifyUrl(nextSpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        trip.SpotifyUrl = nextSpotifyUrl;
        await _db.SaveChangesAsync();

        var ownerIds = await GetOwnerIds(id);
        return Ok(BuildResponse(trip, ownerIds, CanViewFull(userId, trip, ownerIds)));
    }

    // ── DELETE /api/trips/{id} ────────────────────────────────────────────────

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<ActionResult> DeleteTrip(Guid id, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        // Collect image URLs that will become orphaned by this deletion.
        var images = new List<string?> { trip.ImageUrl };
        images.AddRange(await _db.TripActivities
            .Where(a => a.TripId == id)
            .Select(a => a.ImageUrl)
            .ToListAsync(cancellationToken));
        images.AddRange(await _db.ChatMessages
            .Where(m => m.TripId == id)
            .Select(m => m.ImageUrl)
            .ToListAsync(cancellationToken));

        _db.Trips.Remove(trip);
        await _db.SaveChangesAsync(cancellationToken);

        // Best-effort: never block the user-facing delete on storage cleanup.
        await _storage.DeleteManyByUrlAsync(images, cancellationToken);

        return NoContent();
    }

    // ── GET /api/trips/{id}/members ───────────────────────────────────────────

    [HttpGet("{id}/members")]
    [Authorize]
    public async Task<ActionResult<List<TripMemberDto>>> GetTripMembers(Guid id)
    {
        if (!await _db.Trips.AnyAsync(t => t.Id == id))
            return NotFound();

        var members = await _db.TripMembers
            .Where(tm => tm.TripId == id)
            .Select(tm => new TripMemberDto
            {
                Id = tm.User.Id,
                Name = tm.User.Name,
                AvatarUrl = tm.User.AvatarUrl,
                IsOwner = tm.IsOwner
            })
            .ToListAsync();

        return Ok(members);
    }

    // ── POST /api/trips/{id}/invite ───────────────────────────────────────────

    [HttpGet("{id}/invites")]
    [Authorize]
    public async Task<ActionResult<List<TripInviteDto>>> GetTripInvites(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.Trips.AnyAsync(t => t.Id == id))
            return NotFound();

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var invites = await _db.TripInvites
            .Where(ti => ti.TripId == id && ti.Status == "pending")
            .OrderBy(ti => ti.CreatedAt)
            .Select(ti => new TripInviteDto
            {
                Id = ti.Id,
                Email = ti.Email,
                Status = ti.Status,
                CreatedAt = ti.CreatedAt
            })
            .ToListAsync();

        return Ok(invites);
    }

    [HttpPost("{id}/invites")]
    [Authorize]
    public async Task<ActionResult<TripInviteDto>> CreateTripInvite(Guid id, [FromBody] CreateTripInviteDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId)) return Forbid();

        var normalizedEmail = dto.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return BadRequest("Email is required.");

        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
        if (existingUser != null && await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == existingUser.Id))
            return Conflict("User is already a member of this trip.");

        // (TripId, Email) is a unique index, so there's at most one row ever
        // for this email on this trip — re-inviting reuses it instead of
        // inserting a second one. Only a still-PENDING row blocks a
        // re-invite; an old row this same email already accepted (then
        // later left the trip) or declined must never permanently lock that
        // email out of being invited again.
        var existingInvite = await _db.TripInvites.FirstOrDefaultAsync(ti => ti.TripId == id && ti.Email == normalizedEmail);
        if (existingInvite != null && existingInvite.Status == "pending")
            return Conflict("That email is already invited.");

        TripInvite invite;
        if (existingInvite != null)
        {
            existingInvite.InvitedByUserId = userId;
            existingInvite.Status = "pending";
            existingInvite.CreatedAt = DateTime.UtcNow;
            invite = existingInvite;
        }
        else
        {
            invite = new TripInvite
            {
                TripId = id,
                InvitedByUserId = userId,
                Email = normalizedEmail,
                Status = "pending"
            };
            _db.TripInvites.Add(invite);
        }

        await _db.SaveChangesAsync();

        // Existing account → push notification + in-app pending invite.
        // No account yet → send an invitation email, otherwise the invite
        // would sit invisible until this address happened to sign up. The
        // email link is the same /invite/{code} flow the share sheet uses
        // (landing page → store/app → join), and JoinByCode marks this
        // pending invite accepted when they come in through it.
        if (existingUser != null)
        {
            try
            {
                await _notifications.SendTripInviteAsync(invite, existingUser.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch trip invite push for invite {InviteId}.", invite.Id);
            }
        }
        else if (!string.IsNullOrWhiteSpace(trip.InviteCode))
        {
            try
            {
                var inviter = await _db.Users.FindAsync(userId);
                var (subject, htmlBody, textBody) = BuildInviteEmail(trip, inviter?.Name);
                await _emailSender.SendAsync(normalizedEmail, subject, htmlBody, textBody, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Email failure must never break creating the invite itself —
                // the pending invite still shows up in-app after signup.
                _logger.LogError(ex, "Failed to send trip invite email for invite {InviteId}.", invite.Id);
            }
        }

        return Ok(new TripInviteDto
        {
            Id = invite.Id,
            Email = invite.Email,
            Status = invite.Status,
            CreatedAt = invite.CreatedAt
        });
    }

    [HttpPost("{id}/invite")]
    [Authorize]
    public async Task<ActionResult> InviteMember(Guid id, [FromBody] InviteMemberDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        // Any owner (not just original OwnerId) can invite
        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId)) return Forbid();

        var invitee = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
        if (invitee == null) return NotFound("No user found with that email.");

        if (await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == invitee.Id))
            return Conflict("User is already a member of this trip.");

        _db.TripMembers.Add(new TripMember { TripId = id, UserId = invitee.Id, IsOwner = false });
        AddMemberJoinedChatMessage(id, invitee.Id, invitee.Name);
        await _db.SaveChangesAsync();

        return Ok(new TripMemberDto { Id = invitee.Id, Name = invitee.Name, AvatarUrl = invitee.AvatarUrl, IsOwner = false });
    }

    // ── POST /api/trips/{id}/owners ───────────────────────────────────────────
    // Promote an existing member to owner (caller must be owner or admin)

    [HttpPost("{id}/owners")]
    [Authorize]
    public async Task<ActionResult> AddOwner(Guid id, [FromBody] AddOwnerDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        var member = await _db.TripMembers
            .FirstOrDefaultAsync(tm => tm.TripId == id && tm.UserId == dto.UserId);
        if (member == null) return NotFound("User is not a member of this trip.");

        member.IsOwner = true;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── DELETE /api/trips/{id}/owners/{userId} ────────────────────────────────
    // Demote an owner (caller must be owner or admin; cannot remove the last owner)

    [HttpDelete("{id}/owners/{targetUserId}")]
    [Authorize]
    public async Task<ActionResult> RemoveOwner(Guid id, Guid targetUserId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        if (ownerIds.Count <= 1)
            return BadRequest("Cannot remove the last owner. Promote another member first, or delete the SideQuest.");

        var member = await _db.TripMembers
            .FirstOrDefaultAsync(tm => tm.TripId == id && tm.UserId == targetUserId);
        if (member == null) return NotFound("User is not a member of this trip.");

        member.IsOwner = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── DELETE /api/trips/{id}/members/me ─────────────────────────────────────
    // Leave a trip (error if last owner — must promote someone else first)

    [HttpDelete("{id}/members/me")]
    [Authorize]
    public async Task<ActionResult> LeaveTrip(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var member = await _db.TripMembers
            .FirstOrDefaultAsync(tm => tm.TripId == id && tm.UserId == userId);
        if (member == null) return NotFound("You are not a member of this trip.");

        if (member.IsOwner)
        {
            var ownerCount = await _db.TripMembers
                .CountAsync(tm => tm.TripId == id && tm.IsOwner);
            if (ownerCount <= 1)
                return BadRequest("You are the last owner. Promote another member to owner before leaving, or ask an admin to take ownership.");
        }

        var user = await _db.Users.FindAsync([userId]);
        var actorName = DisplayNameHelper.OrFallback(user?.Name);

        _db.TripMembers.Remove(member);

        _db.TripEvents.Add(new TripEvent
        {
            TripId = id,
            ActorId = userId,
            ActorName = actorName,
            Type = "member_left",
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/members/{targetUserId}")]
    [Authorize]
    public async Task<ActionResult> RemoveMember(Guid id, Guid targetUserId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        var member = await _db.TripMembers
            .FirstOrDefaultAsync(tm => tm.TripId == id && tm.UserId == targetUserId);
        if (member == null) return NotFound("Member not found.");

        if (member.IsOwner)
        {
            return BadRequest("Remove owner access first before removing this member.");
        }

        _db.TripMembers.Remove(member);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}/invites/{inviteId}")]
    [Authorize]
    public async Task<ActionResult> DeleteTripInvite(Guid id, Guid inviteId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId)) return Forbid();

        var invite = await _db.TripInvites
            .FirstOrDefaultAsync(ti => ti.Id == inviteId && ti.TripId == id);
        if (invite == null) return NotFound();

        _db.TripInvites.Remove(invite);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── POST /api/trips/{id}/invites/{inviteId}/accept ────────────────────────

    [HttpPost("{id}/invites/{inviteId}/accept")]
    [Authorize]
    public async Task<ActionResult> AcceptInvite(Guid id, Guid inviteId, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();

        var invite = await _db.TripInvites
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.TripId == id && i.Email == email, cancellationToken);

        if (invite == null) return NotFound();

        var alreadyMember = await _db.TripMembers
            .AnyAsync(m => m.TripId == id && m.UserId == userId, cancellationToken);

        // Emit member_joined event so existing members are notified
        var actor = await _db.Users.FindAsync([userId], cancellationToken);

        if (!alreadyMember)
        {
            _db.TripMembers.Add(new TripMember { TripId = id, UserId = userId, IsOwner = false });
            // Chat announcement only on a genuine membership creation — a
            // stale invite accepted by an existing member must not re-announce.
            AddMemberJoinedChatMessage(id, userId, DisplayNameHelper.OrFallback(actor?.Name));
        }

        invite.Status = "accepted";
        _db.TripEvents.Add(new TripEvent
        {
            TripId = id,
            ActorId = userId,
            ActorName = DisplayNameHelper.OrFallback(actor?.Name),
            Type = "member_joined",
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);

        // Push notification — failures must never break accepting an invite.
        try
        {
            var trip = await _db.Trips.FindAsync(new object?[] { id }, cancellationToken);
            if (trip != null)
                await _notifications.SendMemberJoinedAsync(trip, userId, DisplayNameHelper.OrFallback(actor?.Name), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch member_joined push for trip {TripId}.", id);
        }

        return Ok();
    }

    // ── POST /api/trips/{id}/invites/{inviteId}/decline ───────────────────────

    [HttpPost("{id}/invites/{inviteId}/decline")]
    [Authorize]
    public async Task<ActionResult> DeclineInvite(Guid id, Guid inviteId, CancellationToken cancellationToken)
    {
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();

        var invite = await _db.TripInvites
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.TripId == id && i.Email == email, cancellationToken);

        if (invite == null) return NotFound();

        invite.Status = "declined";
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    // ── POST /api/trips/{id}/admin/claim-ownership ────────────────────────────
    // Admin takeover when a trip has no owners left

    [HttpPost("{id}/admin/claim-ownership")]
    [Authorize]
    public async Task<ActionResult> AdminClaimOwnership(Guid id)
    {
        if (!IsAdmin()) return Forbid();

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (ownerIds.Count > 0)
            return BadRequest("Trip still has owners. Use /owners to manage ownership.");

        // Ensure admin is a member
        var member = await _db.TripMembers
            .FirstOrDefaultAsync(tm => tm.TripId == id && tm.UserId == userId);
        if (member == null)
        {
            _db.TripMembers.Add(new TripMember { TripId = id, UserId = userId, IsOwner = true });
        }
        else
        {
            member.IsOwner = true;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── GET /api/trips/{id}/activities ────────────────────────────────────────

    [HttpGet("{id}/activities")]
    [Authorize]
    public async Task<ActionResult<List<ActivityResponseDto>>> GetActivities(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var isMember = await IsTripMember(id, userId);
        if (!isMember && trip.Visibility != "public" && !IsRevealedNow(trip))
            return Forbid();

        var ownerIds = await GetOwnerIds(id);
        var membersCanEditTrip = isMember && (trip.MembersCanEdit || ownerIds.Contains(userId));

        var activities = await _db.TripActivities
            .Where(a => a.TripId == id)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.SortIndex)
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .ToListAsync();

        var activityIds = activities.Select(a => a.Id).ToList();
        var commentCounts = await _db.ActivityComments
            .Where(c => activityIds.Contains(c.ActivityId))
            .GroupBy(c => c.ActivityId)
            .ToDictionaryAsync(g => g.Key, g => g.Count());

        return Ok(activities.Select(activity =>
        {
            var dto = BuildActivityResponse(userId, activity, membersCanEditTrip);
            dto.CommentCount = commentCounts.GetValueOrDefault(activity.Id, 0);
            return dto;
        }).ToList());
    }

    [HttpGet("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult<ActivityResponseDto>> GetActivity(Guid id, Guid activityId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var isMember = await IsTripMember(id, userId);
        if (!isMember && trip.Visibility != "public" && !IsRevealedNow(trip))
            return Forbid();

        var activity = await _db.TripActivities
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);

        if (activity == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        var membersCanEditTrip = isMember && (trip.MembersCanEdit || ownerIds.Contains(userId));

        var dto = BuildActivityResponse(userId, activity, membersCanEditTrip);
        dto.CommentCount = await _db.ActivityComments.CountAsync(c => c.ActivityId == activityId);
        return Ok(dto);
    }

    // ── POST /api/trips/{id}/activities ───────────────────────────────────────

    [HttpPost("{id}/activities")]
    [Authorize]
    public async Task<ActionResult<ActivityResponseDto>> AddActivity(Guid id, [FromBody] CreateActivityDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        // When the owner has locked editing, only owners may add activities.
        if (!await CanEditActivitiesAsync(id, userId))
            return Forbid();

        // The activity date must sit inside the trip's range — otherwise it
        // shows up on the calendar before the trip starts / after it ends.
        var tripForRange = await _db.Trips.FindAsync(id);
        if (tripForRange == null) return NotFound();
        if (dto.Date < tripForRange.StartDate || dto.Date > tripForRange.EndDate)
            return BadRequest($"Activity date must be within the trip dates ({tripForRange.StartDate:yyyy-MM-dd} – {tripForRange.EndDate:yyyy-MM-dd}).");

        var visibility = NormalizeVisibility(dto.Visibility);
        var teaser = NormalizeOptionalText(dto.Teaser);
        var validationError = ValidateActivityPayload(visibility, dto.RevealAt, teaser, dto.TeaserOffsetMinutes);

        if (validationError != null)
            return BadRequest(validationError);

        if (!IsValidSpotifyUrl(dto.SpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        var finalVisibility = dto.RevealedNow ? "public" : visibility;
        var nextTime = NormalizeOptionalText(dto.Time);
        var nextSortIndex = await InsertChronologicallyAsync(id, dto.Date, nextTime, null);
        var activity = new TripActivity
        {
            TripId = id,
            Date = dto.Date,
            SortIndex = nextSortIndex,
            Title = dto.Title.Trim(),
            Description = NormalizeOptionalText(dto.Description),
            Time = nextTime,
            Category = NormalizeOptionalText(dto.Category),
            CustomCategoryLabel = NormalizeOptionalText(dto.CustomCategoryLabel),
            ImageUrl = NormalizeOptionalText(dto.ImageUrl),
            SpotifyUrl = NormalizeOptionalText(dto.SpotifyUrl),
            Visibility = finalVisibility,
            RevealAt = finalVisibility == "hidden" ? dto.RevealAt : null,
            Teaser = finalVisibility == "hidden" ? teaser : null,
            TeaserOffsetMinutes = finalVisibility == "hidden" ? dto.TeaserOffsetMinutes : null,
            IsHidden = finalVisibility == "hidden",
            RevealedAt = dto.RevealedNow ? DateTime.UtcNow : null,
            OwnerId = userId,
            AssignedToUserId = userId
        };

        _db.TripActivities.Add(activity);

        // In-app "Activity" feed entry — deliberately doesn't carry the
        // activity's title (it may be a hidden SideQuest; eventLabel()
        // renders this generically, same as member_joined/member_left).
        // IsHidden also tells the client to suppress ActorName for hidden
        // SideQuests — "who added it" is just as much a spoiler as the title.
        var actorForEvent = await _db.Users.FindAsync(new object?[] { userId });
        _db.TripEvents.Add(new TripEvent
        {
            TripId = id,
            ActorId = userId,
            ActorName = DisplayNameHelper.OrFallback(actorForEvent?.Name),
            Type = "activity_added",
            ActivityId = activity.Id,
            IsHidden = finalVisibility == "hidden",
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();

        var created = await _db.TripActivities
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .FirstAsync(a => a.Id == activity.Id);

        // Push notification — failures must never break adding an activity.
        try
        {
            await _notifications.SendActivityAddedAsync(created, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch activity_added push for activity {ActivityId}.", activity.Id);
        }

        return Ok(BuildActivityResponse(userId, created, membersCanEditTrip: true));
    }

    // ── PATCH /api/trips/{id}/activities/{activityId} ─────────────────────────

    [HttpPatch("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult> UpdateActivity(Guid id, Guid activityId, [FromBody] UpdateActivityDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        // SideQuests are creator-only to edit, regardless of MembersCanEdit.
        // Regular (non-SideQuest) activities keep the old collaborative
        // behavior — any member may edit when MembersCanEdit is on, or a
        // trip owner always can.
        if (IsSideQuest(activity))
        {
            if (activity.OwnerId != userId) return Forbid();
        }
        else if (!await CanEditActivitiesAsync(id, userId))
        {
            return Forbid();
        }

        var nextVisibility = dto.RevealedNow ? "public" : (dto.Visibility != null ? NormalizeVisibility(dto.Visibility) : activity.Visibility);
        var nextRevealAt = dto.RevealedNow ? null : (dto.ClearRevealAt ? null : dto.RevealAt ?? activity.RevealAt);
        var nextTeaser = dto.RevealedNow ? null : (dto.ClearTeaser ? null : dto.Teaser != null ? NormalizeOptionalText(dto.Teaser) : activity.Teaser);
        var nextTeaserOffset = dto.RevealedNow ? null : (dto.ClearTeaserOffset ? null : dto.TeaserOffsetMinutes ?? activity.TeaserOffsetMinutes);
        var validationError = ValidateActivityPayload(nextVisibility, nextRevealAt, nextTeaser, nextTeaserOffset);

        if (validationError != null)
            return BadRequest(validationError);

        var nextSpotifyUrl = dto.ClearSpotifyUrl ? null : dto.SpotifyUrl != null ? NormalizeOptionalText(dto.SpotifyUrl) : activity.SpotifyUrl;
        if (!IsValidSpotifyUrl(nextSpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        // A changed date must stay inside the trip's range.
        if (dto.Date.HasValue)
        {
            var tripForRange = await _db.Trips.FindAsync(id);
            if (tripForRange == null) return NotFound();
            if (dto.Date.Value < tripForRange.StartDate || dto.Date.Value > tripForRange.EndDate)
                return BadRequest($"Activity date must be within the trip dates ({tripForRange.StartDate:yyyy-MM-dd} – {tripForRange.EndDate:yyyy-MM-dd}).");
        }

        var previousImageUrl = activity.ImageUrl;
        var wasHiddenBeforeUpdate = activity.Visibility == "hidden";

        // Reposition when the day changes (old manual position was relative
        // to the old day's list) or when a Time is set/changed — in the
        // Time case, slot it in chronologically among same-day siblings
        // that also have a Time (see InsertChronologicallyAsync). A later
        // drag-to-reorder still fully overrides whatever this lands on.
        var nextTime = dto.Time != null ? NormalizeOptionalText(dto.Time) : activity.Time;
        var dateChanged = dto.Date.HasValue && dto.Date.Value != activity.Date;
        var timeChanged = dto.Time != null && nextTime != activity.Time;
        if (dateChanged || timeChanged)
        {
            var targetDate = dto.Date ?? activity.Date;
            activity.SortIndex = await InsertChronologicallyAsync(id, targetDate, nextTime, activity.Id);
        }

        if (dto.Date.HasValue) activity.Date = dto.Date.Value;
        if (dto.Title != null) activity.Title = dto.Title.Trim();
        if (dto.Description != null) activity.Description = NormalizeOptionalText(dto.Description);
        if (dto.Time != null) activity.Time = nextTime;
        if (dto.Category != null) activity.Category = NormalizeOptionalText(dto.Category);
        if (dto.ClearCustomCategoryLabel) activity.CustomCategoryLabel = null;
        else if (dto.CustomCategoryLabel != null) activity.CustomCategoryLabel = NormalizeOptionalText(dto.CustomCategoryLabel);
        if (dto.ClearImage) activity.ImageUrl = null;
        else if (dto.ImageUrl != null) activity.ImageUrl = NormalizeOptionalText(dto.ImageUrl);
        activity.SpotifyUrl = nextSpotifyUrl;

        activity.Visibility = nextVisibility;
        activity.IsHidden = nextVisibility == "hidden";
        activity.RevealAt = nextVisibility == "hidden" ? nextRevealAt : null;
        activity.Teaser = nextVisibility == "hidden" ? nextTeaser : null;
        activity.TeaserOffsetMinutes = nextVisibility == "hidden" ? nextTeaserOffset : null;

        // Manual reveal — fires once, the moment a previously-hidden
        // SideQuest is flipped to public via the "Reveal now" button.
        // (Bug fix: this previously checked activity.Visibility AFTER it had
        // already been mutated to "public" above, so the condition was
        // always false and RevealedAt never actually got set.)
        var isManualReveal = dto.RevealedNow && wasHiddenBeforeUpdate;
        if (isManualReveal)
        {
            activity.RevealedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        if (isManualReveal)
        {
            // Same in-app event + push as a scheduled reveal (see
            // RevealNotificationScheduler.ProcessRevealsAsync) — "Reveal
            // now" must show up in Home Activity and the bell exactly like a
            // timed one does, not silently bypass both.
            var actorForEvent = await _db.Users.FindAsync(new object?[] { userId });
            _db.TripEvents.Add(new TripEvent
            {
                TripId = id,
                ActorId = userId,
                ActorName = DisplayNameHelper.OrFallback(actorForEvent?.Name),
                Type = "sidequest_revealed",
                ActivityId = activity.Id,
                ActivityTitle = activity.Title,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();

            try
            {
                await _notifications.SendRevealAsync(activity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch sidequest_revealed push for activity {ActivityId} (manual reveal).", activity.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(previousImageUrl) && previousImageUrl != activity.ImageUrl)
        {
            await _storage.DeleteByUrlAsync(previousImageUrl);
        }

        return NoContent();
    }

    // ── PATCH /api/trips/{id}/activities/reorder ──────────────────────────────
    // Drag-to-reorder within a single day: takes the full ordered list of
    // activity IDs for that date and rewrites their SortIndex 0..N-1.

    [HttpPatch("{id}/activities/reorder")]
    [Authorize]
    public async Task<ActionResult> ReorderActivities(Guid id, [FromBody] ReorderActivitiesDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await CanEditActivitiesAsync(id, userId)) return Forbid();

        if (dto.ActivityIds == null || dto.ActivityIds.Count == 0)
            return BadRequest("activityIds is required.");

        var activities = await _db.TripActivities
            .Where(a => a.TripId == id && a.Date == dto.Date && dto.ActivityIds.Contains(a.Id))
            .ToListAsync();

        if (activities.Count != dto.ActivityIds.Count)
            return BadRequest("One or more activities do not belong to this trip and date.");

        var activitiesById = activities.ToDictionary(a => a.Id);
        for (var i = 0; i < dto.ActivityIds.Count; i++)
        {
            activitiesById[dto.ActivityIds[i]].SortIndex = i;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── PATCH /api/trips/{id}/activities/{activityId}/move ───────────────────
    // Drag-to-move across days: sets the activity's new date and rewrites the
    // TARGET day's SortIndex from the full ordered id list (which includes
    // the moved activity) in one atomic save. Same trip-wide permission as
    // reorder — moving is collaborative feed curation, not content editing.

    [HttpPatch("{id}/activities/{activityId}/move")]
    [Authorize]
    public async Task<ActionResult> MoveActivity(Guid id, Guid activityId, [FromBody] MoveActivityDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await CanEditActivitiesAsync(id, userId)) return Forbid();

        if (dto.ActivityIds == null || dto.ActivityIds.Count == 0 || !dto.ActivityIds.Contains(activityId))
            return BadRequest("activityIds must include the moved activity.");

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        // Same range rule as editing an activity's date.
        if (dto.Date < trip.StartDate || dto.Date > trip.EndDate)
            return BadRequest($"Activity date must be within the trip dates ({trip.StartDate:yyyy-MM-dd} – {trip.EndDate:yyyy-MM-dd}).");

        var activities = await _db.TripActivities
            .Where(a => a.TripId == id && dto.ActivityIds.Contains(a.Id))
            .ToListAsync();

        if (activities.Count != dto.ActivityIds.Count)
            return BadRequest("One or more activities do not belong to this trip.");

        var moved = activities.FirstOrDefault(a => a.Id == activityId);
        if (moved == null) return NotFound();

        // Everything except the moved activity must already live on the
        // target day — the id list is that day's final order, nothing else.
        if (activities.Any(a => a.Id != activityId && a.Date != dto.Date))
            return BadRequest("activityIds must contain only the target day's activities.");

        moved.Date = dto.Date;

        var byId = activities.ToDictionary(a => a.Id);
        for (var i = 0; i < dto.ActivityIds.Count; i++)
        {
            byId[dto.ActivityIds[i]].SortIndex = i;
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("{id}/activities/{activityId}/spotify")]
    [Authorize]
    public async Task<ActionResult<ActivityResponseDto>> UpdateActivitySpotify(Guid id, Guid activityId, [FromBody] UpdateActivitySpotifyDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var activity = await _db.TripActivities
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        var nextSpotifyUrl = dto.ClearSpotifyUrl ? null : NormalizeOptionalText(dto.SpotifyUrl);
        if (!IsValidSpotifyUrl(nextSpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        activity.SpotifyUrl = nextSpotifyUrl;
        await _db.SaveChangesAsync();

        return Ok(BuildActivityResponse(userId, activity, membersCanEditTrip: true));
    }

    // ── DELETE /api/trips/{id}/activities/{activityId} ────────────────────────

    [HttpDelete("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult> DeleteActivity(Guid id, Guid activityId, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id, cancellationToken);
        if (activity == null) return NotFound();

        // SideQuests are creator-only to delete, regardless of MembersCanEdit.
        // Regular (non-SideQuest) activities keep the old collaborative
        // behavior — any member may delete when MembersCanEdit is on, or a
        // trip owner always can.
        if (IsSideQuest(activity))
        {
            if (activity.OwnerId != userId) return Forbid();
        }
        else if (!await CanEditActivitiesAsync(id, userId))
        {
            return Forbid();
        }

        var imageUrl = activity.ImageUrl;

        // NotificationLog has no FK to TripActivity (it must survive the
        // activity being deleted later, since Title/Body/DataJson are
        // captured once at claim time) — so deleting the activity does not
        // cascade. Without this, other members keep a stale bell entry
        // whose route 404s the moment they tap it. DedupeKey embeds the
        // activity id as its second ":"-segment for every type that's
        // actually about one activity (see NotificationDispatchService).
        var activityIdStr = activity.Id.ToString();
        var teaserPrefix = $"teaser:{activityIdStr}:";
        var revealedPrefix = $"sidequest_revealed:{activityIdStr}:";
        var newActivityPrefix = $"new_activity:{activityIdStr}:";
        var newHiddenPrefix = $"new_hidden_sidequest:{activityIdStr}:";
        var staleNotifications = await _db.NotificationLogs
            .Where(n =>
                n.DedupeKey.StartsWith(teaserPrefix) ||
                n.DedupeKey.StartsWith(revealedPrefix) ||
                n.DedupeKey.StartsWith(newActivityPrefix) ||
                n.DedupeKey.StartsWith(newHiddenPrefix))
            .ToListAsync(cancellationToken);
        if (staleNotifications.Count > 0)
        {
            _db.NotificationLogs.RemoveRange(staleNotifications);
        }

        _db.TripActivities.Remove(activity);
        await _db.SaveChangesAsync(cancellationToken);

        await _storage.DeleteByUrlAsync(imageUrl, cancellationToken);

        return NoContent();
    }

    // ── GET /api/trips/{id}/activities/{activityId}/comments ─────────────────

    [HttpGet("{id}/activities/{activityId}/comments")]
    [Authorize]
    public async Task<ActionResult<List<ActivityCommentDto>>> GetComments(Guid id, Guid activityId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var activity = await _db.TripActivities.FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        var isMember = await IsTripMember(id, userId);
        if (activity.Visibility != "public" && !isMember)
            return Forbid();

        var comments = await _db.ActivityComments
            .Where(c => c.ActivityId == activityId)
            .OrderBy(c => c.CreatedAt)
            .Include(c => c.User)
            .ToListAsync();

        return Ok(comments.Select(c => new ActivityCommentDto
        {
            Id = c.Id,
            ActivityId = c.ActivityId,
            UserId = c.UserId,
            UserName = c.User.Name,
            UserAvatarUrl = c.User.AvatarUrl,
            Text = c.Text,
            CreatedAt = c.CreatedAt,
        }).ToList());
    }

    // ── POST /api/trips/{id}/activities/{activityId}/comments ─────────────────

    [HttpPost("{id}/activities/{activityId}/comments")]
    [Authorize]
    public async Task<ActionResult<ActivityCommentDto>> AddComment(Guid id, Guid activityId, [FromBody] CreateCommentDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var activity = await _db.TripActivities.FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        if (activity.Visibility != "public")
            return BadRequest("Comments are only allowed on public activities.");

        var text = dto.Text.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest("Comment cannot be empty.");

        var comment = new ActivityComment
        {
            ActivityId = activityId,
            UserId = userId,
            Text = text,
        };

        _db.ActivityComments.Add(comment);
        await _db.SaveChangesAsync();

        var user = await _db.Users.FindAsync(userId);

        return Ok(new ActivityCommentDto
        {
            Id = comment.Id,
            ActivityId = comment.ActivityId,
            UserId = comment.UserId,
            UserName = user?.Name ?? "",
            UserAvatarUrl = user?.AvatarUrl,
            Text = comment.Text,
            CreatedAt = comment.CreatedAt,
        });
    }

    // ── GET /api/trips/completed ────────────────────────────────────────────

    [HttpGet("completed")]
    [Authorize]
    public async Task<ActionResult<List<TripResponseDto>>> GetCompletedTrips()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var memberTripIds = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Select(tm => tm.TripId)
            .ToListAsync();

        var trips = await _db.Trips
            .Where(t => memberTripIds.Contains(t.Id) && t.Status == "completed")
            .OrderByDescending(t => t.EndDate)
            .ToListAsync();

        var tripIds = trips.Select(t => t.Id).ToList();
        var ownerMap = await _db.TripMembers
            .Where(tm => tripIds.Contains(tm.TripId) && tm.IsOwner)
            .GroupBy(tm => tm.TripId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(tm => tm.UserId).ToList());

        var result = trips.Select(trip =>
        {
            var owners = ownerMap.GetValueOrDefault(trip.Id, new List<Guid>());
            return BuildResponse(trip, owners, CanViewFull(userId, trip, owners));
        }).ToList();

        return Ok(result);
    }

    // ── PATCH /api/trips/{id}/complete ─────────────────────────────────────

    [HttpPatch("{id}/complete")]
    [Authorize]
    public async Task<ActionResult> CompleteTrip(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId)) return Forbid();

        trip.Status = "completed";
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── DELETE /api/trips/{id}/share ────────────────────────────────────────
    // Revoke the public share link for a completed adventure.
    // Existing copies made by other users (separate Trip rows) are unaffected.

    [HttpDelete("{id}/share")]
    [Authorize]
    public async Task<ActionResult> RevokeShare(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        if (string.IsNullOrEmpty(trip.ShareCode))
        {
            // Idempotent: nothing to revoke.
            return NoContent();
        }

        trip.ShareCode = null;
        trip.SharedAt = null;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    // ── POST /api/trips/{id}/share ──────────────────────────────────────────

    [HttpPost("{id}/share")]
    [Authorize]
    public async Task<ActionResult<ShareTripDto>> ShareTrip(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId)) return Forbid();

        if (trip.Status != "completed")
            return BadRequest("Only completed adventures can be shared.");

        if (string.IsNullOrEmpty(trip.ShareCode))
        {
            trip.ShareCode = GenerateShareCode();
            trip.SharedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(new ShareTripDto
        {
            ShareCode = trip.ShareCode,
            ShareUrl = $"https://sidequesttravel.app/share/{trip.ShareCode}"
        });
    }

    // ── GET /api/trips/share/{code} ─────────────────────────────────────────

    [HttpGet("share/{code}")]
    public async Task<ActionResult<SharedTripDto>> GetSharedTrip(string code)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.ShareCode == code);
        if (trip == null) return NotFound();

        if (trip.Status != "completed")
            return BadRequest("This adventure is no longer available for viewing.");

        var activities = await _db.TripActivities
            .Where(a => a.TripId == trip.Id && a.Visibility == "public")
            .OrderBy(a => a.Date)
            .ThenBy(a => a.Time)
            .ToListAsync();

        return Ok(new SharedTripDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Description = trip.Description,
            Destination = trip.Destination,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            ImageUrl = trip.ImageUrl,
            SpotifyUrl = trip.SpotifyUrl,
            OwnerName = trip.Owner.Name
        });
    }

    // ── POST /api/trips/share/{code}/copy ──────────────────────────────────────
    [Authorize]
    [HttpPost("share/{code}/copy")]
    public async Task<ActionResult<TripResponseDto>> CopySharedTrip(string code)
    {
        var originalTrip = await _db.Trips.FirstOrDefaultAsync(t => t.ShareCode == code);
        if (originalTrip == null) return NotFound("Adventure not found.");

        if (originalTrip.Status != "completed")
            return BadRequest("This adventure is no longer available for copying.");

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(userId, out var userGuid))
            return Unauthorized();

        var newTrip = new Trip
        {
            Title = originalTrip.Title,
            Description = originalTrip.Description,
            Destination = originalTrip.Destination,
            StartDate = originalTrip.StartDate,
            EndDate = originalTrip.EndDate,
            ImageUrl = originalTrip.ImageUrl,
            SpotifyUrl = originalTrip.SpotifyUrl,
            OwnerId = userGuid,
            Status = "active",
            InviteCode = GenerateInviteCode(),
        };

        _db.Trips.Add(newTrip);
        _db.TripMembers.Add(new TripMember { TripId = newTrip.Id, UserId = userGuid, IsOwner = true });
        await _db.SaveChangesAsync();

        var originalActivities = await _db.TripActivities
            .Where(a => a.TripId == originalTrip.Id && a.Visibility == "public")
            .ToListAsync();

        foreach (var activity in originalActivities)
        {
            var newActivity = new TripActivity
            {
                TripId = newTrip.Id,
                OwnerId = userGuid,
                Title = activity.Title,
                Category = activity.Category,
                CustomCategoryLabel = activity.CustomCategoryLabel,
                Date = activity.Date,
                Time = activity.Time,
                Description = activity.Description,
                ImageUrl = activity.ImageUrl,
                SpotifyUrl = activity.SpotifyUrl,
                Visibility = "public",
                CreatedAt = DateTime.UtcNow,
            };
            _db.TripActivities.Add(newActivity);
        }

        await _db.SaveChangesAsync();

        var ownerIds = new List<Guid> { userGuid };
        return Ok(BuildResponse(newTrip, ownerIds, canViewFull: true));
    }

    // ── POST /api/trips/seed ────────────────────────────────────────────────────
    // Development only: create test adventure with activities for user by email
    [HttpPost("seed")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> SeedTestData([FromQuery] string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return NotFound($"User with email {email} not found");

        var existingTrips = await _db.Trips.Where(t => t.OwnerId == user.Id).CountAsync();
        if (existingTrips > 0)
            return BadRequest("User already has trips. Seeding cancelled.");

        var trip = new Trip
        {
            Title = "Barcelona City Adventure",
            Description = "Explore the vibrant streets of Barcelona, from Gaudí's masterpieces to hidden beachside gems.",
            Destination = "Barcelona, Spain",
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(17)),
            ImageUrl = "https://images.unsplash.com/photo-1583422409516-2895a77efded?w=800",
            OwnerId = user.Id,
            Visibility = "public",
            InviteCode = GenerateInviteCode()
        };

        _db.Trips.Add(trip);
        await _db.SaveChangesAsync();

        var activities = new[]
        {
            new TripActivity
            {
                TripId = trip.Id,
                Title = "Sagrada Familia Tour",
                Description = "Visit Gaudí's most iconic basilica. Arrive early to beat the crowds.",
                Date = trip.StartDate.AddDays(0),
                Time = "09:00",
                Category = "sight",
                Visibility = "public",
                ImageUrl = "https://images.unsplash.com/photo-1583422409516-2895a77efded?w=500",
                OwnerId = user.Id,
            },
            new TripActivity
            {
                TripId = trip.Id,
                Title = "Park Güell Sunset",
                Description = "Explore Gaudí's whimsical park with panoramic city views.",
                Date = trip.StartDate.AddDays(1),
                Time = "17:30",
                Category = "sight",
                Visibility = "public",
                ImageUrl = "https://images.unsplash.com/photo-1579174905393-a64d5faf2f06?w=500",
                OwnerId = user.Id,
            },
            new TripActivity
            {
                TripId = trip.Id,
                Title = "Gothic Quarter Wandering",
                Description = "Get lost in the narrow medieval streets of the Gothic Quarter.",
                Date = trip.StartDate.AddDays(2),
                Time = "14:00",
                Category = "sight",
                Visibility = "public",
                ImageUrl = "https://images.unsplash.com/photo-1583422409516-2895a77efded?w=500",
                OwnerId = user.Id,
            },
            new TripActivity
            {
                TripId = trip.Id,
                Title = "Beach Day at Barceloneta",
                Description = "Relax at the popular city beach. Try some local seafood paella.",
                Date = trip.StartDate.AddDays(3),
                Time = "10:00",
                Category = "food",
                Visibility = "public",
                ImageUrl = "https://images.unsplash.com/photo-1507525428034-b723cf961d3e?w=500",
                OwnerId = user.Id,
            },
            new TripActivity
            {
                TripId = trip.Id,
                Title = "Las Ramblas Street Life",
                Description = "Walk the famous boulevard lined with shops, cafes, and street performers. No photo - hidden surprise!",
                Date = trip.StartDate.AddDays(4),
                Time = "16:00",
                Category = "sight",
                Visibility = "hidden",
                RevealAt = trip.StartDate.AddDays(4).ToDateTime(new TimeOnly(12, 0)),
                Teaser = "A famous Barcelona boulevard awaits...",
                OwnerId = user.Id,
            }
        };

        _db.TripActivities.AddRange(activities);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Test adventure created successfully",
            tripId = trip.Id,
            tripTitle = trip.Title,
            activitiesCount = activities.Length
        });
    }

    // ── POST /api/trips/add-past-event ──────────────────────────────────────
    // Test endpoint: add a past event for testing
    [HttpPost("add-past-event")]
    [AllowAnonymous]
    public async Task<ActionResult> AddPastEvent([FromQuery] string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return NotFound($"User with email {email} not found");

        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.OwnerId == user.Id);
        if (trip == null)
            return NotFound("No trip found for this user");

        var pastEvent = new TripEvent
        {
            Id = Guid.NewGuid(),
            TripId = trip.Id,
            ActorId = user.Id,
            ActorName = user.Name,
            Type = "trip_started",
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        };

        _db.TripEvents.Add(pastEvent);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Past event added successfully",
            eventType = pastEvent.Type,
            createdAt = pastEvent.CreatedAt
        });
    }

    // ── POST /api/trips/mark-completed ──────────────────────────────────────
    // Test endpoint: mark a trip as completed
    [HttpPost("mark-completed")]
    [AllowAnonymous]
    public async Task<ActionResult> MarkCompleted([FromQuery] string email)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return NotFound($"User with email {email} not found");

        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.OwnerId == user.Id);
        if (trip == null)
            return NotFound("No trip found for this user");

        trip.Status = "completed";
        trip.EndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Trip marked as completed",
            tripId = trip.Id,
            tripTitle = trip.Title,
            status = trip.Status
        });
    }

    private static string GenerateShareCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        var code = new char[8];
        for (int i = 0; i < 8; i++)
            code[i] = chars[random.Next(chars.Length)];
        return new string(code);
    }
}

public class ShareTripDto
{
    public string ShareCode { get; set; } = "";
    public string ShareUrl { get; set; } = "";
}

public class SharedTripDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Destination { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string OwnerName { get; set; } = "";
}
