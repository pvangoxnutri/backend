namespace sidequest.backend.Models;

/// <summary>
/// One Gluno conversation, owned by exactly one user.
///
/// Scope is the whole point of this table. <see cref="TripId"/> null means a
/// GLOBAL conversation — Gluno as the app's travel expert, with no Adventure
/// selected. A non-null TripId scopes the conversation to that Adventure, and
/// every context lookup and every proposed action in it is bound to that trip
/// and re-checked against the user's membership on each turn. A conversation
/// therefore cannot silently drift from one Adventure to another: to talk
/// about a different trip you start a different conversation.
///
/// Ownership is single-user by design. Conversations are never shared with
/// other trip members, even for a trip-scoped conversation — a member's
/// questions to Gluno are their own. Every read path filters on UserId.
/// </summary>
public class GlunoConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// Null = global (no Adventure selected). Otherwise the Adventure this
    /// conversation is locked to.
    public Guid? TripId { get; set; }
    public Trip? Trip { get; set; }

    /// Short label derived from the first user message. Purely for a future
    /// conversation list; never sent to the model.
    public string? Title { get; set; }

    /// Which version of the backend system prompt this conversation was
    /// started on (see GlunoSystemPrompt). Recorded so an old conversation can
    /// be told apart from a new one after the prompt changes, and so a prompt
    /// regression is traceable to the conversations it affected.
    public int SystemPromptVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// Soft-delete. Archived conversations stay readable by their owner but
    /// accept no new turns.
    public DateTime? ArchivedAt { get; set; }
}
