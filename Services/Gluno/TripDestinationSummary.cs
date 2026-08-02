using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One stop on the trip: a place, and the dates it covers.
/// </summary>
public sealed record TripStop
{
    public required string Label { get; init; }

    /// First and last date this stop covers, inclusive. A single-day stop has
    /// both the same.
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>
    /// Where the place came from. Gluno must be able to tell "the user set
    /// this as the day's location" from "an activity happened to be here" —
    /// the first is a statement about the trip, the second is an inference,
    /// and presenting the second as the first is how a plan acquires a
    /// destination nobody chose.
    /// </summary>
    /// <remarks>day_location | extra_stop | activity | stay | trip_destination</remarks>
    public required string Source { get; init; }

    /// True when a row explicitly names this place on this date, false when it
    /// carries forward from an earlier day.
    public bool IsExplicit { get; init; }

    /// Set only on an extra stop, which applies to its own day and no other.
    public bool AppliesToOneDayOnly { get; init; }
}

/// <summary>
/// Where a trip actually goes, in the order it goes there.
///
/// WHY THIS EXISTS. The context used to carry raw TripDayLocation rows: one
/// entry per stored row, no carry-forward, no ordering beyond a sort index.
/// The Feed showed Málaga across three days because it runs those rows through
/// <see cref="TripDayLocationService.ResolveTimeline"/>; Gluno saw a single
/// row on a single date and had to guess the rest. Asked where the trip went,
/// it could only answer with the trip's title.
///
/// So this is built from the SAME selector the Feed uses. Not a second
/// interpretation of what a day's location means — there is exactly one, and
/// both surfaces read it. A second one would drift, and the two would disagree
/// about somebody's holiday.
///
/// NOTHING HERE IS INFERRED FROM PROSE. Every stop traces to a stored row, a
/// location marker, or the trip's own destination. An activity described as
/// "dinner somewhere near the old town in Ronda" contributes nothing unless it
/// carries a real location — inventing a destination from description text is
/// how Gluno ends up planning a day in a city the trip never visits.
/// </summary>
public sealed record TripDestinationSummary
{
    public required string Title { get; init; }
    public required string StartDate { get; init; }

    /// Null when the trip has no end date yet. Rendered as ongoing rather than
    /// invented.
    public string? EndDate { get; init; }
    public bool IsOngoing => EndDate == null;

    /// Distinct countries, in the order the trip reaches them. Only ever from
    /// the trip's own stored countries — never guessed from a place name.
    public IReadOnlyList<string> Countries { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Every stop in chronological order, with its dates.
    ///
    /// A roadtrip is a sequence, and flattening it to one destination is the
    /// specific failure this type prevents: "Spain" is true and useless when
    /// the answer needed was Ronda on Thursday.
    /// </summary>
    public IReadOnlyList<TripStop> Stops { get; init; } = Array.Empty<TripStop>();

    /// Dates inside the trip with no location at all. Stated plainly so Gluno
    /// can say a day is unplanned rather than assuming it continues the last
    /// one.
    public IReadOnlyList<string> DaysWithoutLocation { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Builds the summary from what SideQuest actually stores.
    ///
    /// Deterministic and pure: same rows in, same summary out. The timeline
    /// comes from the shared resolver, so carry-forward, explicitness and the
    /// trip-destination fallback all behave exactly as the Feed shows them.
    /// </summary>
    public static TripDestinationSummary Build(
        Trip trip,
        IReadOnlyList<TripDayLocation> dayLocations,
        IReadOnlyList<GlunoActivityContext> activities,
        IReadOnlyList<string> countries)
    {
        var start = trip.StartDate;
        var end = trip.EndDate ?? start.AddDays(30);

        var timeline = new TripDayLocationService().ResolveTimeline(
            start, end, dayLocations, trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude);

        var stops = new List<TripStop>();
        var missing = new List<string>();

        // ── Main locations, collapsed into runs ───────────────────────────
        //
        // Consecutive days at the same place are ONE stop, not five. "Málaga
        // 12–14 August" is what somebody would say; five identical rows is
        // what a database holds, and sending those spends context to say the
        // same thing repeatedly.
        TripStop? open = null;

        for (var index = 0; index < timeline.Count; index++)
        {
            var day = timeline[index];
            var date = start.AddDays(index);

            if (day == null)
            {
                if (open != null) { stops.Add(open); open = null; }
                missing.Add(date.ToString("yyyy-MM-dd"));
                continue;
            }

            if (open != null && open.Label == day.LocationLabel)
            {
                open = open with { To = date.ToString("yyyy-MM-dd") };
                continue;
            }

            if (open != null) stops.Add(open);

            open = new TripStop
            {
                Label = day.LocationLabel,
                From = date.ToString("yyyy-MM-dd"),
                To = date.ToString("yyyy-MM-dd"),
                Source = day.IsExplicit ? "day_location" : "trip_destination",
                IsExplicit = day.IsExplicit,
            };
        }

        if (open != null) stops.Add(open);

        // ── Extra stops ───────────────────────────────────────────────────
        //
        // A day's additional places apply to that day and no other. They are
        // added alongside the run rather than merged into it, and flagged, so
        // Gluno cannot spread a single afternoon across the whole stay.
        foreach (var extra in dayLocations.Where(row => row.SortIndex > 0).OrderBy(row => row.StartDate))
        {
            var date = extra.StartDate.ToString("yyyy-MM-dd");

            stops.Add(new TripStop
            {
                Label = extra.LocationLabel,
                From = date,
                To = date,
                Source = "extra_stop",
                IsExplicit = true,
                AppliesToOneDayOnly = true,
            });
        }

        // ── Activity locations ────────────────────────────────────────────
        //
        // Only where a day has no location of its own, and only from a real
        // marker. This is the weakest source and is labelled as such: it says
        // where something happens, not where the trip is.
        var covered = stops.Select(stop => stop.From).ToHashSet(StringComparer.Ordinal);

        foreach (var activity in activities)
        {
            if (activity.LocationLabel is not { Length: > 0 } label) continue;

            var date = activity.Date.ToString("yyyy-MM-dd");
            if (!missing.Contains(date) || covered.Contains(date)) continue;

            stops.Add(new TripStop
            {
                Label = label,
                From = date,
                To = date,
                Source = "activity",
                IsExplicit = false,
            });

            covered.Add(date);
            missing.Remove(date);
        }

        return new TripDestinationSummary
        {
            Title = trip.Title,
            StartDate = start.ToString("yyyy-MM-dd"),
            EndDate = trip.EndDate.HasValue ? end.ToString("yyyy-MM-dd") : null,
            Countries = countries,
            Stops = stops.OrderBy(stop => stop.From, StringComparer.Ordinal).ToList(),
            DaysWithoutLocation = missing,
        };
    }
}
