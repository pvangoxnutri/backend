using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public interface IGlunoPreferenceService
{
    /// <summary>
    /// Everything that applies right now: this conversation's preferences,
    /// this Adventure's, and the user's global ones.
    /// </summary>
    Task<List<GlunoPreference>> GetForContextAsync(
        Guid userId, Guid conversationId, Guid? tripId, CancellationToken ct);

    /// Upserts one preference. Re-stating a preference updates it rather than
    /// stacking a second row, so "actually, make it packed" wins.
    Task<GlunoPreference> RememberAsync(
        Guid userId, Guid conversationId, Guid? tripId, string key, string value, string scope, CancellationToken ct);

    /// <summary>Removes a preference. Returns how many rows went.</summary>
    Task<int> ForgetAsync(Guid userId, Guid conversationId, Guid? tripId, string key, CancellationToken ct);
}

/// <summary>
/// Gluno's memory for how someone wants to travel.
///
/// Three rules it enforces:
///
///  • **Only planning preferences.** The key must be on
///    <see cref="GlunoPreferenceKeys"/>'s allow-list. There is no path that
///    stores an arbitrary key, so the store cannot drift into holding whatever
///    the model decided was interesting.
///
///  • **Scope is respected on read AND write.** A trip-scoped preference is
///    invisible to another Adventure, and a conversation-scoped one dies with
///    the conversation. Only an explicitly global preference follows the user
///    around.
///
///  • **Forget means forget.** <see cref="ForgetAsync"/> deletes rows rather
///    than marking them; "forget that I said that" leaving a tombstone behind
///    would not be forgetting.
/// </summary>
public sealed class GlunoPreferenceService : IGlunoPreferenceService
{
    /// Long enough for a sentence of nuance, short enough that this cannot
    /// become a place to stash free text.
    public const int MaxValueLength = 240;

    private readonly AppDbContext _db;

    public GlunoPreferenceService(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<GlunoPreference>> GetForContextAsync(
        Guid userId, Guid conversationId, Guid? tripId, CancellationToken ct)
        => _db.GlunoPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Where(p =>
                p.Scope == GlunoPreferenceScopes.Global
                || (p.Scope == GlunoPreferenceScopes.Conversation && p.ConversationId == conversationId)
                || (p.Scope == GlunoPreferenceScopes.Trip && tripId != null && p.TripId == tripId))
            .OrderBy(p => p.Key)
            .ThenBy(p => p.Scope)
            .ToListAsync(ct);

    public async Task<GlunoPreference> RememberAsync(
        Guid userId, Guid conversationId, Guid? tripId, string key, string value, string scope, CancellationToken ct)
    {
        // A trip-scoped preference without a trip has nowhere to live; fall
        // back to the conversation rather than silently widening it to global.
        if (scope == GlunoPreferenceScopes.Trip && tripId == null) scope = GlunoPreferenceScopes.Conversation;

        var scopedConversationId = scope == GlunoPreferenceScopes.Conversation ? conversationId : (Guid?)null;
        var scopedTripId = scope == GlunoPreferenceScopes.Trip ? tripId : null;

        var existing = await _db.GlunoPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId
                 && p.Key == key
                 && p.Scope == scope
                 && p.ConversationId == scopedConversationId
                 && p.TripId == scopedTripId,
            ct);

        var trimmed = value.Trim();
        if (trimmed.Length > MaxValueLength) trimmed = trimmed[..MaxValueLength];

        if (existing != null)
        {
            existing.Value = trimmed;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var preference = new GlunoPreference
        {
            UserId = userId,
            ConversationId = scopedConversationId,
            TripId = scopedTripId,
            Key = key,
            Value = trimmed,
            Scope = scope,
        };

        _db.GlunoPreferences.Add(preference);
        await _db.SaveChangesAsync(ct);
        return preference;
    }

    /// <summary>
    /// Removes this preference everywhere it currently applies.
    ///
    /// Deliberately not scope-specific: a user saying "forget that" means the
    /// preference, not one particular row of it, and leaving a global copy
    /// behind after deleting the conversation one would look exactly like
    /// ignoring them.
    /// </summary>
    public async Task<int> ForgetAsync(
        Guid userId, Guid conversationId, Guid? tripId, string key, CancellationToken ct)
        => await _db.GlunoPreferences
            .Where(p => p.UserId == userId && p.Key == key)
            .Where(p =>
                p.Scope == GlunoPreferenceScopes.Global
                || (p.Scope == GlunoPreferenceScopes.Conversation && p.ConversationId == conversationId)
                || (p.Scope == GlunoPreferenceScopes.Trip && tripId != null && p.TripId == tripId))
            .ExecuteDeleteAsync(ct);
}
