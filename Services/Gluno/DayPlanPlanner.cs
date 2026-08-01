using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services.Gluno;

/// <summary>One stop the model wants in the day.</summary>
public sealed record DayPlanItem(
    string Title,
    string? Description,
    /// Only set when the stop has a genuinely fixed time — a booking, a tour.
    TimeOnly? FixedTime,
    string? Category,
    int? DurationMinutes,
    double? Latitude,
    double? Longitude,
    string? LocationLabel,
    /// Namespaced provider id from search_places, when the stop came from one.
    string? PlaceId);

public sealed class DayPlanInput
{
    public required Guid TripId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<DayPlanItem> Items { get; init; }
    public TripPace Pace { get; init; } = TripPace.Balanced;
    public required TransportPreferences Transport { get; init; }
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? EndTime { get; init; }
    /// Only when the user said so this turn; otherwise the stored preference wins.
    public TravelMode? RequestedMode { get; init; }
    public string Language { get; init; } = "en";
}

public sealed class DayPlanResult
{
    public required DaySchedule Schedule { get; init; }
    public required JsonElement Payload { get; init; }
    public required string Summary { get; init; }
    public required bool RoutingVerified { get; init; }
}

public interface IDayPlanPlanner
{
    Task<DayPlanResult> PlanAsync(DayPlanInput input, CancellationToken ct);

    /// <summary>
    /// Re-lays a plan the user edited in review. Same engine, same rules — an
    /// edited plan is validated exactly as hard as a generated one, so nobody
    /// can drag two stops on top of each other and then apply.
    /// </summary>
    Task<DayPlanResult> RevalidateAsync(Guid tripId, JsonElement payload, string language, CancellationToken ct);
}

/// <summary>
/// The bridge between "Gluno wants these six things in a day" and a timeline
/// somebody can actually follow.
///
/// It does four things the model must not do itself:
///   1. pulls the day's EXISTING activities in as fixed anchors, so a plan is
///      built around the dinner reservation already in the Adventure rather
///      than on top of it;
///   2. asks the routing layer for real travel times between the stops;
///   3. fetches verified opening hours for stops that came from a place
///      provider;
///   4. hands all of it to the deterministic schedule engine and reports what
///      came back, including what did not fit.
///
/// The output payload is deliberately rich — verified-vs-estimated on every
/// number, the source of every duration, the travel leg before every stop.
/// The proposal card, the review screen and the eventual apply all read from
/// this one structure, so there is no second place where a travel time could
/// quietly turn into a fact.
/// </summary>
public sealed class DayPlanPlanner : IDayPlanPlanner
{
    private readonly AppDbContext _db;
    private readonly IRoutingService _routing;
    private readonly ITravelDataRegistry _travelData;
    private readonly ActivityDurationTable _durations;
    private readonly DayScheduleEngine _engine;

    /// <summary>
    /// How many stops may have their opening hours looked up in one plan.
    ///
    /// Each is an upstream details call. Six covers a full day; an unbounded
    /// fan-out would turn one planning request into a burst of provider traffic.
    /// </summary>
    private const int MaxOpeningHourLookups = 6;

    /// <summary>
    /// The shape of the <c>grounding</c> block in a day-plan payload.
    ///
    /// Versioned separately from the payload itself: a proposal stored under an
    /// older grounding version can still be applied — its critical refs are
    /// re-checked the same way — but a build that does not recognise the
    /// version must not read fields it does not understand.
    /// </summary>
    public const int GroundingVersion = 1;

    public DayPlanPlanner(
        AppDbContext db,
        IRoutingService routing,
        ITravelDataRegistry travelData,
        ActivityDurationTable durations,
        DayScheduleEngine engine)
    {
        _db = db;
        _routing = routing;
        _travelData = travelData;
        _durations = durations;
        _engine = engine;
    }

    public async Task<DayPlanResult> PlanAsync(DayPlanInput input, CancellationToken ct)
    {
        var paceValue = TripPaces.ToWireValue(input.Pace);
        var mode = input.RequestedMode ?? input.Transport.PrimaryMode;

        // ── Existing activities become fixed anchors ────────────────────────
        //
        // Without this the plan is built in a vacuum and lands on top of a
        // booking the user already made. Anything already saved with a time is
        // immovable here; changing one is a separate proposal.
        var existing = await _db.TripActivities
            .AsNoTracking()
            .Where(activity => activity.TripId == input.TripId && activity.Date == input.Date)
            .OrderBy(activity => activity.SortIndex)
            .ToListAsync(ct);

        var candidates = new List<ScheduleCandidate>();

        foreach (var activity in existing)
        {
            var coordinates = ActivityLocationMarkers.Read(activity.Description);
            var role = ActivityRoles.FromCategory(activity.Category, activity.EndDate);
            var duration = DurationOf(activity.Time, activity.EndTime)
                ?? _durations.Estimate(activity.Category, paceValue).Minutes;

            candidates.Add(new ScheduleCandidate
            {
                Id = "existing:" + activity.Id,
                Title = activity.Title,
                Category = activity.Category,
                // Only a saved activity WITH a time is an anchor. One without
                // is a note, and pinning the day to it would be inventing a
                // commitment the user never made.
                IsFixed = ParseTime(activity.Time) != null,
                FixedStart = ParseTime(activity.Time),
                DurationMinutes = duration,
                DurationSource = activity.EndTime != null ? DurationSources.User : DurationSources.CategoryEstimate,
                Latitude = coordinates.Latitude,
                Longitude = coordinates.Longitude,
                Role = role,
                Meal = MealOf(activity.Category, ParseTime(activity.Time)),
                // Existing commitments outrank new suggestions when the day is
                // full.
                Priority = 100,
            });
        }

        // ── The model's proposed stops ──────────────────────────────────────
        var hoursLookups = 0;

        for (var index = 0; index < input.Items.Count; index++)
        {
            var item = input.Items[index];
            var (minutes, source) = _durations.Estimate(item.Category, paceValue, item.DurationMinutes);

            OpeningHours? hours = null;
            if (item.PlaceId != null && hoursLookups < MaxOpeningHourLookups)
            {
                hoursLookups++;
                // A details call the cache almost always answers: search_places
                // fetched this exact place moments ago.
                var place = await _travelData.GetPlaceDetailsAsync(item.PlaceId, input.Language, ct);
                hours = place?.Hours;
            }

            candidates.Add(new ScheduleCandidate
            {
                Id = "new:" + index,
                Title = item.Title,
                Category = item.Category,
                IsFixed = item.FixedTime != null,
                FixedStart = item.FixedTime,
                DurationMinutes = minutes,
                DurationSource = source,
                Latitude = item.Latitude,
                Longitude = item.Longitude,
                OpeningHours = hours,
                Role = ActivityRoles.FromCategory(item.Category, null),
                Meal = MealOf(item.Category, item.FixedTime),
                // Earlier in the model's list means it mattered more. Below the
                // existing-activity floor, so nothing new displaces a booking.
                Priority = Math.Max(0, 50 - index),
            });
        }

        // ── Verified travel times ───────────────────────────────────────────
        var legs = await ResolveLegsAsync(candidates, input, mode, ct);

        var (earliest, latest) = DayBounds(existing, input);

        var schedule = _engine.Build(new ScheduleRequest
        {
            Date = input.Date,
            Candidates = candidates,
            DayStart = input.StartTime ?? new TimeOnly(9, 0),
            DayEnd = input.EndTime ?? new TimeOnly(21, 0),
            Pace = input.Pace,
            PrimaryMode = mode,
            EarliestStart = earliest,
            LatestEnd = latest,
            Legs = legs,
            NowUtc = DateTime.UtcNow,
        });

        var payload = BuildPayload(schedule, input, mode);

        return new DayPlanResult
        {
            Schedule = schedule,
            Payload = payload,
            Summary = BuildSummary(schedule, input.Date),
            RoutingVerified = _routing.HasVerifiedRouting,
        };
    }

    public async Task<DayPlanResult> RevalidateAsync(
        Guid tripId, JsonElement payload, string language, CancellationToken ct)
    {
        var date = ReadDate(payload, "date") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var pace = TripPaces.Parse(ReadString(payload, "pace"));
        var mode = TravelModes.Parse(ReadString(payload, "transportMode"));

        var items = new List<DayPlanItem>();
        if (payload.TryGetProperty("activities", out var activities) && activities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in activities.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                // An edited row keeps whatever the user set. A start time they
                // chose IS fixed — the engine must honour it rather than
                // helpfully re-deriving one.
                items.Add(new DayPlanItem(
                    ReadString(entry, "title") ?? "",
                    ReadString(entry, "description"),
                    ParseTime(ReadString(entry, "time")),
                    ReadString(entry, "category"),
                    ReadInt(entry, "durationMinutes"),
                    ReadDouble(entry, "latitude"),
                    ReadDouble(entry, "longitude"),
                    ReadString(entry, "locationLabel"),
                    ReadString(entry, "placeId")));
            }
        }

        items.RemoveAll(item => string.IsNullOrWhiteSpace(item.Title));

        return await PlanAsync(new DayPlanInput
        {
            TripId = tripId,
            Date = date,
            Items = items,
            Pace = pace,
            Transport = new TransportPreferences { PrimaryMode = mode },
            RequestedMode = mode,
            StartTime = ParseTime(ReadString(payload, "startTime")),
            EndTime = ParseTime(ReadString(payload, "endTime")),
            Language = language,
        }, ct);
    }

    /// <summary>
    /// Asks the routing layer about every pair of stops that could end up
    /// adjacent.
    ///
    /// Every pair rather than only the proposed order, because the engine
    /// reorders: it inserts flexible stops wherever they fit, and a leg it has
    /// no time for would fall back to an estimate. The matrix returns the whole
    /// grid for one call anyway, so asking for all of it costs nothing extra.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, RouteLeg>> ResolveLegsAsync(
        IReadOnlyList<ScheduleCandidate> candidates, DayPlanInput input, TravelMode mode, CancellationToken ct)
    {
        var located = candidates
            .Where(candidate => candidate.Latitude.HasValue && candidate.Longitude.HasValue)
            .ToList();

        var legs = new Dictionary<string, RouteLeg>(StringComparer.Ordinal);
        if (located.Count < 2 || !_routing.HasVerifiedRouting) return legs;

        // Departure at the middle of the planned day: close enough for traffic
        // and timetables, and it keeps every leg of the day on ONE cache
        // bucket instead of one per stop.
        var midday = input.StartTime ?? new TimeOnly(12, 0);
        var departureUtc = input.Date.ToDateTime(midday, DateTimeKind.Utc);

        var requests = new List<RouteRequest>();
        var pairs = new List<(string From, string To)>();

        foreach (var from in located)
        {
            foreach (var to in located)
            {
                if (from.Id == to.Id) continue;

                var straightLineKm = GeoDistance.KilometresBetween(
                    from.Latitude, from.Longitude, to.Latitude, to.Longitude);

                requests.Add(new RouteRequest(
                    new RoutePoint(from.Latitude!.Value, from.Longitude!.Value, from.Title),
                    new RoutePoint(to.Latitude!.Value, to.Longitude!.Value, to.Title),
                    // Mode per leg: a two-block hop is a walk even for a group
                    // with a car, and a leg past their walking limit is not.
                    input.RequestedMode ?? input.Transport.ModeForLeg(straightLineKm),
                    departureUtc));
                pairs.Add((from.Id, to.Id));
            }
        }

        var resolved = await _routing.GetLegsAsync(requests, ct);

        for (var index = 0; index < pairs.Count && index < resolved.Count; index++)
        {
            legs[$"{pairs[index].From}>{pairs[index].To}"] = resolved[index];
        }

        return legs;
    }

    /// <summary>
    /// The window the day's flexible stops may occupy.
    ///
    /// A flight is a hard edge in a way nothing else is: nobody sightsees
    /// before their plane lands or after it leaves. Fixed activities are still
    /// placed at their own times — this only bounds where NEW stops may go.
    /// </summary>
    private static (TimeOnly? Earliest, TimeOnly? Latest) DayBounds(
        IReadOnlyList<Models.TripActivity> existing, DayPlanInput input)
    {
        TimeOnly? earliest = input.StartTime;
        TimeOnly? latest = input.EndTime;

        foreach (var activity in existing)
        {
            if (ActivityRoles.FromCategory(activity.Category, activity.EndDate) != "transport") continue;

            var start = ParseTime(activity.Time);
            var end = ParseTime(activity.EndTime);

            // An arrival — a transport leg that ENDS today. Nothing before it.
            if (end is { } arrival && (earliest == null || arrival > earliest)) earliest = arrival;

            // A departure with no end time on the day: treat its start as the
            // day's ceiling only when it is in the afternoon, so a morning
            // train to the next town does not wipe out the whole day.
            if (end == null && start is { } departure && departure.Hour >= 12
                && (latest == null || departure < latest))
            {
                latest = departure;
            }
        }

        return (earliest, latest);
    }

    // ── Payload ───────────────────────────────────────────────────────────

    /// <summary>
    /// The proposal payload.
    ///
    /// Every number carries where it came from. That is not decoration: the
    /// review screen renders "12 min walk (verified)" differently from
    /// "~15 min (estimated)", the prompt has different sentences it is allowed
    /// to say about each, and apply refuses to claim a travel time was saved
    /// when only Activities were.
    /// </summary>
    private JsonElement BuildPayload(DaySchedule schedule, DayPlanInput input, TravelMode mode)
    {
        var rows = schedule.Stops.Select(stop => new
        {
            title = stop.Candidate.Title,
            description = DescriptionOf(stop),
            category = stop.Candidate.Category,
            time = stop.Start.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            endTime = stop.End.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            durationMinutes = stop.Candidate.DurationMinutes,
            durationSource = stop.Candidate.DurationSource,
            isFixed = stop.Candidate.IsFixed,
            // Rows that already exist are shown for context and are NOT
            // re-created on apply.
            existingActivityId = stop.Candidate.Id.StartsWith("existing:", StringComparison.Ordinal)
                ? stop.Candidate.Id["existing:".Length..]
                : null,
            latitude = stop.Candidate.Latitude,
            longitude = stop.Candidate.Longitude,
            locationLabel = (string?)null,
            travelFromPrevious = stop.TravelFromPrevious == null ? null : new
            {
                minutes = stop.TravelFromPrevious.Minutes,
                verified = stop.TravelFromPrevious.Verified,
                mode = TravelModes.ToWireValue(stop.TravelFromPrevious.Mode),
                modeLabel = TravelModes.Label(stop.TravelFromPrevious.Mode, input.Language),
                distanceKm = stop.TravelFromPrevious.DistanceKm,
                source = stop.TravelFromPrevious.Source,
            },
            openingHours = stop.Opening == null ? null : new
            {
                status = stop.Opening.Status.ToString().ToLowerInvariant(),
                opensAt = stop.Opening.OpensAt?.ToString("HH\\:mm", CultureInfo.InvariantCulture),
                closesAt = stop.Opening.ClosesAt?.ToString("HH\\:mm", CultureInfo.InvariantCulture),
                warning = stop.Opening.WarningCode,
                source = stop.Candidate.OpeningHours?.Source,
            },
            warnings = stop.Warnings,
        }).ToList();

        return JsonSerializer.SerializeToElement(new
        {
            date = input.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            startTime = input.StartTime?.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            endTime = input.EndTime?.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            pace = TripPaces.ToWireValue(input.Pace),
            transportMode = TravelModes.ToWireValue(mode),
            transportModeLabel = TravelModes.Label(mode, input.Language),
            // Whether ANY verified routing was available at all. The app uses
            // this for the "travel times are estimates" note, and it must never
            // be inferred from the presence of numbers.
            routingVerified = _routing.HasVerifiedRouting,
            feasible = schedule.Feasible,
            utilisation = schedule.Utilisation,
            warnings = schedule.Warnings,
            activities = rows,
            dropped = schedule.Dropped.Select(item => new
            {
                title = item.Candidate.Title,
                reason = item.Reason,
            }).ToList(),
            // Travel is shown between rows; it is never an Activity of its own
            // unless the user asks for one. Stated explicitly so apply and the
            // review screen cannot disagree about it.
            saveTravelAsActivities = false,
            // ── Grounding ────────────────────────────────────────────────
            //
            // What this plan rests on, in stable references only. NOT the
            // prompt, not the model's reasoning, not a provider payload — just
            // enough that apply can re-check the things that would make the
            // plan wrong if they changed underneath it.
            //
            // The distinction that matters at apply time: a stale RATING is a
            // warning (the user already reviewed the plan and a rating does not
            // change what gets saved), while a stale DATE or Activity id is a
            // blocker (it would write to the wrong place).
            grounding = new
            {
                version = GroundingVersion,
                // The date is critical: applying to the wrong day is silent and
                // destructive.
                criticalRefs = new
                {
                    date = input.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    tripId = input.TripId,
                    existingActivityIds = schedule.Stops
                        .Where(stop => stop.Candidate.Id.StartsWith("existing:", StringComparison.Ordinal))
                        .Select(stop => stop.Candidate.Id["existing:".Length..])
                        .ToList(),
                },
                // Provider data behind the plan. Going stale here is worth a
                // warning, not a refusal.
                providerRefs = schedule.Stops
                    .Where(stop => stop.Candidate.OpeningHours != null)
                    .Select(stop => new
                    {
                        title = stop.Candidate.Title,
                        source = stop.Candidate.OpeningHours!.Source,
                        verifiedAt = stop.Candidate.OpeningHours.FetchedAtUtc,
                    })
                    .ToList(),
                routing = new
                {
                    verified = _routing.HasVerifiedRouting,
                    legs = schedule.Stops.Count(stop => stop.TravelFromPrevious is { Verified: true }),
                    estimatedLegs = schedule.Stops.Count(stop => stop.TravelFromPrevious is { Verified: false }),
                },
                // Which parts are assumptions rather than measurements.
                estimates = new
                {
                    durations = schedule.Stops
                        .Count(stop => stop.Candidate.DurationSource == DurationSources.CategoryEstimate),
                    travelTimes = schedule.Stops.Count(stop => stop.TravelFromPrevious is { Verified: false }),
                },
                // The constraints the engine actually checked, so a reviewer
                // knows what "feasible: true" covered.
                validatedConstraints = new[]
                {
                    "no_overlaps", "fixed_activities_preserved", "day_window",
                    "opening_hours_where_known", "travel_time_between_stops", "pace_stop_count",
                },
            },
        });
    }

    /// The description keeps the model's prose. Duration and travel live in
    /// their own fields — writing "about 2 hours" into the description would
    /// bake an estimate into saved text where nothing can correct it later.
    private static string? DescriptionOf(ScheduledStop stop) => null;

    private static string BuildSummary(DaySchedule schedule, DateOnly date)
    {
        var count = schedule.Stops.Count(stop => !stop.Candidate.Id.StartsWith("existing:", StringComparison.Ordinal));
        var suffix = schedule.Dropped.Count > 0 ? $", {schedule.Dropped.Count} didn't fit" : "";

        return $"Plan for {date:yyyy-MM-dd} — {count} {(count == 1 ? "Activity" : "Activities")}{suffix}";
    }

    // ── Small helpers ─────────────────────────────────────────────────────

    private static int? DurationOf(string? start, string? end)
    {
        var from = ParseTime(start);
        var to = ParseTime(end);
        if (from == null || to == null) return null;

        var minutes = (to.Value.Hour * 60 + to.Value.Minute) - (from.Value.Hour * 60 + from.Value.Minute);
        return minutes > 0 ? minutes : null;
    }

    private static MealSlot MealOf(string? category, TimeOnly? time)
    {
        if (ActivityRoles.FromCategory(category, null) != "meal") return MealSlot.None;

        var normalised = category?.Trim().ToLowerInvariant();
        if (normalised is "breakfast" or "frukost") return MealSlot.Breakfast;
        if (normalised is "lunch") return MealSlot.Lunch;
        if (normalised is "dinner" or "middag") return MealSlot.Dinner;

        // Fall back on the clock when the category is just "food".
        return time?.Hour switch
        {
            < 11 => MealSlot.Breakfast,
            >= 11 and < 16 => MealSlot.Lunch,
            >= 16 => MealSlot.Dinner,
            _ => MealSlot.Lunch,
        };
    }

    private static TimeOnly? ParseTime(string? value)
        => TimeOnly.TryParseExact(value, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static DateOnly? ReadDate(JsonElement element, string name)
        => DateOnly.TryParseExact(
            ReadString(element, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
