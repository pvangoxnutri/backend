namespace sidequest.backend.Dtos;

public class UpdateActivityDto
{
    public DateOnly? Date { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Time { get; set; }
    // Hotel stay (check-out): null = keep. EndTime follows Time's
    // convention (empty string clears, null keeps). ClearEndDate removes
    // the whole stay (both EndDate and EndTime).
    public DateOnly? EndDate { get; set; }
    public string? EndTime { get; set; }
    public bool ClearEndDate { get; set; }
    public string? Category { get; set; }
    public string? CustomCategoryLabel { get; set; }
    public bool ClearCustomCategoryLabel { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public bool ClearImage { get; set; }
    public bool ClearSpotifyUrl { get; set; }
    public string? Visibility { get; set; }
    public DateTime? RevealAt { get; set; }
    public bool ClearRevealAt { get; set; }
    public string? Teaser { get; set; }
    public bool ClearTeaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool ClearTeaserOffset { get; set; }
    public bool RevealedNow { get; set; }
}
