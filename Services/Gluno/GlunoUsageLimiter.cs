using System.Collections.Concurrent;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A per-user ceiling on external provider calls.
///
/// This is the FOUNDATION for usage limiting, not a paywall and not a plan
/// tier. External travel data is the one part of Gluno with a per-call cost
/// outside SideQuest's control, so there needs to be somewhere a single
/// account cannot run it up unbounded — whether through enthusiasm, a stuck
/// client, or a script.
///
/// The default is deliberately generous: a real conversation makes a handful
/// of searches, so a normal user should never meet this. It exists to stop
/// runaway usage, not to ration anybody.
///
/// In-memory and per-instance, matching the rest of the app's single-instance
/// assumptions (presence throttle, weather cache). A restart resets the
/// window, which is the right trade for a backstop of this kind — the point is
/// to bound a runaway, not to bill anyone accurately.
/// </summary>
public sealed class GlunoUsageLimiter
{
    private sealed class Window
    {
        public DateTime StartedAt;
        public int Count;
    }

    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(1);

    private readonly IConfiguration _config;
    private readonly ConcurrentDictionary<Guid, Window> _windows = new();

    public GlunoUsageLimiter(IConfiguration config)
    {
        _config = config;
    }

    private int MaxExternalSearchesPerHour
        => Math.Max(1, _config.GetValue("Gluno:MaxExternalSearchesPerHour", 60));

    /// <summary>
    /// Claims one external search for this user, or returns false when their
    /// hourly window is used up.
    ///
    /// Claim-on-attempt rather than count-on-success: a provider that is
    /// failing must not become a free unlimited retry loop.
    /// </summary>
    public bool TryClaimExternalSearch(Guid userId)
    {
        var now = DateTime.UtcNow;
        var limit = MaxExternalSearchesPerHour;

        var window = _windows.GetOrAdd(userId, _ => new Window { StartedAt = now });

        lock (window)
        {
            if (now - window.StartedAt >= WindowLength)
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            if (window.Count >= limit) return false;

            window.Count++;
            return true;
        }
    }
}
