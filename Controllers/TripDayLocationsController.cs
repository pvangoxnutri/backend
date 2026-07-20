using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

// ── /api/trips/{tripId}/day-locations ─────────────────────────────────────
// "From this day onwards, the travellers are here." Activities never know
// about this table — no ActivityId anywhere — and it never infers a
// location from the itinerary; every row is a location the user explicitly
// picked from Places autocomplete.
[ApiController]
[Route("api/trips/{tripId}/day-locations")]
[Authorize]
public class TripDayLocationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TripDayLocationService _resolver;

    public TripDayLocationsController(AppDbContext db, TripDayLocationService resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsTripMember(Guid tripId, Guid userId)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId);

    // Mirrors TripsController.CanEditActivitiesAsync — day-locations follow
    // the same "who can edit the plan" rule as activities.
    private async Task<bool> CanEditTripPlanAsync(Trip trip, Guid userId)
    {
        if (!await IsTripMember(trip.Id, userId)) return false;
        if (trip.MembersCanEdit) return true;
        var ownerIds = await _db.TripMembers
            .Where(tm => tm.TripId == trip.Id && tm.IsOwner)
            .Select(tm => tm.UserId)
            .ToListAsync();
        return ownerIds.Contains(userId);
    }

    private static bool IsValidCoordinate(double latitude, double longitude)
        => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    // ── GET /api/trips/{tripId}/day-locations ─────────────────────────────
    // Returns the resolved timeline, not just the stored rows — see
    // TripDayLocationService. Empty list for a trip with degenerate dates.

    [HttpGet]
    public async Task<ActionResult<List<TripDayLocationDto>>> GetDayLocations(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await IsTripMember(tripId, userId)) return Forbid();

        if (trip.EndDate < trip.StartDate) return Ok(new List<TripDayLocationDto>());

        var dayLocations = await _db.TripDayLocations
            .Where(d => d.TripId == tripId)
            .ToListAsync(ct);

        var timeline = _resolver.ResolveTimeline(
            trip.StartDate, trip.EndDate, dayLocations,
            trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude);

        var result = timeline
            .Where(t => t != null)
            .Select(t => new TripDayLocationDto
            {
                Date = t!.Date,
                LocationLabel = t.LocationLabel,
                Latitude = t.Latitude,
                Longitude = t.Longitude,
                PlaceId = t.PlaceId,
                IsExplicit = t.IsExplicit,
            })
            .ToList();

        return Ok(result);
    }

    // ── PUT /api/trips/{tripId}/day-locations/{date} ──────────────────────
    // Upsert: sets or replaces the location for exactly this date. Updates
    // in place when a row already exists, so CreatedByUserId/CreatedAt keep
    // meaning "who first claimed this date" rather than "who most recently
    // touched it."

    [HttpPut("{date}")]
    public async Task<ActionResult<TripDayLocationDto>> SetDayLocation(
        Guid tripId, DateOnly date, [FromBody] SetTripDayLocationDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        if (date < trip.StartDate || date > trip.EndDate)
            return BadRequest("Date must fall within the trip's dates.");
        if (string.IsNullOrWhiteSpace(dto.LocationLabel))
            return BadRequest("A location label is required.");
        if (!IsValidCoordinate(dto.Latitude, dto.Longitude))
            return BadRequest("Invalid coordinates.");

        var label = dto.LocationLabel.Trim();
        var placeId = string.IsNullOrWhiteSpace(dto.PlaceId) ? null : dto.PlaceId.Trim();
        var now = DateTime.UtcNow;

        var existing = await _db.TripDayLocations
            .FirstOrDefaultAsync(d => d.TripId == tripId && d.StartDate == date, ct);

        if (existing == null)
        {
            existing = new TripDayLocation
            {
                TripId = tripId,
                StartDate = date,
                LocationLabel = label,
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                PlaceId = placeId,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.TripDayLocations.Add(existing);
        }
        else
        {
            existing.LocationLabel = label;
            existing.Latitude = dto.Latitude;
            existing.Longitude = dto.Longitude;
            existing.PlaceId = placeId;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new TripDayLocationDto
        {
            Date = date,
            LocationLabel = label,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            PlaceId = placeId,
            IsExplicit = true,
        });
    }

    // ── DELETE /api/trips/{tripId}/day-locations/{date} ───────────────────
    // Idempotent — succeeds whether or not a row existed for this date, so
    // the "remove" action in the editor never has to check first.

    [HttpDelete("{date}")]
    public async Task<ActionResult> DeleteDayLocation(Guid tripId, DateOnly date, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        var existing = await _db.TripDayLocations
            .FirstOrDefaultAsync(d => d.TripId == tripId && d.StartDate == date, ct);
        if (existing != null)
        {
            _db.TripDayLocations.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
