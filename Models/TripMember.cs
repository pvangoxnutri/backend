namespace sidequest.backend.Models;

public class TripMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public bool IsOwner { get; set; } = false;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}
