namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How long each kind of fact may be presented as current.
///
/// WHY THIS IS NOT THE CACHE TTL. They are different questions and conflating
/// them is a real bug. A cache TTL asks "may I reuse this instead of calling
/// the provider again?" — an operational, cost-driven decision. Freshness asks
/// "may I still tell a person this is true?" — a truthfulness decision.
///
/// The two pull in opposite directions. A rating cached for 24 hours is fine to
/// serve without a re-fetch, and also fine to quote, because ratings barely
/// move. Driving times cached for 20 minutes are fine to reuse and NOT fine to
/// quote an hour later, because traffic changed. Opening hours are the sharpest
/// case: cheap to cache for a day, and dangerous to state as current after a
/// week, because a seasonal change makes a confident answer wrong in a way the
/// user only discovers at the door.
///
/// So: a cached entry may be technically reusable and still have to be labelled
/// as "as of yesterday" rather than "right now".
///
/// Every window here is deliberately conservative. The cost of marking
/// something stale is a slightly weaker sentence; the cost of not marking it is
/// telling somebody a museum is open when it is not.
/// </summary>
public static class GlunoFreshness
{
    /// <summary>
    /// Observed conditions. Almost nothing qualifies — SideQuest has forecasts,
    /// not observations — which is why "it's raining right now" is essentially
    /// never sayable.
    /// </summary>
    public static readonly TimeSpan CurrentWeather = TimeSpan.FromMinutes(30);

    /// A forecast for a future day. Re-checked daily; a three-day-old forecast
    /// for Saturday is not what anyone means by "the forecast".
    public static readonly TimeSpan Forecast = TimeSpan.FromHours(6);

    /// <summary>
    /// Driving times, which depend on traffic.
    ///
    /// The shortest window here by a wide margin. A 20-minute drive computed at
    /// 11:00 is not a 20-minute drive at 17:00, and stating it as one is the
    /// kind of confident wrongness that makes people miss things.
    /// </summary>
    public static readonly TimeSpan DrivingRoute = TimeSpan.FromMinutes(30);

    /// Timetables are stable within a day and not across one.
    public static readonly TimeSpan TransitRoute = TimeSpan.FromHours(3);

    /// <summary>
    /// Walking and cycling. The distance between two fixed points does not
    /// change, so this window is long — a week only guards against the
    /// pedestrian network itself changing.
    /// </summary>
    public static readonly TimeSpan WalkingRoute = TimeSpan.FromDays(7);

    /// Ratings move slowly. A day-old rating is the same rating.
    public static readonly TimeSpan PlaceRating = TimeSpan.FromDays(3);

    /// Review counts move faster than ratings but nobody plans around them.
    public static readonly TimeSpan ReviewCount = TimeSpan.FromDays(3);

    /// Price bands are the most stable provider field there is.
    public static readonly TimeSpan PriceLevel = TimeSpan.FromDays(14);

    /// <summary>
    /// Opening hours. Strictest of the provider fields, and deliberately much
    /// stricter than the address data that arrives in the same response.
    ///
    /// Two days, not two weeks. A place's address does not change; its winter
    /// hours do, and the consequence of being wrong is somebody standing
    /// outside a locked door having planned their afternoon around it.
    /// </summary>
    public static readonly TimeSpan OpeningHours = TimeSpan.FromDays(2);

    /// <summary>
    /// The user's own Adventure.
    ///
    /// Short, because it is not really about time — it is about the plan
    /// changing under us. Invalidation on edit is what actually protects this;
    /// the window is the backstop for a turn that runs while somebody else in
    /// the group is editing.
    /// </summary>
    public static readonly TimeSpan AdventureData = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What SideQuest can do.
    ///
    /// Bounded by the registry VERSION rather than the clock — see
    /// <see cref="MatchesCapabilityVersion"/>. The window exists so a
    /// long-running process cannot serve last release's feature list forever.
    /// </summary>
    public static readonly TimeSpan CapabilityRegistry = TimeSpan.FromHours(12);

    /// <summary>
    /// A live fact with no stated end date.
    ///
    /// Short, because this is the fallback for exactly the facts that are worst
    /// to get wrong — a disruption whose source never published an "it's over"
    /// notice. Past this it stops being quotable as current, which is the right
    /// failure: "I couldn't verify the current status" beats "the ferry is
    /// cancelled" three weeks after service resumed.
    /// </summary>
    public static readonly TimeSpan LiveTravelFact = TimeSpan.FromHours(12);

    public static TimeSpan ForMode(TravelMode mode) => mode switch
    {
        TravelMode.Driving => DrivingRoute,
        TravelMode.Transit => TransitRoute,
        _ => WalkingRoute,
    };

    /// <summary>
    /// When something verified now (or at <paramref name="verifiedAtUtc"/>)
    /// stops being quotable as current.
    /// </summary>
    public static DateTime Until(TimeSpan window, DateTime? verifiedAtUtc = null)
        => (verifiedAtUtc ?? DateTime.UtcNow).Add(window);

    /// <summary>
    /// True when a client's capability version matches this backend's.
    ///
    /// A mismatch means the app in the user's hand does not have the features
    /// this backend knows about — telling them to tap a button that is not in
    /// their build is its own kind of hallucination, and one that looks exactly
    /// like a correct answer from the server's side.
    /// </summary>
    public static bool MatchesCapabilityVersion(int? clientVersion)
        => clientVersion == null || clientVersion == SideQuestCapabilities.Version;

    /// <summary>
    /// How to talk about something past its window.
    ///
    /// Not "do not use it" — often the old value is the best available and
    /// genuinely useful. The rule is that it must be labelled, and must never
    /// be phrased as "right now".
    /// </summary>
    public static string StaleLabel(string language, DateTime verifiedAtUtc, DateTime nowUtc)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);
        var age = nowUtc - verifiedAtUtc;

        if (age < TimeSpan.FromHours(24))
            return swedish ? "kontrollerat tidigare idag" : "checked earlier today";

        var days = Math.Max(1, (int)age.TotalDays);
        return swedish
            ? days == 1 ? "kontrollerat igår" : $"kontrollerat för {days} dagar sedan"
            : days == 1 ? "checked yesterday" : $"checked {days} days ago";
    }
}
