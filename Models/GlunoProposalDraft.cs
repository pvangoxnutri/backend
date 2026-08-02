using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

public static class GlunoProposalDraftStatuses
{
    /// The model has produced something; validation has not finished.
    public const string Building = "building";
    /// A conflict is on screen and the user has not chosen yet.
    public const string AwaitingClarification = "awaiting_clarification";
    /// Validated clean. This is the only status that may become a proposal.
    public const string ReadyForApproval = "ready_for_approval";
    public const string Applied = "applied";
    public const string Cancelled = "cancelled";
    /// The plan changed underneath it.
    public const string Stale = "stale";
    /// Rebuilt to the limit and still conflicting.
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All =
    [
        Building, AwaitingClarification, ReadyForApproval,
        Applied, Cancelled, Stale, Failed,
    ];

    public static bool IsKnown(string? status) => status != null && All.Contains(status);

    /// True while the draft can still change.
    public static bool IsOpen(string status)
        => status is Building or AwaitingClarification or ReadyForApproval;
}

/// <summary>
/// A suggestion mid-negotiation: built, not yet agreed, and not yet anything
/// the Adventure knows about.
///
/// WHY THIS IS A ROW AND NOT A PENDING PROPOSAL. A proposal is something the
/// user can apply. A draft is something Gluno is still working out with them —
/// it may conflict with the plan, be rebuilt two or three times, and never
/// become a proposal at all. Storing it as a proposal would put a half-resolved
/// suggestion in front of an Apply button, which is exactly the write this
/// whole flow exists to defer.
///
/// NOTHING HERE IS AN EF GRAPH. The payload is the same detached JSON shape a
/// proposal carries. A draft that held entities would serialise navigation
/// properties into the conversation and rot the moment the plan changed.
///
/// THE TWO VERSIONS EARN THEIR KEEP.
///
/// `DraftVersion` moves when the draft's own content changes, and apply
/// re-checks it: a plan approved from a card built ninety seconds ago must not
/// be written if the draft was rebuilt since.
///
/// `ConflictVersion` moves when the conflicts are recomputed. A tap carrying an
/// old one is answering a question about a plan that no longer exists, and
/// honouring it would resolve the wrong conflict.
/// </summary>
public class GlunoProposalDraft
{
    /// <summary>
    /// How many times a draft may be rebuilt before Gluno gives up.
    ///
    /// Three, because each rebuild costs a model round and a validation pass,
    /// and a conflict that survives three attempts is one the user should be
    /// told about plainly rather than watched fighting.
    /// </summary>
    public const int MaxRebuilds = 3;

    /// Long enough to think about, short enough that the plan it was built
    /// against is still the plan.
    public const int DefaultLifetimeMinutes = 60;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public GlunoConversation Conversation { get; set; } = null!;

    public Guid UserId { get; set; }
    public Guid TripId { get; set; }

    /// The question this draft answers. Replayed on continuation so the user
    /// never retypes it, and never stored twice.
    public Guid OriginalUserMessageId { get; set; }

    [MaxLength(48)]
    public string OriginalIntent { get; set; } = string.Empty;

    /// The allow-listed action this will become, e.g. "propose_day_plan".
    [MaxLength(60)]
    public string ActionType { get; set; } = string.Empty;

    /// <summary>
    /// The proposal payload as it currently stands — the same detached shape a
    /// GlunoProposalRecord carries, never an entity graph.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// Bumped on every content change. Apply re-checks it.
    public int DraftVersion { get; set; } = 1;

    /// Bumped whenever conflicts are recomputed. A tap carrying an older one
    /// is answering about a plan that has moved on.
    public int ConflictVersion { get; set; } = 1;

    /// <summary>
    /// How many times this has been rebuilt from a chosen strategy.
    ///
    /// The loop guard. Without it a conflict that each fix reintroduces would
    /// bounce between two cards until something else stopped it.
    /// </summary>
    public int RebuildCount { get; set; }

    /// <summary>
    /// The conflict and strategy last applied, so the same pair cannot be
    /// applied twice against unchanged state — which is the shape of an
    /// infinite loop that a simple counter alone would not catch until three
    /// wasted rounds had passed.
    /// </summary>
    [MaxLength(64)]
    public string? LastConflictType { get; set; }

    [MaxLength(48)]
    public string? LastStrategy { get; set; }

    /// <summary>
    /// Conflict types the user has explicitly accepted, comma-separated.
    ///
    /// WITHOUT THIS, "KEEP BOTH" IS AN INFINITE CARD. That strategy changes no
    /// content, so the gate re-runs and finds the identical conflict, and the
    /// user is asked the same question they just answered. Recording the
    /// acceptance is what makes the answer stick.
    ///
    /// Scoped to the conflict TYPE, not to "all conflicts": accepting a full
    /// day says nothing about a clash with a booking, and a blanket suppression
    /// would silently swallow the next real problem.
    /// </summary>
    [MaxLength(400)]
    public string? AcceptedConflictsJson { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = GlunoProposalDraftStatuses.Building;

    /// The proposal this finally became, once approved.
    public Guid? ProposalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(DefaultLifetimeMinutes);

    public bool IsUsable => GlunoProposalDraftStatuses.IsOpen(Status) && ExpiresAt > DateTime.UtcNow;

    /// True when another rebuild would exceed the limit.
    public bool IsOutOfRebuilds => RebuildCount >= MaxRebuilds;

    /// <summary>
    /// Whether this exact fix has already been tried on this exact conflict.
    ///
    /// Repeating it without the draft having changed cannot produce a different
    /// outcome, so it is refused rather than spent.
    /// </summary>
    public bool WouldRepeat(string conflictType, string strategy)
        => LastConflictType == conflictType && LastStrategy == strategy;

    /// The conflict types already accepted, as a set.
    public IReadOnlyList<string> AcceptedConflicts => string.IsNullOrWhiteSpace(AcceptedConflictsJson)
        ? Array.Empty<string>()
        : AcceptedConflictsJson.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasAccepted(string conflictType) => AcceptedConflicts.Contains(conflictType);

    /// <summary>
    /// Records an acceptance. Returns false when it was already recorded, so
    /// the caller can tell a state change from a repeat and only move the
    /// version when something actually moved.
    /// </summary>
    public bool Accept(string conflictType)
    {
        if (HasAccepted(conflictType)) return false;

        var accepted = AcceptedConflicts.Append(conflictType);
        AcceptedConflictsJson = string.Join(',', accepted);
        return true;
    }
}
