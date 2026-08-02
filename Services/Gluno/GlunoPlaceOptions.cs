using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The handle the app sends back when somebody taps a recommended place.
///
/// WHY NOT THE PROVIDER ID. "tripadvisor:12345" identifies a place in the
/// world; it says nothing about whether THIS user was shown it. A client
/// sending one back could name any place the provider has, and the backend
/// would have no way to tell a tap on a rendered card from a hand-written
/// request. Every check it could make afterwards — is it real, is it nearby —
/// would be checking the wrong thing.
///
/// A positional key scoped to the message that produced it inverts that. The
/// tap says "the second thing you showed me", the server already knows what it
/// showed, and a key from another conversation resolves to nothing rather than
/// to somebody else's search results.
///
/// The message id is the other half: it is already ownership-scoped, already
/// persisted, and already carries the places in its payload. Nothing new has
/// to be stored for a recommendation to survive a reload — it always did.
/// </summary>
public static class GlunoPlaceOptions
{
    /// <summary>
    /// How many places a single answer may offer.
    ///
    /// Six. Past that a recommendation stops being a shortlist and becomes a
    /// directory, and nobody reads to the bottom of one in a chat.
    /// </summary>
    public const int MaxPlaces = 6;

    public static string KeyFor(int index) => $"place-{index}";

    /// <summary>
    /// The index a key refers to, or -1 when it is not one of ours.
    ///
    /// Parsed strictly: "place-2" and nothing else. A key that does not match
    /// exactly is refused rather than coerced, because the only source of a
    /// valid key is a card this backend rendered.
    /// </summary>
    public static int IndexOf(string? optionKey)
    {
        if (optionKey == null || !optionKey.StartsWith("place-", StringComparison.Ordinal)) return -1;

        return int.TryParse(optionKey["place-".Length..], out var index) && index >= 0
            ? index
            : -1;
    }

    /// <summary>
    /// The place a key points at, within one assistant turn's payload.
    ///
    /// Returns null for an unknown key, an out-of-range index, or a payload
    /// that cannot be read. All three are the same answer to the caller: this
    /// conversation never showed you that.
    /// </summary>
    public static GlunoPlaceCard? Resolve(GlunoMessage message, string? optionKey)
    {
        var index = IndexOf(optionKey);
        if (index < 0 || message.PayloadJson == null) return null;

        GlunoAssistantPayload? payload;

        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<GlunoAssistantPayload>(
                message.PayloadJson, GlunoJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var places = payload?.Places;

        return places != null && index < places.Count ? places[index] : null;
    }

    /// <summary>
    /// Words that mean "put this in the plan".
    ///
    /// Only ever checked against places THIS conversation already showed. The
    /// phrase decides that the user wants to add something; it never decides
    /// what — that comes from matching against a verified payload.
    /// </summary>
    private static readonly string[] AddWords =
    [
        "lagg till", "lagg in", "boka in", "ta med", "add ", "put ", "include",
    ];

    public static bool IsAddRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = Normalise(message);

        return AddWords.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Which recommended place a sentence like "add Real Alcázar" means.
    ///
    /// Matched against the places already shown, never against a provider
    /// search — the user is pointing at something on their screen, and
    /// searching again could return a different place with a similar name.
    ///
    /// Returns every plausible match: exactly one is resolved silently, more
    /// than one is a real question, and none means asking which they meant.
    /// A model may never supply the answer here; it has no way to produce a
    /// provider reference at all.
    /// </summary>
    public static IReadOnlyList<int> Match(IReadOnlyList<GlunoPlaceCard> places, string message)
    {
        if (places.Count == 0) return Array.Empty<int>();

        var text = Normalise(message);

        // ── Named outright ────────────────────────────────────────────────
        //
        // Word-boundary, so "Alcázar" does not match inside a longer word and
        // a two-word name still matches when the user typed only part of it.
        var named = new List<int>();

        for (var index = 0; index < places.Count; index++)
        {
            if (MentionsName(text, places[index].Name)) named.Add(index);
        }

        if (named.Count > 0) return named;

        // ── By position ───────────────────────────────────────────────────
        //
        // "the first one", "den andra". Counted as people count, from one.
        for (var ordinal = 0; ordinal < Ordinals.Length; ordinal++)
        {
            if (!Ordinals[ordinal].Any(word => ContainsWord(text, word))) continue;

            return ordinal < places.Count ? [ordinal] : Array.Empty<int>();
        }

        return Array.Empty<int>();
    }

    private static readonly string[][] Ordinals =
    [
        ["forsta", "first"],
        ["andra", "second"],
        ["tredje", "third"],
        ["fjarde", "fourth"],
        ["femte", "fifth"],
    ];

    /// <summary>
    /// Whether the message names this place.
    ///
    /// A place name is often several words, and people type part of it —
    /// "Alcázar" for "Real Alcázar de Sevilla". So every significant word of
    /// the name is tried, and any one of them matching on a word boundary
    /// counts. Short words are excluded: "de" and "la" would match everything.
    /// </summary>
    private static bool MentionsName(string text, string name)
    {
        var words = Normalise(name)
            .Split([' ', '-', ',', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .ToList();

        return words.Count > 0 && words.Any(word => ContainsWord(text, word));
    }

    /// Word-boundary search with a bounded inflection allowance, matching the
    /// rest of the codebase — "Alcázars" counts, "Alcázarvägen" does not.
    private static bool ContainsWord(string text, string word)
    {
        var from = 0;

        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0) return false;

            var startsClean = at == 0 || !char.IsLetterOrDigit(text[at - 1]);

            if (startsClean)
            {
                var after = at + word.Length;
                var extra = 0;

                while (after + extra < text.Length && char.IsLetter(text[after + extra])) extra++;

                if (extra <= 3) return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static string Normalise(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
