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
    public DateOnly EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid OwnerId { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? Destination { get; set; }
    public string InviteCode { get; set; } = string.Empty;

    // Only returned when canViewFull
    public string? Title { get; set; }
    public string? Description { get; set; }
}
