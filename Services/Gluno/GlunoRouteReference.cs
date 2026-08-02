using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How confident the resolver is, and why.
///
/// Named rather than numeric: "the user typed the city" and "we guessed from
/// the only remaining stop" are different kinds of certainty, and a score
/// flattens them into one number nobody can act on.
/// </summary>
public enum GlunoRouteMatch
{
    None,
    /// The stop or leg was named outright.
    Named,
    /// A date the user gave falls inside exactly one stop.
    ByDate,
    /// "after Málaga", "before Sevilla", "next city".
    Relative,
    /// "there", "that leg" — carried from what was last discussed.
    Carried,
    /// Several stops or legs fit. The caller must ask.
    Ambiguous,
}

/// <summary>
/// What a message turned out to be about, geographically.
///
/// At most one of the three is set. A question about a leg is not a question
/// about either of its endpoints, and collapsing them is how "what's between
/// Málaga and Ronda" becomes a search in Málaga.
/// </summary>
public sealed record GlunoRouteResolution
{
    public static readonly GlunoRouteResolution None = new();

    public TripRouteStop? Stop { get; init; }
    public TripRouteLeg? Leg { get; init; }
    /// A specific trip day, when the message named one.
    public string? Date { get; init; }

    public GlunoRouteMatch Match { get; init; } = GlunoRouteMatch.None;

    /// Machine reason for telemetry: "named_stop", "date_in_stop", "after".
    public string? Reason { get; init; }

    /// <summary>
    /// The question is geographic and more than one answer fits. The caller
    /// asks with <see cref="Candidates"/> rather than picking.
    /// </summary>
    public bool NeedsClarification { get; init; }

    /// Which kind of card to build: route_stop or route_leg.
    public string? ClarificationType { get; init; }

    public IReadOnlyList<TripRouteStop> Candidates { get; init; } = Array.Empty<TripRouteStop>();
    public IReadOnlyList<TripRouteLeg> LegCandidates { get; init; } = Array.Empty<TripRouteLeg>();

    public bool Resolved => Stop != null || Leg != null || Date != null;
}

/// <summary>
/// Works out which stop, leg or day a message is about — deterministically.
///
/// WHY THIS IS CODE AND NOT PROMPT. "What's after Málaga" has exactly one
/// answer given the route, and it is an index lookup. A model asked to work it
/// out will usually get it right and occasionally answer about Ronda when the
/// trip goes Málaga → Sevilla — and there is no way to tell those two cases
/// apart from the outside. Resolving it here makes the relationship a fact the
/// model is told rather than one it infers.
///
/// THE MATCHING RULE THAT KEEPS BITING. Swedish and Spanish place names inflect
/// and contain each other. "Venice" contains "nice"; "Rondavägen" contains
/// "Ronda". Every comparison below is on WORD BOUNDARIES over a normalised,
/// accent-stripped form, never a substring — that bug has been reintroduced
/// four times in this codebase and this is the file most likely to do it again.
/// </summary>
public static class GlunoRouteReferenceResolver
{
    /// <summary>
    /// Phrases meaning "the journey between two places" rather than a place.
    ///
    /// Stems, matched with a bounded suffix, so "vägen"/"väg" and
    /// "sträckan"/"sträcka" both land without "vägra" doing so.
    /// </summary>
    private static readonly string[] LegPhrases =
    [
        "on the way", "along the way", "en route", "on route", "along the route",
        "between", "stop off", "stopover", "detour",
        "pa vagen", "langs vagen", "langs rutten", "pa vag", "mellan",
        "stanna pa vagen", "strackan", "stracka", "avstickare", "omvag",
    ];

    /// "after Málaga", "before Sevilla", "next city", "first stop", "last day".
    private static readonly string[] AfterWords = ["after", "efter", "following"];
    private static readonly string[] BeforeWords = ["before", "innan", "fore"];
    private static readonly string[] NextWords = ["next", "nasta", "following"];
    private static readonly string[] PreviousWords = ["previous", "foregaende", "forra", "last one"];
    private static readonly string[] FirstWords = ["first", "forsta"];
    private static readonly string[] LastWords = ["last", "final", "sista", "sista dagen"];

    /// "there", "that leg" — meaningless alone, resolved from what was discussed.
    private static readonly string[] VagueWords =
    [
        "there", "that place", "that city", "that stop", "that leg", "that stretch",
        "dar", "den staden", "det stoppet", "den stracken", "den strackan", "dit",
    ];

    /// <summary>
    /// The whole trip is the subject: "analyse our route", "is the trip
    /// sensible". These must NOT produce a city chooser — asking which city to
    /// analyse the route of is answering a different question.
    /// </summary>
    private static readonly string[] WholeRoutePhrases =
    [
        "our route", "the route", "whole trip", "entire trip", "the trip overall",
        "all the stops", "every stop", "the itinerary", "whole itinerary",
        "var rutt", "rutten", "hela resan", "hela rutten", "hela turen",
        "alla stopp", "hela resvagen", "resvagen", "upplagget",
    ];

    /// <summary>
    /// Resolves the geographic subject of a message.
    ///
    /// Order is significance. A named place beats a date, a date beats a
    /// relation, and a relation beats a carried reference — because each is a
    /// stronger statement of what the user actually meant.
    /// </summary>
    public static GlunoRouteResolution Resolve(
        string message,
        TripRouteContext? route,
        DateOnly today,
        string? lastDiscussedStopDate = null)
    {
        if (route == null || route.Stops.Count == 0) return GlunoRouteResolution.None;

        var text = Normalise(message);
        var mainStops = route.Stops.Where(stop => stop.IsMainStop).ToList();

        // ── The whole route ───────────────────────────────────────────────
        //
        // Checked first and answered with nothing. "Analyse our route" is not
        // a question about a stop, and offering a city chooser for it would be
        // asking the user to narrow a question whose whole point is breadth.
        if (MentionsAny(text, WholeRoutePhrases))
        {
            return new GlunoRouteResolution
            {
                Match = GlunoRouteMatch.None,
                Reason = "whole_route",
            };
        }

        // ── Is this about a journey rather than a place? ──────────────────
        var aboutALeg = MentionsAny(text, LegPhrases);

        if (aboutALeg && route.Legs.Count > 0)
        {
            var leg = ResolveLeg(text, route, mainStops);
            if (leg != null) return leg;
        }

        // ── A named stop ──────────────────────────────────────────────────
        var named = mainStops
            .Where(stop => MentionsWord(text, stop.Label))
            .ToList();

        // "after Málaga", "before Sevilla" — the named stop is the ANCHOR, and
        // the answer is its neighbour.
        if (named.Count == 1)
        {
            var anchor = named[0];
            var position = mainStops.IndexOf(anchor);

            if (MentionsAnchorRelation(text, anchor.Label, AfterWords) && position < mainStops.Count - 1)
            {
                return new GlunoRouteResolution
                {
                    Stop = mainStops[position + 1],
                    Match = GlunoRouteMatch.Relative,
                    Reason = "after_stop",
                };
            }

            if (MentionsAnchorRelation(text, anchor.Label, BeforeWords) && position > 0)
            {
                return new GlunoRouteResolution
                {
                    Stop = mainStops[position - 1],
                    Match = GlunoRouteMatch.Relative,
                    Reason = "before_stop",
                };
            }

            return new GlunoRouteResolution
            {
                Stop = anchor,
                Match = GlunoRouteMatch.Named,
                Reason = "named_stop",
            };
        }

        // Two cities named and the message is about the space between them —
        // handled above when a leg phrase was present. Without one it is
        // genuinely ambiguous which they mean.
        if (named.Count > 1)
        {
            return Ask(named, "two_named_stops");
        }

        // ── A date ────────────────────────────────────────────────────────
        if (ResolveDate(text, route, today) is { } byDate) return byDate;

        // ── An ordinal or a bare relation ─────────────────────────────────
        if (MentionsAny(text, FirstWords) && MentionsStopWord(text))
        {
            return new GlunoRouteResolution
            {
                Stop = mainStops[0], Match = GlunoRouteMatch.Relative, Reason = "first_stop",
            };
        }

        if (MentionsAny(text, LastWords) && MentionsStopWord(text))
        {
            return new GlunoRouteResolution
            {
                Stop = mainStops[^1], Match = GlunoRouteMatch.Relative, Reason = "last_stop",
            };
        }

        // "next city" — relative to what was last discussed, or to today.
        if (MentionsAny(text, NextWords) && MentionsStopWord(text))
        {
            var from = AnchorIndex(mainStops, lastDiscussedStopDate, today);
            if (from >= 0 && from < mainStops.Count - 1)
            {
                return new GlunoRouteResolution
                {
                    Stop = mainStops[from + 1], Match = GlunoRouteMatch.Relative, Reason = "next_stop",
                };
            }
        }

        if (MentionsAny(text, PreviousWords) && MentionsStopWord(text))
        {
            var from = AnchorIndex(mainStops, lastDiscussedStopDate, today);
            if (from > 0)
            {
                return new GlunoRouteResolution
                {
                    Stop = mainStops[from - 1], Match = GlunoRouteMatch.Relative, Reason = "previous_stop",
                };
            }
        }

        // ── "there" ───────────────────────────────────────────────────────
        if (MentionsAny(text, VagueWords) && lastDiscussedStopDate != null)
        {
            var carried = mainStops.FirstOrDefault(stop => stop.From == lastDiscussedStopDate);

            if (carried != null)
            {
                return new GlunoRouteResolution
                {
                    Stop = carried, Match = GlunoRouteMatch.Carried, Reason = "carried_stop",
                };
            }
        }

        return GlunoRouteResolution.None;
    }

    // ── Legs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Which journey the message is about.
    ///
    /// Three ways in, strongest first: both ends named, the destination named
    /// ("on the way to Gibraltar"), or neither — in which case the answer is a
    /// question, not a guess.
    /// </summary>
    private static GlunoRouteResolution? ResolveLeg(
        string text, TripRouteContext route, IReadOnlyList<TripRouteStop> mainStops)
    {
        // "between Málaga and Ronda" — both ends named, one leg.
        var both = route.Legs
            .Where(leg => MentionsWord(text, leg.FromLabel) && MentionsWord(text, leg.ToLabel))
            .ToList();

        if (both.Count == 1)
        {
            return new GlunoRouteResolution
            {
                Leg = both[0], Match = GlunoRouteMatch.Named, Reason = "named_leg",
            };
        }

        var byDestination = route.Legs
            .Where(leg => MentionsWord(text, leg.ToLabel))
            .ToList();

        var byOrigin = route.Legs
            .Where(leg => MentionsWord(text, leg.FromLabel))
            .ToList();

        // ── Which end did they name? ──────────────────────────────────────
        //
        // "from Tanger" and "to Tanger" are different journeys, and Tanger is
        // both the arrival of one leg and the departure of the next. Without
        // reading the preposition, "on the way from Tanger" resolves to the
        // leg that ARRIVES there — the one they have already travelled.
        var saysFrom = MentionsAny(text, ["from", "fran", "efter", "after"]);
        var saysTo = MentionsAny(text, ["to ", "till", "mot", "towards"]);

        var first = saysFrom && !saysTo
            ? (Origin: byOrigin, Other: byDestination)
            : (Origin: byDestination, Other: byOrigin);

        if (first.Origin.Count == 1)
        {
            return new GlunoRouteResolution
            {
                Leg = first.Origin[0],
                Match = GlunoRouteMatch.Named,
                Reason = saysFrom && !saysTo ? "leg_by_origin" : "leg_by_destination",
            };
        }

        if (first.Other.Count == 1)
        {
            return new GlunoRouteResolution
            {
                Leg = first.Other[0],
                Match = GlunoRouteMatch.Named,
                Reason = saysFrom && !saysTo ? "leg_by_destination" : "leg_by_origin",
            };
        }

        // A journey question with no journey identified. On a multi-leg trip
        // that is a real choice, and guessing it would search the wrong stretch
        // of road.
        var candidates = both.Count > 1 ? both
            : byDestination.Count > 1 ? byDestination
            : byOrigin.Count > 1 ? byOrigin
            : route.Legs;

        if (candidates.Count <= 1) return null;

        return new GlunoRouteResolution
        {
            Match = GlunoRouteMatch.Ambiguous,
            NeedsClarification = true,
            ClarificationType = Models.GlunoClarificationTypes.RouteLeg,
            LegCandidates = candidates.ToList(),
            Reason = "ambiguous_leg",
        };
    }

    // ── Dates ────────────────────────────────────────────────────────────

    /// <summary>
    /// A date the user gave, mapped to the stop that covers it.
    ///
    /// ISO first, then "9 augusti" / "9 August" / "August 9". Only dates INSIDE
    /// the trip resolve — "the 9th" on a trip that ends on the 8th is not a
    /// trip day, and silently picking the nearest one would answer about a day
    /// they did not ask about.
    /// </summary>
    private static GlunoRouteResolution? ResolveDate(
        string text, TripRouteContext route, DateOnly today)
    {
        var iso = Regex.Match(text, @"\b(\d{4}-\d{2}-\d{2})\b");

        if (iso.Success) return StopCovering(route, iso.Groups[1].Value, "iso_date");

        // ── Day + month name, in either order ─────────────────────────────
        //
        // Scanned as adjacent TOKENS rather than with one regex. A single
        // pattern over "den 9 augusti" matches "den 9" first — which fits the
        // month-first shape perfectly, yields no month, and consumes the 9 so
        // the real date can never match. Both languages are full of
        // three-letter words in front of a number.
        var tokens = Regex.Matches(text, @"[a-z]+|\d{1,2}")
            .Select(match => match.Value)
            .ToList();

        for (var index = 0; index < tokens.Count - 1; index++)
        {
            var left = tokens[index];
            var right = tokens[index + 1];

            var dayText = char.IsDigit(left[0]) ? left : char.IsDigit(right[0]) ? right : null;
            var monthText = char.IsDigit(left[0]) ? right : char.IsDigit(right[0]) ? left : null;

            if (dayText == null || monthText == null) continue;
            if (!int.TryParse(dayText, out var day) || day is < 1 or > 31) continue;

            var month = MonthNumber(monthText);
            if (month == 0) continue;

            // The year the trip is in — a date has no year in "9 augusti", and
            // taking today's would land outside a trip that spans New Year.
            foreach (var year in new[] { StartYear(route), today.Year })
            {
                if (year == 0) continue;

                try
                {
                    var date = new DateOnly(year, month, day);
                    var match = StopCovering(route, Iso(date), "written_date");
                    if (match != null) return match;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 31 February. Not a date, not an error.
                }
            }
        }

        return null;
    }

    private static GlunoRouteResolution? StopCovering(TripRouteContext route, string iso, string reason)
    {
        var stop = route.Stops.FirstOrDefault(candidate =>
            candidate.IsMainStop && candidate.Dates.Contains(iso));

        return stop == null
            ? null
            : new GlunoRouteResolution
            {
                Stop = stop, Date = iso, Match = GlunoRouteMatch.ByDate, Reason = reason,
            };
    }

    private static int StartYear(TripRouteContext route)
        => DateOnly.TryParse(route.StartDate, CultureInfo.InvariantCulture, out var start) ? start.Year : 0;

    /// English and Swedish month names, by their first three letters. Both
    /// languages agree on those for every month, which is why three is enough.
    private static int MonthNumber(string word) => word switch
    {
        var w when w.StartsWith("jan") => 1,
        var w when w.StartsWith("feb") => 2,
        var w when w.StartsWith("mar") => 3,
        var w when w.StartsWith("apr") => 4,
        var w when w.StartsWith("maj") || w.StartsWith("may") => 5,
        var w when w.StartsWith("jun") => 6,
        var w when w.StartsWith("jul") => 7,
        var w when w.StartsWith("aug") => 8,
        var w when w.StartsWith("sep") => 9,
        var w when w.StartsWith("okt") || w.StartsWith("oct") => 10,
        var w when w.StartsWith("nov") => 11,
        var w when w.StartsWith("dec") => 12,
        _ => 0,
    };

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GlunoRouteResolution Ask(IReadOnlyList<TripRouteStop> candidates, string reason)
        => new()
        {
            Match = GlunoRouteMatch.Ambiguous,
            NeedsClarification = true,
            ClarificationType = Models.GlunoClarificationTypes.RouteStop,
            Candidates = candidates,
            Reason = reason,
        };

    /// Where to count "next" and "previous" from: what was last discussed,
    /// otherwise where the trip is today, otherwise its beginning.
    private static int AnchorIndex(
        IReadOnlyList<TripRouteStop> stops, string? lastDiscussedStopDate, DateOnly today)
    {
        if (lastDiscussedStopDate != null)
        {
            var index = stops.ToList().FindIndex(stop => stop.From == lastDiscussedStopDate);
            if (index >= 0) return index;
        }

        var iso = Iso(today);
        var current = stops.ToList().FindIndex(stop => stop.Dates.Contains(iso));

        return current >= 0 ? current : 0;
    }

    /// True when the message is talking about stops at all, so a bare "next"
    /// in an unrelated sentence does not resolve to a city.
    private static bool MentionsStopWord(string text)
        => MentionsAny(text, ["city", "stop", "town", "place", "day", "stad", "stopp", "ort", "dag", "plats"]);

    /// <summary>
    /// Whether a relation word appears NEAR the anchor rather than anywhere in
    /// the sentence.
    ///
    /// "After Málaga, what about Ronda?" is a relation. "We ate in Málaga after
    /// the museum" is not, and a whole-sentence check cannot tell them apart.
    /// Twenty characters is about four words either side.
    /// </summary>
    private static bool MentionsAnchorRelation(string text, string anchor, string[] words)
    {
        var normalisedAnchor = Normalise(anchor);
        var at = IndexOfWord(text, normalisedAnchor);
        if (at < 0) return false;

        var from = Math.Max(0, at - 20);
        var window = text[from..Math.Min(text.Length, at + normalisedAnchor.Length)];

        return MentionsAny(window, words);
    }

    private static bool MentionsAny(string text, IReadOnlyList<string> phrases)
        => phrases.Any(phrase => IndexOfWord(text, Normalise(phrase)) >= 0);

    private static bool MentionsWord(string text, string word)
        => IndexOfWord(text, Normalise(word)) >= 0;

    /// <summary>
    /// Word-boundary search with a bounded inflection allowance.
    ///
    /// THE BUG THIS PREVENTS: a plain Contains matches "Nice" inside "Venice"
    /// and "Ronda" inside "Rondavägen". Requiring a boundary before the match
    /// and allowing at most three trailing letters after it covers Swedish
    /// inflection ("resan", "tåget", "fredagen") without opening the door to
    /// unrelated longer words.
    /// </summary>
    private static int IndexOfWord(string text, string word)
    {
        if (word.Length == 0) return -1;

        var from = 0;

        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0) return -1;

            var startsClean = at == 0 || !char.IsLetterOrDigit(text[at - 1]);

            if (startsClean)
            {
                var after = at + word.Length;
                var extra = 0;

                while (after + extra < text.Length && char.IsLetter(text[after + extra])) extra++;

                if (extra <= 3) return at;
            }

            from = at + 1;
        }

        return -1;
    }

    /// <summary>
    /// Lower-cased and accent-stripped.
    ///
    /// So "Málaga" matches "malaga" and "Tanger" matches "tánger". A user
    /// typing on a phone keyboard is not going to reach for the accent, and a
    /// resolver that needs them is a resolver that fails silently.
    /// </summary>
    private static string Normalise(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
