namespace sidequest.backend.Dtos;

public class ChatPresenceDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public bool IsTyping { get; set; }
}

// Optional PUT body — an empty body (the plain 15s heartbeat) leaves the
// typing state untouched, so old clients keep working unchanged.
public class UpdateChatPresenceDto
{
    public bool? IsTyping { get; set; }
}
