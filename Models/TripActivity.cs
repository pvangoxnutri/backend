namespace sidequest.backend.Models;

public class TripActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Time { get; set; }
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool IsHidden { get; set; } = false;
    public DateTime? RevealedAt { get; set; }
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public Guid? AssignedToUserId { get; set; }
    public User? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
