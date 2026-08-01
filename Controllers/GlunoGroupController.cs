using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;

namespace sidequest.backend.Controllers;

/// <summary>
/// Group planning: shared preferences, decisions and polls.
///
/// EVERY endpoint re-checks membership of the Adventure in question, on every
/// call. Not once at creation, not carried in a token — a member removed from a
/// trip loses access to its decisions the moment they are removed, including to
/// polls they were voting in.
///
/// The acting user always comes from the authenticated principal. No endpoint
/// here reads a userId from a request body, because there is no legitimate
/// reason for a client to name a different person, and every illegitimate one
/// is somebody voting or sharing as somebody else.
///
/// Nothing here returns another member's private preferences — not to the
/// owner, not to anyone. Privacy is not a permission level.
/// </summary>
[ApiController]
[Route("api/gluno/group")]
[Authorize]
public class GlunoGroupController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IGlunoGroupDecisionService _decisions;
    private readonly ITripPlanningProfileBuilder _profiles;

    public GlunoGroupController(
        AppDbContext db,
        IGlunoGroupDecisionService decisions,
        ITripPlanningProfileBuilder profiles)
    {
        _db = db;
        _decisions = decisions;
        _profiles = profiles;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string Language()
        => Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase)
            ? "sv"
            : "en";

    /// <summary>
    /// The Adventure's shared planning profile.
    ///
    /// Only <c>trip_shared</c> constraints, with neutral member refs. Nobody —
    /// including the owner — sees whose constraint is whose, and private
    /// preferences never enter the profile at all.
    /// </summary>
    [HttpGet("{tripId:guid}/profile")]
    public async Task<ActionResult<GlunoGroupProfileDto>> GetProfile(Guid tripId)
    {
        var ct = HttpContext.RequestAborted;
        if (!await IsMemberAsync(tripId, ct)) return Forbid();

        var profile = await _profiles.BuildAsync(tripId, ct);
        var conflicts = GroupPreferenceConflictDetector.Detect(profile, Language());

        return Ok(new GlunoGroupProfileDto
        {
            Version = profile.Version,
            GroupSize = profile.GroupSize,
            ContributingMembers = profile.ContributingMembers,
            // Keys and hard/soft only. The VALUE of a shared constraint can be
            // personal ("needs short walking distances"), and the group screen
            // does not need it to show that a constraint exists.
            SharedConstraintKeys = profile.Constraints
                .Select(constraint => constraint.Key)
                .Distinct()
                .ToList(),
            HardConstraintCount = profile.Hard.Count(),
            Conflicts = conflicts.Select(conflict => new GlunoGroupConflictDto
            {
                Type = conflict.Type,
                Severity = conflict.Severity,
                Explanation = conflict.Explanation,
                Compromises = conflict.Compromises.ToList(),
                RequiresGroupDecision = conflict.RequiresGroupDecision,
            }).ToList(),
        });
    }

    /// <summary>
    /// Shares one of the caller's OWN preferences with the group, or takes it
    /// back.
    ///
    /// Sharing is always a deliberate act by the person whose preference it is.
    /// There is no path by which anyone else — or Gluno — can share it for them.
    /// </summary>
    [HttpPatch("preferences/{preferenceId:guid}/visibility")]
    public async Task<ActionResult<GlunoSharedPreferenceDto>> SetVisibility(
        Guid preferenceId, [FromBody] GlunoPreferenceVisibilityDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        // Scoped to the caller's own rows in the QUERY. A preference id from
        // somebody else's conversation simply does not resolve.
        var preference = await _db.GlunoPreferences
            .FirstOrDefaultAsync(row => row.Id == preferenceId && row.UserId == userId, ct);

        if (preference == null) return NotFound();

        if (!GlunoPreferenceVisibility.IsKnown(dto.Visibility ?? ""))
            return BadRequest(new { error = "unknown_visibility" });

        // Sharing needs an Adventure to share INTO.
        if (dto.Visibility == GlunoPreferenceVisibility.TripShared)
        {
            if (preference.TripId is not { } tripId) return BadRequest(new { error = "preference_not_trip_scoped" });
            if (!await IsMemberAsync(tripId, ct)) return Forbid();
        }
        else if (preference.TripId is { } formerTrip
            && !GlunoPreferenceVisibility.IsSharedWithGroup(preference.Visibility)
            && !await IsMemberAsync(formerTrip, ct))
        {
            // Somebody who left the Adventure. Withdrawing a share they left
            // behind stays open — that direction only ever removes access, and
            // being unable to take your own constraint back out of a group you
            // are no longer in would be the wrong failure. Anything else about
            // this Adventure's planning data is closed to them.
            return Forbid();
        }

        var wasShared = GlunoPreferenceVisibility.IsSharedWithGroup(preference.Visibility);

        preference.Visibility = dto.Visibility!;
        preference.IsHardConstraint = dto.IsHardConstraint ?? preference.IsHardConstraint;
        preference.ConfirmedAt = DateTime.UtcNow;
        preference.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Withdrawing a shared constraint invalidates plans built on it. A
        // pending proposal that assumed "short walking distances" must not stay
        // applicable once that requirement is gone from the profile.
        if (wasShared && !GlunoPreferenceVisibility.IsSharedWithGroup(preference.Visibility)
            && preference.TripId is { } affectedTrip)
        {
            await MarkGroupProposalsStaleAsync(affectedTrip, ct);
        }

        return Ok(new GlunoSharedPreferenceDto
        {
            Id = preference.Id,
            Key = preference.Key,
            Visibility = preference.Visibility,
            IsHardConstraint = preference.IsHardConstraint,
        });
    }

    [HttpGet("{tripId:guid}/decisions")]
    public async Task<ActionResult<List<GlunoGroupDecisionDto>>> ListDecisions(Guid tripId)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var decisions = await _decisions.ListAsync(tripId, userId, ct);
        if (decisions.Count == 0 && !await IsMemberAsync(tripId, ct)) return Forbid();

        var mapped = new List<GlunoGroupDecisionDto>(decisions.Count);
        foreach (var decision in decisions)
        {
            var result = await _decisions.GetAsync(decision.Id, userId, ct);
            mapped.Add(Map(decision, result.Result, userId, await MyVoteAsync(decision.Id, userId, ct)));
        }

        return Ok(mapped);
    }

    [HttpGet("decisions/{decisionId:guid}")]
    public async Task<ActionResult<GlunoGroupDecisionDto>> GetDecision(Guid decisionId)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var result = await _decisions.GetAsync(decisionId, userId, ct);
        return await MapResultAsync(result, userId, ct);
    }

    /// <summary>
    /// Casts or changes the caller's vote.
    ///
    /// A null option is a deliberate abstention and is recorded as one. Silence
    /// — never calling this — is not, because absence of a reply must never be
    /// counted as agreement.
    /// </summary>
    [HttpPost("decisions/{decisionId:guid}/vote")]
    public async Task<ActionResult<GlunoGroupDecisionDto>> Vote(
        Guid decisionId, [FromBody] GlunoGroupVoteDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        // dto carries the option only. There is deliberately no userId field.
        var result = await _decisions.VoteAsync(decisionId, userId, dto.OptionId, ct);
        return await MapResultAsync(result, userId, ct);
    }

    [HttpPost("decisions/{decisionId:guid}/close")]
    public async Task<ActionResult<GlunoGroupDecisionDto>> Close(Guid decisionId)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var result = await _decisions.CloseAsync(decisionId, userId, ct);
        return await MapResultAsync(result, userId, ct);
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private async Task<ActionResult<GlunoGroupDecisionDto>> MapResultAsync(
        GlunoGroupDecisionResult result, Guid userId, CancellationToken ct)
    {
        switch (result.Error)
        {
            case GlunoGroupError.NotFound:
                return NotFound();
            case GlunoGroupError.Forbidden:
                return Forbid();
            case GlunoGroupError.NotOpen:
                return Conflict(new { error = "decision_closed" });
            case GlunoGroupError.InvalidOption:
                return BadRequest(new { error = "unknown_option" });
            case GlunoGroupError.InvalidOptions:
                return BadRequest(new { error = "invalid_options", problems = result.Problems });
            case GlunoGroupError.UnknownVersion:
                // An old client against a shape it does not understand. Refused
                // rather than interpreted.
                return Conflict(new { error = "unsupported_decision_version" });
            case GlunoGroupError.Conflict:
                return Conflict(new { error = "decision_changed", retryable = true });
        }

        if (result.Decision is not { } decision) return NotFound();

        return Ok(Map(decision, result.Result, userId, await MyVoteAsync(decision.Id, userId, ct)));
    }

    /// <summary>
    /// What the app sees.
    ///
    /// Counts per option and the caller's OWN vote. Never who voted for what —
    /// a poll that reveals individual votes is a poll people answer
    /// strategically rather than honestly.
    /// </summary>
    private static GlunoGroupDecisionDto Map(
        GlunoGroupDecision decision, GlunoPollResult? result, Guid userId, string? myVote)
        => new()
        {
            Id = decision.Id,
            TripId = decision.TripId,
            Version = decision.Version,
            Kind = decision.Kind,
            Question = decision.Question,
            Status = decision.Status,
            ClosingRule = decision.ClosingRule,
            ClosesAt = decision.ClosesAt,
            AcceptedOptionId = decision.AcceptedOptionId,
            Options = GlunoPollOptions.Parse(decision.OptionsJson)
                .Select(option => new GlunoGroupOptionDto
                {
                    Id = option.Id,
                    Label = option.Label,
                    Summary = option.Summary,
                    Votes = result?.Tallies.FirstOrDefault(tally => tally.OptionId == option.Id)?.Votes ?? 0,
                })
                .ToList(),
            Responded = result?.Responded ?? 0,
            GroupSize = result?.GroupSize ?? 0,
            IsTie = result?.IsTie ?? false,
            MyVote = myVote,
            /// True when the caller has answered at all, abstention included.
            HasVoted = myVote != null || (result?.Abstained ?? 0) > 0 && myVote == null,
        };

    private async Task<string?> MyVoteAsync(Guid decisionId, Guid userId, CancellationToken ct)
        => await _db.GlunoGroupVotes
            .AsNoTracking()
            .Where(vote => vote.DecisionId == decisionId && vote.UserId == userId)
            .Select(vote => vote.OptionId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Marks pending proposals built on the group profile as stale.
    ///
    /// Fired when a shared constraint is withdrawn. A proposal grounded in
    /// "short walking distances" describes a plan built for a requirement that
    /// no longer exists, and applying it later would be applying reasoning
    /// nobody stands behind any more.
    /// </summary>
    private async Task MarkGroupProposalsStaleAsync(Guid tripId, CancellationToken ct)
    {
        var pending = await _db.GlunoProposals
            .Where(proposal => proposal.TripId == tripId
                && proposal.Status == GlunoProposalStatuses.Pending
                && proposal.SnapshotJson != null
                && proposal.SnapshotJson.Contains("\"groupProfileVersion\""))
            .ToListAsync(ct);

        foreach (var proposal in pending)
        {
            proposal.Status = GlunoProposalStatuses.Stale;
            proposal.FailureCode = "group_constraint_withdrawn";
            proposal.UpdatedAt = DateTime.UtcNow;
        }

        if (pending.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private Task<bool> IsMemberAsync(Guid tripId, CancellationToken ct)
    {
        var userId = GetUserId();
        return _db.TripMembers.AnyAsync(member => member.TripId == tripId && member.UserId == userId, ct);
    }
}
