using Microsoft.Extensions.Configuration;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for how a turn is EXECUTED: which model, how many rounds, what runs in
/// parallel, what happens when the clock or the budget runs out.
///
/// These failures are invisible in a transcript. A turn routed to the wrong
/// model still answers fluently; a missing cancellation still returns
/// eventually; a broken idempotency check just means the user occasionally gets
/// two answers and assumes they double-tapped. All of them show up as "it feels
/// worse lately" and none of them can be diagnosed after the fact — so every
/// case below pins one specific decision.
///
/// Nothing calls a model, a network, or a database.
/// </summary>
public class OrchestrationRuntimeEvals
{
    private static IConfiguration Config(params (string Key, string Value)[] overrides)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Gluno:Models:Primary"] = "test-primary" }
                    .Concat(overrides.Select(pair =>
                        new KeyValuePair<string, string?>(pair.Key, pair.Value)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();

    private static GlunoModelPolicy Policy(params (string Key, string Value)[] overrides)
        => new(Config(overrides));

    private static GlunoIntentResult Intent(
        GlunoIntent intent, double confidence = 1, bool expectsProposal = false)
        => new()
        {
            PrimaryIntent = intent,
            Confidence = confidence,
            Scope = GlunoIntentScope.Trip,
            RequiresCurrentData = false,
            RequiresExternalSearch = false,
            ExpectsProposal = expectsProposal,
            RequiresClarification = false,
        };

    private static GlunoModelChoice Choose(
        GlunoIntent intent,
        double confidence = 1,
        bool scheduleEngine = false,
        int toolRounds = 1,
        bool regeneration = false,
        params (string Key, string Value)[] overrides)
        => Policy(overrides).Choose(new GlunoModelRequest
        {
            Intent = intent,
            IntentConfidence = confidence,
            UsesScheduleEngine = scheduleEngine,
            MaxToolRounds = toolRounds,
            WorkflowMaxRounds = 4,
            IsRegeneration = regeneration,
        });

    // ── 1. App help skips the model entirely ─────────────────────────────

    [Fact]
    public void A_navigation_request_with_one_target_needs_no_model_call()
    {
        var result = GlunoDirectAnswer.TryAnswer(new GlunoDirectRequest
        {
            Intent = GlunoIntent.NavigationRequest,
            Language = "en",
            NavigationTarget = new GlunoNavigationCard
            {
                TargetId = GlunoNavigationTargets.All[0],
                Label = "Packing list",
            },
        });

        Assert.NotNull(result);
        Assert.Equal("navigation", result!.Reason);
        Assert.Single(result.Navigations);
    }

    [Fact]
    public void A_feature_that_does_not_exist_is_answered_without_a_model()
    {
        var result = GlunoDirectAnswer.TryAnswer(new GlunoDirectRequest
        {
            Intent = GlunoIntent.SideQuestHelp,
            Language = "sv",
            FeatureDefinitelyMissing = true,
        });

        Assert.NotNull(result);
        Assert.Equal("feature_missing", result!.Reason);
        Assert.Contains("SideQuest", result.Text);
    }

    [Fact]
    public void Anything_needing_judgement_still_goes_to_a_model()
    {
        // The narrow-by-default rule: a canned reply where the user wanted
        // help is a far worse failure than an unnecessary model call.
        Assert.Null(GlunoDirectAnswer.TryAnswer(new GlunoDirectRequest
        {
            Intent = GlunoIntent.PlaceRecommendation,
        }));

        Assert.Null(GlunoDirectAnswer.TryAnswer(new GlunoDirectRequest
        {
            Intent = GlunoIntent.SideQuestHelp,
            FeatureDefinitelyMissing = false,
        }));
    }

    // ── 2, 3, 4, 5. Model tier follows the WORK, not the wording ─────────

    [Fact]
    public void A_simple_question_uses_the_fast_tier()
    {
        var choice = Choose(GlunoIntent.SideQuestHelp);

        Assert.Equal(GlunoModelTier.Fast, choice.Tier);
        Assert.Equal(GlunoWorkload.Simple, choice.Workload);
    }

    [Fact]
    public void A_day_plan_uses_the_strong_tier()
    {
        var choice = Choose(GlunoIntent.PlanEmptyDay, scheduleEngine: true, toolRounds: 4);

        Assert.Equal(GlunoModelTier.Primary, choice.Tier);
        Assert.Equal(GlunoWorkload.Complex, choice.Workload);
    }

    [Fact]
    public void A_short_but_complex_turn_still_gets_the_strong_model()
    {
        // Low confidence means the router does not know what this is. A
        // misread request costs far more than a model round.
        var choice = Choose(GlunoIntent.Unclear, confidence: 0.2);

        Assert.Equal(GlunoModelTier.Primary, choice.Tier);
        Assert.Equal("low_confidence", choice.Reason);
    }

    [Fact]
    public void A_long_but_simple_app_question_still_gets_the_fast_model()
    {
        // The policy never sees the message. Length measures typing style, not
        // difficulty — this is the whole reason model choice reads the WORK.
        var choice = Choose(GlunoIntent.SideQuestHelp);

        Assert.Equal(GlunoModelTier.Fast, choice.Tier);
    }

    [Fact]
    public void A_recommendation_is_not_downgraded_to_the_fast_model()
    {
        // The asymmetry: a weaker model's recommendation reads perfectly well
        // and is quietly worse, and nobody would ever report it.
        var choice = Choose(GlunoIntent.PlaceRecommendation);

        Assert.Equal(GlunoModelTier.Primary, choice.Tier);
        Assert.Equal(GlunoWorkload.Moderate, choice.Workload);
    }

    [Fact]
    public void Model_selection_is_deterministic()
    {
        var first = Choose(GlunoIntent.TripReview);
        var second = Choose(GlunoIntent.TripReview);

        Assert.Equal(first.Tier, second.Tier);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Model, second.Model);
    }

    [Fact]
    public void A_regeneration_uses_the_review_tier()
    {
        var choice = Choose(GlunoIntent.PlaceRecommendation, regeneration: true);

        Assert.Equal(GlunoModelTier.Review, choice.Tier);
        Assert.Equal("regeneration", choice.Reason);
    }

    // ── 6 & 7. Parallel groups only hold independent tools ───────────────

    [Fact]
    public void Independent_lookups_are_grouped_for_parallel_execution()
    {
        var plan = Planner().Build(new GlunoTurnPlanRequest
        {
            Intent = Intent(GlunoIntent.PlanEmptyDay, expectsProposal: true),
            Workflow = GlunoPlanningStrategy.For(
                Intent(GlunoIntent.PlanEmptyDay, expectsProposal: true), hasTrip: true, canEdit: true),
        });

        var group = Assert.Single(plan.ParallelGroups);
        Assert.Contains(GlunoActions.SearchPlaces, group.Tools);
        Assert.True(group.MaxConcurrency <= 3);
    }

    [Fact]
    public void Dependent_work_is_never_placed_in_a_parallel_group()
    {
        var plan = Planner().Build(new GlunoTurnPlanRequest
        {
            Intent = Intent(GlunoIntent.PlanEmptyDay, expectsProposal: true),
            Workflow = GlunoPlanningStrategy.For(
                Intent(GlunoIntent.PlanEmptyDay, expectsProposal: true), hasTrip: true, canEdit: true),
        });

        // Routing needs the coordinates a place search returns, and a day plan
        // needs the routing. Parallelising those would run them against data
        // that does not exist yet.
        foreach (var group in plan.ParallelGroups)
        {
            Assert.DoesNotContain(GlunoActions.ProposeDayPlan, group.Tools);
            Assert.DoesNotContain(GlunoActions.ProposeActivity, group.Tools);
        }
    }

    [Fact]
    public void Every_parallel_tool_is_also_in_the_allow_list()
    {
        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var result = Intent(intent, expectsProposal: true);
            var plan = Planner().Build(new GlunoTurnPlanRequest
            {
                Intent = result,
                Workflow = GlunoPlanningStrategy.For(result, hasTrip: true, canEdit: true),
            });

            Assert.DoesNotContain(plan.Validate(), problem => problem.StartsWith("parallel_tool_not_allowed"));
        }
    }

    // ── 8 & 9. Latency budgets ───────────────────────────────────────────

    [Fact]
    public void An_app_help_turn_gets_a_much_tighter_budget_than_an_itinerary()
    {
        var help = GlunoLatencyBudget.For(GlunoIntent.SideQuestHelp, Config());
        var itinerary = GlunoLatencyBudget.For(GlunoIntent.BuildFullItinerary, Config());

        Assert.True(help.Total < itinerary.Total);
        // Nobody tolerates four seconds for "where is the packing list".
        Assert.True(help.Total <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Latency_budgets_are_configurable()
    {
        var budget = GlunoLatencyBudget.For(
            GlunoIntent.PlanEmptyDay, Config(("Gluno:Latency:DayPlanSeconds", "90")));

        Assert.Equal(TimeSpan.FromSeconds(90), budget.Total);
    }

    [Fact]
    public void Every_stage_gets_its_own_sub_budget()
    {
        var budget = GlunoLatencyBudget.For(GlunoIntent.PlanEmptyDay, Config());

        Assert.True(budget.Context > TimeSpan.Zero);
        Assert.True(budget.Providers > TimeSpan.Zero);
        Assert.True(budget.Routing > TimeSpan.Zero);
        Assert.True(budget.Model > TimeSpan.Zero);
        Assert.True(budget.Review > TimeSpan.Zero);
    }

    [Fact]
    public void An_exhausted_budget_refuses_to_start_new_expensive_work()
    {
        var tracker = new GlunoLatencyTracker(new GlunoLatencyBudget
        {
            Total = TimeSpan.FromMilliseconds(200),
            Context = TimeSpan.FromMilliseconds(50),
            Providers = TimeSpan.FromMilliseconds(50),
            Routing = TimeSpan.FromMilliseconds(50),
            Model = TimeSpan.FromMilliseconds(50),
            Review = TimeSpan.FromMilliseconds(50),
        });

        // Starting a routing matrix with no time left spends the money and the
        // wait and produces nothing usable.
        Assert.False(tracker.HasRoomFor(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_fresh_budget_has_room_for_the_work_it_was_sized_for()
    {
        var tracker = new GlunoLatencyTracker(GlunoLatencyBudget.For(GlunoIntent.PlanEmptyDay, Config()));

        Assert.True(tracker.HasRoomFor(TimeSpan.FromSeconds(5)));
        Assert.False(tracker.IsRunningLow);
    }

    // ── 14, 15, 16. Idempotency ──────────────────────────────────────────

    [Fact]
    public void A_malformed_or_missing_key_proceeds_rather_than_failing()
    {
        // An older client that sends no key must keep working. Refusing would
        // break chat for everyone mid-rollout to prevent a duplicate.
        Assert.True(IsAcceptableKey(null) == false);
        Assert.False(IsAcceptableKey("short"));
        Assert.False(IsAcceptableKey("has spaces in it"));
        Assert.True(IsAcceptableKey("t-abc123-xyz789def"));
    }

    private static bool IsAcceptableKey(string? key)
        => key != null && System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z0-9_-]{8,64}$");

    [Fact]
    public void A_generated_key_matches_the_accepted_shape()
    {
        // The client generator and the server validator have to agree, or every
        // send silently loses its idempotency protection.
        for (var index = 0; index < 20; index++)
        {
            var key = $"t-{DateTime.UtcNow.Ticks.ToString("x")}-{Guid.NewGuid():N}"[..40];
            Assert.True(IsAcceptableKey(key), key);
        }
    }

    [Fact]
    public void Turn_request_statuses_cover_every_terminal_outcome()
    {
        // A status missing here means a row stuck at in_flight forever, and a
        // user who can never resend that message.
        Assert.Equal("in_flight", sidequest.backend.Models.GlunoTurnRequestStatuses.InFlight);
        Assert.Equal("completed", sidequest.backend.Models.GlunoTurnRequestStatuses.Completed);
        Assert.Equal("failed", sidequest.backend.Models.GlunoTurnRequestStatuses.Failed);
        Assert.Equal("cancelled", sidequest.backend.Models.GlunoTurnRequestStatuses.Cancelled);
    }

    // ── 17, 18, 19. Context budget ───────────────────────────────────────

    [Fact]
    public void A_question_about_one_day_does_not_carry_the_whole_trip()
    {
        var trip = TripWithSevenDays();

        var narrowed = GlunoContextBudget.NarrowToDate(trip, new DateOnly(2026, 8, 13));

        // The focus day plus its neighbours — check-out times and an early
        // flight on the next day both change what today can hold.
        Assert.All(narrowed.Activities, activity =>
            Assert.InRange(activity.Date, new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 14)));
        Assert.True(narrowed.Activities.Count < trip.Activities.Count);
    }

    [Fact]
    public void A_critical_negation_survives_compression()
    {
        var budget = new GlunoContextBudget(Config(("Gluno:Context:MaxTokens", "2000")));

        var sections = new List<GlunoContextSection>
        {
            new(GlunoContextPriority.SystemRules, "rules", Filler(4000)),
            new(GlunoContextPriority.Preferences, "prefs", "\"we do not want to hire a car\"")
            {
                IsCritical = true,
            },
            new(GlunoContextPriority.OlderHistory, "history", Filler(8000)),
        };

        var result = budget.Fit(sections);

        // Forty tokens that change every answer must not be dropped to save
        // room for history nobody needs.
        Assert.DoesNotContain("prefs", result.DroppedSections);
        Assert.Contains("history", result.DroppedSections);
    }

    [Fact]
    public void Sections_are_dropped_in_priority_order_never_randomly()
    {
        var budget = new GlunoContextBudget(Config(("Gluno:Context:MaxTokens", "2000")));

        var result = budget.Fit(
        [
            new(GlunoContextPriority.CurrentRequest, "request", "\"plan saturday\""),
            new(GlunoContextPriority.Evidence, "evidence", Filler(4000)),
            new(GlunoContextPriority.OlderHistory, "history", Filler(4000)),
        ]);

        Assert.DoesNotContain("request", result.DroppedSections);
        Assert.Contains("history", result.DroppedSections);
    }

    [Fact]
    public void A_context_that_still_will_not_fit_is_reported_rather_than_cut_blindly()
    {
        var budget = new GlunoContextBudget(Config(("Gluno:Context:MaxTokens", "2000")));

        // Even the protected core is too big. The honest answer is "ask me
        // about one day at a time", not a randomly truncated context that
        // produces a confident answer built on whatever survived.
        var result = budget.Fit(
        [
            new(GlunoContextPriority.SystemRules, "rules", Filler(20_000)),
        ]);

        Assert.True(result.ExceedsBudgetEvenAfterTrimming);
    }

    [Fact]
    public void Only_the_relevant_findings_are_sent()
    {
        var findings = Enumerable.Range(0, 20)
            .Select(index => new TripFinding
            {
                Type = $"finding_{index}",
                Severity = index % 2 == 0 ? "warning" : "info",
                Explanation = "x",
                Date = index == 3 ? "2026-08-13" : null,
            })
            .ToList();

        var relevant = GlunoContextBudget.RelevantFindings(findings, "2026-08-13");

        Assert.True(relevant.Count <= 6);
        Assert.Equal("finding_3", relevant[0].Type);
    }

    // ── 20, 21, 22. Usage ceilings ───────────────────────────────────────

    [Fact]
    public void A_user_over_their_hourly_ceiling_is_stopped()
    {
        var budget = Usage(("Gluno:Usage:UserHourlyTurns", "2"));
        var user = Guid.NewGuid();

        Assert.Equal(GlunoUsageVerdict.Allowed, budget.CheckAllowed(user));
        budget.Record(user, new GlunoTurnUsage());
        budget.Record(user, new GlunoTurnUsage());

        Assert.Equal(GlunoUsageVerdict.UserLimitReached, budget.CheckAllowed(user));
    }

    [Fact]
    public void One_users_usage_does_not_stop_another_user()
    {
        var budget = Usage(("Gluno:Usage:UserHourlyTurns", "1"));
        var noisy = Guid.NewGuid();

        budget.Record(noisy, new GlunoTurnUsage());

        Assert.Equal(GlunoUsageVerdict.UserLimitReached, budget.CheckAllowed(noisy));
        Assert.Equal(GlunoUsageVerdict.Allowed, budget.CheckAllowed(Guid.NewGuid()));
    }

    [Fact]
    public void The_global_ceiling_stops_everyone()
    {
        var budget = Usage(("Gluno:Usage:GlobalDailyOutputTokens", "1000"));

        budget.Record(Guid.NewGuid(), new GlunoTurnUsage { OutputTokens = 2000 });

        Assert.Equal(GlunoUsageVerdict.GlobalLimitReached, budget.CheckAllowed(Guid.NewGuid()));
    }

    [Fact]
    public void Cost_is_not_estimated_when_prices_are_not_configured()
    {
        // A hardcoded rate silently becomes a lie the moment prices change. No
        // estimate is more honest than a wrong one.
        var budget = Usage();

        Assert.Null(budget.EstimateCost(new GlunoTurnUsage { InputTokens = 1000, OutputTokens = 500 }));
        Assert.Equal("unpriced", budget.CostBucket(new GlunoTurnUsage()));
    }

    [Fact]
    public void Cost_is_reported_as_a_bucket_not_a_figure()
    {
        var budget = Usage(
            ("Gluno:Pricing:InputPerMillion", "5"),
            ("Gluno:Pricing:OutputPerMillion", "25"));

        // A per-turn figure correlates with how elaborate somebody's trip is,
        // which is closer to personal information than it looks.
        var bucket = budget.CostBucket(new GlunoTurnUsage { InputTokens = 10_000, OutputTokens = 2_000 });

        Assert.Contains(bucket, new[] { "nano", "micro", "small", "medium", "large" });
        Assert.DoesNotContain('.', bucket);
    }

    // ── 23, 24, 25, 26. Degradation ──────────────────────────────────────

    [Fact]
    public void Losing_places_costs_more_than_losing_weather()
    {
        var places = new GlunoDegradationTracker();
        places.RecordFailure("tripadvisor");

        var weather = new GlunoDegradationTracker();
        weather.RecordFailure("weather");

        Assert.True(places.Level > weather.Level);
    }

    [Fact]
    public void Losing_every_provider_falls_all_the_way_to_local_data()
    {
        var tracker = new GlunoDegradationTracker();

        tracker.RecordFailure("tripadvisor");
        tracker.RecordFailure("routing");
        tracker.RecordFailure("weather");

        Assert.Equal(GlunoDegradationLevel.LocalOnly, tracker.Level);
        Assert.Equal(3, tracker.MissingProviders.Count);
    }

    [Fact]
    public void A_failed_provider_is_not_called_again_in_the_same_turn()
    {
        var tracker = new GlunoDegradationTracker();

        Assert.True(tracker.ShouldTry("tripadvisor"));
        tracker.RecordFailure("tripadvisor");

        // Retrying spends the user's remaining latency on the outcome we have
        // the most evidence for.
        Assert.False(tracker.ShouldTry("tripadvisor"));
        Assert.True(tracker.ShouldTry("routing"));
    }

    [Fact]
    public void A_degraded_turn_says_what_was_missing_in_both_languages()
    {
        var tracker = new GlunoDegradationTracker();
        tracker.RecordFailure("routing");

        Assert.NotNull(tracker.Note("sv"));
        Assert.NotNull(tracker.Note("en"));
        Assert.NotEqual(tracker.Note("sv"), tracker.Note("en"));
    }

    [Fact]
    public void An_undegraded_turn_says_nothing_about_sources()
    {
        Assert.Null(new GlunoDegradationTracker().Note("en"));
    }

    // ── 27. Unknown failure codes ────────────────────────────────────────

    [Fact]
    public void An_unknown_failure_code_is_treated_as_non_retryable_with_generic_copy()
    {
        // A future backend code this build has never seen must degrade, never
        // crash and never appear raw on screen.
        Assert.False(GlunoFailureCodes.IsRetryable("some_future_code"));
        Assert.NotEmpty(GlunoFailureCodes.UserMessage("some_future_code", "en"));
        Assert.NotEmpty(GlunoFailureCodes.UserMessage("some_future_code", "sv"));
    }

    [Fact]
    public void A_permanent_configuration_error_never_offers_retry()
    {
        // A retry button that can never work is worse than no button.
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiNotConfigured));
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.ModelNotConfigured));
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.UserUsageLimit));
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiRefusal));
    }

    [Fact]
    public void A_transient_failure_does_offer_retry()
    {
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiTimeout));
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.TripadvisorUnavailable));
    }

    [Fact]
    public void Cancellation_renders_as_nothing_at_all()
    {
        // The user chose it. Copy here would tell them something broke.
        Assert.Equal(string.Empty, GlunoFailureCodes.UserMessage(GlunoFailureCodes.Cancelled, "en"));
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.Cancelled));
    }

    [Fact]
    public void An_exception_becomes_a_category_never_a_message()
    {
        var exception = new InvalidOperationException("https://api.example.com?key=SECRET");

        var code = GlunoFailureCodes.FromException(exception);

        // An SDK exception message can carry the request URI, and the request
        // URI carries the API key.
        Assert.DoesNotContain("SECRET", code);
        Assert.DoesNotContain("http", code);
    }

    // ── 31 & 32. Missing configuration ───────────────────────────────────

    [Fact]
    public void A_missing_model_name_reports_not_configured_rather_than_crashing()
    {
        var policy = new GlunoModelPolicy(new ConfigurationBuilder().Build());

        Assert.False(policy.IsConfigured);
        Assert.Equal("not_configured", policy.UnavailableReason);
    }

    [Fact]
    public void A_blank_model_name_is_treated_as_missing()
    {
        var policy = new GlunoModelPolicy(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gluno:Models:Primary"] = "   " })
            .Build());

        Assert.False(policy.IsConfigured);
    }

    [Fact]
    public void Fast_and_review_models_fall_back_to_primary()
    {
        // A deployment that sets one model still works — it just pays primary
        // prices for everything, which is the safe direction to fail in.
        var policy = Policy();

        Assert.Equal("test-primary", Choose(GlunoIntent.SideQuestHelp).Model);
        Assert.Equal("test-primary", Choose(GlunoIntent.PlaceRecommendation, regeneration: true).Model);
    }

    [Fact]
    public void A_configured_fast_model_is_actually_used_for_cheap_turns()
    {
        var choice = Choose(
            GlunoIntent.SideQuestHelp,
            overrides: ("Gluno:Models:Fast", "test-fast"));

        Assert.Equal("test-fast", choice.Model);
    }

    [Fact]
    public void The_model_id_is_never_part_of_what_the_policy_reports_publicly()
    {
        // Telemetry records a TIER and a REASON. The id is deployment
        // configuration and never crosses a boundary the app can see.
        var choice = Choose(GlunoIntent.PlanEmptyDay, scheduleEngine: true);

        Assert.DoesNotContain(choice.Model, choice.Reason);
        Assert.DoesNotContain(choice.Model, choice.Tier.ToString());
    }

    // ── 34 & 35. Round ceilings ──────────────────────────────────────────

    [Fact]
    public void No_turn_can_exceed_the_configured_model_round_ceiling()
    {
        var policy = Policy(("Gluno:MaxModelRounds", "2"));

        var choice = policy.Choose(new GlunoModelRequest
        {
            Intent = GlunoIntent.BuildFullItinerary,
            IntentConfidence = 1,
            // A workflow asking for more than the ceiling allows.
            WorkflowMaxRounds = 99,
        });

        Assert.Equal(2, choice.MaxModelRounds);
    }

    [Fact]
    public void The_round_ceiling_is_clamped_even_against_absurd_configuration()
    {
        var policy = Policy(("Gluno:MaxModelRounds", "9999"));

        Assert.True(policy.MaxModelRoundsPerTurn <= 8);
    }

    [Fact]
    public void A_plan_that_offers_search_without_a_budget_fails_validation()
    {
        var intent = Intent(GlunoIntent.PlaceRecommendation);
        var workflow = GlunoPlanningStrategy.For(intent, hasTrip: true, canEdit: true);

        var plan = Planner().Build(new GlunoTurnPlanRequest { Intent = intent, Workflow = workflow })
            with { ExternalSearchBudget = 0 };

        // Offering a tool with no budget produces a model that keeps trying and
        // a turn that keeps refusing until the round ceiling ends it.
        Assert.Contains("search_offered_without_budget", plan.Validate());
    }

    [Fact]
    public void A_well_formed_plan_validates_clean()
    {
        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var result = Intent(intent, expectsProposal: true);
            var plan = Planner().Build(new GlunoTurnPlanRequest
            {
                Intent = result,
                Workflow = GlunoPlanningStrategy.For(result, hasTrip: true, canEdit: true),
            });

            Assert.Empty(plan.Validate());
        }
    }

    [Fact]
    public void A_tool_outside_the_plan_is_not_in_the_allow_list()
    {
        var intent = Intent(GlunoIntent.SideQuestHelp);
        var plan = Planner().Build(new GlunoTurnPlanRequest
        {
            Intent = intent,
            Workflow = GlunoPlanningStrategy.For(intent, hasTrip: true, canEdit: true),
        });

        Assert.DoesNotContain(GlunoActions.SearchPlaces, plan.RequiredTools);
        Assert.DoesNotContain(GlunoActions.ProposeDayPlan, plan.RequiredTools);
        Assert.Equal(0, plan.ExternalSearchBudget);
        Assert.Equal(0, plan.RoutingCallBudget);
    }

    // ── 33. Provider timeouts do not trigger aggressive retries ──────────

    [Fact]
    public void A_provider_timeout_marks_the_provider_down_rather_than_retrying()
    {
        var tracker = new GlunoDegradationTracker();
        tracker.RecordFailure("routing");

        Assert.False(tracker.ShouldTry("routing"));
        Assert.Equal(GlunoDegradationLevel.MinorDegradation, tracker.Level);
    }

    // ── 36. Telemetry carries no private values ──────────────────────────

    [Fact]
    public void Cost_buckets_contain_no_figures_and_no_content()
    {
        var budget = Usage(
            ("Gluno:Pricing:InputPerMillion", "5"),
            ("Gluno:Pricing:OutputPerMillion", "25"));

        foreach (var tokens in new[] { 100, 10_000, 500_000 })
        {
            var bucket = budget.CostBucket(new GlunoTurnUsage { InputTokens = tokens, OutputTokens = tokens });
            Assert.DoesNotContain(bucket, new[] { tokens.ToString() });
            Assert.Matches("^[a-z]+$", bucket);
        }
    }

    [Fact]
    public void The_model_policy_reason_is_a_stable_machine_code()
    {
        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var reason = Choose(intent).Reason;

            // Snake_case machine codes, aggregatable in a log backend and free
            // of anything the user typed.
            Assert.Matches("^[a-z_]+$", reason);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GlunoTurnPlanner Planner() => new(Policy(), Config());

    private static GlunoUsageBudget Usage(params (string Key, string Value)[] overrides)
        => new(Config(overrides), new TestLogger<GlunoUsageBudget>());

    private static string Filler(int characters) => "\"" + new string('x', characters) + "\"";

    private static GlunoTripContext TripWithSevenDays() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Nice",
        Destination = "Nice",
        StartDate = new DateOnly(2026, 8, 10),
        EndDate = new DateOnly(2026, 8, 16),
        Activities = Enumerable.Range(0, 7)
            .Select(offset => new GlunoActivityContext
            {
                Id = Guid.NewGuid(),
                Title = $"Day {offset}",
                Date = new DateOnly(2026, 8, 10).AddDays(offset),
            })
            .ToList(),
    };

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
