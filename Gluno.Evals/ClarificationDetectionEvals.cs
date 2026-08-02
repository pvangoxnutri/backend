using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for WHEN Gluno stops to ask.
///
/// The builder decides what the options are; this decides whether there is a
/// question at all. That second judgement is the harder one, and it fails in
/// both directions.
///
/// Ask too little and Gluno guesses — the wrong Friday on a two-week trip, the
/// wrong museum when there are two. Ask too much and every question grows a
/// chooser in front of it, including the ones whose answer was already sitting
/// in the data. The second is easier to cause and worse to live with: a chat
/// that asks before it answers stops being faster than the screens it replaced.
///
/// So almost every case below is a pair — one that must resolve silently, one
/// that must ask.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ClarificationDetectionEvals
{
    private static readonly DateOnly Today = new(2026, 8, 12);

    private static GlunoDetectionInput Input(
        string message,
        GlunoIntent intent = GlunoIntent.ImproveExistingDay,
        GlunoTripContext? trip = null,
        GlunoWorkflow? workflow = null,
        params (string Key, string Value)[] preferences)
        => new()
        {
            Message = message,
            Intent = new GlunoIntentResult
            {
                PrimaryIntent = intent,
                Confidence = 0.9,
                Scope = trip == null ? GlunoIntentScope.Global : GlunoIntentScope.Trip,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = false,
            },
            Context = new GlunoContext
            {
                Today = Today,
                User = new GlunoUserContext { Language = "sv" },
                Trip = trip,
                Preferences = preferences
                    .Select(pair => new GlunoPreferenceContext { Key = pair.Key, Value = pair.Value })
                    .ToList(),
            },
            Workflow = workflow ?? GlunoPlanningStrategy.For(
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
                hasTrip: trip != null,
                canEdit: true),
            Today = Today,
            Language = "sv",
        };

    /// A trip from the 10th to the 22nd — long enough to contain two Fridays.
    private static GlunoTripContext TwoWeekTrip(
        IEnumerable<GlunoActivityContext>? activities = null,
        params string[] stops)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = "Andalusien",
            StartDate = new DateOnly(2026, 8, 10),
            EffectiveEndDate = new DateOnly(2026, 8, 22),
            Activities = activities?.ToList() ?? [],
            Destinations = new TripDestinationSummary
            {
                Title = "Andalusien",
                StartDate = "2026-08-10",
                EndDate = "2026-08-22",
                Stops = stops.Select((stop, index) => new TripStop
                {
                    Label = stop,
                    From = new DateOnly(2026, 8, 10).AddDays(index * 3).ToString("yyyy-MM-dd"),
                    To = new DateOnly(2026, 8, 12).AddDays(index * 3).ToString("yyyy-MM-dd"),
                    Source = "day_location",
                }).ToList(),
            },
        };

    private static GlunoActivityContext Activity(string title, int day) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Date = new DateOnly(2026, 8, day),
    };

    // ── Day ──────────────────────────────────────────────────────────────

    [Fact]
    public void Two_Fridays_produce_a_date_choice()
    {
        var detection = GlunoClarificationDetector.DetectDay(
            Input("Vad gör vi på fredag?", trip: TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(GlunoClarificationTypes.Day, detection.Type);
        Assert.Equal(2, detection.Options.Count);
    }

    [Fact]
    public void One_Friday_resolves_without_asking()
    {
        var trip = TwoWeekTrip();
        var shortTrip = trip with { EffectiveEndDate = new DateOnly(2026, 8, 16) };

        var detection = GlunoClarificationDetector.DetectDay(
            Input("Vad gör vi på fredag?", trip: shortTrip));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("2026-08-14", detection.ResolvedValue);
    }

    [Fact]
    public void Tomorrow_resolves_against_today()
    {
        var detection = GlunoClarificationDetector.DetectDay(
            Input("Flytta den till imorgon", trip: TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("2026-08-13", detection.ResolvedValue);
    }

    [Fact]
    public void A_swedish_definite_weekday_is_still_recognised()
    {
        // "fredagen" — the definite form. Whole-word matching finds none of
        // these once the article is attached.
        var detection = GlunoClarificationDetector.DetectDay(
            Input("Vad gör vi på fredagen?", trip: TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
    }

    [Fact]
    public void An_explicit_date_is_never_questioned()
    {
        var detection = GlunoClarificationDetector.DetectDay(
            Input("Vad gör vi 2026-08-14?", trip: TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.NotApplicable, detection.Outcome);
    }

    [Fact]
    public void A_weekday_the_trip_never_reaches_is_not_a_choice()
    {
        var trip = TwoWeekTrip() with
        {
            StartDate = new DateOnly(2026, 8, 10),
            EffectiveEndDate = new DateOnly(2026, 8, 11),
        };

        // Monday and Tuesday only. Asking "which Friday" would be absurd.
        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectDay(Input("på fredag?", trip: trip)).Outcome);
    }

    // ── Activity ─────────────────────────────────────────────────────────

    [Fact]
    public void Two_Activities_with_the_same_word_produce_a_choice()
    {
        var trip = TwoWeekTrip([Activity("Picasso Museum", 11), Activity("Museum of Málaga", 13)]);

        var detection = GlunoClarificationDetector.DetectActivity(
            Input("Flytta museum till torsdag", GlunoIntent.MoveActivity, trip));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(2, detection.Options.Count);
    }

    [Fact]
    public void A_uniquely_named_Activity_resolves_without_asking()
    {
        var trip = TwoWeekTrip([Activity("Picasso Museum", 11), Activity("Flamenco kväll", 13)]);

        var detection = GlunoClarificationDetector.DetectActivity(
            Input("Flytta flamenco till torsdag", GlunoIntent.MoveActivity, trip));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
    }

    [Fact]
    public void A_short_token_does_not_match_an_Activity()
    {
        // "bar" inside "Barcelona" would make every trip's activity a match.
        var trip = TwoWeekTrip([Activity("Bar", 11)]);

        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectActivity(
                Input("Vad gör vi i Barcelona?", GlunoIntent.MoveActivity, trip)).Outcome);
    }

    [Fact]
    public void An_Activity_choice_is_only_offered_when_the_turn_acts_on_one()
    {
        var trip = TwoWeekTrip([Activity("Picasso Museum", 11), Activity("Museum of Málaga", 13)]);

        // A general question mentioning a museum does not need one pinned down.
        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectActivity(
                Input("Är museum kul?", GlunoIntent.GeneralTravelQuestion, trip)).Outcome);
    }

    // ── Place ────────────────────────────────────────────────────────────

    [Fact]
    public void Several_stops_and_a_vague_there_produce_a_place_choice()
    {
        var trip = TwoWeekTrip(null, "Málaga", "Ronda", "Sevilla");

        var detection = GlunoClarificationDetector.DetectPlace(
            Input("Vad kan vi göra där?", trip: trip));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(3, detection.Options.Count);
    }

    [Fact]
    public void A_named_stop_resolves_the_vague_reference()
    {
        var trip = TwoWeekTrip(null, "Málaga", "Ronda", "Sevilla");

        var detection = GlunoClarificationDetector.DetectPlace(
            Input("Vad kan vi göra där i Ronda?", trip: trip));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("Ronda", detection.ResolvedValue);
    }

    [Fact]
    public void A_single_stop_trip_never_asks_where()
    {
        var trip = TwoWeekTrip(null, "Málaga");

        var detection = GlunoClarificationDetector.DetectPlace(
            Input("Vad kan vi göra där?", trip: trip));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
    }

    [Fact]
    public void A_question_with_no_vague_reference_is_left_alone()
    {
        var trip = TwoWeekTrip(null, "Málaga", "Ronda");

        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectPlace(
                Input("Vilket väder blir det?", trip: trip)).Outcome);
    }

    // ── Transport ────────────────────────────────────────────────────────

    [Fact]
    public void A_stated_mode_is_never_asked_about()
    {
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.PlanEmptyDay), hasTrip: true, canEdit: true);

        foreach (var (message, expected) in new[]
        {
            ("Vi ska köra dit", "car"),
            ("Vi vill gå", "walking"),
            ("Vi tar tåget", "public_transport"),
        })
        {
            var detection = GlunoClarificationDetector.DetectTransport(
                Input(message, GlunoIntent.PlanEmptyDay, TwoWeekTrip(), workflow));

            Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
            Assert.Equal(expected, detection.ResolvedValue);
        }
    }

    [Fact]
    public void A_saved_transport_preference_answers_without_asking()
    {
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.PlanEmptyDay), hasTrip: true, canEdit: true);

        var detection = GlunoClarificationDetector.DetectTransport(
            Input("Planera dagen", GlunoIntent.PlanEmptyDay, TwoWeekTrip(), workflow,
                (GlunoPreferenceKeys.Transport, "walking")));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("saved_preference", detection.Reason);
    }

    [Fact]
    public void Transport_is_never_asked_when_routing_will_not_run()
    {
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.GeneralTravelQuestion), hasTrip: true, canEdit: true);

        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectTransport(
                Input("Berätta om Spanien", GlunoIntent.GeneralTravelQuestion, TwoWeekTrip(), workflow))
                .Outcome);
    }

    // ── Pace ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_saved_pace_is_used_rather_than_asked_for()
    {
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.PlanEmptyDay), hasTrip: true, canEdit: true);

        var detection = GlunoClarificationDetector.DetectPace(
            Input("Planera fredagen", GlunoIntent.PlanEmptyDay, TwoWeekTrip(), workflow,
                (GlunoPreferenceKeys.Pace, "relaxed")));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal("relaxed", detection.ResolvedValue);
    }

    [Fact]
    public void Pace_is_never_asked_on_a_factual_question()
    {
        // It changes how many stops fit in a day. It changes nothing about
        // when the museum opens.
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.GeneralTravelQuestion), hasTrip: true, canEdit: true);

        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectPace(
                Input("När öppnar museet?", GlunoIntent.GeneralTravelQuestion, TwoWeekTrip(), workflow))
                .Outcome);
    }

    // ── Budget ───────────────────────────────────────────────────────────

    [Fact]
    public void An_explicit_price_level_is_never_questioned()
    {
        foreach (var message in new[]
        {
            "Hitta en billig restaurang",
            "Nåt exklusivt",
            "Något under 500 kr",
        })
        {
            Assert.Equal(
                GlunoDetectionOutcome.NotApplicable,
                GlunoClarificationDetector.DetectBudget(
                    Input(message, GlunoIntent.PlaceRecommendation, TwoWeekTrip())).Outcome);
        }
    }

    [Fact]
    public void A_recommendation_with_no_budget_signal_asks()
    {
        var detection = GlunoClarificationDetector.DetectBudget(
            Input("Hitta en restaurang", GlunoIntent.PlaceRecommendation, TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(3, detection.Options.Count);
    }

    [Fact]
    public void A_saved_budget_answers_without_asking()
    {
        var detection = GlunoClarificationDetector.DetectBudget(
            Input("Hitta en restaurang", GlunoIntent.PlaceRecommendation, TwoWeekTrip(),
                preferences: (GlunoPreferenceKeys.Budget, "moderate")));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
    }

    // ── Preference scope ─────────────────────────────────────────────────

    [Fact]
    public void A_preference_with_no_stated_scope_asks_before_storing_anything()
    {
        var detection = GlunoClarificationDetector.DetectPreferenceScope(
            Input("Kom ihåg att jag föredrar lugna dagar", GlunoIntent.PreferenceUpdate, TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        // Every offered scope is a real one.
        foreach (var option in detection.Options)
        {
            Assert.True(GlunoPreferenceScopes.IsKnown(option.Value));
        }
    }

    [Fact]
    public void Always_means_global_without_asking()
    {
        var detection = GlunoClarificationDetector.DetectPreferenceScope(
            Input("Jag vill alltid undvika turistfällor", GlunoIntent.PreferenceUpdate, TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal(GlunoPreferenceScopes.Global, detection.ResolvedValue);
    }

    [Fact]
    public void On_this_trip_means_the_Adventure_without_asking()
    {
        var detection = GlunoClarificationDetector.DetectPreferenceScope(
            Input("Bara på den här resan", GlunoIntent.PreferenceUpdate, TwoWeekTrip()));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal(GlunoPreferenceScopes.Trip, detection.ResolvedValue);
    }

    [Fact]
    public void A_trip_scope_is_not_offered_without_an_Adventure()
    {
        var detection = GlunoClarificationDetector.DetectPreferenceScope(
            Input("Kom ihåg att jag gillar lugna dagar", GlunoIntent.PreferenceUpdate));

        Assert.DoesNotContain(detection.Options, option => option.Value == GlunoPreferenceScopes.Trip);
    }

    // ── Not asking ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(GlunoIntent.SideQuestHelp, "Hur fungerar Gluno?")]
    [InlineData(GlunoIntent.NavigationRequest, "Öppna packlistan")]
    [InlineData(GlunoIntent.ForgetPreference, "Glöm det där")]
    public void A_question_a_choice_cannot_change_is_never_interrupted(GlunoIntent intent, string message)
    {
        var detection = GlunoClarificationDetector.Detect(
            Input(message, intent, TwoWeekTrip(null, "Málaga", "Ronda")));

        Assert.Equal(GlunoDetectionOutcome.NotApplicable, detection.Outcome);
    }

    [Fact]
    public void A_request_that_already_answers_everything_produces_no_card()
    {
        // An explicit date, a stated mode, and a saved pace. There is nothing
        // left to ask, and asking anyway is the failure mode that turns this
        // feature into a form.
        var detection = GlunoClarificationDetector.Detect(
            Input("Planera 2026-08-14, vi vill gå",
                GlunoIntent.PlanEmptyDay,
                TwoWeekTrip(null, "Málaga"),
                preferences: (GlunoPreferenceKeys.Pace, "relaxed")));

        Assert.NotEqual(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
    }

    [Fact]
    public void A_global_turn_with_no_Adventure_is_not_asked_about_days_or_places()
    {
        // Nothing to build options from. Asking would offer an empty card.
        var detection = GlunoClarificationDetector.Detect(
            Input("Vad gör vi på fredag?", GlunoIntent.GeneralTravelQuestion));

        Assert.NotEqual(GlunoClarificationTypes.Day, detection.Type);
        Assert.NotEqual(GlunoClarificationTypes.Place, detection.Type);
    }

    // ── The pipeline ─────────────────────────────────────────────────────

    [Fact]
    public void The_detector_returns_the_first_thing_that_needs_answering()
    {
        // A day and a place are both ambiguous here. One question, not two —
        // asking twice in a row turns a chat into a wizard.
        var trip = TwoWeekTrip(null, "Málaga", "Ronda", "Sevilla");

        var detection = GlunoClarificationDetector.Detect(
            Input("Vad gör vi där på fredag?", GlunoIntent.ImproveExistingDay, trip));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(GlunoClarificationTypes.Day, detection.Type);
    }

    [Fact]
    public void Every_asking_detection_carries_options_and_a_reason()
    {
        var trip = TwoWeekTrip(null, "Málaga", "Ronda");

        foreach (var detection in new[]
        {
            GlunoClarificationDetector.DetectDay(Input("på fredag?", trip: trip)),
            GlunoClarificationDetector.DetectPlace(Input("vad gör vi där?", trip: trip)),
            GlunoClarificationDetector.DetectBudget(
                Input("hitta restaurang", GlunoIntent.PlaceRecommendation, trip)),
        })
        {
            if (detection.Outcome != GlunoDetectionOutcome.NeedsClarification) continue;

            Assert.NotEmpty(detection.Options);
            Assert.False(string.IsNullOrWhiteSpace(detection.Reason));
            Assert.True(GlunoClarificationTypes.IsKnown(detection.Type));
        }
    }

    [Fact]
    public void No_detection_ever_produces_more_options_than_fit()
    {
        var trip = TwoWeekTrip(null, "A", "B", "C", "D", "E", "F", "G");

        var detection = GlunoClarificationDetector.DetectPlace(
            Input("Vad gör vi där?", trip: trip));

        Assert.True(detection.Options.Count <= GlunoClarificationBuilder.MaxOptions);
    }

    private static GlunoIntentResult Intent(GlunoIntent intent) => new()
    {
        PrimaryIntent = intent,
        Confidence = 0.9,
        Scope = GlunoIntentScope.Trip,
        RequiresCurrentData = false,
        RequiresExternalSearch = false,
        ExpectsProposal = false,
        RequiresClarification = false,
    };
}
