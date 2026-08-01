using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One thing SideQuest noticed about a plan.
///
/// Structured, not prose, and deliberately so: the model receives findings it
/// can reason about and cite, instead of having to rediscover "these two
/// activities overlap" from raw rows on every turn — which is exactly the kind
/// of arithmetic a language model does unreliably.
///
/// <see cref="Facts"/> is what makes a finding quotable. Everything in it is
/// computed, not judged: distances in kilometres, times, counts. Gluno may
/// state those as fact. <see cref="Explanation"/> and
/// <see cref="SuggestedAction"/> are SideQuest's OPINION, and the prompt
/// requires that distinction to survive into the answer.
/// </summary>
public sealed class TripFinding
{
    /// Stable machine key, e.g. "empty_day". Never localised — the model
    /// writes the user-facing sentence.
    public required string Type { get; init; }
    /// "info" | "suggestion" | "warning". Nothing here is an error: a plan is
    /// the traveller's to make, and an "unusual" plan is often deliberate.
    public required string Severity { get; init; }
    /// The day this concerns, ISO. Null for whole-trip findings.
    public string? Date { get; init; }
    public IReadOnlyList<Guid> ActivityIds { get; init; } = Array.Empty<Guid>();
    /// One sentence of SideQuest's reading. An opinion, not a measurement.
    public required string Explanation { get; init; }
    /// Computed values behind the finding — safe to state as fact.
    public IReadOnlyDictionary<string, string> Facts { get; init; } = new Dictionary<string, string>();
    /// What could be done about it, when there is an obvious move. Null when
    /// the finding is worth knowing but the fix is the user's call.
    public string? SuggestedAction { get; init; }
}

/// <summary>
/// The pace a plan is being built for. Drives how many stops a day should
/// hold and how much air is left between them.
/// </summary>
public enum TripPace
{
    Relaxed,
    Balanced,
    Packed,
}

public static class TripPaces
{
    public static TripPace Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "relaxed" or "lugnt" or "lugn" or "slow" => TripPace.Relaxed,
        "packed" or "fullspäckat" or "fullspackat" or "full" or "busy" => TripPace.Packed,
        _ => TripPace.Balanced,
    };

    public static string ToWireValue(TripPace pace) => pace switch
    {
        TripPace.Relaxed => "relaxed",
        TripPace.Packed => "packed",
        _ => "balanced",
    };

    /// The number of non-stay, non-transport stops a day is expected to hold.
    /// Used only to flag the extremes — nothing is rejected for being outside
    /// it, because a deliberately quiet day is a perfectly good day.
    public static (int Min, int Max) DayStopRange(TripPace pace) => pace switch
    {
        TripPace.Relaxed => (1, 3),
        TripPace.Packed => (3, 7),
        _ => (2, 5),
    };
}

/// <summary>
/// SideQuest's deterministic read of an Adventure.
///
/// WHY THIS IS NOT THE MODEL'S JOB. Counting activities, comparing clock
/// times, measuring kilometres between coordinates and spotting that a
/// restaurant sits 9 km from everything else that day are all arithmetic. A
/// language model can do them, but not reliably and not for free — and a wrong
/// answer is invisible. Doing them here means Gluno starts every turn already
/// knowing what is wrong with the plan, and spends its reasoning on what to do
/// about it.
///
/// WHAT IT DELIBERATELY DOES NOT DO:
///
///  • **No travel times.** SideQuest has no routing data. Every distance below
///    is straight-line and labelled as such; nothing here produces "a
///    12-minute walk", because that number would be invented.
///  • **No verdicts.** Severity tops out at "warning". A long hop may be a
///    day trip someone planned on purpose, and the analyzer has no way to know
///    that — so it reports and lets the user decide.
///  • **No guessing at missing data.** An activity without coordinates is
///    skipped by the geographic checks rather than being placed at the city
///    centre; a date with no forecast produces no weather finding.
/// </summary>
public static class TripAnalyzer
{
    /// Two activities closer together than this are treated as the same spot —
    /// below it, coordinate noise dominates.
    private const double SameAreaKm = 0.6;
    /// A hop worth mentioning inside a single day.
    private const double LongHopKm = 6;
    /// A hop that almost certainly needs planned transport.
    private const double VeryLongHopKm = 25;
    /// How far a meal may sit from the day's centre before it looks misplaced.
    private const double MealDetourKm = 4;
    /// How far the hotel may sit from the day's centre before it is worth a
    /// mention.
    private const double HotelDetourKm = 8;

    /// An activity that starts before this is "very early".
    private const int VeryEarlyMinutes = 8 * 60;
    /// An activity that starts after this counts as a late night.
    private const int LateNightMinutes = 22 * 60;
    /// Assumed occupancy of a stop when checking for clashes. Activities have
    /// no duration field, so this is a HEURISTIC for overlap detection only —
    /// it is never reported to the user as a fact.
    private const int AssumedStopMinutes = 90;

    public static IReadOnlyList<TripFinding> Analyze(GlunoTripContext trip, TripPace pace, IReadOnlyList<GlunoWeatherContext> weather)
    {
        var findings = new List<TripFinding>();

        var byDate = trip.Activities
            .GroupBy(a => a.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.SortIndex).ToList());

        AnalyzeTripLevel(trip, findings);

        foreach (var date in EnumerateDays(trip))
        {
            var day = byDate.GetValueOrDefault(date, new List<GlunoActivityContext>());
            var stays = ActiveStaysOn(trip, date);

            AnalyzeDayLoad(date, day, stays, pace, findings);
            AnalyzeTimes(date, day, findings);
            AnalyzeGeography(date, day, stays, trip, findings);
            AnalyzeMeals(date, day, pace, findings);
            AnalyzeDuplicates(date, day, findings);
            AnalyzeStayBoundaries(date, day, stays, findings);
            AnalyzeWeather(date, day, weather, findings);
        }

        AnalyzeIntercityTransport(trip, byDate, findings);

        return findings;
    }

    // ── Trip level ────────────────────────────────────────────────────────

    private static void AnalyzeTripLevel(GlunoTripContext trip, List<TripFinding> findings)
    {
        foreach (var activity in trip.Activities)
        {
            if (TripDateRange.Contains(trip.StartDate, trip.EndDate, activity.Date)) continue;

            findings.Add(new TripFinding
            {
                Type = "activity_outside_trip_dates",
                Severity = "warning",
                Date = Iso(activity.Date),
                ActivityIds = [activity.Id],
                Explanation = "This is planned outside the Adventure's dates, so it will not show up in the trip.",
                Facts = new Dictionary<string, string>
                {
                    ["activityDate"] = Iso(activity.Date),
                    ["tripStart"] = Iso(trip.StartDate),
                    ["tripEnd"] = trip.EndDate.HasValue ? Iso(trip.EndDate.Value) : "open",
                },
                SuggestedAction = "Move it inside the dates, or change the Adventure's dates.",
            });
        }

        foreach (var stay in trip.Activities.Where(a => a.EndDate.HasValue))
        {
            if (stay.EndDate!.Value > stay.Date) continue;

            findings.Add(new TripFinding
            {
                Type = "invalid_stay",
                Severity = "warning",
                Date = Iso(stay.Date),
                ActivityIds = [stay.Id],
                Explanation = "Check-out is not after check-in, so this stay cannot be right.",
                Facts = new Dictionary<string, string>
                {
                    ["checkIn"] = Iso(stay.Date),
                    ["checkOut"] = Iso(stay.EndDate.Value),
                },
                SuggestedAction = "Correct the check-out date.",
            });
        }
    }

    // ── Day load ──────────────────────────────────────────────────────────

    private static void AnalyzeDayLoad(
        DateOnly date,
        List<GlunoActivityContext> day,
        List<GlunoActivityContext> stays,
        TripPace pace,
        List<TripFinding> findings)
    {
        // Stays and transfers are not "things to do" — a day whose only entry
        // is a hotel is an empty day.
        var stops = day.Where(a => a.Role is not ("stay" or "transport")).ToList();
        var (min, max) = TripPaces.DayStopRange(pace);

        if (stops.Count == 0)
        {
            findings.Add(new TripFinding
            {
                Type = "empty_day",
                Severity = "suggestion",
                Date = Iso(date),
                Explanation = stays.Count > 0
                    ? "Nothing is planned on this day, though there is somewhere to stay."
                    : "Nothing is planned on this day.",
                Facts = new Dictionary<string, string> { ["plannedStops"] = "0" },
                SuggestedAction = "Offer a day plan if the user wants one.",
            });
            return;
        }

        if (stops.Count > max)
        {
            findings.Add(new TripFinding
            {
                Type = "overpacked_day",
                Severity = "suggestion",
                Date = Iso(date),
                ActivityIds = stops.Select(a => a.Id).ToList(),
                Explanation = $"This day has more stops than a {TripPaces.ToWireValue(pace)} pace usually allows for.",
                Facts = new Dictionary<string, string>
                {
                    ["plannedStops"] = stops.Count.ToString(CultureInfo.InvariantCulture),
                    ["paceMax"] = max.ToString(CultureInfo.InvariantCulture),
                    ["pace"] = TripPaces.ToWireValue(pace),
                },
                SuggestedAction = "Suggest moving one or two to a lighter day.",
            });
        }
        else if (stops.Count < min)
        {
            findings.Add(new TripFinding
            {
                Type = "sparse_day",
                Severity = "info",
                Date = Iso(date),
                ActivityIds = stops.Select(a => a.Id).ToList(),
                Explanation = "This day is lighter than the rest of the trip.",
                Facts = new Dictionary<string, string>
                {
                    ["plannedStops"] = stops.Count.ToString(CultureInfo.InvariantCulture),
                    ["paceMin"] = min.ToString(CultureInfo.InvariantCulture),
                    ["pace"] = TripPaces.ToWireValue(pace),
                },
            });
        }
    }

    // ── Times ─────────────────────────────────────────────────────────────

    private static void AnalyzeTimes(DateOnly date, List<GlunoActivityContext> day, List<TripFinding> findings)
    {
        var timed = day
            .Where(a => a.Role != "stay" && ParseMinutes(a.Time) is not null)
            .Select(a => (Activity: a, Start: ParseMinutes(a.Time)!.Value))
            .OrderBy(x => x.Start)
            .ToList();

        // Identical clock times are an unambiguous clash — no duration
        // assumption needed, so this is reported as fact.
        foreach (var clash in timed.GroupBy(x => x.Start).Where(g => g.Count() > 1))
        {
            findings.Add(new TripFinding
            {
                Type = "time_overlap",
                Severity = "warning",
                Date = Iso(date),
                ActivityIds = clash.Select(x => x.Activity.Id).ToList(),
                Explanation = "These are booked for the same time.",
                Facts = new Dictionary<string, string>
                {
                    ["time"] = MinutesToClock(clash.Key),
                    ["count"] = clash.Count().ToString(CultureInfo.InvariantCulture),
                },
                SuggestedAction = "Move one of them.",
            });
        }

        // Tight spacing is a SUGGESTION, not a clash: without a duration field
        // the overlap is assumed, and the wording must not pretend otherwise.
        for (var i = 1; i < timed.Count; i++)
        {
            var gap = timed[i].Start - timed[i - 1].Start;
            if (gap <= 0 || gap >= AssumedStopMinutes) continue;

            findings.Add(new TripFinding
            {
                Type = "tight_schedule",
                Severity = "suggestion",
                Date = Iso(date),
                ActivityIds = [timed[i - 1].Activity.Id, timed[i].Activity.Id],
                Explanation = "These start close together, so the first may still be going when the second begins.",
                Facts = new Dictionary<string, string>
                {
                    ["firstTime"] = MinutesToClock(timed[i - 1].Start),
                    ["secondTime"] = MinutesToClock(timed[i].Start),
                    ["gapMinutes"] = gap.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        // A long unplanned stretch mid-day.
        for (var i = 1; i < timed.Count; i++)
        {
            var gap = timed[i].Start - timed[i - 1].Start;
            if (gap < 5 * 60) continue;

            findings.Add(new TripFinding
            {
                Type = "large_gap",
                Severity = "info",
                Date = Iso(date),
                ActivityIds = [timed[i - 1].Activity.Id, timed[i].Activity.Id],
                Explanation = "There is a long stretch with nothing planned between these.",
                Facts = new Dictionary<string, string>
                {
                    ["fromTime"] = MinutesToClock(timed[i - 1].Start),
                    ["toTime"] = MinutesToClock(timed[i].Start),
                    ["gapMinutes"] = gap.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        if (timed.Count > 0 && timed[0].Start <= VeryEarlyMinutes)
        {
            findings.Add(new TripFinding
            {
                Type = "very_early_start",
                Severity = "info",
                Date = Iso(date),
                ActivityIds = [timed[0].Activity.Id],
                Explanation = "This day starts very early.",
                Facts = new Dictionary<string, string> { ["startTime"] = MinutesToClock(timed[0].Start) },
            });
        }
    }

    // ── Geography ─────────────────────────────────────────────────────────

    private static void AnalyzeGeography(
        DateOnly date,
        List<GlunoActivityContext> day,
        List<GlunoActivityContext> stays,
        GlunoTripContext trip,
        List<TripFinding> findings)
    {
        var placed = day
            .Where(a => a.Role != "stay" && a.Latitude.HasValue && a.Longitude.HasValue)
            .ToList();

        // Consecutive hops, in the order the day is actually planned.
        for (var i = 1; i < placed.Count; i++)
        {
            var distance = GeoDistance.KilometresBetween(
                placed[i - 1].Latitude, placed[i - 1].Longitude, placed[i].Latitude, placed[i].Longitude);
            if (distance is not { } km || km < LongHopKm) continue;

            findings.Add(new TripFinding
            {
                Type = km >= VeryLongHopKm ? "very_long_hop" : "long_hop",
                Severity = km >= VeryLongHopKm ? "warning" : "suggestion",
                Date = Iso(date),
                ActivityIds = [placed[i - 1].Id, placed[i].Id],
                Explanation = km >= VeryLongHopKm
                    ? "These two are far enough apart that the day needs planned transport between them."
                    : "These two are in different parts of the area.",
                Facts = new Dictionary<string, string>
                {
                    ["straightLineKm"] = Km(km),
                    ["from"] = placed[i - 1].Title,
                    ["to"] = placed[i].Title,
                },
                SuggestedAction = km >= VeryLongHopKm
                    ? "Check how they plan to travel between them."
                    : "Consider reordering the day so nearby stops sit together.",
            });
        }

        // Zigzag: the planned order is materially longer than visiting the
        // same places grouped by proximity. Reported as a ratio, so the model
        // can say "about twice as much back and forth" without inventing a
        // travel time.
        if (placed.Count >= 3)
        {
            var plannedKm = PathLengthKm(placed);
            var greedyKm = PathLengthKm(GreedyNearestOrder(placed));

            if (plannedKm > 1 && greedyKm > 0 && plannedKm / greedyKm >= 1.4 && plannedKm - greedyKm >= 3)
            {
                findings.Add(new TripFinding
                {
                    Type = "zigzag_order",
                    Severity = "suggestion",
                    Date = Iso(date),
                    ActivityIds = placed.Select(a => a.Id).ToList(),
                    Explanation = "The current order crosses back and forth more than it needs to.",
                    Facts = new Dictionary<string, string>
                    {
                        ["plannedStraightLineKm"] = Km(plannedKm),
                        ["groupedStraightLineKm"] = Km(greedyKm),
                    },
                    SuggestedAction = "Suggest an order that groups nearby stops together.",
                });
            }
        }

        // A meal marooned away from everything else that day.
        var centre = Centroid(placed.Where(a => a.Role != "meal").ToList());
        if (centre != null)
        {
            foreach (var meal in placed.Where(a => a.Role == "meal"))
            {
                var distance = GeoDistance.KilometresBetween(centre.Value.Lat, centre.Value.Lon, meal.Latitude, meal.Longitude);
                if (distance is not { } km || km < MealDetourKm) continue;

                findings.Add(new TripFinding
                {
                    Type = "meal_far_from_day",
                    Severity = "suggestion",
                    Date = Iso(date),
                    ActivityIds = [meal.Id],
                    Explanation = "This place to eat sits away from the rest of the day.",
                    Facts = new Dictionary<string, string> { ["straightLineKmFromDayCentre"] = Km(km) },
                    SuggestedAction = "Offer somewhere closer, or move it to a day that is already over there.",
                });
            }
        }

        // The hotel against the day's centre of gravity.
        var dayCentre = Centroid(placed);
        if (dayCentre != null)
        {
            foreach (var stay in stays.Where(s => s.Latitude.HasValue && s.Longitude.HasValue))
            {
                var distance = GeoDistance.KilometresBetween(
                    dayCentre.Value.Lat, dayCentre.Value.Lon, stay.Latitude, stay.Longitude);
                if (distance is not { } km || km < HotelDetourKm) continue;

                findings.Add(new TripFinding
                {
                    Type = "stay_far_from_day",
                    Severity = "info",
                    Date = Iso(date),
                    ActivityIds = [stay.Id],
                    Explanation = "The place they are staying is well away from where this day happens.",
                    Facts = new Dictionary<string, string> { ["straightLineKmFromDayCentre"] = Km(km) },
                });
            }
        }

        // Several day locations on one date is a legitimate plan, but it
        // changes what "nearby" means, so the model should know.
        var dayLocations = trip.DayLocations.Where(d => d.Date == date).ToList();
        if (dayLocations.Count > 1)
        {
            findings.Add(new TripFinding
            {
                Type = "multiple_locations_same_day",
                Severity = "info",
                Date = Iso(date),
                Explanation = "This day covers more than one place.",
                Facts = new Dictionary<string, string>
                {
                    ["locations"] = string.Join(", ", dayLocations.Select(d => d.Label)),
                },
            });
        }
    }

    // ── Meals ─────────────────────────────────────────────────────────────

    private static void AnalyzeMeals(
        DateOnly date, List<GlunoActivityContext> day, TripPace pace, List<TripFinding> findings)
    {
        var stops = day.Where(a => a.Role is not ("stay" or "transport")).ToList();
        // Only worth raising on a day that is otherwise busy — nobody needs to
        // be told to eat on a day with one museum.
        if (stops.Count < 3) return;

        var meals = stops.Where(a => a.Role == "meal").ToList();
        var hasMidday = meals.Any(m => ParseMinutes(m.Time) is >= 11 * 60 and <= 15 * 60);
        var hasEvening = meals.Any(m => ParseMinutes(m.Time) is >= 17 * 60 and <= 22 * 60);

        // Times are optional, so "no meal at all" is the only safe signal when
        // nothing is timed.
        if (meals.Count == 0)
        {
            findings.Add(new TripFinding
            {
                Type = "missing_meal",
                Severity = "suggestion",
                Date = Iso(date),
                Explanation = "A full day with nowhere to eat planned.",
                Facts = new Dictionary<string, string>
                {
                    ["plannedStops"] = stops.Count.ToString(CultureInfo.InvariantCulture),
                    ["plannedMeals"] = "0",
                },
                SuggestedAction = "Offer a lunch or dinner spot that fits the day's area.",
            });
            return;
        }

        if (meals.Any(m => ParseMinutes(m.Time) is not null) && (!hasMidday || !hasEvening))
        {
            findings.Add(new TripFinding
            {
                Type = "missing_meal",
                Severity = "info",
                Date = Iso(date),
                ActivityIds = meals.Select(m => m.Id).ToList(),
                Explanation = hasMidday
                    ? "Lunch is covered but there is no dinner on this day."
                    : "Dinner is covered but there is no lunch on this day.",
                Facts = new Dictionary<string, string>
                {
                    ["hasMiddayMeal"] = hasMidday ? "true" : "false",
                    ["hasEveningMeal"] = hasEvening ? "true" : "false",
                    ["pace"] = TripPaces.ToWireValue(pace),
                },
            });
        }
    }

    // ── Duplicates ────────────────────────────────────────────────────────

    private static void AnalyzeDuplicates(DateOnly date, List<GlunoActivityContext> day, List<TripFinding> findings)
    {
        for (var i = 0; i < day.Count; i++)
        {
            for (var j = i + 1; j < day.Count; j++)
            {
                if (!LooksLikeSameThing(day[i], day[j])) continue;

                findings.Add(new TripFinding
                {
                    Type = "near_duplicate",
                    Severity = "suggestion",
                    Date = Iso(date),
                    ActivityIds = [day[i].Id, day[j].Id],
                    Explanation = "These two look like the same thing planned twice.",
                    Facts = new Dictionary<string, string>
                    {
                        ["first"] = day[i].Title,
                        ["second"] = day[j].Title,
                    },
                    SuggestedAction = "Check whether one of them should go.",
                });
            }
        }
    }

    private static bool LooksLikeSameThing(GlunoActivityContext a, GlunoActivityContext b)
    {
        var left = Normalise(a.Title);
        var right = Normalise(b.Title);
        if (left.Length == 0 || right.Length == 0) return false;

        if (left == right) return true;

        // Same place id is definitive; same coordinates within noise is close
        // enough to be worth asking about.
        if (!string.IsNullOrWhiteSpace(a.PlaceId) && a.PlaceId == b.PlaceId) return true;

        var distance = GeoDistance.KilometresBetween(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
        return distance is { } km && km < 0.05 && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));
    }

    // ── Stay boundaries ───────────────────────────────────────────────────

    private static void AnalyzeStayBoundaries(
        DateOnly date,
        List<GlunoActivityContext> day,
        List<GlunoActivityContext> stays,
        List<TripFinding> findings)
    {
        foreach (var stay in stays)
        {
            var checkInMinutes = ParseMinutes(stay.Time);
            var checkOutMinutes = ParseMinutes(stay.EndTime);

            if (stay.Date == date && checkInMinutes is { } checkIn)
            {
                var before = day
                    .Where(a => a.Id != stay.Id && ParseMinutes(a.Time) is { } start && start < checkIn)
                    .ToList();

                if (before.Count > 0)
                {
                    findings.Add(new TripFinding
                    {
                        Type = "activity_before_checkin",
                        Severity = "info",
                        Date = Iso(date),
                        ActivityIds = before.Select(a => a.Id).Append(stay.Id).ToList(),
                        Explanation = "There is something planned before check-in, so luggage may need somewhere to go.",
                        Facts = new Dictionary<string, string>
                        {
                            ["checkInTime"] = MinutesToClock(checkIn),
                            ["activitiesBefore"] = before.Count.ToString(CultureInfo.InvariantCulture),
                        },
                    });
                }
            }

            if (stay.EndDate == date && checkOutMinutes is { } checkOut)
            {
                var after = day
                    .Where(a => a.Id != stay.Id && ParseMinutes(a.Time) is { } start && start > checkOut)
                    .ToList();

                if (after.Count > 0)
                {
                    findings.Add(new TripFinding
                    {
                        Type = "activity_after_checkout",
                        Severity = "info",
                        Date = Iso(date),
                        ActivityIds = after.Select(a => a.Id).Append(stay.Id).ToList(),
                        Explanation = "There is something planned after check-out with no next stay on this day.",
                        Facts = new Dictionary<string, string>
                        {
                            ["checkOutTime"] = MinutesToClock(checkOut),
                            ["activitiesAfter"] = after.Count.ToString(CultureInfo.InvariantCulture),
                        },
                    });
                }
            }
        }
    }

    // ── Weather ───────────────────────────────────────────────────────────

    private static readonly string[] WetConditions = ["rain", "heavy_rain", "thunderstorm", "snow"];

    private static void AnalyzeWeather(
        DateOnly date,
        List<GlunoActivityContext> day,
        IReadOnlyList<GlunoWeatherContext> weather,
        List<TripFinding> findings)
    {
        // Only for dates SideQuest actually has a forecast for. No forecast
        // means no finding — never an assumption about the weather.
        var forecast = weather.FirstOrDefault(w => w.Date == date);
        if (forecast?.Condition == null) return;

        var wet = WetConditions.Contains(forecast.Condition);
        var highRain = forecast.PrecipitationProbability >= 60;
        if (!wet && !highRain) return;

        var outdoorish = day
            .Where(a => a.Role is not ("stay" or "transport" or "meal"))
            .ToList();
        if (outdoorish.Count == 0) return;

        findings.Add(new TripFinding
        {
            Type = "weather_risk",
            Severity = "suggestion",
            Date = Iso(date),
            ActivityIds = outdoorish.Select(a => a.Id).ToList(),
            Explanation = "The forecast may not suit whatever is outdoors on this day.",
            Facts = new Dictionary<string, string>
            {
                ["condition"] = forecast.Condition,
                ["precipitationProbability"] = forecast.PrecipitationProbability?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                ["tempMaxC"] = forecast.TempMaxC?.ToString("0.#", CultureInfo.InvariantCulture) ?? "unknown",
                ["source"] = "sidequest_weather",
            },
            SuggestedAction = "Offer an indoor alternative, or suggest swapping days with a drier one.",
        });
    }

    // ── Intercity transport ───────────────────────────────────────────────

    private static void AnalyzeIntercityTransport(
        GlunoTripContext trip,
        Dictionary<DateOnly, List<GlunoActivityContext>> byDate,
        List<TripFinding> findings)
    {
        var mainLocations = trip.DayLocations
            .Where(d => d.SortIndex == 0)
            .OrderBy(d => d.Date)
            .ToList();

        for (var i = 1; i < mainLocations.Count; i++)
        {
            var previous = mainLocations[i - 1];
            var current = mainLocations[i];

            var distance = GeoDistance.KilometresBetween(
                previous.Latitude, previous.Longitude, current.Latitude, current.Longitude);
            if (distance is not { } km || km < VeryLongHopKm) continue;

            // A transport activity on either the departure or the arrival day
            // counts as "planned" — the user may have booked the train the
            // evening before.
            var hasTransport =
                byDate.GetValueOrDefault(current.Date, []).Any(a => a.Role == "transport")
                || byDate.GetValueOrDefault(previous.Date, []).Any(a => a.Role == "transport");

            if (hasTransport) continue;

            findings.Add(new TripFinding
            {
                Type = "missing_intercity_transport",
                Severity = "warning",
                Date = Iso(current.Date),
                Explanation = "The trip moves between two places with nothing planned for getting there.",
                Facts = new Dictionary<string, string>
                {
                    ["from"] = previous.Label,
                    ["to"] = current.Label,
                    ["straightLineKm"] = Km(km),
                    ["onDate"] = Iso(current.Date),
                },
                SuggestedAction = "Ask how they are travelling, and offer to add it to the plan.",
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IEnumerable<DateOnly> EnumerateDays(GlunoTripContext trip)
    {
        var end = trip.EffectiveEndDate;
        for (var date = trip.StartDate; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static List<GlunoActivityContext> ActiveStaysOn(GlunoTripContext trip, DateOnly date)
        => trip.Activities
            .Where(a => a.Role == "stay" && a.EndDate.HasValue && a.Date <= date && date <= a.EndDate.Value)
            .ToList();

    private static double PathLengthKm(IReadOnlyList<GlunoActivityContext> path)
    {
        double total = 0;
        for (var i = 1; i < path.Count; i++)
        {
            total += GeoDistance.KilometresBetween(
                path[i - 1].Latitude, path[i - 1].Longitude, path[i].Latitude, path[i].Longitude) ?? 0;
        }
        return total;
    }

    /// <summary>
    /// Nearest-neighbour ordering from the first stop. Not an optimal route —
    /// it is only the yardstick the planned order is compared against, and a
    /// cheap greedy walk is enough to tell "sensible" from "zigzag".
    /// </summary>
    private static List<GlunoActivityContext> GreedyNearestOrder(IReadOnlyList<GlunoActivityContext> stops)
    {
        var remaining = stops.ToList();
        var ordered = new List<GlunoActivityContext> { remaining[0] };
        remaining.RemoveAt(0);

        while (remaining.Count > 0)
        {
            var last = ordered[^1];
            var nearestIndex = 0;
            var nearestKm = double.MaxValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var km = GeoDistance.KilometresBetween(
                    last.Latitude, last.Longitude, remaining[i].Latitude, remaining[i].Longitude) ?? double.MaxValue;
                if (km >= nearestKm) continue;
                nearestKm = km;
                nearestIndex = i;
            }

            ordered.Add(remaining[nearestIndex]);
            remaining.RemoveAt(nearestIndex);
        }

        return ordered;
    }

    private static (double Lat, double Lon)? Centroid(IReadOnlyList<GlunoActivityContext> stops)
    {
        var placed = stops.Where(a => a.Latitude.HasValue && a.Longitude.HasValue).ToList();
        if (placed.Count == 0) return null;

        return (placed.Average(a => a.Latitude!.Value), placed.Average(a => a.Longitude!.Value));
    }

    /// Minutes since midnight from "HH:mm". Null for anything else — an
    /// unparseable time is treated as no time, never as midnight.
    internal static int? ParseMinutes(string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return null;
        var parts = time.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)) return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return null;
        if (hours is < 0 or > 23 || minutes is < 0 or > 59) return null;
        return (hours * 60) + minutes;
    }

    private static string MinutesToClock(int minutes)
        => $"{minutes / 60:00}:{minutes % 60:00}";

    private static string Km(double value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Normalise(string value)
        => new(value.Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
}

/// <summary>
/// Maps an activity's category to the coarse role the analyzer reasons about.
/// One place, so "is this a restaurant?" cannot mean two different things in
/// two different checks.
/// </summary>
public static class ActivityRoles
{
    private static readonly string[] StayCategories = ["hotel", "stay", "accommodation", "lodging", "airbnb"];
    private static readonly string[] MealCategories = ["food", "restaurant", "drink", "cafe", "coffee", "bar", "breakfast", "lunch", "dinner"];
    private static readonly string[] TransportCategories = ["car", "transport", "flight", "plane", "train", "bus", "ferry", "boat", "taxi", "transfer"];

    public static string FromCategory(string? category, DateOnly? endDate)
    {
        // A multi-day entry is a stay whatever it is labelled — that is what
        // check-in/check-out semantics mean in this app.
        if (endDate.HasValue) return "stay";

        var key = category?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return "activity";

        if (StayCategories.Contains(key)) return "stay";
        if (MealCategories.Contains(key)) return "meal";
        if (TransportCategories.Contains(key)) return "transport";
        return "activity";
    }
}
