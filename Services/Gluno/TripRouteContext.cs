using System.Globalization;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One place the trip actually is, and the days it is there.
///
/// The unit the whole route is made of. A stop is a RUN of days, not a day:
/// "Málaga 5–7 August" is what somebody would say, and three identical rows is
/// what a database holds.
/// </summary>
public sealed record TripRouteStop
{
    /// Position in the chain, from 0. What "the next stop" is counted from.
    public required int Index { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// Only when SideQuest actually stores it. Never inferred from a place
    /// name — "Tanger" does not tell us Morocco, and guessing is how a plan
    /// acquires a border crossing nobody is making.
    /// </summary>
    public string? Country { get; init; }

    /// Inclusive, ISO. A single-day stop has both the same.
    public required string From { get; init; }
    public required string To { get; init; }

    /// Every date this stop covers, so a day question resolves without the
    /// model doing date arithmetic on a range.
    public IReadOnlyList<string> Dates { get; init; } = Array.Empty<string>();

    /// <summary>
    /// False for an extra stop — an afternoon somewhere, which applies to its
    /// own day and no other. Spreading one across a stay is how an
    /// hour-long detour becomes a destination.
    /// </summary>
    public bool IsMainStop { get; init; } = true;

    /// True when a row explicitly names this place on this date, false when it
    /// carries forward from an earlier day.
    public bool IsExplicit { get; init; }

    /// <remarks>day_location | extra_stop | activity | stay | trip_destination</remarks>
    public required string Source { get; init; }

    /// <summary>
    /// Present for a real place, absent for the trip-destination fallback.
    ///
    /// Used server-side for corridors and distances. Never rendered: a
    /// coordinate on screen is noise, and one in a sentence is false
    /// precision.
    /// </summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

/// <summary>
/// The journey between two consecutive stops.
///
/// WHY THIS IS ITS OWN THING. "What's worth seeing between Málaga and Ronda"
/// is not a question about either city — it is a question about the space
/// between them, and a model given only a list of stops has to infer that the
/// space exists. Naming the legs makes "on the way" answerable without
/// guessing which two places were meant.
///
/// Deterministic and free. Nothing here calls a routing provider; it is
/// context for understanding the question, not an answer to it.
/// </summary>
public sealed record TripRouteLeg
{
    public required int Index { get; init; }

    public required string FromLabel { get; init; }
    public required string ToLabel { get; init; }

    /// The last day at the origin, and the first at the destination. On a
    /// same-day move both are that day.
    public required string DepartureDate { get; init; }
    public required string ArrivalDate { get; init; }

    public double? FromLatitude { get; init; }
    public double? FromLongitude { get; init; }
    public double? ToLatitude { get; init; }
    public double? ToLongitude { get; init; }

    /// <summary>
    /// Straight-line kilometres, when both ends have coordinates.
    ///
    /// A LOWER BOUND ON THE JOURNEY AND NOTHING ELSE. It is not a driving
    /// distance and must never be quoted as one — the ledger rules on numbers
    /// apply to this exactly as they do everywhere else.
    /// </summary>
    public double? StraightLineKm { get; init; }

    /// <summary>
    /// Only when SideQuest stores a country for BOTH ends and they differ.
    /// Null means unknown, which is not the same as "no".
    /// </summary>
    public bool? CrossesBorder { get; init; }

    /// <summary>
    /// Transport the user has already planned on the departure day — a flight,
    /// a ferry, a train. Titles only, from their own Activities.
    ///
    /// This is what makes "how do we get to Tanger" answerable from the plan
    /// instead of from a guess.
    /// </summary>
    public IReadOnlyList<string> TransportOnDay { get; init; } = Array.Empty<string>();

    /// True when something on the departure day is a fixed booking, so a
    /// suggested stop on the way cannot be placed without regard to it.
    public bool HasFixedBookingOnDay { get; init; }
}

/// <summary>
/// Where the trip goes, in order, with the journeys in between.
///
/// WHY THIS EXISTS SEPARATELY FROM THE TRIP CONTEXT. The route used to live
/// inside <see cref="GlunoTripContext"/>, which is loaded all-or-nothing based
/// on the turn's intent. On any turn whose intent did not need the full plan —
/// app help, a navigation request, a preference update — the trip context was
/// not built at all, and the model was left with the Adventure SUMMARY: title,
/// `Trip.Destination`, and the dates.
///
/// That is precisely the failure this type fixes. Asked where the trip went,
/// Gluno answered "I only have España and the dates 5–16 August" — while
/// SideQuest knew six cities and showed their weather on the same screen.
///
/// So the route is now loaded whenever the conversation is scoped to an
/// Adventure, regardless of what the intent asked for, and travels as its own
/// critical context section. It is small — a handful of stops — and its absence
/// makes Gluno ask questions the Adventure already answers.
///
/// BUILT FROM THE SAME SELECTOR AS THE WEATHER. <see cref="TripDayLocationService.ResolveTimeline"/>
/// is what the Adventure's weather, its slideshow and its feed all read. A
/// second interpretation of "where is the trip on this day" would drift, and
/// then two surfaces of the same app would disagree about somebody's holiday.
/// </summary>
public sealed record TripRouteContext
{
    /// <summary>
    /// How far an open-ended trip is walked.
    ///
    /// Weather stops at the forecast horizon because past it there are no
    /// numbers. Here the limit is only about context size — a route is a few
    /// dozen tokens per stop, and an open-ended trip must not produce an
    /// unbounded chain.
    /// </summary>
    public const int MaxOpenEndedDays = 60;

    public required string StartDate { get; init; }
    public string? EndDate { get; init; }

    /// Chronological. Index 0 is where the trip begins.
    public IReadOnlyList<TripRouteStop> Stops { get; init; } = Array.Empty<TripRouteStop>();

    /// One per transition between consecutive main stops. Empty on a
    /// single-city trip, which is the common case.
    public IReadOnlyList<TripRouteLeg> Legs { get; init; } = Array.Empty<TripRouteLeg>();

    /// <summary>
    /// Dates inside the trip with no location at all.
    ///
    /// Stated plainly so Gluno can say a day is unplanned rather than assuming
    /// it continues the last one — and so "I don't know where you are" is
    /// reserved for days where that is actually true.
    /// </summary>
    public IReadOnlyList<string> DaysWithoutLocation { get; init; } = Array.Empty<string>();

    /// <summary>
    /// True when the chain is nothing but the trip's own destination.
    ///
    /// The one case where "I only know the country" is an honest answer. Any
    /// other time Gluno saying that is a bug, and this flag is what lets the
    /// prompt draw that line.
    /// </summary>
    public bool IsDestinationOnly => Stops.Count <= 1
        && Stops.All(stop => stop.Source == "trip_destination");

    /// True when there is more than one place, so a broad question genuinely
    /// needs to know which part of the trip it is about.
    public bool HasMultipleStops => Stops.Count(stop => stop.IsMainStop) > 1;

    /// <summary>
    /// A stable digest of the chain, for detecting that the route moved.
    ///
    /// WHAT IT IS FOR. A route-leg card offers "Málaga → Ronda". If the user
    /// then changes the 8th from Ronda to Córdoba and taps the card, that leg
    /// no longer exists — and honouring the tap would search a stretch of road
    /// nobody is driving. Comparing the fingerprint at resolve time catches
    /// exactly that.
    ///
    /// Built from label, dates and kind for every stop, in order. Coordinates
    /// are excluded deliberately: a geocoder refining Málaga by fifty metres
    /// is not a change to the route, and treating it as one would make every
    /// card stale for no reason.
    /// </summary>
    public string Fingerprint => string.Join(
        '|',
        Stops.Select(stop => $"{stop.Label}:{stop.From}:{stop.To}:{(stop.IsMainStop ? 'm' : 'x')}"));
}

/// <summary>
/// Builds the route from what SideQuest actually stores.
///
/// Deterministic and pure: same rows in, same route out. No provider, no model,
/// no database.
///
/// THE PRIORITY ORDER IS THE POINT. The resolved per-day timeline first —
/// exactly what the weather reads — then explicit extra stops, then activity
/// and stay locations for days the timeline could not resolve. `Trip.Destination`
/// is the last resort and never replaces a real chain: a trip that stores six
/// cities must never be described as "España".
/// </summary>
public static class TripRouteResolver
{
    public static TripRouteContext Build(
        Trip trip,
        IReadOnlyList<TripDayLocation> dayLocations,
        IReadOnlyList<GlunoActivityContext> activities)
    {
        var start = trip.StartDate;
        var end = trip.EndDate ?? start.AddDays(TripRouteContext.MaxOpenEndedDays);

        // THE shared selector. Carry-forward, explicitness and the
        // trip-destination fallback all behave exactly as the weather shows
        // them, because this is the same call the weather makes.
        var timeline = new TripDayLocationService().ResolveTimeline(
            start, end, dayLocations,
            trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude);

        var stops = new List<TripRouteStop>();
        var missing = new List<string>();

        // ── Main locations, collapsed into runs ───────────────────────────
        TripRouteStop? open = null;
        var openDates = new List<string>();

        for (var index = 0; index < timeline.Count; index++)
        {
            var day = timeline[index];
            var date = start.AddDays(index);
            var iso = Iso(date);

            if (day == null)
            {
                if (open != null) { stops.Add(open with { Dates = openDates.ToList() }); open = null; }
                missing.Add(iso);
                continue;
            }

            if (open != null && open.Label == day.LocationLabel)
            {
                open = open with { To = iso };
                openDates.Add(iso);
                continue;
            }

            if (open != null) stops.Add(open with { Dates = openDates.ToList() });

            openDates = [iso];
            open = new TripRouteStop
            {
                Index = 0,
                Label = day.LocationLabel,
                From = iso,
                To = iso,
                IsMainStop = true,
                IsExplicit = day.IsExplicit,
                Source = day.IsExplicit ? "day_location" : "trip_destination",
                Latitude = day.Latitude,
                Longitude = day.Longitude,
            };
        }

        if (open != null) stops.Add(open with { Dates = openDates.ToList() });

        // ── Extra stops ───────────────────────────────────────────────────
        //
        // A day's additional places apply to that day and no other. Flagged, so
        // an afternoon cannot be spread across a stay.
        foreach (var extra in dayLocations.Where(row => row.SortIndex > 0).OrderBy(row => row.StartDate))
        {
            var iso = Iso(extra.StartDate);

            stops.Add(new TripRouteStop
            {
                Index = 0,
                Label = extra.LocationLabel,
                From = iso,
                To = iso,
                Dates = [iso],
                IsMainStop = false,
                IsExplicit = true,
                Source = "extra_stop",
                Latitude = extra.Latitude,
                Longitude = extra.Longitude,
            });
        }

        // ── Days the timeline could not place ─────────────────────────────
        //
        // A stay first, then an activity: a hotel is a statement about where
        // somebody sleeps, an activity only about where one thing happens.
        // Both need a REAL location — description text contributes nothing.
        var covered = stops.Select(stop => stop.From).ToHashSet(StringComparer.Ordinal);

        foreach (var source in new[] { "stay", "activity" })
        {
            foreach (var activity in activities.Where(row => row.Role == source || source == "activity"))
            {
                if (activity.LocationLabel is not { Length: > 0 } label) continue;

                var iso = Iso(activity.Date);
                if (!missing.Contains(iso) || covered.Contains(iso)) continue;

                stops.Add(new TripRouteStop
                {
                    Index = 0,
                    Label = label,
                    From = iso,
                    To = iso,
                    Dates = [iso],
                    IsMainStop = true,
                    IsExplicit = false,
                    Source = source,
                    Latitude = activity.Latitude,
                    Longitude = activity.Longitude,
                });

                covered.Add(iso);
                missing.Remove(iso);
            }
        }

        var ordered = stops
            .OrderBy(stop => stop.From, StringComparer.Ordinal)
            .ThenBy(stop => stop.IsMainStop ? 0 : 1)
            .Select((stop, index) => stop with { Index = index })
            .ToList();

        return new TripRouteContext
        {
            StartDate = Iso(start),
            EndDate = trip.EndDate.HasValue ? Iso(trip.EndDate.Value) : null,
            Stops = ordered,
            Legs = BuildLegs(ordered, activities),
            DaysWithoutLocation = missing,
        };
    }

    /// <summary>
    /// One leg per transition between consecutive MAIN stops.
    ///
    /// Extra stops are excluded: an afternoon in a village between two cities
    /// is not a leg of the journey, and treating it as one would turn a
    /// five-city trip into an eleven-leg itinerary nobody is travelling.
    /// </summary>
    private static IReadOnlyList<TripRouteLeg> BuildLegs(
        IReadOnlyList<TripRouteStop> stops, IReadOnlyList<GlunoActivityContext> activities)
    {
        var main = stops.Where(stop => stop.IsMainStop).ToList();
        if (main.Count < 2) return Array.Empty<TripRouteLeg>();

        var legs = new List<TripRouteLeg>();

        for (var index = 0; index < main.Count - 1; index++)
        {
            var from = main[index];
            var to = main[index + 1];

            // The move happens between the last day at the origin and the
            // first at the destination.
            var departure = from.To;
            var arrival = to.From;

            var onDay = activities
                .Where(activity => Iso(activity.Date) == departure || Iso(activity.Date) == arrival)
                .ToList();

            legs.Add(new TripRouteLeg
            {
                Index = index,
                FromLabel = from.Label,
                ToLabel = to.Label,
                DepartureDate = departure,
                ArrivalDate = arrival,
                FromLatitude = from.Latitude,
                FromLongitude = from.Longitude,
                ToLatitude = to.Latitude,
                ToLongitude = to.Longitude,
                StraightLineKm = GeoDistance.KilometresBetween(
                    from.Latitude, from.Longitude, to.Latitude, to.Longitude),
                // Both countries known and different. Null when either is
                // unknown, which is not the same as "no".
                CrossesBorder = from.Country != null && to.Country != null
                    ? !string.Equals(from.Country, to.Country, StringComparison.OrdinalIgnoreCase)
                    : null,
                // Their own planned transport, by title. What makes "how do we
                // get there" answerable from the plan rather than from a guess.
                TransportOnDay = onDay
                    .Where(activity => activity.Role == "transport")
                    .Select(activity => activity.Title)
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                HasFixedBookingOnDay = onDay.Any(activity =>
                    activity.Role == "transport" && !string.IsNullOrWhiteSpace(activity.Time)),
            });
        }

        return legs;
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
