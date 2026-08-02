using System.Globalization;
using System.Text.RegularExpressions;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoDetectionOutcome
{
    /// Nothing about this turn is ambiguous, or the ambiguity resolved itself.
    NotApplicable,
    /// A choice was needed and the data answered it. Carry on silently.
    Resolved,
    /// A real choice, with real options. Ask before doing anything expensive.
    NeedsClarification,
}

/// <summary>
/// What the detector decided.
/// </summary>
public sealed record GlunoDetection(GlunoDetectionOutcome Outcome)
{
    public static readonly GlunoDetection NotApplicable = new(GlunoDetectionOutcome.NotApplicable);

    /// <see cref="GlunoClarificationTypes"/>. Set when asking.
    public string? Type { get; init; }
    public IReadOnlyList<GlunoOptionDraft> Options { get; init; } = Array.Empty<GlunoOptionDraft>();

    /// The value the turn should carry on with, when it resolved itself.
    public string? ResolvedValue { get; init; }

    /// Short machine reason for telemetry: "two_fridays", "saved_preference".
    public string? Reason { get; init; }

    public bool AllowFreeText { get; init; }

    public static GlunoDetection Ask(
        string type, IReadOnlyList<GlunoOptionDraft> options, string reason, bool allowFreeText = false)
        => new(GlunoDetectionOutcome.NeedsClarification)
        {
            Type = type,
            Options = options,
            Reason = reason,
            AllowFreeText = allowFreeText,
        };

    public static GlunoDetection Resolved(string type, string value, string reason)
        => new(GlunoDetectionOutcome.Resolved) { Type = type, ResolvedValue = value, Reason = reason };
}

/// <summary>
/// Everything the detector needs, as plain data.
/// </summary>
public sealed class GlunoDetectionInput
{
    public required string Message { get; init; }
    public required GlunoIntentResult Intent { get; init; }
    public required GlunoContext Context { get; init; }
    public required GlunoWorkflow Workflow { get; init; }
    public DateOnly Today { get; init; }
    public string Language { get; init; } = "en";
}

/// <summary>
/// Decides, once, whether this turn is missing a choice.
///
/// WHY ONE PLACE. The alternative is a detection per ambiguity scattered
/// through the chat service, and they would drift: one would run before the
/// provider call and another after, one would ask when the answer was already
/// known, and nobody could say from reading the code what a turn will do.
/// Here the order is explicit and the rules sit next to each other, which is
/// the only way "does this actually change the answer?" stays a single
/// consistent judgement.
///
/// TWO FAILURE MODES, AND THE SECOND IS EASIER TO CAUSE. Asking too little
/// means guessing — the wrong Friday, the wrong museum, the wrong Adventure.
/// Asking too much turns a chat into a form: a chooser in front of questions
/// whose answer was already knowable, which is slower than the sentence it
/// replaced and teaches people to tap without reading. Every rule below
/// resolves silently first and only asks when the data genuinely does not
/// settle it.
///
/// Deterministic and pure. No model, no provider, no database.
/// </summary>
public static class GlunoClarificationDetector
{
    /// <summary>
    /// Runs the checks in order and returns the FIRST that needs an answer.
    ///
    /// Order is significance, not convenience: a turn missing its Adventure
    /// cannot sensibly be asked about its pace, and asking two questions in a
    /// row is how a chat becomes a wizard.
    /// </summary>
    public static GlunoDetection Detect(GlunoDetectionInput input)
    {
        // Never in front of a question that does not need the answer. This is
        // the single guard that stops the feature spreading everywhere.
        if (!CouldChangeTheAnswer(input.Intent)) return GlunoDetection.NotApplicable;

        foreach (var check in new Func<GlunoDetectionInput, GlunoDetection>[]
        {
            // Before the day: "add the second one" is about a specific place,
            // and knowing WHICH changes what the rest of the turn is even for.
            DetectDiscussedPlace,
            DetectDay,
            DetectActivity,
            DetectPlace,
            DetectTransport,
            DetectPace,
            DetectBudget,
            DetectPreferenceScope,
        })
        {
            var detection = check(input);
            if (detection.Outcome != GlunoDetectionOutcome.NotApplicable) return detection;
        }

        return GlunoDetection.NotApplicable;
    }

    /// <summary>
    /// Whether a choice could change what this turn does at all.
    ///
    /// "What is SideQuest?", "which Adventures do I have?", "how does Gluno
    /// work?" have one right answer regardless of any choice — putting a
    /// chooser in front of them is pure friction.
    /// </summary>
    private static bool CouldChangeTheAnswer(GlunoIntentResult intent) => intent.PrimaryIntent
        is not GlunoIntent.SideQuestHelp
        and not GlunoIntent.NavigationRequest
        and not GlunoIntent.ForgetPreference;

    // ── The places Gluno just showed ─────────────────────────────────────

    /// Ordinals, in both languages. Index is 1-based as people count.
    private static readonly (string[] Stems, int Index)[] Ordinals =
    [
        (["forsta", "first"], 1),
        (["andra", "second"], 2),
        (["tredje", "third"], 3),
        (["fjarde", "fourth"], 4),
        (["femte", "fifth"], 5),
    ];

    /// Vague enough to mean "one of those" without saying which.
    private static readonly string[] VagueChoiceWords =
        ["dem", "dom", "them", "nagon", "nagot", "one", "vilken", "which"];

    /// <summary>
    /// "Take the second one", "add one of the restaurants".
    ///
    /// Resolved against the places already shown IN THIS CONVERSATION rather
    /// than by searching again — a fresh search can come back in a different
    /// order, and "the second one" would then mean something the user never
    /// saw. The ids are the ones the provider issued, carried forward by the
    /// context builder.
    /// </summary>
    public static GlunoDetection DetectDiscussedPlace(GlunoDetectionInput input)
    {
        var places = input.Context.DiscussedPlaces;
        if (places.Count == 0) return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        // An ordinal is exact when the list is long enough to have that entry.
        foreach (var (stems, index) in Ordinals)
        {
            if (!ContainsStem(text, stems)) continue;
            if (index > places.Count) break;

            var chosen = places[index - 1];
            return GlunoDetection.Resolved(
                GlunoClarificationTypes.Place, Reference(chosen), $"ordinal_{index}");
        }

        // A name or a category that matches exactly one of them.
        var named = places
            .Where(place => MentionsPlace(text, place))
            .ToList();

        if (named.Count == 1)
            return GlunoDetection.Resolved(
                GlunoClarificationTypes.Place, Reference(named[0]), "named");

        // "The cheapest" — resolvable ONLY when price data actually settles
        // it. The context carries no price level, so this always asks rather
        // than ranking on something we do not have.
        var vague = VagueChoiceWords.Any(word => ContainsWord(text, word)) || named.Count > 1;
        if (!vague) return GlunoDetection.NotApplicable;

        return GlunoDetection.Ask(
            GlunoClarificationTypes.Place,
            GlunoClarificationBuilder.PlaceOptions(places),
            $"places_x{places.Count}",
            // More were shown than fit, or the user means something else
            // entirely — either way a search over the snapshot helps.
            allowFreeText: places.Count > GlunoClarificationBuilder.MaxOptions);
    }

    /// Provider-namespaced: an id is only meaningful with the provider that
    /// issued it, and two providers can use the same number.
    private static string Reference(GlunoDiscussedPlaceContext place)
        => $"{place.Provider}:{place.ExternalId}";

    private static bool MentionsPlace(string text, GlunoDiscussedPlaceContext place)
    {
        var words = Normalise(place.Name + " " + (place.Category ?? ""))
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .ToList();

        // Stem, not whole word. Swedish inflects adjectives and definites —
        // "den italienska" against a category of "italiensk", "restaurangen"
        // against "restaurang" — and whole-word matching finds none of them.
        return words.Count > 0 && ContainsStem(text, words);
    }

    // ── Day ──────────────────────────────────────────────────────────────

    private static readonly (string[] Stems, DayOfWeek Day)[] Weekdays =
    [
        (["mandag", "monday"], DayOfWeek.Monday),
        (["tisdag", "tuesday"], DayOfWeek.Tuesday),
        (["onsdag", "wednesday"], DayOfWeek.Wednesday),
        (["torsdag", "thursday"], DayOfWeek.Thursday),
        (["fredag", "friday"], DayOfWeek.Friday),
        (["lordag", "saturday"], DayOfWeek.Saturday),
        (["sondag", "sunday"], DayOfWeek.Sunday),
    ];

    /// <summary>
    /// "On Friday" when the trip has two Fridays.
    ///
    /// The intent router already picks the FIRST matching weekday so a turn
    /// has something to work with. That is a guess, and on a two-week trip it
    /// is wrong half the time — so this counts them properly and asks when
    /// there is more than one.
    /// </summary>
    public static GlunoDetection DetectDay(GlunoDetectionInput input)
    {
        if (input.Context.Trip is not { } trip) return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        // An explicit date settles it outright.
        if (Regex.IsMatch(text, @"\b\d{4}-\d{2}-\d{2}\b")) return GlunoDetection.NotApplicable;

        var start = trip.StartDate;
        var end = trip.EffectiveEndDate;

        // Relative days are anchored to today and clamped to the trip: there
        // is only ever one "tomorrow".
        if (ContainsStem(text, ["idag", "today"]) || ContainsStem(text, ["imorgon", "tomorrow"]))
        {
            var target = ContainsStem(text, ["imorgon", "tomorrow"])
                ? input.Today.AddDays(1)
                : input.Today;

            return GlunoDetection.Resolved(
                GlunoClarificationTypes.Day, Iso(Clamp(target, start, end)), "relative_day");
        }

        foreach (var (stems, day) in Weekdays)
        {
            if (!ContainsStem(text, stems)) continue;

            var matches = new List<DateOnly>();
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (date.DayOfWeek == day) matches.Add(date);
            }

            if (matches.Count == 0) return GlunoDetection.NotApplicable;

            // One Friday on the trip. Nothing to ask.
            if (matches.Count == 1)
                return GlunoDetection.Resolved(GlunoClarificationTypes.Day, Iso(matches[0]), "one_match");

            var destinations = trip.Destinations ?? EmptySummary(trip);

            return GlunoDetection.Ask(
                GlunoClarificationTypes.Day,
                GlunoClarificationBuilder.DayOptions(destinations, matches, input.Language),
                $"weekday_x{matches.Count}");
        }

        return GlunoDetection.NotApplicable;
    }

    // ── Activity ─────────────────────────────────────────────────────────

    /// <summary>
    /// "Move the museum" when the trip has two museums.
    ///
    /// Gluno must never pick between them. Moving the wrong thing on somebody's
    /// holiday costs far more than one tap.
    /// </summary>
    public static GlunoDetection DetectActivity(GlunoDetectionInput input)
    {
        if (input.Context.Trip is not { } trip) return GlunoDetection.NotApplicable;

        // Only for turns that act on an Activity. A question that merely
        // mentions one does not need it pinned down.
        if (input.Intent.PrimaryIntent is not (GlunoIntent.MoveActivity or GlunoIntent.ImproveExistingDay))
            return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        var matches = trip.Activities
            .Where(activity => MentionsActivity(text, activity))
            .ToList();

        if (matches.Count == 0) return GlunoDetection.NotApplicable;

        if (matches.Count == 1)
            return GlunoDetection.Resolved(
                GlunoClarificationTypes.Activity, matches[0].Id.ToString(), "one_match");

        return GlunoDetection.Ask(
            GlunoClarificationTypes.Activity,
            GlunoClarificationBuilder.ActivityOptions(matches, input.Language),
            $"activity_x{matches.Count}");
    }

    // ── Place ────────────────────────────────────────────────────────────

    private static readonly string[] VaguePlaceWords =
        ["dar", "darborta", "there", "i stan", "in town", "pa plats"];

    /// <summary>
    /// "What can we do there?" on a trip with four stops.
    ///
    /// Resolved silently when the trip only ever goes one place, or when the
    /// message names a stop outright.
    /// </summary>
    public static GlunoDetection DetectPlace(GlunoDetectionInput input)
    {
        if (input.Context.Trip?.Destinations is not { } destinations) return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);
        if (!VaguePlaceWords.Any(word => ContainsWord(text, word))) return GlunoDetection.NotApplicable;

        // Distinct places, in trip order. Two nights in Málaga is one stop for
        // the purposes of "where".
        var stops = destinations.Stops
            .Select(stop => stop.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (stops.Count <= 1)
        {
            return stops.Count == 1
                ? GlunoDetection.Resolved(GlunoClarificationTypes.Place, stops[0], "single_stop")
                : GlunoDetection.NotApplicable;
        }

        // The message names one of them: that IS the answer.
        var named = stops.Where(stop => ContainsWord(text, Normalise(stop))).ToList();
        if (named.Count == 1)
            return GlunoDetection.Resolved(GlunoClarificationTypes.Place, named[0], "named_stop");

        var options = stops
            .Take(GlunoClarificationBuilder.MaxOptions)
            .Select((stop, index) => new GlunoOptionDraft($"stop-{index}", stop)
            {
                Description = destinations.Stops.First(entry =>
                    string.Equals(entry.Label, stop, StringComparison.OrdinalIgnoreCase)).From,
                EntityType = GlunoClarificationEntityTypes.Enum,
                Value = stop,
                Icon = "location-outline",
            })
            .ToList();

        return GlunoDetection.Ask(
            GlunoClarificationTypes.Place, options, $"stops_x{stops.Count}");
    }

    // ── Transport ────────────────────────────────────────────────────────

    private static readonly (string[] Words, string Mode)[] TransportWords =
    [
        (["kora", "bil", "drive", "car"], "car"),
        (["ga", "gang", "promenad", "walk", "walking", "foot"], "walking"),
        (["tag", "buss", "kollektiv", "tunnelbana", "train", "bus", "metro", "transit"], "public_transport"),
        (["cykel", "cykla", "bike", "cycling"], "bike"),
        (["taxi", "uber"], "taxi"),
    ];

    /// <summary>
    /// Only when routing is actually going to run and the message did not say.
    ///
    /// "How do we drive there" already answered it, and asking anyway is the
    /// clearest possible signal that Gluno was not listening.
    /// </summary>
    public static GlunoDetection DetectTransport(GlunoDetectionInput input)
    {
        // Only when a route is actually being LAID OUT. Routing being permitted
        // is not the same as a plan being built, and asking how somebody wants
        // to travel before they have seen anything is pure friction.
        if (!input.Workflow.UsesScheduleEngine || !input.Workflow.AllowsRouting)
            return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        foreach (var (words, mode) in TransportWords)
        {
            // Stem, not whole word: "tåget", "bilen", "cyklar" are all the
            // same answer, and whole-word matching finds none of them.
            if (ContainsStem(text, words))
                return GlunoDetection.Resolved(GlunoClarificationTypes.TransportMode, mode, "stated");
        }

        // A stored preference is an answer the user already gave.
        var saved = Preference(input, GlunoPreferenceKeys.Transport);
        if (saved != null)
            return GlunoDetection.Resolved(GlunoClarificationTypes.TransportMode, saved, "saved_preference");

        return GlunoDetection.Ask(
            GlunoClarificationTypes.TransportMode,
            GlunoClarificationBuilder.TransportOptions(input.Language),
            "no_mode");
    }

    // ── Pace ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Only when a plan is actually being laid out.
    ///
    /// Pace changes how many stops fit in a day. It changes nothing about "what
    /// time does the museum open", so a factual question never sees this.
    /// </summary>
    public static GlunoDetection DetectPace(GlunoDetectionInput input)
    {
        if (!input.Workflow.UsesScheduleEngine) return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        if (ContainsWord(text, "lugnt") || ContainsWord(text, "relaxed") || ContainsWord(text, "chill"))
            return GlunoDetection.Resolved(GlunoClarificationTypes.Pace, "relaxed", "stated");

        if (ContainsWord(text, "fullspackat") || ContainsWord(text, "packed") || ContainsWord(text, "mycket"))
            return GlunoDetection.Resolved(GlunoClarificationTypes.Pace, "packed", "stated");

        var saved = Preference(input, GlunoPreferenceKeys.Pace);
        if (saved != null)
            return GlunoDetection.Resolved(GlunoClarificationTypes.Pace, saved, "saved_preference");

        return GlunoDetection.Ask(
            GlunoClarificationTypes.Pace,
            GlunoClarificationBuilder.PaceOptions(input.Language),
            "no_pace");
    }

    // ── Budget ───────────────────────────────────────────────────────────

    /// <summary>
    /// Only for recommendations, where price level genuinely changes the list.
    /// </summary>
    public static GlunoDetection DetectBudget(GlunoDetectionInput input)
    {
        if (input.Intent.PrimaryIntent is not GlunoIntent.PlaceRecommendation)
            return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        // An explicit price level, or a figure, settles it. "Under 500 kr" is
        // a budget, and asking after it would be absurd.
        if (Regex.IsMatch(text, @"\b\d{2,}\s*(kr|sek|eur|usd|\$|€)\b")
            || ContainsStem(text, ["billig", "cheap", "lyx", "luxury", "exklusiv", "premium", "dyr"]))
        {
            return GlunoDetection.NotApplicable;
        }

        var saved = Preference(input, GlunoPreferenceKeys.Budget);
        if (saved != null)
            return GlunoDetection.Resolved(GlunoClarificationTypes.Budget, saved, "saved_preference");

        return GlunoDetection.Ask(
            GlunoClarificationTypes.Budget,
            GlunoClarificationBuilder.BudgetOptions(input.Language),
            "no_budget");
    }

    // ── Preference scope ─────────────────────────────────────────────────

    /// <summary>
    /// "Remember that I prefer quiet days" — for this trip, or for ever?
    ///
    /// Nothing is stored until the user says. Defaulting to the widest scope
    /// would put a preference on every future trip from one sentence; the
    /// narrowest silently loses what they asked for.
    /// </summary>
    public static GlunoDetection DetectPreferenceScope(GlunoDetectionInput input)
    {
        if (input.Intent.PrimaryIntent is not GlunoIntent.PreferenceUpdate)
            return GlunoDetection.NotApplicable;

        var text = Normalise(input.Message);

        if (ContainsStem(text, ["alltid", "always", "framtid", "future"]))
        {
            return GlunoDetection.Resolved(
                GlunoClarificationTypes.PreferenceScope, GlunoPreferenceScopes.Global, "stated_global");
        }

        if (ContainsStem(text, ["resa", "resan", "aventyr", "trip", "adventure"]))
        {
            return GlunoDetection.Resolved(
                GlunoClarificationTypes.PreferenceScope,
                input.Context.Trip != null ? GlunoPreferenceScopes.Trip : GlunoPreferenceScopes.Conversation,
                "stated_trip");
        }

        return GlunoDetection.Ask(
            GlunoClarificationTypes.PreferenceScope,
            GlunoClarificationBuilder.PreferenceScopeOptions(input.Context.Trip != null, input.Language),
            "no_scope");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? Preference(GlunoDetectionInput input, string key)
        => input.Context.Preferences
            .FirstOrDefault(preference => preference.Key == key)
            ?.Value is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Whether the message points at this Activity.
    ///
    /// Word level, and only on tokens long enough to mean something — a
    /// substring match would make "the bar" match "Barcelona".
    /// </summary>
    private static bool MentionsActivity(string text, GlunoActivityContext activity)
    {
        var words = Normalise(activity.Title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4);

        return words.Any(word => ContainsWord(text, word));
    }

    private static TripDestinationSummary EmptySummary(GlunoTripContext trip) => new()
    {
        Title = trip.Title,
        StartDate = Iso(trip.StartDate),
        EndDate = trip.EndDate.HasValue ? Iso(trip.EndDate.Value) : null,
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly Clamp(DateOnly value, DateOnly start, DateOnly end)
        => value < start ? start : value > end ? end : value;

    /// Lowercased and accent-folded, so "Málaga" matches "malaga".
    private static string Normalise(string text)
    {
        var lower = text.ToLowerInvariant();
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

    private static readonly char[] Separators =
        [' ', ',', '.', '?', '!', ':', ';', '·', '&', '-', '(', ')', '"', '\''];

    private static bool ContainsWord(string text, string word)
        => text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, word, StringComparison.Ordinal))
        // Multi-word needles ("i stan") cannot be token-compared.
        || (word.Contains(' ') && text.Contains(word, StringComparison.Ordinal));

    /// A token starting with the stem and at most three letters longer —
    /// Swedish definite and genitive endings, nothing wider.
    private static bool ContainsStem(string text, IReadOnlyList<string> stems)
        => text.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Any(token =>
            stems.Any(stem =>
                token.StartsWith(stem, StringComparison.Ordinal) && token.Length <= stem.Length + 3));
}
