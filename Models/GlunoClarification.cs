using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

/// <summary>
/// The kinds of choice Gluno can put in front of somebody.
///
/// A CLOSED list. The model may ask for one of these; it cannot invent a new
/// kind, because every kind corresponds to a builder that knows how to produce
/// real options from real data.
/// </summary>
public static class GlunoClarificationTypes
{
    /// Which Adventure the question is about.
    public const string Adventure = "adventure";
    /// Which day — "on Friday" when the trip has two Fridays.
    public const string Day = "day";
    /// Which Activity — two museums, two dinners with the same name.
    public const string Activity = "activity";
    /// Which of the places just recommended.
    public const string Place = "place";
    public const string TransportMode = "transport_mode";
    public const string Pace = "pace";
    public const string Budget = "budget";
    /// Where a preference should apply.
    public const string PreferenceScope = "preference_scope";
    /// Move it, drop it, or pick another day.
    public const string ProposalConflict = "proposal_conflict";

    /// <summary>
    /// Which time an activity should start at.
    ///
    /// A closed vocabulary of HH:mm values the SCHEDULE ENGINE produced, never
    /// the model. Every offered time has already been checked against the
    /// activity's length, the day's bookings, the journey either side and the
    /// opening hours — so tapping one cannot fail.
    /// </summary>
    public const string ActivityTime = "activity_time";

    /// <summary>
    /// Which part of the trip — a city and the days it covers.
    ///
    /// "What should we see?" on a six-city trip is six different questions.
    /// Answering about all of them at once is a wall of text; guessing one is
    /// planning the wrong city.
    /// </summary>
    public const string RouteStop = "route_stop";

    /// <summary>
    /// Which journey — "Málaga → Ronda".
    ///
    /// "Anything worth stopping at on the way?" is a question about the space
    /// BETWEEN two stops, and on a multi-stop trip there is more than one such
    /// space.
    /// </summary>
    public const string RouteLeg = "route_leg";

    public static readonly IReadOnlyList<string> All =
    [
        Adventure, Day, Activity, Place, TransportMode,
        Pace, Budget, PreferenceScope, ProposalConflict, ActivityTime,
        RouteStop, RouteLeg,
    ];

    public static bool IsKnown(string? type) => type != null && All.Contains(type);
}

public static class GlunoClarificationStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    /// The options referred to something that has since changed.
    public const string Stale = "stale";

    public static bool IsOpen(string status) => status == Pending;
}

/// <summary>
/// What an option points AT.
///
/// The entity type decides which check runs before the choice is honoured —
/// an Adventure option re-verifies membership, an Activity option re-verifies
/// the Activity still exists. Without it, resolving would be a lookup by id
/// with no authorisation attached.
/// </summary>
public static class GlunoClarificationEntityTypes
{
    public const string Trip = "trip";
    public const string Activity = "activity";
    public const string Date = "date";
    public const string ExternalPlace = "external_place";
    /// A fixed vocabulary value — a transport mode, a pace, a scope. No entity
    /// behind it, so nothing to re-check beyond the allow-list.
    public const string Enum = "enum";

    public static readonly IReadOnlyList<string> All =
        [Trip, Activity, Date, ExternalPlace, Enum];

    public static bool IsKnown(string? type) => type != null && All.Contains(type);
}

/// <summary>
/// One tappable choice.
///
/// Everything here is either a stable id the backend produced or a label the
/// backend wrote. Nothing on this type comes from the model, which is what
/// stops a suggested option from pointing at an Adventure the user cannot see.
/// </summary>
public class GlunoClarificationOption
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClarificationId { get; set; }
    public GlunoClarification Clarification { get; set; } = null!;

    /// Stable within its clarification. What the client sends back.
    [MaxLength(64)]
    public string OptionKey { get; set; } = string.Empty;

    [MaxLength(160)]
    public string Label { get; set; } = string.Empty;

    /// Dates, a destination summary, a time — the second line on the row.
    [MaxLength(240)]
    public string? Description { get; set; }

    /// A stable icon name from the app's own set, never a URL.
    [MaxLength(48)]
    public string? Icon { get; set; }

    /// See <see cref="GlunoClarificationEntityTypes"/>.
    [MaxLength(32)]
    public string EntityType { get; set; } = GlunoClarificationEntityTypes.Enum;

    /// The row this points at. Null for an enum option.
    public Guid? EntityId { get; set; }

    /// <summary>
    /// The value the continuation uses: an ISO date, a transport mode, a
    /// namespaced external id. Never a route, a URL or a query.
    /// </summary>
    [MaxLength(200)]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Shown but not selectable — an Adventure that ended, a day already full.
    /// Kept visible rather than hidden so the list does not silently change
    /// shape between the question and the answer.
    /// </summary>
    public bool Disabled { get; set; }

    /// A localised sentence written by the backend, never a raw status.
    [MaxLength(160)]
    public string? DisabledReason { get; set; }

    public int SortIndex { get; set; }
}

/// <summary>
/// A question with tappable answers, and the means to carry on afterwards.
///
/// WHY THIS IS A ROW AND NOT JUST A MESSAGE PAYLOAD. Three things need to
/// outlive the turn that asked. The options must still be verifiable when the
/// user taps — an Adventure can be deleted, a member can leave, and the check
/// has to run against the state at TAP time, not at ASK time. The choice must
/// be resolvable exactly once, which needs a status somebody can transition
/// atomically. And the original question has to be recoverable so the user
/// never types it twice.
///
/// WHAT IS DELIBERATELY NOT STORED: the user's message text. The reference to
/// the message row is enough, and keeping a second copy of what somebody typed
/// is a second thing to leak.
/// </summary>
public class GlunoClarification
{
    /// How long a question stays answerable. Long enough to put the phone
    /// down mid-thought, short enough that the options still describe reality.
    public const int DefaultLifetimeMinutes = 60;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public GlunoConversation Conversation { get; set; } = null!;

    public Guid UserId { get; set; }

    /// Set when the clarification is already scoped to an Adventure. Null when
    /// the whole point of the question is to choose one.
    public Guid? TripId { get; set; }

    /// <summary>
    /// The user turn this is asking about. The continuation replays THAT
    /// question — the user does not send it again, and it is not stored twice.
    /// </summary>
    public Guid OriginalUserMessageId { get; set; }

    /// The assistant message this clarification was attached to, so the app
    /// can render the card in the right place in the history.
    public Guid? MessageId { get; set; }

    [MaxLength(32)]
    public string Type { get; set; } = string.Empty;

    /// Short, and written by the backend or the model within the response
    /// contract. "Which of your Adventures do you mean?"
    [MaxLength(200)]
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// The intent the original question had, so the continuation resumes the
    /// same task rather than re-classifying and possibly landing somewhere
    /// else.
    /// </summary>
    [MaxLength(48)]
    public string OriginalIntent { get; set; } = string.Empty;

    /// True when the answer genuinely might not be in the list.
    public bool AllowFreeText { get; set; }

    /// Reserved for choices that are genuinely multiple. Single by default:
    /// most of these are "which one", and a multi-select that offers no real
    /// choice is just a slower tap.
    public bool MultiSelect { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = GlunoClarificationStatuses.Pending;

    /// Which option the user picked. Null until resolved.
    public Guid? SelectedOptionId { get; set; }

    /// <summary>
    /// The assistant message the continuation produced.
    ///
    /// This is what makes a repeated tap idempotent: the second tap returns
    /// the first tap's answer instead of running the turn again.
    /// </summary>
    public Guid? ContinuationMessageId { get; set; }

    /// Which context shape built the options, so a future build can tell a
    /// stale snapshot from a current one.
    public int ContextVersion { get; set; } = 1;

    // ── Questions about a place that may not be stored ───────────────────

    /// <summary>
    /// True when this question's real wording could not be written down.
    ///
    /// A provider whose terms forbid storing its content forbids it in prose
    /// too: "Which day should Real Alcázar go on?" is that name, stored. So the
    /// row holds a neutral version, the live response carries the real one, and
    /// this flag says the two differ.
    ///
    /// HISTORY DOES NOT REBUILD IT. A reopened conversation would otherwise
    /// show "Which of the places do you mean?" above rows reading "Option 1"
    /// and "Option 2" — a question nobody can answer, still holding an Apply
    /// path open. It is dropped from the history instead.
    /// </summary>
    public bool ContentSuppressed { get; set; }

    /// <summary>
    /// The turn that showed the place this question is about, and which of its
    /// cards.
    ///
    /// SERVER-OWNED STATE FOR THE CONTINUATION. Answering "Thursday" has to
    /// resume adding a specific place, and the place cannot travel through the
    /// answer: the client sends a clarification id and an option key, and the
    /// place's own identity lives here where the client cannot see or change
    /// it. Without this the continuation would have to replay a sentence
    /// through the model and hope it worked out which place was meant.
    ///
    /// Both null on every other kind of question.
    /// </summary>
    public Guid? PlaceMessageId { get; set; }

    [MaxLength(64)]
    public string? PlaceOptionKey { get; set; }

    // ── Proposal-conflict state ──────────────────────────────────────────
    //
    // Server-owned, every one of them. The client sends back a clarification
    // id and an option key and nothing else — a version number or a draft id
    // travelling from the app would be a number the app could change, and the
    // whole point of the versions is that they cannot be.

    /// The draft this conflict is about. Null on every other type.
    public Guid? DraftId { get; set; }

    /// The draft's content version when this question was asked.
    public int? DraftVersion { get; set; }

    /// The conflict-set version when this question was asked.
    public int? ConflictVersion { get; set; }

    /// <see cref="GlunoConflictTypes"/>. Null on every other type.
    [MaxLength(64)]
    public string? ConflictType { get; set; }

    /// <summary>
    /// What the card displays about the clash — the day, the times, the titles
    /// involved, whether the other thing is locked. Serialised as the app-facing
    /// DTO shape.
    ///
    /// Stored rather than recomputed because the conflict was derived from the
    /// draft payload as it stood when the question was asked, and re-deriving it
    /// at render time would describe a different plan.
    ///
    /// NO IDS AND NO VERSIONS GO IN HERE. This is the one part of the
    /// clarification that reaches the client, and everything the resolve is
    /// checked against must stay where the client cannot see or change it.
    /// </summary>
    [MaxLength(1000)]
    public string? ConflictMetaJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(DefaultLifetimeMinutes);

    public List<GlunoClarificationOption> Options { get; set; } = new();

    public bool IsAnswerable => GlunoClarificationStatuses.IsOpen(Status) && ExpiresAt > DateTime.UtcNow;
}
