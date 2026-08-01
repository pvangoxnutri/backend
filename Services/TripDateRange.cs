namespace sidequest.backend.Services;

/// <summary>
/// The one place that answers "what finite date range does this trip cover?"
/// when the trip may have no end date at all.
///
/// An adventure with EndDate == null is deliberately open — the traveller does
/// not know when it ends yet — but plenty of code needs a concrete last day:
/// the feed builds one row per day, the day-location resolver walks the range
/// one date at a time, the weather timeline iterates it. None of those can be
/// handed "forever".
///
/// The rule is: go as far as there is a reason to, and no further.
///
///     max(today, latest planned content) capped at start + MaxHorizonDays
///
/// Today, because an ongoing adventure must always render up to now. Latest
/// content, because a deliberately-scheduled future activity has to be
/// reachable. The cap, because both of those are user-supplied and a single
/// mistyped year would otherwise turn one loop into decades of iterations.
///
/// Note what this is NOT: it is never written to the database. Trip.EndDate
/// stays null, and null keeps meaning "unknown" everywhere it is stored or
/// displayed. This is a view-time bound, computed fresh each time, so an
/// open-ended trip's range grows with today rather than freezing at whatever
/// today happened to be when a fake end date got persisted.
/// </summary>
public static class TripDateRange
{
    /// <summary>
    /// How far past the start date an open-ended adventure may be planned.
    /// Two years is well beyond any real trip while still bounding every loop
    /// and every validation to a number that cannot hurt.
    /// </summary>
    public const int MaxHorizonDays = 730;

    /// <summary>
    /// The last date an open-ended trip accepts content on. Also the ceiling
    /// for <see cref="EffectiveEnd"/>, so anything the user is allowed to
    /// create is guaranteed to be renderable.
    /// </summary>
    public static DateOnly PlanningHorizon(DateOnly startDate)
        => startDate.AddDays(MaxHorizonDays);

    /// <summary>
    /// The last day to materialise for a trip.
    ///
    /// A trip WITH an end date returns it unchanged — existing adventures
    /// behave exactly as before, and this helper is a no-op for them.
    /// </summary>
    /// <param name="latestContentDate">
    /// The latest date that already has an activity or a day location, if any.
    /// Pass null when the caller has not loaded that (the range then simply
    /// ends at today, which is always correct for "show me up to now").
    /// </param>
    public static DateOnly EffectiveEnd(
        DateOnly startDate,
        DateOnly? endDate,
        DateOnly today,
        DateOnly? latestContentDate = null)
    {
        if (endDate.HasValue) return endDate.Value;

        var end = startDate;
        if (today > end) end = today;
        if (latestContentDate.HasValue && latestContentDate.Value > end) end = latestContentDate.Value;

        var horizon = PlanningHorizon(startDate);
        return end > horizon ? horizon : end;
    }

    /// <summary>
    /// True when <paramref name="date"/> is a date this trip can hold content
    /// on. An open-ended trip has no real upper bound, only the planning
    /// horizon that keeps every downstream loop finite.
    /// </summary>
    public static bool Contains(DateOnly startDate, DateOnly? endDate, DateOnly date)
    {
        if (date < startDate) return false;
        return endDate.HasValue
            ? date <= endDate.Value
            : date <= PlanningHorizon(startDate);
    }

    /// <summary>
    /// The message shown when a date falls outside the trip. Open-ended trips
    /// get copy that does not name a fake end date.
    /// </summary>
    public static string OutOfRangeMessage(DateOnly startDate, DateOnly? endDate)
        => endDate.HasValue
            ? $"Activity date must be within the trip dates ({startDate:yyyy-MM-dd} – {endDate.Value:yyyy-MM-dd})."
            : $"Activity date must be on or after the start date ({startDate:yyyy-MM-dd}).";
}
