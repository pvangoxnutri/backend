using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoClarificationError
{
    None,
    NotFound,
    Forbidden,
    /// Already answered, cancelled, or past its expiry.
    NotAnswerable,
    /// The option is no longer valid — a deleted Adventure, a left group.
    OptionStale,
    UnknownOption,
}

public sealed record GlunoClarificationResult(
    GlunoClarificationError Error,
    GlunoClarification? Clarification)
{
    /// The value the continuation should run with.
    public GlunoClarificationOption? Selected { get; init; }

    /// True when this tap was a repeat and the first one already answered.
    public bool WasAlreadyResolved { get; init; }
}

public interface IGlunoClarificationService
{
    Task<GlunoClarification> CreateAsync(
        GlunoClarification clarification, IReadOnlyList<GlunoOptionDraft> options, CancellationToken ct);

    /// <summary>
    /// Claims the choice. Atomic: the FIRST caller gets it, and a second tap
    /// is told the answer already exists rather than running the work twice.
    /// </summary>
    Task<GlunoClarificationResult> ResolveAsync(
        Guid clarificationId, Guid userId, string optionKey, CancellationToken ct);

    Task<GlunoClarificationError> CancelAsync(Guid clarificationId, Guid userId, CancellationToken ct);

    /// Binds the answer that was produced, so a repeat tap replays it.
    Task RecordContinuationAsync(Guid clarificationId, Guid messageId, CancellationToken ct);

    /// One clarification, scoped to its owner.
    Task<GlunoClarification?> GetOwnedAsync(Guid clarificationId, Guid userId, CancellationToken ct);

    /// Replaces the options with search results. Same question, new answers.
    Task<GlunoClarification?> ReplaceOptionsAsync(
        Guid clarificationId, Guid userId, IReadOnlyList<GlunoOptionDraft> options, CancellationToken ct);

    Task<GlunoClarification?> GetForConversationAsync(
        Guid conversationId, Guid userId, CancellationToken ct);
}

/// <summary>
/// Stores a question, and hands out its answer exactly once.
///
/// TWO THINGS THIS OWNS, AND THEY ARE BOTH ABOUT TIME. Options are built when
/// the question is asked and honoured when the user taps — which can be an
/// hour later, after the Adventure was deleted or they left the group. So
/// every resolve re-verifies access against the state NOW, not the state the
/// options were built from.
///
/// And a tap must count once. A double tap, a retry after a dropped
/// connection, a resend from a flaky network: all three must produce one
/// continuation and one answer. That is a status transition guarded by the
/// database, not a flag checked in application code.
/// </summary>
public sealed class GlunoClarificationService : IGlunoClarificationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GlunoClarificationService> _logger;

    public GlunoClarificationService(AppDbContext db, ILogger<GlunoClarificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoClarification> CreateAsync(
        GlunoClarification clarification, IReadOnlyList<GlunoOptionDraft> options, CancellationToken ct)
    {
        var index = 0;

        foreach (var draft in options)
        {
            clarification.Options.Add(new GlunoClarificationOption
            {
                ClarificationId = clarification.Id,
                OptionKey = draft.Key,
                Label = draft.Label,
                Description = draft.Description,
                Icon = draft.Icon,
                EntityType = draft.EntityType,
                EntityId = draft.EntityId,
                Value = draft.Value,
                Disabled = draft.Disabled,
                DisabledReason = draft.DisabledReason,
                SortIndex = index++,
            });
        }

        _db.GlunoClarifications.Add(clarification);
        await _db.SaveChangesAsync(ct);

        // Type and count only — never the question, the labels or the ids.
        _logger.LogInformation(
            "[GLUNO] clarification asked type={Type} options={Count}",
            clarification.Type, clarification.Options.Count);

        return clarification;
    }

    public async Task<GlunoClarificationResult> ResolveAsync(
        Guid clarificationId, Guid userId, string optionKey, CancellationToken ct)
    {
        // Scoped to the caller in the QUERY. An id from somebody else's
        // conversation does not resolve, so there is no branch where the wrong
        // clarification is loaded and then checked.
        var clarification = await _db.GlunoClarifications
            .Include(row => row.Options)
            .FirstOrDefaultAsync(row => row.Id == clarificationId && row.UserId == userId, ct);

        if (clarification == null)
            return new GlunoClarificationResult(GlunoClarificationError.NotFound, null);

        var option = clarification.Options.FirstOrDefault(row => row.OptionKey == optionKey);

        // ── The repeat tap ────────────────────────────────────────────────
        //
        // Answered already. Returning the FIRST answer rather than an error is
        // what makes a double tap harmless: the user sees the same reply
        // appear, which is what they expected from tapping.
        if (clarification.Status == GlunoClarificationStatuses.Resolved)
        {
            var previous = clarification.Options
                .FirstOrDefault(row => row.Id == clarification.SelectedOptionId);

            return new GlunoClarificationResult(GlunoClarificationError.None, clarification)
            {
                Selected = previous,
                WasAlreadyResolved = true,
            };
        }

        if (!clarification.IsAnswerable)
        {
            // Expiry is discovered lazily rather than swept: nothing else needs
            // to know, and a background job to age out questions nobody
            // answered would be work for its own sake.
            if (clarification.Status == GlunoClarificationStatuses.Pending
                && clarification.ExpiresAt <= DateTime.UtcNow)
            {
                clarification.Status = GlunoClarificationStatuses.Expired;
                await _db.SaveChangesAsync(ct);
            }

            return new GlunoClarificationResult(GlunoClarificationError.NotAnswerable, clarification);
        }

        if (option == null)
            return new GlunoClarificationResult(GlunoClarificationError.UnknownOption, clarification);

        if (option.Disabled)
            return new GlunoClarificationResult(GlunoClarificationError.OptionStale, clarification);

        // ── Re-verify, at TAP time ────────────────────────────────────────
        //
        // The options were built when the question was asked. Between then and
        // now the Adventure can have been deleted, the user can have left the
        // group, the Activity can have been removed. Trusting the snapshot
        // would turn a stale button into an access path.
        if (!await IsStillValidAsync(option, userId, ct))
        {
            clarification.Status = GlunoClarificationStatuses.Stale;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[GLUNO] clarification option no longer valid type={Type} entity={Entity}",
                clarification.Type, option.EntityType);

            return new GlunoClarificationResult(GlunoClarificationError.OptionStale, clarification);
        }

        // ── Claim it ──────────────────────────────────────────────────────
        //
        // A conditional update, so two taps racing on two connections cannot
        // both win. The loser reads the row again and takes the repeat path
        // above.
        var claimed = await _db.GlunoClarifications
            .Where(row => row.Id == clarificationId
                && row.Status == GlunoClarificationStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, GlunoClarificationStatuses.Resolved)
                .SetProperty(row => row.SelectedOptionId, option.Id)
                .SetProperty(row => row.ResolvedAt, DateTime.UtcNow), ct);

        if (claimed == 0)
        {
            // Somebody else got there first. Not an error — the same outcome.
            return new GlunoClarificationResult(GlunoClarificationError.None, clarification)
            {
                Selected = option,
                WasAlreadyResolved = true,
            };
        }

        return new GlunoClarificationResult(GlunoClarificationError.None, clarification)
        {
            Selected = option,
        };
    }

    public async Task<GlunoClarificationError> CancelAsync(
        Guid clarificationId, Guid userId, CancellationToken ct)
    {
        var cancelled = await _db.GlunoClarifications
            .Where(row => row.Id == clarificationId
                && row.UserId == userId
                && row.Status == GlunoClarificationStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, GlunoClarificationStatuses.Cancelled)
                .SetProperty(row => row.ResolvedAt, DateTime.UtcNow), ct);

        // Cancelling something already closed is the same outcome as
        // cancelling it now.
        return cancelled == 0 ? GlunoClarificationError.NotFound : GlunoClarificationError.None;
    }

    public Task<GlunoClarification?> GetOwnedAsync(
        Guid clarificationId, Guid userId, CancellationToken ct)
        => _db.GlunoClarifications
            .Include(row => row.Options)
            .FirstOrDefaultAsync(row => row.Id == clarificationId && row.UserId == userId, ct);

    /// <summary>
    /// Swaps the options for search results.
    ///
    /// The QUESTION does not change — only what can answer it. Keys are rebuilt
    /// server-side, so a result the user just searched for is as verifiable at
    /// resolve time as one that was offered originally.
    ///
    /// Refuses on anything not still open, so a search cannot revive a
    /// clarification that was answered or expired.
    /// </summary>
    public async Task<GlunoClarification?> ReplaceOptionsAsync(
        Guid clarificationId, Guid userId, IReadOnlyList<GlunoOptionDraft> options, CancellationToken ct)
    {
        var clarification = await _db.GlunoClarifications
            .Include(row => row.Options)
            .FirstOrDefaultAsync(row => row.Id == clarificationId && row.UserId == userId, ct);

        if (clarification == null || !clarification.IsAnswerable) return clarification;

        // Nothing matched. The previous options stay so the card can offer
        // them back rather than becoming an empty list.
        if (options.Count == 0) return clarification;

        _db.GlunoClarificationOptions.RemoveRange(clarification.Options);
        clarification.Options.Clear();

        var index = 0;
        foreach (var draft in options)
        {
            clarification.Options.Add(new GlunoClarificationOption
            {
                ClarificationId = clarification.Id,
                OptionKey = draft.Key,
                Label = draft.Label,
                Description = draft.Description,
                Icon = draft.Icon,
                EntityType = draft.EntityType,
                EntityId = draft.EntityId,
                Value = draft.Value,
                SortIndex = index++,
            });
        }

        await _db.SaveChangesAsync(ct);

        // Counts only — never the query or what it matched.
        _logger.LogInformation(
            "[GLUNO] clarification searched type={Type} results={Count}",
            clarification.Type, options.Count);

        return clarification;
    }

    public Task RecordContinuationAsync(Guid clarificationId, Guid messageId, CancellationToken ct)
        => _db.GlunoClarifications
            .Where(row => row.Id == clarificationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ContinuationMessageId, messageId), ct);

    public Task<GlunoClarification?> GetForConversationAsync(
        Guid conversationId, Guid userId, CancellationToken ct)
        => _db.GlunoClarifications
            .Include(row => row.Options)
            .Where(row => row.ConversationId == conversationId && row.UserId == userId)
            .Where(row => row.Status == GlunoClarificationStatuses.Pending)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Whether the thing an option points at is still there and still the
    /// caller's to choose.
    ///
    /// An enum option has nothing behind it — the value came from a fixed
    /// allow-list and cannot rot.
    /// </summary>
    private async Task<bool> IsStillValidAsync(
        GlunoClarificationOption option, Guid userId, CancellationToken ct)
        => option.EntityType switch
        {
            GlunoClarificationEntityTypes.Trip when option.EntityId is { } tripId
                => await _db.TripMembers.AnyAsync(
                    member => member.TripId == tripId && member.UserId == userId, ct),

            GlunoClarificationEntityTypes.Activity when option.EntityId is { } activityId
                => await _db.TripActivities
                    .Join(_db.TripMembers.Where(member => member.UserId == userId),
                        activity => activity.TripId, member => member.TripId, (activity, _) => activity)
                    .AnyAsync(activity => activity.Id == activityId, ct),

            // A date and a vocabulary value are self-describing.
            GlunoClarificationEntityTypes.Date
                or GlunoClarificationEntityTypes.Enum
                or GlunoClarificationEntityTypes.ExternalPlace => true,

            _ => false,
        };
}
