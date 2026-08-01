using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoFeedbackError
{
    None,
    NotFound,
    Forbidden,
    UnknownType,
}

public sealed record GlunoFeedbackResult(GlunoFeedbackError Error, GlunoFeedbackEvent? Event)
{
    /// A candidate that just reached the point where asking is worth a turn.
    public GlunoPreferenceCandidate? ReadyCandidate { get; init; }
}

public interface IGlunoFeedbackService
{
    Task<GlunoFeedbackResult> RecordAsync(GlunoFeedbackInput input, CancellationToken ct);

    /// <summary>
    /// Records what happened to a proposal. Never throws — see the class
    /// comment for why a feedback failure must not break an apply.
    /// </summary>
    Task RecordProposalOutcomeAsync(
        GlunoProposalRecord proposal, string outcome, GlunoProposalDiffResult? diff, CancellationToken ct);

    Task<IReadOnlyList<GlunoPreferenceCandidate>> GetCandidatesAsync(
        Guid userId, Guid? tripId, CancellationToken ct);

    Task<GlunoFeedbackError> ResolveCandidateAsync(
        Guid candidateId, Guid userId, bool confirm, string? scope, CancellationToken ct);

    /// Rejections still in force for this user and trip.
    Task<IReadOnlyList<GlunoRejection>> GetActiveRejectionsAsync(
        Guid userId, Guid? tripId, CancellationToken ct);
}

public sealed class GlunoFeedbackInput
{
    /// Always the authenticated principal — never a body field.
    public required Guid UserId { get; init; }
    public required Guid ConversationId { get; init; }
    public Guid? TripId { get; init; }
    public Guid? MessageId { get; init; }
    public Guid? ProposalId { get; init; }
    public string? RecommendationRef { get; init; }
    public required string EventType { get; init; }
    public string? Reason { get; init; }
    /// Raw from the client. Sanitised before it is stored.
    public string? Note { get; init; }
    public string Scope { get; init; } = GlunoPreferenceScopes.Conversation;
    public string Source { get; init; } = "client";
}

/// <summary>
/// Turns what people do into signals that improve their next answer.
///
/// THIS IS NOT TRAINING. Nothing here is sent anywhere, fine-tunes anything, or
/// leaves SideQuest. It is product data: rows that change which suggestions one
/// user sees, on one trip, at a scope they agreed to.
///
/// THREE RULES THAT SHAPE EVERYTHING BELOW.
///
/// One tap is not a preference. A single edit is an observation; a preference
/// is something the user confirmed out loud. Everything in between lives as a
/// candidate that influences nothing.
///
/// The narrowest scope that fits. A pattern seen on one Adventure is evidence
/// about that Adventure. Promoting it to "how this person always travels"
/// requires them to say so.
///
/// Feedback failure NEVER blocks the user. Every write here is best-effort:
/// somebody applying a day plan must not have it fail because a telemetry row
/// could not be inserted. The catch blocks are the feature, not defensive
/// clutter.
/// </summary>
public sealed class GlunoFeedbackService : IGlunoFeedbackService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GlunoFeedbackService> _logger;

    public GlunoFeedbackService(AppDbContext db, ILogger<GlunoFeedbackService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoFeedbackResult> RecordAsync(GlunoFeedbackInput input, CancellationToken ct)
    {
        if (!GlunoFeedbackTypes.IsKnown(input.EventType))
            return new GlunoFeedbackResult(GlunoFeedbackError.UnknownType, null);

        // The conversation must be the caller's own. Scoped in the query, so a
        // conversation id from somebody else simply does not resolve.
        var conversation = await _db.GlunoConversations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == input.ConversationId && candidate.UserId == input.UserId, ct);

        if (conversation == null) return new GlunoFeedbackResult(GlunoFeedbackError.Forbidden, null);

        // Trip-scoped feedback needs current membership. Somebody removed from
        // an Adventure stops contributing signals to it.
        if (input.TripId is { } tripId)
        {
            var isMember = await _db.TripMembers
                .AnyAsync(member => member.TripId == tripId && member.UserId == input.UserId, ct);

            if (!isMember) return new GlunoFeedbackResult(GlunoFeedbackError.Forbidden, null);
        }

        // ── Changing an opinion supersedes, never overwrites ──────────────
        //
        // Append-only: the earlier row survives as a record of what was true
        // then, and stops counting.
        if (input.MessageId is { } messageId
            && input.EventType is GlunoFeedbackTypes.ResponseHelpful or GlunoFeedbackTypes.ResponseNotHelpful)
        {
            var previous = await _db.GlunoFeedbackEvents
                .Where(row => row.UserId == input.UserId
                    && row.MessageId == messageId
                    && row.SupersededAt == null
                    && (row.EventType == GlunoFeedbackTypes.ResponseHelpful
                        || row.EventType == GlunoFeedbackTypes.ResponseNotHelpful))
                .ToListAsync(ct);

            foreach (var row in previous)
            {
                // A double tap of the SAME verdict is a no-op, not a duplicate.
                if (row.EventType == input.EventType && input.Reason == null && input.Note == null)
                {
                    return new GlunoFeedbackResult(GlunoFeedbackError.None, row);
                }

                row.SupersededAt = DateTime.UtcNow;
            }
        }

        var note = GlunoTextSanitizer.Clean(input.Note, 280);

        var stored = new GlunoFeedbackEvent
        {
            UserId = input.UserId,
            ConversationId = input.ConversationId,
            TripId = input.TripId,
            MessageId = input.MessageId,
            ProposalId = input.ProposalId,
            RecommendationRef = input.RecommendationRef,
            EventType = input.EventType,
            Reason = input.Reason,
            // Sanitised, capped, and stored as DATA. Nothing reads it looking
            // for instructions, and it never enters a prompt.
            Note = note.Value.Length == 0 ? null : note.Value,
            Scope = GlunoPreferenceScopes.IsKnown(input.Scope) ? input.Scope : GlunoPreferenceScopes.Conversation,
            Source = input.Source,
        };

        _db.GlunoFeedbackEvents.Add(stored);

        // A rejection of a specific thing is remembered narrowly — see
        // GlunoRejection for why widening it is the failure mode.
        if (input.EventType == GlunoFeedbackTypes.RecommendationRejected
            && input.RecommendationRef is { } reference)
        {
            _db.GlunoRejections.Add(new GlunoRejection
            {
                UserId = input.UserId,
                TripId = input.TripId,
                ConversationId = input.ConversationId,
                Kind = GlunoRejectionKinds.Place,
                Reference = reference,
                Reason = input.Reason,
                // Always bounded. An open-ended rejection quietly shrinks what
                // Gluno can ever offer.
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            });
        }

        await _db.SaveChangesAsync(ct);

        // Categories only. Never the note, never the place, never the reason
        // in combination with anything identifying.
        _logger.LogInformation(
            "[GLUNO] feedback type={Type} reason={Reason} scope={Scope} hasNote={HasNote}",
            stored.EventType, stored.Reason ?? "none", stored.Scope, stored.Note != null);

        var candidate = await UpdateCandidatesAsync(stored, ct);

        return new GlunoFeedbackResult(GlunoFeedbackError.None, stored) { ReadyCandidate = candidate };
    }

    /// <summary>
    /// Records the outcome of a proposal, best-effort.
    ///
    /// Swallows everything. A user applying a day plan must never see it fail
    /// because a signal row could not be written — the plan is the product, the
    /// telemetry is not.
    /// </summary>
    public async Task RecordProposalOutcomeAsync(
        GlunoProposalRecord proposal, string outcome, GlunoProposalDiffResult? diff, CancellationToken ct)
    {
        try
        {
            var stored = new GlunoFeedbackEvent
            {
                UserId = proposal.UserId,
                ConversationId = proposal.ConversationId,
                TripId = proposal.TripId,
                MessageId = proposal.MessageId,
                ProposalId = proposal.Id,
                EventType = outcome,
                // Inferred from what happened rather than something the user
                // pressed. Weighted more cautiously downstream for that reason.
                Source = "backend",
                Scope = proposal.TripId.HasValue
                    ? GlunoPreferenceScopes.Trip
                    : GlunoPreferenceScopes.Conversation,
            };

            _db.GlunoFeedbackEvents.Add(stored);
            await _db.SaveChangesAsync(ct);

            if (diff is { HasUserEdits: true })
            {
                _logger.LogInformation(
                    "[GLUNO] proposal edited categories={Categories}", string.Join(',', diff.Categories));

                await ApplyDiffSignalsAsync(stored, diff, ct);
            }
        }
        catch (Exception ex)
        {
            // A category, and the turn continues. This is the whole point of
            // the method being best-effort.
            _logger.LogWarning("[GLUNO] outcome logging failed: {Category}", ex.GetType().Name);
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Folds a signal into the candidate that might eventually become a
    /// preference.
    ///
    /// Returns a candidate only when it has JUST crossed the evidence
    /// threshold — that is the moment asking is worth a turn of the user's
    /// attention, and asking repeatedly afterwards would be nagging.
    /// </summary>
    private async Task<GlunoPreferenceCandidate?> UpdateCandidatesAsync(
        GlunoFeedbackEvent stored, CancellationToken ct)
    {
        if (!GlunoFeedbackTypes.CarriesPreferenceSignal(stored.EventType)) return null;

        var signal = SignalFor(stored);
        if (signal == null) return null;

        return await ObserveAsync(
            stored.UserId, stored.TripId, stored.ConversationId,
            signal.Value.Key, signal.Value.Value, stored.EventType, ct);
    }

    private async Task ApplyDiffSignalsAsync(
        GlunoFeedbackEvent stored, GlunoProposalDiffResult diff, CancellationToken ct)
    {
        foreach (var change in diff.Changes.Where(change => change.IsUserIntent))
        {
            var signal = GlunoProposalDiff.ToCandidateSignal(change);
            if (signal == null) continue;

            await ObserveAsync(
                stored.UserId, stored.TripId, stored.ConversationId,
                signal.Value.Key, signal.Value.Value,
                GlunoFeedbackTypes.ProposalEditedBeforeApply, ct);
        }
    }

    /// <summary>
    /// Records one observation towards a candidate.
    /// </summary>
    private async Task<GlunoPreferenceCandidate?> ObserveAsync(
        Guid userId, Guid? tripId, Guid? conversationId,
        string key, string value, string eventType, CancellationToken ct)
    {
        if (!GlunoPreferenceKeys.IsKnown(key)) return null;

        // If the user has already STATED this preference, inferring it is moot.
        // An explicit statement always supersedes a guess.
        var stated = await _db.GlunoPreferences.AnyAsync(
            preference => preference.UserId == userId
                && preference.Key == key
                && (tripId == null || preference.TripId == tripId),
            ct);

        if (stated) return null;

        var candidate = await _db.GlunoPreferenceCandidates.FirstOrDefaultAsync(
            row => row.UserId == userId
                && row.Key == key
                && row.TripId == tripId
                && (row.Status == GlunoCandidateStatuses.Observing
                    || row.Status == GlunoCandidateStatuses.ReadyToConfirm),
            ct);

        if (candidate == null)
        {
            candidate = new GlunoPreferenceCandidate
            {
                UserId = userId,
                TripId = tripId,
                ConversationId = conversationId,
                Key = key,
                ProposedValue = value,
                // The narrowest reading that fits. Global is never inferred.
                Scope = tripId.HasValue ? GlunoPreferenceScopes.Trip : GlunoPreferenceScopes.Conversation,
                EvidenceCount = 1,
                SourceEventTypes = eventType,
                Confidence = 0.2,
            };

            _db.GlunoPreferenceCandidates.Add(candidate);
            await _db.SaveChangesAsync(ct);
            return null;
        }

        // A DIFFERENT value for the same key means the pattern is not a
        // pattern. Reset rather than accumulate — someone who moves the start
        // later once and earlier once has no consistent preference.
        if (!string.Equals(candidate.ProposedValue, value, StringComparison.OrdinalIgnoreCase))
        {
            candidate.ProposedValue = value;
            candidate.EvidenceCount = 1;
            candidate.Confidence = 0.2;
        }
        else
        {
            candidate.EvidenceCount++;
            candidate.Confidence = Math.Min(0.9, 0.2 + candidate.EvidenceCount * 0.2);
        }

        candidate.LastObservedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;

        if (!candidate.SourceEventTypes.Contains(eventType, StringComparison.Ordinal))
        {
            candidate.SourceEventTypes = $"{candidate.SourceEventTypes},{eventType}";
        }

        var justReady = candidate.Status == GlunoCandidateStatuses.Observing
            && candidate.EvidenceCount >= GlunoPreferenceCandidate.EvidenceThreshold;

        if (justReady) candidate.Status = GlunoCandidateStatuses.ReadyToConfirm;

        await _db.SaveChangesAsync(ct);

        if (justReady)
        {
            _logger.LogInformation("[GLUNO] preference candidate ready key={Key} scope={Scope}",
                candidate.Key, candidate.Scope);
        }

        // Only at the crossing. Asking again on every subsequent observation
        // would be nagging.
        return justReady ? candidate : null;
    }

    public async Task<IReadOnlyList<GlunoPreferenceCandidate>> GetCandidatesAsync(
        Guid userId, Guid? tripId, CancellationToken ct)
        => await _db.GlunoPreferenceCandidates
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Where(candidate => tripId == null || candidate.TripId == tripId)
            .Where(candidate => candidate.Status == GlunoCandidateStatuses.ReadyToConfirm)
            // A pattern from two months ago is not a pattern.
            .Where(candidate => candidate.LastObservedAt > DateTime.UtcNow.AddDays(-45))
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(5)
            .ToListAsync(ct);

    /// <summary>
    /// The user's answer to "shall I assume this?".
    ///
    /// A yes writes a real preference at the scope THEY chose. A no closes the
    /// candidate permanently — it is never asked again, because being asked
    /// twice about something you declined is worse than never being asked.
    /// </summary>
    public async Task<GlunoFeedbackError> ResolveCandidateAsync(
        Guid candidateId, Guid userId, bool confirm, string? scope, CancellationToken ct)
    {
        var candidate = await _db.GlunoPreferenceCandidates
            .FirstOrDefaultAsync(row => row.Id == candidateId && row.UserId == userId, ct);

        if (candidate == null) return GlunoFeedbackError.NotFound;

        candidate.ResolvedAt = DateTime.UtcNow;
        candidate.UpdatedAt = DateTime.UtcNow;

        if (!confirm)
        {
            candidate.Status = GlunoCandidateStatuses.Rejected;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("[GLUNO] candidate rejected key={Key}", candidate.Key);
            return GlunoFeedbackError.None;
        }

        // The scope the user picked, bounded by what the candidate could
        // support. Global requires them to say global — it is never the
        // default and never inferred.
        var chosen = GlunoPreferenceScopes.IsKnown(scope ?? "") ? scope! : candidate.Scope;

        _db.GlunoPreferences.Add(new GlunoPreference
        {
            UserId = userId,
            TripId = chosen == GlunoPreferenceScopes.Global ? null : candidate.TripId,
            ConversationId = chosen == GlunoPreferenceScopes.Conversation ? candidate.ConversationId : null,
            Key = candidate.Key,
            Value = candidate.ProposedValue,
            Scope = chosen,
            // A confirmed candidate is a PRIVATE preference. Sharing it with a
            // group is a separate, deliberate act.
            Visibility = GlunoPreferenceVisibility.Private,
            ConfirmedAt = DateTime.UtcNow,
        });

        candidate.Status = GlunoCandidateStatuses.Confirmed;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[GLUNO] candidate confirmed key={Key} scope={Scope}", candidate.Key, chosen);
        return GlunoFeedbackError.None;
    }

    public async Task<IReadOnlyList<GlunoRejection>> GetActiveRejectionsAsync(
        Guid userId, Guid? tripId, CancellationToken ct)
        => await _db.GlunoRejections
            .AsNoTracking()
            .Where(rejection => rejection.UserId == userId)
            .Where(rejection => tripId == null || rejection.TripId == tripId)
            // Expired rejections stop suppressing anything. "Not that one
            // today" was about today.
            .Where(rejection => rejection.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(rejection => rejection.CreatedAt)
            .Take(30)
            .ToListAsync(ct);

    /// <summary>
    /// Which preference a reason could eventually support.
    ///
    /// Deliberately sparse. "Not relevant" and "wrong reference" say the answer
    /// missed; they say nothing about how the person travels, and turning them
    /// into preferences would be inventing a profile out of frustration.
    /// </summary>
    private static (string Key, string Value)? SignalFor(GlunoFeedbackEvent stored) => stored.EventType switch
    {
        GlunoFeedbackTypes.TooExpensive => (GlunoPreferenceKeys.Budget, "lower budget"),
        GlunoFeedbackTypes.TooMuchWalking => (GlunoPreferenceKeys.WalkingDistance, "shorter walks"),
        GlunoFeedbackTypes.TooBusy => (GlunoPreferenceKeys.Pace, "relaxed"),
        GlunoFeedbackTypes.TooSlow => (GlunoPreferenceKeys.Pace, "packed"),
        GlunoFeedbackTypes.TooManySuggestions => (GlunoPreferenceKeys.Pace, "relaxed"),
        _ => null,
    };
}
