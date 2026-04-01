namespace sidequest.backend.Dtos;

public class ActivityResponseDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Time { get; set; }
    public string? Category { get; set; }
    public bool IsHidden { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime CreatedAt { get; set; }
}
