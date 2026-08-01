using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

public static class GlunoGroupDecisionKinds
{
    public const string Pace = "pace";
    public const string Budget = "budget";
    public const string Transport = "transport";
    public const string ActivityChoice = "activity_choice";
    public const string DayPlanChoice = "day_plan_choice";
    public const string RestaurantChoice = "restaurant_choice";
    public const string ExcursionDay = "excursion_day";
    public const string ProposalChoice = "proposal_choice";

    public static readonly IReadOnlyList<string> All =
    [
        Pace, Budget, Transport, ActivityChoice, DayPlanChoice,
        RestaurantChoice, ExcursionDay, ProposalChoice,
    ];

    public static bool IsKnown(string? kind) => kind != null && All.Contains(kind);
}

public static class GlunoGroupDecisionStatuses
{
    /// Open for votes.
    public const string Pending = "pending";
    /// The group settled on an option.
    public const string Accepted = "accepted";
    /// Closed without an outcome the group wanted.
    public const string Rejected = "rejected";
    /// Its closing time passed with no resolution.
    public const string Expired = "expired";
    /// A later decision replaced it.
    public const string Superseded = "superseded";

    public static bool IsOpen(string status) => status == Pending;

    public static bool IsTerminal(string status)
        => status is Accepted or Rejected or Expired or Superseded;
}

/// <summary>
/// Something the group is deciding together.
///
/// A GROUP DECISION IS NOT A PROPOSAL, and the distinction is the whole point.
/// A decision records what the group PREFERS — "we'd rather do Saturday in
/// Monaco". A proposal is the exact, validated change that could be written to
/// the Adventure. Accepting a decision never writes anything: somebody with
/// edit rights still has to review a proposal and tap apply.
///
/// Keeping them separate is what stops a vote from becoming a write. Five
/// people agreeing about a restaurant does not grant any of them permission to
/// modify the Adventure, and a poll must never be a path around the edit
/// checks.
/// </summary>
public class GlunoGroupDecision
{
    /// <summary>
    /// The shape of <see cref="OptionsJson"/> and the voting rules.
    ///
    /// Versioned because an old mobile client must not be able to corrupt a
    /// decision by voting against a shape it misunderstands — an unknown
    /// version is refused rather than interpreted.
    /// </summary>
    public const int CurrentVersion = 1;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;

    /// Who asked for the decision. Not an authority over its outcome.
    public Guid CreatedByUserId { get; set; }

    public int Version { get; set; } = CurrentVersion;

    /// <see cref="GlunoGroupDecisionKinds"/>.
    [Required]
    [MaxLength(30)]
    public string Kind { get; set; } = string.Empty;

    /// The question, already localised when it was created.
    [Required]
    [MaxLength(200)]
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// The options: <c>[{ id, label, summary }]</c>. Two to four — see
    /// GlunoPollRules for why more is worse.
    /// </summary>
    [Required]
    public string OptionsJson { get; set; } = "[]";

    /// <see cref="GlunoGroupDecisionStatuses"/>.
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = GlunoGroupDecisionStatuses.Pending;

    /// The winning option id, once there is one. Never written by a client.
    [MaxLength(40)]
    public string? AcceptedOptionId { get; set; }

    /// <summary>
    /// How this decision closes: "all_voted", "owner_closes", "deadline".
    /// Stated up front so nobody can move the finish line afterwards.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string ClosingRule { get; set; } = "owner_closes";

    public DateTime? ClosesAt { get; set; }

    /// <summary>
    /// Optimistic concurrency. Two people voting at once must not overwrite
    /// each other, and the row version is what makes the second write retry
    /// rather than silently win.
    /// </summary>
    [Timestamp]
    public uint RowVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// One member's vote.
///
/// One row per member per decision, enforced by a unique index. Changing a vote
/// updates the row rather than adding a second — and the RESULT is always
/// counted from these rows, never from a total a client sent. A client-supplied
/// tally is a client-supplied outcome.
/// </summary>
public class GlunoGroupVote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DecisionId { get; set; }
    public GlunoGroupDecision Decision { get; set; } = null!;

    /// <summary>
    /// Always taken from the authenticated principal. A userId in a request
    /// body is never trusted — that would let anyone vote as anyone.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The chosen option, or null for a deliberate abstention.
    ///
    /// Abstaining is a real answer and is stored as one. Silence is not: a
    /// member who never responded has no row, because absence of a reply must
    /// never be counted as agreement.
    /// </summary>
    [MaxLength(40)]
    public string? OptionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
