using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One constraint in the group's planning profile.
///
/// <see cref="MemberRef"/> is a NEUTRAL identifier — "member-2" — not a name
/// and not a user id. It exists so the planner can tell two people's
/// constraints apart and spread priorities fairly, and stops there. Sending a
/// name to a model is how "member 2 needs short walks" becomes "Anna can't
/// walk far" in an answer the whole group reads.
/// </summary>
public sealed record GroupConstraint
{
    public required string Key { get; init; }
    public required string Value { get; init; }

    /// "member-1", "member-2". Stable within one profile build, meaningless
    /// outside it.
    public required string MemberRef { get; init; }

    /// <summary>
    /// A hard requirement cannot be outvoted. A soft one can lose gracefully.
    /// This single flag is what stops a majority from voting away somebody's
    /// mobility needs.
    /// </summary>
    public bool IsHard { get; init; }

    /// Always <see cref="GlunoPreferenceVisibility.TripShared"/> here — nothing
    /// else reaches this type. Carried so the rule is visible at the point of
    /// use rather than only at the query.
    public required string Visibility { get; init; }

    /// "preference" | "group_decision" | "poll_result".
    public required string Source { get; init; }

    public double Confidence { get; init; } = 1;
    public DateTime? ConfirmedAt { get; init; }

    /// ISO date when the constraint is about one day rather than the trip.
    public string? AppliesToDate { get; init; }
}

/// <summary>
/// What the group has actually agreed, as opposed to what individuals want.
/// </summary>
public sealed record GroupDecisionSummary(
    Guid Id,
    string Kind,
    string Status,
    string? AcceptedOptionLabel,
    int Version)
{
    /// True only when the decision reached <c>accepted</c>. Anything else — and
    /// Gluno may not say the group has decided.
    public bool IsSettled => Status == GlunoGroupDecisionStatuses.Accepted;
}

/// <summary>
/// Everything the group's planning may use, and nothing else.
///
/// THE INVARIANT THIS TYPE ENFORCES: only <c>trip_shared</c> preferences get in.
/// A private preference is useful to the planner — that is exactly why the
/// temptation exists — and it still does not enter, because the person who
/// stated it did not agree to that. The filter lives in the QUERY, not in a
/// later check, so there is no code path where private data is loaded and then
/// remembered not to be used.
///
/// Names never appear. Members are numbered.
/// </summary>
public sealed record TripPlanningProfile
{
    /// Bumped when the shape changes, so proposal grounding can record which
    /// version a plan was built against.
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required Guid TripId { get; init; }

    /// <summary>
    /// How many people are on the Adventure — not how many shared anything.
    /// A group of five where two answered is still a group of five, and a plan
    /// that forgets that is a plan for two.
    /// </summary>
    public required int GroupSize { get; init; }

    /// How many distinct members contributed something.
    public required int ContributingMembers { get; init; }

    public IReadOnlyList<GroupConstraint> Constraints { get; init; } = Array.Empty<GroupConstraint>();

    /// Decisions the group has actually settled.
    public IReadOnlyList<GroupDecisionSummary> Decisions { get; init; } = Array.Empty<GroupDecisionSummary>();

    public DateTime BuiltAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// A single member's Adventure. Group machinery — polls, fairness, conflict
    /// detection — is noise here, and Gluno should plan exactly as it always has.
    /// </summary>
    public bool IsSoloTrip => GroupSize <= 1;

    public IEnumerable<GroupConstraint> Hard => Constraints.Where(constraint => constraint.IsHard);
    public IEnumerable<GroupConstraint> Soft => Constraints.Where(constraint => !constraint.IsHard);

    public IEnumerable<GroupConstraint> ForKey(string key)
        => Constraints.Where(constraint => constraint.Key == key);

    /// <summary>
    /// The settled value for a key, when the group actually decided it.
    ///
    /// Returns null for a pending or expired decision — Gluno must not say the
    /// group has chosen something until the decision reached its own defined
    /// status.
    /// </summary>
    public string? SettledDecision(string kind)
        => Decisions.FirstOrDefault(decision => decision.Kind == kind && decision.IsSettled)
            ?.AcceptedOptionLabel;

    /// <summary>
    /// What goes into the prompt.
    ///
    /// Neutral member refs, the hard/soft split, and nothing else. Not names,
    /// not user ids, not which preference came from whose private conversation.
    /// </summary>
    public object ForPrompt() => new
    {
        version = Version,
        groupSize = GroupSize,
        contributingMembers = ContributingMembers,
        // Numbers only. "3 of 5 shared something" tells the planner what it
        // needs; who the other two are does not.
        hardConstraints = Hard.Select(constraint => new
        {
            constraint.Key,
            constraint.Value,
            constraint.MemberRef,
            constraint.AppliesToDate,
        }).ToList(),
        softPreferences = Soft.Select(constraint => new
        {
            constraint.Key,
            constraint.Value,
            constraint.MemberRef,
            constraint.AppliesToDate,
        }).ToList(),
        settledDecisions = Decisions
            .Where(decision => decision.IsSettled)
            .Select(decision => new { decision.Kind, decision.AcceptedOptionLabel })
            .ToList(),
        openDecisions = Decisions
            .Where(decision => decision.Status == GlunoGroupDecisionStatuses.Pending)
            .Select(decision => new { decision.Kind, decision.Id })
            .ToList(),
    };
}

public interface ITripPlanningProfileBuilder
{
    Task<TripPlanningProfile> BuildAsync(Guid tripId, CancellationToken ct);
}

/// <summary>
/// Assembles the group profile from shared preferences and settled decisions.
/// </summary>
public sealed class TripPlanningProfileBuilder : ITripPlanningProfileBuilder
{
    private readonly AppDbContext _db;

    public TripPlanningProfileBuilder(AppDbContext db) => _db = db;

    public async Task<TripPlanningProfile> BuildAsync(Guid tripId, CancellationToken ct)
    {
        var memberIds = await _db.TripMembers
            .AsNoTracking()
            .Where(member => member.TripId == tripId)
            .Select(member => member.UserId)
            .ToListAsync(ct);

        // ── Only trip_shared, filtered IN THE QUERY ───────────────────────
        //
        // Deliberately not "load everything and filter later". A private
        // preference that is never fetched cannot be leaked by a later bug,
        // and this is the one place where that distinction is worth the
        // slightly less flexible code.
        var shared = await _db.GlunoPreferences
            .AsNoTracking()
            .Where(preference => preference.TripId == tripId)
            .Where(preference => preference.Visibility == GlunoPreferenceVisibility.TripShared)
            // A member who left takes their contributions with them.
            .Where(preference => memberIds.Contains(preference.UserId))
            .ToListAsync(ct);

        // Neutral refs, assigned in a stable order so the same member is
        // "member-2" across a whole planning session — and means nothing
        // outside it.
        var refs = memberIds
            .OrderBy(id => id)
            .Select((id, index) => (Id: id, Ref: $"member-{index + 1}"))
            .ToDictionary(pair => pair.Id, pair => pair.Ref);

        var constraints = shared
            .Select(preference => new GroupConstraint
            {
                Key = preference.Key,
                Value = preference.Value,
                MemberRef = refs.GetValueOrDefault(preference.UserId, "member-?"),
                IsHard = preference.IsHardConstraint,
                Visibility = preference.Visibility,
                Source = "preference",
                ConfirmedAt = preference.ConfirmedAt ?? preference.UpdatedAt,
            })
            .ToList();

        var decisions = await _db.GlunoGroupDecisions
            .AsNoTracking()
            .Where(decision => decision.TripId == tripId)
            .OrderByDescending(decision => decision.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        return new TripPlanningProfile
        {
            TripId = tripId,
            GroupSize = memberIds.Count,
            ContributingMembers = shared.Select(preference => preference.UserId).Distinct().Count(),
            Constraints = constraints,
            Decisions = decisions
                .Select(decision => new GroupDecisionSummary(
                    decision.Id,
                    decision.Kind,
                    decision.Status,
                    GlunoPollOptions.LabelOf(decision.OptionsJson, decision.AcceptedOptionId),
                    decision.Version))
                .ToList(),
        };
    }
}
