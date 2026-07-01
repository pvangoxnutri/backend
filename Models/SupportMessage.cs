namespace sidequest.backend.Models;

public class SupportMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public SupportTicket Ticket { get; set; } = null!;

    // "user" | "admin"
    public string SenderType { get; set; } = "user";

    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<SupportAttachment> Attachments { get; set; } = new();
}
