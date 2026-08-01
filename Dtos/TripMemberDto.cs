namespace sidequest.backend.Dtos;

// Exactly what an avatar row needs: who they are, their picture, whether they
// own the adventure, whether they are online — plus the one field that makes
// the list's ORDER reproducible on the client.
public class TripMemberDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public bool IsOwner { get; set; }
    // When this membership was created. Sent so the client can sort by the
    // same rule the server does (owner → joined → id) rather than depending on
    // response order, which no cached copy can preserve on its own.
    public DateTime JoinedAt { get; set; }
    public bool IsOnline { get; set; }
}
