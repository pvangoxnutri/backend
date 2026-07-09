namespace sidequest.backend.Dtos;

public class ChatMessageDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsSystem { get; set; }
    public string? SystemEventType { get; set; }
    public DateTime CreatedAt { get; set; }
    public LinkPreviewDto? LinkPreview { get; set; }
    // True when the message's author has blocked the requesting user.
    // The client should render a blocked placeholder instead of the content.
    public bool IsBlockedByAuthor { get; set; }
    // Per-emoji reaction summary, ordered by first reaction time. Empty for
    // system messages and messages without reactions.
    public List<ChatReactionSummaryDto> Reactions { get; set; } = [];
}

public class ChatReactionSummaryDto
{
    public string Emoji { get; set; } = string.Empty;
    public int Count { get; set; }
    // True when the requesting user is among the reactors — drives the
    // highlighted state of the reaction chip.
    public bool ReactedByMe { get; set; }
}

public class ToggleChatReactionDto
{
    public string Emoji { get; set; } = string.Empty;
}
