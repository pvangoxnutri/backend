namespace sidequest.backend.Dtos;

public class ChatPresenceDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}
