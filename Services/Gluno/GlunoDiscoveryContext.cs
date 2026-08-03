using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The conversation's active place-discovery thread, server-owned.
///
/// WHY THIS EXISTS. After "Platser i Sevilla" showed five places, "Ge mig 5
/// platser" and "Fler" are about SEVILLA — resolving them from the sentence
/// alone re-asks a question the conversation already answered, and in
/// production it re-ran a destination guess instead of continuing the thread.
///
/// WHAT IT HOLDS is SideQuest's own facts: the resolved destination, our
/// category vocabulary, the language, the id of the turn that showed the list,
/// the requested count — and the provider LOCATION IDS already shown, which
/// are the one thing Terra's terms permit keeping. No names, no content.
///
/// One per conversation, replaced by each new search, expired after
/// <see cref="GlunoDiscoveryContexts.Lifetime"/>.
/// </summary>
public sealed class GlunoDiscoveryContext
{
    /// SideQuest's resolved geography — the user's words or their Adventure.
    public string Destination { get; set; } = string.Empty;

    /// SideQuest's four-value category vocabulary, wire form.
    public string Category { get; set; } = "general";

    public string Language { get; set; } = "en";

    /// The assistant turn that showed the latest list.
    public Guid? LastMessageId { get; set; }

    /// <summary>
    /// Bare provider location ids already shown in this thread, so "fler" can
    /// exclude them. Ids only — the single thing the provider's terms allow
    /// keeping — and capped so the list cannot grow without bound.
    /// </summary>
    public List<string> ShownLocationIds { get; set; } = [];

    public int? RequestedCount { get; set; }

    /// <summary>
    /// True when the discovery question is waiting for a destination — the
    /// user asked for tips with no place named and no Adventure to borrow one
    /// from, and the next short message names where.
    /// </summary>
    public bool AwaitingDestination { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
}

public static class GlunoDiscoveryContexts
{
    /// Long enough to browse a list and ask for more; short enough that
    /// "fler" tomorrow is a fresh question.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public const int MaxShownIds = 50;

    public static GlunoDiscoveryContext? Usable(GlunoDiscoveryContext? context, DateTime nowUtc)
        => context == null || context.ExpiresAtUtc <= nowUtc ? null : context;

    public static GlunoDiscoveryContext WithLifetime(GlunoDiscoveryContext context, DateTime nowUtc)
    {
        context.CreatedAtUtc = nowUtc;
        context.ExpiresAtUtc = nowUtc + Lifetime;
        return context;
    }

    /// New ids appended, oldest dropped past the cap, duplicates ignored.
    public static void RememberShown(GlunoDiscoveryContext context, IEnumerable<string> locationIds)
    {
        foreach (var id in locationIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (context.ShownLocationIds.Contains(id, StringComparer.Ordinal)) continue;

            context.ShownLocationIds.Add(id);
        }

        if (context.ShownLocationIds.Count > MaxShownIds)
        {
            context.ShownLocationIds.RemoveRange(
                0, context.ShownLocationIds.Count - MaxShownIds);
        }
    }
}

/// <summary>
/// What a short message means INSIDE an active discovery thread.
/// </summary>
public sealed record GlunoDiscoveryFollowUp
{
    /// More results, avoiding what was already shown.
    public bool More { get; init; }

    /// A specific number of results.
    public int? RequestedCount { get; init; }

    /// Switch the thread to another category, same destination.
    public TravelPlaceCategory? SwitchCategory { get; init; }
}

/// <summary>
/// Recognises the short follow-ups a shown list invites.
///
/// ONLY CONSULTED WHILE A DISCOVERY CONTEXT IS ACTIVE — "fler" with nothing
/// on the table means nothing and falls through to ordinary routing. The
/// phrases are deliberately short-message-only: a long sentence is a new
/// request, not a follow-up.
/// </summary>
public static class GlunoDiscoveryFollowUps
{
    private const int MaxWords = 6;

    private static readonly string[] MorePhrases =
    [
        "fler", "flera", "mer", "fler forslag", "fler tips", "har du andra",
        "har du fler", "andra forslag", "nagot annat", "nagot mer",
        "nagot lugnare", "nagot annorlunda", "visa fler", "fler platser",
        "more", "show more", "others", "other suggestions", "anything else",
        "something else", "something calmer", "more places",
    ];

    /// Category nouns for "visa restauranger istället" — reuses the search
    /// resolver's fuzzy vocabulary so a plural or a typo still switches.
    private static readonly (string Word, TravelPlaceCategory Category)[] CategoryNouns =
    [
        ("restauranger", TravelPlaceCategory.Restaurant),
        ("restaurang", TravelPlaceCategory.Restaurant),
        ("matstallen", TravelPlaceCategory.Restaurant),
        ("barer", TravelPlaceCategory.Restaurant),
        ("kafeer", TravelPlaceCategory.Restaurant),
        ("restaurants", TravelPlaceCategory.Restaurant),
        ("bars", TravelPlaceCategory.Restaurant),
        ("cafes", TravelPlaceCategory.Restaurant),
        ("hotell", TravelPlaceCategory.Hotel),
        ("boenden", TravelPlaceCategory.Hotel),
        ("hotels", TravelPlaceCategory.Hotel),
        ("sevardheter", TravelPlaceCategory.Attraction),
        ("attraktioner", TravelPlaceCategory.Attraction),
        ("museer", TravelPlaceCategory.Attraction),
        ("attractions", TravelPlaceCategory.Attraction),
        ("sights", TravelPlaceCategory.Attraction),
        ("museums", TravelPlaceCategory.Attraction),
        ("platser", TravelPlaceCategory.General),
        ("places", TravelPlaceCategory.General),
        ("aktiviteter", TravelPlaceCategory.General),
        ("activities", TravelPlaceCategory.General),
    ];

    private static readonly (string Word, int Value)[] CountWords =
    [
        ("tva", 2), ("tre", 3), ("fyra", 4), ("fem", 5), ("sex", 6),
        ("two", 2), ("three", 3), ("four", 4), ("five", 5), ("six", 6),
    ];

    public static GlunoDiscoveryFollowUp? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = GlunoDirectPlaceSearch.Normalise(message);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || words.Length > MaxWords) return null;

        // A destination in the message means a NEW search, not a follow-up —
        // "platser i Ronda" after Sevilla changes the subject.
        if (GlunoDirectPlaceSearch.ExtractDestination(message) != null) return null;

        // ── A count: "ge mig 5 platser", "5 till", "ge mig fem" ───────────
        var count = (int?)null;

        foreach (var word in words)
        {
            if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
                && digits is >= 1 and <= 10)
            {
                count = Math.Min(digits, GlunoPlaceOptions.MaxPlaces);
            }

            foreach (var (countWord, value) in CountWords)
            {
                if (word == countWord) count = Math.Min(value, GlunoPlaceOptions.MaxPlaces);
            }
        }

        // ── A category switch: "visa restauranger istället" ───────────────
        var category = (TravelPlaceCategory?)null;

        foreach (var word in words)
        {
            foreach (var (noun, nounCategory) in CategoryNouns)
            {
                if (GlunoDirectPlaceSearch.Fuzzy(word, noun))
                {
                    category ??= nounCategory;
                }
            }
        }

        var padded = " " + text + " ";
        var more = MorePhrases.Any(phrase =>
            padded.Contains(" " + phrase + " ", StringComparison.Ordinal));

        // ── What it adds up to ────────────────────────────────────────────
        //
        // A category noun inside an active thread is a follow-up whether or
        // not "istället" was typed: "restauranger?" after a Sevilla list can
        // only mean Sevilla's restaurants. A bare count is "give me N". A
        // more-phrase is more. Anything else is not a follow-up at all.
        if (category != null && category != TravelPlaceCategory.General)
        {
            return new GlunoDiscoveryFollowUp { SwitchCategory = category, RequestedCount = count };
        }

        if (count != null) return new GlunoDiscoveryFollowUp { RequestedCount = count, More = more };
        if (more) return new GlunoDiscoveryFollowUp { More = true };

        // "platser"/"places" with no qualifier: more of the same.
        if (category == TravelPlaceCategory.General)
        {
            return new GlunoDiscoveryFollowUp { More = true };
        }

        return null;
    }

    /// <summary>
    /// A short bare place name, answering "which destination?". Only ever
    /// consulted while a discovery context is awaiting one.
    /// </summary>
    /// Words that are answers but not places. "Ja", "tack" and "vet inte" must
    /// not be sent to a geo lookup.
    private static readonly HashSet<string> NotAPlace = new(StringComparer.Ordinal)
    {
        "ja", "nej", "jo", "ok", "okej", "okay", "tack", "kanske", "hmm",
        "vet", "inte", "ingen", "aning", "yes", "no", "thanks", "maybe",
        "dont", "know", "sure", "nope",
    };

    public static string? ParseDestinationAnswer(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var tokens = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('?', '.', '!', ',', ':', ';', '"', '\''))
            .Where(token => token.Length > 0)
            .ToList();

        if (tokens.Count is 0 or > 3) return null;
        if (!tokens.All(token => token.All(char.IsLetter) && token.Length >= 2)) return null;

        if (tokens.Any(token =>
            NotAPlace.Contains(GlunoDirectPlaceSearch.Normalise(token))))
        {
            return null;
        }

        return string.Join(' ', tokens);
    }
}
