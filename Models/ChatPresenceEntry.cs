namespace sidequest.backend.Models;

public class ChatPresenceEntry
{
    public Guid TripId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
