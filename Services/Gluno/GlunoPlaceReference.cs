using System.Text;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What is kept about a place whose content may not be stored.
///
/// THREE FIELDS, AND NOTHING THAT RENDERS. No name, no address, no rating, no
/// hours, no snippet, no coordinates, no URL, no attribution. Written into a
/// message payload where the full card would otherwise go, so that tapping
/// "Add" days later still has something the server can verify — and so that a
/// reload has nothing to draw a card from, which is the intended outcome
/// rather than an oversight.
///
/// The id alone is useless for display and sufficient for identity. That is the
/// whole design: Tripadvisor Terra forbids storing content and permits keeping
/// the Location ID, so the place is fetched again at the moment somebody wants
/// to act on it.
/// </summary>
public sealed class GlunoPlaceReference
{
    /// SideQuest's own positional key for the card — "place-0". Server
    /// generated, scoped to the message, and the only handle the app ever
    /// sends back.
    public string OptionKey { get; set; } = string.Empty;

    /// Which provider family issued the id, so it can never be handed to
    /// another one. A name, never a key or a host.
    public string ProviderId { get; set; } = string.Empty;

    /// The provider's own bare location id.
    public string LocationId { get; set; } = string.Empty;
}

/// <summary>
/// SideQuest's own request behind a set of references.
///
/// EVERY FIELD HERE IS SIDEQUEST'S, NOT THE PROVIDER'S. A destination resolved
/// from the user's own Adventure, a category from SideQuest's four-value
/// vocabulary, search words SideQuest composed, a locale, a count. None of it
/// came out of a provider response, so none of it is provider content — which
/// is what makes it storable when the results themselves are not.
///
/// It exists for one purpose: to reproduce the same call later. Without it the
/// location id would be an id nobody could look up, since the endpoint that
/// answers by id is governed by an account allowlist and the one that found the
/// place in the first place takes a query and a geography.
///
/// WHAT IS DELIBERATELY ABSENT is the user's own sentence. The message that
/// caused the search can contain anything; the search words below are bounded,
/// stripped and capped before they are written.
/// </summary>
public sealed class GlunoPlaceSearchContext
{
    /// The normalised geography the search ran against — "Sevilla". Required:
    /// the provider needs a place to look in, and rehydration without one would
    /// search the world.
    public string Near { get; set; } = string.Empty;

    /// SideQuest's internal category vocabulary — "restaurant" | "attraction" |
    /// "hotel" | "general". Never a provider taxonomy value.
    public string Category { get; set; } = "general";

    /// Sanitised search words, or null when the search was category-only.
    public string? Query { get; set; }

    /// ISO language, so the same call comes back in the same language.
    public string Language { get; set; } = "en";

    public int Limit { get; set; } = 5;

    /// Where the geography came from — a day location, the destination, the
    /// model. A fixed vocabulary value, useful when a rehydration misses and
    /// somebody has to work out why.
    public string? OriginSource { get; set; }

    /// When the original search ran. Not a licence to cache: it records how
    /// stale the reference is, so a miss can be told from a mistake.
    public DateTime SearchedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsUsable => !string.IsNullOrWhiteSpace(Near);
}

public static class GlunoPlaceSearchContexts
{
    /// <summary>
    /// Longest search phrase that may be kept.
    ///
    /// Short on purpose. A search term is a handful of words — "rooftop bar",
    /// "modern art museum". Anything longer is a sentence that has drifted in
    /// from somewhere, and the cap is what stops a whole user message from
    /// being written down under the name of a query.
    /// </summary>
    public const int MaxQueryLength = 60;

    public const int MaxQueryWords = 6;

    /// <summary>
    /// Search words reduced to search words.
    ///
    /// Letters, digits, spaces and hyphens survive; everything else is dropped
    /// rather than escaped. Then a word cap, then a length cap. The result is
    /// either a short phrase or null — never a truncated sentence, because
    /// half a sentence is still a sentence somebody wrote.
    /// </summary>
    public static string? Sanitise(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var builder = new StringBuilder(query.Length);

        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character == '-')
            {
                builder.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxQueryWords)
            .ToList();

        if (words.Count == 0) return null;

        var text = string.Join(' ', words);

        // Whole words only. A phrase cut mid-word searches for something
        // nobody asked about.
        while (text.Length > MaxQueryLength && words.Count > 1)
        {
            words.RemoveAt(words.Count - 1);
            text = string.Join(' ', words);
        }

        return text.Length > MaxQueryLength ? text[..MaxQueryLength] : text;
    }
}
