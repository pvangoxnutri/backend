namespace sidequest.backend.Models;

public class TripActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Time { get; set; }
    public string? Category { get; set; }
    public bool IsHidden { get; set; } = false;
    public Guid? AssignedToUserId { get; set; }
    public User? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
