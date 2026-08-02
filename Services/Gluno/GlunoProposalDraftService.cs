using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoDraftError
{
    None,
    NotFound,
    Forbidden,
    /// Expired, applied, cancelled or failed.
    NotUsable,
    /// The draft or its conflicts moved on since the card was built.
    Stale,
    /// The strategy is not one this conflict allows any more.
    StrategyNotAllowed,
    /// Rebuilt to the limit, or the same fix tried twice.
    OutOfRebuilds,
}

public sealed record GlunoDraftResult(GlunoDraftError Error, GlunoProposalDraft? Draft)
{
    public IReadOnlyList<GlunoProposalConflict> Conflicts { get; init; } = Array.Empty<GlunoProposalConflict>();
}

public interface IGlunoProposalDraftService
{
    /// <summary>
    /// Records what the model produced, before anything can act on it.
    ///
    /// Every applicable proposal starts here — that is the invariant the whole
    /// flow rests on.
    /// </summary>
    Task<GlunoProposalDraft> CreateAsync(GlunoProposalDraft draft, CancellationToken ct);

    Task<GlunoProposalDraft?> GetOwnedAsync(Guid draftId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Replaces the payload and moves <c>DraftVersion</c>.
    ///
    /// Version movement is server-side and unconditional: a caller cannot
    /// change content without the version moving, which is what makes a stale
    /// tap detectable at all.
    /// </summary>
    Task<GlunoProposalDraft?> UpdatePayloadAsync(
        Guid draftId, Guid userId, string payloadJson, CancellationToken ct);

    /// Records the conflicts a validation produced and moves ConflictVersion.
    Task<GlunoProposalDraft?> RecordConflictsAsync(
        Guid draftId, Guid userId, bool hasConflicts, CancellationToken ct);

    /// <summary>
    /// Checks a tap against the draft's current state before anything is
    /// rebuilt. This is the gate every continuation passes through.
    /// </summary>
    Task<GlunoDraftResult> ValidateResolveAsync(
        Guid draftId, Guid userId, int draftVersion, int conflictVersion,
        string conflictType, string strategy, CancellationToken ct);

    /// Records a rebuild: the counter, and which fix was tried on which conflict.
    Task<GlunoProposalDraft?> RecordRebuildAsync(
        Guid draftId, Guid userId, string conflictType, string strategy, CancellationToken ct);

    /// <summary>
    /// Records that the user accepted a conflict type, so the gate does not ask
    /// about that same accepted uncertainty again.
    ///
    /// Moves DraftVersion only when the acceptance is new — an unchanged draft
    /// must not look changed, or a card built moments ago would read as stale.
    /// </summary>
    Task<GlunoProposalDraft?> AcceptConflictAsync(
        Guid draftId, Guid userId, string conflictType, CancellationToken ct);

    Task<GlunoProposalDraft?> SetStatusAsync(
        Guid draftId, Guid userId, string status, Guid? proposalId, CancellationToken ct);
}

/// <summary>
/// Owns a suggestion from the moment the model produces it until it becomes a
/// proposal, or does not.
///
/// WHY A SERVICE AND NOT INLINE STATE. Three things have to be true at once,
/// and only a single owner can guarantee them.
///
/// A version must move whenever content moves. If the caller could update a
/// payload without bumping DraftVersion, a stale tap would be undetectable and
/// would resolve the wrong conflict.
///
/// A status transition must be one-way at the right moments. `applied` is
/// terminal; a draft that could go back to `awaiting_clarification` afterwards
/// would offer to rebuild something already written to the Adventure.
///
/// And the loop guard has to be checked before work is spent, not after. Three
/// wasted model rounds is a bad way to discover that the same fix keeps being
/// tried.
///
/// NO WRITES TO AN ADVENTURE HAPPEN HERE. Nothing in this file touches
/// TripActivities. A draft is a conversation about a change, not the change.
/// </summary>
public sealed class GlunoProposalDraftService : IGlunoProposalDraftService
{
    private readonly AppDbContext _db;
    private readonly ILogger<GlunoProposalDraftService> _logger;

    public GlunoProposalDraftService(AppDbContext db, ILogger<GlunoProposalDraftService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoProposalDraft> CreateAsync(GlunoProposalDraft draft, CancellationToken ct)
    {
        _db.GlunoProposalDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        // Status and shape only — never the payload, the title or the dates.
        _logger.LogInformation(
            "[GLUNO] draft created action={Action} status={Status}",
            draft.ActionType, draft.Status);

        return draft;
    }

    public Task<GlunoProposalDraft?> GetOwnedAsync(Guid draftId, Guid userId, CancellationToken ct)
        => _db.GlunoProposalDrafts
            .FirstOrDefaultAsync(row => row.Id == draftId && row.UserId == userId, ct);

    public async Task<GlunoProposalDraft?> UpdatePayloadAsync(
        Guid draftId, Guid userId, string payloadJson, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);
        if (draft == null || !draft.IsUsable) return draft;

        draft.PayloadJson = payloadJson;
        // Unconditional. Content changed, so the version moves — there is no
        // path that edits a draft quietly.
        draft.DraftVersion++;
        draft.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<GlunoProposalDraft?> RecordConflictsAsync(
        Guid draftId, Guid userId, bool hasConflicts, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);
        if (draft == null || !draft.IsUsable) return draft;

        draft.ConflictVersion++;
        draft.Status = hasConflicts
            ? GlunoProposalDraftStatuses.AwaitingClarification
            : GlunoProposalDraftStatuses.ReadyForApproval;
        draft.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[GLUNO] draft validated status={Status} draftVersion={DraftVersion} "
            + "conflictVersion={ConflictVersion} rebuilds={Rebuilds}",
            draft.Status, draft.DraftVersion, draft.ConflictVersion, draft.RebuildCount);

        return draft;
    }

    public async Task<GlunoDraftResult> ValidateResolveAsync(
        Guid draftId, Guid userId, int draftVersion, int conflictVersion,
        string conflictType, string strategy, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);

        if (draft == null) return new GlunoDraftResult(GlunoDraftError.NotFound, null);

        // Expired, applied, cancelled, failed. A terminal draft is never
        // rebuilt — that is what stops a resolve arriving after an apply from
        // producing a second change.
        if (!draft.IsUsable) return new GlunoDraftResult(GlunoDraftError.NotUsable, draft);

        if (draft.Status != GlunoProposalDraftStatuses.AwaitingClarification)
            return new GlunoDraftResult(GlunoDraftError.NotUsable, draft);

        // ── Both versions, checked separately ─────────────────────────────
        //
        // A tap carrying an old DraftVersion is answering about different
        // content; an old ConflictVersion is answering a question that has
        // since been recomputed. Either way the answer is about a plan that no
        // longer exists, and applying it would fix the wrong thing.
        if (draft.DraftVersion != draftVersion || draft.ConflictVersion != conflictVersion)
        {
            _logger.LogInformation(
                "[GLUNO] draft resolve stale draftVersion={DraftVersion} conflictVersion={ConflictVersion}",
                draft.DraftVersion, draft.ConflictVersion);

            return new GlunoDraftResult(GlunoDraftError.Stale, draft);
        }

        // ── The loop guard, before any work ───────────────────────────────
        if (draft.IsOutOfRebuilds)
            return new GlunoDraftResult(GlunoDraftError.OutOfRebuilds, draft);

        // The same fix on the same conflict against unchanged state cannot
        // produce a different outcome. Caught here rather than after a wasted
        // model round.
        if (draft.WouldRepeat(conflictType, strategy))
            return new GlunoDraftResult(GlunoDraftError.OutOfRebuilds, draft);

        if (!GlunoConflictStrategies.IsKnown(strategy))
            return new GlunoDraftResult(GlunoDraftError.StrategyNotAllowed, draft);

        return new GlunoDraftResult(GlunoDraftError.None, draft);
    }

    public async Task<GlunoProposalDraft?> RecordRebuildAsync(
        Guid draftId, Guid userId, string conflictType, string strategy, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);
        if (draft == null) return null;

        draft.RebuildCount++;
        draft.LastConflictType = conflictType;
        draft.LastStrategy = strategy;
        draft.UpdatedAt = DateTime.UtcNow;

        // Out of attempts and still conflicting. Failing plainly beats
        // bouncing between two cards until something else stops it.
        if (draft.IsOutOfRebuilds && draft.Status == GlunoProposalDraftStatuses.AwaitingClarification)
        {
            draft.Status = GlunoProposalDraftStatuses.Failed;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[GLUNO] draft rebuilt conflict={Conflict} strategy={Strategy} rebuilds={Rebuilds} status={Status}",
            conflictType, strategy, draft.RebuildCount, draft.Status);

        return draft;
    }

    public async Task<GlunoProposalDraft?> AcceptConflictAsync(
        Guid draftId, Guid userId, string conflictType, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);
        if (draft == null || !draft.IsUsable) return draft;

        // Only on a real change. "Keep both" tapped twice is one acceptance,
        // and bumping the version for the second tap would make every card
        // built from the first one stale for no reason.
        if (!draft.Accept(conflictType)) return draft;

        draft.DraftVersion++;
        draft.LastConflictType = conflictType;
        draft.LastStrategy = GlunoConflictStrategies.KeepBoth;
        draft.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<GlunoProposalDraft?> SetStatusAsync(
        Guid draftId, Guid userId, string status, Guid? proposalId, CancellationToken ct)
    {
        var draft = await GetOwnedAsync(draftId, userId, ct);
        if (draft == null) return null;

        // `applied` is terminal. A draft that could return to
        // awaiting_clarification afterwards would offer to rebuild something
        // already written to the Adventure.
        if (draft.Status == GlunoProposalDraftStatuses.Applied) return draft;

        draft.Status = status;
        if (proposalId.HasValue) draft.ProposalId = proposalId;
        draft.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>
    /// Applies a strategy that needs no model.
    ///
    /// Returns the new payload, or null when the strategy is not one of the
    /// deterministic ones — the caller then knows a rebuild is required rather
    /// than assuming the draft is unchanged.
    /// </summary>
    public static string? ApplyDeterministic(
        string payloadJson, string strategy, IReadOnlyList<int> affectedIndexes)
    {
        switch (strategy)
        {
            // Drop the suggested row. Nothing else about the day changes, so
            // there is nothing to re-plan.
            case GlunoConflictStrategies.RemoveNew:
                return RemoveRows(payloadJson, affectedIndexes);

            // The plan already works; the user has decided the clash is
            // acceptable. The strategy builder only offers this where the
            // validator agrees it is survivable.
            case GlunoConflictStrategies.KeepBoth:
                return payloadJson;

            default:
                return null;
        }
    }

    /// <summary>
    /// Removes rows from a day-plan payload by index.
    ///
    /// Rebuilds the array rather than mutating: the payload is a detached
    /// document, and an in-place edit of a JsonElement is not a thing.
    /// </summary>
    private static string? RemoveRows(string payloadJson, IReadOnlyList<int> indexes)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("activities", out var activities)
                || activities.ValueKind != JsonValueKind.Array)
            {
                // A single-activity proposal: removing its only row leaves
                // nothing to propose, which the caller treats as a cancel.
                return null;
            }

            var kept = activities.EnumerateArray()
                .Where((_, index) => !indexes.Contains(index))
                .ToList();

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("activities")) continue;
                    property.WriteTo(writer);
                }

                writer.WritePropertyName("activities");
                writer.WriteStartArray();
                foreach (var row in kept) row.WriteTo(writer);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
