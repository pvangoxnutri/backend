namespace sidequest.backend.Models;

/// <summary>
/// The four roles a Gluno turn can have. Deliberately a superset of what the
/// chat UI renders: <see cref="System"/> and <see cref="Tool"/> rows exist so
/// the conversation can be replayed to the model exactly as it happened, but
/// they are NOT chat bubbles — see <see cref="GlunoMessageRoles.IsRenderable"/>.
/// </summary>
public static class GlunoMessageRoles
{
    /// What the person typed.
    public const string User = "user";
    /// What Gluno answered.
    public const string Assistant = "assistant";
    /// Backend-authored context or instruction injected into the turn. Never a
    /// bubble — the user did not say it and Gluno did not say it.
    public const string System = "system";
    /// The result of an action Gluno asked for. Never a bubble either: the
    /// user sees the PROPOSAL card the assistant turn produces, not the raw
    /// tool payload behind it.
    public const string Tool = "tool";

    public static bool IsKnown(string role)
        => role is User or Assistant or System or Tool;

    /// The single rule the mobile app and the API agree on for what becomes a
    /// chat bubble. Keeping it here means a new role added later cannot start
    /// leaking into the UI just because a client forgot to filter it.
    public static bool IsRenderable(string role)
        => role is User or Assistant;
}

/// <summary>
/// One turn in a <see cref="GlunoConversation"/>.
///
/// Rows are append-only: a turn is never rewritten once stored, so the exact
/// sequence sent to the model can always be reconstructed. Tool traffic lives
/// in <see cref="PayloadJson"/> rather than being flattened into
/// <see cref="Text"/>, which is what lets a proposal be re-rendered as a
/// structured card later instead of as prose the client has to parse.
/// </summary>
public class GlunoMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public GlunoConversation Conversation { get; set; } = null!;

    /// One of <see cref="GlunoMessageRoles"/>.
    public string Role { get; set; } = GlunoMessageRoles.User;

    /// Human-readable text. Empty for a pure tool turn.
    public string Text { get; set; } = string.Empty;

    /// Action name for tool turns (e.g. "propose_activity"). Null otherwise.
    public string? ToolName { get; set; }

    /// The provider's id for the tool call this row answers or requests, so a
    /// request and its result can be paired on replay.
    public string? ToolCallId { get; set; }

    /// Structured payload — validated action parameters for a request, or the
    /// validated preview for a result. JSON, never free text.
    public string? PayloadJson { get; set; }

    /// Usage for this turn, when the provider reported it. Nullable because
    /// locally-generated turns (user messages, injected context) have none.
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
