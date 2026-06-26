namespace sidequest.backend.Dtos;

public class NotificationLogDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public Guid? TripId { get; set; }
    public string? Route { get; set; }
    public DateTime CreatedAt { get; set; }
}
