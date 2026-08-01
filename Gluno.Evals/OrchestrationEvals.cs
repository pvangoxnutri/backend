using System.Text.Json;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for how Gluno DECIDES: what a message means, what the turn is allowed
/// to spend, what a pronoun points at, and what must never reach the user.
///
/// These are the cheapest bugs to ship and the most expensive to notice. A
/// misrouted turn still produces a fluent answer; "the second one" resolving to
/// the wrong restaurant still produces a confident proposal. Nothing about
/// either failure looks wrong in a transcript, which is exactly why every case
/// below is pinned here rather than left to review.
///
/// Nothing calls a model, a network, or a database.
/// </summary>
public class OrchestrationEvals
{
    private static readonly DateOnly Start = new(2026, 8, 10);   // Monday
    private static readonly DateOnly End = new(2026, 8, 16);     // Sunday
    private static readonly Guid MuseumId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HotelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LunchAId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LunchBId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static GlunoIntentResult Classify(
        string message,
        bool hasTrip = true,
        bool hasRecentContext = false,
        params (string Date, int Count)[] activityCounts)
        => GlunoIntentRouter.Classify(new GlunoIntentInput
        {
            Message = message,
            HasTrip = hasTrip,
            TripStartDate = hasTrip ? Start : null,
            TripEndDate = hasTrip ? End : null,
            Today = Start,
            HasRecentContext = hasRecentContext,
            ActivityCountByDate = activityCounts.ToDictionary(
                entry => entry.Date, entry => entry.Count, StringComparer.Ordinal),
        });

    private static GlunoWorkflow Workflow(GlunoIntentResult intent, bool hasTrip = true, bool canEdit = true)
        => GlunoPlanningStrategy.For(intent, hasTrip, canEdit);

    // ── 1. A simple factual question spends nothing ──────────────────────

    [Fact]
    public void A_plain_travel_question_loads_no_analysis_and_calls_no_provider()
    {
        var intent = Classify("Är Nice varmt i augusti?");
        var workflow = Workflow(intent);

        Assert.Equal(GlunoIntent.GeneralTravelQuestion, intent.PrimaryIntent);
        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsRouting);
        Assert.False(workflow.AllowsProposals);
        Assert.False(workflow.NeedsTripAnalysis);
        Assert.True(workflow.MaxModelRounds <= 2);
    }

    // ── 2. An app question uses only the capability registry ─────────────

    [Fact]
    public void An_app_question_reaches_only_the_capability_actions()
    {
        var intent = Classify("Hur lägger jag till en bild i appen?");
        var workflow = Workflow(intent);

        Assert.Equal(GlunoIntent.SideQuestHelp, intent.PrimaryIntent);
        Assert.False(workflow.NeedsTripContext);
        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsRouting);
        Assert.False(workflow.AllowsProposals);

        var offered = GlunoPlanningStrategy.FilterActions(GlunoActions.All, workflow)
            .Select(action => action.Name)
            .ToList();

        Assert.Contains(GlunoActions.SearchSideQuestFeatures, offered);
        Assert.DoesNotContain(GlunoActions.SearchPlaces, offered);
        Assert.DoesNotContain(GlunoActions.ProposeActivity, offered);
    }

    // ── 3. Planning an empty day runs the full pipeline ──────────────────

    [Fact]
    public void Planning_an_empty_day_gets_the_whole_pipeline()
    {
        var intent = Classify("Planera lördagen åt oss");
        var workflow = Workflow(intent);

        Assert.Equal(GlunoIntent.PlanEmptyDay, intent.PrimaryIntent);
        Assert.Equal("2026-08-15", intent.ReferencedDate);
        Assert.True(workflow.NeedsTripContext);
        Assert.True(workflow.NeedsTripAnalysis);
        Assert.True(workflow.NeedsWeather);
        Assert.True(workflow.AllowsExternalSearch);
        Assert.True(workflow.AllowsRouting);
        Assert.True(workflow.UsesScheduleEngine);
        Assert.True(workflow.AllowsProposals);
        Assert.True(workflow.RunsQualityGate);
    }

    [Fact]
    public void A_day_that_already_has_activities_is_improved_not_replanned()
    {
        var intent = Classify("Planera om fredagen", activityCounts: ("2026-08-14", 3));

        Assert.Equal(GlunoIntent.ImproveExistingDay, intent.PrimaryIntent);
        Assert.Equal("2026-08-14", intent.ReferencedDate);
    }

    // ── 4. "The second one" after three recommendations ──────────────────

    [Fact]
    public void The_second_one_resolves_to_the_second_result_the_user_was_shown()
    {
        var state = StateWithThreeRestaurants();

        var resolution = GlunoReferenceResolver.Resolve("Ta den andra", state, Trip(), "sv");

        Assert.NotNull(resolution.Subject);
        Assert.Equal(GlunoReferenceKind.Place, resolution.Subject!.Kind);
        Assert.Equal("tripadvisor:200", resolution.Subject.Id);
        Assert.False(resolution.IsAmbiguous);
    }

    // ── 5. "Move it after the museum" ────────────────────────────────────

    [Fact]
    public void After_the_museum_resolves_the_anchor_the_relation_and_the_day()
    {
        var state = StateWithThreeRestaurants();
        state.Recent.Activities.Add(new MentionedActivity(
            MuseumId, "Matisse Museum", "2026-08-12", "sight", "activity"));

        var resolution = GlunoReferenceResolver.Resolve(
            "Ta den andra och lägg den efter Matisse Museum", state, Trip(), "sv");

        Assert.Equal("tripadvisor:200", resolution.Subject!.Id);
        Assert.Equal(GlunoRelation.After, resolution.Relation);
        Assert.NotNull(resolution.Anchor);
        Assert.Equal(MuseumId.ToString(), resolution.Anchor!.Id);
        Assert.Equal("2026-08-12", resolution.Date);
    }

    // ── 6. Two Activities with the same name ─────────────────────────────

    [Fact]
    public void Two_activities_with_the_same_name_produce_a_question_not_a_guess()
    {
        var state = new GlunoWorkingState();
        state.Recent.Activities.Add(new MentionedActivity(LunchAId, "Lunch", "2026-08-11", "food", "meal"));
        state.Recent.Activities.Add(new MentionedActivity(LunchBId, "Lunch", "2026-08-12", "food", "meal"));

        var resolution = GlunoReferenceResolver.Resolve("Flytta lunch till senare", state, TripWithTwoLunches(), "sv");

        Assert.True(resolution.IsAmbiguous);
        Assert.Null(resolution.Subject);
        Assert.NotNull(resolution.Question);
        // The question names them rather than asking "which one?".
        Assert.Contains("Lunch", resolution.Question!);
        Assert.Equal(2, resolution.Candidates.Count);
    }

    // ── 7. The referenced Activity was deleted ───────────────────────────

    [Fact]
    public void A_deleted_activity_is_never_reused_as_a_referent()
    {
        var state = new GlunoWorkingState();
        state.Recent.Activities.Add(new MentionedActivity(
            MuseumId, "Matisse Museum", "2026-08-12", "sight", "activity"));

        // The plan no longer contains it.
        var resolution = GlunoReferenceResolver.Resolve("Flytta tillbaka den", state, EmptyTrip(), "sv");

        Assert.Null(resolution.Subject);
        Assert.True(resolution.ReferentGone);
    }

    // ── 8. The user changes the subject ──────────────────────────────────

    [Fact]
    public void A_topic_change_is_not_classified_as_a_follow_up()
    {
        var state = StateWithThreeRestaurants();
        Assert.True(state.HasReferents());

        var intent = Classify("Hur bjuder jag in en vän till appen?", hasRecentContext: true);

        Assert.Equal(GlunoIntent.SideQuestHelp, intent.PrimaryIntent);
        Assert.NotEqual(GlunoIntent.FollowUpClarification, intent.PrimaryIntent);
    }

    // ── 9. A known preference is never asked for again ───────────────────

    [Fact]
    public void Asking_about_a_budget_that_is_already_known_is_flagged()
    {
        var review = GlunoResponseReview.Review(new GlunoReviewInput
        {
            AnswerText = "Här är tre alternativ. Vad har ni för budget?",
            TargetWordCount = 120,
            PreferencesAlreadyKnown = ["budget"],
        });

        Assert.False(review.Acceptable);
        Assert.Contains(review.Findings, finding => finding.Code == "asks_for_known_preference");
    }

    // ── 10. Two possible hotels need a concrete question ─────────────────

    [Fact]
    public void Two_candidate_hotels_produce_a_question_naming_both()
    {
        var state = new GlunoWorkingState();
        state.Recent.Places.Add(new MentionedPlace("tripadvisor:900", "Hotel Negresco", "hotel") { Position = 0 });
        state.Recent.Places.Add(new MentionedPlace("tripadvisor:901", "Hotel Windsor", "hotel") { Position = 1 });

        var resolution = GlunoReferenceResolver.Resolve("Boka den", state, Trip(), "sv");

        Assert.True(resolution.IsAmbiguous);
        Assert.Contains("Negresco", resolution.Question!);
        Assert.Contains("Windsor", resolution.Question);
    }

    // ── 11. Results already shown are reused, not re-searched ────────────

    [Fact]
    public void A_follow_up_about_a_shown_place_does_not_get_the_search_tool()
    {
        var intent = Classify("Är den andra bra?", hasRecentContext: true);
        var workflow = Workflow(intent);

        Assert.Equal(GlunoIntent.FollowUpClarification, intent.PrimaryIntent);
        Assert.False(workflow.AllowsExternalSearch);

        var offered = GlunoPlanningStrategy.FilterActions(GlunoActions.All, workflow)
            .Select(action => action.Name)
            .ToList();

        Assert.DoesNotContain(GlunoActions.SearchPlaces, offered);
    }

    // ── 12. Stale provider data is refreshable ───────────────────────────

    [Fact]
    public void A_remembered_place_carries_when_it_was_fetched_so_it_can_be_refreshed()
    {
        var place = new MentionedPlace("tripadvisor:200", "Le Bistrot", "restaurant")
        {
            FetchedAtUtc = DateTime.UtcNow.AddDays(-30),
        };

        // Identity plus a timestamp, never the payload — so a stale entry can
        // be re-fetched rather than quietly served from an ageing copy.
        Assert.Equal("tripadvisor:200", place.ExternalId);
        Assert.True(DateTime.UtcNow - place.FetchedAtUtc > TimeSpan.FromDays(14));
    }

    // ── 13. A relaxed pace against eight stops ───────────────────────────

    [Fact]
    public void Relaxed_pace_with_eight_stops_is_reported_as_a_conflict_with_choices()
    {
        var conflicts = GlunoConflictDetector.Detect(new GlunoConflictInput
        {
            Pace = TripPace.Relaxed,
            RequestedStopCount = 8,
            Language = "sv",
        });

        var conflict = Assert.Single(conflicts, item => item.Code == "pace_vs_stops");
        Assert.NotEmpty(conflict.Explanation);
        Assert.InRange(conflict.Alternatives.Count, 1, 2);
    }

    // ── 14. No car and real distances ────────────────────────────────────

    [Fact]
    public void No_car_with_a_thirty_kilometre_hop_is_reported_with_alternatives()
    {
        var conflicts = GlunoConflictDetector.Detect(new GlunoConflictInput
        {
            Transport = TransportPreferences.From("vi vill inte köra bil", null, null),
            LongestLegKm = 30,
            Language = "en",
        });

        var conflict = Assert.Single(conflicts, item => item.Code == "no_car_vs_distance");
        Assert.InRange(conflict.Alternatives.Count, 1, 2);
    }

    // ── 15 & 16. Provider failures degrade rather than break ─────────────

    [Fact]
    public void A_routing_timeout_leaves_an_unverified_leg_rather_than_no_answer()
    {
        var leg = RouteLeg.StraightLine(
            new RoutePoint(43.69, 7.27), new RoutePoint(43.70, 7.28), TravelMode.Walking, "provider_failed");

        Assert.False(leg.Verified);
        Assert.Null(leg.DurationMinutes);
        Assert.Equal("provider_failed", leg.UnavailableReason);
    }

    [Fact]
    public void A_travel_time_with_no_verified_routing_behind_it_is_blocked()
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            AnswerText = "Det är ungefär 20 minuters promenad från hotellet.",
            HasVerifiedTravelTimes = false,
            Language = "sv",
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Blockers, blocker => blocker.Code == "fabricated_travel_time");
    }

    [Fact]
    public void Opening_hours_with_no_provider_behind_them_are_blocked()
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            AnswerText = "The museum opens at 09:00 and closes at 18:00.",
            HasVerifiedOpeningHours = false,
            Language = "en",
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Blockers, blocker => blocker.Code == "fabricated_opening_hours");
    }

    // ── 17. The gate stops a clash ───────────────────────────────────────

    [Fact]
    public void An_overlapping_day_plan_is_blocked()
    {
        var plan = Plan("""
            [
              { "title": "Museum", "time": "10:00", "endTime": "12:00" },
              { "title": "Lunch",  "time": "11:30", "endTime": "12:30" }
            ]
            """);

        var result = Gate().Check(new GlunoQualityInput
        {
            DayPlan = plan,
            ProducedProposal = true,
            ExpectsProposal = true,
            Language = "en",
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Blockers, blocker => blocker.Code == "time_overlap");
    }

    // ── 18. The gate removes an optional stop ────────────────────────────

    [Fact]
    public void A_clashing_optional_stop_is_removed_and_the_rest_survives()
    {
        var plan = Plan("""
            [
              { "title": "Museum", "time": "10:00", "endTime": "12:00", "isFixed": true },
              { "title": "Gallery", "time": "11:30", "endTime": "12:30" }
            ]
            """);

        var result = Gate().Check(new GlunoQualityInput
        {
            DayPlan = plan,
            ProducedProposal = true,
            ExpectsProposal = true,
            Language = "en",
        });

        Assert.NotNull(result.CorrectedPlan);
        var kept = result.CorrectedPlan!.Value.GetProperty("activities").EnumerateArray().ToList();
        Assert.Single(kept);
        Assert.Equal("Museum", kept[0].GetProperty("title").GetString());
        Assert.True(result.CorrectedPlan.Value.GetProperty("autoCorrected").GetBoolean());
    }

    // ── 19. A fixed booking is never auto-moved ──────────────────────────

    [Fact]
    public void Two_clashing_fixed_bookings_are_reported_and_neither_is_touched()
    {
        var plan = Plan("""
            [
              { "title": "Guided tour", "time": "14:00", "endTime": "16:00", "isFixed": true },
              { "title": "Booked dinner", "time": "15:00", "endTime": "16:30", "isFixed": true }
            ]
            """);

        var result = Gate().Check(new GlunoQualityInput
        {
            DayPlan = plan,
            ProducedProposal = true,
            ExpectsProposal = true,
            Language = "en",
        });

        Assert.False(result.Passed);
        // Nothing was "fixed" for them — a clash between two commitments is a
        // conversation, not a correction.
        Assert.Null(result.CorrectedPlan);
        Assert.True(result.RequiresClarification);
    }

    // ── 20. A good plan is left alone ────────────────────────────────────

    [Fact]
    public void A_sound_day_plan_passes_with_nothing_to_say()
    {
        var plan = Plan("""
            [
              { "title": "Old town walk", "time": "10:00", "endTime": "11:30", "category": "sight" },
              { "title": "Lunch",         "time": "12:30", "endTime": "13:30", "category": "food" },
              { "title": "Matisse Museum","time": "14:30", "endTime": "16:30", "category": "sight" }
            ]
            """);

        var result = Gate().Check(new GlunoQualityInput
        {
            DayPlan = plan,
            ProducedProposal = true,
            ExpectsProposal = true,
            HasVerifiedTravelTimes = true,
            Language = "en",
        });

        Assert.True(result.Passed);
        Assert.Empty(result.Blockers);
        Assert.Null(result.UserFacingNote);
    }

    // ── 21. An information question creates no proposal ──────────────────

    [Fact]
    public void A_trip_review_cannot_produce_a_proposal_at_all()
    {
        var intent = Classify("Vad saknas på resan?");
        var workflow = Workflow(intent);

        Assert.Equal(GlunoIntent.TripReview, intent.PrimaryIntent);
        Assert.False(intent.ExpectsProposal);
        Assert.False(workflow.AllowsProposals);
        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsRouting);

        var offered = GlunoPlanningStrategy.FilterActions(GlunoActions.All, workflow)
            .Select(action => action.Name)
            .ToList();

        Assert.DoesNotContain(offered, name => name.StartsWith("propose_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proposal_on_a_question_turn_is_blocked()
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            AnswerText = "Here's what I'd change.",
            ProducedProposal = true,
            ExpectsProposal = false,
            Language = "en",
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Blockers, blocker => blocker.Code == "unrequested_proposal");
    }

    // ── 22. A change request does produce one ────────────────────────────

    [Fact]
    public void An_explicit_change_request_is_allowed_to_propose()
    {
        var intent = Classify("Lägg till en middag på torsdag");
        var workflow = Workflow(intent);

        Assert.True(intent.ExpectsProposal);
        Assert.True(workflow.AllowsProposals);
    }

    // ── 23. No false confirmation that something was saved ───────────────

    [Theory]
    [InlineData("I've added it to Friday.")]
    [InlineData("Jag har lagt till den på fredag.")]
    [InlineData("That's now saved to your Adventure.")]
    [InlineData("Middagen är nu inlagd.")]
    public void Claiming_something_was_saved_is_blocked(string answer)
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            AnswerText = answer,
            SomethingWasApplied = false,
            Language = "en",
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Blockers, blocker => blocker.Code == "claims_already_saved");
    }

    [Fact]
    public void Proposing_language_is_not_mistaken_for_a_saved_claim()
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            AnswerText = "I've prepared a suggestion for Friday — accept it and it goes into the Adventure.",
            SomethingWasApplied = false,
            Language = "en",
        });

        Assert.DoesNotContain(result.Blockers, blocker => blocker.Code == "claims_already_saved");
    }

    // ── 24. A rejected restaurant is not offered again ───────────────────

    [Fact]
    public void A_previously_rejected_place_cannot_be_recommended_again()
    {
        var result = Gate().Check(new GlunoQualityInput
        {
            SuggestedPlaceIds = ["tripadvisor:200"],
            RejectedOptions = [new RejectedOption("Place", "tripadvisor:200", "Le Bistrot", "too expensive")],
            Language = "en",
        });

        Assert.False(result.Passed);
        var blocker = Assert.Single(result.Blockers, item => item.Code == "previously_rejected");
        Assert.Contains("Le Bistrot", blocker.Message);
    }

    // ── 25 & 26. Short Swedish, long English ─────────────────────────────

    [Fact]
    public void A_short_swedish_question_routes_the_same_as_its_english_twin()
    {
        Assert.Equal(GlunoIntent.SideQuestHelp, Classify("Var hittar jag packlistan?").PrimaryIntent);
        Assert.Equal(GlunoIntent.SideQuestHelp, Classify("Where do I find the packing list?").PrimaryIntent);
    }

    [Fact]
    public void A_long_english_planning_request_still_routes_to_the_planning_pipeline()
    {
        var intent = Classify(
            "We've got Saturday completely free and we'd love you to plan the whole day for us — "
            + "somewhere for lunch, a couple of things to see, and dinner in the evening.",
            activityCounts: ("2026-08-15", 0));

        Assert.Equal(GlunoIntent.PlanEmptyDay, intent.PrimaryIntent);
        Assert.True(Workflow(intent).UsesScheduleEngine);
    }

    // ── 27. A contradictory budget ───────────────────────────────────────

    [Fact]
    public void A_low_budget_against_expensive_places_is_reported_with_choices()
    {
        var conflicts = GlunoConflictDetector.Detect(new GlunoConflictInput
        {
            BudgetIsLow = true,
            ExpensivePlaceCount = 3,
            Language = "en",
        });

        var conflict = Assert.Single(conflicts, item => item.Code == "budget_vs_places");
        Assert.InRange(conflict.Alternatives.Count, 1, 2);
    }

    // ── 28. An open-ended Adventure ──────────────────────────────────────

    [Fact]
    public void An_adventure_with_no_end_date_still_routes_and_resolves_a_weekday()
    {
        var intent = GlunoIntentRouter.Classify(new GlunoIntentInput
        {
            Message = "Planera onsdagen",
            HasTrip = true,
            TripStartDate = Start,
            TripEndDate = null,
            Today = Start,
        });

        Assert.Equal(GlunoIntent.PlanEmptyDay, intent.PrimaryIntent);
        Assert.Equal("2026-08-12", intent.ReferencedDate);
    }

    // ── 29. A global conversation with no Adventure ──────────────────────

    [Fact]
    public void A_global_conversation_can_never_propose_or_route()
    {
        var intent = Classify("Planera lördagen åt oss", hasTrip: false);
        var workflow = Workflow(intent, hasTrip: false);

        Assert.Equal(GlunoIntentScope.Global, intent.Scope);
        Assert.False(workflow.AllowsProposals);
        Assert.False(workflow.AllowsRouting);
        Assert.False(workflow.NeedsTripContext);
    }

    [Fact]
    public void A_read_only_member_can_never_propose_however_the_message_reads()
    {
        var intent = Classify("Lägg till en middag på torsdag");
        var workflow = Workflow(intent, canEdit: false);

        Assert.True(intent.ExpectsProposal);
        Assert.False(workflow.AllowsProposals);
    }

    // ── 30. Route and id injection are refused ───────────────────────────

    [Fact]
    public void A_reference_never_resolves_to_an_id_the_user_typed()
    {
        var state = new GlunoWorkingState();

        // The message contains a well-formed Guid that is not in the plan and
        // was never discussed. Resolving it would mean the model could act on
        // any Activity by naming its id.
        var resolution = GlunoReferenceResolver.Resolve(
            $"Flytta {Guid.NewGuid()} till fredag", state, Trip(), "sv");

        Assert.Null(resolution.Subject);
    }

    [Fact]
    public void A_navigation_target_that_is_not_allow_listed_is_refused()
    {
        Assert.False(GlunoNavigationTargets.IsKnown("/trip/../admin"));
        Assert.False(GlunoNavigationTargets.IsKnown("https://example.com"));
    }

    // ── Cost control ─────────────────────────────────────────────────────

    [Fact]
    public void No_workflow_may_exceed_the_absolute_model_round_ceiling()
    {
        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var workflow = Workflow(new GlunoIntentResult
            {
                PrimaryIntent = intent,
                Confidence = 1,
                Scope = GlunoIntentScope.Trip,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = false,
            });

            Assert.InRange(workflow.MaxModelRounds, 1, GlunoPlanningStrategy.AbsoluteMaxModelRounds);
        }
    }

    [Fact]
    public void An_unsure_router_widens_the_workflow_rather_than_narrowing_it()
    {
        var unsure = new GlunoIntentResult
        {
            PrimaryIntent = GlunoIntent.Unclear,
            Confidence = 0.1,
            Scope = GlunoIntentScope.Trip,
            RequiresCurrentData = false,
            RequiresExternalSearch = false,
            ExpectsProposal = false,
            RequiresClarification = true,
        };

        var workflow = Workflow(unsure);

        // Being expensive is recoverable. Answering a planning question with no
        // plan loaded is not.
        Assert.True(workflow.NeedsTripContext);
        Assert.True(workflow.NeedsTripAnalysis);
    }

    // ── Working state ────────────────────────────────────────────────────

    [Fact]
    public void Re_mentioning_a_place_moves_it_to_the_front_instead_of_duplicating_it()
    {
        var list = new List<MentionedPlace>();

        GlunoRecentMentions.Promote(
            list, new MentionedPlace("a", "A", null), place => place.ExternalId, 5);
        GlunoRecentMentions.Promote(
            list, new MentionedPlace("b", "B", null), place => place.ExternalId, 5);
        GlunoRecentMentions.Promote(
            list, new MentionedPlace("a", "A", null), place => place.ExternalId, 5);

        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].ExternalId);
    }

    [Fact]
    public void A_negated_preference_survives_the_summary_verbatim()
    {
        var state = new GlunoWorkingState
        {
            DecidedPreferences = [new GlunoStatePreference("transport", "vi vill inte hyra bil")],
        };

        var roundTripped = JsonSerializer.Deserialize<GlunoWorkingState>(
            JsonSerializer.Serialize(state, GlunoJson.Options), GlunoJson.Options);

        // "transport: car" would invert the meaning. The user's own words are
        // the only safe representation.
        Assert.Equal("vi vill inte hyra bil", roundTripped!.DecidedPreferences[0].Value);
    }

    [Fact]
    public void The_working_state_format_is_versioned()
    {
        Assert.True(GlunoWorkingState.CurrentVersion >= 1);
        Assert.Equal(GlunoWorkingState.CurrentVersion, new GlunoWorkingState().Version);
    }

    // ── Answer shape ─────────────────────────────────────────────────────

    [Fact]
    public void A_stock_opening_and_a_stock_closing_are_both_flagged()
    {
        var review = GlunoResponseReview.Review(new GlunoReviewInput
        {
            AnswerText = "Great question! Nice is lovely in August. Let me know if you need anything else.",
            TargetWordCount = 110,
        });

        Assert.Contains(review.Findings, finding => finding.Code == "filler_opening");
        Assert.Contains(review.Findings, finding => finding.Code == "empty_closing");
    }

    [Fact]
    public void More_than_one_question_in_an_answer_is_flagged()
    {
        var review = GlunoResponseReview.Review(new GlunoReviewInput
        {
            AnswerText = "Vilket tempo vill ni ha? Och har ni bil? Och vad är budgeten?",
            TargetWordCount = 80,
        });

        Assert.Contains(review.Findings, finding => finding.Code == "too_many_questions");
    }

    [Fact]
    public void A_concise_direct_answer_passes_review_untouched()
    {
        var review = GlunoResponseReview.Review(new GlunoReviewInput
        {
            AnswerText = "Nice sits around 28°C in August, and the sea is warm. Pack for heat and shade.",
            TargetWordCount = 110,
        });

        Assert.True(review.Acceptable);
        Assert.Null(review.RevisionInstruction);
    }

    // ── The prompt's own contract ────────────────────────────────────────

    [Fact]
    public void The_prompt_tells_Gluno_to_use_the_resolved_reference_rather_than_guess()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("resolvedReference", prompt);
        Assert.Contains("placesAlreadyShown", prompt);
        Assert.Contains("rejectedOptions", prompt);
        Assert.Contains("referenceAmbiguous", prompt);
    }

    [Fact]
    public void The_prompt_carries_a_response_contract_per_question_shape()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("SideQuest help", prompt);
        Assert.Contains("A trip review", prompt);
        Assert.Contains("A day plan", prompt);
        Assert.Contains("A conflict", prompt);
    }

    [Fact]
    public void The_prompt_names_all_five_levels_of_certainty()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("**Verified.**", prompt);
        Assert.Contains("**A reasonable assumption.**", prompt);
        Assert.Contains("**Missing information.**", prompt);
        Assert.Contains("**Provider data that could not be fetched.**", prompt);
        Assert.Contains("**Your own planning judgement.**", prompt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GlunoQualityGate Gate() => new();

    private static JsonElement Plan(string activitiesJson)
        => JsonSerializer.Deserialize<JsonElement>($$"""
            { "date": "2026-08-12", "feasible": true, "activities": {{activitiesJson}} }
            """);

    private static GlunoWorkingState StateWithThreeRestaurants()
    {
        var state = new GlunoWorkingState();

        state.Recent.Places.AddRange(
        [
            new MentionedPlace("tripadvisor:100", "La Merenda", "restaurant") { Position = 0 },
            new MentionedPlace("tripadvisor:200", "Le Bistrot", "restaurant") { Position = 1 },
            new MentionedPlace("tripadvisor:300", "Chez Pipo", "restaurant") { Position = 2 },
        ]);

        return state;
    }

    private static GlunoTripContext Trip() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Nice",
        Destination = "Nice",
        StartDate = Start,
        EndDate = End,
        Activities =
        [
            new GlunoActivityContext
            {
                Id = MuseumId, Title = "Matisse Museum", Date = new DateOnly(2026, 8, 12), Category = "sight",
            },
            new GlunoActivityContext
            {
                Id = HotelId, Title = "Hotel Windsor", Date = Start, Category = "hotel",
                EndDate = End,
            },
        ],
    };

    private static GlunoTripContext TripWithTwoLunches() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Nice",
        Destination = "Nice",
        StartDate = Start,
        EndDate = End,
        Activities =
        [
            new GlunoActivityContext
            {
                Id = LunchAId, Title = "Lunch", Date = new DateOnly(2026, 8, 11), Category = "food",
            },
            new GlunoActivityContext
            {
                Id = LunchBId, Title = "Lunch", Date = new DateOnly(2026, 8, 12), Category = "food",
            },
        ],
    };

    private static GlunoTripContext EmptyTrip() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Nice",
        Destination = "Nice",
        StartDate = Start,
        EndDate = End,
        Activities = Array.Empty<GlunoActivityContext>(),
    };
}
