namespace sidequest.backend.Models;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsSystem { get; set; } = false;
    // Lets the client render a localized string for known system events
    // instead of the English text baked into Text at creation time (kept for
    // backward compat with app builds that don't know this field). Null for
    // ordinary user messages and for any system event type predating this.
    public string? SystemEventType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
