namespace sidequest.backend.Dtos;

public class TripResponseDto
{
    public Guid Id { get; set; }

    // Always returned (visibility metadata)
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public bool IsRevealed { get; set; }
    public List<Guid> OwnerIds { get; set; } = new();
    public string? Teaser { get; set; }
    public DateOnly StartDate { get; set; }
    // Null = open-ended. Clients render this as "Ongoing" rather than a date
    // range; they must never fill it in with a placeholder.
    public DateOnly? EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid OwnerId { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? Destination { get; set; }
    public double? DestinationLatitude { get; set; }
    public double? DestinationLongitude { get; set; }
    public string? DestinationPlaceId { get; set; }
    public List<string> Countries { get; set; } = new();
    public string InviteCode { get; set; } = string.Empty;

    // Only returned when canViewFull
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ShareCode { get; set; }

    // Owner-controlled: whether non-owner members may edit activities.
    public bool MembersCanEdit { get; set; } = true;

    // Slideshow cover — presentation preference (default on).
    public bool SlideshowEnabled { get; set; } = true;
}
