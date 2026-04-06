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
}
