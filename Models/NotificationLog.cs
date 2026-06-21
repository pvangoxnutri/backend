namespace sidequest.backend.Models;

public class NotificationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // "teaser" | "reveal" | "trip_invite" | "chat_message"
    public string Type { get; set; } = string.Empty;

    // Uniquely identifies "this exact notification, for this exact
    // recipient" so the scheduler (which polls every minute) and any retry
    // can never send the same notification twice. Examples:
    //   teaser:{activityId}:{userId}
    //   reveal:{activityId}:{userId}
    //   trip_invite:{inviteId}
    //   chat_message:{chatMessageId}:{userId}
    public string DedupeKey { get; set; } = string.Empty;

    public Guid RecipientUserId { get; set; }
    public Guid? TripId { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
