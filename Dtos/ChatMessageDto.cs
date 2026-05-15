namespace sidequest.backend.Dtos;

public class ChatMessageDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    public LinkPreviewDto? LinkPreview { get; set; }
}
