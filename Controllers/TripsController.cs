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

    /// canViewFull: owner, OR public visibility, OR already revealed
    private static bool CanViewFull(Guid userId, Trip trip, List<Guid> ownerIds)
        => ownerIds.Contains(userId)
        || trip.Visibility == "public"
        || IsRevealedNow(trip);

    /// canEdit: must be owner AND not yet revealed
    private static bool CanEdit(Guid userId, Trip trip, List<Guid> ownerIds)
        => ownerIds.Contains(userId) && !IsRevealedNow(trip);

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
            OwnerId = userId,
            Visibility = dto.Visibility == "hidden" ? "hidden" : "public",
            RevealAt = dto.RevealAt,
            Teaser = dto.Teaser,
        };

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

        if (isOwner && !revealed)
        {
            // Owners can edit all fields before reveal
            if (dto.Title != null) trip.Title = dto.Title;
            if (dto.Description != null) trip.Description = dto.Description;
            if (dto.Destination != null) trip.Destination = dto.Destination;
            if (dto.ImageUrl != null) trip.ImageUrl = dto.ImageUrl;
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
            .Select(a => new ActivityResponseDto
            {
                Id = a.Id,
                TripId = a.TripId,
                Date = a.Date,
                Title = a.Title,
                Description = a.Description,
                Time = a.Time,
                Category = a.Category,
                IsHidden = a.IsHidden,
                AssignedToUserId = a.AssignedToUserId,
                AssignedToName = a.AssignedTo != null ? a.AssignedTo.Name : null,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(activities);
    }

    // ── POST /api/trips/{id}/activities ───────────────────────────────────────

    [HttpPost("{id}/activities")]
    [Authorize]
    public async Task<ActionResult<ActivityResponseDto>> AddActivity(Guid id, [FromBody] CreateActivityDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == id && tm.UserId == userId))
            return Forbid();

        var user = await _db.Users.FindAsync(userId);

        var activity = new TripActivity
        {
            TripId = id,
            Date = dto.Date,
            Title = dto.Title,
            Description = dto.Description,
            Time = dto.Time,
            Category = dto.Category,
            AssignedToUserId = userId
        };

        _db.TripActivities.Add(activity);
        await _db.SaveChangesAsync();

        return Ok(new ActivityResponseDto
        {
            Id = activity.Id,
            TripId = activity.TripId,
            Date = activity.Date,
            Title = activity.Title,
            Description = activity.Description,
            Time = activity.Time,
            Category = activity.Category,
            IsHidden = activity.IsHidden,
            AssignedToUserId = activity.AssignedToUserId,
            AssignedToName = user?.Name,
            CreatedAt = activity.CreatedAt
        });
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
        if (activity.AssignedToUserId != userId) return Forbid();

        if (dto.IsHidden.HasValue) activity.IsHidden = dto.IsHidden.Value;

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

        _db.TripActivities.Remove(activity);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
