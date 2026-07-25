namespace sidequest.backend.Models;

/// <summary>
/// Aggregated Travel Tracker statistics a user has chosen to sync from
/// their device. The tracker's raw country statuses are DEVICE-LOCAL by
/// design — this row deliberately stores ONLY the aggregate numbers
/// (never the country list, never per-country statuses), so another
/// user's profile can show "27 countries · 5/7 continents" without any
/// private travel data ever reaching the server. One row per user,
/// upserted by PUT /api/users/me/travel-stats.
/// </summary>
public class UserTravelStats
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public int CountriesVisited { get; set; }

    public int ContinentsReached { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
