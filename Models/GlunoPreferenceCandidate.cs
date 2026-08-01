using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

public static class GlunoCandidateStatuses
{
    /// Seen once or twice. Not enough to ask about, and it shapes nothing.
    public const string Observing = "observing";
    /// Enough repetition that asking is worth a turn of the user's attention.
    public const string ReadyToConfirm = "ready_to_confirm";
    /// The user said yes. A real preference now exists; this row is history.
    public const string Confirmed = "confirmed";
    /// The user said no. Never asked again.
    public const string Rejected = "rejected";
    /// Went quiet. A pattern from two months ago is not a pattern.
    public const string Expired = "expired";
    /// The user stated the preference outright, so inferring it is moot.
    public const string Superseded = "superseded";

    public static bool IsActive(string status) => status is Observing or ReadyToConfirm;
}

/// <summary>
/// A preference Gluno THINKS it has noticed, and has not earned the right to
/// assume.
///
/// THE FAILURE THIS TYPE PREVENTS. Someone moves one morning's start from 08:00
/// to 10:00. A naive system stores "prefers late starts" and plans every
/// subsequent day from ten — including the day with the 07:00 ferry. One tap
/// became a rule about a person, and they never agreed to it and cannot see it.
///
/// So an observation is a CANDIDATE. It accumulates evidence, it never shapes a
/// plan on its own, and it becomes a preference only when the user says yes to
/// a plain question. The status field is the whole mechanism: nothing below
/// <see cref="GlunoCandidateStatuses.Confirmed"/> is allowed to influence
/// anything.
///
/// KEYS ARE THE EXISTING ALLOW-LIST. No new vocabulary, and emphatically no
/// psychological or sensitive inference — "prefers a later start" is a planning
/// fact, "anxious in crowds" is a profile, and this table only ever holds the
/// first kind.
/// </summary>
public class GlunoPreferenceCandidate
{
    /// <summary>
    /// How many independent observations before asking is worth the user's
    /// attention.
    ///
    /// Three, deliberately. Two is a coincidence — a late start on two
    /// consecutive mornings might be one lie-in and one delayed train. Asking
    /// too early is worse than asking late: a wrong guess presented as a
    /// question still tells someone you have been watching.
    /// </summary>
    public const int EvidenceThreshold = 3;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// Null for a global candidate. Present for anything trip-scoped.
    public Guid? TripId { get; set; }

    public Guid? ConversationId { get; set; }

    /// <summary>
    /// From <see cref="GlunoPreferenceKeys"/> — the same closed list a stated
    /// preference uses. There is no path that invents a key.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Key { get; set; } = string.Empty;

    /// What Gluno would store, in the same shape a stated preference uses.
    [Required]
    [MaxLength(240)]
    public string ProposedValue { get; set; } = string.Empty;

    /// <summary>
    /// conversation | trip | global.
    ///
    /// Defaults to the NARROWEST reading that fits what was observed. A pattern
    /// seen on one Adventure is evidence about that Adventure; promoting it to
    /// "how this person always travels" needs the user to say so explicitly.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Scope { get; set; } = GlunoPreferenceScopes.Trip;

    public int EvidenceCount { get; set; }

    public DateTime FirstObservedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastObservedAt { get; set; } = DateTime.UtcNow;

    /// 0–1, from evidence count and consistency. Never shown to the user.
    public double Confidence { get; set; }

    /// Comma-separated feedback types that contributed. For auditing why a
    /// candidate exists, not for display.
    [MaxLength(200)]
    public string SourceEventTypes { get; set; } = string.Empty;

    /// <see cref="GlunoCandidateStatuses"/>.
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = GlunoCandidateStatuses.Observing;

    /// <summary>
    /// When the user was last asked. Prevents asking twice about the same
    /// thing in one planning session.
    /// </summary>
    public DateTime? AskedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class GlunoRejectionKinds
{
    /// A specific place, by namespaced provider id.
    public const string Place = "place";
    public const string ActivityProposal = "activity_proposal";
    public const string DayPlan = "day_plan";
    public const string TransportMode = "transport_mode";
    public const string TimeWindow = "time_window";
    public const string BudgetLevel = "budget_level";
    /// A CATEGORY of activity, not one instance of it.
    public const string ActivityType = "activity_type";

    public static readonly IReadOnlyList<string> All =
    [
        Place, ActivityProposal, DayPlan, TransportMode, TimeWindow, BudgetLevel, ActivityType,
    ];

    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind);
}

/// <summary>
/// Something the user said no to, remembered narrowly and briefly.
///
/// THE SCOPE RULE THAT MATTERS MOST: a rejected café is a rejected CAFÉ. It is
/// not evidence that the person dislikes cafés, or coffee, or that
/// neighbourhood. Widening a specific no into a general dislike is the single
/// most common way a recommender becomes useless — it runs out of things to
/// suggest and the user cannot work out why.
///
/// Rejections also EXPIRE. "Not that one today" is usually about today: the
/// wrong side of town for this afternoon, too expensive for this particular
/// meal. Six weeks later it is not information about anything.
/// </summary>
public class GlunoRejection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public Guid? TripId { get; set; }
    public Guid? ConversationId { get; set; }

    /// <see cref="GlunoRejectionKinds"/>.
    [Required]
    [MaxLength(30)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The namespaced provider id, proposal id, or category key. An
    /// IDENTIFIER — never a display name, so a log or an export cannot reveal
    /// where somebody chose not to go.
    /// </summary>
    [Required]
    [MaxLength(120)]
    public string Reference { get; set; } = string.Empty;

    /// The user's own stated reason, when they gave one. "Too expensive"
    /// narrows what to suggest next in a way a bare no does not.
    [MaxLength(40)]
    public string? Reason { get; set; }

    /// <summary>
    /// The date the rejection was about, when it was about one.
    ///
    /// A "no" for Tuesday says nothing about Friday, and a rejection tied to a
    /// date stops applying once that date is past.
    /// </summary>
    public DateOnly? ForDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this stops suppressing suggestions.
    ///
    /// Always set. An open-ended rejection quietly shrinks what Gluno can ever
    /// offer, and nobody would connect that to a tap they made in May.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(30);
}
