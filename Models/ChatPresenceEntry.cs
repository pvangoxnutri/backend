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

    // Set when the user explicitly reports the chat is no longer on screen
    // (closed it, or the app went to the background); cleared again by the
    // next heartbeat. Push suppression requires LeftAt == null, so an
    // explicit leave makes the user pushable immediately WITHOUT touching
    // LastSeenAt — which doubles as the "last read" anchor for the push
    // unread count and must never be written backwards. Null on rows from
    // older clients ⇒ exactly the old heartbeat-window behavior.
    public DateTime? LeftAt { get; set; }
}
