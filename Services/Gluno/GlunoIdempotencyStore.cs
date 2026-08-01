using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoIdempotencyOutcome
{
    /// No prior request with this key. Proceed.
    Proceed,
    /// An identical request is running right now.
    AlreadyInFlight,
    /// An identical request finished. Return what it produced.
    AlreadyCompleted,
}

public sealed record GlunoIdempotencyCheck(
    GlunoIdempotencyOutcome Outcome,
    GlunoTurnRequest? Existing);

public interface IGlunoIdempotencyStore
{
    /// <summary>
    /// Claims a key, or reports that it is taken.
    ///
    /// The claim is ATOMIC: two concurrent requests with the same key must not
    /// both proceed, or a double tap produces two model turns and two
    /// proposals — which is the failure this whole type exists to prevent.
    /// </summary>
    Task<GlunoIdempotencyCheck> ClaimAsync(
        string? key, Guid userId, Guid conversationId, CancellationToken ct);

    Task CompleteAsync(Guid requestId, Guid assistantMessageId, CancellationToken ct);
    Task FailAsync(Guid requestId, string failureCode, CancellationToken ct);
    Task CancelAsync(Guid requestId, CancellationToken ct);
}

/// <summary>
/// Idempotency for chat sends.
///
/// A key that is absent or malformed simply proceeds without protection —
/// deliberately. An older client that does not send one must keep working, and
/// refusing the request would break chat for everyone mid-rollout to protect
/// against a duplicate.
/// </summary>
public sealed class GlunoIdempotencyStore : IGlunoIdempotencyStore
{
    /// <summary>
    /// A key must be a plain opaque token. Validated for SHAPE rather than
    /// parsed: it is client-supplied text that becomes a database predicate,
    /// and there is no reason to accept anything but this.
    /// </summary>
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);

    /// <summary>
    /// How long an in-flight claim stays authoritative.
    ///
    /// A process that dies mid-turn leaves its row at in_flight forever, and
    /// the user would never be able to send that message again. Past this, the
    /// claim is treated as abandoned — comfortably longer than the longest
    /// latency budget, so it can never fire on a turn that is genuinely still
    /// running.
    /// </summary>
    private static readonly TimeSpan InFlightTimeout = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly ILogger<GlunoIdempotencyStore> _logger;

    public GlunoIdempotencyStore(AppDbContext db, ILogger<GlunoIdempotencyStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoIdempotencyCheck> ClaimAsync(
        string? key, Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (key == null || !KeyPattern.IsMatch(key))
        {
            return new GlunoIdempotencyCheck(GlunoIdempotencyOutcome.Proceed, null);
        }

        var existing = await _db.GlunoTurnRequests
            .FirstOrDefaultAsync(
                request => request.IdempotencyKey == key
                    && request.UserId == userId
                    && request.ConversationId == conversationId,
                ct);

        if (existing != null)
        {
            if (existing.Status == GlunoTurnRequestStatuses.Completed)
                return new GlunoIdempotencyCheck(GlunoIdempotencyOutcome.AlreadyCompleted, existing);

            if (existing.Status == GlunoTurnRequestStatuses.InFlight
                && DateTime.UtcNow - existing.UpdatedAt < InFlightTimeout)
            {
                return new GlunoIdempotencyCheck(GlunoIdempotencyOutcome.AlreadyInFlight, existing);
            }

            // Abandoned, failed or cancelled — the user may genuinely retry.
            existing.Status = GlunoTurnRequestStatuses.InFlight;
            existing.FailureCode = null;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new GlunoIdempotencyCheck(GlunoIdempotencyOutcome.Proceed, existing);
        }

        var claim = new GlunoTurnRequest
        {
            IdempotencyKey = key,
            UserId = userId,
            ConversationId = conversationId,
            Status = GlunoTurnRequestStatuses.InFlight,
        };

        _db.GlunoTurnRequests.Add(claim);

        try
        {
            await _db.SaveChangesAsync(ct);
            return new GlunoIdempotencyCheck(GlunoIdempotencyOutcome.Proceed, claim);
        }
        catch (DbUpdateException)
        {
            // The unique index caught a genuine race — two requests with the
            // same key arrived together. The database is the arbiter here, not
            // the read above, which is exactly why the index exists.
            _db.ChangeTracker.Clear();

            var winner = await _db.GlunoTurnRequests
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    request => request.IdempotencyKey == key
                        && request.UserId == userId
                        && request.ConversationId == conversationId,
                    ct);

            _logger.LogInformation("[GLUNO] idempotency race resolved for conversation {Conversation}", conversationId);

            return new GlunoIdempotencyCheck(
                winner?.Status == GlunoTurnRequestStatuses.Completed
                    ? GlunoIdempotencyOutcome.AlreadyCompleted
                    : GlunoIdempotencyOutcome.AlreadyInFlight,
                winner);
        }
    }

    public Task CompleteAsync(Guid requestId, Guid assistantMessageId, CancellationToken ct)
        => UpdateAsync(requestId, GlunoTurnRequestStatuses.Completed, assistantMessageId, null, ct);

    public Task FailAsync(Guid requestId, string failureCode, CancellationToken ct)
        => UpdateAsync(requestId, GlunoTurnRequestStatuses.Failed, null, failureCode, ct);

    /// <summary>
    /// Marks a turn cancelled.
    ///
    /// Runs on <see cref="CancellationToken.None"/> on purpose: the request's
    /// own token is precisely what was just cancelled, and using it here would
    /// mean the bookkeeping that records the cancellation is itself cancelled —
    /// leaving the row stuck at in_flight and the user unable to resend.
    /// </summary>
    public Task CancelAsync(Guid requestId, CancellationToken ct)
        => UpdateAsync(requestId, GlunoTurnRequestStatuses.Cancelled, null, null, CancellationToken.None);

    private async Task UpdateAsync(
        Guid requestId, string status, Guid? assistantMessageId, string? failureCode, CancellationToken ct)
    {
        var row = await _db.GlunoTurnRequests.FirstOrDefaultAsync(request => request.Id == requestId, ct);
        if (row == null) return;

        row.Status = status;
        row.UpdatedAt = DateTime.UtcNow;
        if (assistantMessageId.HasValue) row.AssistantMessageId = assistantMessageId.Value;
        if (failureCode != null) row.FailureCode = failureCode;

        await _db.SaveChangesAsync(ct);
    }
}
