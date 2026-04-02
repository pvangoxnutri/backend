namespace sidequest.backend.Models;

public class TripInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid InvitedByUserId { get; set; }
    public User InvitedByUser { get; set; } = null!;
    public string Email { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
