using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Data;
using sidequest.backend.Models;
using sidequest.backend.Services;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Proof that the weather screen and Gluno read the SAME rows.
///
/// WHY THESE ARE DATABASE-BACKED WHEN NOTHING ELSE HERE IS. The bug they exist
/// for cannot be caught any other way. Both features already called
/// ResolveTimeline — a pure function — and every test that handed each a
/// hand-built list passed, because the function is deterministic and always
/// was. What no such test can show is whether the two CALLERS load the same
/// thing. They each ran their own query, and a difference in either one
/// produces exactly the reported symptom: cities on the weather screen, and an
/// assistant insisting no places are set.
///
/// So these run both production loaders against one in-memory database and
/// compare day by day. In-memory, per test, never a real connection string.
/// </summary>
public class SharedTimelineEvals
{
    private static AppDbContext NewDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"timeline-{Guid.NewGuid()}")
            .ConfigureWarnings(warnings =>
                warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// The reported Adventure: destination "España", 5–16 August, six cities
    /// stored per day.
    /// </summary>
    private static async Task<Guid> SeedSemester2026(AppDbContext db, bool withExtraStop = false)
    {
        var tripId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            Title = "Semester 2026",
            Destination = "España",
            DestinationLatitude = 40.42,
            DestinationLongitude = -3.70,
            StartDate = new DateOnly(2026, 8, 5),
            EndDate = new DateOnly(2026, 8, 16),
        });

        var cities = new (string Label, int Day, double Lat, double Lon)[]
        {
            ("Málaga", 5, 36.72, -4.42),
            ("Ronda", 8, 36.74, -5.16),
            ("Gibraltar", 10, 36.14, -5.35),
            ("Tanger", 11, 35.76, -5.83),
            ("Sevilla", 14, 37.39, -5.98),
            ("Faro", 16, 37.02, -7.93),
        };

        foreach (var (label, day, lat, lon) in cities)
        {
            db.TripDayLocations.Add(new TripDayLocation
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                StartDate = new DateOnly(2026, 8, day),
                SortIndex = 0,
                LocationLabel = label,
                Latitude = lat,
                Longitude = lon,
            });
        }

        if (withExtraStop)
        {
            db.TripDayLocations.Add(new TripDayLocation
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                StartDate = new DateOnly(2026, 8, 8),
                SortIndex = 1,
                LocationLabel = "Setenil",
                Latitude = 36.86,
                Longitude = -5.18,
            });
        }

        await db.SaveChangesAsync();
        return tripId;
    }

    private static TripResolvedLocationTimelineService Service(AppDbContext db) => new(db);

    /// The route exactly as Gluno's context builder produces it, from the
    /// shared timeline.
    private static async Task<TripRouteContext> GlunoRoute(AppDbContext db, Guid tripId)
    {
        var resolved = await Service(db).BuildAsync(tripId, null, CancellationToken.None);

        return TripRouteResolver.Build(
            resolved!.Trip, resolved.DayLocations, Array.Empty<GlunoActivityContext>());
    }

    // ── The reported symptom ─────────────────────────────────────────────

    [Fact]
    public async Task The_reported_Adventure_yields_six_cities_and_not_the_country()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        var route = await GlunoRoute(db, tripId);
        var labels = route.Stops.Where(stop => stop.IsMainStop).Select(stop => stop.Label).ToList();

        // The exact answer that was wrong in production.
        Assert.Equal(
            new[] { "Málaga", "Ronda", "Gibraltar", "Tanger", "Sevilla", "Faro" }, labels);
        Assert.False(route.IsDestinationOnly);
        Assert.DoesNotContain("España", labels);
    }

    [Fact]
    public async Task Weather_and_Gluno_resolve_the_same_place_for_every_single_day()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        // Both production paths, one database.
        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var route = await GlunoRoute(db, tripId);

        for (var date = new DateOnly(2026, 8, 5); date <= new DateOnly(2026, 8, 16); date = date.AddDays(1))
        {
            var iso = date.ToString("yyyy-MM-dd");

            // What the weather screen labels this day.
            var weatherLabel = shared!.Days
                .FirstOrDefault(day => day?.Date == date)?.LocationLabel;

            // What Gluno says the trip is doing this day.
            var glunoLabel = route.Stops
                .FirstOrDefault(stop => stop.IsMainStop && stop.Dates.Contains(iso))?.Label;

            Assert.Equal(weatherLabel, glunoLabel);
        }
    }

    [Fact]
    public async Task Both_read_the_same_rows_from_the_same_query()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db, withExtraStop: true);

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);

        // Seven rows: six main locations and one extra stop. Neither caller
        // clips, filters or projects any of them away.
        Assert.Equal(7, shared!.DayLocations.Count);
        Assert.Equal(6, shared.MainLocationCount);
        Assert.Equal(1, shared.ExtraStopCount);
    }

    // ── The invariant ────────────────────────────────────────────────────

    [Fact]
    public async Task Stored_locations_can_never_collapse_to_the_country()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var route = await GlunoRoute(db, tripId);

        // THE invariant. If the shared timeline found stored locations, the
        // route has to show them — the failure mode was a route that came back
        // as the country alone while the timeline held six cities.
        Assert.False(shared!.IsDestinationOnly);
        Assert.False(route.IsDestinationOnly);
        Assert.True(route.Stops.Count(stop => stop.IsMainStop) >= shared.MainLocationCount);
    }

    [Fact]
    public async Task A_trip_with_no_stored_locations_is_the_only_country_only_case()
    {
        using var db = NewDb();
        var tripId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            Title = "Somewhere",
            Destination = "España",
            DestinationLatitude = 40.42,
            DestinationLongitude = -3.70,
            StartDate = new DateOnly(2026, 8, 5),
            EndDate = new DateOnly(2026, 8, 16),
        });
        await db.SaveChangesAsync();

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var route = await GlunoRoute(db, tripId);

        // Both agree it is the country, and both say so for the same reason.
        Assert.True(shared!.IsDestinationOnly);
        Assert.True(route.IsDestinationOnly);
    }

    // ── The loading rules ────────────────────────────────────────────────

    [Fact]
    public async Task Every_stored_location_is_loaded_with_no_cap()
    {
        using var db = NewDb();
        var tripId = Guid.NewGuid();

        db.Trips.Add(new Trip
        {
            Id = tripId,
            Title = "Long one",
            Destination = "España",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 4, 10),
        });

        // 100 days, 100 anchors — past any Take() a caller might have applied.
        for (var index = 0; index < 100; index++)
        {
            db.TripDayLocations.Add(new TripDayLocation
            {
                Id = Guid.NewGuid(),
                TripId = tripId,
                StartDate = new DateOnly(2026, 1, 1).AddDays(index),
                SortIndex = 0,
                LocationLabel = $"Stop {index}",
                Latitude = 40 + (index * 0.01),
                Longitude = -3,
            });
        }
        await db.SaveChangesAsync();

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);

        // A clipping rule is a rule two callers can disagree about. There is
        // none.
        Assert.Equal(100, shared!.DayLocations.Count);
    }

    [Fact]
    public async Task Only_the_asked_for_trips_rows_are_loaded()
    {
        using var db = NewDb();
        var mine = await SeedSemester2026(db);
        var theirs = await SeedSemester2026(db);

        var shared = await Service(db).BuildAsync(mine, null, CancellationToken.None);

        Assert.Equal(6, shared!.DayLocations.Count);
        Assert.All(shared.DayLocations, row => Assert.Equal(mine, row.TripId));
        Assert.NotEqual(mine, theirs);
    }

    [Fact]
    public async Task An_unknown_trip_resolves_to_nothing_rather_than_an_empty_route()
    {
        using var db = NewDb();

        // Null, not an empty timeline. "This trip does not exist" and "this
        // trip has no places" are different answers.
        Assert.Null(await Service(db).BuildAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Sort_index_ordering_is_preserved()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db, withExtraStop: true);

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);

        var onTheEighth = shared!.DayLocations
            .Where(row => row.StartDate == new DateOnly(2026, 8, 8))
            .ToList();

        // Main location first, extra stop second — the order the resolver
        // relies on to know which one anchors the day.
        Assert.Equal(2, onTheEighth.Count);
        Assert.Equal(0, onTheEighth[0].SortIndex);
        Assert.Equal(1, onTheEighth[1].SortIndex);
    }

    [Fact]
    public async Task Carry_forward_is_identical_on_both_sides()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var route = await GlunoRoute(db, tripId);

        // 6 and 7 August are not stored rows — they carry forward from the 5th.
        foreach (var day in new[] { 6, 7 })
        {
            var iso = $"2026-08-0{day}";

            Assert.Equal(
                "Málaga",
                shared!.Days.First(entry => entry?.Date == new DateOnly(2026, 8, day))!.LocationLabel);

            Assert.Equal(
                "Málaga",
                route.Stops.First(stop => stop.IsMainStop && stop.Dates.Contains(iso)).Label);
        }
    }

    [Fact]
    public async Task An_extra_stop_applies_to_its_own_day_on_both_sides()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db, withExtraStop: true);

        var shared = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var route = await GlunoRoute(db, tripId);

        // The extra stop never anchors a day: the 8th and 9th are still Ronda.
        Assert.Equal(
            "Ronda",
            shared!.Days.First(day => day?.Date == new DateOnly(2026, 8, 9))!.LocationLabel);

        var extra = route.Stops.First(stop => stop.Label == "Setenil");
        Assert.False(extra.IsMainStop);
        Assert.Single(extra.Dates);
    }

    [Fact]
    public async Task The_forecast_horizon_narrows_the_range_and_nothing_else()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        var full = await Service(db).BuildAsync(tripId, null, CancellationToken.None);
        var clipped = await Service(db).BuildAsync(
            tripId, new DateOnly(2026, 8, 9), CancellationToken.None);

        // Weather stops at its horizon because past it there are no numbers.
        // The ROWS are the same; only how far the walk goes differs.
        Assert.Equal(full!.DayLocations.Count, clipped!.DayLocations.Count);
        Assert.Equal(12, full.Days.Count);
        Assert.Equal(5, clipped.Days.Count);
    }

    [Fact]
    public async Task A_backwards_range_does_not_produce_a_placeless_trip()
    {
        using var db = NewDb();
        var tripId = await SeedSemester2026(db);

        // An end before the start would iterate nothing and look exactly like
        // a trip with no locations at all.
        var clipped = await Service(db).BuildAsync(
            tripId, new DateOnly(2026, 8, 1), CancellationToken.None);

        Assert.NotEmpty(clipped!.Days);
        Assert.False(clipped.IsDestinationOnly);
    }

    // ── Both callers actually use it ─────────────────────────────────────

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    [Fact]
    public void The_weather_controller_goes_through_the_shared_service()
    {
        var source = Source("Controllers", "TripWeatherController.cs");

        Assert.Contains("ITripResolvedLocationTimelineService", source);
        Assert.Contains("_timeline.BuildAsync(", source);
    }

    [Fact]
    public void Gluno_goes_through_the_shared_service()
    {
        var source = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        Assert.Contains("ITripResolvedLocationTimelineService _timeline", source);
        Assert.Contains("await _timeline.BuildAsync(tripId, endOverride: null, ct)", source);
    }

    [Fact]
    public void Neither_caller_still_loads_day_locations_itself()
    {
        var weather = Source("Controllers", "TripWeatherController.cs");
        var gluno = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        // The whole point. A second query is a second set of rules about which
        // rows count, and the two would drift again.
        Assert.DoesNotContain("_db.TripDayLocations", weather);
        Assert.DoesNotContain("_db.TripDayLocations", gluno);
    }

    [Fact]
    public void Neither_caller_calls_the_resolver_directly()
    {
        var weather = Source("Controllers", "TripWeatherController.cs");
        var gluno = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        // Calling the same pure function with your own list is precisely the
        // pattern that produced this bug.
        Assert.DoesNotContain("ResolveTimeline(", weather);
        Assert.DoesNotContain("ResolveTimeline(", gluno);
    }

    [Fact]
    public void The_shared_service_is_registered()
    {
        var program = Source("Program.cs");

        Assert.Contains(
            "AddScoped<ITripResolvedLocationTimelineService, TripResolvedLocationTimelineService>()",
            program);
    }

    [Fact]
    public void A_collapsed_route_is_logged_as_an_error()
    {
        var source = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        // Silent was how this survived to production. If the timeline has rows
        // and the route does not, that is loud.
        Assert.Contains("if (!resolved.IsDestinationOnly && route.IsDestinationOnly)", source);
        Assert.Contains("LogError", source);
    }

    // ── Scope: the route reaches the model only when there IS a trip ─────

    [Fact]
    public void A_conversation_with_no_Adventure_has_no_route_at_all()
    {
        var source = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        // THE remaining way to see "I only have España and the dates": a
        // conversation with no trip. The route is null, and the model falls
        // back to the Adventure SUMMARY — which carries Destination and dates
        // and nothing else. That is the reported sentence, from a global chat.
        Assert.Contains("var route = tripId.HasValue", source);
        Assert.Contains("            : null;", source);
    }

    [Fact]
    public void The_scope_comes_from_the_conversation_row_not_the_request()
    {
        var source = Source("Services", "Gluno", "GlunoChatService.cs");

        // A client cannot widen or narrow its own scope. scopeTripId is
        // resolved server-side from the clarification the user answered.
        Assert.Contains("scopeTripId ?? conversation.TripId", source);
    }

    [Fact]
    public void The_Adventure_summary_is_exactly_the_wrong_answer_shaped()
    {
        var source = Source("Services", "Gluno", "GlunoContext.cs");

        var start = source.IndexOf("public sealed class GlunoTripSummary", StringComparison.Ordinal);
        var body = source[start..(start + 700)];

        // Title, Destination, StartDate, EndDate. Nothing about cities. When
        // the route is absent this is all the model has for a trip, which is
        // why the fallback answer reads exactly as it did.
        Assert.Contains("public string Destination", body);
        Assert.Contains("public DateOnly StartDate", body);
        Assert.DoesNotContain("Stops", body);
    }

    // ── History must not outrank the route ───────────────────────────────

    [Fact]
    public void The_prompt_makes_the_route_outrank_an_older_claim()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        // A stale assistant turn saying "no places are set" must not survive
        // into a turn whose route says otherwise. The context is what is true
        // now; the history is what was said then.
        Assert.Contains("The route in this turn's context is what is TRUE NOW", prompt);
    }

    [Fact]
    public void Listing_the_stops_needs_no_model_round()
    {
        // "Which cities are we going to?" is answerable from the route alone.
        // The chain is data, and reading it back is not a judgement call.
        using var db = NewDb();
        var tripId = SeedSemester2026(db).GetAwaiter().GetResult();

        var route = GlunoRoute(db, tripId).GetAwaiter().GetResult();

        var listed = route.Stops
            .Where(stop => stop.IsMainStop)
            .Select(stop => $"{stop.Label} · {stop.From}–{stop.To}")
            .ToList();

        Assert.Equal(6, listed.Count);
        Assert.StartsWith("Málaga · 2026-08-05", listed[0]);
    }

    [Fact]
    public void The_diagnostics_carry_counts_and_no_content()
    {
        var source = Source("Services", "Gluno", "GlunoContextBuilder.cs");

        var index = source.IndexOf("[GLUNO] route resolved", StringComparison.Ordinal);
        Assert.True(index > 0);

        var line = source[index..(index + 700)];

        Assert.Contains("rows=", line);
        Assert.Contains("main=", line);
        Assert.Contains("destinationOnly=", line);
        // Never a place name, a date, a coordinate or the trip's title.
        Assert.DoesNotContain("Label", line);
        Assert.DoesNotContain("Latitude", line);
        Assert.DoesNotContain("Title", line);
    }
}
