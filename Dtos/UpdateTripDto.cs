namespace sidequest.backend.Dtos;

public class UpdateTripDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Destination { get; set; }
    public string? ImageUrl { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    // Hidden SideQuest fields
    public string? Visibility { get; set; }
    public DateTime? RevealAt { get; set; }
    public bool ClearRevealAt { get; set; } = false;
    public string? Teaser { get; set; }
}
