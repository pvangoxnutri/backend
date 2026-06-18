namespace sidequest.backend.Models;

public class Trip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Destination { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string InviteCode { get; set; } = string.Empty;

    // Hidden SideQuest fields
    public string Visibility { get; set; } = "public"; // "public" | "hidden"
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public string Status { get; set; } = "active"; // "active" | "completed"
    public string? ShareCode { get; set; }
    public DateTime? SharedAt { get; set; }

    // When true (default), any trip member can add/edit/delete activities
    // (collaborative planning). When false, only the owner can edit; members
    // have read-only access. Owner-controlled via trip settings.
    public bool MembersCanEdit { get; set; } = true;
}
