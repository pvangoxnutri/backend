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

    private static bool CanEditActivity(Guid userId, TripActivity activity)
        => activity.OwnerId == userId;

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

    private static ActivityResponseDto BuildActivityResponse(Guid userId, TripActivity activity)
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
            Visibility = activity.Visibility,
            RevealAt = activity.RevealAt,
            IsRevealed = IsActivityRevealedNow(activity),
            Teaser = canViewFull || teaserVisible ? activity.Teaser : null,
            TeaserOffsetMinutes = activity.TeaserOffsetMinutes,
            IsHiddenForViewer = !canViewFull,
            TeaserVisible = teaserVisible,
            CanEdit = CanEditActivity(userId, activity),
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
            Destination = trip.Destination,  // always (shown in date/location row)
            InviteCode = trip.InviteCode,
            Title = canViewFull ? trip.Title : null,
            Description = canViewFull ? trip.Description : null,
        };

    // ── GET /api/trips ────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<List<TripResponseDto>>> GetMyTrips()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var tripIds = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Select(tm => tm.TripId)
            .ToListAsync();

        var trips = await _db.Trips
            .Where(t => tripIds.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

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

        // Must be a member to access the trip
        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
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

        _db.TripMembers.Remove(member);
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

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var activities = await _db.TripActivities
            .Where(a => a.TripId == id)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.Time)
            .ThenBy(a => a.CreatedAt)
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .ToListAsync();

        return Ok(activities.Select(activity => BuildActivityResponse(userId, activity)).ToList());
    }

    [HttpGet("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult<ActivityResponseDto>> GetActivity(Guid id, Guid activityId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var activity = await _db.TripActivities
            .Include(a => a.Owner)
            .Include(a => a.AssignedTo)
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);

        if (activity == null) return NotFound();

        return Ok(BuildActivityResponse(userId, activity));
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

        var activity = new TripActivity
        {
            TripId = id,
            Date = dto.Date,
            Title = dto.Title.Trim(),
            Description = NormalizeOptionalText(dto.Description),
            Time = NormalizeOptionalText(dto.Time),
            Category = NormalizeOptionalText(dto.Category),
            ImageUrl = NormalizeOptionalText(dto.ImageUrl),
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

        return Ok(BuildActivityResponse(userId, created));
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
        if (!CanEditActivity(userId, activity)) return Forbid();

        var nextVisibility = dto.Visibility != null ? NormalizeVisibility(dto.Visibility) : activity.Visibility;
        var nextRevealAt = dto.ClearRevealAt ? null : dto.RevealAt ?? activity.RevealAt;
        var nextTeaser = dto.ClearTeaser ? null : dto.Teaser != null ? NormalizeOptionalText(dto.Teaser) : activity.Teaser;
        var nextTeaserOffset = dto.ClearTeaserOffset ? null : dto.TeaserOffsetMinutes ?? activity.TeaserOffsetMinutes;
        var validationError = ValidateActivityPayload(nextVisibility, nextRevealAt, nextTeaser, nextTeaserOffset);

        if (validationError != null)
            return BadRequest(validationError);

        if (dto.Date.HasValue) activity.Date = dto.Date.Value;
        if (dto.Title != null) activity.Title = dto.Title.Trim();
        if (dto.Description != null) activity.Description = NormalizeOptionalText(dto.Description);
        if (dto.Time != null) activity.Time = NormalizeOptionalText(dto.Time);
        if (dto.Category != null) activity.Category = NormalizeOptionalText(dto.Category);
        if (dto.ClearImage) activity.ImageUrl = null;
        else if (dto.ImageUrl != null) activity.ImageUrl = NormalizeOptionalText(dto.ImageUrl);

        activity.Visibility = nextVisibility;
        activity.IsHidden = nextVisibility == "hidden";
        activity.RevealAt = nextVisibility == "hidden" ? nextRevealAt : null;
        activity.Teaser = nextVisibility == "hidden" ? nextTeaser : null;
        activity.TeaserOffsetMinutes = nextVisibility == "hidden" ? nextTeaserOffset : null;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── DELETE /api/trips/{id}/activities/{activityId} ────────────────────────

    [HttpDelete("{id}/activities/{activityId}")]
    [Authorize]
    public async Task<ActionResult> DeleteActivity(Guid id, Guid activityId)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == id);
        if (activity == null) return NotFound();
        if (!CanEditActivity(userId, activity)) return Forbid();

        _db.TripActivities.Remove(activity);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
