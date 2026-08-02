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
}
