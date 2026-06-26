namespace sidequest.backend.Dtos;

public class TripEventDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string? TripTitle { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Guid? ActivityId { get; set; }
    public bool IsHidden { get; set; }
    public string? ActivityTitle { get; set; }
    public DateTime CreatedAt { get; set; }
}
