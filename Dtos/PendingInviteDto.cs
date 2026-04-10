namespace sidequest.backend.Dtos;

public class PendingInviteDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string? TripTitle { get; set; }
    public string? TripDestination { get; set; }
    public string? TripImageUrl { get; set; }
    public string InvitedByName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
