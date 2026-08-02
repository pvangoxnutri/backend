using System.Globalization;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A candidate option before it becomes a row. Pure data, no database.
/// </summary>
public sealed record GlunoOptionDraft(string Key, string Label)
{
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string EntityType { get; init; } = GlunoClarificationEntityTypes.Enum;
    public Guid? EntityId { get; init; }
    public string Value { get; init; } = string.Empty;
    public bool Disabled { get; init; }
    public string? DisabledReason { get; init; }
}

/// <summary>
/// An Adventure as the ranker sees it. Deliberately not the entity — ranking
/// must not depend on anything that is not also shown to the user.
/// </summary>
public sealed record TripChoice(Guid Id, string Title, DateOnly StartDate, DateOnly? EndDate)
{
    /// "Málaga · Ronda · Sevilla", already built from the destination summary.
    public string? DestinationSummary { get; init; }
    public DateTime? LastUsedAt { get; init; }
}

/// <summary>
/// Turns a need-to-choose into real, verifiable options.
///
/// THE RULE THIS FILE EXISTS TO ENFORCE. The model may say "I need to know
/// which Adventure". It may not say WHICH Adventures exist. Every option below
/// is built from data the backend already fetched under the user's own
/// membership, with ids the backend produced — so a suggested option cannot
/// point at a trip the user cannot see, and a tap cannot become an
/// authorisation bypass.
///
/// Everything here is deterministic and pure: same inputs, same options, same
/// order. That matters more than it sounds. A list that reorders itself
/// between the question and the answer means the user taps the second row and
/// gets the third thing.
/// </summary>
public static class GlunoClarificationBuilder
{
    /// Enough to choose from without becoming a menu. Past this, the list is
    /// trimmed and a free-text escape is offered instead.
    public const int MaxOptions = 5;

    // ── Adventure ────────────────────────────────────────────────────────

    /// <summary>
    /// Ranks the user's Adventures for a question that needs one.
    ///
    /// The order is: what the question mentions, then what is happening now,
    /// then what is coming, then what they last used. Deliberately NOT
    /// "newest first" — somebody asking about Spain while a Spain trip is
    /// running should see it at the top even if they created a different trip
    /// yesterday.
    /// </summary>
    public static IReadOnlyList<TripChoice> RankTrips(
        IReadOnlyList<TripChoice> trips, string message, DateOnly today)
    {
        var text = message.ToLowerInvariant();

        return trips
            .OrderByDescending(trip => MentionsTrip(text, trip))
            .ThenByDescending(trip => IsActive(trip, today))
            .ThenByDescending(trip => IsUpcoming(trip, today))
            // Nearest first among upcoming, most recent first among past.
            .ThenBy(trip => IsUpcoming(trip, today) ? DaysUntil(trip, today) : int.MaxValue)
            .ThenByDescending(trip => trip.LastUsedAt ?? DateTime.MinValue)
            .ThenByDescending(trip => trip.StartDate)
            .ThenBy(trip => trip.Id)
            .ToList();
    }

    /// <summary>
    /// True when exactly one Adventure is an obvious match.
    ///
    /// This is the branch that stops the feature becoming annoying. Asking
    /// "which Adventure?" when the user has one trip, or when they named it,
    /// is a question whose answer we already have — and every one of those
    /// costs a tap and a turn.
    /// </summary>
    public static TripChoice? ResolveSingle(
        IReadOnlyList<TripChoice> trips, string message, DateOnly today)
    {
        if (trips.Count == 0) return null;
        if (trips.Count == 1) return trips[0];

        var text = message.ToLowerInvariant();

        // Named outright: one trip and only one matches the words used.
        var named = trips.Where(trip => MentionsTrip(text, trip)).ToList();
        if (named.Count == 1) return named[0];

        // Exactly one trip is happening right now, and nothing else was
        // mentioned. "What have we got on Friday" during a trip means this one.
        var active = trips.Where(trip => IsActive(trip, today)).ToList();
        if (active.Count == 1 && named.Count == 0) return active[0];

        return null;
    }

    /// <summary>
    /// Adventure options, ranked, capped, and never pre-selected.
    ///
    /// Pre-selecting the likeliest would be a guess wearing a tap's clothing:
    /// the user would confirm without reading, and a wrong first place becomes
    /// a wrong plan.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> TripOptions(
        IReadOnlyList<TripChoice> ranked, DateOnly today, string language)
    {
        var swedish = IsSwedish(language);

        return ranked.Take(MaxOptions).Select((trip, index) => new GlunoOptionDraft(
            $"trip-{index}", trip.Title)
        {
            Description = TripDescription(trip, today, swedish),
            EntityType = GlunoClarificationEntityTypes.Trip,
            EntityId = trip.Id,
            Value = trip.Id.ToString(),
            Icon = "map-outline",
        }).ToList();
    }

    /// <summary>
    /// The option meaning "carry on without an Adventure".
    ///
    /// A fixed key rather than a trip id, so the continuation can tell it apart
    /// from a real choice and refuse to load any trip context for it.
    /// </summary>
    public const string NoAdventureKey = "no-adventure";

    /// <summary>
    /// Appends the honest way out of an Adventure chooser.
    ///
    /// Somebody asking a general travel question does not have an Adventure in
    /// mind, and a chooser with no way past it makes them pick one at random to
    /// get on with the conversation.
    ///
    /// ONLY ON THE QUESTION, never on a search result. A search that found one
    /// Adventure should show that one — adding an escape hatch to a list the
    /// user just narrowed themselves reads as the search having failed.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> WithNoAdventureOption(
        IReadOnlyList<GlunoOptionDraft> options, string language)
        => options
            .Append(new GlunoOptionDraft(
                NoAdventureKey, IsSwedish(language) ? "Vet inte än" : "Not sure yet")
            {
                EntityType = GlunoClarificationEntityTypes.Enum,
                Value = NoAdventureKey,
                Icon = "help-circle-outline",
            })
            .ToList();

    private static string TripDescription(TripChoice trip, DateOnly today, bool swedish)
    {
        var dates = trip.EndDate is { } end
            ? $"{Short(trip.StartDate, swedish)}–{Short(end, swedish)}"
            : swedish ? "Pågående" : "Ongoing";

        var status = IsActive(trip, today)
            ? swedish ? "Pågår" : "Now"
            : IsUpcoming(trip, today)
                ? swedish ? "Kommande" : "Upcoming"
                : swedish ? "Avslutad" : "Past";

        // Places last: they are the most useful line and the most likely to be
        // truncated on a narrow phone, so they get what room is left.
        return string.IsNullOrWhiteSpace(trip.DestinationSummary)
            ? $"{status} · {dates}"
            : $"{status} · {dates} · {trip.DestinationSummary}";
    }

    // ── Days ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The trip days matching a weekday the user named.
    ///
    /// Returns every candidate. One means the caller resolves it silently;
    /// more than one is a real question, because "Friday" on a two-week trip
    /// genuinely is ambiguous.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> DayOptions(
        TripDestinationSummary destinations, IEnumerable<DateOnly> candidates, string language)
    {
        var swedish = IsSwedish(language);

        return candidates.OrderBy(date => date).Take(MaxOptions).Select((date, index) =>
        {
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            // The place makes the choice readable: "Friday 7 August" is two
            // identical rows on a roadtrip; "Friday 7 August — Málaga" is not.
            var place = destinations.Stops
                .FirstOrDefault(stop =>
                    string.CompareOrdinal(stop.From, iso) <= 0 && string.CompareOrdinal(stop.To, iso) >= 0)
                ?.Label;

            return new GlunoOptionDraft($"day-{index}", LongDate(date, swedish))
            {
                Description = place,
                EntityType = GlunoClarificationEntityTypes.Date,
                Value = iso,
                Icon = "calendar-outline",
            };
        }).ToList();
    }

    // ── The route ────────────────────────────────────────────────────────

    /// <summary>
    /// Which part of the trip — "Málaga · 5–7 Aug".
    ///
    /// Built from the resolved route and nothing else. The model may decide a
    /// question is too broad; it cannot decide what the trip's stops ARE, and
    /// a free-text city button would be a city nothing verified.
    ///
    /// Main stops only. An extra stop is an afternoon somewhere, not a part of
    /// the trip somebody plans separately.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> RouteStopOptions(
        TripRouteContext route, string language)
    {
        var swedish = IsSwedish(language);

        return route.Stops
            .Where(stop => stop.IsMainStop)
            .Take(MaxOptions)
            .Select((stop, index) => new GlunoOptionDraft($"stop-{index}", stop.Label)
            {
                Description = DateSpan(stop.From, stop.To, swedish),
                // A date, because that is what the continuation acts on: the
                // stop is identified by when the trip is there. No stop id
                // exists to point at — the chain is resolved per turn.
                EntityType = GlunoClarificationEntityTypes.Date,
                Value = stop.From,
                Icon = "location-outline",
            })
            .ToList();
    }

    /// <summary>
    /// Which journey — "Málaga → Ronda".
    ///
    /// The arrow is the whole point: a leg is not a place, and labelling it
    /// with one end would make two legs from the same city indistinguishable.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> RouteLegOptions(
        TripRouteContext route, string language)
    {
        var swedish = IsSwedish(language);

        return route.Legs
            .Take(MaxOptions)
            .Select((leg, index) => new GlunoOptionDraft(
                $"leg-{index}", $"{leg.FromLabel} → {leg.ToLabel}")
            {
                // The date, and the transport they have already planned when
                // there is any. "8 aug · Ferry Tarifa–Tanger" tells somebody
                // which stretch this is far faster than the date alone on a
                // trip where two legs fall on consecutive days.
                Description = LegDescription(leg, swedish),
                EntityType = GlunoClarificationEntityTypes.Date,
                // The departure day. What the continuation needs to know which
                // leg was meant, and it is a date the backend produced.
                Value = leg.DepartureDate,
                Icon = "navigate-outline",
            })
            .ToList();
    }

    /// <summary>
    /// A leg's second line: when, and how, when the plan says how.
    ///
    /// Their own transport titles, never a mode Gluno inferred. A leg with no
    /// planned travel shows the date alone rather than a guess at driving.
    /// </summary>
    private static string LegDescription(TripRouteLeg leg, bool swedish)
    {
        var when = leg.DepartureDate == leg.ArrivalDate
            ? LongDate(DateOnly.Parse(leg.DepartureDate, CultureInfo.InvariantCulture), swedish)
            : DateSpan(leg.DepartureDate, leg.ArrivalDate, swedish);

        var transport = leg.TransportOnDay.FirstOrDefault();

        return string.IsNullOrWhiteSpace(transport) ? when : $"{when} · {transport}";
    }

    /// "5–7 aug" — one month name when both ends share it, two when they do not.
    private static string DateSpan(string fromIso, string toIso, bool swedish)
    {
        if (!DateOnly.TryParse(fromIso, CultureInfo.InvariantCulture, out var from)
            || !DateOnly.TryParse(toIso, CultureInfo.InvariantCulture, out var to))
        {
            return string.Empty;
        }

        if (from == to) return LongDate(from, swedish);

        var culture = CultureInfo.GetCultureInfo(swedish ? "sv-SE" : "en-GB");

        return from.Month == to.Month
            ? $"{from.Day}–{to.ToString("d MMM", culture)}"
            : $"{from.ToString("d MMM", culture)} – {to.ToString("d MMM", culture)}";
    }

    // ── Activities ───────────────────────────────────────────────────────

    /// <summary>
    /// Activities that all plausibly match what the user said.
    ///
    /// Gluno must never pick between two Activities with the same name. The
    /// cost of guessing is moving the wrong thing on somebody's holiday, and
    /// the cost of asking is one tap.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> ActivityOptions(
        IEnumerable<GlunoActivityContext> activities, string language)
    {
        var swedish = IsSwedish(language);

        return activities
            .OrderBy(activity => activity.Date)
            .ThenBy(activity => activity.SortIndex)
            .Take(MaxOptions)
            .Select((activity, index) => new GlunoOptionDraft($"activity-{index}", activity.Title)
            {
                Description = string.Join(" · ", new[]
                {
                    LongDate(activity.Date, swedish),
                    activity.Time,
                    activity.LocationLabel,
                }.Where(part => !string.IsNullOrWhiteSpace(part))),
                EntityType = GlunoClarificationEntityTypes.Activity,
                EntityId = activity.Id,
                Value = activity.Id.ToString(),
            })
            .ToList();
    }

    // ── Places already shown ─────────────────────────────────────────────

    /// <summary>
    /// The recommendations from this conversation, so "the second one"
    /// resolves to a stable external id rather than to a fresh search that
    /// might come back in a different order.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> PlaceOptions(
        IEnumerable<GlunoDiscussedPlaceContext> places)
        => places.Take(MaxOptions).Select((place, index) => new GlunoOptionDraft(
            $"place-{index}", place.Name)
        {
            Description = place.Address,
            EntityType = GlunoClarificationEntityTypes.ExternalPlace,
            // Namespaced: an id is only meaningful with the provider that
            // issued it, and two providers can use the same number.
            Value = $"{place.Provider}:{place.ExternalId}",
        }).ToList();

    // ── Fixed vocabularies ───────────────────────────────────────────────

    public static IReadOnlyList<GlunoOptionDraft> TransportOptions(string language)
    {
        var swedish = IsSwedish(language);

        return
        [
            Enum("walking", swedish ? "Till fots" : "On foot", "walk-outline"),
            Enum("public_transport", swedish ? "Kollektivtrafik" : "Public transport", "bus-outline"),
            Enum("car", swedish ? "Bil" : "Car", "car-outline"),
            Enum("taxi", swedish ? "Taxi" : "Taxi", "car-sport-outline"),
            Enum("bike", swedish ? "Cykel" : "Bike", "bicycle-outline"),
        ];
    }

    public static IReadOnlyList<GlunoOptionDraft> PaceOptions(string language)
    {
        var swedish = IsSwedish(language);

        return
        [
            Enum("relaxed", swedish ? "Lugnt" : "Relaxed"),
            Enum("balanced", swedish ? "Balanserat" : "Balanced"),
            Enum("packed", swedish ? "Fullspäckat" : "Packed"),
        ];
    }

    public static IReadOnlyList<GlunoOptionDraft> BudgetOptions(string language)
    {
        var swedish = IsSwedish(language);

        return
        [
            Enum("budget", swedish ? "Prisvärt" : "Good value"),
            Enum("moderate", swedish ? "Mellan" : "Mid-range"),
            Enum("premium", swedish ? "Exklusivt" : "Premium"),
        ];
    }

    /// <summary>
    /// Where a preference should apply.
    ///
    /// Narrowest first, and global last — the order is the recommendation.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> PreferenceScopeOptions(bool hasTrip, string language)
    {
        var swedish = IsSwedish(language);

        var options = new List<GlunoOptionDraft>
        {
            Enum(GlunoPreferenceScopes.Conversation, swedish ? "Bara nu" : "Just now"),
        };

        if (hasTrip)
        {
            options.Add(Enum(
                GlunoPreferenceScopes.Trip, swedish ? "Det här Äventyret" : "This Adventure"));
        }

        options.Add(Enum(
            GlunoPreferenceScopes.Global, swedish ? "Framtida resor" : "Future trips"));

        return options;
    }

    public static IReadOnlyList<GlunoOptionDraft> ProposalConflictOptions(string language)
    {
        var swedish = IsSwedish(language);

        return
        [
            Enum("move", swedish ? "Flytta den" : "Move it"),
            Enum("remove", swedish ? "Ta bort den" : "Remove it"),
            Enum("choose_other_day", swedish ? "Välj en annan dag" : "Pick another day"),
        ];
    }

    /// The question, in the user's language. Short by contract.
    public static string QuestionFor(string type, string language)
    {
        var swedish = IsSwedish(language);

        return type switch
        {
            // Short by contract. The options ARE the explanation; a sentence
            // about scope in front of them is words nobody reads before
            // tapping.
            GlunoClarificationTypes.Adventure => swedish
                ? "Vilket Adventure gäller det?" : "Which Adventure is this about?",
            GlunoClarificationTypes.Day => swedish
                ? "Vilken dag menar du?" : "Which day do you mean?",
            GlunoClarificationTypes.Activity => swedish
                ? "Vilken menar du?" : "Which one do you mean?",
            GlunoClarificationTypes.Place => swedish
                ? "Vilken av dem?" : "Which one?",
            GlunoClarificationTypes.TransportMode => swedish
                ? "Hur vill ni ta er dit?" : "How do you want to get there?",
            GlunoClarificationTypes.Pace => swedish
                ? "Vilket tempo vill ni ha?" : "What pace would you like?",
            GlunoClarificationTypes.Budget => swedish
                ? "Vilken prisnivå?" : "What price level?",
            GlunoClarificationTypes.PreferenceScope => swedish
                ? "Var ska det gälla?" : "Where should that apply?",
            GlunoClarificationTypes.ProposalConflict => swedish
                ? "Vad vill du göra?" : "What would you like to do?",
            _ => swedish ? "Vad menar du?" : "What do you mean?",
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GlunoOptionDraft Enum(string value, string label, string? icon = null)
        => new(value, label)
        {
            EntityType = GlunoClarificationEntityTypes.Enum,
            Value = value,
            Icon = icon,
        };

    private static bool IsSwedish(string language)
        => string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(TripChoice trip, DateOnly today)
        => trip.StartDate <= today && (trip.EndDate == null || trip.EndDate >= today);

    private static bool IsUpcoming(TripChoice trip, DateOnly today) => trip.StartDate > today;

    private static int DaysUntil(TripChoice trip, DateOnly today)
        => Math.Max(0, trip.StartDate.DayNumber - today.DayNumber);

    /// <summary>
    /// Whether the question names this trip.
    ///
    /// Word-level and bounded: matching on any shared substring would make
    /// "Nice" match "Venice", and a three-letter token match almost anything.
    /// </summary>
    private static readonly char[] WordSeparators =
        [' ', ',', '.', '?', '!', ':', ';', '·', '&', '-', '(', ')', '"', '\''];

    private static bool MentionsTrip(string lowerMessage, TripChoice trip)
    {
        // WORD level, not substring. "Venice" contains "nice", and a substring
        // check would silently resolve a question about Venice to the Nice
        // trip — the exact class of wrong-Adventure guess this whole feature
        // exists to prevent.
        var messageWords = lowerMessage
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var tripWords = (trip.Title + " " + (trip.DestinationSummary ?? ""))
            .ToLowerInvariant()
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            // Short tokens match too much: "och", "the", "&" would make every
            // trip a candidate.
            .Where(word => word.Length >= 4);

        return tripWords.Any(messageWords.Contains);
    }

    private static string Short(DateOnly date, bool swedish)
        => date.ToString(swedish ? "d MMM" : "d MMM",
            CultureInfo.GetCultureInfo(swedish ? "sv-SE" : "en-GB"));

    private static string LongDate(DateOnly date, bool swedish)
        => date.ToString(swedish ? "dddd d MMMM" : "dddd d MMMM",
            CultureInfo.GetCultureInfo(swedish ? "sv-SE" : "en-GB"));
}
