namespace sidequest.backend.Services;

// Single source of truth for what "online" means, so the members endpoint,
// the profile endpoint and anything added later can never drift apart.
// A user counts as online if any authenticated request from them landed
// within the window. The app sends a foreground heartbeat every ~60s, so
// 2 minutes tolerates one missed beat without flickering the dot off.
public static class PresenceHelper
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    public static DateTime OnlineCutoff => DateTime.UtcNow - OnlineWindow;

    public static bool IsOnline(DateTime? lastSeenAt) => lastSeenAt.HasValue && lastSeenAt.Value > OnlineCutoff;
}
