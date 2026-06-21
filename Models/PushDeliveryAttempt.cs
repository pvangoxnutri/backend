namespace sidequest.backend.Models;

// Per-device delivery state for one NotificationLog. Created once per active
// PushToken at claim time. Retried independently of other devices/attempts —
// a dead token on device A must never block delivery (or retries) to device
// B for the same logical notification.
public class PushDeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid NotificationLogId { get; set; }
    public NotificationLog NotificationLog { get; set; } = null!;

    public Guid PushTokenId { get; set; }
    public PushToken PushToken { get; set; } = null!;

    // "pending" | "sending" | "accepted_by_expo" | "delivered" |
    // "retry_pending" | "failed_permanent"
    public string Status { get; set; } = "pending";

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }

    // The "id" Expo's send API returns once it has accepted the message —
    // needed to look up the real delivery receipt later via getReceipts.
    public string? ExpoTicketId { get; set; }

    public string? LastErrorCode { get; set; }
    public DateTime? DeliveredAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
