namespace sidequest.backend.Dtos;

public class TripEventDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string? TripTitle { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
