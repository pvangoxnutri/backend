using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A pure place-list question, recognised deterministically.
///
/// <see cref="Destination"/> is the user's own words for where, in their own
/// casing, or null when the message named nowhere and the Adventure has to
/// answer. It is never provider content.
/// </summary>
public sealed record GlunoDirectPlaceQuery
{
    public required TravelPlaceCategory Category { get; init; }
    public string? Destination { get; init; }
}

/// <summary>
/// Recognises the questions whose whole answer is a provider shortlist.
///
/// THE PRODUCTION FAILURE THIS FIXES. "Platser i Sevilla" contains none of the
/// intent router's category words, classified as Unclear, and went to the model
/// — which wrote its own list of Sevilla landmarks as prose. No structured
/// places, no optionKeys, no provider data, and every later "lägg till" had
/// nothing to resolve against. The router's markers were beside the point:
/// whether a message is a pure place question is decidable from the text, and
/// the answer to one is a search, not a paragraph.
///
/// DELIBERATELY NARROW. A question that compares, plans, changes, or reasons
/// still belongs to the model — the failure mode of matching too much is a bare
/// list where somebody wanted judgement. When in doubt this returns null and
/// the ordinary pipeline runs.
///
/// Pure and deterministic: same text, same answer, no context, no model.
/// </summary>
public static class GlunoDirectPlaceSearch
{
    /// A message longer than this is a request with reasoning in it, not a
    /// list request.
    private const int MaxWords = 10;

    /// <summary>
    /// Nouns that name a kind of place list. Matched as words on the
    /// accent-folded text, so "sevärdheter" and "sevardheter" are the same.
    /// </summary>
    private static readonly (string Word, TravelPlaceCategory Category)[] ListNouns =
    [
        ("platser", TravelPlaceCategory.General),
        ("stallen", TravelPlaceCategory.General),
        ("sevardheter", TravelPlaceCategory.Attraction),
        ("sevardhet", TravelPlaceCategory.Attraction),
        ("attraktioner", TravelPlaceCategory.Attraction),
        ("museer", TravelPlaceCategory.Attraction),
        ("museum", TravelPlaceCategory.Attraction),
        ("restauranger", TravelPlaceCategory.Restaurant),
        ("restaurang", TravelPlaceCategory.Restaurant),
        ("matstallen", TravelPlaceCategory.Restaurant),
        ("barer", TravelPlaceCategory.Restaurant),
        ("kafeer", TravelPlaceCategory.Restaurant),
        ("hotell", TravelPlaceCategory.Hotel),
        ("boenden", TravelPlaceCategory.Hotel),
        ("places", TravelPlaceCategory.General),
        ("sights", TravelPlaceCategory.Attraction),
        ("attractions", TravelPlaceCategory.Attraction),
        ("museums", TravelPlaceCategory.Attraction),
        ("restaurants", TravelPlaceCategory.Restaurant),
        ("bars", TravelPlaceCategory.Restaurant),
        ("cafes", TravelPlaceCategory.Restaurant),
        ("hotels", TravelPlaceCategory.Hotel),
    ];

    /// "What should we…" phrasings that mean the same thing as a list noun.
    private static readonly string[] WhatToDoPhrases =
    [
        "vad borde vi se", "vad ska vi se", "vad borde vi gora", "vad ska vi gora",
        "vad kan man gora", "vad kan man se", "vad finns att gora", "vad finns att se",
        "vad borde jag se", "vad ska jag gora",
        "what should we see", "what should we do", "what can we do", "what can we see",
        "what is there to do", "whats there to do", "what to do", "what to see",
        "things to do", "things to see",
    ];

    /// <summary>
    /// Phrases that mean the question is NOT a bare list: it compares, changes
    /// something, or asks for judgement between named options. Matched inside
    /// a space-padded copy so single words match whole words only.
    /// </summary>
    private static readonly string[] Disqualifiers =
    [
        " jamfor", " compare", " versus ", " vs ", " skillnad", " vilken av ", " which of ",
        " eller ", " hellre ", " or ",
        // Change verbs belong to the add and planning flows, never here.
        " lagg till ", " lagg in ", " boka", " planera", " flytta", " ta bort ", " schemalagg",
        " add ", " book", " plan ", " move ", " remove", " schedule",
    ];

    /// Prepositions a destination follows: "platser i Sevilla", "in Seville".
    private static readonly HashSet<string> Prepositions = new(StringComparer.Ordinal)
    {
        "i", "pa", "in", "at", "till", "to", "near", "nara", "runt", "around", "kring",
    };

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

        // ── Is this a list request at all? ────────────────────────────────
        var category = (TravelPlaceCategory?)null;

        foreach (var (word, nounCategory) in ListNouns)
        {
            if (words.Contains(word, StringComparer.Ordinal))
            {
                category = nounCategory;
                break;
            }
        }

        if (category == null && WhatToDoPhrases.Any(
            phrase => text.Contains(phrase, StringComparison.Ordinal)))
        {
            category = TravelPlaceCategory.General;
        }

        if (category == null) return null;

        return new GlunoDirectPlaceQuery
        {
            Category = category.Value,
            Destination = ExtractDestination(message),
        };
    }

    /// <summary>
    /// The place the message names, in the user's own casing, or null.
    ///
    /// A capitalised run after a preposition — "i Sevilla", "in New York" — or,
    /// failing that, a capitalised run at the end of the message. Never the
    /// first word, whose capital is the sentence's.
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

        return run.Count > 0 ? string.Join(' ', run) : null;
    }

    private static bool StartsUpper(string token)
        => token.Length >= 2 && char.IsUpper(token[0]);

    private static string Normalise(string value)
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
