namespace sidequest.backend.Models;

public class UserReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReporterId { get; set; }
    public User Reporter { get; set; } = null!;

    // "user" | "chat_message" | "activity"
    public string ContentType { get; set; } = "user";

    // userId, chatMessageId, or activityId as string
    public string ContentId { get; set; } = string.Empty;

    // "spam" | "harassment" | "offensive" | "explicit" | "other"
    public string Reason { get; set; } = "other";

    public string? Notes { get; set; }

    // "pending" | "reviewed" | "resolved"
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
