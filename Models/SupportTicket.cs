namespace sidequest.backend.Models;

public class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // "bug" | "feature" | "account" | "billing" | "report_user" | "other"
    public string Category { get; set; } = "other";

    public string Subject { get; set; } = string.Empty;

    // "open" | "waiting_for_reply" | "closed"
    public string Status { get; set; } = "open";

    public bool HasUnreadAdminReply { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    public List<SupportMessage> Messages { get; set; } = new();
}
