using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TripsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TripsController(AppDbContext db)
    {
        _db = db;
    }

    // ── Permission helpers ────────────────────────────────────────────────────

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

    private static ActivityResponseDto BuildActivityResponse(Guid userId, TripActivity activity, bool canEdit)
    {
        var canViewFull = CanViewActivityFull(userId, activity);
        var teaserVisible = !canViewFull && IsTeaserVisibleNow(activity);

        return new ActivityResponseDto
        {
            Id = activity.Id,
            TripId = activity.TripId,
            Date = activity.Date,
            Title = canViewFull ? activity.Title : null,
            Description = canViewFull ? activity.Description : null,
            Time = activity.Time,
            Category = activity.Category,
            ImageUrl = activity.ImageUrl,
            SpotifyUrl = activity.SpotifyUrl,
            Visibility = activity.Visibility,
            RevealAt = activity.RevealAt,
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
            InviteCode = trip.InviteCode,
            Title = canViewFull ? trip.Title : null,
            Description = canViewFull ? trip.Description : null,
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
        var actorName = user?.Name ?? "Someone";

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

        await _db.SaveChangesAsync(cancellationToken);

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
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var memberTripIds = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Select(tm => tm.TripId)
            .ToListAsync();

        var trips = await _db.Trips
            .Where(t => memberTripIds.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt)
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

    // ── POST /api/trips ───────────────────────────────────────────────────────

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<TripResponseDto>> CreateTrip([FromBody] CreateTripDto dto)
    {
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
        await _db.SaveChangesAsync();

        var ownerIds = new List<Guid> { userId };
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

        var outOfRangeActivityExists = await _db.TripActivities.AnyAsync(activity =>
            activity.TripId == id
            && (activity.Date < nextStartDate || activity.Date > nextEndDate));

        if (outOfRangeActivityExists)
        {
            return BadRequest("One or more SideQuests fall outside the updated trip dates.");
        }

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
    public async Task<ActionResult> DeleteTrip(Guid id)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isAdmin = IsAdmin();

        var trip = await _db.Trips.FindAsync(id);
        if (trip == null) return NotFound();

        var ownerIds = await GetOwnerIds(id);
        if (!ownerIds.Contains(userId) && !isAdmin) return Forbid();

        _db.Trips.Remove(trip);
        await _db.SaveChangesAsync();
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
            .Where(ti => ti.TripId == id)
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

        if (await _db.TripInvites.AnyAsync(ti => ti.TripId == id && ti.Email == normalizedEmail))
            return Conflict("That email is already invited.");

        var invite = new TripInvite
        {
            TripId = id,
            InvitedByUserId = userId,
            Email = normalizedEmail,
            Status = "pending"
        };

        _db.TripInvites.Add(invite);
        await _db.SaveChangesAsync();

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
        var actorName = user?.Name ?? "Someone";

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

        if (!alreadyMember)
        {
            _db.TripMembers.Add(new TripMember { TripId = id, UserId = userId, IsOwner = false });
        }

        invite.Status = "accepted";

        // Emit member_joined event so existing members are notified
        var actor = await _db.Users.FindAsync([userId], cancellationToken);
        _db.TripEvents.Add(new TripEvent
        {
            TripId = id,
            ActorId = userId,
            ActorName = actor?.Name ?? "Someone",
            Type = "member_joined",
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
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

        var activities = await _db.TripActivities
            .Where(a => a.TripId == id)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.Time)
            .ThenBy(a => a.CreatedAt)
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
            var dto = BuildActivityResponse(userId, activity, isMember);
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

        var dto = BuildActivityResponse(userId, activity, isMember);
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

        var visibility = NormalizeVisibility(dto.Visibility);
        var teaser = NormalizeOptionalText(dto.Teaser);
        var validationError = ValidateActivityPayload(visibility, dto.RevealAt, teaser, dto.TeaserOffsetMinutes);

        if (validationError != null)
            return BadRequest(validationError);

        if (!IsValidSpotifyUrl(dto.SpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        var activity = new TripActivity
        {
            TripId = id,
            Date = dto.Date,
            Title = dto.Title.Trim(),
            Description = NormalizeOptionalText(dto.Description),
            Time = NormalizeOptionalText(dto.Time),
            Category = NormalizeOptionalText(dto.Category),
            ImageUrl = NormalizeOptionalText(dto.ImageUrl),
            SpotifyUrl = NormalizeOptionalText(dto.SpotifyUrl),
            Visibility = visibility,
            RevealAt = visibility == "hidden" ? dto.RevealAt : null,
            Teaser = visibility == "hidden" ? teaser : null,
            TeaserOffsetMinutes = visibility == "hidden" ? dto.TeaserOffsetMinutes : null,
            IsHidden = visibility == "hidden",
            OwnerId = userId,
            AssignedToUserId = userId
        };

        _db.TripActivities.Add(activity);
        await _db.SaveChangesAsync();

        var created = await _db.TripActivities
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .FirstAsync(a => a.Id == activity.Id);

        return Ok(BuildActivityResponse(userId, created, canEdit: true));
    }

    // ── PATCH /api/trips/{id}/activities/{activityId} ─────────────────────────

    [HttpPatch("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult> UpdateActivity(Guid id, Guid activityId, [FromBody] UpdateActivityDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isMember = await IsTripMember(id, userId);
        if (!isMember) return Forbid();

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        var nextVisibility = dto.Visibility != null ? NormalizeVisibility(dto.Visibility) : activity.Visibility;
        var nextRevealAt = dto.ClearRevealAt ? null : dto.RevealAt ?? activity.RevealAt;
        var nextTeaser = dto.ClearTeaser ? null : dto.Teaser != null ? NormalizeOptionalText(dto.Teaser) : activity.Teaser;
        var nextTeaserOffset = dto.ClearTeaserOffset ? null : dto.TeaserOffsetMinutes ?? activity.TeaserOffsetMinutes;
        var validationError = ValidateActivityPayload(nextVisibility, nextRevealAt, nextTeaser, nextTeaserOffset);

        if (validationError != null)
            return BadRequest(validationError);

        var nextSpotifyUrl = dto.ClearSpotifyUrl ? null : dto.SpotifyUrl != null ? NormalizeOptionalText(dto.SpotifyUrl) : activity.SpotifyUrl;
        if (!IsValidSpotifyUrl(nextSpotifyUrl))
            return BadRequest("Spotify link must be a valid public Spotify URL.");

        if (dto.Date.HasValue) activity.Date = dto.Date.Value;
        if (dto.Title != null) activity.Title = dto.Title.Trim();
        if (dto.Description != null) activity.Description = NormalizeOptionalText(dto.Description);
        if (dto.Time != null) activity.Time = NormalizeOptionalText(dto.Time);
        if (dto.Category != null) activity.Category = NormalizeOptionalText(dto.Category);
        if (dto.ClearImage) activity.ImageUrl = null;
        else if (dto.ImageUrl != null) activity.ImageUrl = NormalizeOptionalText(dto.ImageUrl);
        activity.SpotifyUrl = nextSpotifyUrl;

        activity.Visibility = nextVisibility;
        activity.IsHidden = nextVisibility == "hidden";
        activity.RevealAt = nextVisibility == "hidden" ? nextRevealAt : null;
        activity.Teaser = nextVisibility == "hidden" ? nextTeaser : null;
        activity.TeaserOffsetMinutes = nextVisibility == "hidden" ? nextTeaserOffset : null;

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

        return Ok(BuildActivityResponse(userId, activity, canEdit: true));
    }

    // ── DELETE /api/trips/{id}/activities/{activityId} ────────────────────────

    [HttpDelete("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult> DeleteActivity(Guid id, Guid activityId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isMember = await IsTripMember(id, userId);
        if (!isMember) return Forbid();

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();

        _db.TripActivities.Remove(activity);
        await _db.SaveChangesAsync();
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
}
