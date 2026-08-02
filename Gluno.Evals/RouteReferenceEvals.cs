using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for working out which stop, leg or day a question is about.
///
/// WHY THIS IS CODE AND NOT PROMPT. "What's after Málaga" has exactly one
/// answer given the route, and it is an index lookup. A model asked to work it
/// out will usually get it right and occasionally answer about Ronda when the
/// trip goes Málaga → Sevilla — and from the outside those two cases are
/// indistinguishable. Resolving here makes the relationship a fact the model is
/// told rather than one it infers.
///
/// THE MATCHING BUG THIS FILE GUARDS. "Venice" contains "nice"; "Rondavägen"
/// contains "Ronda"; Swedish inflects everything. That class of bug has been
/// reintroduced four times in this codebase, and a resolver over place names is
/// the most likely place for a fifth.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class RouteReferenceEvals
{
    private static readonly DateOnly Today = new(2026, 8, 6);

    private static Trip SpainTrip() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Semester 2026",
        Destination = "España",
        DestinationLatitude = 40.42,
        DestinationLongitude = -3.70,
        StartDate = new DateOnly(2026, 8, 5),
        EndDate = new DateOnly(2026, 8, 16),
    };

    private static TripDayLocation Stop(string label, int day, double lat, double lon, int sortIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        StartDate = new DateOnly(2026, 8, day),
        SortIndex = sortIndex,
        LocationLabel = label,
        Latitude = lat,
        Longitude = lon,
    };

    private static TripRouteContext Route(params GlunoActivityContext[] activities)
        => TripRouteResolver.Build(SpainTrip(),
        [
            Stop("Málaga", 5, 36.72, -4.42),
            Stop("Ronda", 8, 36.74, -5.16),
            Stop("Gibraltar", 10, 36.14, -5.35),
            Stop("Tanger", 11, 35.76, -5.83),
            Stop("Sevilla", 14, 37.39, -5.98),
            Stop("Faro", 16, 37.02, -7.93),
        ], activities);

    private static GlunoRouteResolution Ask(string message, string? lastStop = null)
        => GlunoRouteReferenceResolver.Resolve(message, Route(), Today, lastStop);

    // ── Named stops ──────────────────────────────────────────────────────

    [Fact]
    public void A_named_city_resolves_to_its_stop()
    {
        var result = Ask("Vad kan vi göra i Ronda?");

        Assert.Equal("Ronda", result.Stop?.Label);
        Assert.Equal(GlunoRouteMatch.Named, result.Match);
        Assert.False(result.NeedsClarification);
    }

    [Fact]
    public void An_accent_is_not_required_to_name_a_city()
    {
        // Nobody reaches for the accent on a phone keyboard, and a resolver
        // that needs one fails silently.
        Assert.Equal("Málaga", Ask("what about malaga?").Stop?.Label);
        Assert.Equal("Málaga", Ask("Vad gör vi i MÁLAGA?").Stop?.Label);
    }

    [Fact]
    public void A_city_name_inside_a_longer_word_does_not_match()
    {
        // "Rondavägen" is a street, not the town. This is the bug class that
        // keeps coming back.
        var result = Ask("Vi bor på Rondavägen i Stockholm");

        Assert.NotEqual("Ronda", result.Stop?.Label);
    }

    [Fact]
    public void Swedish_inflection_still_matches_the_city()
    {
        // A bounded suffix allows "Rondas" without allowing "Rondavägen".
        Assert.Equal("Ronda", Ask("Vad är Rondas bästa utsikt?").Stop?.Label);
    }

    // ── Relations ────────────────────────────────────────────────────────

    [Fact]
    public void After_a_city_resolves_to_the_next_stop()
    {
        var result = Ask("Vad gör vi efter Málaga?");

        Assert.Equal("Ronda", result.Stop?.Label);
        Assert.Equal(GlunoRouteMatch.Relative, result.Match);
        Assert.Equal("after_stop", result.Reason);
    }

    [Fact]
    public void Before_a_city_resolves_to_the_previous_stop()
    {
        var result = Ask("What do we do before Sevilla?");

        Assert.Equal("Tanger", result.Stop?.Label);
        Assert.Equal("before_stop", result.Reason);
    }

    [Fact]
    public void A_relation_word_far_from_the_city_is_not_a_relation()
    {
        // "We ate in Málaga after the museum" is about Málaga, not about what
        // follows it. A whole-sentence check cannot tell those apart.
        var result = Ask("Vi åt i Málaga efter museet och det var bra");

        Assert.Equal("Málaga", result.Stop?.Label);
        Assert.Equal(GlunoRouteMatch.Named, result.Match);
    }

    [Fact]
    public void After_the_last_stop_resolves_to_nothing_rather_than_wrapping()
    {
        var result = Ask("Vad gör vi efter Faro?");

        // There is no stop after the last one. Wrapping to the first would be
        // an answer about the wrong end of the holiday.
        Assert.Equal("Faro", result.Stop?.Label);
        Assert.Equal(GlunoRouteMatch.Named, result.Match);
    }

    [Fact]
    public void First_and_last_stop_resolve_by_ordinal()
    {
        Assert.Equal("Málaga", Ask("Vilken är första staden?").Stop?.Label);
        Assert.Equal("Faro", Ask("What is the last stop?").Stop?.Label);
    }

    [Fact]
    public void Next_city_counts_from_where_the_trip_is_today()
    {
        // Today is 6 August, which is inside the Málaga run.
        var result = Ask("Vilken är nästa stad?");

        Assert.Equal("Ronda", result.Stop?.Label);
        Assert.Equal("next_stop", result.Reason);
    }

    [Fact]
    public void Next_city_counts_from_the_last_thing_discussed_when_there_is_one()
    {
        var result = Ask("Vilken är nästa stad?", lastStop: "2026-08-11");

        // Last discussed was Tanger, so next is Sevilla — not Ronda.
        Assert.Equal("Sevilla", result.Stop?.Label);
    }

    [Fact]
    public void A_bare_relation_word_with_no_stop_word_resolves_to_nothing()
    {
        // "Next" in an unrelated sentence must not become a city.
        var result = Ask("Vad händer sen då?");

        Assert.Null(result.Stop);
    }

    // ── Dates ────────────────────────────────────────────────────────────

    [Fact]
    public void A_written_date_resolves_to_the_stop_covering_it()
    {
        var result = Ask("Vilken stad är vi i den 9 augusti?");

        Assert.Equal("Ronda", result.Stop?.Label);
        Assert.Equal("2026-08-09", result.Date);
        Assert.Equal(GlunoRouteMatch.ByDate, result.Match);
    }

    [Fact]
    public void An_ISO_date_resolves_too()
    {
        Assert.Equal("Tanger", Ask("what about 2026-08-12?").Stop?.Label);
    }

    [Fact]
    public void An_English_date_in_either_order_resolves()
    {
        Assert.Equal("Sevilla", Ask("what's on August 15?").Stop?.Label);
        Assert.Equal("Sevilla", Ask("what's on 15 August?").Stop?.Label);
    }

    [Fact]
    public void A_date_outside_the_trip_resolves_to_nothing()
    {
        // 20 August is not a trip day. Picking the nearest stop would answer
        // about a day they did not ask about.
        var result = Ask("Vad gör vi den 20 augusti?");

        Assert.Null(result.Stop);
    }

    [Fact]
    public void An_impossible_date_does_not_throw()
    {
        Assert.Null(Ask("den 31 februari").Stop);
        Assert.Null(Ask("den 99 augusti").Stop);
    }

    // ── Legs ─────────────────────────────────────────────────────────────

    [Fact]
    public void Between_two_cities_resolves_to_the_leg()
    {
        var result = Ask("Finns det något sevärt mellan Málaga och Ronda?");

        Assert.NotNull(result.Leg);
        Assert.Equal("Málaga", result.Leg!.FromLabel);
        Assert.Equal("Ronda", result.Leg.ToLabel);
        // And NOT to either endpoint — a question about the space between them
        // is not a question about either.
        Assert.Null(result.Stop);
    }

    [Fact]
    public void On_the_way_to_a_city_resolves_the_leg_that_arrives_there()
    {
        var result = Ask("Vad kan vi stanna vid på vägen till Gibraltar?");

        Assert.NotNull(result.Leg);
        Assert.Equal("Ronda", result.Leg!.FromLabel);
        Assert.Equal("Gibraltar", result.Leg.ToLabel);
    }

    [Fact]
    public void On_the_way_from_a_city_resolves_the_leg_that_leaves_it()
    {
        var result = Ask("anything on the way from Tanger?");

        Assert.Equal("Tanger", result.Leg?.FromLabel);
        Assert.Equal("Sevilla", result.Leg?.ToLabel);
    }

    [Fact]
    public void A_journey_question_with_no_journey_named_asks()
    {
        var result = Ask("Finns det något sevärt på vägen?");

        Assert.True(result.NeedsClarification);
        Assert.Equal(GlunoClarificationTypes.RouteLeg, result.ClarificationType);
        Assert.Equal(5, result.LegCandidates.Count);
    }

    [Fact]
    public void A_single_leg_trip_never_asks_which_leg()
    {
        var route = TripRouteResolver.Build(SpainTrip(),
            [Stop("Málaga", 5, 36.72, -4.42), Stop("Ronda", 10, 36.74, -5.16)],
            []);

        var result = GlunoRouteReferenceResolver.Resolve("något på vägen?", route, Today);

        // One leg is not a choice.
        Assert.False(result.NeedsClarification);
    }

    // ── Vague references ─────────────────────────────────────────────────

    [Fact]
    public void There_resolves_to_the_last_stop_discussed()
    {
        var result = Ask("Vad kan vi göra där?", lastStop: "2026-08-08");

        Assert.Equal("Ronda", result.Stop?.Label);
        Assert.Equal(GlunoRouteMatch.Carried, result.Match);
    }

    [Fact]
    public void There_with_nothing_discussed_resolves_to_nothing()
    {
        // Better to ask than to pick a city because it happens to be first.
        Assert.Null(Ask("Vad kan vi göra där?").Stop);
    }

    // ── Two cities, no relation ──────────────────────────────────────────

    [Fact]
    public void Two_named_cities_with_no_relation_is_a_real_choice()
    {
        var result = Ask("Ronda och Sevilla, vad tycker du?");

        Assert.True(result.NeedsClarification);
        Assert.Equal(GlunoClarificationTypes.RouteStop, result.ClarificationType);
        Assert.Equal(2, result.Candidates.Count);
    }

    // ── The whole route ──────────────────────────────────────────────────

    [Fact]
    public void Analysing_the_route_is_not_a_question_about_one_city()
    {
        foreach (var message in new[]
        {
            "Analysera vår rutt",
            "Hur ser hela resan ut?",
            "Är rutten rimlig?",
            "analyse the whole trip",
        })
        {
            var result = GlunoRouteReferenceResolver.Resolve(message, Route(), Today);

            Assert.Equal("whole_route", result.Reason);
            // Crucially: no city chooser. Asking which city to analyse the
            // route of answers a different question.
            Assert.False(result.NeedsClarification);
            Assert.Null(result.Stop);
        }
    }

    // ── The detector ─────────────────────────────────────────────────────

    private static GlunoDetectionInput Input(
        string message, GlunoIntent intent, TripRouteContext? route)
        => new()
        {
            Message = message,
            Intent = new GlunoIntentResult
            {
                PrimaryIntent = intent,
                Confidence = 0.9,
                Scope = GlunoIntentScope.Trip,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = false,
            },
            Context = new GlunoContext { Route = route },
            Workflow = GlunoPlanningStrategy.For(
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
                hasTrip: true, canEdit: true),
            Today = Today,
            Language = "sv",
        };

    [Fact]
    public void A_broad_recommendation_question_asks_which_stop()
    {
        var detection = GlunoClarificationDetector.DetectRouteStop(
            Input("Vad borde vi se?", GlunoIntent.PlaceRecommendation, Route()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(GlunoClarificationTypes.RouteStop, detection.Type);
        Assert.Equal(5, detection.Options.Count);
        Assert.Equal("Málaga", detection.Options[0].Label);
    }

    [Fact]
    public void A_question_that_names_its_city_is_not_asked_about()
    {
        var detection = GlunoClarificationDetector.DetectRouteStop(
            Input("Vad kan vi göra i Ronda?", GlunoIntent.PlaceRecommendation, Route()));

        // Asking here would read as not having listened.
        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("2026-08-08", detection.ResolvedValue);
    }

    [Fact]
    public void A_single_stop_trip_is_never_asked_about()
    {
        var route = TripRouteResolver.Build(SpainTrip(), [Stop("Málaga", 5, 36.72, -4.42)], []);

        var detection = GlunoClarificationDetector.DetectRouteStop(
            Input("Vad borde vi se?", GlunoIntent.PlaceRecommendation, route));

        Assert.Equal(GlunoDetectionOutcome.NotApplicable, detection.Outcome);
    }

    [Fact]
    public void A_whole_route_question_never_produces_a_city_chooser()
    {
        var detection = GlunoClarificationDetector.DetectRouteStop(
            Input("Analysera vår rutt", GlunoIntent.TripReview, Route()));

        Assert.Equal(GlunoDetectionOutcome.NotApplicable, detection.Outcome);
    }

    [Fact]
    public void A_question_that_does_not_need_a_place_is_not_asked_about()
    {
        // "How much have we spent" spans the whole trip. A city chooser in
        // front of it is pure friction.
        var detection = GlunoClarificationDetector.DetectRouteStop(
            Input("Hur mycket har vi spenderat?", GlunoIntent.TripReview, Route()));

        Assert.Equal(GlunoDetectionOutcome.NotApplicable, detection.Outcome);
    }

    [Fact]
    public void An_ambiguous_journey_question_asks_which_leg()
    {
        var detection = GlunoClarificationDetector.DetectRouteLeg(
            Input("Något sevärt på vägen?", GlunoIntent.PlaceRecommendation, Route()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(GlunoClarificationTypes.RouteLeg, detection.Type);
        Assert.Contains("→", detection.Options[0].Label);
    }

    [Fact]
    public void A_named_journey_is_resolved_rather_than_asked_about()
    {
        var detection = GlunoClarificationDetector.DetectRouteLeg(
            Input("Något mellan Málaga och Ronda?", GlunoIntent.PlaceRecommendation, Route()));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("2026-08-07", detection.ResolvedValue);
    }

    [Fact]
    public void The_leg_detector_runs_before_the_stop_detector()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoClarificationDetector.cs"));

        var legAt = source.IndexOf("DetectRouteLeg,", StringComparison.Ordinal);
        var stopAt = source.IndexOf("DetectRouteStop,", StringComparison.Ordinal);
        var dayAt = source.IndexOf("DetectDay,", StringComparison.Ordinal);

        Assert.True(legAt > 0 && stopAt > 0 && dayAt > 0);
        // "On the way" is a journey question, not a city question. And both are
        // asked before the day, because on a multi-city trip the gap is a
        // place rather than a date.
        Assert.True(legAt < stopAt);
        Assert.True(stopAt < dayAt);
    }

    [Fact]
    public void Route_clarification_happens_before_any_provider()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var detectAt = source.IndexOf("GlunoClarificationDetector.Detect(", StringComparison.Ordinal);
        var modelAt = source.IndexOf("RunModelAsync", StringComparison.Ordinal);

        Assert.True(detectAt > 0);
        // Searching first and asking afterwards would spend a provider call on
        // the wrong stretch of road.
        if (modelAt > 0) Assert.True(detectAt < modelAt);
    }

    // ── Freshness ────────────────────────────────────────────────────────

    [Fact]
    public void A_changed_stop_changes_the_fingerprint()
    {
        var before = Route().Fingerprint;

        var after = TripRouteResolver.Build(SpainTrip(),
        [
            Stop("Málaga", 5, 36.72, -4.42),
            // Ronda swapped for Córdoba.
            Stop("Córdoba", 8, 37.89, -4.78),
            Stop("Gibraltar", 10, 36.14, -5.35),
            Stop("Tanger", 11, 35.76, -5.83),
            Stop("Sevilla", 14, 37.39, -5.98),
            Stop("Faro", 16, 37.02, -7.93),
        ], []).Fingerprint;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_moved_date_changes_the_fingerprint()
    {
        var before = Route().Fingerprint;

        var after = TripRouteResolver.Build(SpainTrip(),
        [
            Stop("Málaga", 5, 36.72, -4.42),
            // Ronda now starts a day later.
            Stop("Ronda", 9, 36.74, -5.16),
            Stop("Gibraltar", 10, 36.14, -5.35),
            Stop("Tanger", 11, 35.76, -5.83),
            Stop("Sevilla", 14, 37.39, -5.98),
            Stop("Faro", 16, 37.02, -7.93),
        ], []).Fingerprint;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_refined_coordinate_does_not_change_the_fingerprint()
    {
        var before = Route().Fingerprint;

        var after = TripRouteResolver.Build(SpainTrip(),
        [
            // Same places and dates, geocoder nudged Málaga by ~50 metres.
            Stop("Málaga", 5, 36.7205, -4.4205),
            Stop("Ronda", 8, 36.74, -5.16),
            Stop("Gibraltar", 10, 36.14, -5.35),
            Stop("Tanger", 11, 35.76, -5.83),
            Stop("Sevilla", 14, 37.39, -5.98),
            Stop("Faro", 16, 37.02, -7.93),
        ], []).Fingerprint;

        // Treating that as a route change would make every card stale for no
        // reason.
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_fingerprint_is_stable_for_an_unchanged_route()
    {
        Assert.Equal(Route().Fingerprint, Route().Fingerprint);
    }

    // ── The analyzer ─────────────────────────────────────────────────────

    private static GlunoTripContext TripContext(params GlunoActivityContext[] activities)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = "Semester 2026",
            Destination = "España",
            StartDate = new DateOnly(2026, 8, 5),
            EndDate = new DateOnly(2026, 8, 16),
            EffectiveEndDate = new DateOnly(2026, 8, 16),
            Activities = activities,
        };

    [Fact]
    public void The_analyzer_flags_a_very_short_stay()
    {
        var findings = TripAnalyzer.Analyze(
            TripContext(), TripPace.Balanced, [], Route());

        var shortStays = findings.Where(finding => finding.Type == "very_short_stay").ToList();

        // Gibraltar and Faro are single days.
        Assert.NotEmpty(shortStays);
        Assert.Contains(shortStays, finding => finding.StopLabels.Contains("Gibraltar"));
    }

    [Fact]
    public void The_analyzer_flags_a_change_of_city_with_no_travel_planned()
    {
        var findings = TripAnalyzer.Analyze(
            TripContext(), TripPace.Balanced, [], Route());

        var missing = findings.Where(finding => finding.Type == "leg_without_transport").ToList();

        Assert.NotEmpty(missing);
        // And it names the leg rather than describing it in prose.
        Assert.Contains(missing, finding => finding.LegLabels.Contains("Málaga → Ronda"));
    }

    [Fact]
    public void A_leg_with_transport_is_not_flagged_as_missing_it()
    {
        var ferry = new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 10),
            Title = "Ferry Tarifa–Tanger",
            Time = "09:30",
            Role = "transport",
        };

        var findings = TripAnalyzer.Analyze(
            TripContext(ferry), TripPace.Balanced, [], Route(ferry));

        Assert.DoesNotContain(
            findings.Where(finding => finding.Type == "leg_without_transport"),
            finding => finding.LegLabels.Contains("Gibraltar → Tanger"));
    }

    [Fact]
    public void The_analyzer_flags_an_activity_in_the_wrong_city()
    {
        // Planned in Barcelona on a Ronda day.
        var away = new GlunoActivityContext
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 8, 8),
            Title = "Sagrada Família",
            Role = "activity",
            Latitude = 41.40,
            Longitude = 2.17,
        };

        var findings = TripAnalyzer.Analyze(
            TripContext(away), TripPace.Balanced, [], Route());

        var wrong = findings.FirstOrDefault(finding => finding.Type == "activity_in_another_city");

        Assert.NotNull(wrong);
        Assert.Contains("Ronda", wrong!.StopLabels);
        Assert.Contains(away.Id, wrong.ActivityIds);
    }

    [Fact]
    public void An_activity_without_coordinates_is_never_called_the_wrong_city()
    {
        var vague = new GlunoActivityContext
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 8, 8),
            Title = "Dinner",
            Description = "somewhere in Barcelona maybe",
            Role = "activity",
        };

        var findings = TripAnalyzer.Analyze(
            TripContext(vague), TripPace.Balanced, [], Route());

        // Prose is not a location. Flagging on it would ask somebody to fix a
        // plan that was right.
        Assert.DoesNotContain(findings, finding => finding.Type == "activity_in_another_city");
    }

    [Fact]
    public void The_analyzer_never_guesses_a_country_or_a_border()
    {
        var findings = TripAnalyzer.Analyze(TripContext(), TripPace.Balanced, [], Route());

        // Tanger is in Morocco and Faro is in Portugal, and SideQuest stores
        // neither. Inferring them from the names is the invention the whole
        // route layer avoids.
        Assert.DoesNotContain(findings, finding => finding.Type.Contains("border"));
        Assert.All(Route().Legs, leg => Assert.Null(leg.CrossesBorder));
    }

    [Fact]
    public void Findings_carry_the_stops_and_legs_they_are_about()
    {
        var findings = TripAnalyzer.Analyze(TripContext(), TripPace.Balanced, [], Route());

        var routeFindings = findings
            .Where(finding => finding.StopLabels.Count > 0 || finding.LegLabels.Count > 0)
            .ToList();

        // Not just text. A finding that cannot be pointed at a part of the
        // route is a finding the answer has to re-derive.
        Assert.NotEmpty(routeFindings);
    }

    [Fact]
    public void The_analyzer_still_works_without_a_route()
    {
        // Every existing caller passes three arguments. The route is optional
        // so none of them break.
        var findings = TripAnalyzer.Analyze(TripContext(), TripPace.Balanced, []);

        Assert.DoesNotContain(findings, finding => finding.Type == "very_short_stay");
    }

    [Fact]
    public void A_straight_line_is_labelled_as_one_in_the_facts()
    {
        var busy = Enumerable.Range(0, 3).Select(index => new GlunoActivityContext
        {
            Id = Guid.NewGuid(),
            Date = new DateOnly(2026, 8, 14),
            Title = $"Thing {index}",
            Role = "activity",
        }).ToArray();

        var findings = TripAnalyzer.Analyze(TripContext(busy), TripPace.Balanced, [], Route());

        var afterLongLeg = findings.FirstOrDefault(finding => finding.Type == "busy_day_after_long_leg");

        if (afterLongLeg != null)
        {
            // Named for what it is. A straight line must never read as a
            // driving distance.
            Assert.True(afterLongLeg.Facts.ContainsKey("straightLineKm"));
            Assert.False(afterLongLeg.Facts.ContainsKey("drivingKm"));
        }
    }

    // ── Options ──────────────────────────────────────────────────────────

    [Fact]
    public void A_leg_option_shows_the_planned_transport_when_there_is_any()
    {
        var ferry = new GlunoActivityContext
        {
            Date = new DateOnly(2026, 8, 10),
            Title = "Ferry Tarifa–Tanger",
            Time = "09:30",
            Role = "transport",
        };

        var options = GlunoClarificationBuilder.RouteLegOptions(Route(ferry), "sv");
        var leg = options.First(option => option.Label == "Gibraltar → Tanger");

        Assert.Contains("Ferry Tarifa–Tanger", leg.Description);
    }

    [Fact]
    public void A_leg_option_without_transport_shows_the_date_alone()
    {
        var options = GlunoClarificationBuilder.RouteLegOptions(Route(), "sv");

        // Never a guess at driving.
        Assert.DoesNotContain("·", options[0].Description ?? string.Empty);
    }

    [Fact]
    public void Route_options_carry_no_coordinates()
    {
        foreach (var option in GlunoClarificationBuilder.RouteStopOptions(Route(), "sv")
            .Concat(GlunoClarificationBuilder.RouteLegOptions(Route(), "sv")))
        {
            // Used server-side for corridors; noise on screen and false
            // precision in a sentence.
            Assert.DoesNotContain("36.", option.Value);
            Assert.DoesNotContain("36.", option.Description ?? string.Empty);
        }
    }

    // ── The prompt ───────────────────────────────────────────────────────

    private static string Prompt() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Services", "Gluno", "GlunoSystemPrompt.cs"));

    [Fact]
    public void The_prompt_forbids_calling_a_straight_line_a_detour()
    {
        Assert.Contains("Never call a straight-line distance a detour", Prompt());
    }

    [Fact]
    public void The_prompt_requires_checking_what_is_fixed_after_a_road_stop()
    {
        Assert.Contains("Do not suggest a stop on the way without checking what is fixed", Prompt());
    }

    [Fact]
    public void The_prompt_tells_the_model_the_subject_is_already_resolved()
    {
        Assert.Contains("SideQuest has already worked out which stop, leg or day", Prompt());
        Assert.Contains("do not ask about it again", Prompt());
    }

    [Fact]
    public void The_prompt_protects_whole_route_questions_from_becoming_city_questions()
    {
        Assert.Contains("are about the WHOLE chain", Prompt());
        Assert.Contains("that answers a", Prompt());
    }
}
