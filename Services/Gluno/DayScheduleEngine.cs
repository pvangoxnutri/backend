namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Which meal a stop is, when it is one. Meals have windows the rest of a day
/// does not: lunch at 16:30 is technically a schedule and practically a
/// mistake.
/// </summary>
public enum MealSlot
{
    None,
    Breakfast,
    Lunch,
    Dinner,
}

/// <summary>One thing someone wants to do on a day.</summary>
public sealed class ScheduleCandidate
{
    /// Existing Activity id, or a proposal-local id for something not saved yet.
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Category { get; init; }

    /// <summary>
    /// True for anything already in the Adventure with a time on it: a booked
    /// dinner, a flight, a guided tour, a check-in.
    ///
    /// The engine NEVER moves these. Not to make the day fit, not to save five
    /// minutes of walking. A booking has a reality behind it that the engine
    /// cannot see, and silently shifting one is the fastest way to make a
    /// planning assistant untrustworthy. When a fixed stop is genuinely in the
    /// wrong place, that becomes a separate proposal the user has to accept.
    /// </summary>
    public bool IsFixed { get; init; }

    public TimeOnly? FixedStart { get; init; }

    public required int DurationMinutes { get; init; }

    /// <see cref="DurationSources"/>.
    public required string DurationSource { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public OpeningHours? OpeningHours { get; init; }

    public MealSlot Meal { get; init; } = MealSlot.None;

    /// Higher goes in first when the day cannot hold everything. The user's
    /// explicit "I really want to see X" outranks a filler suggestion.
    public int Priority { get; init; }

    /// stay | meal | transport | activity — from <see cref="ActivityRoles"/>.
    public string Role { get; init; } = "activity";
}

/// <summary>What the engine worked out about getting from one stop to the next.</summary>
public sealed record TravelInfo(
    int Minutes,
    /// True only when a routing provider computed it. False means the minutes
    /// below are SideQuest's own estimate from a straight line — usable for
    /// laying out a timeline, never quotable as a travel time.
    bool Verified,
    double? DistanceKm,
    TravelMode Mode,
    string Source);

public sealed class ScheduledStop
{
    public required ScheduleCandidate Candidate { get; init; }
    public required TimeOnly Start { get; init; }
    public required TimeOnly End { get; init; }
    /// Settable: inserting a stop changes what the NEXT stop travelled from,
    /// so this is recomputed as the day takes shape.
    public TravelInfo? TravelFromPrevious { get; set; }
    public OpeningHoursCheck? Opening { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed record DroppedCandidate(ScheduleCandidate Candidate, string Reason);

public sealed class DaySchedule
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<ScheduledStop> Stops { get; init; }

    /// <summary>
    /// What did not fit, and why. This is the honest half of the output — a
    /// planner that quietly discards the third museum is worse than one that
    /// says "these two didn't fit; which matters more?".
    /// </summary>
    public required IReadOnlyList<DroppedCandidate> Dropped { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// False when the day contains something the engine could not resolve —
    /// overlapping fixed bookings, a stop that cannot fit anywhere. Apply is
    /// blocked on this.
    public required bool Feasible { get; init; }

    /// Share of the planned window that is occupied, travel included. Drives
    /// "this day has room" versus "this day is full".
    public required double Utilisation { get; init; }
}

public sealed class ScheduleRequest
{
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<ScheduleCandidate> Candidates { get; init; }

    /// When the traveller wants to be out. Defaults are deliberate rather than
    /// clever: most people are not doing anything before nine or after nine.
    public TimeOnly DayStart { get; init; } = new(9, 0);
    public TimeOnly DayEnd { get; init; } = new(21, 0);

    public TripPace Pace { get; init; } = TripPace.Balanced;
    public TravelMode PrimaryMode { get; init; } = TravelMode.Walking;

    /// Nothing may be scheduled before check-in on an arrival day, or after
    /// check-out on a departure day.
    public TimeOnly? EarliestStart { get; init; }
    public TimeOnly? LatestEnd { get; init; }

    /// <summary>
    /// Verified legs, keyed <c>"fromId&gt;toId"</c>. Missing pairs fall back to
    /// a straight-line estimate flagged unverified — the day still gets a
    /// shape, it is simply honest about where the numbers came from.
    /// </summary>
    public IReadOnlyDictionary<string, RouteLeg> Legs { get; init; }
        = new Dictionary<string, RouteLeg>(StringComparer.Ordinal);

    public required DateTime NowUtc { get; init; }

    /// Local wall-clock time at the destination, when known. Only used to
    /// avoid planning a stop that has already started.
    public DateTime? LocalNow { get; init; }
}

/// <summary>
/// Turns a wish-list into a timeline, or explains why it will not become one.
///
/// WHY THIS IS NOT THE MODEL'S JOB, again. Arithmetic on clock times is
/// something a language model does plausibly and not reliably. Ask one to fit
/// six stops with travel and opening hours into a day and it will produce
/// something that reads perfectly and has a stop starting before the previous
/// one ends. The failure is invisible in prose and obvious in a loop — so the
/// loop does it.
///
/// The model still decides WHAT goes in the day and WHY. That is judgement, and
/// it is genuinely good at it. This engine decides WHEN, and refuses to produce
/// a day that cannot happen.
///
/// THE CENTRAL RULE: a day that does not fit loses a stop. It never gains an
/// impossible one. An itinerary with four realistic stops beats one with six
/// that quietly assumes teleportation, because the second one fails at 14:00 on
/// a Tuesday when someone is standing in a street with a phone.
/// </summary>
public sealed class DayScheduleEngine
{
    private readonly ActivityDurationTable _durations;

    public DayScheduleEngine(ActivityDurationTable durations) => _durations = durations;

    /// <summary>
    /// How full a day is allowed to get before the engine stops adding.
    ///
    /// This is what "don't fill every minute" means concretely. A relaxed day
    /// keeps a third of itself empty — not as slack for overruns, but as actual
    /// unplanned time, which is the entire point of a relaxed trip. Even a
    /// packed day keeps a little, because a day with no air is a day where the
    /// first delay breaks everything after it.
    /// </summary>
    private static double MaxUtilisation(TripPace pace) => pace switch
    {
        TripPace.Relaxed => 0.62,
        TripPace.Packed => 0.92,
        _ => 0.80,
    };

    /// Meal windows, local. Outside these a meal is flagged rather than moved —
    /// people do eat at odd hours on purpose.
    private static (TimeOnly From, TimeOnly To) MealWindow(MealSlot meal) => meal switch
    {
        MealSlot.Breakfast => (new TimeOnly(7, 0), new TimeOnly(10, 30)),
        MealSlot.Lunch => (new TimeOnly(11, 30), new TimeOnly(14, 30)),
        MealSlot.Dinner => (new TimeOnly(17, 30), new TimeOnly(21, 30)),
        _ => (TimeOnly.MinValue, TimeOnly.MaxValue),
    };

    public DaySchedule Build(ScheduleRequest request)
    {
        var pace = request.Pace;
        var buffer = _durations.BufferMinutes(TripPaces.ToWireValue(pace));
        var warnings = new List<string>();

        var windowStart = Max(request.DayStart, request.EarliestStart);
        var windowEnd = Min(request.DayEnd, request.LatestEnd);

        // Planning a day that has already started: everything before now is
        // gone. Rounded up to the next quarter hour so the first stop is not
        // "starts in three minutes".
        if (request.LocalNow is { } localNow && DateOnly.FromDateTime(localNow) == request.Date)
        {
            var nowRounded = RoundUpToQuarter(TimeOnly.FromDateTime(localNow));
            if (nowRounded > windowStart)
            {
                windowStart = nowRounded;
                warnings.Add("day_already_started");
            }
        }

        if (windowEnd <= windowStart)
        {
            return new DaySchedule
            {
                Date = request.Date,
                Stops = Array.Empty<ScheduledStop>(),
                Dropped = request.Candidates.Select(c => new DroppedCandidate(c, "no_time_available")).ToList(),
                Warnings = [.. warnings, "no_usable_window"],
                Feasible = false,
                Utilisation = 0,
            };
        }

        var totalWindowMinutes = Math.Max(1, Minutes(windowEnd) - Minutes(windowStart));

        // ── 1. Fixed anchors go down first, exactly where they are ──────────
        var placed = new List<ScheduledStop>();
        var dropped = new List<DroppedCandidate>();
        var feasible = true;

        var fixedItems = request.Candidates
            .Where(candidate => candidate is { IsFixed: true, FixedStart: not null })
            .OrderBy(candidate => candidate.FixedStart!.Value)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var item in fixedItems)
        {
            var start = item.FixedStart!.Value;
            var stop = new ScheduledStop
            {
                Candidate = item,
                Start = start,
                End = AddMinutes(start, item.DurationMinutes),
                Opening = CheckOpening(item, request),
            };

            if (placed.Count > 0)
            {
                var previous = placed[^1];
                stop.TravelFromPrevious = Travel(previous.Candidate, item, request);

                // Two bookings that collide is a real-world problem, not a
                // layout problem. Report it; do not "solve" it by moving one.
                if (Minutes(start) < Minutes(previous.End))
                {
                    stop.Warnings.Add("overlaps_previous_fixed");
                    feasible = false;
                }
                else if (Minutes(start) - Minutes(previous.End) < (stop.TravelFromPrevious?.Minutes ?? 0))
                {
                    stop.Warnings.Add("not_enough_travel_time");
                    feasible = false;
                }
            }

            if (Minutes(start) < Minutes(windowStart)) stop.Warnings.Add("before_day_start");
            if (Minutes(stop.End) > Minutes(windowEnd)) stop.Warnings.Add("after_day_end");
            if (request.EarliestStart is { } checkIn && Minutes(start) < Minutes(checkIn))
            {
                stop.Warnings.Add("before_check_in");
            }
            if (request.LatestEnd is { } checkOut && Minutes(stop.End) > Minutes(checkOut))
            {
                stop.Warnings.Add("after_check_out");
            }

            AddOpeningWarning(stop);
            placed.Add(stop);
        }

        // ── 2. Flexible stops, most important first ─────────────────────────
        var flexible = request.Candidates
            .Where(candidate => !candidate.IsFixed || candidate.FixedStart == null)
            .OrderByDescending(candidate => candidate.Priority)
            // Meals before sights: a meal has a narrow window, and placing the
            // wide-window items first is how lunch ends up homeless.
            .ThenByDescending(candidate => candidate.Meal != MealSlot.None)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();

        var (_, maxStops) = TripPaces.DayStopRange(pace);
        var maxUtilisation = MaxUtilisation(pace);

        foreach (var item in flexible)
        {
            var countingStops = placed.Count(stop => stop.Candidate.Role is "activity" or "meal");
            if (item.Role is "activity" or "meal" && countingStops >= maxStops)
            {
                dropped.Add(new DroppedCandidate(item, "pace_limit"));
                continue;
            }

            var placement = FindPlacement(item, placed, windowStart, windowEnd, buffer, request);
            if (placement == null)
            {
                dropped.Add(new DroppedCandidate(item, "no_room"));
                continue;
            }

            var projected = OccupiedMinutes(placed, placement) / (double)totalWindowMinutes;
            if (projected > maxUtilisation)
            {
                // The stop fits on the clock but not in the day. This is the
                // rule that keeps a "relaxed" day from turning into a packed
                // one just because there were technically gaps.
                dropped.Add(new DroppedCandidate(item, "day_would_be_too_full"));
                continue;
            }

            placed.Insert(placement.Index, placement.Stop);
            RecomputeTravel(placed, request);
        }

        // ── 3. Final consistency pass ───────────────────────────────────────
        for (var index = 1; index < placed.Count; index++)
        {
            if (Minutes(placed[index].Start) < Minutes(placed[index - 1].End)
                && !placed[index].Warnings.Contains("overlaps_previous_fixed"))
            {
                placed[index].Warnings.Add("overlaps_previous");
                feasible = false;
            }
        }

        if (dropped.Count > 0) warnings.Add("some_stops_did_not_fit");
        if (placed.Any(stop => stop.Warnings.Contains("before_check_in"))) warnings.Add("before_check_in");
        if (placed.Any(stop => stop.Warnings.Contains("after_check_out"))) warnings.Add("after_check_out");
        if (placed.Any(stop => stop.TravelFromPrevious is { Verified: false })) warnings.Add("unverified_travel_times");
        if (placed.Any(stop => stop.Opening?.Status == OpeningStatus.Unknown)) warnings.Add("unknown_opening_hours");

        var utilisation = placed.Count == 0 ? 0 : OccupiedMinutes(placed, null) / (double)totalWindowMinutes;

        return new DaySchedule
        {
            Date = request.Date,
            Stops = placed,
            Dropped = dropped,
            Warnings = warnings,
            Feasible = feasible,
            Utilisation = Math.Round(Math.Clamp(utilisation, 0, 1.5), 3),
        };
    }

    private sealed record Placement(int Index, ScheduledStop Stop);

    /// <summary>
    /// Finds the earliest slot a flexible stop genuinely fits.
    ///
    /// "Genuinely" carries the weight: travel in, travel out, buffers on both
    /// sides, opening hours, meal window, and the day's own edges. A gap on the
    /// clock is not a slot if getting there eats it.
    /// </summary>
    private Placement? FindPlacement(
        ScheduleCandidate item,
        List<ScheduledStop> placed,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        int buffer,
        ScheduleRequest request)
    {
        var (mealFrom, mealTo) = MealWindow(item.Meal);

        for (var index = 0; index <= placed.Count; index++)
        {
            var previous = index > 0 ? placed[index - 1] : null;
            var next = index < placed.Count ? placed[index] : null;

            var travelIn = previous == null ? null : Travel(previous.Candidate, item, request);
            var travelOut = next == null ? null : Travel(item, next.Candidate, request);

            var earliest = previous == null
                ? windowStart
                : AddMinutes(previous.End, (travelIn?.Minutes ?? 0) + buffer);

            if (item.Meal != MealSlot.None && Minutes(earliest) < Minutes(mealFrom))
            {
                earliest = mealFrom;
            }

            // Opening hours can only ever push a stop LATER. A place that opens
            // at ten does not open at nine because the plan wanted it to.
            if (item.OpeningHours is { } hours && hours.IsFresh(request.NowUtc) && hours.IsKnown)
            {
                var check = hours.Evaluate(request.Date, earliest, item.DurationMinutes, request.NowUtc);
                if (check is { Status: OpeningStatus.Closed, WarningCode: "opens_later", OpensAt: { } opensAt })
                {
                    earliest = opensAt;
                }
                else if (check.Status == OpeningStatus.Closed)
                {
                    // Shut all day, or already shut by now — no slot in this gap
                    // will help.
                    continue;
                }
            }

            var end = AddMinutes(earliest, item.DurationMinutes);

            var latest = next == null
                ? windowEnd
                : SubtractMinutes(next.Start, (travelOut?.Minutes ?? 0) + buffer);

            if (Minutes(end) > Minutes(latest)) continue;
            if (item.Meal != MealSlot.None && Minutes(earliest) > Minutes(mealTo)) continue;

            var stop = new ScheduledStop
            {
                Candidate = item,
                Start = earliest,
                End = end,
                TravelFromPrevious = travelIn,
                Opening = CheckOpening(item, request, earliest),
            };

            AddOpeningWarning(stop);
            if (travelIn is { Verified: false }) stop.Warnings.Add("travel_time_estimated");

            return new Placement(index, stop);
        }

        return null;
    }

    /// <summary>
    /// Inserting a stop changes what the stop AFTER it travelled from. Without
    /// this the second half of a day keeps travel times measured from a place
    /// nobody visits any more.
    /// </summary>
    private void RecomputeTravel(List<ScheduledStop> placed, ScheduleRequest request)
    {
        for (var index = 1; index < placed.Count; index++)
        {
            placed[index].TravelFromPrevious = Travel(placed[index - 1].Candidate, placed[index].Candidate, request);
        }
    }

    /// <summary>
    /// Travel between two stops: verified if a provider gave us the leg,
    /// otherwise SideQuest's own straight-line estimate — clearly marked, so
    /// nothing downstream can present it as a measured travel time.
    /// </summary>
    private static TravelInfo? Travel(ScheduleCandidate from, ScheduleCandidate to, ScheduleRequest request)
    {
        if (request.Legs.TryGetValue($"{from.Id}>{to.Id}", out var leg)
            && leg is { Verified: true, DurationMinutes: { } minutes })
        {
            return new TravelInfo(minutes, true, leg.DistanceKm, leg.Mode, leg.Source);
        }

        var distance = GeoDistance.KilometresBetween(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
        var estimate = ActivityDurationTable.EstimateTravelMinutes(distance, request.PrimaryMode);

        // No coordinates on either side: no distance, no estimate, no number.
        // A missing travel time is better than a made-up one.
        if (estimate == null) return null;

        return new TravelInfo(estimate.Value, false, distance, request.PrimaryMode, "straight_line");
    }

    private static OpeningHoursCheck? CheckOpening(ScheduleCandidate item, ScheduleRequest request, TimeOnly? start = null)
    {
        if (item.OpeningHours is not { } hours) return null;

        var at = start ?? item.FixedStart;
        if (at == null) return null;

        return hours.Evaluate(request.Date, at.Value, item.DurationMinutes, request.NowUtc);
    }

    private static void AddOpeningWarning(ScheduledStop stop)
    {
        if (stop.Opening?.WarningCode is { } code && stop.Opening.Status != OpeningStatus.Open)
        {
            stop.Warnings.Add("opening_" + code);
        }
    }

    /// Minutes of the day actually spoken for — stops plus the travel between
    /// them. Travel counts: it is time the traveller is not free.
    private static int OccupiedMinutes(IReadOnlyList<ScheduledStop> placed, Placement? candidate)
    {
        var total = placed.Sum(stop => stop.Candidate.DurationMinutes + (stop.TravelFromPrevious?.Minutes ?? 0));

        if (candidate != null)
        {
            total += candidate.Stop.Candidate.DurationMinutes + (candidate.Stop.TravelFromPrevious?.Minutes ?? 0);
        }

        return total;
    }

    // ── Clock helpers ───────────────────────────────────────────────────────
    //
    // All arithmetic goes through minutes-from-midnight rather than TimeOnly,
    // which wraps. A day that runs past midnight should read as "past the end
    // of the day", not silently become 00:30 and sort first.

    private static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;

    private static TimeOnly AddMinutes(TimeOnly time, int minutes)
        => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(Minutes(time) + minutes, 0, 24 * 60 - 1)));

    private static TimeOnly SubtractMinutes(TimeOnly time, int minutes)
        => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(Minutes(time) - minutes, 0, 24 * 60 - 1)));

    private static TimeOnly RoundUpToQuarter(TimeOnly time)
        => AddMinutes(new TimeOnly(0, 0), (int)Math.Ceiling(Minutes(time) / 15.0) * 15);

    private static TimeOnly Max(TimeOnly value, TimeOnly? other)
        => other is { } limit && Minutes(limit) > Minutes(value) ? limit : value;

    private static TimeOnly Min(TimeOnly value, TimeOnly? other)
        => other is { } limit && Minutes(limit) < Minutes(value) ? limit : value;
}
