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
/// Feedback, preference candidates, and what Gluno has learned.
///
/// EVERY read and write here is scoped to the authenticated principal, in the
/// query. Not checked afterwards — scoped, so a candidate id or a preference id
/// belonging to somebody else simply does not resolve. That includes an
/// Adventure owner: privacy is not a permission level, and there is no path by
/// which one member reads another's Gluno data.
///
/// Nothing here changes an Adventure. Feedback adjusts what Gluno SUGGESTS
/// next; changing a trip still goes through propose → review → apply.
/// </summary>
[ApiController]
[Route("api/gluno/feedback")]
[Authorize]
public class GlunoFeedbackController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IGlunoFeedbackService _feedback;

    public GlunoFeedbackController(AppDbContext db, IGlunoFeedbackService feedback)
    {
        _db = db;
        _feedback = feedback;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Records one signal.
    ///
    /// Pressing the same verdict twice is a no-op rather than a duplicate;
    /// changing it supersedes the earlier row without deleting it.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<GlunoFeedbackResponseDto>> Record([FromBody] GlunoFeedbackDto dto)
    {
        var ct = HttpContext.RequestAborted;

        var result = await _feedback.RecordAsync(new GlunoFeedbackInput
        {
            // ALWAYS the principal. The DTO deliberately has no userId field.
            UserId = GetUserId(),
            ConversationId = dto.ConversationId,
            TripId = dto.TripId,
            MessageId = dto.MessageId,
            ProposalId = dto.ProposalId,
            RecommendationRef = dto.RecommendationRef,
            EventType = dto.EventType ?? string.Empty,
            Reason = dto.Reason,
            Note = dto.Note,
            Scope = dto.Scope ?? GlunoPreferenceScopes.Conversation,
        }, ct);

        return result.Error switch
        {
            GlunoFeedbackError.UnknownType => BadRequest(new { error = "unknown_feedback_type" }),
            GlunoFeedbackError.Forbidden => Forbid(),
            GlunoFeedbackError.NotFound => NotFound(),
            _ => Ok(new GlunoFeedbackResponseDto
            {
                Recorded = true,
                // Surfaced only when a candidate just crossed the threshold, so
                // the app can offer the confirmation question once rather than
                // on every subsequent tap.
                ReadyCandidate = result.ReadyCandidate == null
                    ? null
                    : MapCandidate(result.ReadyCandidate),
            }),
        };
    }

    /// <summary>
    /// What Gluno currently assumes, and what it is thinking about assuming.
    ///
    /// This is the user-facing control surface: confirmed preferences with
    /// their scope, and pending candidates they can accept or dismiss. It shows
    /// product language, never raw feedback rows — a list of every tap somebody
    /// made is a surveillance log, not a settings screen.
    /// </summary>
    [HttpGet("learned")]
    public async Task<ActionResult<GlunoLearnedDto>> GetLearned([FromQuery] Guid? tripId)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        // The Adventures the caller is CURRENTLY in. Used twice below: to drop
        // trip-scoped rows for Adventures they have left, and to put a name
        // next to the ones they have not.
        var memberTrips = await _db.TripMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Join(_db.Trips.AsNoTracking(), member => member.TripId, trip => trip.Id,
                (member, trip) => new GlunoTripLabelDto { Id = trip.Id, Title = trip.Title })
            .ToListAsync(ct);

        var memberTripIds = memberTrips.Select(trip => trip.Id).ToHashSet();

        var preferences = await _db.GlunoPreferences
            .AsNoTracking()
            .Where(preference => preference.UserId == userId)
            .Where(preference => tripId == null || preference.TripId == tripId || preference.TripId == null)
            .OrderBy(preference => preference.Key)
            .Take(60)
            .ToListAsync(ct);

        // Leaving an Adventure ends the caller's access to its planning data,
        // including their own preference for it. The row stays — it is theirs,
        // and rejoining should not have lost it — but it stops being readable
        // and, through the guard on every mutation below, stops being editable.
        preferences = preferences
            .Where(preference => preference.TripId is not { } trip || memberTripIds.Contains(trip))
            .Take(40)
            .ToList();

        var candidates = await _feedback.GetCandidatesAsync(userId, tripId, ct);

        return Ok(new GlunoLearnedDto
        {
            Preferences = preferences.Select(MapPreference).ToList(),
            Candidates = candidates
                .Where(candidate => candidate.TripId is not { } trip || memberTripIds.Contains(trip))
                .Select(MapCandidate)
                .ToList(),
            Trips = memberTrips
                .Where(trip => preferences.Any(preference => preference.TripId == trip.Id)
                    || candidates.Any(candidate => candidate.TripId == trip.Id))
                .ToList(),
        });
    }

    /// <summary>
    /// Changes one of the caller's own confirmed preferences.
    ///
    /// Narrow on purpose. The key cannot move — a row is about one thing — and
    /// the value is validated against what that key actually accepts rather
    /// than merely being length-capped. Widening the scope to global is
    /// allowed but never inferred: it takes an explicit scope in the body.
    /// </summary>
    [HttpPatch("preferences/{preferenceId:guid}")]
    public async Task<ActionResult<GlunoLearnedPreferenceDto>> UpdatePreference(
        Guid preferenceId, [FromBody] GlunoPreferenceUpdateDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        // Scoped to the caller's own rows IN THE QUERY. An id from somebody
        // else's account does not resolve, so there is no branch where the
        // wrong person's preference is loaded and then checked.
        var preference = await _db.GlunoPreferences
            .FirstOrDefaultAsync(row => row.Id == preferenceId && row.UserId == userId, ct);

        if (preference == null) return NotFound();

        if (preference.TripId is { } tripId && !await IsMemberAsync(tripId, userId, ct)) return Forbid();

        if (dto.Value is not null)
        {
            var canonical = GlunoPreferenceValues.Canonicalise(preference.Key, dto.Value);
            if (canonical == null) return BadRequest(new { error = "invalid_value" });

            preference.Value = canonical;
        }

        if (dto.Scope is { } scope)
        {
            if (!GlunoPreferenceScopes.IsKnown(scope)) return BadRequest(new { error = "unknown_scope" });

            if (scope == GlunoPreferenceScopes.Trip && preference.TripId == null)
                return BadRequest(new { error = "preference_not_trip_scoped" });

            // Going global drops the Adventure and the conversation: it is no
            // longer about either. It also drops sharing — a preference that
            // follows the user between trips is not one this Adventure's group
            // has any claim on.
            if (scope == GlunoPreferenceScopes.Global)
            {
                preference.TripId = null;
                preference.ConversationId = null;
                preference.Visibility = GlunoPreferenceVisibility.GlobalPrivate;
            }

            preference.Scope = scope;
        }

        preference.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // A plan built on "no car" or "30 minutes of walking" stops being a
        // plan when that changes. Revalidating is the existing mechanism; a
        // pending proposal quietly surviving its own premise is not.
        if (GlunoPreferenceValues.AffectsFeasibility(preference.Key))
        {
            await MarkDependentProposalsStaleAsync(userId, preference.TripId, ct);
        }

        return Ok(MapPreference(preference));
    }

    /// <summary>
    /// The user's answer to a candidate.
    ///
    /// A yes writes a real preference at the scope THEY chose. A no closes it
    /// permanently — being asked twice about something you declined is worse
    /// than never being asked.
    /// </summary>
    [HttpPost("candidates/{candidateId:guid}")]
    public async Task<IActionResult> ResolveCandidate(
        Guid candidateId, [FromBody] GlunoCandidateDecisionDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        // Confirming a candidate for an Adventure the caller has left would
        // write a trip-scoped preference into a trip they cannot see. The
        // candidate is theirs; the Adventure is no longer.
        var candidateTrip = await _db.GlunoPreferenceCandidates
            .AsNoTracking()
            .Where(row => row.Id == candidateId && row.UserId == userId)
            .Select(row => row.TripId)
            .FirstOrDefaultAsync(ct);

        if (candidateTrip is { } tripId && !await IsMemberAsync(tripId, userId, ct)) return Forbid();

        var error = await _feedback.ResolveCandidateAsync(
            candidateId, userId, dto.Confirm, dto.Scope, ct);

        return error switch
        {
            GlunoFeedbackError.NotFound => NotFound(),
            GlunoFeedbackError.Forbidden => Forbid(),
            _ => NoContent(),
        };
    }

    /// <summary>
    /// Forgets a confirmed preference.
    ///
    /// A hard delete, deliberately. "Forget that" should mean the row is gone,
    /// not flagged — a soft-deleted preference is one the user believes is gone
    /// and is not.
    /// </summary>
    [HttpDelete("preferences/{preferenceId:guid}")]
    public async Task<IActionResult> ForgetPreference(Guid preferenceId)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var preference = await _db.GlunoPreferences
            .FirstOrDefaultAsync(row => row.Id == preferenceId && row.UserId == userId, ct);

        // Already gone. Forgetting twice is the same outcome as forgetting
        // once, so a repeated tap must not surface as an error the user has to
        // interpret.
        if (preference == null) return NoContent();

        if (preference.TripId is { } tripId && !await IsMemberAsync(tripId, userId, ct)) return Forbid();

        var affectedTrip = preference.TripId;
        var mattered = GlunoPreferenceValues.AffectsFeasibility(preference.Key)
            || GlunoPreferenceVisibility.IsSharedWithGroup(preference.Visibility);

        _db.GlunoPreferences.Remove(preference);
        await _db.SaveChangesAsync(ct);

        if (mattered) await MarkDependentProposalsStaleAsync(userId, affectedTrip, ct);

        return NoContent();
    }

    /// <summary>
    /// Retires pending proposals that were built on a preference the user just
    /// changed or removed.
    ///
    /// Stale rather than deleted: the suggestion still happened, and the app
    /// already knows how to say "this is no longer current" and offer a fresh
    /// one. Deleting it would make somebody's screen change under them with no
    /// explanation.
    /// </summary>
    private async Task MarkDependentProposalsStaleAsync(Guid userId, Guid? tripId, CancellationToken ct)
    {
        var pending = await _db.GlunoProposals
            .Where(proposal => proposal.UserId == userId
                && proposal.Status == GlunoProposalStatuses.Pending
                && (tripId == null || proposal.TripId == tripId))
            .Take(50)
            .ToListAsync(ct);

        foreach (var proposal in pending)
        {
            proposal.Status = GlunoProposalStatuses.Stale;
            proposal.FailureCode = "preference_changed";
            proposal.UpdatedAt = DateTime.UtcNow;
        }

        if (pending.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private Task<bool> IsMemberAsync(Guid tripId, Guid userId, CancellationToken ct)
        => _db.TripMembers.AnyAsync(member => member.TripId == tripId && member.UserId == userId, ct);

    private static GlunoLearnedPreferenceDto MapPreference(GlunoPreference preference) => new()
    {
        Id = preference.Id,
        Key = preference.Key,
        Value = preference.Value,
        Scope = preference.Scope,
        Visibility = preference.Visibility,
        IsHardConstraint = preference.IsHardConstraint,
        ConfirmedAt = preference.ConfirmedAt,
        TripId = preference.TripId,
        ConversationId = preference.ConversationId,
        Editor = GlunoPreferenceValues.EditorFor(preference.Key),
        Options = GlunoPreferenceValues.OptionsFor(preference.Key).ToList(),
    };

    private static GlunoCandidateDto MapCandidate(GlunoPreferenceCandidate candidate) => new()
    {
        Id = candidate.Id,
        Key = candidate.Key,
        ProposedValue = candidate.ProposedValue,
        Scope = candidate.Scope,
        TripId = candidate.TripId,
        // Deliberately absent: evidenceCount and confidence. Showing "we saw
        // you do this 4 times" reads as surveillance, and the number does not
        // help anyone decide.
    };
}
