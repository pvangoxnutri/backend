namespace sidequest.backend.Dtos;

public class TripEventDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string? TripTitle { get; set; }
    // Actor fields are nulled/blanked server-side for hidden SideQuest adds —
    // their anonymity must not rely on the client choosing not to render them.
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    // Resolved live from the Users table at read time — the TripEvent row
    // only snapshots the name, and a brand-new member usually uploads their
    // avatar (in onboarding) *after* the join event was written.
    public string? ActorAvatarUrl { get; set; }
    public string Type { get; set; } = string.Empty;
    public Guid? ActivityId { get; set; }
    public bool IsHidden { get; set; }
    public string? ActivityTitle { get; set; }
    public DateTime CreatedAt { get; set; }
}
