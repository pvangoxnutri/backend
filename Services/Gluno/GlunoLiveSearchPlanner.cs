using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What a live search should ask about, when one is warranted.
/// </summary>
public sealed record GlunoLiveSearchPlan
{
    public required bool ShouldSearch { get; init; }

    /// <see cref="LiveTravelCategories"/> worth asking about, most relevant
    /// first. Empty when nothing should be searched.
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The place to ask about — a town or region, never coordinates and never
    /// the Adventure's title.
    /// </summary>
    public string? Destination { get; init; }

    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }

    /// How many provider searches this turn may make.
    public int MaxSearches { get; init; }

    /// Short machine reason for telemetry: "explicit_disruption_question",
    /// "planning_with_dates", "no_live_need".
    public required string Reason { get; init; }
}

/// <summary>
/// Decides when Gluno needs to look at the world outside SideQuest.
///
/// WHY THIS IS DELIBERATELY CONSERVATIVE. A live search costs money, adds
/// seconds to a chat turn, and — the part that matters most — pulls untrusted
/// web text into a prompt. Doing it on every turn would mean paying all three
/// costs to answer "how do I add a photo?".
///
/// The test is not "could current information possibly be relevant" — it almost
/// always could. It is "does the ANSWER change without it". Someone asking
/// whether the museum is open on Sunday cannot be answered from anything
/// SideQuest holds. Someone reordering two Activities can.
///
/// The model does not get a say. It cannot request more searches, widen the
/// window, or search a place the user did not mention — the plan is built here,
/// before the model runs, from the intent and the dates.
/// </summary>
public static class GlunoLiveSearchPlanner
{
    /// <summary>
    /// Phrases that mean "tell me what is happening there right now".
    ///
    /// Each maps to categories rather than to a generic search, so a question
    /// about strikes does not come back with festivals.
    /// </summary>
    private static readonly (string[] Phrases, string[] Categories)[] Triggers =
    [
        (
            ["strejk", "strejkar", "strike", "strikes", "industrial action"],
            [LiveTravelCategories.Strike, LiveTravelCategories.TransportDisruption]
        ),
        (
            ["stangt", "stanger", "oppet", "oppettider", "closed", "closing", "open on", "opening hours"],
            [LiveTravelCategories.Closure, LiveTravelCategories.PublicHoliday]
        ),
        (
            ["helgdag", "rod dag", "public holiday", "bank holiday", "national holiday"],
            [LiveTravelCategories.PublicHoliday, LiveTravelCategories.Closure]
        ),
        (
            ["farja", "farjan", "ferry", "ferries", "tag", "taget", "train", "trains", "buss", "flyg", "flight"],
            [LiveTravelCategories.TransportDisruption, LiveTravelCategories.Strike]
        ),
        (
            ["vag", "vagen", "vagavstangning", "road", "roads", "roadworks", "closed road", "border", "grans"],
            [LiveTravelCategories.RoadDisruption, LiveTravelCategories.BorderInformation]
        ),
        (
            ["sakert", "sakerhet", "safe", "safety", "dangerous", "advisory", "avradan", "unrest", "demonstration"],
            [LiveTravelCategories.SafetyNotice, LiveTravelCategories.TravelAdvisory]
        ),
        (
            ["evenemang", "event", "events", "festival", "konsert", "concert", "marknad", "market",
             "vad hander", "what's on", "whats on", "going on"],
            [LiveTravelCategories.Event]
        ),
        (
            ["varning", "warning", "oväder", "ovader", "storm", "extremvader", "flood", "heatwave"],
            [LiveTravelCategories.WeatherWarning]
        ),
    ];

    /// <summary>
    /// Intents that may search at all, even with a matching phrase.
    ///
    /// Note what is absent: SideQuestHelp and NavigationRequest. "Where is the
    /// packing list?" contains none of the trigger words, but a question like
    /// "is the documents screen open to everyone?" contains "open" — and app
    /// help must never reach for the web.
    /// </summary>
    private static bool IntentAllowsSearch(GlunoIntent intent) => intent switch
    {
        GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest => false,
        GlunoIntent.PreferenceUpdate or GlunoIntent.ForgetPreference => false,
        GlunoIntent.ConfirmationOrRejection => false,
        // A pure reorder of existing Activities is answered from the plan.
        GlunoIntent.MoveActivity => false,
        _ => true,
    };

    public static GlunoLiveSearchPlan Plan(GlunoLiveSearchRequest request)
    {
        if (!request.ProviderAvailable)
            return NoSearch("provider_unavailable");

        if (!IntentAllowsSearch(request.Intent))
            return NoSearch("intent_does_not_need_live_data");

        var text = GlunoIntentRouter.Normalise(request.Message);
        var categories = new List<string>();

        foreach (var (phrases, matched) in Triggers)
        {
            if (!phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal))) continue;

            foreach (var category in matched)
            {
                if (!categories.Contains(category)) categories.Add(category);
            }
        }

        // An explicit question about current conditions. This is the case the
        // whole layer exists for.
        if (categories.Count > 0)
        {
            return new GlunoLiveSearchPlan
            {
                ShouldSearch = true,
                // Bounded: three categories is already two provider calls, and
                // an answer built from ten is a wall rather than help.
                Categories = categories.Take(3).ToList(),
                Destination = request.Destination,
                From = request.WindowStart,
                To = request.WindowEnd,
                MaxSearches = request.MaxSearchesPerTurn,
                Reason = "explicit_live_question",
            };
        }

        // Planning a specific day is worth ONE broad check — a national holiday
        // or a transport strike changes what a day can hold, and the user has
        // no way to know they should have asked.
        if (request.Intent is GlunoIntent.PlanEmptyDay or GlunoIntent.BuildFullItinerary
            && request.WindowStart != null
            && request.Destination != null)
        {
            return new GlunoLiveSearchPlan
            {
                ShouldSearch = true,
                Categories = [LiveTravelCategories.PublicHoliday, LiveTravelCategories.Strike],
                Destination = request.Destination,
                From = request.WindowStart,
                To = request.WindowEnd,
                MaxSearches = Math.Min(1, request.MaxSearchesPerTurn),
                Reason = "planning_with_dates",
            };
        }

        return NoSearch("no_live_need");
    }

    /// <summary>
    /// The query string sent to the provider.
    ///
    /// MINIMAL BY DESIGN. A place, a date range, a topic. Not the Adventure
    /// title, not who is travelling, not the conversation, not the user's
    /// preferences — a search provider needs to know what to look for and
    /// roughly where, and a trip's private details are not part of that.
    /// </summary>
    public static string BuildQuery(GlunoLiveSearchPlan plan, string category, string language)
    {
        var subject = category switch
        {
            LiveTravelCategories.Strike => language == "sv" ? "strejk kollektivtrafik" : "transport strike",
            LiveTravelCategories.TransportDisruption => language == "sv" ? "trafikstörningar" : "transport disruption",
            LiveTravelCategories.RoadDisruption => language == "sv" ? "vägavstängning" : "road closure",
            LiveTravelCategories.Closure => language == "sv" ? "tillfälligt stängt" : "temporary closure",
            LiveTravelCategories.PublicHoliday => language == "sv" ? "helgdag öppettider" : "public holiday opening hours",
            LiveTravelCategories.Event => language == "sv" ? "evenemang" : "events",
            LiveTravelCategories.WeatherWarning => language == "sv" ? "vädervarning" : "weather warning",
            LiveTravelCategories.TravelAdvisory => language == "sv" ? "reseinformation" : "travel advisory",
            LiveTravelCategories.BorderInformation => language == "sv" ? "gränsregler inresa" : "border entry rules",
            LiveTravelCategories.SafetyNotice => language == "sv" ? "säkerhetsinformation" : "safety notice",
            _ => language == "sv" ? "aktuell information" : "current information",
        };

        var parts = new List<string> { subject };
        if (plan.Destination != null) parts.Add(plan.Destination);

        if (plan.From is { } from)
        {
            parts.Add(plan.To is { } to && to != from
                ? $"{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                : from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return string.Join(' ', parts);
    }

    private static GlunoLiveSearchPlan NoSearch(string reason) => new()
    {
        ShouldSearch = false,
        Reason = reason,
    };
}

public sealed class GlunoLiveSearchRequest
{
    public required string Message { get; init; }
    public required GlunoIntent Intent { get; init; }

    /// The Adventure's destination, or the day's location. Never its title.
    public string? Destination { get; init; }

    public DateOnly? WindowStart { get; init; }
    public DateOnly? WindowEnd { get; init; }

    public bool ProviderAvailable { get; init; }
    public int MaxSearchesPerTurn { get; init; } = 2;
}
