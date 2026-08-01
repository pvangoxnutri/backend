using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services;

/// <summary>
/// The rules that decide whether a trip edit is allowed and where it lands.
///
/// Extracted from TripsController so there is exactly ONE definition of each,
/// now that a second caller exists: applying a Gluno proposal must go through
/// the same permission check, the same date-range rule, the same stay
/// validation and the same sort-index placement as the ordinary endpoints.
/// A parallel implementation for the assistant would be the fastest way to end
/// up with an "AI path" that quietly allows something the normal path forbids.
///
/// TripsController keeps thin private forwarders to these, so its call sites
/// read exactly as before.
/// </summary>
public static class TripEditRules
{
    public static Task<bool> IsTripMemberAsync(AppDbContext db, Guid tripId, Guid userId, CancellationToken ct = default)
        => db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct);

    /// <summary>
    /// A member may add or reorder activities when the owner allows
    /// collaborative editing, or when the member is itself a trip owner.
    ///
    /// SideQuests are the exception — editing or deleting THOSE is always
    /// creator-only, checked separately at each call site.
    /// </summary>
    public static async Task<bool> CanEditActivitiesAsync(
        AppDbContext db, Guid tripId, Guid userId, CancellationToken ct = default)
    {
        if (!await IsTripMemberAsync(db, tripId, userId, ct)) return false;

        var trip = await db.Trips.FindAsync([tripId], ct);
        if (trip == null) return false;
        if (trip.MembersCanEdit) return true;

        return await db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId && tm.IsOwner, ct);
    }

    /// <summary>
    /// Picks the SortIndex for an activity being created or edited so that —
    /// only when it has a Time — it lands chronologically among same-day
    /// siblings that also have a Time. Siblings without a Time, and the
    /// relative order between timed siblings once placed, are left alone, so a
    /// later drag-to-reorder fully overrides this.
    ///
    /// Shifts existing siblings' SortIndex itself (and saves that); the caller
    /// still saves the target activity's own new SortIndex.
    /// </summary>
    public static async Task<int> InsertChronologicallyAsync(
        AppDbContext db, Guid tripId, DateOnly date, string? time, Guid? excludeActivityId, CancellationToken ct = default)
    {
        var siblingsQuery = db.TripActivities.Where(a => a.TripId == tripId && a.Date == date);
        if (excludeActivityId.HasValue)
        {
            siblingsQuery = siblingsQuery.Where(a => a.Id != excludeActivityId.Value);
        }

        if (string.IsNullOrEmpty(time))
        {
            return (await siblingsQuery.Select(a => (int?)a.SortIndex).MaxAsync(ct) ?? -1) + 1;
        }

        var siblings = await siblingsQuery.OrderBy(a => a.SortIndex).ToListAsync(ct);

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
            await db.SaveChangesAsync(ct);
        }

        return insertAt;
    }

    /// <summary>
    /// Multi-day stays (hotels): Date/Time is check-in, EndDate/EndTime
    /// check-out. Returns a user-facing message, or null when valid.
    /// </summary>
    public static string? ValidateStayRange(DateOnly date, DateOnly? endDate, string? endTime, Trip trip)
    {
        if (!endDate.HasValue)
        {
            return endTime != null ? "Check-out time requires a check-out date." : null;
        }
        if (endDate.Value <= date)
            return "Check-out must be after check-in.";
        // An open-ended adventure has no closing date for a stay to exceed —
        // only the planning horizon that keeps every downstream loop finite.
        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, endDate.Value))
        {
            return trip.EndDate.HasValue
                ? $"Check-out must be within the trip dates ({trip.StartDate:yyyy-MM-dd} – {trip.EndDate.Value:yyyy-MM-dd})."
                : $"Check-out must be on or after the start date ({trip.StartDate:yyyy-MM-dd}).";
        }
        return null;
    }

    public static string? ValidateTripDateRange(DateOnly? startDate, DateOnly? endDate)
    {
        if (startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value)
        {
            return "End date must be the same day or later than start date.";
        }

        return null;
    }

    /// <summary>
    /// Activities that a proposed date range would strand.
    ///
    /// Removing the end date only ever WIDENS the range, so nothing can be
    /// stranded by it — only the lower bound, and an end date that is actually
    /// set, can leave an activity outside.
    /// </summary>
    public static Task<List<TripActivity>> FindStrandedActivitiesAsync(
        AppDbContext db, Guid tripId, DateOnly startDate, DateOnly? endDate, CancellationToken ct = default)
        => db.TripActivities
            .Where(a => a.TripId == tripId
                        && (a.Date < startDate || (endDate != null && a.Date > endDate)))
            .OrderBy(a => a.Date)
            .ToListAsync(ct);

    /// Both must be present and inside valid ranges — a half-set or bogus pair
    /// is treated as "no coordinates" rather than rejected.
    public static bool IsValidCoordinate(double? latitude, double? longitude)
        => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
}
