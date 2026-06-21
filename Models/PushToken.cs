namespace sidequest.backend.Models;

public class PushToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Expo push token, e.g. "ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]".
    public string Token { get; set; } = string.Empty;

    public string Platform { get; set; } = "unknown"; // "ios" | "android" | "unknown"

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    // Flipped to false when the user disables push in-app, or when Expo
    // reports the token as no-longer-registered (uninstalled app, etc.).
    // Deactivated tokens are kept (not deleted) for the delivery history.
    public bool IsActive { get; set; } = true;
}
