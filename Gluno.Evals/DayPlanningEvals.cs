using System.Text.Json;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the part of Gluno that has to be RIGHT rather than merely
/// plausible: travel times, opening hours and the clock.
///
/// A wrong recommendation is a bad afternoon. A wrong travel time is someone
/// standing in a street watching a booking they cannot reach — and because the
/// number reads exactly like a correct one, nobody catches it until it fails.
/// So every case here pins a specific way that failure could happen.
///
/// Nothing calls a model, a network, or a database.
/// </summary>
public class DayPlanningEvals
{
    private static readonly DateOnly Monday = new(2026, 8, 10);

    /// Fixed "now" so freshness checks are deterministic — the same test must
    /// not pass in the morning and fail two weeks later.
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ActivityDurationTable Durations(params (string Key, string Value)[] overrides)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(overrides.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build());

    private static DayScheduleEngine Engine(params (string Key, string Value)[] overrides)
        => new(Durations(overrides));

    private static ScheduleCandidate Stop(
        string id,
        string title,
        int minutes = 60,
        double? latitude = null,
        double? longitude = null,
        TimeOnly? fixedStart = null,
        OpeningHours? hours = null,
        MealSlot meal = MealSlot.None,
        string role = "activity",
        int priority = 0)
        => new()
        {
            Id = id,
            Title = title,
            DurationMinutes = minutes,
            DurationSource = DurationSources.CategoryEstimate,
            Latitude = latitude,
            Longitude = longitude,
            IsFixed = fixedStart != null,
            FixedStart = fixedStart,
            OpeningHours = hours,
            Meal = meal,
            Role = role,
            Priority = priority,
        };

    private static ScheduleRequest Request(
        IReadOnlyList<ScheduleCandidate> candidates,
        TripPace pace = TripPace.Balanced,
        IReadOnlyDictionary<string, RouteLeg>? legs = null,
        TimeOnly? dayStart = null,
        TimeOnly? dayEnd = null,
        TravelMode mode = TravelMode.Walking)
        => new()
        {
            Date = Monday,
            Candidates = candidates,
            Pace = pace,
            PrimaryMode = mode,
            DayStart = dayStart ?? new TimeOnly(9, 0),
            DayEnd = dayEnd ?? new TimeOnly(21, 0),
            Legs = legs ?? new Dictionary<string, RouteLeg>(),
            NowUtc = Now,
        };

    private static RouteLeg Verified(string from, string to, int minutes, TravelMode mode = TravelMode.Walking)
        => new()
        {
            Origin = new RoutePoint(0, 0, from),
            Destination = new RoutePoint(0, 0, to),
            Mode = mode,
            DurationMinutes = minutes,
            DistanceKm = minutes / 12.0,
            Source = "google_routes",
            Verified = true,
        };

    private static OpeningHours Hours(params (DayOfWeek Day, int OpenHour, int CloseHour)[] intervals)
        => new()
        {
            Intervals = intervals
                .Select(entry => new OpeningInterval(entry.Day, new TimeOnly(entry.OpenHour, 0), new TimeOnly(entry.CloseHour, 0)))
                .ToList(),
            Source = "tripadvisor",
            FetchedAtUtc = Now.AddDays(-1),
        };

    // ── 1. A leg with no routing provider gets NO travel time ─────────────

    [Fact]
    public void Unconfigured_routing_produces_a_distance_but_never_a_duration()
    {
        var leg = RouteLeg.StraightLine(
            new RoutePoint(43.6961, 7.2758), new RoutePoint(43.6947, 7.2650), TravelMode.Walking, "no_provider");

        Assert.False(leg.Verified);
        Assert.Null(leg.DurationMinutes);
        Assert.NotNull(leg.DistanceKm);
        Assert.Equal("straight_line", leg.Source);
    }

    // ── 2. An estimated leg is never labelled verified ────────────────────

    [Fact]
    public void A_leg_without_provider_data_is_flagged_estimated_in_the_schedule()
    {
        var schedule = Engine().Build(Request([
            Stop("a", "Old town", latitude: 43.6961, longitude: 7.2758),
            Stop("b", "Promenade", latitude: 43.6947, longitude: 7.2650),
        ]));

        var second = schedule.Stops[1];
        Assert.NotNull(second.TravelFromPrevious);
        Assert.False(second.TravelFromPrevious!.Verified);
        Assert.Contains("travel_time_estimated", second.Warnings);
        Assert.Contains("unverified_travel_times", schedule.Warnings);
    }

    // ── 3. A verified leg is used and marked verified ─────────────────────

    [Fact]
    public void A_verified_leg_is_used_verbatim_and_kept_verified()
    {
        var legs = new Dictionary<string, RouteLeg> { ["a>b"] = Verified("a", "b", 17) };

        var schedule = Engine().Build(Request([
            Stop("a", "Old town", latitude: 43.6961, longitude: 7.2758),
            Stop("b", "Promenade", latitude: 43.6947, longitude: 7.2650),
        ], legs: legs));

        var travel = schedule.Stops[1].TravelFromPrevious!;
        Assert.True(travel.Verified);
        Assert.Equal(17, travel.Minutes);
        Assert.DoesNotContain("travel_time_estimated", schedule.Stops[1].Warnings);
    }

    // ── 4. Travel time actually pushes the next stop later ────────────────

    [Fact]
    public void Travel_time_and_buffer_are_subtracted_from_the_day_not_ignored()
    {
        var legs = new Dictionary<string, RouteLeg> { ["a>b"] = Verified("a", "b", 30) };

        var schedule = Engine().Build(Request([
            Stop("a", "Museum", minutes: 60, latitude: 43.6961, longitude: 7.2758),
            Stop("b", "Market", minutes: 60, latitude: 43.6947, longitude: 7.2650),
        ], legs: legs));

        // 09:00 + 60 min stop + 30 min travel + 20 min balanced buffer.
        Assert.Equal(new TimeOnly(9, 0), schedule.Stops[0].Start);
        Assert.Equal(new TimeOnly(10, 50), schedule.Stops[1].Start);
    }

    // ── 5. A fixed booking is never moved ─────────────────────────────────

    [Fact]
    public void A_fixed_booking_keeps_its_exact_time()
    {
        var schedule = Engine().Build(Request([
            Stop("dinner", "Booked dinner", minutes: 90, fixedStart: new TimeOnly(19, 0)),
            Stop("museum", "Museum", minutes: 120),
        ]));

        var dinner = schedule.Stops.Single(stop => stop.Candidate.Id == "dinner");
        Assert.Equal(new TimeOnly(19, 0), dinner.Start);
        Assert.True(dinner.Candidate.IsFixed);
    }

    // ── 6. Flexible stops are planned AROUND fixed ones ───────────────────

    [Fact]
    public void A_flexible_stop_is_placed_so_it_cannot_run_into_a_booking()
    {
        var schedule = Engine().Build(Request([
            Stop("dinner", "Booked dinner", minutes: 90, fixedStart: new TimeOnly(19, 0)),
            Stop("museum", "Museum", minutes: 120),
        ]));

        var museum = schedule.Stops.Single(stop => stop.Candidate.Id == "museum");
        Assert.True(museum.End <= new TimeOnly(19, 0));
        Assert.All(schedule.Stops, stop => Assert.DoesNotContain("overlaps_previous", stop.Warnings));
    }

    // ── 7. Two colliding bookings are REPORTED, not silently fixed ────────

    [Fact]
    public void Two_overlapping_bookings_make_the_day_infeasible_and_neither_is_moved()
    {
        var schedule = Engine().Build(Request([
            Stop("tour", "Guided tour", minutes: 120, fixedStart: new TimeOnly(14, 0)),
            Stop("dinner", "Booked dinner", minutes: 90, fixedStart: new TimeOnly(15, 0)),
        ]));

        Assert.False(schedule.Feasible);
        // Both keep the times the user committed to. An assistant that quietly
        // shifted one would be hiding a real-world clash.
        Assert.Equal(new TimeOnly(14, 0), schedule.Stops[0].Start);
        Assert.Equal(new TimeOnly(15, 0), schedule.Stops[1].Start);
        Assert.Contains("overlaps_previous_fixed", schedule.Stops[1].Warnings);
    }

    // ── 8. Nothing is ever scheduled on top of anything else ──────────────

    [Fact]
    public void No_two_scheduled_stops_ever_overlap()
    {
        var schedule = Engine().Build(Request([
            Stop("a", "One", minutes: 90),
            Stop("b", "Two", minutes: 90),
            Stop("c", "Three", minutes: 90),
            Stop("d", "Four", minutes: 90),
        ], pace: TripPace.Packed));

        for (var index = 1; index < schedule.Stops.Count; index++)
        {
            Assert.True(
                schedule.Stops[index].Start >= schedule.Stops[index - 1].End,
                $"{schedule.Stops[index].Candidate.Title} starts before the previous stop ends");
        }
    }

    // ── 9. A closed place is not scheduled while it is closed ─────────────

    [Fact]
    public void A_stop_is_pushed_to_when_the_place_actually_opens()
    {
        var schedule = Engine().Build(Request([
            Stop("museum", "Museum", minutes: 90, hours: Hours((DayOfWeek.Monday, 11, 18))),
        ]));

        Assert.Equal(new TimeOnly(11, 0), schedule.Stops[0].Start);
        Assert.Equal(OpeningStatus.Open, schedule.Stops[0].Opening!.Status);
    }

    // ── 10. A place shut all day is dropped, not scheduled ────────────────

    [Fact]
    public void A_place_closed_that_weekday_is_dropped_rather_than_planned()
    {
        var schedule = Engine().Build(Request([
            Stop("museum", "Museum", minutes: 90, hours: Hours((DayOfWeek.Tuesday, 10, 18))),
        ]));

        Assert.Empty(schedule.Stops);
        Assert.Single(schedule.Dropped);
        Assert.Equal("museum", schedule.Dropped[0].Candidate.Id);
    }

    // ── 11. Missing hours are UNKNOWN, never assumed closed ───────────────

    [Fact]
    public void Absent_opening_hours_are_unknown_and_do_not_block_planning()
    {
        var schedule = Engine().Build(Request([Stop("museum", "Museum", minutes: 90)]));

        Assert.Single(schedule.Stops);
        // No hours object at all means no claim in either direction.
        Assert.Null(schedule.Stops[0].Opening);
    }

    // ── 12. Stale hours stop being quoted ─────────────────────────────────

    [Fact]
    public void Opening_hours_older_than_the_freshness_window_become_unknown()
    {
        var stale = new OpeningHours
        {
            Intervals = [new OpeningInterval(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(18, 0))],
            Source = "tripadvisor",
            FetchedAtUtc = Now - OpeningHours.MaxAge - TimeSpan.FromDays(1),
        };

        var check = stale.Evaluate(Monday, new TimeOnly(11, 0), 60, Now);

        Assert.Equal(OpeningStatus.Unknown, check.Status);
        Assert.Equal("unknown_hours", check.WarningCode);
        Assert.Null(stale.Describe(Monday, "sv", Now));
    }

    // ── 13. Hours past midnight are handled ───────────────────────────────

    [Fact]
    public void A_place_open_past_midnight_counts_as_open_after_midnight()
    {
        var bar = new OpeningHours
        {
            Intervals = [new OpeningInterval(DayOfWeek.Sunday, new TimeOnly(20, 0), new TimeOnly(2, 0))],
            Source = "tripadvisor",
            FetchedAtUtc = Now,
        };

        // 00:30 on Monday is inside Sunday's 20:00–02:00 span.
        var check = bar.Evaluate(Monday, new TimeOnly(0, 30), 60, Now);

        Assert.Equal(OpeningStatus.Open, check.Status);
    }

    // ── 14. Several intervals in one day are not collapsed ────────────────

    [Fact]
    public void A_lunch_and_dinner_service_leaves_the_afternoon_closed()
    {
        var restaurant = Hours(
            (DayOfWeek.Monday, 12, 14),
            (DayOfWeek.Monday, 18, 23));

        Assert.Equal(OpeningStatus.Open, restaurant.Evaluate(Monday, new TimeOnly(12, 30), 60, Now).Status);
        Assert.Equal(OpeningStatus.Open, restaurant.Evaluate(Monday, new TimeOnly(19, 0), 90, Now).Status);
        // The gap between services must not read as open.
        Assert.Equal(OpeningStatus.Closed, restaurant.Evaluate(Monday, new TimeOnly(16, 0), 60, Now).Status);
    }

    // ── 15. A stop that outlasts closing time is flagged ──────────────────

    [Fact]
    public void A_visit_running_past_closing_time_is_warned_about()
    {
        var check = Hours((DayOfWeek.Monday, 10, 17))
            .Evaluate(Monday, new TimeOnly(16, 0), 120, Now);

        Assert.Equal(OpeningStatus.PartiallyOpen, check.Status);
        Assert.Equal("closes_before_end", check.WarningCode);
    }

    // ── 16. A day that will not fit loses a stop and SAYS so ──────────────

    [Fact]
    public void An_overfull_day_drops_stops_instead_of_producing_an_impossible_schedule()
    {
        var schedule = Engine().Build(Request(
            Enumerable.Range(0, 8)
                .Select(index => Stop($"s{index}", $"Stop {index}", minutes: 150))
                .ToList(),
            dayStart: new TimeOnly(9, 0),
            dayEnd: new TimeOnly(18, 0)));

        Assert.NotEmpty(schedule.Dropped);
        Assert.Contains("some_stops_did_not_fit", schedule.Warnings);
        Assert.All(schedule.Stops, stop => Assert.True(stop.End <= new TimeOnly(18, 0)));
    }

    // ── 17. Relaxed leaves real empty time ────────────────────────────────

    [Fact]
    public void A_relaxed_day_keeps_meaningfully_more_air_than_a_packed_one()
    {
        var candidates = Enumerable.Range(0, 6)
            .Select(index => Stop($"s{index}", $"Stop {index}", minutes: 90))
            .ToList();

        var relaxed = Engine().Build(Request(candidates, TripPace.Relaxed));
        var packed = Engine().Build(Request(candidates, TripPace.Packed));

        Assert.True(relaxed.Stops.Count < packed.Stops.Count);
        Assert.True(relaxed.Utilisation < 0.7);
    }

    // ── 18. Meals land at meal times ──────────────────────────────────────

    [Fact]
    public void Lunch_is_scheduled_inside_a_believable_lunch_window()
    {
        var schedule = Engine().Build(Request([
            Stop("morning", "Morning walk", minutes: 60),
            Stop("lunch", "Lunch", minutes: 60, meal: MealSlot.Lunch, role: "meal"),
            Stop("afternoon", "Gallery", minutes: 90),
        ]));

        var lunch = schedule.Stops.Single(stop => stop.Candidate.Id == "lunch");
        Assert.InRange(lunch.Start, new TimeOnly(11, 30), new TimeOnly(14, 30));
    }

    // ── 19. Nothing before the day's start, nothing after its end ─────────

    [Fact]
    public void Every_stop_stays_inside_the_days_window()
    {
        var schedule = Engine().Build(Request([
            Stop("a", "One", minutes: 60),
            Stop("b", "Two", minutes: 60),
            Stop("c", "Three", minutes: 60),
        ], dayStart: new TimeOnly(10, 0), dayEnd: new TimeOnly(16, 0)));

        Assert.All(schedule.Stops, stop =>
        {
            Assert.True(stop.Start >= new TimeOnly(10, 0));
            Assert.True(stop.End <= new TimeOnly(16, 0));
        });
    }

    // ── 20. Duration estimates are assumptions, and adjustable ────────────

    [Fact]
    public void Category_durations_are_labelled_as_estimates_and_move_with_pace()
    {
        var durations = Durations();

        var (balanced, source) = durations.Estimate("museum", "balanced");
        var (relaxed, _) = durations.Estimate("museum", "relaxed");
        var (packed, _) = durations.Estimate("museum", "packed");

        Assert.Equal(DurationSources.CategoryEstimate, source);
        Assert.True(relaxed > balanced);
        Assert.True(packed < balanced);
    }

    [Fact]
    public void A_provider_duration_beats_the_table_and_is_attributed_as_such()
    {
        var (minutes, source) = Durations().Estimate("museum", "balanced", providerMinutes: 45);

        Assert.Equal(45, minutes);
        Assert.Equal(DurationSources.Provider, source);
    }

    [Fact]
    public void The_duration_table_is_configurable_without_a_code_change()
    {
        var (minutes, _) = Durations(("Planning:Durations:museum", "45")).Estimate("museum", "balanced");

        Assert.Equal(45, minutes);
    }

    // ── Transport preferences ─────────────────────────────────────────────

    [Fact]
    public void A_car_is_never_assumed_from_distance_alone()
    {
        var preferences = TransportPreferences.From(null, null, null);

        Assert.False(preferences.CarAvailable);
        // A stop 40 km out still does not conjure a rental car.
        Assert.NotEqual(TravelMode.Driving, preferences.ModeForLeg(40));
    }

    [Fact]
    public void A_stated_rental_car_becomes_the_mode_for_a_long_leg_but_not_a_short_one()
    {
        var preferences = TransportPreferences.From("vi har hyrbil hela veckan", null, null);

        Assert.True(preferences.CarAvailable);
        Assert.Equal(TravelMode.Walking, preferences.ModeForLeg(0.4));
        Assert.Equal(TravelMode.Driving, preferences.ModeForLeg(12));
    }

    [Fact]
    public void Someone_who_said_they_do_not_want_to_drive_is_not_routed_by_car()
    {
        var preferences = TransportPreferences.From("vi vill inte köra bil, helst kollektivt", null, null);

        Assert.True(preferences.AvoidCar);
        Assert.False(preferences.CarAvailable);
        Assert.Equal(TravelMode.Transit, preferences.PrimaryMode);
    }

    [Fact]
    public void A_stated_walking_limit_is_read_out_of_the_users_own_words()
    {
        var preferences = TransportPreferences.From(null, "max 2 km mellan stoppen", null);

        Assert.Equal(2.0, preferences.MaxWalkKm);
        Assert.Equal(TravelMode.Walking, preferences.ModeForLeg(1.5));
    }

    [Fact]
    public void An_accessibility_note_shortens_the_walking_default_without_being_stored_as_health_data()
    {
        var preferences = TransportPreferences.From(null, null, "min mamma går med rullator");

        Assert.True(preferences.HasAccessibilityNeed);
        Assert.True(preferences.EffectiveMaxWalkKm < TransportPreferences.DefaultMaxWalkKm);
        // It is only ever read back out of the preference the user stated —
        // there is no separate structured health record to leak.
        Assert.Null(preferences.MaxWalkKm);
    }

    // ── Mode labels ───────────────────────────────────────────────────────

    [Fact]
    public void Swedish_mode_labels_are_the_ones_the_spec_asked_for()
    {
        Assert.Equal("Gång", TravelModes.Label(TravelMode.Walking, "sv"));
        Assert.Equal("Bil", TravelModes.Label(TravelMode.Driving, "sv"));
        Assert.Equal("Kollektivtrafik", TravelModes.Label(TravelMode.Transit, "sv"));
        Assert.Equal("Cykel", TravelModes.Label(TravelMode.Cycling, "sv"));
    }

    // ── Cache keys ────────────────────────────────────────────────────────

    [Fact]
    public void Rounded_coordinates_let_nearby_points_share_a_cache_entry()
    {
        // ~40 m apart: the same street corner as far as a route is concerned.
        Assert.Equal(
            new RoutePoint(43.69612, 7.27581).CacheKey(),
            new RoutePoint(43.69649, 7.27612).CacheKey());

        Assert.NotEqual(
            new RoutePoint(43.6961, 7.2758).CacheKey(),
            new RoutePoint(43.7384, 7.4246).CacheKey());
    }

    [Fact]
    public void Coordinate_validation_rejects_what_must_never_reach_a_provider()
    {
        Assert.False(new RoutePoint(900, 7.2).IsValid());
        Assert.False(new RoutePoint(43.7, 400).IsValid());
        Assert.False(new RoutePoint(double.NaN, 7.2).IsValid());
        Assert.True(new RoutePoint(43.6961, 7.2758).IsValid());
    }

    // ── Tripadvisor hours parsing ─────────────────────────────────────────

    [Fact]
    public void Structured_provider_hours_are_parsed_and_prose_lines_are_not()
    {
        var withPeriods = JsonSerializer.Deserialize<JsonElement>("""
            { "hours": { "periods": [
                { "open": { "day": 1, "time": "0900" }, "close": { "day": 1, "time": "1700" } }
            ] } }
            """);

        var parsed = OpeningHours.FromTripadvisor(withPeriods, Now);
        Assert.NotNull(parsed);
        Assert.Equal(OpeningStatus.Open, parsed!.Evaluate(Monday, new TimeOnly(10, 0), 60, Now).Status);

        // Display prose alone yields nothing: a misread timetable is worse
        // than an unknown one.
        var proseOnly = JsonSerializer.Deserialize<JsonElement>("""
            { "hours": { "weekday_text": ["Mon 9:00 AM - 5:00 PM"] } }
            """);

        Assert.Null(OpeningHours.FromTripadvisor(proseOnly, Now));
    }

    // ── The prompt's own rules ────────────────────────────────────────────

    [Fact]
    public void The_system_prompt_separates_the_four_kinds_of_number()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("VERIFIED TRAVEL TIME", prompt);
        Assert.Contains("STRAIGHT-LINE DISTANCE", prompt);
        Assert.Contains("DURATION ESTIMATE", prompt);
        Assert.Contains("VERIFIED OPENING HOURS", prompt);
    }

    [Fact]
    public void The_system_prompt_forbids_the_specific_sentences_that_would_be_lies()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("NEVER:", prompt);
        Assert.Contains("It's open now.", prompt);
        Assert.Contains("The train leaves at", prompt);
        // Travel time is shown between rows and never becomes an Activity —
        // claiming otherwise would tell the user something was saved that
        // was not.
        Assert.Contains("Never tell the user a travel time was added", prompt);
    }

    [Fact]
    public void The_system_prompt_tells_Gluno_to_report_what_did_not_fit()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("dropped", prompt);
        Assert.Contains("feasible is false", prompt);
    }
}
