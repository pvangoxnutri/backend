namespace sidequest.backend.Models;

public class SupportAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public SupportMessage Message { get; set; } = null!;

    public string FileUrl { get; set; } = string.Empty;
    public string FileType { get; set; } = "image/jpeg";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
