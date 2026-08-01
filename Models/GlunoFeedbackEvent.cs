using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

public static class GlunoFeedbackTypes
{
    public const string ResponseHelpful = "response_helpful";
    public const string ResponseNotHelpful = "response_not_helpful";
    public const string ProposalAccepted = "proposal_accepted";
    public const string ProposalRejected = "proposal_rejected";
    public const string ProposalEditedBeforeApply = "proposal_edited_before_apply";
    public const string RecommendationSelected = "recommendation_selected";
    public const string RecommendationRejected = "recommendation_rejected";
    public const string PreferenceCorrected = "preference_corrected";
    public const string FactualCorrection = "factual_correction";
    public const string WrongReference = "wrong_reference";
    public const string TooManySuggestions = "too_many_suggestions";
    public const string TooFewSuggestions = "too_few_suggestions";
    public const string TooExpensive = "too_expensive";
    public const string TooMuchWalking = "too_much_walking";
    public const string TooBusy = "too_busy";
    public const string TooSlow = "too_slow";
    public const string NotRelevant = "not_relevant";
    public const string AlreadyPlanned = "already_planned";
    public const string ProviderInformationWrong = "provider_information_wrong";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        ResponseHelpful, ResponseNotHelpful, ProposalAccepted, ProposalRejected,
        ProposalEditedBeforeApply, RecommendationSelected, RecommendationRejected,
        PreferenceCorrected, FactualCorrection, WrongReference,
        TooManySuggestions, TooFewSuggestions, TooExpensive, TooMuchWalking,
        TooBusy, TooSlow, NotRelevant, AlreadyPlanned, ProviderInformationWrong, Other,
    ];

    public static bool IsKnown(string? type) => type != null && All.Contains(type);

    /// <summary>
    /// Types that say something about HOW the user wants to travel, as opposed
    /// to whether one answer landed.
    ///
    /// Only these can ever contribute to a preference candidate. "Not helpful"
    /// says the answer missed; it does not say what the person prefers, and
    /// treating it as if it did is how an assistant builds a profile out of
    /// noise.
    /// </summary>
    public static bool CarriesPreferenceSignal(string type) => type is TooExpensive
        or TooMuchWalking or TooBusy or TooSlow or TooManySuggestions or TooFewSuggestions
        or ProposalEditedBeforeApply or RecommendationRejected or PreferenceCorrected;
}

/// <summary>
/// One thing the user did that tells us something.
///
/// APPEND-ONLY, and that is the point. A feedback row is a record of a moment —
/// "on Tuesday they said this answer missed". Editing it in place would rewrite
/// history and make the candidate arithmetic below unauditable. A later
/// correction SUPERSEDES an earlier row; it never overwrites it.
///
/// WHAT THIS IS NOT: training data. Nothing here is sent anywhere, fine-tunes
/// anything, or leaves SideQuest. It is product data that changes which
/// suggestions this user sees next, on this trip, at the scope they agreed to.
///
/// Deliberately absent: the prompt, the answer, the conversation. A feedback
/// row points AT a message by id; it does not contain one.
/// </summary>
public class GlunoFeedbackEvent
{
    /// Bumped when the meaning of the fields changes, so old rows are
    /// recognisable rather than silently reinterpreted.
    public const int CurrentContextVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Always the authenticated principal. A userId in a request body is never
    /// read — that would let anyone file feedback as anyone.
    /// </summary>
    public Guid UserId { get; set; }

    public Guid ConversationId { get; set; }

    /// Null for feedback that is not about one Adventure.
    public Guid? TripId { get; set; }

    /// The assistant turn this is about.
    public Guid? MessageId { get; set; }

    public Guid? ProposalId { get; set; }

    /// <summary>
    /// A namespaced provider id when the feedback is about a specific
    /// recommendation — "tripadvisor:12345". Never a place NAME.
    /// </summary>
    [MaxLength(80)]
    public string? RecommendationRef { get; set; }

    /// <see cref="GlunoFeedbackTypes"/>.
    [Required]
    [MaxLength(40)]
    public string EventType { get; set; } = string.Empty;

    /// conversation | trip | global — how far this signal may reach.
    [Required]
    [MaxLength(20)]
    public string Scope { get; set; } = GlunoPreferenceScopes.Conversation;

    /// <summary>
    /// A structured reason from a closed list, when the user picked one.
    /// Free text lives in <see cref="Note"/> and is never a rule.
    /// </summary>
    [MaxLength(40)]
    public string? Reason { get; set; }

    /// <summary>
    /// An optional short comment, sanitised and capped.
    ///
    /// DATA, never an instruction. It is displayed back to its author and
    /// counted in aggregate; it never enters a system prompt, and nothing reads
    /// it looking for commands.
    /// </summary>
    [MaxLength(280)]
    public string? Note { get; set; }

    public int ContextVersion { get; set; } = CurrentContextVersion;

    /// "client" | "backend". Backend events are inferred from what happened
    /// (an apply, an edit); client events are something the user pressed.
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = "client";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// When the candidate builder last folded this into its arithmetic.
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// Set when a later event replaced this one's meaning — the user changed
    /// "not helpful" to "helpful", or corrected a preference they had just
    /// stated. The row survives; its influence stops.
    /// </summary>
    public DateTime? SupersededAt { get; set; }
}
