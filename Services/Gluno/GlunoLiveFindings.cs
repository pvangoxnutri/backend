using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Turns live travel facts into trip findings.
///
/// WHY THESE ARE SEPARATE FROM TripAnalyzer's OWN FINDINGS. TripAnalyzer
/// measures the plan: it counts stops, compares clock times, measures
/// kilometres. Everything it says is arithmetic over data SideQuest holds, and
/// it is always available.
///
/// These are different in both directions. They come from OUTSIDE, so they can
/// be wrong, stale, or missing entirely — and they must never be able to break
/// the deterministic analysis, which is why they are produced here and merged
/// rather than computed inside it. And they are about the WORLD rather than the
/// plan, so the honest sentence names a source and a date where TripAnalyzer's
/// findings simply state what they measured.
///
/// THE SEVERITY RULE. A current, official closure of a place somebody has
/// planned to visit is a blocker — applying a proposal to a shut museum is a
/// wasted afternoon. Everything reported, undated, or secondary is a warning.
/// Getting this backwards in either direction is bad: block on a rumour and
/// Gluno becomes useless; warn on a confirmed closure and it becomes unsafe.
/// </summary>
public static class GlunoLiveFindings
{
    /// <summary>
    /// Finding types, kept distinct from TripAnalyzer's so a consumer can tell
    /// at a glance whether a finding is measured or reported.
    /// </summary>
    public const string PlaceClosed = "live_place_closed";
    public const string TransportDisrupted = "live_transport_disrupted";
    public const string StrikeOnTravelDay = "live_strike_on_travel_day";
    public const string PublicHolidayOnDay = "live_public_holiday";
    public const string WeatherWarningOnDay = "live_weather_warning";
    public const string AreaDisruption = "live_area_disruption";
    public const string EventNearby = "live_event_nearby";

    public static readonly IReadOnlyList<string> All =
    [
        PlaceClosed, TransportDisrupted, StrikeOnTravelDay,
        PublicHolidayOnDay, WeatherWarningOnDay, AreaDisruption, EventNearby,
    ];

    /// <summary>
    /// Which of these may BLOCK a proposal rather than merely warn about it.
    ///
    /// Only when the fact is also current and official — see
    /// <see cref="Build"/>. The type alone is not enough.
    /// </summary>
    public static bool CanBlock(string type) => type is PlaceClosed or TransportDisrupted or StrikeOnTravelDay;

    /// <summary>
    /// Builds findings by matching live facts against the plan.
    /// </summary>
    public static IReadOnlyList<TripFinding> Build(
        GlunoTripContext trip, IReadOnlyList<LiveTravelFact> facts, string language)
    {
        var findings = new List<TripFinding>();
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        foreach (var fact in facts)
        {
            // Expired and undated facts produce nothing. A finding is a
            // statement about the user's trip, and "something might have been
            // happening at some point" is not one.
            if (fact.Recency is LiveRecency.Expired or LiveRecency.Unclear) continue;

            var affectedDates = DatesCovered(fact, trip).ToList();
            if (affectedDates.Count == 0 && fact.Category != LiveTravelCategories.TravelAdvisory) continue;

            foreach (var date in affectedDates.DefaultIfEmpty(trip.StartDate))
            {
                var onThatDay = trip.Activities.Where(activity => activity.Date == date).ToList();

                var finding = fact.Category switch
                {
                    LiveTravelCategories.Closure when onThatDay.Count > 0 =>
                        Closure(fact, date, onThatDay, swedish),

                    LiveTravelCategories.Strike or LiveTravelCategories.TransportDisruption =>
                        Disruption(fact, date, onThatDay, swedish),

                    LiveTravelCategories.PublicHoliday => Holiday(fact, date, swedish),

                    LiveTravelCategories.WeatherWarning when onThatDay.Count > 0 =>
                        WeatherWarning(fact, date, swedish),

                    LiveTravelCategories.RoadDisruption or LiveTravelCategories.SafetyNotice =>
                        Area(fact, date, swedish),

                    LiveTravelCategories.Event => Event(fact, date, swedish),

                    _ => null,
                };

                if (finding != null) findings.Add(finding);
            }
        }

        // Bounded. A day with eight external findings on it produces an answer
        // that recites a list instead of helping.
        return findings.Take(8).ToList();
    }

    private static TripFinding Closure(
        LiveTravelFact fact, DateOnly date, IReadOnlyList<GlunoActivityContext> activities, bool swedish)
        => new()
        {
            Type = PlaceClosed,
            // A current, first-party closure is worth blocking on; anything
            // reported second-hand is worth saying and no more.
            Severity = Blocking(fact) ? "warning" : "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string>
            {
                [swedish ? "Källa" : "Source"] = fact.SourceName,
                [swedish ? "Gäller" : "Applies"] = Window(fact, swedish),
            },
            Explanation = swedish
                ? $"{fact.Title} — enligt {fact.SourceName}."
                : $"{fact.Title} — according to {fact.SourceName}.",
            SuggestedAction = swedish
                ? "Kontrollera med platsen innan ni går dit."
                : "Check with the place before you go.",
        };

    private static TripFinding Disruption(
        LiveTravelFact fact, DateOnly date, IReadOnlyList<GlunoActivityContext> activities, bool swedish)
    {
        var travelThatDay = activities.Any(activity =>
            ActivityRoles.FromCategory(activity.Category, activity.EndDate) == "transport");

        return new TripFinding
        {
            Type = fact.Category == LiveTravelCategories.Strike ? StrikeOnTravelDay : TransportDisrupted,
            Severity = Blocking(fact) && travelThatDay ? "warning" : "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string>
            {
                [swedish ? "Källa" : "Source"] = fact.SourceName,
                [swedish ? "Gäller" : "Applies"] = Window(fact, swedish),
            },
            Explanation = swedish
                ? $"{fact.Title} — enligt {fact.SourceName}."
                : $"{fact.Title} — according to {fact.SourceName}.",
            // Never "the train is cancelled". SideQuest has no operator feed,
            // and the operator is the only one who can answer that.
            SuggestedAction = swedish
                ? "Kontrollera avgången hos operatören innan ni åker."
                : "Check the departure with the operator before you travel.",
        };
    }

    private static TripFinding Holiday(LiveTravelFact fact, DateOnly date, bool swedish)
        => new()
        {
            Type = PublicHolidayOnDay,
            // Always informational. A holiday is a reason to check opening
            // hours, never evidence that anything in particular is shut.
            Severity = "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string> { [swedish ? "Källa" : "Source"] = fact.SourceName },
            Explanation = swedish
                ? $"{fact.Title} är en helgdag i området."
                : $"{fact.Title} is a public holiday in the area.",
            SuggestedAction = swedish
                ? "Kontrollera öppettiderna för dagens stopp."
                : "Check opening hours for that day's stops.",
        };

    private static TripFinding WeatherWarning(LiveTravelFact fact, DateOnly date, bool swedish)
        => new()
        {
            Type = WeatherWarningOnDay,
            Severity = fact.IsOfficial && fact.Severity is "high" or "medium" ? "warning" : "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string>
            {
                [swedish ? "Källa" : "Source"] = fact.SourceName,
                [swedish ? "Gäller" : "Applies"] = Window(fact, swedish),
            },
            Explanation = swedish
                ? $"Officiell vädervarning: {fact.Title}."
                : $"Official weather warning: {fact.Title}.",
            SuggestedAction = swedish
                ? "Överväg inomhusalternativ den dagen."
                : "Consider indoor options that day.",
        };

    private static TripFinding Area(LiveTravelFact fact, DateOnly date, bool swedish)
        => new()
        {
            Type = AreaDisruption,
            Severity = Blocking(fact) ? "warning" : "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string>
            {
                [swedish ? "Källa" : "Source"] = fact.SourceName,
                [swedish ? "Gäller" : "Applies"] = Window(fact, swedish),
            },
            Explanation = swedish
                ? $"{fact.Title} — enligt {fact.SourceName}."
                : $"{fact.Title} — according to {fact.SourceName}.",
            SuggestedAction = swedish
                ? "Lägg in extra marginal den dagen."
                : "Leave extra margin that day.",
        };

    private static TripFinding Event(LiveTravelFact fact, DateOnly date, bool swedish)
        => new()
        {
            Type = EventNearby,
            // An event is an opportunity, never a problem.
            Severity = "info",
            Date = Iso(date),
            Facts = new Dictionary<string, string>
            {
                [swedish ? "Källa" : "Source"] = fact.SourceName,
                [swedish ? "Datum" : "Dates"] = Window(fact, swedish),
            },
            Explanation = swedish
                ? $"{fact.Title} pågår i området."
                : $"{fact.Title} is on in the area.",
            SuggestedAction = swedish
                ? "Kan passa in i dagen om ni vill."
                : "Could fit into the day if you fancy it.",
        };

    /// <summary>
    /// Whether a fact is solid enough to raise a finding above informational.
    ///
    /// Both halves are required. Current but secondary is a rumour; official
    /// but expired is history.
    /// </summary>
    private static bool Blocking(LiveTravelFact fact)
        => fact.Recency == LiveRecency.Current
        && LiveSourceTiers.CanCarryCriticalClaim(fact.SourceTier);

    /// <summary>
    /// Which of the Adventure's days a fact touches.
    ///
    /// Intersects the fact's own effective range with the trip. A fact with no
    /// start date touches nothing — an undated disruption is not a statement
    /// about any particular day.
    /// </summary>
    private static IEnumerable<DateOnly> DatesCovered(LiveTravelFact fact, GlunoTripContext trip)
    {
        if (fact.EffectiveFrom is not { } from) yield break;

        var until = fact.EffectiveUntil ?? from;
        // EffectiveEndDate is non-nullable on the context — an open-ended
        // Adventure already carries a computed horizon there.
        var tripEnd = trip.EffectiveEndDate;

        var start = from > trip.StartDate ? from : trip.StartDate;
        var end = until < tripEnd ? until : tripEnd;

        // Bounded: a three-month advisory must not produce ninety findings.
        var days = 0;
        for (var date = start; date <= end && days < 14; date = date.AddDays(1), days++)
        {
            yield return date;
        }
    }

    private static string Window(LiveTravelFact fact, bool swedish)
    {
        if (fact.EffectiveFrom is not { } from)
            return swedish ? "datum ej angivet" : "dates not stated";

        if (fact.EffectiveUntil is not { } until)
            return swedish ? $"från {Iso(from)}, slutdatum okänt" : $"from {Iso(from)}, no end date given";

        return from == until ? Iso(from) : $"{Iso(from)} – {Iso(until)}";
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
