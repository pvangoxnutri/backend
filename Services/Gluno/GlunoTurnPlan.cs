using System.Diagnostics;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A set of tools that may run at the same time.
///
/// Membership is a claim about INDEPENDENCE, not about speed: two tools belong
/// in a group only when neither needs the other's result. Weather and place
/// search are independent. Place search and routing are not — routing needs the
/// coordinates search returns — and putting them in one group would route
/// between places nobody chose.
/// </summary>
public sealed record GlunoToolGroup(string Name, IReadOnlyList<string> Tools)
{
    /// Ceiling on concurrent calls inside this group. Bounds burst load on a
    /// provider that is already rate-limited.
    public int MaxConcurrency { get; init; } = 3;
}

/// <summary>How to degrade when a stage fails or the clock runs out.</summary>
public enum GlunoFallbackStrategy
{
    /// Answer from what SideQuest already holds.
    UseLocalDataOnly,
    /// Drop the optional enrichment, keep the core answer.
    SkipOptionalHydration,
    /// Reorder what is already planned rather than proposing new stops.
    ReorderExistingOnly,
    /// A localised, honest fallback sentence.
    SafeFallbackText,
}

/// <summary>
/// Everything a turn will do, decided before any of it happens.
///
/// WHY DECIDE UP FRONT. Two reasons that both come from the same place. First,
/// a plan can be VALIDATED — budgets checked, tool lists reconciled, ceilings
/// applied — while a turn that decides as it goes can only be observed after
/// the fact. Second, and more importantly, a tool that is not in the plan is
/// refused: the model cannot widen its own budget by asking nicely, because
/// the allow-list was fixed before it saw the question.
///
/// The plan may be rebuilt AT MOST ONCE, when reference resolution changes what
/// the turn turns out to be ("the second one" resolving to a restaurant makes
/// an add-activity turn). More than once and the ceilings stop meaning
/// anything.
/// </summary>
public sealed record GlunoTurnPlan
{
    public required GlunoIntentResult Intent { get; init; }
    public required GlunoWorkflow Workflow { get; init; }
    public required GlunoModelChoice Model { get; init; }
    public required GlunoContextOptions RequiredContext { get; init; }

    /// The complete allow-list for this turn. A call to anything else is
    /// refused by the executor rather than argued with.
    public required IReadOnlyList<string> RequiredTools { get; init; }

    public required IReadOnlyList<GlunoToolGroup> ParallelGroups { get; init; }

    /// External searches this turn may make, across all providers.
    public required int ExternalSearchBudget { get; init; }
    /// Routing provider calls this turn may make.
    public required int RoutingCallBudget { get; init; }

    public required bool RequiresProposal { get; init; }
    public required bool RequiresReview { get; init; }
    public required bool RequiresGrounding { get; init; }

    public required GlunoLatencyBudget Latency { get; init; }
    public required GlunoFallbackStrategy Fallback { get; init; }

    /// Short label for telemetry: "app_help", "day_plan", "recommendation".
    public required string PlanType { get; init; }

    /// <summary>
    /// Checks the plan is internally consistent before anything runs.
    ///
    /// Catches the class of bug where a workflow permits something the budget
    /// forbids — offering search_places with a search budget of zero, say,
    /// which would produce a model that keeps trying and a turn that keeps
    /// refusing until the round ceiling ends it.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Model.MaxModelRounds < 1) problems.Add("model_rounds_below_one");
        if (Latency.Total <= TimeSpan.Zero) problems.Add("no_latency_budget");

        if (RequiredTools.Contains(GlunoActions.SearchPlaces) && ExternalSearchBudget < 1)
            problems.Add("search_offered_without_budget");

        if (RequiresProposal && !Workflow.AllowsProposals)
            problems.Add("proposal_required_but_not_allowed");

        // A tool in a parallel group that is not in the allow-list would be
        // refused at call time — a plan that contains one is a bug in plan
        // construction, not a runtime condition.
        foreach (var group in ParallelGroups)
        {
            foreach (var tool in group.Tools)
            {
                if (!RequiredTools.Contains(tool)) problems.Add($"parallel_tool_not_allowed:{tool}");
            }
        }

        return problems;
    }
}

/// <summary>
/// How long a turn may take, split by stage.
///
/// WHY PER-STAGE AND NOT ONE NUMBER. A single total tells you nothing while it
/// is being spent. Per-stage means the orchestrator can ask "is there time for
/// a routing matrix?" BEFORE starting one, which is the only point at which the
/// answer is useful — a timeout that fires mid-provider-call has already spent
/// the money and the wait.
///
/// And these are budgets, not timeouts. The response to running low is to do
/// LESS: skip optional hydration, stop starting new expensive work, answer with
/// what is already in hand. Raising every timeout instead is how a product ends
/// up with a spinner nobody trusts.
/// </summary>
public sealed record GlunoLatencyBudget
{
    public required TimeSpan Total { get; init; }
    public required TimeSpan Context { get; init; }
    public required TimeSpan Providers { get; init; }
    public required TimeSpan Routing { get; init; }
    public required TimeSpan Model { get; init; }
    /// Grounding regeneration and the review pass share this.
    public required TimeSpan Review { get; init; }

    public static GlunoLatencyBudget For(GlunoIntent intent, IConfiguration config)
    {
        // Seconds, configurable per turn shape. The defaults reflect what each
        // kind of answer is worth waiting for: nobody tolerates four seconds
        // for "where is the packing list", and everybody tolerates fifteen for
        // a whole day planned around their bookings.
        var total = intent switch
        {
            GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest
                => config.GetValue("Gluno:Latency:HelpSeconds", 8),
            GlunoIntent.GeneralTravelQuestion or GlunoIntent.PreferenceUpdate
                or GlunoIntent.ForgetPreference or GlunoIntent.ConfirmationOrRejection
                => config.GetValue("Gluno:Latency:SimpleSeconds", 12),
            GlunoIntent.PlaceRecommendation or GlunoIntent.DestinationRecommendation
                => config.GetValue("Gluno:Latency:RecommendationSeconds", 25),
            GlunoIntent.TripReview
                => config.GetValue("Gluno:Latency:ReviewSeconds", 20),
            GlunoIntent.BuildFullItinerary
                => config.GetValue("Gluno:Latency:ItinerarySeconds", 60),
            GlunoIntent.PlanEmptyDay or GlunoIntent.ImproveExistingDay
                => config.GetValue("Gluno:Latency:DayPlanSeconds", 45),
            _ => config.GetValue("Gluno:Latency:SimpleSeconds", 12),
        };

        var totalSpan = TimeSpan.FromSeconds(Math.Clamp(total, 5, 120));

        // Proportions rather than fixed slices, so raising the total raises
        // every stage coherently instead of leaving one starved.
        return new GlunoLatencyBudget
        {
            Total = totalSpan,
            Context = Fraction(totalSpan, 0.12),
            Providers = Fraction(totalSpan, 0.30),
            Routing = Fraction(totalSpan, 0.20),
            Model = Fraction(totalSpan, 0.55),
            Review = Fraction(totalSpan, 0.20),
        };
    }

    private static TimeSpan Fraction(TimeSpan total, double share)
        => TimeSpan.FromMilliseconds(Math.Max(500, total.TotalMilliseconds * share));
}

/// <summary>
/// Tracks how much of the budget is left, mid-turn.
///
/// The whole point is <see cref="HasRoomFor"/>: asking BEFORE starting
/// expensive work whether there is time to finish it. Starting a routing matrix
/// with two seconds left spends the money and the wait and produces nothing.
/// </summary>
public sealed class GlunoLatencyTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _stageMs = new(StringComparer.Ordinal);

    public GlunoLatencyTracker(GlunoLatencyBudget budget) => Budget = budget;

    public GlunoLatencyBudget Budget { get; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public TimeSpan Remaining
    {
        get
        {
            var left = Budget.Total - _stopwatch.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Whether there is time to start something that takes roughly this long.
    ///
    /// The margin is deliberate: finishing a provider call with 50 ms to spare
    /// still leaves no time to use the result, so work that would consume the
    /// entire remainder is not worth starting.
    /// </summary>
    public bool HasRoomFor(TimeSpan estimated) => Remaining > estimated + TimeSpan.FromMilliseconds(500);

    /// Below this, only work already in flight gets to finish.
    public bool IsRunningLow => Remaining < Budget.Total * 0.25;

    public IDisposable Stage(string name) => new StageTimer(this, name);

    public IReadOnlyDictionary<string, long> StageMilliseconds => _stageMs;

    private void Record(string name, long milliseconds)
        => _stageMs[name] = _stageMs.GetValueOrDefault(name) + milliseconds;

    private sealed class StageTimer : IDisposable
    {
        private readonly GlunoLatencyTracker _tracker;
        private readonly string _name;
        private readonly long _startedAt;

        public StageTimer(GlunoLatencyTracker tracker, string name)
        {
            _tracker = tracker;
            _name = name;
            _startedAt = tracker._stopwatch.ElapsedMilliseconds;
        }

        public void Dispose()
            => _tracker.Record(_name, _tracker._stopwatch.ElapsedMilliseconds - _startedAt);
    }
}

/// <summary>
/// Builds and validates the plan for a turn.
/// </summary>
public sealed class GlunoTurnPlanner
{
    private readonly GlunoModelPolicy _models;
    private readonly IConfiguration _config;

    public GlunoTurnPlanner(GlunoModelPolicy models, IConfiguration config)
    {
        _models = models;
        _config = config;
    }

    public GlunoTurnPlan Build(GlunoTurnPlanRequest request)
    {
        var workflow = request.Workflow;
        var intent = request.Intent;

        var model = _models.Choose(new GlunoModelRequest
        {
            Intent = intent.PrimaryIntent,
            IntentConfidence = intent.Confidence,
            UsesScheduleEngine = workflow.UsesScheduleEngine,
            MaxToolRounds = workflow.MaxModelRounds,
            WorkflowMaxRounds = workflow.MaxModelRounds,
            ReferenceResolved = request.ReferenceResolved,
            CanAnswerDeterministically = request.CanAnswerDeterministically,
        });

        var tools = GlunoPlanningStrategy
            .FilterActions(GlunoActions.All, workflow)
            .Select(action => action.Name)
            .ToList();

        return new GlunoTurnPlan
        {
            Intent = intent,
            Workflow = workflow,
            Model = model,
            RequiredContext = new GlunoContextOptions
            {
                IncludeTrip = workflow.NeedsTripContext,
                IncludeWeather = workflow.NeedsWeather,
                IncludeAnalysis = workflow.NeedsTripAnalysis,
                IncludeDiscussedPlaces = true,
            },
            RequiredTools = tools,
            ParallelGroups = BuildGroups(tools, workflow),
            ExternalSearchBudget = workflow.AllowsExternalSearch ? GlunoActions.MaxSearchesPerTurn : 0,
            RoutingCallBudget = workflow.AllowsRouting
                ? Math.Clamp(_config.GetValue("Routing:MaxRequestsPerTurn", 4), 1, 20)
                : 0,
            RequiresProposal = workflow.AllowsProposals && intent.ExpectsProposal,
            RequiresReview = workflow.RunsInternalReview,
            RequiresGrounding = true,
            Latency = GlunoLatencyBudget.For(intent.PrimaryIntent, _config),
            Fallback = FallbackFor(intent.PrimaryIntent),
            PlanType = PlanTypeFor(intent.PrimaryIntent),
        };
    }

    /// <summary>
    /// Which tools may run at once.
    ///
    /// One group, deliberately. The independent pairs worth parallelising are
    /// the read-only lookups — capability search, screen help, trip overview,
    /// and a place search that does not depend on any of them. Everything else
    /// in Gluno has a real dependency: routing needs the places search
    /// returned, a day plan needs the routing, a proposal needs the plan.
    ///
    /// Listing a dependent tool here would not make it faster; it would make it
    /// run against data that does not exist yet.
    /// </summary>
    private static IReadOnlyList<GlunoToolGroup> BuildGroups(
        IReadOnlyList<string> tools, GlunoWorkflow workflow)
    {
        var independent = new[]
        {
            GlunoActions.SearchPlaces,
            GlunoActions.GetTripOverview,
            GlunoActions.SearchSideQuestFeatures,
            GlunoActions.GetSideQuestFeature,
            GlunoActions.GetCurrentScreenHelp,
            GlunoActions.GetAvailableActions,
        }
            .Where(tools.Contains)
            .ToList();

        if (independent.Count < 2) return Array.Empty<GlunoToolGroup>();

        return
        [
            new GlunoToolGroup("independent_lookups", independent)
            {
                // Small on purpose: these hit a rate-limited provider and a
                // database, and three at once already saturates the useful
                // parallelism for a single chat turn.
                MaxConcurrency = workflow.AllowsExternalSearch ? 3 : 2,
            },
        ];
    }

    private static GlunoFallbackStrategy FallbackFor(GlunoIntent intent) => intent switch
    {
        GlunoIntent.PlanEmptyDay or GlunoIntent.BuildFullItinerary => GlunoFallbackStrategy.SkipOptionalHydration,
        GlunoIntent.ImproveExistingDay => GlunoFallbackStrategy.ReorderExistingOnly,
        GlunoIntent.PlaceRecommendation => GlunoFallbackStrategy.UseLocalDataOnly,
        GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest => GlunoFallbackStrategy.SafeFallbackText,
        _ => GlunoFallbackStrategy.UseLocalDataOnly,
    };

    private static string PlanTypeFor(GlunoIntent intent) => intent switch
    {
        GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest => "app_help",
        GlunoIntent.PlanEmptyDay or GlunoIntent.ImproveExistingDay => "day_plan",
        GlunoIntent.BuildFullItinerary => "itinerary",
        GlunoIntent.PlaceRecommendation or GlunoIntent.DestinationRecommendation => "recommendation",
        GlunoIntent.TripReview => "trip_review",
        _ => "simple",
    };
}

public sealed class GlunoTurnPlanRequest
{
    public required GlunoIntentResult Intent { get; init; }
    public required GlunoWorkflow Workflow { get; init; }
    public bool ReferenceResolved { get; init; }
    public bool CanAnswerDeterministically { get; init; }
}
