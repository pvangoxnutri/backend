namespace sidequest.backend.Models;

public class ChatPresenceEntry
{
    public Guid TripId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    // Last moment the user actively typed in the chat input. Null = not
    // typing. Read through a short cutoff window (see TripChatController)
    // so a crashed/offline client can never leave a stale indicator.
    public DateTime? TypingAt { get; set; }
}
