namespace sidequest.backend.Dtos;

public class TripMemberDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}
