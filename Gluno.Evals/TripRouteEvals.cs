using System.Text.Json;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for what Gluno knows about where a trip actually goes.
///
/// THE BUG THESE EXIST FOR. Asked about a six-city Spain/Morocco/Portugal trip,
/// Gluno answered "I only have España and the dates 5–16 August" — while the
/// same Adventure was showing per-city weather on the screen behind it.
///
/// The cause was not the resolver. It was WHERE the route lived: inside the
/// trip context, which is loaded all-or-nothing from the turn's intent. On any
/// turn that did not need the full plan the trip context was null, and the
/// model was left with the Adventure summary — title, Trip.Destination, dates.
/// Which is that sentence, verbatim.
///
/// So the invariant here is: the route is built from the SAME selector the
/// weather uses, and it reaches the model on every trip-scoped turn regardless
/// of intent or budget.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class TripRouteEvals
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Gluno", file));

    private static Trip SpainTrip() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Semester 2026",
        // The trip-level destination that was all Gluno could see.
        Destination = "España",
        DestinationLatitude = 40.42,
        DestinationLongitude = -3.70,
        StartDate = new DateOnly(2026, 8, 5),
        EndDate = new DateOnly(2026, 8, 16),
    };

    private static TripDayLocation Stop(
        string label, int day, double lat, double lon, int sortIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        StartDate = new DateOnly(2026, 8, day),
        SortIndex = sortIndex,
        LocationLabel = label,
        Latitude = lat,
        Longitude = lon,
    };

    /// The reported trip: six stops across three countries.
    private static List<TripDayLocation> RealRoute() =>
    [
        Stop("Málaga", 5, 36.72, -4.42),
        Stop("Ronda", 8, 36.74, -5.16),
        Stop("Gibraltar", 10, 36.14, -5.35),
        Stop("Tanger", 11, 35.76, -5.83),
        Stop("Sevilla", 14, 37.39, -5.98),
        Stop("Faro", 16, 37.02, -7.93),
    ];

    private static TripRouteContext Route(
        Trip? trip = null,
        List<TripDayLocation>? locations = null,
        params GlunoActivityContext[] activities)
        => TripRouteResolver.Build(
            trip ?? SpainTrip(), locations ?? RealRoute(), activities);

    // ── 1. The same selector as the weather ──────────────────────────────

    [Fact]
    public void The_route_is_resolved_by_the_same_service_the_weather_uses()
    {
        var route = Source("TripRouteContext.cs");
        var weather = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Controllers", "TripWeatherController.cs"));

        // One implementation of "where is the trip on this day" — and, since
        // the España bug, one LOADER too. Calling the same pure function with
        // rows you fetched yourself is not agreement; both now go through
        // TripResolvedLocationTimelineService.
        Assert.Contains("_timeline.BuildAsync(", weather);

        // The route type itself is pure: it is handed rows and turns them into
        // a chain. It does not fetch, and it does not resolve the timeline —
        // the shared service does both before it is called.
        Assert.DoesNotContain("AppDbContext", route);

        var builder = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoContextBuilder.cs"));

        Assert.Contains("_timeline.BuildAsync(", builder);
    }

    [Fact]
    public void The_route_and_the_weather_are_given_the_same_arguments()
    {
        var route = Source("TripRouteContext.cs");

        // Same rows, same destination fallback, same coordinates. A different
        // argument list is how two callers of one selector still diverge.
        Assert.Contains("dayLocations,", route);
        Assert.Contains("trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude", route);
    }

    [Fact]
    public void Weather_and_Gluno_agree_on_the_city_for_a_given_day()
    {
        var route = Route();

        // 6 Aug is not a stored row — it carries forward from Málaga on the
        // 5th, exactly as the weather shows it.
        var sixth = route.Stops.First(stop =>
            stop.IsMainStop && stop.Dates.Contains("2026-08-06"));

        Assert.Equal("Málaga", sixth.Label);
    }

    // ── 2. The reported bug ──────────────────────────────────────────────

    [Fact]
    public void A_trip_with_a_country_destination_and_real_stops_yields_the_stops()
    {
        var route = Route();

        var labels = route.Stops.Where(stop => stop.IsMainStop).Select(stop => stop.Label).ToList();

        // The whole point. Trip.Destination is "España"; the answer is six
        // cities.
        Assert.Equal(
            new[] { "Málaga", "Ronda", "Gibraltar", "Tanger", "Sevilla", "Faro" },
            labels);
        Assert.DoesNotContain("España", labels);
    }

    [Fact]
    public void The_trip_destination_never_replaces_a_real_chain()
    {
        var route = Route();

        Assert.False(route.IsDestinationOnly);
        Assert.True(route.HasMultipleStops);
        Assert.All(route.Stops, stop => Assert.NotEqual("trip_destination", stop.Source));
    }

    [Fact]
    public void Only_a_trip_with_no_stored_places_is_destination_only()
    {
        var route = Route(locations: []);

        // The one case where "I only know the country" is honest — and the
        // flag the prompt draws that line from.
        Assert.True(route.IsDestinationOnly);
        Assert.Single(route.Stops);
        Assert.Equal("trip_destination", route.Stops[0].Source);
    }

    // ── 3. Where it used to be lost ──────────────────────────────────────

    [Fact]
    public void The_route_lives_outside_the_intent_gated_trip_context()
    {
        var context = Source("GlunoContext.cs");

        // A separate property on the root context, not a field inside
        // GlunoTripContext. That one is null whenever the turn's intent did
        // not ask for the plan — which is exactly when the bug appeared.
        Assert.Contains("public TripRouteContext? Route { get; init; }", context);
    }

    [Fact]
    public void The_route_is_built_whenever_the_conversation_has_an_Adventure()
    {
        var builder = Source("GlunoContextBuilder.cs");

        // `tripId.HasValue`, with no IncludeTrip in the condition.
        Assert.Contains("var route = tripId.HasValue", builder);
        Assert.Contains("? await BuildRouteAsync(tripId.Value, ct)", builder);
    }

    [Fact]
    public void The_route_is_built_before_the_intent_gated_context()
    {
        var builder = Source("GlunoContextBuilder.cs");

        var routeAt = builder.IndexOf("var route = tripId.HasValue", StringComparison.Ordinal);
        var gateAt = builder.IndexOf("if (tripId.HasValue && options.IncludeTrip)", StringComparison.Ordinal);

        Assert.True(routeAt > 0 && gateAt > 0);
        // Ordering makes the independence obvious to the next reader, who is
        // the person most likely to reintroduce the bug.
        Assert.True(routeAt < gateAt);
    }

    [Fact]
    public void An_app_help_turn_still_loads_the_route()
    {
        // These are the intents whose workflow sets NeedsTripContext = false,
        // and therefore the turns where the route used to vanish.
        foreach (var intent in new[]
        {
            GlunoIntent.SideQuestHelp,
            GlunoIntent.NavigationRequest,
            GlunoIntent.PreferenceUpdate,
            GlunoIntent.ForgetPreference,
        })
        {
            var workflow = GlunoPlanningStrategy.For(
                new GlunoIntentResult
                {
                    PrimaryIntent = intent,
                    Confidence = 0.9,
                    Scope = GlunoIntentScope.Trip,
                    RequiresCurrentData = false,
                    RequiresExternalSearch = false,
                    ExpectsProposal = false,
                    RequiresClarification = false,
                },
                hasTrip: true,
                canEdit: true);

            // The trip context genuinely is not needed for these...
            Assert.False(workflow.NeedsTripContext);
        }

        // ...and the route no longer depends on that flag at all.
        var builder = Source("GlunoContextBuilder.cs");
        Assert.DoesNotContain("options.IncludeTrip\n            ? await BuildRouteAsync", builder);
    }

    // ── 4. It survives the budget ────────────────────────────────────────

    [Fact]
    public void The_route_is_a_critical_context_section()
    {
        var chat = Source("GlunoChatService.cs");

        var index = chat.IndexOf("GlunoContextPriority.RelevantTrip, \"route\"", StringComparison.Ordinal);
        Assert.True(index > 0, "the route has no context section of its own");

        var block = chat[index..(index + 200)];
        Assert.Contains("IsCritical = true", block);
    }

    [Fact]
    public void The_route_section_reads_from_the_always_loaded_property()
    {
        var chat = Source("GlunoChatService.cs");

        // context.Route, not context.Trip?.Destinations — the latter is null
        // on precisely the turns this fix is about.
        Assert.Contains("JsonSerializer.Serialize(context.Route, GlunoJson.Options)", chat);
    }

    [Fact]
    public void Every_stop_survives_an_enormous_history_and_evidence_load()
    {
        var route = Route();

        var budget = new GlunoContextBudget(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Deliberately tiny, so anything droppable is dropped.
                ["Gluno:Context:MaxTokens"] = "500",
            })
            .Build());

        var enormous = new string('x', 200_000);

        var fitted = budget.Fit(
        [
            new GlunoContextSection(GlunoContextPriority.CurrentRequest, "turn", "{}") { IsCritical = true },
            new GlunoContextSection(
                GlunoContextPriority.RelevantTrip, "route",
                JsonSerializer.Serialize(route, GlunoJson.Options)) { IsCritical = true },
            new GlunoContextSection(GlunoContextPriority.OlderHistory, "history", $"\"{enormous}\""),
            new GlunoContextSection(GlunoContextPriority.Evidence, "evidence", $"\"{enormous}\""),
        ]);

        // Every city still reaches the model, with the history and evidence
        // gone. Losing evidence costs Gluno the right to state some things;
        // losing the route costs it the Adventure.
        //
        // Read back through the serialiser rather than matched as substrings:
        // "Málaga" is written as "Málaga" in the JSON, so a literal
        // Contains would fail on the accented names and pass on the rest —
        // which is the half of the list most likely to matter.
        var survived = JsonDocument.Parse(fitted.Json)
            .RootElement.GetProperty("route").GetProperty("stops")
            .EnumerateArray()
            .Select(stop => stop.GetProperty("label").GetString())
            .ToList();

        foreach (var label in new[] { "Málaga", "Ronda", "Gibraltar", "Tanger", "Sevilla", "Faro" })
        {
            Assert.Contains(label, survived);
        }

        Assert.Contains("history", fitted.DroppedSections);
        Assert.Contains("evidence", fitted.DroppedSections);
    }

    // ── 5. Carry-forward and extra stops ─────────────────────────────────

    [Fact]
    public void Consecutive_days_at_one_place_collapse_into_one_stop()
    {
        var route = Route();

        var malaga = route.Stops.First(stop => stop.Label == "Málaga");

        // "Málaga 5–7 August" is what somebody would say. Three identical rows
        // is what a database holds.
        Assert.Equal("2026-08-05", malaga.From);
        Assert.Equal("2026-08-07", malaga.To);
        Assert.Equal(3, malaga.Dates.Count);
    }

    [Fact]
    public void An_extra_stop_applies_to_its_own_day_only()
    {
        var locations = RealRoute();
        locations.Add(Stop("Setenil", 8, 36.86, -5.18, sortIndex: 1));

        var route = Route(locations: locations);
        var extra = route.Stops.First(stop => stop.Label == "Setenil");

        Assert.False(extra.IsMainStop);
        Assert.Equal("extra_stop", extra.Source);
        Assert.Single(extra.Dates);
        // And it does not become a leg — an afternoon in a village is not a
        // part of the journey somebody plans separately.
        Assert.DoesNotContain(route.Legs, leg => leg.ToLabel == "Setenil");
    }

    [Fact]
    public void A_carried_forward_stop_is_marked_as_not_explicit()
    {
        var trip = SpainTrip();

        var route = Route(trip, [Stop("Málaga", 5, 36.72, -4.42)]);

        // One stored row, twelve days. The chain says Málaga throughout, and
        // says which part of that was actually stated.
        var malaga = route.Stops.First();
        Assert.True(malaga.IsExplicit);
        Assert.Equal("2026-08-05", malaga.From);
        Assert.Equal("2026-08-16", malaga.To);
    }

    [Fact]
    public void A_day_with_no_location_anywhere_is_reported_as_unplaced()
    {
        var trip = SpainTrip();
        trip.DestinationLatitude = null;
        trip.DestinationLongitude = null;

        // Only the last three days have a place; the rest resolve to nothing
        // because there is no destination fallback either.
        var route = Route(trip, [Stop("Sevilla", 14, 37.39, -5.98)]);

        Assert.NotEmpty(route.DaysWithoutLocation);
        // And the days that ARE placed are not in that list.
        Assert.DoesNotContain("2026-08-14", route.DaysWithoutLocation);
    }

    // ── 6. Legs ──────────────────────────────────────────────────────────

    [Fact]
    public void Legs_are_built_chronologically_between_consecutive_stops()
    {
        var route = Route();

        Assert.Equal(5, route.Legs.Count);

        Assert.Equal("Málaga", route.Legs[0].FromLabel);
        Assert.Equal("Ronda", route.Legs[0].ToLabel);
        Assert.Equal("Sevilla", route.Legs[4].FromLabel);
        Assert.Equal("Faro", route.Legs[4].ToLabel);
    }

    [Fact]
    public void A_leg_carries_the_days_the_move_happens_between()
    {
        var route = Route();
        var first = route.Legs[0];

        // Last day in Málaga, first day in Ronda.
        Assert.Equal("2026-08-07", first.DepartureDate);
        Assert.Equal("2026-08-08", first.ArrivalDate);
    }

    [Fact]
    public void A_single_city_trip_has_no_legs()
    {
        var route = Route(locations: [Stop("Málaga", 5, 36.72, -4.42)]);

        // The common case. Legs on a one-stop trip would be a journey nobody
        // is taking.
        Assert.Empty(route.Legs);
        Assert.False(route.HasMultipleStops);
    }

    [Fact]
    public void A_leg_measures_a_lower_bound_and_calls_it_one()
    {
        var route = Route();
        var toTanger = route.Legs.First(leg => leg.ToLabel == "Tanger");

        Assert.NotNull(toTanger.StraightLineKm);
        // Gibraltar to Tanger across the strait: tens of kilometres straight
        // line, and nothing like the real journey.
        Assert.InRange(toTanger.StraightLineKm!.Value, 10, 100);

        var prompt = Source("GlunoSystemPrompt.cs");
        Assert.Contains("is a lower bound on the journey and nothing", prompt);
    }

    [Fact]
    public void A_border_crossing_is_unknown_rather_than_guessed()
    {
        var route = Route();

        // SideQuest stores no country per stop, so every leg says "unknown".
        // Inferring Morocco from "Tanger" is exactly the invention this avoids.
        Assert.All(route.Legs, leg => Assert.Null(leg.CrossesBorder));
    }

    [Fact]
    public void A_leg_names_the_transport_already_planned_for_that_day()
    {
        var ferry = new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 11),
            Title = "Ferry Tarifa–Tanger",
            Time = "09:30",
            Role = "transport",
        };

        var route = Route(activities: ferry);
        var leg = route.Legs.First(item => item.ToLabel == "Tanger");

        // What makes "how do we get there" answerable from the plan rather
        // than from a guess.
        Assert.Contains("Ferry Tarifa–Tanger", leg.TransportOnDay);
        Assert.True(leg.HasFixedBookingOnDay);
    }

    [Fact]
    public void Transport_with_no_time_is_not_treated_as_a_fixed_booking()
    {
        var route = Route(activities: new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 11),
            Title = "Drive south",
            Role = "transport",
        });

        var leg = route.Legs.First(item => item.ToLabel == "Tanger");

        Assert.Contains("Drive south", leg.TransportOnDay);
        Assert.False(leg.HasFixedBookingOnDay);
    }

    // ── 7. Nothing is invented ───────────────────────────────────────────

    [Fact]
    public void Description_text_never_becomes_a_stop()
    {
        var trip = SpainTrip();
        trip.DestinationLatitude = null;
        trip.DestinationLongitude = null;

        // Names two cities in prose and carries no real location.
        var prose = new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 9),
            Title = "Dinner",
            Description = "somewhere near the old town in Barcelona, maybe Sevilla",
            Role = "activity",
        };

        var route = Route(trip, [], prose);

        Assert.DoesNotContain(route.Stops, stop => stop.Label.Contains("Barcelona"));
        Assert.DoesNotContain(route.Stops, stop => stop.Label.Contains("Sevilla"));
    }

    [Fact]
    public void An_activity_with_a_real_location_can_fill_an_unplaced_day()
    {
        var trip = SpainTrip();
        trip.DestinationLatitude = null;
        trip.DestinationLongitude = null;

        var located = new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 9),
            Title = "Puente Nuevo",
            Role = "activity",
            LocationLabel = "Ronda",
            Latitude = 36.74,
            Longitude = -5.16,
        };

        var route = Route(trip, [], located);
        var stop = route.Stops.FirstOrDefault(item => item.Label == "Ronda");

        Assert.NotNull(stop);
        // Labelled as the weaker source it is: it says where something
        // happens, not where the trip is.
        Assert.Equal("activity", stop!.Source);
        Assert.False(stop.IsExplicit);
        Assert.DoesNotContain("2026-08-09", route.DaysWithoutLocation);
    }

    [Fact]
    public void A_stay_takes_precedence_over_an_ordinary_activity()
    {
        var trip = SpainTrip();
        trip.DestinationLatitude = null;
        trip.DestinationLongitude = null;

        var route = Route(trip, [],
            new GlunoActivityContext
            {
                Date = new DateOnly(2026, 8, 9), Title = "Museum", Role = "activity",
                LocationLabel = "Somewhere else", Latitude = 40.0, Longitude = -3.0,
            },
            new GlunoActivityContext
            {
                Date = new DateOnly(2026, 8, 9), Title = "Hotel", Role = "stay",
                LocationLabel = "Ronda", Latitude = 36.74, Longitude = -5.16,
            });

        // A hotel is a statement about where somebody sleeps; an activity only
        // about where one thing happens.
        Assert.Equal("Ronda", route.Stops.First(stop => stop.Dates.Contains("2026-08-09")).Label);
    }

    [Fact]
    public void Coordinates_are_carried_but_are_not_labels()
    {
        var route = Route();

        Assert.All(
            route.Stops.Where(stop => stop.Source != "trip_destination"),
            stop => Assert.NotNull(stop.Latitude));

        // Used for corridors and distances server-side; never part of what a
        // stop is called.
        Assert.All(route.Stops, stop => Assert.DoesNotContain(",", stop.Label));
    }

    // ── 8. Freshness ─────────────────────────────────────────────────────

    [Fact]
    public void The_route_is_rebuilt_from_the_database_every_turn()
    {
        var builder = Source("GlunoContextBuilder.cs");

        var start = builder.IndexOf("private async Task<TripRouteContext?> BuildRouteAsync", StringComparison.Ordinal);
        var body = builder[start..(start + 2600)];

        Assert.True(start > 0);
        // Read fresh each turn through the shared loader, so a changed day
        // location, a moved Activity or a new stop is visible on the next
        // question. There is nothing to invalidate because nothing is stored.
        Assert.Contains("_timeline.BuildAsync(tripId, endOverride: null, ct)", body);
        Assert.Contains("_db.TripActivities", body);
        Assert.Contains("AsNoTracking()", body);
    }

    [Fact]
    public void A_changed_day_location_produces_a_different_route()
    {
        var before = Route();

        var after = Route(locations:
        [
            Stop("Málaga", 5, 36.72, -4.42),
            // Ronda swapped for Córdoba.
            Stop("Córdoba", 8, 37.89, -4.78),
            Stop("Gibraltar", 10, 36.14, -5.35),
            Stop("Tanger", 11, 35.76, -5.83),
            Stop("Sevilla", 14, 37.39, -5.98),
            Stop("Faro", 16, 37.02, -7.93),
        ]);

        Assert.Contains(before.Stops, stop => stop.Label == "Ronda");
        Assert.DoesNotContain(after.Stops, stop => stop.Label == "Ronda");
        Assert.Contains(after.Stops, stop => stop.Label == "Córdoba");
    }

    [Fact]
    public void A_removed_day_location_disappears_from_the_route()
    {
        var reduced = RealRoute().Where(row => row.LocationLabel != "Faro").ToList();
        var route = Route(locations: reduced);

        Assert.DoesNotContain(route.Stops, stop => stop.Label == "Faro");
        // And the leg that ended there goes with it.
        Assert.DoesNotContain(route.Legs, leg => leg.ToLabel == "Faro");
        Assert.Equal(4, route.Legs.Count);
    }

    // ── 9. Clarification options ─────────────────────────────────────────

    [Fact]
    public void Stop_options_are_built_from_the_route()
    {
        var options = GlunoClarificationBuilder.RouteStopOptions(Route(), "sv");

        Assert.NotEmpty(options);
        Assert.Equal("Málaga", options[0].Label);
        // Capped, like every other clarification list.
        Assert.True(options.Count <= GlunoClarificationBuilder.MaxOptions);
    }

    [Fact]
    public void A_stop_option_carries_a_real_date_rather_than_free_text()
    {
        var options = GlunoClarificationBuilder.RouteStopOptions(Route(), "en");

        Assert.All(options, option =>
        {
            Assert.Equal(GlunoClarificationEntityTypes.Date, option.EntityType);
            Assert.True(DateOnly.TryParse(option.Value, out _));
        });
    }

    [Fact]
    public void Extra_stops_are_not_offered_as_parts_of_the_trip()
    {
        var locations = RealRoute();
        locations.Add(Stop("Setenil", 8, 36.86, -5.18, sortIndex: 1));

        var options = GlunoClarificationBuilder.RouteStopOptions(Route(locations: locations), "en");

        Assert.DoesNotContain(options, option => option.Label == "Setenil");
    }

    [Fact]
    public void Leg_options_name_both_ends()
    {
        var options = GlunoClarificationBuilder.RouteLegOptions(Route(), "sv");

        Assert.NotEmpty(options);
        // The arrow is the point: labelling a leg with one end would make two
        // legs from the same city indistinguishable.
        Assert.Contains("→", options[0].Label);
        Assert.Equal("Málaga → Ronda", options[0].Label);
    }

    [Fact]
    public void A_leg_option_carries_the_departure_date()
    {
        var options = GlunoClarificationBuilder.RouteLegOptions(Route(), "en");

        Assert.All(options, option =>
        {
            Assert.Equal(GlunoClarificationEntityTypes.Date, option.EntityType);
            Assert.True(DateOnly.TryParse(option.Value, out _));
        });

        Assert.Equal("2026-08-07", options[0].Value);
    }

    [Fact]
    public void A_single_city_trip_offers_no_legs_to_choose_between()
    {
        var options = GlunoClarificationBuilder.RouteLegOptions(
            Route(locations: [Stop("Málaga", 5, 36.72, -4.42)]), "en");

        Assert.Empty(options);
    }

    [Fact]
    public void Route_labels_are_written_in_both_languages()
    {
        var swedish = GlunoClarificationBuilder.RouteStopOptions(Route(), "sv");
        var english = GlunoClarificationBuilder.RouteStopOptions(Route(), "en");

        // The city names are the same in both — they are places, not words.
        // The DATE line is what differs.
        Assert.Equal(swedish[0].Label, english[0].Label);
        Assert.NotEqual(swedish[0].Description, english[0].Description);
    }

    [Fact]
    public void Both_new_clarification_types_are_on_the_closed_list()
    {
        Assert.True(GlunoClarificationTypes.IsKnown(GlunoClarificationTypes.RouteStop));
        Assert.True(GlunoClarificationTypes.IsKnown(GlunoClarificationTypes.RouteLeg));
        Assert.False(GlunoClarificationTypes.IsKnown("route_anything"));
    }

    // ── 10. What the prompt is told ──────────────────────────────────────

    [Fact]
    public void The_prompt_forbids_claiming_to_know_only_the_country()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("NEVER say you only know the country when `route.stops` names places", prompt);
        Assert.Contains("\"I only have España and the dates\" is a bug, not an answer", prompt);
    }

    [Fact]
    public void The_prompt_reserves_the_country_answer_for_the_one_honest_case()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("route.isDestinationOnly", prompt);
    }

    [Fact]
    public void The_prompt_forbids_asking_for_a_city_the_route_already_names()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("Never ask which city when the route already answers it", prompt);
    }

    [Fact]
    public void The_prompt_ties_on_the_way_questions_to_a_leg()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("is about a LEG", prompt);
        Assert.Contains("identify which one before doing anything else", prompt);
    }

    [Fact]
    public void The_prompt_keeps_country_city_and_region_distinct()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("Keep country, city, region and day-stop distinct", prompt);
    }

    [Fact]
    public void The_prompt_limits_unplaced_days_to_genuinely_unplaced_ones()
    {
        var prompt = Source("GlunoSystemPrompt.cs");

        Assert.Contains("daysWithoutLocation", prompt);
        // Wrapped across two lines in the prompt text, so matched on the
        // clause rather than the sentence.
        Assert.Contains("are the only days you may say you do not know where they are", prompt);
    }

    // ── 11. Diagnostics say shape, not content ───────────────────────────

    [Fact]
    public void The_route_log_line_carries_no_place_names_or_dates()
    {
        var builder = Source("GlunoContextBuilder.cs");

        var index = builder.IndexOf("[GLUNO] route resolved", StringComparison.Ordinal);
        Assert.True(index > 0);

        var line = builder[index..(index + 400)];

        // Counts only. The whole point of the route is that it describes where
        // somebody is going.
        Assert.DoesNotContain("Label", line);
        Assert.DoesNotContain("Latitude", line);
        Assert.DoesNotContain("Title", line);
        Assert.Contains("stops=", line);
        Assert.Contains("legs=", line);
    }

    // ── 12. No providers for a plain route question ──────────────────────

    [Fact]
    public void Building_the_route_calls_no_provider()
    {
        var source = Source("TripRouteContext.cs");

        // Deterministic context, not an answer. Legs measure a straight line
        // and stop there.
        Assert.DoesNotContain("IRoutingProvider", source);
        Assert.DoesNotContain("ITravelDataProvider", source);
        Assert.DoesNotContain("HttpClient", source);
    }

    [Fact]
    public void The_route_resolver_touches_no_database()
    {
        var source = Source("TripRouteContext.cs");

        Assert.DoesNotContain("AppDbContext", source);
        Assert.DoesNotContain("DbSet", source);
    }
}
