namespace sidequest.backend.Dtos;

public class ActivityResponseDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public DateOnly Date { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Time { get; set; }
    // Check-out for hotel stays; withheld for sealed viewers (duration
    // leaks the surprise). Null for single-day activities.
    public DateOnly? EndDate { get; set; }
    public string? EndTime { get; set; }
    public int SortIndex { get; set; }
    public string? Category { get; set; }
    public string? CustomCategoryLabel { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public DateTime? RevealedAt { get; set; }
    public bool IsRevealed { get; set; }
    public string? Teaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool IsHiddenForViewer { get; set; }
    public bool TeaserVisible { get; set; }
    public bool CanEdit { get; set; }
    public bool IsHidden { get; set; }
    public Guid OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerAvatarUrl { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CommentCount { get; set; }
}
