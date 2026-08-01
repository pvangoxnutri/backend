using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Models;

/// <summary>
/// The compact working memory of one Gluno conversation.
///
/// WHY A ROW AND NOT THE TRANSCRIPT. The transcript already exists, but reading
/// it back means re-deriving "which restaurant was the second one" from prose,
/// every turn, with a model. That is expensive, non-deterministic, and wrong
/// often enough to matter — "the second one" resolving to the wrong restaurant
/// is the kind of error nobody notices until they are standing outside it.
///
/// So the facts that a follow-up depends on are written down once, structured,
/// and read back exactly.
///
/// ONE ROW PER CONVERSATION. Scoped, private, and deleted with the conversation.
/// It holds ids, short labels and coordinates — never a provider payload, never
/// message text, never the trip context.
/// </summary>
public class GlunoConversationState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    /// <summary>
    /// The shape of <see cref="StateJson"/>.
    ///
    /// Versioned because this format WILL change and old rows must not be
    /// misread as the new shape. A row whose version this build does not
    /// understand is discarded and rebuilt, which costs one turn of continuity
    /// and never produces a wrong reference.
    /// </summary>
    public int Version { get; set; }

    [Required]
    public string StateJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
