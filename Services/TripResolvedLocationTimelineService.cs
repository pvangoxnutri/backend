using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services;

/// <summary>
/// Where a trip is on each of its days, loaded and resolved in one place.
///
/// WHY THIS EXISTS, WHEN <see cref="TripDayLocationService.ResolveTimeline"/>
/// ALREADY DID THE HARD PART. Because calling the same function is not the same
/// as agreeing. Weather loaded its rows one way and Gluno loaded them another,
/// and both then handed their own list to one pure resolver. A test giving each
/// a hand-built list proves the resolver is deterministic, which was never in
/// question — it cannot prove the two callers pass the same thing.
///
/// The moment the two loaders differ at all — a Take() that clips, a date range
/// computed differently, a filter one side has and the other does not — the app
/// shows one set of cities on the weather screen and Gluno describes another.
/// The user cannot see which layer disagreed; they just see an assistant that
/// does not know where they are going.
///
/// So the loading moves in here with the resolving. Both callers pass a trip id
/// and a date range and get the same answer, by construction rather than by
/// review.
/// </summary>
public sealed record ResolvedTripTimeline
{
    public required Trip Trip { get; init; }

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    /// <summary>
    /// Every stored location row for the trip — main locations AND extra
    /// stops.
    ///
    /// Handed back rather than kept private because the two consumers need
    /// different halves: weather builds a forecast per row, Gluno builds a
    /// chain of runs plus the extra stops that hang off single days.
    /// </summary>
    public required IReadOnlyList<TripDayLocation> DayLocations { get; init; }

    /// <summary>
    /// One entry per calendar day in the range. Never fewer, so no caller has
    /// to guess whether a day was silently skipped. Null means the day has no
    /// anchor and the trip has no destination coordinates either.
    /// </summary>
    public required IReadOnlyList<ResolvedDayLocation?> Days { get; init; }

    /// Rows that anchor a day and carry forward.
    public int MainLocationCount => DayLocations.Count(row => row.SortIndex == 0);

    /// Rows that apply to their own day only.
    public int ExtraStopCount => DayLocations.Count(row => row.SortIndex > 0);

    /// Days that resolved to a real place rather than to nothing.
    public int ResolvedDayCount => Days.Count(day => day != null);

    /// <summary>
    /// True when every resolved day came from the trip's own destination
    /// rather than from a stored row.
    ///
    /// The ONE case where "I only know the country" is an honest answer. Any
    /// other time an assistant saying that is a bug.
    /// </summary>
    public bool IsDestinationOnly => MainLocationCount == 0;

    /// Days inside the range with no location at all.
    public int UnplacedDayCount => Days.Count(day => day == null);
}

public interface ITripResolvedLocationTimelineService
{
    /// <summary>
    /// The trip's resolved timeline, or null when the trip does not exist.
    /// </summary>
    /// <param name="endOverride">
    /// A ceiling on how far to walk. Weather passes the forecast horizon,
    /// because past it there are no numbers to show. Null means the trip's own
    /// end date, or a bounded window for an open-ended trip.
    /// </param>
    Task<ResolvedTripTimeline?> BuildAsync(
        Guid tripId, DateOnly? endOverride, CancellationToken ct);
}

public sealed class TripResolvedLocationTimelineService : ITripResolvedLocationTimelineService
{
    /// <summary>
    /// How far an open-ended trip is walked when the caller sets no ceiling.
    ///
    /// A trip with no end date must not produce an unbounded timeline just
    /// because somebody asked where it goes.
    /// </summary>
    public const int MaxOpenEndedDays = 60;

    private readonly AppDbContext _db;
    private readonly TripDayLocationService _resolver = new();

    public TripResolvedLocationTimelineService(AppDbContext db) => _db = db;

    public async Task<ResolvedTripTimeline?> BuildAsync(
        Guid tripId, DateOnly? endOverride, CancellationToken ct)
    {
        var trip = await _db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tripId, ct);
        if (trip == null) return null;

        // ── THE query ─────────────────────────────────────────────────────
        //
        // No Take, no projection, no date filter. Every row for the trip, so
        // there is no clipping rule for two callers to disagree about — this
        // is exactly the shape of bug the service exists to make impossible.
        //
        // The row count is bounded by the trip's own length in practice; a
        // trip cannot have more day locations than it has days plus its extra
        // stops, and both are small.
        var dayLocations = await _db.TripDayLocations
            .AsNoTracking()
            .Where(row => row.TripId == tripId)
            .OrderBy(row => row.StartDate)
            .ThenBy(row => row.SortIndex)
            .ToListAsync(ct);

        var end = endOverride
            ?? trip.EndDate
            ?? trip.StartDate.AddDays(MaxOpenEndedDays);

        // A backwards range would make the resolver iterate nothing and look
        // like a trip with no places at all.
        if (end < trip.StartDate) end = trip.StartDate;

        var days = _resolver.ResolveTimeline(
            trip.StartDate, end, dayLocations,
            trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude);

        return new ResolvedTripTimeline
        {
            Trip = trip,
            StartDate = trip.StartDate,
            EndDate = end,
            DayLocations = dayLocations,
            Days = days,
        };
    }
}
