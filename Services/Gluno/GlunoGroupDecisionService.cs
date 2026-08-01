using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoGroupError
{
    None,
    NotFound,
    Forbidden,
    NotOpen,
    InvalidOption,
    InvalidOptions,
    UnknownVersion,
    Conflict,
}

public sealed record GlunoGroupDecisionResult(
    GlunoGroupError Error,
    GlunoGroupDecision? Decision)
{
    public GlunoPollResult? Result { get; init; }
    /// Machine codes when the options were refused: "leading_options", …
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

public interface IGlunoGroupDecisionService
{
    Task<GlunoGroupDecisionResult> CreateAsync(
        Guid tripId, Guid userId, string kind, string question,
        IReadOnlyList<GlunoPollOption> options, string closingRule, DateTime? closesAt, CancellationToken ct);

    Task<GlunoGroupDecisionResult> VoteAsync(
        Guid decisionId, Guid userId, string? optionId, CancellationToken ct);

    Task<GlunoGroupDecisionResult> GetAsync(Guid decisionId, Guid userId, CancellationToken ct);

    Task<GlunoGroupDecisionResult> CloseAsync(Guid decisionId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<GlunoGroupDecision>> ListAsync(Guid tripId, Guid userId, CancellationToken ct);
}

/// <summary>
/// Group decisions and the votes that settle them.
///
/// THREE RULES THAT ARE NOT NEGOTIABLE HERE.
///
/// The acting user always comes from the authenticated principal. A userId in a
/// request body is never read — that would let anyone vote as anyone, and there
/// is no legitimate reason for a client to name a different voter.
///
/// The result is always counted from the vote ROWS. A tally a client sent is a
/// result a client chose, and a poll whose outcome can be posted is not a poll.
///
/// A decision NEVER writes to the Adventure. Accepting one records what the
/// group prefers; turning that into a change is a separate proposal that
/// somebody with edit rights reviews and applies. Five people agreeing about a
/// restaurant does not grant any of them permission to modify the trip.
/// </summary>
public sealed class GlunoGroupDecisionService : IGlunoGroupDecisionService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GlunoGroupDecisionService> _logger;

    public GlunoGroupDecisionService(AppDbContext db, ILogger<GlunoGroupDecisionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoGroupDecisionResult> CreateAsync(
        Guid tripId,
        Guid userId,
        string kind,
        string question,
        IReadOnlyList<GlunoPollOption> options,
        string closingRule,
        DateTime? closesAt,
        CancellationToken ct)
    {
        if (!await IsMemberAsync(tripId, userId, ct))
            return new GlunoGroupDecisionResult(GlunoGroupError.Forbidden, null);

        if (!GlunoGroupDecisionKinds.IsKnown(kind))
            return new GlunoGroupDecisionResult(GlunoGroupError.InvalidOptions, null)
            {
                Problems = ["unknown_kind"],
            };

        // Clamped BEFORE validation, so an over-long list becomes a valid poll
        // rather than a rejection the user has to work around.
        var clamped = GlunoPollRules.Clamp(options);
        var problems = GlunoPollRules.Validate(clamped);

        if (problems.Count > 0)
        {
            // Logged as codes. The option text is Gluno's phrasing of somebody's
            // holiday and does not belong in a log line.
            _logger.LogInformation("[GLUNO] poll rejected: {Problems}", string.Join(',', problems));
            return new GlunoGroupDecisionResult(GlunoGroupError.InvalidOptions, null) { Problems = problems };
        }

        // A newer decision of the same kind replaces the older one. Two open
        // polls about the pace would split the group's answers in half.
        var previous = await _db.GlunoGroupDecisions
            .Where(decision => decision.TripId == tripId
                && decision.Kind == kind
                && decision.Status == GlunoGroupDecisionStatuses.Pending)
            .ToListAsync(ct);

        foreach (var stale in previous)
        {
            stale.Status = GlunoGroupDecisionStatuses.Superseded;
            stale.UpdatedAt = DateTime.UtcNow;
        }

        var created = new GlunoGroupDecision
        {
            TripId = tripId,
            CreatedByUserId = userId,
            Kind = kind,
            Question = question.Length > 200 ? question[..200] : question,
            OptionsJson = JsonSerializer.Serialize(clamped, GlunoJson.Options),
            ClosingRule = closingRule is "all_voted" or "owner_closes" or "deadline" ? closingRule : "owner_closes",
            ClosesAt = closesAt,
        };

        _db.GlunoGroupDecisions.Add(created);
        await _db.SaveChangesAsync(ct);

        return new GlunoGroupDecisionResult(GlunoGroupError.None, created)
        {
            Result = await TallyAsync(created, ct),
        };
    }

    public async Task<GlunoGroupDecisionResult> VoteAsync(
        Guid decisionId, Guid userId, string? optionId, CancellationToken ct)
    {
        var decision = await _db.GlunoGroupDecisions
            .FirstOrDefaultAsync(candidate => candidate.Id == decisionId, ct);

        if (decision == null) return new GlunoGroupDecisionResult(GlunoGroupError.NotFound, null);

        // Membership re-checked on every vote, not trusted from when the poll
        // was created. Somebody removed from the Adventure loses their say
        // immediately.
        if (!await IsMemberAsync(decision.TripId, userId, ct))
            return new GlunoGroupDecisionResult(GlunoGroupError.Forbidden, null);

        // An old client voting against a shape it does not understand could
        // corrupt the decision. Refuse rather than interpret.
        if (decision.Version != GlunoGroupDecision.CurrentVersion)
            return new GlunoGroupDecisionResult(GlunoGroupError.UnknownVersion, decision);

        if (!GlunoGroupDecisionStatuses.IsOpen(decision.Status))
            return new GlunoGroupDecisionResult(GlunoGroupError.NotOpen, decision);

        // null is a deliberate abstention and is allowed. An option id that is
        // not on the poll is not.
        var options = GlunoPollOptions.Parse(decision.OptionsJson);
        if (optionId != null && options.All(option => option.Id != optionId))
            return new GlunoGroupDecisionResult(GlunoGroupError.InvalidOption, decision);

        var existing = await _db.GlunoGroupVotes
            .FirstOrDefaultAsync(vote => vote.DecisionId == decisionId && vote.UserId == userId, ct);

        if (existing == null)
        {
            _db.GlunoGroupVotes.Add(new GlunoGroupVote
            {
                DecisionId = decisionId,
                // ALWAYS the authenticated principal. Never a body field.
                UserId = userId,
                OptionId = optionId,
            });
        }
        else
        {
            // Changing a vote updates the row. A second row would double-count
            // one person.
            existing.OptionId = optionId;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody resolved the poll while this vote was in flight. The
            // caller re-reads rather than the write silently winning.
            _db.ChangeTracker.Clear();
            return new GlunoGroupDecisionResult(GlunoGroupError.Conflict, null);
        }
        catch (DbUpdateException)
        {
            // The unique index caught two simultaneous first votes from one
            // user — a double tap. The existing row stands.
            _db.ChangeTracker.Clear();
        }

        var refreshed = await _db.GlunoGroupDecisions
            .FirstOrDefaultAsync(candidate => candidate.Id == decisionId, ct);

        if (refreshed == null) return new GlunoGroupDecisionResult(GlunoGroupError.NotFound, null);

        var tally = await TallyAsync(refreshed, ct);

        // Auto-close only under the rule the poll was created with. Moving the
        // finish line afterwards is how a result stops meaning anything.
        if (refreshed.ClosingRule == "all_voted" && tally.EveryoneResponded && !tally.IsTie)
        {
            await ResolveAsync(refreshed, tally, ct);
        }

        return new GlunoGroupDecisionResult(GlunoGroupError.None, refreshed) { Result = tally };
    }

    public async Task<GlunoGroupDecisionResult> GetAsync(Guid decisionId, Guid userId, CancellationToken ct)
    {
        var decision = await _db.GlunoGroupDecisions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == decisionId, ct);

        if (decision == null) return new GlunoGroupDecisionResult(GlunoGroupError.NotFound, null);

        // Someone who has left cannot read the result either.
        if (!await IsMemberAsync(decision.TripId, userId, ct))
            return new GlunoGroupDecisionResult(GlunoGroupError.Forbidden, null);

        return new GlunoGroupDecisionResult(GlunoGroupError.None, decision)
        {
            Result = await TallyAsync(decision, ct),
        };
    }

    /// <summary>
    /// Closes a decision.
    ///
    /// A tie does NOT resolve. It closes as <c>rejected</c> with no accepted
    /// option, and Gluno offers a compromise or asks the group to choose again
    /// — picking a side arbitrarily would manufacture a decision nobody made.
    /// </summary>
    public async Task<GlunoGroupDecisionResult> CloseAsync(Guid decisionId, Guid userId, CancellationToken ct)
    {
        var decision = await _db.GlunoGroupDecisions
            .FirstOrDefaultAsync(candidate => candidate.Id == decisionId, ct);

        if (decision == null) return new GlunoGroupDecisionResult(GlunoGroupError.NotFound, null);

        var membership = await _db.TripMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(member => member.TripId == decision.TripId && member.UserId == userId, ct);

        if (membership == null) return new GlunoGroupDecisionResult(GlunoGroupError.Forbidden, null);

        // Only the owner or whoever opened it may close it early.
        if (!membership.IsOwner && decision.CreatedByUserId != userId)
            return new GlunoGroupDecisionResult(GlunoGroupError.Forbidden, decision);

        if (!GlunoGroupDecisionStatuses.IsOpen(decision.Status))
            return new GlunoGroupDecisionResult(GlunoGroupError.NotOpen, decision);

        var tally = await TallyAsync(decision, ct);
        await ResolveAsync(decision, tally, ct);

        return new GlunoGroupDecisionResult(GlunoGroupError.None, decision) { Result = tally };
    }

    public async Task<IReadOnlyList<GlunoGroupDecision>> ListAsync(Guid tripId, Guid userId, CancellationToken ct)
    {
        if (!await IsMemberAsync(tripId, userId, ct)) return Array.Empty<GlunoGroupDecision>();

        return await _db.GlunoGroupDecisions
            .AsNoTracking()
            .Where(decision => decision.TripId == tripId)
            .OrderByDescending(decision => decision.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
    }

    private async Task ResolveAsync(
        GlunoGroupDecision decision, GlunoPollResult tally, CancellationToken ct)
    {
        decision.ResolvedAt = DateTime.UtcNow;
        decision.UpdatedAt = DateTime.UtcNow;

        if (tally.WinningOptionId is { } winner)
        {
            decision.Status = GlunoGroupDecisionStatuses.Accepted;
            decision.AcceptedOptionId = winner;
        }
        else
        {
            // A tie, or nobody voted. Closed without an outcome — deliberately
            // not resolved for them.
            decision.Status = GlunoGroupDecisionStatuses.Rejected;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else resolved it first. Theirs stands.
            _db.ChangeTracker.Clear();
        }

        // Counts and status only. Never who voted for what.
        _logger.LogInformation(
            "[GLUNO] group decision resolved kind={Kind} status={Status} responded={Rate} tie={Tie}",
            decision.Kind, decision.Status, tally.ResponseRateBucket, tally.IsTie);
    }

    /// <summary>
    /// Counts the result from the vote rows and the CURRENT membership.
    ///
    /// Both halves matter. Rows rather than a client total; current membership
    /// rather than who was there when the poll opened, so a departed member
    /// neither holds the poll open nor keeps a vote in it.
    /// </summary>
    private async Task<GlunoPollResult> TallyAsync(GlunoGroupDecision decision, CancellationToken ct)
    {
        var memberIds = (await _db.TripMembers
                .AsNoTracking()
                .Where(member => member.TripId == decision.TripId)
                .Select(member => member.UserId)
                .ToListAsync(ct))
            .ToHashSet();

        var votes = await _db.GlunoGroupVotes
            .AsNoTracking()
            .Where(vote => vote.DecisionId == decision.Id)
            .Select(vote => new { vote.UserId, vote.OptionId })
            .ToListAsync(ct);

        return GlunoPollRules.Tally(
            GlunoPollOptions.Parse(decision.OptionsJson),
            votes.Select(vote => (vote.UserId, vote.OptionId)).ToList(),
            memberIds);
    }

    private Task<bool> IsMemberAsync(Guid tripId, Guid userId, CancellationToken ct)
        => _db.TripMembers.AnyAsync(member => member.TripId == tripId && member.UserId == userId, ct);
}
