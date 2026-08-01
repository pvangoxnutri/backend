namespace sidequest.backend.Dtos;

/// <summary>
/// Short-lived read access to one private chat image. ExpiresAt lets the client
/// cache the URL in memory until it lapses instead of re-signing on every
/// render or chat poll.
/// </summary>
public class ChatImageAccessDto
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
