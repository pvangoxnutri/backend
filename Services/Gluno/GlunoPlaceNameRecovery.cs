using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Pulls a place-name candidate out of an add sentence, for the recovery
/// search.
///
/// THE SITUATION THIS SERVES. "Skapa Real Alcázar" arrives in a conversation
/// where the name only ever appeared in the model's own prose — no cards, no
/// references, nothing structured to resolve against. The model must not
/// answer that (its text is not a place identity), and "ask me for suggestions
/// first" is a dead end. The honest deterministic move is to SEARCH the
/// provider for the name and verify what comes back.
///
/// WHAT THIS EXTRACTS IS A QUERY, NEVER AN IDENTITY. The words go to the
/// provider; the place the user gets is whichever verified result matches, by
/// the same matcher the shown-list path uses. The model's earlier prose plays
/// no part.
///
/// Returns null for itinerary requests — "lägg till en vilodag" is about the
/// plan, not a place, and belongs to the model.
/// </summary>
public static class GlunoPlaceNameRecovery
{
    /// The add phrases, longest first so "lagg till" wins over "lagg".
    private static readonly string[] AddPhrases =
    [
        "lagg till", "lagg in", "boka in", "ta med", "satt in",
        "skapa", "boka", "add", "put", "include", "create",
    ];

    /// Dropped from the FRONT of the candidate only. "Casa de Pilatos" keeps
    /// its interior "de".
    private static readonly HashSet<string> LeadingFillers = new(StringComparer.Ordinal)
    {
        "en", "ett", "den", "det", "de", "dom", "a", "an", "the",
        "garna", "tack", "please", "och", "ocksa", "also", "in",
    };

    /// <summary>
    /// Words that describe the itinerary rather than name a place. A candidate
    /// made ONLY of these is not a place name, and the turn belongs to the
    /// model.
    /// </summary>
    private static readonly HashSet<string> ItineraryWords = new(StringComparer.Ordinal)
    {
        "vilodag", "vilodagar", "vila", "paus", "lunch", "middag", "frukost",
        "fika", "kvall", "morgon", "dag", "dagar", "dagen", "notering",
        "anteckning", "aktivitet", "aktiviteter", "tid", "timme", "timmar",
        "mote", "rest", "day", "days", "break", "dinner", "breakfast", "note",
        "activity", "activities", "hour", "hours", "meeting", "nap", "walk",
        "promenad",
        // Demonstratives and ordinals point at something SHOWN — the shown-list
        // matcher's job, never a search query. "Lägg till den andra" searched
        // as the word "andra" would add a random place called Andra.
        "dar", "har", "denna", "detta", "samma", "forsta", "andra", "tredje",
        "fjarde", "femte", "sista", "there", "here", "same", "first", "second",
        "third", "fourth", "fifth", "last", "one", "that", "this",
    };

    private static readonly HashSet<string> DayWords = new(StringComparer.Ordinal)
    {
        "mandag", "tisdag", "onsdag", "torsdag", "fredag", "lordag", "sondag",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
        "sunday", "idag", "imorgon", "today", "tomorrow",
    };

    /// <summary>
    /// The words after the add verb, minus a trailing day reference, or null
    /// when nothing name-shaped remains.
    ///
    /// Original casing survives — it makes a better provider query — and the
    /// result is capped through <see cref="GlunoPlaceSearchContexts.Sanitise"/>
    /// so a runaway sentence can never become a stored search phrase.
    /// </summary>
    public static string? ExtractCandidate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var tokens = message
            .Split([' ', ',', '!', '?', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('.', ':', ';', '"', '\''))
            .Where(token => token.Length > 0)
            .ToList();

        // ── Find the add phrase ───────────────────────────────────────────
        var start = -1;

        for (var index = 0; index < tokens.Count && start < 0; index++)
        {
            foreach (var phrase in AddPhrases)
            {
                var phraseWords = phrase.Split(' ');
                if (index + phraseWords.Length > tokens.Count) continue;

                var matches = true;
                for (var offset = 0; offset < phraseWords.Length; offset++)
                {
                    if (Normalise(tokens[index + offset]) != phraseWords[offset])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    start = index + phraseWords.Length;
                    break;
                }
            }
        }

        if (start < 0 || start >= tokens.Count) return null;

        var candidate = tokens.Skip(start).ToList();

        // ── Trailing day reference goes ───────────────────────────────────
        //
        // "på torsdag", "on Friday", a bare weekday, an ISO date. The day is
        // the add flow's business, not the search's.
        while (candidate.Count > 0 && IsDayish(candidate[^1]))
        {
            candidate.RemoveAt(candidate.Count - 1);

            if (candidate.Count > 0 && Normalise(candidate[^1]) is "pa" or "on" or "till")
            {
                candidate.RemoveAt(candidate.Count - 1);
            }
        }

        while (candidate.Count > 0 && LeadingFillers.Contains(Normalise(candidate[0])))
        {
            candidate.RemoveAt(0);
        }

        if (candidate.Count == 0) return null;

        // Nothing but itinerary vocabulary is not a name.
        if (candidate.All(token => ItineraryWords.Contains(Normalise(token))
            || LeadingFillers.Contains(Normalise(token))))
        {
            return null;
        }

        return GlunoPlaceSearchContexts.Sanitise(string.Join(' ', candidate));
    }

    private static bool IsDayish(string token)
    {
        var normalised = Normalise(token);

        return DayWords.Contains(normalised)
            || DateOnly.TryParseExact(
                token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static string Normalise(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        }

        return builder.ToString();
    }
}
