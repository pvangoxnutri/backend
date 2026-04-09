namespace sidequest.backend.Models;

public class Settlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid FromUserId { get; set; }
    public User FromUser { get; set; } = null!;
    public Guid ToUserId { get; set; }
    public User ToUser { get; set; } = null!;
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
