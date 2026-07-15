namespace sidequest.backend.Models;

// One row per (logical notification, recipient) — e.g. "this teaser, for
// this user". This is the idempotency boundary: DedupeKey is unique, so the
// notification is *claimed* exactly once regardless of how many times the
// scheduler considers it or how many retries happen afterward. Actual
// delivery state (per device) lives in PushDeliveryAttempt — a user can have
// several devices, and a temporary failure on one device must not block
// retrying that same device, nor count as "this user never got notified".
public class NotificationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // "teaser" | "trip_invite" | "member_joined" | "new_activity" |
    // "new_hidden_sidequest" | "sidequest_revealed" | "chat" | "expense"
    public string Type { get; set; } = string.Empty;

    // Uniquely identifies "this exact notification, for this exact
    // recipient" — claimed via insert before anything is sent. Examples:
    //   teaser:{activityId}:{userId}
    //   trip_invite:{inviteId}
    //   member_joined:{tripId}:{newMemberId}:{userId}
    //   new_activity:{activityId}:{userId}
    //   sidequest_revealed:{activityId}:{userId}
    //   chat:{chatMessageId}:{userId}
    //   expense:{expenseId}:{userId}
    public string DedupeKey { get; set; } = string.Empty;

    public Guid RecipientUserId { get; set; }
    public Guid? TripId { get; set; }

    // The rendered payload, captured once at claim time so it can be (re)sent
    // by any retry/attempt later without re-deriving it from the activity/
    // invite/message — which may have changed (or been deleted) by then.
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";

    // Set only when the notification is allowed to attribute itself to one
    // specific person (e.g. who joined, who added the activity, who sent the
    // chat message) — null whenever that would defeat a hidden SideQuest's
    // anonymity, or there's no single actor to name (a reveal, an aggregated
    // "N new chat messages"). The in-app notification center shows this
    // person's avatar only when both are present; otherwise it falls back to
    // a generic type icon.
    public string? ActorName { get; set; }
    public string? ActorAvatarUrl { get; set; }
    // Lets the notification center resolve the actor's *current* avatar at
    // read time — the snapshot above is often empty because new members
    // upload their picture (in onboarding) after the notification was
    // claimed. Null for older rows and anonymous notifications.
    public Guid? ActorId { get; set; }

    // Rollup across this notification's PushDeliveryAttempts:
    // "pending" | "delivered" | "failed_permanent"
    // ("pending" covers in-flight/retrying — see PushDeliveryAttempt.Status
    // for the detailed per-device state.)
    public string Status { get; set; } = "pending";

    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<PushDeliveryAttempt> Attempts { get; set; } = new();
}
