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
    // True when the added activity is a hidden SideQuest — Home's feed must
    // not reveal who added it or its title in that case, only that *a*
    // SideQuest was added. False/irrelevant for non-"activity_added" types.
    public bool IsHidden { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
