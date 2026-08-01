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

    private static DateOnly? MaxDate(DateOnly? left, DateOnly? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value > right.Value ? left : right;
    }

    private static TripDayLocationEntryDto ToEntryDto(TripDayLocation row) => new()
    {
        Id = row.Id,
        TripId = row.TripId,
        StartDate = row.StartDate,
        SortIndex = row.SortIndex,
        LocationLabel = row.LocationLabel,
        Latitude = row.Latitude,
        Longitude = row.Longitude,
        PlaceId = row.PlaceId,
    };

    /// <summary>
    /// Rewrites SortIndex to 0,1,2… for one date, in the order given. The
    /// single place contiguity is enforced, so removing the main location
    /// promotes the next place to 0 by construction rather than by a special
    /// case somewhere. Callers must have already loaded/mutated the rows.
    /// </summary>
    private static void Reindex(IEnumerable<TripDayLocation> orderedRowsForDate, DateTime now)
    {
        var index = 0;
        foreach (var row in orderedRowsForDate)
        {
            if (row.SortIndex != index)
            {
                row.SortIndex = index;
                row.UpdatedAt = now;
            }
            index++;
        }
    }

    private Task<List<TripDayLocation>> LoadDateRowsAsync(Guid tripId, DateOnly date, CancellationToken ct)
        => _db.TripDayLocations
            .Where(d => d.TripId == tripId && d.StartDate == date)
            .OrderBy(d => d.SortIndex)
            .ToListAsync(ct);

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

        // An open-ended adventure has no stored last day to walk to, so the
        // timeline is resolved up to a derived one: today, or the furthest day
        // that already has something planned on it. The resolver walks a date
        // at a time, so handing it "no end" would not terminate.
        var latestActivityDate = await _db.TripActivities
            .Where(a => a.TripId == tripId)
            .Select(a => (DateOnly?)a.Date)
            .MaxAsync(ct);
        var latestContentDate = MaxDate(
            latestActivityDate,
            dayLocations.Count > 0 ? dayLocations.Max(d => d.StartDate) : null);

        var rangeEnd = TripDateRange.EffectiveEnd(
            trip.StartDate, trip.EndDate, DateOnly.FromDateTime(DateTime.UtcNow), latestContentDate);

        var timeline = _resolver.ResolveTimeline(
            trip.StartDate, rangeEnd, dayLocations,
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

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return BadRequest("Date must fall within the trip's dates.");
        if (string.IsNullOrWhiteSpace(dto.LocationLabel))
            return BadRequest("A location label is required.");
        if (!IsValidCoordinate(dto.Latitude, dto.Longitude))
            return BadRequest("Invalid coordinates.");

        var label = dto.LocationLabel.Trim();
        var placeId = string.IsNullOrWhiteSpace(dto.PlaceId) ? null : dto.PlaceId.Trim();
        var now = DateTime.UtcNow;

        // The date's MAIN location is SortIndex 0 — this endpoint has always
        // meant "set this day's location" and still does. Any additional places
        // on the same date are left exactly where they are.
        var existing = await _db.TripDayLocations
            .FirstOrDefaultAsync(d => d.TripId == tripId && d.StartDate == date && d.SortIndex == 0, ct);

        if (existing == null)
        {
            existing = new TripDayLocation
            {
                TripId = tripId,
                StartDate = date,
                SortIndex = 0,
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

        // Clears the WHOLE date, additional places included: "remove this day's
        // location" cannot leave stops behind that would then silently promote
        // one of themselves to be the day's main location.
        var rows = await LoadDateRowsAsync(tripId, date, ct);
        if (rows.Count > 0)
        {
            _db.TripDayLocations.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    // ── GET /api/trips/{tripId}/day-locations/entries ─────────────────────
    // The STORED rows, not the resolved timeline: every place the user has
    // actually added, ordered by date then position. This is what an editor
    // needs — the timeline above cannot address a single place by id.

    [HttpGet("entries")]
    public async Task<ActionResult<List<TripDayLocationEntryDto>>> GetEntries(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await IsTripMember(tripId, userId)) return Forbid();

        var rows = await _db.TripDayLocations
            .Where(d => d.TripId == tripId)
            .OrderBy(d => d.StartDate)
            .ThenBy(d => d.SortIndex)
            .ToListAsync(ct);

        return Ok(rows.Select(ToEntryDto).ToList());
    }

    // ── POST /api/trips/{tripId}/day-locations/entries ────────────────────
    // Appends an ADDITIONAL place to a date. The server picks SortIndex, so a
    // client cannot create a gap or collide with an existing position.

    [HttpPost("entries")]
    public async Task<ActionResult<TripDayLocationEntryDto>> AddEntry(
        Guid tripId, [FromBody] AddTripDayLocationDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, dto.StartDate))
            return BadRequest("Date must fall within the trip's dates.");
        if (string.IsNullOrWhiteSpace(dto.LocationLabel))
            return BadRequest("A location label is required.");
        if (!IsValidCoordinate(dto.Latitude, dto.Longitude))
            return BadRequest("Invalid coordinates.");

        var now = DateTime.UtcNow;
        var rows = await LoadDateRowsAsync(tripId, dto.StartDate, ct);

        var row = new TripDayLocation
        {
            TripId = tripId,
            StartDate = dto.StartDate,
            SortIndex = rows.Count,
            LocationLabel = dto.LocationLabel.Trim(),
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            PlaceId = string.IsNullOrWhiteSpace(dto.PlaceId) ? null : dto.PlaceId.Trim(),
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.TripDayLocations.Add(row);

        // Defensive: an older row set could carry a gap from before SortIndex
        // existed. Reindexing the whole date here keeps the invariant true even
        // then, without a separate repair pass.
        Reindex([.. rows, row], now);

        await _db.SaveChangesAsync(ct);
        return Ok(ToEntryDto(row));
    }

    // ── PATCH /api/trips/{tripId}/day-locations/entries/{entryId} ─────────
    // Edits one specific place. Position and date are untouched — moving is
    // the reorder endpoint's job.

    [HttpPatch("entries/{entryId:guid}")]
    public async Task<ActionResult<TripDayLocationEntryDto>> UpdateEntry(
        Guid tripId, Guid entryId, [FromBody] UpdateTripDayLocationEntryDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        if (string.IsNullOrWhiteSpace(dto.LocationLabel))
            return BadRequest("A location label is required.");
        if (!IsValidCoordinate(dto.Latitude, dto.Longitude))
            return BadRequest("Invalid coordinates.");

        // Scoped by tripId as well as id: an entry id from another trip must
        // read as "not found", never as something this caller may edit.
        var row = await _db.TripDayLocations
            .FirstOrDefaultAsync(d => d.Id == entryId && d.TripId == tripId, ct);
        if (row == null) return NotFound();

        row.LocationLabel = dto.LocationLabel.Trim();
        row.Latitude = dto.Latitude;
        row.Longitude = dto.Longitude;
        row.PlaceId = string.IsNullOrWhiteSpace(dto.PlaceId) ? null : dto.PlaceId.Trim();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ToEntryDto(row));
    }

    // ── DELETE /api/trips/{tripId}/day-locations/entries/{entryId} ────────
    // Removes one place and reindexes the rest of that date, so deleting the
    // main location promotes the next place to SortIndex 0.

    [HttpDelete("entries/{entryId:guid}")]
    public async Task<ActionResult> DeleteEntry(Guid tripId, Guid entryId, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        var row = await _db.TripDayLocations
            .FirstOrDefaultAsync(d => d.Id == entryId && d.TripId == tripId, ct);
        // Idempotent, like the by-date delete above.
        if (row == null) return NoContent();

        var date = row.StartDate;
        var rows = await LoadDateRowsAsync(tripId, date, ct);

        _db.TripDayLocations.Remove(row);
        Reindex(rows.Where(r => r.Id != entryId), DateTime.UtcNow);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── PATCH /api/trips/{tripId}/day-locations/entries/reorder ───────────
    // Rewrites a date's whole order from the id list, mirroring how activity
    // reordering works. Whatever ends up first becomes the day's main
    // location and therefore the one that carries forward.

    [HttpPatch("entries/reorder")]
    public async Task<ActionResult<List<TripDayLocationEntryDto>>> ReorderEntries(
        Guid tripId, [FromBody] ReorderTripDayLocationsDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await CanEditTripPlanAsync(trip, userId)) return Forbid();

        var rows = await LoadDateRowsAsync(tripId, dto.StartDate, ct);
        if (rows.Count == 0) return NotFound();

        // The list must name exactly this date's places — no more, no fewer.
        // A partial list would leave the unnamed ones at stale positions and
        // silently break contiguity.
        var requested = dto.LocationIds ?? [];
        if (requested.Count != rows.Count || requested.Distinct().Count() != requested.Count
            || requested.Any(id => rows.All(r => r.Id != id)))
        {
            return BadRequest("The id list must contain every place for this date exactly once.");
        }

        var byId = rows.ToDictionary(r => r.Id);
        Reindex(requested.Select(id => byId[id]), DateTime.UtcNow);

        await _db.SaveChangesAsync(ct);

        return Ok(rows.OrderBy(r => r.SortIndex).Select(ToEntryDto).ToList());
    }
}
