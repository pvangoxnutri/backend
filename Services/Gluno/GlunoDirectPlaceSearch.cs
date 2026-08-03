using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A pure place-discovery question, recognised deterministically.
///
/// <see cref="Destination"/> is the user's own words for where, in their own
/// casing, or null when the message named nowhere and the Adventure has to
/// answer. It is never provider content. <see cref="RequestedCount"/> is the
/// number they asked for, when they asked for one.
/// </summary>
public sealed record GlunoDirectPlaceQuery
{
    public required TravelPlaceCategory Category { get; init; }
    public string? Destination { get; init; }
    public int? RequestedCount { get; init; }
}

/// <summary>
/// Recognises the questions whose whole answer is a provider shortlist.
///
/// A HYBRID RESOLVER, NOT A PHRASE LIST. The first build matched a handful of
/// exact phrases and missed "Ge mig tips på saker att göra i Linz" — a central
/// travel question — because nobody had typed that exact wording into the
/// list. This version reads three SIGNAL GROUPS and combines them:
///
///  • recommendation words — "tips", "förslag", "rekommendera", "recommend";
///  • place/activity nouns — "platser", "saker", "sevärdheter", "restauranger";
///  • question phrases — "vad kan man…", "något kul…", "things to do".
///
/// A noun or a recommendation word alone is not enough: it must sit in a
/// REQUEST — a named destination, a question shape, or an imperative opening —
/// so "Vi såg ett museum igår" stays a sentence and "Sevärdheter i Tanger"
/// becomes a search.
///
/// TYPO-TOLERANT ON INTENT, NEVER ON IDENTITY. "föfslag" and "sevärdheterr"
/// still read as discovery, via bounded edit distance against the signal
/// vocabulary. Which PLACE something is remains a verified provider match —
/// fuzziness here only decides which deterministic path runs.
///
/// Comparison, planning and personal-judgement questions are disqualified and
/// keep their model turn — see <see cref="Disqualifiers"/>.
///
/// Pure and deterministic: same text, same answer, no context, no model.
/// </summary>
public static class GlunoDirectPlaceSearch
{
    /// Longer than this is a request with reasoning in it, not a list request.
    private const int MaxWords = 12;

    // ── Signal groups ─────────────────────────────────────────────────────

    /// Words that ask for recommendations outright.
    private static readonly string[] RecommendationWords =
    [
        "tips", "forslag", "rekommendera", "rekommenderar", "rekommendation",
        "recommend", "recommends", "recommendation", "recommendations",
        "suggest", "suggestion", "suggestions",
    ];

    /// <summary>
    /// Nouns that name a kind of place list, with the category each implies.
    /// Matched fuzzily, so plural/singular and a doubled letter still count.
    /// </summary>
    private static readonly (string Word, TravelPlaceCategory Category)[] PlaceNouns =
    [
        ("platser", TravelPlaceCategory.General),
        ("plats", TravelPlaceCategory.General),
        ("stallen", TravelPlaceCategory.General),
        ("stalle", TravelPlaceCategory.General),
        ("saker", TravelPlaceCategory.General),
        ("aktiviteter", TravelPlaceCategory.General),
        ("aktivitet", TravelPlaceCategory.General),
        ("utflykter", TravelPlaceCategory.General),
        ("utflykt", TravelPlaceCategory.General),
        ("sevardheter", TravelPlaceCategory.Attraction),
        ("sevardhet", TravelPlaceCategory.Attraction),
        ("attraktioner", TravelPlaceCategory.Attraction),
        ("attraktion", TravelPlaceCategory.Attraction),
        ("museer", TravelPlaceCategory.Attraction),
        ("museum", TravelPlaceCategory.Attraction),
        ("restauranger", TravelPlaceCategory.Restaurant),
        ("restaurang", TravelPlaceCategory.Restaurant),
        ("matstallen", TravelPlaceCategory.Restaurant),
        ("barer", TravelPlaceCategory.Restaurant),
        ("kafeer", TravelPlaceCategory.Restaurant),
        ("hotell", TravelPlaceCategory.Hotel),
        ("boenden", TravelPlaceCategory.Hotel),
        ("boende", TravelPlaceCategory.Hotel),
        ("things", TravelPlaceCategory.General),
        ("places", TravelPlaceCategory.General),
        ("place", TravelPlaceCategory.General),
        ("spots", TravelPlaceCategory.General),
        ("activities", TravelPlaceCategory.General),
        ("sights", TravelPlaceCategory.Attraction),
        ("attractions", TravelPlaceCategory.Attraction),
        ("museums", TravelPlaceCategory.Attraction),
        ("restaurants", TravelPlaceCategory.Restaurant),
        ("bars", TravelPlaceCategory.Restaurant),
        ("cafes", TravelPlaceCategory.Restaurant),
        ("hotels", TravelPlaceCategory.Hotel),
    ];

    /// Question shapes that mean "what is there", in the forms people type —
    /// including "va" for "vad".
    private static readonly string[] DiscoveryPhrases =
    [
        "vad kan man", "va kan man", "vad finns det att", "vad finns att",
        "vad borde vi", "vad ska vi", "vad borde jag", "vad ska jag",
        "vad rekommenderar", "va rekommenderar", "hitta pa", "nagot kul",
        "nagot roligt", "nagot att gora", "varda att besoka", "vard att besoka",
        "vart att besoka", "vart att se",
        "what should we", "what can we", "what should i", "what can i",
        "what is there to", "whats there to", "things to do", "things to see",
        "worth visiting", "worth seeing", "what do you recommend",
        "something fun", "something to do",
    ];

    /// Imperative openings that turn a bare noun into a request: "Visa…",
    /// "Ge mig…", "Hitta…".
    private static readonly string[] ImperativeOpenings =
    [
        "visa", "ge", "hitta", "foresla", "lista",
        "show", "give", "find", "list", "suggest",
    ];

    private static readonly string[] QuestionWords =
    [
        "vad", "va", "vilka", "vilken", "vilket", "var", "what", "which", "whats", "where",
    ];

    /// <summary>
    /// Phrases that mean the question is NOT a bare list: it compares, plans,
    /// changes something, or asks for judgement. These keep their model turn —
    /// with verified places arriving through tools, never invented. Matched
    /// inside a space-padded copy so single words match whole words only.
    /// </summary>
    private static readonly string[] Disqualifiers =
    [
        " jamfor", " compare", " versus ", " vs ", " skillnad", " vilken av ",
        " vilket av ", " which of ", " eller ", " hellre ", " or ",
        " passar ", " utifran ", " basta for ", " best for ",
        // Change verbs belong to the add and planning flows, never here.
        " lagg till ", " lagg in ", " boka", " planera", " flytta", " ta bort ",
        " schemalagg", " add ", " book", " plan ", " move ", " remove", " schedule",
    ];

    /// <summary>
    /// Words after a preposition that are not destinations, so "i staden" and
    /// "i sommar" do not become geographies. Time words matter as much as
    /// place-ish nouns: "Vad kan man göra i juli?" names a month, and sending
    /// a month to a geo lookup answers about nowhere.
    /// </summary>
    private static readonly HashSet<string> DestinationStopWords = new(StringComparer.Ordinal)
    {
        "staden", "stan", "omradet", "narheten", "byn", "centrum", "hemma",
        "city", "town", "area", "downtown", "home",
        // Seasons and rough times.
        "sommar", "sommaren", "vinter", "vintern", "varen", "hosten", "host",
        "kvall", "kvallen", "morgon", "morgonen", "helgen", "veckan", "natten",
        "summer", "winter", "spring", "autumn", "fall", "evening", "morning",
        "weekend", "week", "night", "tonight",
        // Months.
        "januari", "februari", "mars", "april", "maj", "juni", "juli",
        "augusti", "september", "oktober", "november", "december",
        "january", "february", "march", "may", "june", "july", "august",
        "october",
        // Weekdays.
        "mandag", "tisdag", "onsdag", "torsdag", "fredag", "lordag", "sondag",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
        "sunday", "idag", "imorgon", "today", "tomorrow",
    };

    /// Prepositions a destination follows: "platser i Sevilla", "in Seville".
    private static readonly HashSet<string> Prepositions = new(StringComparer.Ordinal)
    {
        "i", "pa", "in", "at", "till", "to", "near", "nara", "runt", "around", "kring",
    };

    private static readonly (string Word, int Value)[] CountWords =
    [
        ("en", 1), ("ett", 1), ("tva", 2), ("tre", 3), ("fyra", 4), ("fem", 5),
        ("sex", 6), ("sju", 7), ("atta", 8), ("nio", 9), ("tio", 10),
        ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5),
        ("six", 6), ("seven", 7), ("eight", 8), ("nine", 9), ("ten", 10),
    ];

    public static GlunoDirectPlaceQuery? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = Normalise(message);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || words.Length > MaxWords) return null;

        // More than one sentence is a message with reasoning in it. The
        // trailing question mark of a single question is not.
        var inner = message.Trim().TrimEnd('?', '!', '.', ' ');
        if (inner.IndexOfAny(['.', '?', '!']) >= 0) return null;

        var padded = " " + text + " ";
        if (Disqualifiers.Any(marker => padded.Contains(marker, StringComparison.Ordinal)))
        {
            return null;
        }

        // ── The signals ───────────────────────────────────────────────────
        var hasPhrase = DiscoveryPhrases.Any(
            phrase => text.Contains(phrase, StringComparison.Ordinal));

        var hasRecommendationWord = words.Any(
            word => RecommendationWords.Any(signal => Fuzzy(word, signal)));

        var nounCategory = (TravelPlaceCategory?)null;

        foreach (var word in words)
        {
            foreach (var (noun, category) in PlaceNouns)
            {
                if (!Fuzzy(word, noun)) continue;

                // The first SPECIFIC category wins over General, so
                // "restauranger och barer" reads as restaurants and
                // "platser" alone stays general.
                if (nounCategory is null or TravelPlaceCategory.General)
                {
                    nounCategory = category;
                }
            }
        }

        var destination = ExtractDestination(message);

        // ── A signal must sit in a REQUEST ────────────────────────────────
        //
        // "Vi såg ett museum igår" carries a noun and is a sentence about
        // yesterday. A destination, a question shape, or an imperative
        // opening is what turns a signal into a question.
        var questionish = message.Contains('?')
            || QuestionWords.Contains(words[0], StringComparer.Ordinal);

        var imperative = ImperativeOpenings.Contains(words[0], StringComparer.Ordinal);

        var requested = hasPhrase
            || ((hasRecommendationWord || nounCategory != null)
                && (destination != null || questionish || imperative
                    // A one- or two-word message that IS the signal —
                    // "sevärdheterr", "tips?" — is inherently a request;
                    // there is no sentence around it to be a statement.
                    || words.Length <= 2));

        if (!requested) return null;

        return new GlunoDirectPlaceQuery
        {
            Category = nounCategory ?? TravelPlaceCategory.General,
            Destination = destination,
            RequestedCount = ExtractCount(words),
        };
    }

    /// <summary>
    /// The number of results the message asked for, or null. Bounded to what a
    /// chat answer can carry — "Ge mig 5 platser" is a real request, "Semester
    /// 2026" is not a request for two thousand hotels.
    /// </summary>
    public static int? ExtractCount(IReadOnlyList<string> words)
    {
        foreach (var word in words)
        {
            if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
                && digits is >= 1 and <= 10)
            {
                return Math.Min(digits, GlunoPlaceOptions.MaxPlaces);
            }

            foreach (var (countWord, value) in CountWords)
            {
                // Exact — "en"/"ett" are articles too often to fuzzy-match.
                if (word == countWord) return Math.Min(value, GlunoPlaceOptions.MaxPlaces);
            }
        }

        return null;
    }

    /// <summary>
    /// The place the message names, in the user's own casing, or null.
    ///
    /// A capitalised run after a preposition — "i Sevilla", "in New York" —
    /// or, failing that, a capitalised run at the end of the message. A
    /// LOWERCASE tail after a preposition also counts ("va kan man göra i
    /// linz"), unless it is a stop word — people do not reach for the shift
    /// key mid-question, and the provider does not care about case.
    /// </summary>
    public static string? ExtractDestination(string message)
    {
        var tokens = message.Split(
            [' ', ',', '!', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('?', '.', '!', ':', ';', '"', '\''))
            .Where(token => token.Length > 0)
            .ToList();

        for (var index = 0; index < tokens.Count - 1; index++)
        {
            if (!Prepositions.Contains(Normalise(tokens[index]))) continue;

            var run = CapitalisedRunFrom(tokens, index + 1);
            if (run != null) return run;

            // The lowercase tail: only at the END of the message, only one to
            // three tokens, and never a stop word. "i linz" qualifies; "i
            // staden med barnen" does not.
            if (index + 1 < tokens.Count && tokens.Count - (index + 1) <= 3)
            {
                var tail = tokens.Skip(index + 1).ToList();

                if (tail.All(token => token.Length >= 3
                    && token.All(char.IsLetter)
                    && !DestinationStopWords.Contains(Normalise(token))))
                {
                    return string.Join(' ', tail);
                }
            }
        }

        // No preposition matched. A trailing capitalised run still names a
        // place — "Visa sevärdheter Sevilla" — as long as it is not the
        // sentence-initial capital.
        var end = tokens.Count - 1;

        if (end >= 1 && StartsUpper(tokens[end]))
        {
            var start = end;
            while (start - 1 >= 1 && StartsUpper(tokens[start - 1])) start--;

            return CapitalisedRunFrom(tokens, start);
        }

        return null;
    }

    private static string? CapitalisedRunFrom(IReadOnlyList<string> tokens, int start)
    {
        var run = new List<string>();

        for (var index = start; index < tokens.Count && run.Count < 3; index++)
        {
            if (!StartsUpper(tokens[index])) break;
            run.Add(tokens[index]);
        }

        // "I Augusti" is capitalised and is still a month. The stop list
        // applies whatever the casing.
        if (run.Count > 0 && DestinationStopWords.Contains(Normalise(run[0])))
        {
            return null;
        }

        return run.Count > 0 ? string.Join(' ', run) : null;
    }

    private static bool StartsUpper(string token)
        => token.Length >= 2 && char.IsUpper(token[0]);

    /// <summary>
    /// Whether a typed token means a vocabulary word.
    ///
    /// Exact, or the word plus a bounded inflection (≤3 letters, matching the
    /// codebase's stemming rule), or within a small edit distance — one for
    /// ordinary words, two for long ones. INTENT ONLY: nothing fuzzy ever
    /// decides which place something is.
    /// </summary>
    public static bool Fuzzy(string token, string word)
    {
        if (token == word) return true;

        // Inflection: "sevärdheterr", "tipset", "restaurangerna".
        if (token.Length > word.Length
            && token.Length <= word.Length + 3
            && token.StartsWith(word, StringComparison.Ordinal))
        {
            return true;
        }

        // Typo: "föfslag" → "forslag", "aktivitetr" → "aktiviteter".
        if (word.Length >= 5 && Math.Abs(token.Length - word.Length) <= 2)
        {
            var allowed = word.Length >= 9 ? 2 : 1;
            return EditDistanceAtMost(token, word, allowed);
        }

        return false;
    }

    /// Bounded Levenshtein with early exit — the bound is 1 or 2, so the full
    /// matrix is never needed.
    private static bool EditDistanceAtMost(string a, string b, int bound)
    {
        if (Math.Abs(a.Length - b.Length) > bound) return false;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > bound) return false;

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= bound;
    }

    internal static string Normalise(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
