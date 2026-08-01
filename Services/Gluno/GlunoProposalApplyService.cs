using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoApplyError
{
    None,
    NotFound,
    Forbidden,
    /// Already applied — returned WITH the original result, because a retry of
    /// a successful apply must look like success, not like a new one.
    AlreadyApplied,
    AlreadyRejected,
    /// Another apply for this proposal is in flight.
    InProgress,
    /// The Adventure changed after the proposal was created.
    Stale,
    /// Validation against the live database failed.
    Invalid,
    /// Something went wrong while writing. Retryable.
    Failed,
}

public sealed class GlunoApplyResult
{
    public GlunoApplyError Error { get; init; }
    public GlunoProposalRecord? Proposal { get; init; }
    /// Machine-readable reason, e.g. "date_out_of_range". Never a database or
    /// model message.
    public string? FailureCode { get; init; }
    /// A short, user-facing sentence. Neutral: no stack trace, no payload.
    public string? Message { get; init; }
    /// Ids created or changed, so the app can update the right caches.
    public GlunoApplyChanges Changes { get; init; } = new();
}

/// <summary>
/// What an apply actually touched. The app uses this to invalidate exactly the
/// affected Adventure rather than dropping every cache it has.
/// </summary>
public sealed class GlunoApplyChanges
{
    public Guid? TripId { get; set; }
    public List<Guid> CreatedActivityIds { get; set; } = new();
    public List<Guid> UpdatedActivityIds { get; set; } = new();
    public List<Guid> CreatedDayLocationIds { get; set; } = new();
    public List<Guid> UpdatedDayLocationIds { get; set; } = new();
    public bool TripDatesChanged { get; set; }
    /// Dates whose plan changed — enough for the app to refresh weather and
    /// the feed for just those days.
    public List<string> AffectedDates { get; set; } = new();
}

public interface IGlunoProposalApplyService
{
    Task<GlunoApplyResult> ApplyAsync(Guid proposalId, Guid userId, CancellationToken ct);
    Task<GlunoApplyResult> RejectAsync(Guid proposalId, Guid userId, CancellationToken ct);
    Task<GlunoApplyResult> UpdatePayloadAsync(Guid proposalId, Guid userId, JsonElement payload, CancellationToken ct);
}

/// <summary>
/// The ONLY thing in SideQuest that turns a Gluno proposal into a real change.
///
/// Everything about it is built around one rule: the model never writes. The
/// AI produces a proposal; a human taps apply; and only then does this service
/// run — re-checking membership, edit rights, dates and staleness against the
/// live database, because everything the proposal was based on may have moved
/// since it was made.
///
/// FIVE GUARANTEES.
///
///  1. **Allow-list dispatch.** <see cref="ApplyCoreAsync"/> switches on a
///     fixed set of action names. An unknown or newly-invented action type is
///     refused; there is no default branch that "tries anyway".
///
///  2. **Applied exactly once.** The status transition to `applying` is a
///     conditional UPDATE — the row is claimed atomically, so a double tap, a
///     retried request or two devices produce one write and one no-op. The
///     proposal id IS the idempotency key.
///
///  3. **All or nothing.** Multi-row actions run inside a transaction, so a
///     day plan that fails halfway leaves no half-created day behind and the
///     UI is never told a plan succeeded when it partly did not.
///
///  4. **Same rules as the app.** Permissions, date ranges, stay validation
///     and sort-index placement all go through <see cref="TripEditRules"/> —
///     the same code the ordinary endpoints use. There is no path where the
///     assistant may do something a person could not.
///
///  5. **Nothing leaks outward.** Failures return a code and a neutral
///     sentence. Database messages, model payloads and trip content never
///     reach the client or the log.
/// </summary>
public sealed class GlunoProposalApplyService : IGlunoProposalApplyService
{
    private readonly AppDbContext _db;
    private readonly IGlunoProposalStore _store;
    private readonly IGlunoFeedbackService _feedback;
    private readonly ILogger<GlunoProposalApplyService> _logger;

    public GlunoProposalApplyService(
        AppDbContext db,
        IGlunoProposalStore store,
        IGlunoFeedbackService feedback,
        ILogger<GlunoProposalApplyService> logger)
    {
        _db = db;
        _store = store;
        _feedback = feedback;
        _logger = logger;
    }

    // ── Reject ────────────────────────────────────────────────────────────

    public async Task<GlunoApplyResult> RejectAsync(Guid proposalId, Guid userId, CancellationToken ct)
    {
        var proposal = await _store.GetOwnedAsync(proposalId, userId, ct);
        if (proposal == null) return new GlunoApplyResult { Error = GlunoApplyError.NotFound };

        if (proposal.Status == GlunoProposalStatuses.Applied)
            return new GlunoApplyResult { Error = GlunoApplyError.AlreadyApplied, Proposal = proposal };

        // Rejecting an already-rejected proposal is a no-op success: the user
        // wanted it gone, and it is.
        if (proposal.Status is GlunoProposalStatuses.Rejected or GlunoProposalStatuses.Stale)
            return new GlunoApplyResult { Proposal = proposal };

        proposal.Status = GlunoProposalStatuses.Rejected;
        proposal.RejectedAt = DateTime.UtcNow;
        proposal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // After the commit, and with no diff. A dismissed proposal says the
        // suggestion missed; it does not say what the user wanted instead, and
        // inventing that from a dismissal is exactly the overreach to avoid.
        await _feedback.RecordProposalOutcomeAsync(
            proposal, GlunoFeedbackTypes.ProposalRejected, null, ct);

        return new GlunoApplyResult { Proposal = proposal };
    }

    // ── Edit ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces a pending proposal's payload with the user's edited version.
    ///
    /// The edit is re-validated by the apply itself, not here — this only
    /// enforces that the proposal is still editable. Editing anything that has
    /// already been applied, rejected or gone stale is refused; a terminal
    /// proposal never goes back to pending.
    /// </summary>
    public async Task<GlunoApplyResult> UpdatePayloadAsync(
        Guid proposalId, Guid userId, JsonElement payload, CancellationToken ct)
    {
        var proposal = await _store.GetOwnedAsync(proposalId, userId, ct);
        if (proposal == null) return new GlunoApplyResult { Error = GlunoApplyError.NotFound };

        if (proposal.Status == GlunoProposalStatuses.Applied)
            return new GlunoApplyResult { Error = GlunoApplyError.AlreadyApplied, Proposal = proposal };
        if (proposal.Status == GlunoProposalStatuses.Rejected)
            return new GlunoApplyResult { Error = GlunoApplyError.AlreadyRejected, Proposal = proposal };
        if (proposal.Status == GlunoProposalStatuses.Stale)
            return new GlunoApplyResult { Error = GlunoApplyError.Stale, Proposal = proposal, FailureCode = "trip_changed" };
        if (proposal.Status == GlunoProposalStatuses.Applying)
            return new GlunoApplyResult { Error = GlunoApplyError.InProgress, Proposal = proposal };

        if (payload.ValueKind != JsonValueKind.Object)
            return new GlunoApplyResult { Error = GlunoApplyError.Invalid, Proposal = proposal, FailureCode = "invalid_payload" };

        // Computed BEFORE the overwrite, because this is the only moment both
        // versions exist. What Gluno suggested versus what the user actually
        // wanted is the single most honest signal in the whole feedback system
        // — nobody edits a plan to be polite.
        var diff = ReadDiff(proposal.PayloadJson, payload);

        proposal.PayloadJson = payload.GetRawText();
        proposal.PayloadVersion = GlunoProposalPayloadVersions.Current;
        // An edited proposal is pending again, not failed — a previous failure
        // is exactly what the user just tried to fix.
        proposal.Status = GlunoProposalStatuses.Pending;
        proposal.FailureCode = null;
        proposal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (diff is { HasUserEdits: true })
        {
            await _feedback.RecordProposalOutcomeAsync(
                proposal, GlunoFeedbackTypes.ProposalEditedBeforeApply, diff, ct);
        }

        return new GlunoApplyResult { Proposal = proposal };
    }

    /// <summary>
    /// Diffs the suggestion against the edit, or returns null if either side is
    /// unreadable.
    ///
    /// Never throws: this runs on the edit path, and a signal that could not be
    /// computed is not worth failing somebody's edit over.
    /// </summary>
    private static GlunoProposalDiffResult? ReadDiff(string originalJson, JsonElement edited)
    {
        try
        {
            using var original = JsonDocument.Parse(originalJson);
            return GlunoProposalDiff.Compare(original.RootElement, edited);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    public async Task<GlunoApplyResult> ApplyAsync(Guid proposalId, Guid userId, CancellationToken ct)
    {
        var proposal = await _store.GetOwnedAsync(proposalId, userId, ct);
        if (proposal == null) return new GlunoApplyResult { Error = GlunoApplyError.NotFound };

        // Idempotent replay: the same request arriving twice must not create a
        // second Activity, and must not look like a failure either.
        if (proposal.Status == GlunoProposalStatuses.Applied)
        {
            return new GlunoApplyResult
            {
                Error = GlunoApplyError.AlreadyApplied,
                Proposal = proposal,
                Changes = ReadChanges(proposal),
            };
        }
        if (proposal.Status == GlunoProposalStatuses.Rejected)
            return new GlunoApplyResult { Error = GlunoApplyError.AlreadyRejected, Proposal = proposal };
        if (proposal.Status == GlunoProposalStatuses.Stale)
            return new GlunoApplyResult { Error = GlunoApplyError.Stale, Proposal = proposal, FailureCode = "trip_changed" };

        if (proposal.TripId is not { } tripId)
            return await MarkFailedAsync(proposal.Id,"no_adventure", "This suggestion is not attached to an Adventure.", ct);

        // Permission is re-checked NOW, not at proposal time. Rights can be
        // revoked between suggesting and applying, and the answer that matters
        // is the one at the moment of the write.
        if (!await TripEditRules.CanEditActivitiesAsync(_db, tripId, userId, ct))
            return new GlunoApplyResult { Error = GlunoApplyError.Forbidden, Proposal = proposal };

        if (GlunoActions.Find(proposal.ActionType) == null)
            return await MarkFailedAsync(proposal.Id,"unknown_action", "This suggestion cannot be applied.", ct);

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(proposal.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return await MarkFailedAsync(proposal.Id,"invalid_payload", "This suggestion could not be read.", ct);
        }

        // Staleness before the claim: no point locking a proposal that is
        // already out of date.
        var currentSnapshot = await _store.BuildSnapshotAsync(tripId, proposal.ActionType, payload, ct);
        var storedSnapshot = ReadSnapshot(proposal);
        if (storedSnapshot != null && !storedSnapshot.Matches(currentSnapshot))
        {
            proposal.Status = GlunoProposalStatuses.Stale;
            proposal.FailureCode = "trip_changed";
            proposal.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new GlunoApplyResult { Error = GlunoApplyError.Stale, Proposal = proposal, FailureCode = "trip_changed" };
        }

        // THE claim. A conditional UPDATE, so exactly one caller can move the
        // row out of pending/failed — the losing caller sees InProgress rather
        // than writing a duplicate.
        var claimed = await _db.GlunoProposals
            .Where(p => p.Id == proposal.Id
                        && p.UserId == userId
                        && (p.Status == GlunoProposalStatuses.Pending || p.Status == GlunoProposalStatuses.Failed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, GlunoProposalStatuses.Applying)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), ct);

        if (claimed != 1)
        {
            await _db.Entry(proposal).ReloadAsync(ct);
            return new GlunoApplyResult { Error = GlunoApplyError.InProgress, Proposal = proposal };
        }

        // The tracked entity predates the claim.
        await _db.Entry(proposal).ReloadAsync(ct);

        try
        {
            var changes = new GlunoApplyChanges { TripId = tripId };

            // One transaction for the whole action: a day plan either lands
            // completely or not at all.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            var validation = await ApplyCoreAsync(proposal, tripId, userId, payload, changes, ct);
            if (validation != null)
            {
                await transaction.RollbackAsync(ct);
                return await MarkFailedAsync(proposal.Id,validation.Value.Code, validation.Value.Message, ct);
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            proposal.Status = GlunoProposalStatuses.Applied;
            proposal.AppliedAt = DateTime.UtcNow;
            proposal.UpdatedAt = DateTime.UtcNow;
            proposal.FailureCode = null;
            proposal.ResultJson = JsonSerializer.Serialize(changes, GlunoJson.Options);
            await _db.SaveChangesAsync(ct);

            // Outside the transaction and after the commit. The change is
            // already the user's; a feedback write that fails here must not
            // touch it. Any edits were recorded when they were made.
            await _feedback.RecordProposalOutcomeAsync(
                proposal, GlunoFeedbackTypes.ProposalAccepted, null, ct);

            return new GlunoApplyResult { Proposal = proposal, Changes = changes };
        }
        catch (OperationCanceledException)
        {
            // The caller went away mid-apply. Release the claim so the same
            // proposal is not stuck in `applying` forever.
            await ReleaseClaimAsync(proposal.Id, ct);
            throw;
        }
        catch (Exception ex)
        {
            // Type name only. A database exception can carry column values,
            // which here means trip content.
            _logger.LogWarning(
                "[GLUNO] apply failed for action {Action}: {Category}", proposal.ActionType, ex.GetType().Name);
            return await MarkFailedAsync(proposal.Id,"apply_failed", "The change could not be applied.", ct);
        }
    }

    // ── Allow-listed dispatch ─────────────────────────────────────────────

    /// Returns null on success, or the reason it was refused. Never throws for
    /// a validation problem — those are answers, not errors.
    private async Task<(string Code, string Message)?> ApplyCoreAsync(
        GlunoProposalRecord proposal,
        Guid tripId,
        Guid userId,
        JsonElement payload,
        GlunoApplyChanges changes,
        CancellationToken ct)
        => proposal.ActionType switch
        {
            GlunoActions.ProposeActivity => await ApplyActivityAsync(tripId, userId, payload, changes, ct),
            GlunoActions.ProposeDayPlan => await ApplyDayPlanAsync(tripId, userId, payload, changes, ct),
            GlunoActions.ProposeDayLocation => await ApplyDayLocationAsync(tripId, userId, payload, changes, ct),
            GlunoActions.ProposeActivityMove => await ApplyActivityMoveAsync(tripId, userId, payload, changes, ct),
            GlunoActions.ProposeTripDateChange => await ApplyTripDatesAsync(tripId, userId, payload, changes, ct),
            // No default that tries anyway. An action name this build does not
            // know is refused.
            _ => ("unknown_action", "This suggestion cannot be applied."),
        };

    private async Task<(string, string)?> ApplyActivityAsync(
        Guid tripId, Guid userId, JsonElement payload, GlunoApplyChanges changes, CancellationToken ct)
    {
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return ("trip_missing", "That Adventure no longer exists.");

        var draft = GlunoActivityDraft.Read(payload);
        if (draft.Error != null) return ("invalid_payload", draft.Error);

        var rangeError = ValidateActivityDates(trip, draft);
        if (rangeError != null) return rangeError;

        await CreateActivityAsync(trip, userId, draft, changes, ct);
        return null;
    }

    private async Task<(string, string)?> ApplyDayPlanAsync(
        Guid tripId, Guid userId, JsonElement payload, GlunoApplyChanges changes, CancellationToken ct)
    {
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return ("trip_missing", "That Adventure no longer exists.");

        if (!TryReadDate(payload, "date", out var date))
            return ("invalid_payload", "This suggestion has no valid date.");
        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return ("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        if (!payload.TryGetProperty("activities", out var activities) || activities.ValueKind != JsonValueKind.Array)
            return ("invalid_payload", "This day plan has no activities.");

        // A schedule the engine could not resolve — overlapping bookings, a stop
        // with nowhere to go — is refused rather than half-saved. The review
        // screen already blocks the button; this is the server-side half of the
        // same rule, because the button is not the security boundary.
        if (payload.TryGetProperty("feasible", out var feasible)
            && feasible.ValueKind == JsonValueKind.False)
        {
            return ("schedule_not_feasible",
                "This day still has times that clash. Adjust it in the preview before saving.");
        }

        // ── Grounding: critical references ────────────────────────────────
        //
        // Two categories, and the difference matters. PROVIDER data going stale
        // between review and apply — a rating, an opening hour — is a warning:
        // the user already looked at the plan, and none of it changes what gets
        // written. A CRITICAL reference going stale — the date, an Activity id —
        // would write to the wrong place, silently, and is refused.
        if (payload.TryGetProperty("grounding", out var grounding)
            && grounding.ValueKind == JsonValueKind.Object
            && grounding.TryGetProperty("criticalRefs", out var criticalRefs)
            && criticalRefs.ValueKind == JsonValueKind.Object)
        {
            if (TryReadDate(criticalRefs, "date", out var groundedDate) && groundedDate != date)
            {
                return ("grounding_date_changed",
                    "This suggestion was built for a different day. Ask Gluno for a fresh one.");
            }

            if (criticalRefs.TryGetProperty("existingActivityIds", out var referenced)
                && referenced.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in referenced.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) continue;
                    if (!Guid.TryParse(element.GetString(), out var activityId)) continue;

                    var stillThere = await _db.TripActivities
                        .AnyAsync(activity => activity.Id == activityId && activity.TripId == tripId, ct);

                    if (!stillThere)
                    {
                        // The plan was built around a booking that has since
                        // gone. Its times no longer mean what they meant.
                        return ("grounding_activity_removed",
                            "Part of this day was removed since Gluno suggested it. Ask for an updated plan.");
                    }
                }
            }
        }

        var drafts = new List<GlunoActivityDraft>();
        foreach (var entry in activities.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            // Rows for Activities that are ALREADY in the Adventure are shown in
            // the preview for context — the dinner booking the day was planned
            // around. Saving them again would duplicate them.
            if (entry.TryGetProperty("existingActivityId", out var existingId)
                && existingId.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(existingId.GetString()))
            {
                continue;
            }

            // Each row inherits the plan's date; the row itself carries
            // title/time/endTime/category, exactly as the preview showed it.
            var draft = GlunoActivityDraft.Read(entry, fallbackDate: date);
            if (draft.Error != null) return ("invalid_payload", draft.Error);
            drafts.Add(draft);
        }

        if (drafts.Count == 0) return ("invalid_payload", "This day plan has no activities.");
        if (drafts.Count > GlunoActions.MaxDayPlanActivities)
            return ("too_many_activities", "This day plan has too many activities.");

        // The order the user approved becomes the stored order: a contiguous
        // SortIndex block appended after whatever is already on that day.
        var nextSortIndex = (await _db.TripActivities
            .Where(a => a.TripId == tripId && a.Date == date)
            .Select(a => (int?)a.SortIndex)
            .MaxAsync(ct) ?? -1) + 1;

        foreach (var draft in drafts)
        {
            var rangeError = ValidateActivityDates(trip, draft);
            if (rangeError != null) return rangeError;

            await CreateActivityAsync(trip, userId, draft, changes, ct, explicitSortIndex: nextSortIndex++);
        }

        return null;
    }

    private async Task<(string, string)?> ApplyDayLocationAsync(
        Guid tripId, Guid userId, JsonElement payload, GlunoApplyChanges changes, CancellationToken ct)
    {
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return ("trip_missing", "That Adventure no longer exists.");

        if (!TryReadDate(payload, "date", out var date))
            return ("invalid_payload", "This suggestion has no valid date.");
        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return ("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        var label = ReadText(payload, "label", 120);
        if (label == null) return ("invalid_payload", "This suggestion has no place name.");

        var latitude = ReadNumber(payload, "latitude");
        var longitude = ReadNumber(payload, "longitude");
        if (!TripEditRules.IsValidCoordinate(latitude, longitude))
        {
            // TripDayLocation requires coordinates; a label alone cannot place
            // a day on the map or drive its weather.
            return ("missing_coordinates", "This place has no coordinates, so it cannot be set as a day location.");
        }

        var existing = await _db.TripDayLocations
            .Where(d => d.TripId == tripId && d.StartDate == date)
            .OrderBy(d => d.SortIndex)
            .ToListAsync(ct);

        // Editing an existing row versus adding a stop is the user's choice in
        // the preview, expressed as the target position. Absent means append.
        var requestedIndex = ReadNumber(payload, "sortIndex") is { } raw ? (int)raw : (int?)null;
        var replaceExisting = payload.TryGetProperty("replaceExisting", out var replace) && replace.ValueKind == JsonValueKind.True;

        if (replaceExisting && existing.Count > 0)
        {
            var target = requestedIndex.HasValue
                ? existing.FirstOrDefault(d => d.SortIndex == requestedIndex.Value) ?? existing[0]
                : existing[0];

            target.LocationLabel = label;
            target.Latitude = latitude!.Value;
            target.Longitude = longitude!.Value;
            target.PlaceId = ReadText(payload, "placeId", 200);
            target.UpdatedAt = DateTime.UtcNow;
            changes.UpdatedDayLocationIds.Add(target.Id);
        }
        else
        {
            // Appended at the end. SortIndex 0 stays the day's MAIN location
            // and keeps its carry-forward behaviour — an added stop belongs to
            // its own day only, and this must not change that.
            var created = new TripDayLocation
            {
                TripId = tripId,
                StartDate = date,
                SortIndex = existing.Count,
                LocationLabel = label,
                Latitude = latitude!.Value,
                Longitude = longitude!.Value,
                PlaceId = ReadText(payload, "placeId", 200),
                CreatedByUserId = userId,
            };
            _db.TripDayLocations.Add(created);
            changes.CreatedDayLocationIds.Add(created.Id);
        }

        AddAffectedDate(changes, date);
        return null;
    }

    private async Task<(string, string)?> ApplyActivityMoveAsync(
        Guid tripId, Guid userId, JsonElement payload, GlunoApplyChanges changes, CancellationToken ct)
    {
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return ("trip_missing", "That Adventure no longer exists.");

        if (!TryReadGuid(payload, "activityId", out var activityId))
            return ("invalid_payload", "This suggestion does not name an activity.");
        if (!TryReadDate(payload, "toDate", out var toDate))
            return ("invalid_payload", "This suggestion has no valid date.");
        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, toDate))
            return ("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        var activity = await _db.TripActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.TripId == tripId, ct);
        if (activity == null) return ("activity_missing", "That activity is no longer part of this Adventure.");

        // Someone else's unrevealed SideQuest is not this user's to move.
        if (activity.IsHidden && activity.OwnerId != userId)
            return ("activity_missing", "That activity is no longer part of this Adventure.");

        var fromDate = activity.Date;

        // A hotel stay moves as a whole: check-out shifts by the same number
        // of days, and the shifted stay is re-validated. Same rule as the
        // ordinary move endpoint.
        if (activity.EndDate.HasValue)
        {
            var deltaDays = toDate.DayNumber - activity.Date.DayNumber;
            var shiftedEnd = DateOnly.FromDayNumber(activity.EndDate.Value.DayNumber + deltaDays);
            var stayError = TripEditRules.ValidateStayRange(toDate, shiftedEnd, activity.EndTime, trip);
            if (stayError != null) return ("invalid_stay", stayError);
            activity.EndDate = shiftedEnd;
        }

        var toTime = ReadTime(payload, "toTime");
        if (toTime != null) activity.Time = toTime;

        activity.Date = toDate;
        // Placement uses the same chronological insert as the app, so a moved
        // activity lands where a person dragging it would expect.
        activity.SortIndex = await TripEditRules.InsertChronologicallyAsync(
            _db, tripId, toDate, activity.Time, activity.Id, ct);

        changes.UpdatedActivityIds.Add(activity.Id);
        AddAffectedDate(changes, fromDate);
        AddAffectedDate(changes, toDate);
        return null;
    }

    private async Task<(string, string)?> ApplyTripDatesAsync(
        Guid tripId, Guid userId, JsonElement payload, GlunoApplyChanges changes, CancellationToken ct)
    {
        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return ("trip_missing", "That Adventure no longer exists.");

        // Trip dates are an OWNER decision in the app; the assistant does not
        // get a wider permission than the Edit Adventure screen.
        var isOwner = await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId && tm.IsOwner, ct);
        if (!isOwner) return ("not_owner", "Only an Adventure's owner can change its dates.");

        var startDate = TryReadDate(payload, "startDate", out var parsedStart) ? parsedStart : trip.StartDate;
        var clearEndDate = payload.TryGetProperty("clearEndDate", out var clear) && clear.ValueKind == JsonValueKind.True;
        DateOnly? endDate = clearEndDate
            ? null
            : TryReadDate(payload, "endDate", out var parsedEnd) ? parsedEnd : trip.EndDate;

        var rangeError = TripEditRules.ValidateTripDateRange(startDate, endDate);
        if (rangeError != null) return ("invalid_dates", rangeError);

        // The app refuses to strand activities and sends the user through the
        // reschedule flow instead. Gluno must not be a way around that, so the
        // same refusal applies here — it never silently moves anything.
        var stranded = await TripEditRules.FindStrandedActivitiesAsync(_db, tripId, startDate, endDate, ct);
        if (stranded.Count > 0)
        {
            return ("would_strand_activities",
                $"{stranded.Count} planned {(stranded.Count == 1 ? "activity falls" : "activities fall")} outside these dates. Reschedule them in the Adventure first.");
        }

        trip.StartDate = startDate;
        trip.EndDate = endDate;
        changes.TripDatesChanged = true;
        return null;
    }

    // ── Shared activity creation ──────────────────────────────────────────

    private async Task CreateActivityAsync(
        Trip trip,
        Guid userId,
        GlunoActivityDraft draft,
        GlunoApplyChanges changes,
        CancellationToken ct,
        int? explicitSortIndex = null)
    {
        var sortIndex = explicitSortIndex
            ?? await TripEditRules.InsertChronologicallyAsync(_db, trip.Id, draft.Date, draft.Time, null, ct);

        var activity = new TripActivity
        {
            TripId = trip.Id,
            Date = draft.Date,
            SortIndex = sortIndex,
            Title = draft.Title,
            // Location rides in the description under the app's existing
            // marker format (lib/sidequest-location.ts) — the same structured
            // storage the Activity form itself uses. A provider link is NOT
            // written here: there is no field for it, and hiding a URL in the
            // description would be worse than leaving it on the Gluno card.
            Description = GlunoActivityLocation.WithLocationMarker(draft.Description, draft.LocationLabel),
            Time = draft.Time,
            EndDate = draft.EndDate,
            EndTime = draft.EndDate.HasValue ? draft.EndTime : null,
            Category = draft.Category,
            // Always public: an assistant must never create a hidden SideQuest
            // on someone's behalf — a surprise is the creator's to author.
            Visibility = "public",
            IsHidden = false,
            OwnerId = userId,
            AssignedToUserId = userId,
        };

        _db.TripActivities.Add(activity);

        // Same feed entry the ordinary create writes, so a Gluno-made activity
        // is visible in the Adventure's history like any other.
        var actor = await _db.Users.FindAsync([userId], ct);
        _db.TripEvents.Add(new TripEvent
        {
            TripId = trip.Id,
            ActorId = userId,
            ActorName = DisplayNameHelper.OrFallback(actor?.Name),
            Type = "activity_added",
            ActivityId = activity.Id,
            IsHidden = false,
            CreatedAt = DateTime.UtcNow,
        });

        changes.CreatedActivityIds.Add(activity.Id);
        AddAffectedDate(changes, draft.Date);
    }

    private static (string, string)? ValidateActivityDates(Trip trip, GlunoActivityDraft draft)
    {
        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, draft.Date))
            return ("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        var stayError = TripEditRules.ValidateStayRange(draft.Date, draft.EndDate, draft.EndTime, trip);
        return stayError != null ? ("invalid_stay", stayError) : null;
    }

    // ── Status helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Records a failed apply.
    ///
    /// The <c>ChangeTracker.Clear()</c> is load-bearing, not tidiness. An
    /// aborted apply can leave half-built entities tracked — a day plan that
    /// added three activities before the fourth was rejected, say — and the
    /// transaction rollback undoes the DATABASE work but not the tracker.
    /// Saving the failure status without clearing first would flush exactly
    /// those pending inserts, outside the transaction, and create the partial
    /// day the rollback just prevented.
    /// </summary>
    private async Task<GlunoApplyResult> MarkFailedAsync(
        Guid proposalId, string code, string message, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();

        var proposal = await _db.GlunoProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal == null) return new GlunoApplyResult { Error = GlunoApplyError.NotFound };

        // Failed, not terminal: the user can edit the suggestion and try
        // again, and a transient problem should not kill a good proposal.
        proposal.Status = GlunoProposalStatuses.Failed;
        proposal.FailureCode = code;
        proposal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new GlunoApplyResult
        {
            Error = GlunoApplyError.Failed,
            Proposal = proposal,
            FailureCode = code,
            Message = message,
        };
    }

    private Task ReleaseClaimAsync(Guid proposalId, CancellationToken _)
        => _db.GlunoProposals
            .Where(p => p.Id == proposalId && p.Status == GlunoProposalStatuses.Applying)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, GlunoProposalStatuses.Pending)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow), CancellationToken.None);

    private static GlunoProposalSnapshot? ReadSnapshot(GlunoProposalRecord proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.SnapshotJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<GlunoProposalSnapshot>(proposal.SnapshotJson, GlunoJson.Options);
        }
        catch (JsonException)
        {
            // An unreadable snapshot must not block a legitimate apply; the
            // per-action validation below still guards the write.
            return null;
        }
    }

    private static GlunoApplyChanges ReadChanges(GlunoProposalRecord proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ResultJson)) return new GlunoApplyChanges();
        try
        {
            return JsonSerializer.Deserialize<GlunoApplyChanges>(proposal.ResultJson, GlunoJson.Options)
                   ?? new GlunoApplyChanges();
        }
        catch (JsonException)
        {
            return new GlunoApplyChanges();
        }
    }

    private static void AddAffectedDate(GlunoApplyChanges changes, DateOnly date)
    {
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!changes.AffectedDates.Contains(iso)) changes.AffectedDates.Add(iso);
    }

    // ── Payload reading ───────────────────────────────────────────────────

    private static bool TryReadDate(JsonElement payload, string name, out DateOnly value)
    {
        value = default;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        return DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = Guid.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.String) return false;
        return Guid.TryParse(element.GetString(), out value);
    }

    internal static string? ReadText(JsonElement payload, string name, int maxLength)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;

        var text = element.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    internal static double? ReadNumber(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var element)) return null;
        return element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;
    }

    internal static string? ReadTime(JsonElement payload, string name)
    {
        var raw = ReadText(payload, name, 5);
        if (raw == null) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(raw, @"^([01]\d|2[0-3]):[0-5]\d$") ? raw : null;
    }
}

/// <summary>
/// One activity's worth of an edited proposal payload, validated.
///
/// A struct in front of the JSON so nothing reaches EF straight from a parsed
/// document: every field below is read by name, type-checked and length-capped
/// first. The model's raw arguments never become entity state.
/// </summary>
internal sealed class GlunoActivityDraft
{
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Time { get; private set; }
    public DateOnly? EndDate { get; private set; }
    public string? EndTime { get; private set; }
    public string? Category { get; private set; }
    public string? LocationLabel { get; private set; }
    public string? Error { get; private set; }

    public static GlunoActivityDraft Read(JsonElement element, DateOnly? fallbackDate = null)
    {
        var draft = new GlunoActivityDraft();

        var title = GlunoProposalApplyService.ReadText(element, "title", 120);
        if (title == null)
        {
            draft.Error = "This suggestion has no title.";
            return draft;
        }
        draft.Title = title;

        if (element.TryGetProperty("date", out var dateElement)
            && dateElement.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            draft.Date = date;
        }
        else if (fallbackDate.HasValue)
        {
            draft.Date = fallbackDate.Value;
        }
        else
        {
            draft.Error = "This suggestion has no valid date.";
            return draft;
        }

        draft.Description = GlunoProposalApplyService.ReadText(element, "description", 2000);
        draft.Time = GlunoProposalApplyService.ReadTime(element, "time");
        draft.EndTime = GlunoProposalApplyService.ReadTime(element, "endTime");
        draft.Category = GlunoProposalApplyService.ReadText(element, "category", 40);
        draft.LocationLabel = GlunoProposalApplyService.ReadText(element, "locationLabel", 200);

        if (element.TryGetProperty("endDate", out var endElement)
            && endElement.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(endElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            draft.EndDate = endDate;
        }

        return draft;
    }
}

/// <summary>
/// Server-side writer for the app's existing activity-location marker.
///
/// Mirrors mobile/lib/sidequest-location.ts. An Activity has no location
/// column — the app stores it as a marker line in the description and parses
/// it back out — so writing the same marker is reusing the app's mechanism,
/// not smuggling data into free text.
///
/// Only the plain location marker is written. The richer place marker carries
/// a Google place id, which Gluno's provider results do not have; inventing
/// one would break the map links the app builds from it.
/// </summary>
internal static class GlunoActivityLocation
{
    private const string LocationMarker = "[map-location]:";

    public static string? WithLocationMarker(string? description, string? locationLabel)
    {
        var label = locationLabel?.Trim();
        if (string.IsNullOrWhiteSpace(label)) return description;

        var encoded = Uri.EscapeDataString(label);
        return string.IsNullOrWhiteSpace(description)
            ? $"{LocationMarker}{encoded}"
            : $"{description}\n\n{LocationMarker}{encoded}";
    }
}
