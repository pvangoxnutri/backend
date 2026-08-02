using System.Globalization;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// "Something else" — searching for an option that was not on the list.
///
/// THE BOUNDARY THIS ENFORCES. Every search here runs over data the CALLER
/// ALREADY HAS in front of them: their own Adventures, the days of the trip
/// being discussed, that trip's Activities, its destinations, the provider
/// results already shown in this conversation. Nothing else is reachable.
///
/// That is deliberate and it is the whole security story. A free-text box wired
/// to a general query would be a search endpoint with a text field on it — and
/// the one thing a clarification must never become is a way to look up rows the
/// user could not otherwise see.
///
/// NO MODEL AND NO EXTERNAL PROVIDER. Tapping "something else" must not start a
/// paid search or a web lookup. It filters what is already loaded, which is
/// also why it is instant.
/// </summary>
public static class GlunoClarificationSearch
{
    /// Below this a query matches almost everything, and the list it produces
    /// is noise. An exact date is exempt — "2026-08-14" is unambiguous.
    public const int MinQueryLength = 2;

    public static bool IsUsable(string? query)
        => !string.IsNullOrWhiteSpace(query)
            && (query.Trim().Length >= MinQueryLength || LooksLikeDate(query));

    /// <summary>
    /// Filters the caller's own Adventures.
    ///
    /// The list is already scoped by membership before it reaches here — this
    /// only narrows it, and cannot widen it.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> Adventures(
        IReadOnlyList<TripChoice> memberTrips, string query, DateOnly today, string language)
    {
        var needle = Normalise(query);

        var matches = memberTrips
            .Where(trip => Normalise(trip.Title + " " + (trip.DestinationSummary ?? "")).Contains(needle))
            .Take(GlunoClarificationBuilder.MaxOptions)
            .ToList();

        return GlunoClarificationBuilder.TripOptions(matches, today, language);
    }

    /// <summary>
    /// Days of the Adventure being discussed.
    ///
    /// Matches a date, a weekday name, or the place that day is in — the three
    /// ways somebody refers to a day out loud.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> Days(
        TripDestinationSummary destinations,
        DateOnly start,
        DateOnly end,
        string query,
        string language)
    {
        var needle = Normalise(query);
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);
        var culture = CultureInfo.GetCultureInfo(swedish ? "sv-SE" : "en-GB");

        var matches = new List<DateOnly>();

        for (var date = start; date <= end && matches.Count < GlunoClarificationBuilder.MaxOptions; date = date.AddDays(1))
        {
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var weekday = Normalise(date.ToString("dddd", culture));
            var readable = Normalise(date.ToString("d MMMM", culture));

            var place = Normalise(destinations.Stops
                .FirstOrDefault(stop =>
                    string.CompareOrdinal(stop.From, iso) <= 0 && string.CompareOrdinal(stop.To, iso) >= 0)
                ?.Label ?? "");

            if (iso.Contains(needle) || weekday.Contains(needle)
                || readable.Contains(needle) || (place.Length > 0 && place.Contains(needle)))
            {
                matches.Add(date);
            }
        }

        return GlunoClarificationBuilder.DayOptions(destinations, matches, language);
    }

    /// Activities of the Adventure being discussed, by title or location.
    public static IReadOnlyList<GlunoOptionDraft> Activities(
        IReadOnlyList<GlunoActivityContext> activities, string query, string language)
    {
        var needle = Normalise(query);

        var matches = activities
            .Where(activity =>
                Normalise(activity.Title).Contains(needle)
                || Normalise(activity.LocationLabel ?? "").Contains(needle))
            .Take(GlunoClarificationBuilder.MaxOptions)
            .ToList();

        return GlunoClarificationBuilder.ActivityOptions(matches, language);
    }

    /// Stops on the trip. Never a general place lookup — a destination the
    /// Adventure does not contain is not an answer to "which stop".
    public static IReadOnlyList<GlunoOptionDraft> Destinations(
        TripDestinationSummary destinations, string query)
    {
        var needle = Normalise(query);

        return destinations.Stops
            .Where(stop => Normalise(stop.Label).Contains(needle))
            .Select(stop => stop.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(GlunoClarificationBuilder.MaxOptions)
            .Select((label, index) => new GlunoOptionDraft($"found-stop-{index}", label)
            {
                EntityType = GlunoClarificationEntityTypes.Enum,
                Value = label,
                Icon = "location-outline",
            })
            .ToList();
    }

    /// <summary>
    /// The provider results already shown in THIS conversation.
    ///
    /// Deliberately not a fresh provider call. Tapping "something else" must
    /// not spend money or seconds, and a new search could return a different
    /// set — which would make "the second one" mean something the user never
    /// saw.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> DiscussedPlaces(
        IReadOnlyList<GlunoDiscussedPlaceContext> places, string query)
    {
        var needle = Normalise(query);

        var matches = places
            .Where(place =>
                Normalise(place.Name).Contains(needle)
                || Normalise(place.Category ?? "").Contains(needle)
                || Normalise(place.Address ?? "").Contains(needle))
            .Take(GlunoClarificationBuilder.MaxOptions)
            .ToList();

        return GlunoClarificationBuilder.PlaceOptions(matches);
    }

    private static bool LooksLikeDate(string query)
        => DateOnly.TryParse(query.Trim(), CultureInfo.InvariantCulture, out _);

    /// Lowercased and accent-folded, so "Málaga" matches a typed "malaga".
    private static string Normalise(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        var builder = new System.Text.StringBuilder(lower.Length);

        foreach (var character in lower.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
