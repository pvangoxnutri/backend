namespace sidequest.backend.Dtos;

public class ActivityCommentDto
{
    public Guid Id { get; set; }
    public Guid ActivityId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? UserAvatarUrl { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
