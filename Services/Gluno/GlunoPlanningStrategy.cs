namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The workflow one turn is allowed to run.
///
/// Every flag here is a permission, not an instruction. The model still decides
/// what to do; this decides what it can reach for. A false flag means the tool
/// is not even offered — which is stronger than telling the model not to use
/// it, and does not depend on the model complying.
/// </summary>
public sealed record GlunoWorkflow
{
    public required GlunoIntent Intent { get; init; }

    /// Load the Adventure at all. False for app help and general questions —
    /// there is nothing in a trip context that improves "where is the packing
    /// list?".
    public required bool NeedsTripContext { get; init; }

    /// Run the deterministic trip analysis. Cheap, but pointless when the
    /// answer is not about the plan.
    public required bool NeedsTripAnalysis { get; init; }

    public required bool NeedsPreferences { get; init; }
    public required bool NeedsWeather { get; init; }

    /// Offer search_places. False keeps Tripadvisor out of the turn entirely.
    public required bool AllowsExternalSearch { get; init; }

    /// Offer verified routing. Costs money per matrix, so it is granted only to
    /// the intents that lay out a day.
    public required bool AllowsRouting { get; init; }

    /// Run the schedule engine.
    public required bool UsesScheduleEngine { get; init; }

    /// Offer the propose_* actions. False means this turn CANNOT produce a
    /// proposal, however the model phrases things.
    public required bool AllowsProposals { get; init; }

    /// Run the quality gate before anything is shown.
    public required bool RunsQualityGate { get; init; }

    /// Run the internal review pass. Costs a model round, so it is reserved for
    /// answers where being wrong is expensive.
    public required bool RunsInternalReview { get; init; }

    /// Ceiling on model rounds for this turn, tool loops included.
    public required int MaxModelRounds { get; init; }

    /// Rough word budget for the answer, from the response contract.
    public required int TargetWordCount { get; init; }

    /// The action names this turn may use. Empty means "everything the scope
    /// already allows".
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Picks the smallest workflow that can answer the question.
///
/// WHY THIS EXISTS. Before it, every turn ran the same pipeline: build the full
/// trip context, analyse it, offer every tool, let the model decide. That is
/// correct and wasteful — "how do I invite someone?" paid for a trip context, a
/// findings pass and the option to call a paid place API, and the extra work
/// could not possibly change the answer. Worse, offering propose_activity on a
/// question turn means the model sometimes takes it, and the user gets an
/// unrequested edit to approve.
///
/// The rule is: grant the least that can still produce a good answer, and grant
/// MORE when the router is unsure rather than less. A misrouted turn that had
/// too much context is merely expensive; one that had too little is wrong.
/// </summary>
public static class GlunoPlanningStrategy
{
    /// <summary>
    /// A hard ceiling on model rounds per turn, whatever the intent.
    ///
    /// The backstop against a loop that keeps calling tools: without it, a
    /// model that misreads a tool result can burn the user's wait and the
    /// budget in one turn.
    /// </summary>
    public const int AbsoluteMaxModelRounds = 6;

    public static GlunoWorkflow For(GlunoIntentResult intent, bool hasTrip, bool canEdit)
    {
        var workflow = Base(intent, hasTrip);

        // A model that is not confident about the intent gets the wider
        // workflow, not the narrower one. Being expensive is recoverable;
        // answering a planning question with no plan loaded is not.
        if (intent.Confidence < GlunoIntentRouter.LowConfidence && hasTrip)
        {
            workflow = workflow with
            {
                NeedsTripContext = true,
                NeedsTripAnalysis = true,
                NeedsPreferences = true,
            };
        }

        // Read-only membership can never produce a proposal, whatever the
        // intent said. Belt-and-braces: the action executor enforces this too.
        if (!canEdit)
        {
            workflow = workflow with { AllowsProposals = false, UsesScheduleEngine = false };
        }

        if (!hasTrip)
        {
            workflow = workflow with
            {
                NeedsTripContext = false,
                NeedsTripAnalysis = false,
                NeedsWeather = false,
                AllowsProposals = false,
                UsesScheduleEngine = false,
                AllowsRouting = false,
            };
        }

        return workflow with
        {
            MaxModelRounds = Math.Clamp(workflow.MaxModelRounds, 1, AbsoluteMaxModelRounds),
        };
    }

    private static GlunoWorkflow Base(GlunoIntentResult intent, bool hasTrip) => intent.PrimaryIntent switch
    {
        // ── Cheapest tier: nothing about this Adventure matters ───────────
        //
        // "How do I add a photo?" is answered from the capability registry and
        // nothing else. No trip, no provider, no proposal, one model round.
        GlunoIntent.SideQuestHelp => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = false,
            NeedsTripAnalysis = false,
            NeedsPreferences = false,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 70,
            AllowedActions =
            [
                GlunoActions.SearchSideQuestFeatures,
                GlunoActions.GetSideQuestFeature,
                GlunoActions.GetAvailableActions,
                GlunoActions.GetCurrentScreenHelp,
                GlunoActions.NavigateInSideQuest,
            ],
        },

        GlunoIntent.NavigationRequest => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = false,
            NeedsTripAnalysis = false,
            NeedsPreferences = false,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 40,
            AllowedActions =
            [
                GlunoActions.NavigateInSideQuest,
                GlunoActions.SearchSideQuestFeatures,
                GlunoActions.GetCurrentScreenHelp,
            ],
        },

        // Knowledge from the model itself. A trip context would only tempt it
        // into an unrequested review.
        GlunoIntent.GeneralTravelQuestion or GlunoIntent.DestinationRecommendation => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = hasTrip,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 110,
        },

        GlunoIntent.PreferenceUpdate => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = false,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 40,
            AllowedActions = [GlunoActions.RememberPreference],
        },

        GlunoIntent.ForgetPreference => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = false,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 30,
            AllowedActions = [GlunoActions.ForgetPreference],
        },

        // ── Reading the plan ──────────────────────────────────────────────
        //
        // Everything needed is already in SideQuest. No provider, no routing,
        // and emphatically no proposal: "what's missing?" is a request to be
        // told, not an invitation to edit.
        GlunoIntent.TripReview => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = true,
            NeedsWeather = true,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 130,
            AllowedActions = [GlunoActions.GetTripOverview],
        },

        // ── Recommending places ───────────────────────────────────────────
        GlunoIntent.PlaceRecommendation => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = hasTrip,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = true,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            // Recommending is not adding. The user picks, then asks.
            AllowsProposals = false,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 3,
            TargetWordCount = 130,
            AllowedActions = [GlunoActions.SearchPlaces, GlunoActions.RememberPreference],
        },

        // ── The full pipeline ─────────────────────────────────────────────
        GlunoIntent.PlanEmptyDay or GlunoIntent.BuildFullItinerary => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = true,
            NeedsWeather = true,
            AllowsExternalSearch = true,
            AllowsRouting = true,
            UsesScheduleEngine = true,
            AllowsProposals = true,
            RunsQualityGate = true,
            RunsInternalReview = true,
            MaxModelRounds = 5,
            TargetWordCount = 170,
        },

        // Improving a day starts from what is there. It may search, but it does
        // not begin by assuming the answer is somewhere else.
        GlunoIntent.ImproveExistingDay => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = true,
            NeedsWeather = true,
            AllowsExternalSearch = true,
            AllowsRouting = true,
            UsesScheduleEngine = true,
            AllowsProposals = true,
            RunsQualityGate = true,
            RunsInternalReview = true,
            MaxModelRounds = 4,
            TargetWordCount = 150,
        },

        // ── Single, targeted changes ──────────────────────────────────────
        GlunoIntent.MoveActivity => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = false,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = true,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 3,
            TargetWordCount = 70,
            AllowedActions = [GlunoActions.ProposeActivityMove, GlunoActions.GetTripOverview],
        },

        GlunoIntent.AddActivity => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = true,
            NeedsWeather = false,
            // Only when the stop is not already something we discussed. The
            // executor's own per-turn budget stops a re-search of the same
            // three restaurants.
            AllowsExternalSearch = intent.ReferencedPlaceId == null,
            AllowsRouting = true,
            UsesScheduleEngine = false,
            AllowsProposals = true,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 3,
            TargetWordCount = 80,
            AllowedActions =
            [
                GlunoActions.ProposeActivity, GlunoActions.SearchPlaces, GlunoActions.GetTripOverview,
            ],
        },

        GlunoIntent.ChangeAdventureDates => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = true,
            NeedsTripAnalysis = true,
            NeedsPreferences = false,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = true,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 3,
            TargetWordCount = 80,
            AllowedActions = [GlunoActions.ProposeTripDateChange, GlunoActions.GetTripOverview],
        },

        // ── Follow-ups ────────────────────────────────────────────────────
        //
        // The reference resolver has already worked out what "the second one"
        // is. Searching again would return the same results at full price and
        // possibly in a different order, which is how "the second one" starts
        // meaning something else halfway through a conversation.
        GlunoIntent.FollowUpClarification => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = hasTrip,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = true,
            UsesScheduleEngine = false,
            AllowsProposals = intent.ExpectsProposal,
            RunsQualityGate = true,
            RunsInternalReview = false,
            MaxModelRounds = 3,
            TargetWordCount = 90,
        },

        GlunoIntent.ConfirmationOrRejection => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = hasTrip,
            NeedsTripAnalysis = false,
            NeedsPreferences = false,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 50,
        },

        // Unclear gets a question, not a pipeline.
        _ => new GlunoWorkflow
        {
            Intent = intent.PrimaryIntent,
            NeedsTripContext = hasTrip,
            NeedsTripAnalysis = false,
            NeedsPreferences = true,
            NeedsWeather = false,
            AllowsExternalSearch = false,
            AllowsRouting = false,
            UsesScheduleEngine = false,
            AllowsProposals = false,
            RunsQualityGate = false,
            RunsInternalReview = false,
            MaxModelRounds = 2,
            TargetWordCount = 45,
        },
    };

    /// <summary>
    /// The actions this turn may offer the model.
    ///
    /// Starts from what the scope already permits (trip membership, edit
    /// rights) and then REMOVES what the workflow does not need. Removing
    /// rather than adding means a new action is available everywhere by
    /// default and has to be deliberately withheld — the failure mode is a tool
    /// being offered too widely, which is visible, rather than silently
    /// missing.
    /// </summary>
    public static IReadOnlyList<GlunoActionDefinition> FilterActions(
        IReadOnlyList<GlunoActionDefinition> scopeActions, GlunoWorkflow workflow)
    {
        var allowed = scopeActions.AsEnumerable();

        if (workflow.AllowedActions.Count > 0)
        {
            var names = workflow.AllowedActions.ToHashSet(StringComparer.Ordinal);
            allowed = allowed.Where(action => names.Contains(action.Name));
        }

        if (!workflow.AllowsExternalSearch)
            allowed = allowed.Where(action => action.Name != GlunoActions.SearchPlaces);

        if (!workflow.AllowsProposals)
            allowed = allowed.Where(action => !action.Name.StartsWith("propose_", StringComparison.Ordinal));

        var result = allowed.ToList();

        // Never hand back an empty toolset — a turn with no tools at all is a
        // model that cannot even look up what SideQuest does.
        return result.Count > 0
            ? result
            : scopeActions.Where(action => !action.Name.StartsWith("propose_", StringComparison.Ordinal)).ToList();
    }
}
