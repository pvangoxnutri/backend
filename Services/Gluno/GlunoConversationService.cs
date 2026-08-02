using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One page of a conversation, oldest message first.
///
/// <see cref="HasMore"/> answers "is there anything further back?" — the app
/// uses it to decide whether scrolling up should keep loading, rather than
/// inferring it from a short page (which is ambiguous when a page happens to
/// land exactly on the boundary).
/// </summary>
public sealed record GlunoMessagePage(List<GlunoMessage> Messages, bool HasMore);

public interface IGlunoConversationService
{
    Task<GlunoConversation?> GetOwnedAsync(Guid conversationId, Guid userId, CancellationToken ct);
    Task<GlunoConversation?> GetLatestForScopeAsync(Guid userId, Guid? tripId, CancellationToken ct);
    Task<GlunoConversation> CreateAsync(Guid userId, Guid? tripId, CancellationToken ct);
    Task<List<GlunoConversation>> ListAsync(Guid userId, Guid? tripId, CancellationToken ct);
    Task<GlunoMessagePage> GetMessagePageAsync(Guid conversationId, DateTime? before, int limit, CancellationToken ct);
    Task<List<GlunoTurn>> GetHistoryTurnsAsync(Guid conversationId, int maxTurns, CancellationToken ct);
    Task<GlunoMessage> AppendAsync(GlunoMessage message, CancellationToken ct);

    /// One message, scoped to its owner through the conversation. Used to
    /// replay the question a clarification was asking about.
    Task<GlunoMessage?> GetMessageAsync(Guid messageId, Guid userId, CancellationToken ct);
    Task ArchiveAsync(GlunoConversation conversation, CancellationToken ct);
}

/// <summary>
/// The conversation-state layer: everything that reads or writes Gluno's
/// stored turns, and nothing else. No prompts, no model, no trip logic.
///
/// The one rule it enforces everywhere is ownership. There is no method that
/// fetches a conversation by id alone — <see cref="GetOwnedAsync"/> always
/// takes the user, so a conversation belonging to somebody else is simply not
/// found. That is deliberate: a Gluno conversation is private to the person
/// who had it, including on a shared Adventure.
/// </summary>
public sealed class GlunoConversationService : IGlunoConversationService
{
    private const int MaxTitleLength = 80;

    /// One screenful and then some. Small enough that opening Gluno is cheap
    /// on a phone connection, large enough that most conversations never need
    /// a second page.
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 60;

    private readonly AppDbContext _db;

    public GlunoConversationService(AppDbContext db)
    {
        _db = db;
    }

    // Trip is included on every read path that reaches the API: the chat header
    // shows the Adventure's real name, and deriving it from a route parameter
    // instead would go stale the moment the trip is renamed — or be missing
    // entirely when the conversation is reopened from elsewhere.
    public Task<GlunoConversation?> GetOwnedAsync(Guid conversationId, Guid userId, CancellationToken ct)
        => _db.GlunoConversations
            .Include(c => c.Trip)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

    public async Task<GlunoConversation> CreateAsync(Guid userId, Guid? tripId, CancellationToken ct)
    {
        var conversation = new GlunoConversation
        {
            UserId = userId,
            TripId = tripId,
            SystemPromptVersion = GlunoSystemPrompt.Version,
        };

        _db.GlunoConversations.Add(conversation);
        await _db.SaveChangesAsync(ct);
        return conversation;
    }

    public Task<List<GlunoConversation>> ListAsync(Guid userId, Guid? tripId, CancellationToken ct)
    {
        var query = _db.GlunoConversations
            .AsNoTracking()
            .Include(c => c.Trip)
            .Where(c => c.UserId == userId && c.ArchivedAt == null);

        // A caller asking for one Adventure's conversations gets exactly those;
        // no tripId means "everything", global ones included.
        if (tripId.HasValue)
        {
            query = query.Where(c => c.TripId == tripId.Value);
        }

        return query
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.Id)
            .Take(50)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The conversation to reopen for this scope, or null if there is none.
    ///
    /// Scope matching is exact: a null <paramref name="tripId"/> means the
    /// GLOBAL conversation, not "any". Getting that wrong would hand a user
    /// their Lisbon conversation when they opened Gluno from the home tab, so
    /// the two histories are kept strictly apart.
    ///
    /// This is what stops opening Gluno from creating an empty conversation
    /// every time — the screen reopens the most recent one instead.
    /// </summary>
    public Task<GlunoConversation?> GetLatestForScopeAsync(Guid userId, Guid? tripId, CancellationToken ct)
        => _db.GlunoConversations
            .Include(c => c.Trip)
            .Where(c => c.UserId == userId && c.ArchivedAt == null)
            .Where(c => tripId == null ? c.TripId == null : c.TripId == tripId)
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// One page, walking backwards from <paramref name="before"/> (exclusive)
    /// or from the newest message when it is null.
    ///
    /// The cursor is a timestamp rather than an offset on purpose: new turns
    /// arrive while the user is scrolling back, and an offset would silently
    /// shift the window under them and re-serve or skip rows. Returned oldest
    /// first, so the caller can prepend a page without reordering it.
    /// </summary>
    public async Task<GlunoMessagePage> GetMessagePageAsync(
        Guid conversationId, DateTime? before, int limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit <= 0 ? DefaultPageSize : limit, 1, MaxPageSize);

        var query = _db.GlunoMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

        if (before.HasValue)
        {
            query = query.Where(m => m.CreatedAt < before.Value);
        }

        // One extra row is the cheapest unambiguous answer to "is there more?".
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        rows.Reverse();
        return new GlunoMessagePage(rows, hasMore);
    }

    /// <summary>
    /// The last <paramref name="maxTurns"/> user/assistant turns, oldest first.
    ///
    /// System and tool rows are excluded on purpose. They are kept in the
    /// database as the record of what actually happened, but replaying tool
    /// traffic to the model on later turns would grow every request without
    /// improving the answer — the proposal's outcome is already described in
    /// the assistant text that followed it.
    /// </summary>
    public async Task<List<GlunoTurn>> GetHistoryTurnsAsync(Guid conversationId, int maxTurns, CancellationToken ct)
    {
        var rows = await _db.GlunoMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .Where(m => m.Role == GlunoMessageRoles.User || m.Role == GlunoMessageRoles.Assistant)
            .Where(m => m.Text != string.Empty)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(maxTurns)
            .Select(m => new { m.Role, m.Text })
            .ToListAsync(ct);

        rows.Reverse();
        return rows.Select(r => new GlunoTurn { Role = r.Role, Text = r.Text }).ToList();
    }

    public Task<GlunoMessage?> GetMessageAsync(Guid messageId, Guid userId, CancellationToken ct)
        => _db.GlunoMessages
            .AsNoTracking()
            .Join(_db.GlunoConversations.Where(c => c.UserId == userId),
                m => m.ConversationId, c => c.Id, (m, _) => m)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

    public async Task<GlunoMessage> AppendAsync(GlunoMessage message, CancellationToken ct)
    {
        _db.GlunoMessages.Add(message);

        var conversation = await _db.GlunoConversations
            .FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);

        if (conversation != null)
        {
            conversation.UpdatedAt = DateTime.UtcNow;

            // The first thing the user said becomes the label. Derived once
            // and never rewritten, so a long conversation keeps the title it
            // was found under.
            if (conversation.Title == null && message.Role == GlunoMessageRoles.User && message.Text.Length > 0)
            {
                conversation.Title = message.Text.Length > MaxTitleLength
                    ? message.Text[..MaxTitleLength].TrimEnd() + "…"
                    : message.Text;
            }
        }

        await _db.SaveChangesAsync(ct);
        return message;
    }

    public async Task ArchiveAsync(GlunoConversation conversation, CancellationToken ct)
    {
        conversation.ArchivedAt = DateTime.UtcNow;
        conversation.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
