namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Which tier of model a turn needs.
///
/// Three, not a continuum: a fourth tier would be a judgement call nobody can
/// make reliably, and the interesting decision is only ever "does this need the
/// expensive one".
/// </summary>
public enum GlunoModelTier
{
    /// Cheap and quick. Enough for app help, a short factual answer, or turning
    /// a deterministic result into a sentence.
    Fast,
    /// The default for real planning work.
    Primary,
    /// Checking or regenerating an answer that failed grounding. Usually the
    /// same model as Primary, configurable separately so it can be raised
    /// without raising the cost of every turn.
    Review,
}

/// <summary>
/// What kind of work a turn actually is.
///
/// Deliberately NOT derived from message length. "Plan our week in Nice around
/// the wedding on Thursday" is fourteen words and needs everything; "hi, I was
/// wondering, could you possibly tell me where in the app I would go to have a
/// look at the packing list we made?" is thirty-two and needs a registry lookup.
/// Length measures typing style, not difficulty.
/// </summary>
public enum GlunoWorkload
{
    /// Answerable from structured data with no model at all.
    Deterministic,
    /// One lookup, one short answer.
    Simple,
    /// Judgement, but bounded: a recommendation, a review of a plan.
    Moderate,
    /// Multi-entity planning, contradictions to resolve, several tool rounds.
    Complex,
}

/// <summary>
/// The model decision for one turn, and why.
/// </summary>
public sealed record GlunoModelChoice
{
    public required GlunoModelTier Tier { get; init; }
    public required GlunoWorkload Workload { get; init; }

    /// <summary>
    /// The configured model id. Server-side only — this string never appears in
    /// an API response, a log line the user could see, or anything the mobile
    /// app receives.
    /// </summary>
    public required string Model { get; init; }

    public required int MaxOutputTokens { get; init; }
    public required double Temperature { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required int MaxModelRounds { get; init; }

    /// Short machine reason, for telemetry: "app_help", "low_confidence",
    /// "multi_entity_proposal", "regeneration".
    public required string Reason { get; init; }

    /// True when the turn needs no model at all.
    public bool SkipsModel => Workload == GlunoWorkload.Deterministic;
}

/// <summary>
/// Picks the model tier for a turn.
///
/// WHY THIS IS ITS OWN TYPE. Before it, the model id lived in one place and
/// every turn got the same one — which means the cheapest question in the
/// product (where is the packing list?) cost the same as the most expensive
/// (plan our week). At any real volume that is most of the bill, spent on turns
/// where the strong model's advantage is literally unobservable.
///
/// THE ASYMMETRY THAT SETS THE DEFAULTS. Getting the tier wrong downward is
/// expensive in quality and invisible — a weaker model produces a fluent,
/// plausible, slightly worse plan and nobody can tell. Getting it wrong upward
/// costs money and is completely safe. So every ambiguous case resolves upward,
/// and the fast tier is granted only where the work is genuinely bounded.
///
/// DETERMINISTIC AND OBSERVABLE, by design. Same intent plus same workflow
/// always yields the same tier, the choice is recorded in telemetry, and every
/// case below is pinned by an eval — because a model-selection bug that only
/// shows up as "answers feel worse lately" is close to undiagnosable otherwise.
/// </summary>
public sealed class GlunoModelPolicy
{
    private readonly IConfiguration _config;

    public GlunoModelPolicy(IConfiguration config) => _config = config;

    // ── Configuration ────────────────────────────────────────────────────
    //
    // No model id is hardcoded. A model that has been retired, renamed or
    // superseded is a configuration change, not a deploy — and a wrong value
    // must produce a clean "not configured" rather than a 400 in the middle of
    // someone's turn.

    private string? PrimaryModel => Trimmed(_config["Gluno:Models:Primary"] ?? _config["Gluno:Model"]);
    private string? FastModel => Trimmed(_config["Gluno:Models:Fast"]);
    private string? ReviewModel => Trimmed(_config["Gluno:Models:Review"]);

    /// <summary>
    /// True when at least the primary model is configured.
    ///
    /// Fast and review both fall back to primary, so a deployment that sets one
    /// model still works — it simply pays primary prices for everything, which
    /// is the safe direction to fail in.
    /// </summary>
    public bool IsConfigured => PrimaryModel != null;

    /// Machine reason for /api/gluno/status. Never the model id.
    public string? UnavailableReason => IsConfigured ? null : "not_configured";

    private int MaxOutputTokens => Math.Clamp(_config.GetValue("Gluno:MaxTokens", 4096), 256, 32_000);

    /// <summary>
    /// Temperature per task type.
    ///
    /// Low for anything that is really a formatting job over structured data —
    /// there is one right answer and creativity is a liability. Higher for
    /// recommendation, where the whole value is having a view.
    /// </summary>
    private double TemperatureFor(GlunoWorkload workload) => workload switch
    {
        GlunoWorkload.Simple => Clamp(_config.GetValue("Gluno:Temperature:Simple", 0.3)),
        GlunoWorkload.Moderate => Clamp(_config.GetValue("Gluno:Temperature:Moderate", 0.7)),
        GlunoWorkload.Complex => Clamp(_config.GetValue("Gluno:Temperature:Complex", 0.7)),
        _ => Clamp(_config.GetValue("Gluno:Temperature:Simple", 0.3)),
    };

    private TimeSpan TimeoutFor(GlunoModelTier tier) => TimeSpan.FromSeconds(Math.Clamp(
        tier == GlunoModelTier.Fast
            ? _config.GetValue("Gluno:TimeoutSeconds:Fast", 20)
            : _config.GetValue("Gluno:TimeoutSeconds:Primary", 60),
        5, 180));

    /// The hard ceiling, whatever a caller asks for.
    public int MaxModelRoundsPerTurn => Math.Clamp(_config.GetValue("Gluno:MaxModelRounds", 4), 1, 8);

    // ── Selection ────────────────────────────────────────────────────────

    public GlunoModelChoice Choose(GlunoModelRequest request)
    {
        var workload = ClassifyWorkload(request);
        var tier = TierFor(workload, request);

        return new GlunoModelChoice
        {
            Tier = tier,
            Workload = workload,
            Model = Resolve(tier),
            MaxOutputTokens = MaxOutputTokens,
            Temperature = TemperatureFor(workload),
            Timeout = TimeoutFor(tier),
            MaxModelRounds = Math.Min(request.WorkflowMaxRounds, MaxModelRoundsPerTurn),
            Reason = ReasonFor(workload, request),
        };
    }

    /// <summary>
    /// The model id for a tier, falling back to primary.
    ///
    /// Never returns null: <see cref="IsConfigured"/> is checked before a turn
    /// starts, so reaching here without a primary model is a bug rather than a
    /// runtime condition.
    /// </summary>
    private string Resolve(GlunoModelTier tier) => tier switch
    {
        GlunoModelTier.Fast => FastModel ?? PrimaryModel!,
        GlunoModelTier.Review => ReviewModel ?? PrimaryModel!,
        _ => PrimaryModel!,
    };

    /// <summary>
    /// How hard the turn is.
    ///
    /// Reads the intent, the workflow's permissions and the router's confidence
    /// — everything that describes the WORK. Never the message text.
    /// </summary>
    public static GlunoWorkload ClassifyWorkload(GlunoModelRequest request)
    {
        // A regeneration is always complex: the first attempt already failed
        // grounding, so this is the expensive-to-get-wrong case by definition.
        if (request.IsRegeneration) return GlunoWorkload.Complex;

        if (request.CanAnswerDeterministically) return GlunoWorkload.Deterministic;

        // Low confidence means the router does not know what this is. A
        // misread request is far costlier than a model round.
        if (request.IntentConfidence < GlunoIntentRouter.LowConfidence) return GlunoWorkload.Complex;

        // Several tools, or a proposal touching several entities, is genuinely
        // multi-step reasoning.
        if (request.UsesScheduleEngine || request.MaxToolRounds > 2) return GlunoWorkload.Complex;

        return request.Intent switch
        {
            GlunoIntent.BuildFullItinerary or GlunoIntent.PlanEmptyDay
                or GlunoIntent.ImproveExistingDay => GlunoWorkload.Complex,

            // Bounded judgement: read the plan, pick a few places, form a view.
            GlunoIntent.TripReview or GlunoIntent.PlaceRecommendation
                or GlunoIntent.DestinationRecommendation
                or GlunoIntent.MoveActivity or GlunoIntent.AddActivity
                or GlunoIntent.ChangeAdventureDates => GlunoWorkload.Moderate,

            // A follow-up whose reference is already resolved is a short
            // answer about one thing. If it turns into a proposal the branch
            // above has already caught it.
            GlunoIntent.FollowUpClarification when request.ReferenceResolved => GlunoWorkload.Simple,

            GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest
                or GlunoIntent.GeneralTravelQuestion
                or GlunoIntent.PreferenceUpdate or GlunoIntent.ForgetPreference
                or GlunoIntent.ConfirmationOrRejection => GlunoWorkload.Simple,

            _ => GlunoWorkload.Moderate,
        };
    }

    private static GlunoModelTier TierFor(GlunoWorkload workload, GlunoModelRequest request)
    {
        if (request.IsRegeneration) return GlunoModelTier.Review;

        return workload switch
        {
            GlunoWorkload.Simple or GlunoWorkload.Deterministic => GlunoModelTier.Fast,
            // Moderate goes to primary rather than fast. This is the asymmetry
            // in action: a recommendation from a weaker model reads perfectly
            // well and is quietly worse, and nobody would ever report it.
            _ => GlunoModelTier.Primary,
        };
    }

    private static string ReasonFor(GlunoWorkload workload, GlunoModelRequest request)
    {
        if (request.IsRegeneration) return "regeneration";
        if (request.CanAnswerDeterministically) return "deterministic";
        if (request.IntentConfidence < GlunoIntentRouter.LowConfidence) return "low_confidence";
        if (request.UsesScheduleEngine) return "schedule_engine";
        if (request.MaxToolRounds > 2) return "multi_tool";

        return workload switch
        {
            GlunoWorkload.Complex => "complex_planning",
            GlunoWorkload.Moderate => "bounded_judgement",
            _ => "simple_lookup",
        };
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Everything the policy reads. Passed in rather than fetched, so selection is
/// a pure function an eval can pin exactly.
///
/// Note what is absent: the message text, its length, and the user. Model
/// choice depends on the shape of the WORK, and nothing else.
/// </summary>
public sealed class GlunoModelRequest
{
    public required GlunoIntent Intent { get; init; }
    public required double IntentConfidence { get; init; }

    /// From the workflow: this turn will lay out a day.
    public bool UsesScheduleEngine { get; init; }

    /// The workflow's tool-round ceiling.
    public int MaxToolRounds { get; init; } = 1;

    public int WorkflowMaxRounds { get; init; } = 2;

    /// A reference resolver already worked out what the user pointed at.
    public bool ReferenceResolved { get; init; }

    /// This turn can be answered from structured data with no model.
    public bool CanAnswerDeterministically { get; init; }

    /// This is the retry after a grounding failure.
    public bool IsRegeneration { get; init; }
}
