namespace sidequest.backend.Models;

public class TripEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    // Set for "activity_added" events so Home's activity feed can deep-link
    // to the specific SideQuest instead of just the trip. Null for events
    // that aren't about one specific activity (member_joined/left).
    public Guid? ActivityId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
