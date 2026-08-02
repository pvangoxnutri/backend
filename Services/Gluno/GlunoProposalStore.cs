using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What the relevant slice of the Adventure looked like when a proposal was
/// made.
///
/// Trip and TripActivity have no UpdatedAt column, so this is the "or
/// equivalent" the staleness rule needs: a signature of exactly the rows the
/// proposal depends on. Comparing it again at apply answers the only question
/// that matters — did anything the user reviewed change underneath them?
///
/// Deliberately coarse. It is not a merge base and must never become one:
/// detecting that the plan moved and asking Gluno for a fresh proposal is the
/// whole design, and an automatic merge would silently apply something nobody
/// reviewed.
/// </summary>
public sealed class GlunoProposalSnapshot
{
    /// "start|end" — end is "open" for an open-ended Adventure.
    public string TripDates { get; set; } = string.Empty;
    /// "id:date:sortIndex:time" per relevant activity, in a stable order.
    public List<string> Activities { get; set; } = new();
    /// "id:sortIndex:label" per relevant day location.
    public List<string> DayLocations { get; set; } = new();

    public bool Matches(GlunoProposalSnapshot other)
        => TripDates == other.TripDates
           && Activities.SequenceEqual(other.Activities, StringComparer.Ordinal)
           && DayLocations.SequenceEqual(other.DayLocations, StringComparer.Ordinal);
}

public interface IGlunoProposalStore
{
    /// <summary>
    /// The only way a proposal comes into existence.
    ///
    /// <paramref name="draftId"/> and <paramref name="draftVersion"/> bind it to
    /// the negotiation it came out of, so apply can refuse a proposal whose
    /// draft has moved on since. Both null only for the ordinary path, where no
    /// conflict was ever raised.
    /// </summary>
    Task<GlunoProposalRecord> CreateAsync(
        GlunoConversation conversation, Guid messageId, GlunoProposal proposal, CancellationToken ct,
        Guid? draftId = null, int? draftVersion = null);

    Task<GlunoProposalRecord?> GetOwnedAsync(Guid proposalId, Guid userId, CancellationToken ct);

    Task<List<GlunoProposalRecord>> ListForMessagesAsync(IReadOnlyList<Guid> messageIds, CancellationToken ct);

    /// <summary>
    /// Builds the snapshot for a proposal from the CURRENT database state.
    /// Called once at creation and again at apply; the two are compared.
    /// </summary>
    Task<GlunoProposalSnapshot> BuildSnapshotAsync(
        Guid tripId, string actionType, JsonElement payload, CancellationToken ct);
}

public sealed class GlunoProposalStore : IGlunoProposalStore
{
    private readonly AppDbContext _db;

    public GlunoProposalStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<GlunoProposalRecord> CreateAsync(
        GlunoConversation conversation, Guid messageId, GlunoProposal proposal, CancellationToken ct,
        Guid? draftId = null, int? draftVersion = null)
    {
        // ── What the row holds ────────────────────────────────────────────
        //
        // Usually the proposal exactly as built. Where the provider's terms
        // forbid keeping its content, the caller supplies a second version
        // carrying the place's identity and the user's own decisions instead —
        // chosen at the point the proposal was built, never derived here by
        // stripping fields, because a stripper that misses one stores it.
        var storedPayload = proposal.PersistedPayload ?? proposal.Payload;

        // Built from the STORED payload, so the snapshot describes what will
        // actually be applied. It holds only trip dates and activity
        // signatures — ids, dates and sort order out of the user's own
        // Adventure — so nothing from a provider reaches it either way.
        var snapshot = await BuildSnapshotAsync(proposal.TripId, proposal.Kind, storedPayload, ct);

        var record = new GlunoProposalRecord
        {
            ConversationId = conversation.Id,
            MessageId = messageId,
            // From the conversation row, never from the model or the request.
            UserId = conversation.UserId,
            TripId = proposal.TripId,
            ActionType = proposal.ActionName,
            Summary = proposal.PersistedSummary ?? proposal.Summary,
            PayloadVersion = GlunoProposalPayloadVersions.Current,
            PayloadJson = storedPayload.GetRawText(),
            SnapshotJson = JsonSerializer.Serialize(snapshot, GlunoJson.Options),
            DraftId = draftId,
            DraftVersion = draftVersion,
            Status = GlunoProposalStatuses.Pending,
        };

        _db.GlunoProposals.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    /// Ownership is part of the lookup, not a check afterwards — a proposal
    /// belonging to somebody else is simply not found.
    public Task<GlunoProposalRecord?> GetOwnedAsync(Guid proposalId, Guid userId, CancellationToken ct)
        => _db.GlunoProposals.FirstOrDefaultAsync(p => p.Id == proposalId && p.UserId == userId, ct);

    public Task<List<GlunoProposalRecord>> ListForMessagesAsync(IReadOnlyList<Guid> messageIds, CancellationToken ct)
        => _db.GlunoProposals
            .AsNoTracking()
            .Where(p => messageIds.Contains(p.MessageId))
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

    public async Task<GlunoProposalSnapshot> BuildSnapshotAsync(
        Guid tripId, string actionType, JsonElement payload, CancellationToken ct)
    {
        var snapshot = new GlunoProposalSnapshot();

        var trip = await _db.Trips
            .AsNoTracking()
            .Where(t => t.Id == tripId)
            .Select(t => new { t.StartDate, t.EndDate })
            .FirstOrDefaultAsync(ct);

        if (trip == null) return snapshot;

        snapshot.TripDates = $"{Iso(trip.StartDate)}|{(trip.EndDate.HasValue ? Iso(trip.EndDate.Value) : "open")}";

        switch (actionType)
        {
            case GlunoActions.ProposeActivity:
            case GlunoActions.ProposeDayPlan:
            {
                // The target day's running order. If someone else adds, moves
                // or re-times anything there, the slot this proposal assumed
                // no longer exists.
                if (TryReadDate(payload, "date", out var date))
                    snapshot.Activities = await ActivitySignaturesForDateAsync(tripId, date, ct);
                break;
            }

            case GlunoActions.ProposeDayLocation:
            {
                if (TryReadDate(payload, "date", out var date))
                    snapshot.DayLocations = await DayLocationSignaturesAsync(tripId, date, ct);
                break;
            }

            case GlunoActions.ProposeActivityMove:
            {
                // Both ends matter: where it is now and where it is going.
                var signatures = new List<string>();

                if (TryReadGuid(payload, "activityId", out var activityId))
                {
                    var moved = await _db.TripActivities
                        .AsNoTracking()
                        .Where(a => a.Id == activityId && a.TripId == tripId)
                        .Select(a => Signature(a.Id, a.Date, a.SortIndex, a.Time))
                        .FirstOrDefaultAsync(ct);
                    if (moved != null) signatures.Add(moved);
                }

                if (TryReadDate(payload, "toDate", out var toDate))
                    signatures.AddRange(await ActivitySignaturesForDateAsync(tripId, toDate, ct));

                snapshot.Activities = signatures.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                break;
            }

            case GlunoActions.ProposeTripDateChange:
            {
                // Only the extremes matter: those are what a narrowed range
                // can strand.
                var bounds = await _db.TripActivities
                    .AsNoTracking()
                    .Where(a => a.TripId == tripId)
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Count = g.Count(),
                        Min = g.Min(a => (DateOnly?)a.Date),
                        Max = g.Max(a => (DateOnly?)(a.EndDate ?? a.Date)),
                    })
                    .FirstOrDefaultAsync(ct);

                var count = bounds?.Count ?? 0;
                var min = bounds?.Min is { } minDate ? Iso(minDate) : "-";
                var max = bounds?.Max is { } maxDate ? Iso(maxDate) : "-";
                snapshot.Activities = [$"count:{count}", $"min:{min}", $"max:{max}"];
                break;
            }
        }

        return snapshot;
    }

    private Task<List<string>> ActivitySignaturesForDateAsync(Guid tripId, DateOnly date, CancellationToken ct)
        => _db.TripActivities
            .AsNoTracking()
            .Where(a => a.TripId == tripId && a.Date == date)
            .OrderBy(a => a.SortIndex)
            .ThenBy(a => a.Id)
            .Select(a => Signature(a.Id, a.Date, a.SortIndex, a.Time))
            .ToListAsync(ct);

    private Task<List<string>> DayLocationSignaturesAsync(Guid tripId, DateOnly date, CancellationToken ct)
        => _db.TripDayLocations
            .AsNoTracking()
            .Where(d => d.TripId == tripId && d.StartDate == date)
            .OrderBy(d => d.SortIndex)
            .Select(d => $"{d.Id:N}:{d.SortIndex}:{d.LocationLabel}")
            .ToListAsync(ct);

    // Expression-friendly (runs in SQL translation) — hence interpolation
    // rather than a helper call.
    private static string Signature(Guid id, DateOnly date, int sortIndex, string? time)
        => $"{id:N}:{date:yyyy-MM-dd}:{sortIndex}:{time ?? "-"}";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
}
