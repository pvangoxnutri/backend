namespace sidequest.backend.Models;

/// <summary>
/// The lifecycle of a Gluno proposal. Stored server-side, because the mobile
/// app's state is not a place where "has this already been applied?" can be
/// answered safely — two devices, a retry, or a double tap all have to reach
/// the same answer.
/// </summary>
public static class GlunoProposalStatuses
{
    /// Waiting for the user. The only status from which apply or reject is
    /// allowed, and the only one whose payload may be edited.
    public const string Pending = "pending";
    /// An apply is in flight. Claimed atomically, which is what makes a double
    /// tap a no-op instead of a duplicate Activity.
    public const string Applying = "applying";
    /// Done. Terminal — never returns to pending.
    public const string Applied = "applied";
    /// Dismissed by the user. Terminal.
    public const string Rejected = "rejected";
    /// The apply was attempted and did not succeed. NOT terminal: the failure
    /// may be transient, so the user can try again.
    public const string Failed = "failed";
    /// The Adventure changed after the proposal was created, so applying it
    /// would act on data the user never saw. Terminal — Gluno makes a new one.
    public const string Stale = "stale";

    public static bool IsTerminal(string status)
        => status is Applied or Rejected or Stale;
}

/// <summary>
/// One structured change Gluno proposed, as a first-class row.
///
/// WHY A TABLE. Before this, a proposal lived only inside the assistant
/// message's payload — fine for rendering, useless for acting. Applying needs
/// an identity that can be claimed exactly once, a status two devices agree
/// on, an owner to authorise against, and a record of what actually happened.
/// None of that can live in the client.
///
/// WHAT IT IS NOT. It is not a queue: nothing here is applied in the
/// background, ever. A proposal changes the Adventure only when the user taps
/// apply, and only through <c>GlunoProposalApplyService</c>, which re-validates
/// everything against the live database at that moment.
/// </summary>
public class GlunoProposalRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public GlunoConversation Conversation { get; set; } = null!;

    /// The assistant turn this proposal was attached to, so the chat can render
    /// it in place when the conversation is reloaded.
    public Guid MessageId { get; set; }

    /// The owner. Authorisation compares this against the JWT's subject — the
    /// user id is never taken from the model or from the request body.
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// The Adventure this acts on. Null only for proposal kinds that do not
    /// touch a trip; today every action type requires one.
    public Guid? TripId { get; set; }
    public Trip? Trip { get; set; }

    /// One of the allow-listed action names (GlunoActions.*). Anything else is
    /// refused at apply time — an unknown type is never "probably fine".
    public string ActionType { get; set; } = string.Empty;

    /// The one-line heading the card shows. Stored rather than recomputed so a
    /// proposal reads the same months later as it did when it was made, even
    /// if the wording elsewhere changes.
    public string Summary { get; set; } = string.Empty;

    /// Schema version of <see cref="PayloadJson"/>. Stamped at creation so a
    /// proposal written by an older build is recognisable rather than being
    /// silently misread by newer parsing code.
    public int PayloadVersion { get; set; } = GlunoProposalPayloadVersions.Current;

    /// The validated parameters, as JSON. Never the model's raw arguments —
    /// what is stored here has already been through the action executor's
    /// validation, and it is re-validated again at apply.
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// What the relevant part of the Adventure looked like when the proposal
    /// was made. Compared again at apply: if someone else moved, added or
    /// re-dated the things this proposal depends on, it becomes
    /// <see cref="GlunoProposalStatuses.Stale"/> rather than being applied to
    /// a plan the user never reviewed.
    /// </summary>
    public string? SnapshotJson { get; set; }

    /// <summary>
    /// The draft this proposal was built from, and the draft's content version
    /// at that moment.
    ///
    /// Apply re-checks both. A draft rebuilt after this proposal was created —
    /// a second conflict answered in another tab, a later continuation — means
    /// the card in front of the user describes a plan that no longer exists,
    /// and writing it would apply an answer to a superseded question.
    ///
    /// NULL IS LEGACY, NOT AN ESCAPE HATCH. Proposals created before the draft
    /// flow existed carry no reference and are applied on the snapshot check
    /// alone. Every proposal a conflict produces carries both.
    /// </summary>
    public Guid? DraftId { get; set; }

    public int? DraftVersion { get; set; }

    public string Status { get; set; } = GlunoProposalStatuses.Pending;

    /// Machine-readable reason for a failed or stale proposal, e.g.
    /// "trip_changed". Never a provider or database error message.
    public string? FailureCode { get; set; }

    /// Ids created or changed by a successful apply, as JSON. Lets the app
    /// jump straight to what was made without a second round trip.
    public string? ResultJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
}

public static class GlunoProposalPayloadVersions
{
    /// <summary>
    /// Bump when a payload shape changes incompatibly.
    ///
    /// 1 — the shapes produced by GlunoActionExecutor's propose_* actions.
    /// </summary>
    public const int Current = 1;
}
