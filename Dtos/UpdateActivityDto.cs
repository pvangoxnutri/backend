namespace sidequest.backend.Dtos;

public class UpdateActivityDto
{
    public DateOnly? Date { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Time { get; set; }
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public bool ClearImage { get; set; }
    public string? Visibility { get; set; }
    public DateTime? RevealAt { get; set; }
    public bool ClearRevealAt { get; set; }
    public string? Teaser { get; set; }
    public bool ClearTeaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool ClearTeaserOffset { get; set; }
}
