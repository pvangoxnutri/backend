namespace sidequest.backend.Models;

public class ChatMessageReaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatMessageId { get; set; }
    public ChatMessage ChatMessage { get; set; } = null!;
    public Guid UserId { get; set; }
    // The emoji character itself ("❤️", "😂", …). The API only accepts the
    // fixed picker set — see TripChatController.AllowedReactionEmojis.
    public string Emoji { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
