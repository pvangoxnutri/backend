namespace sidequest.backend.Services.Gluno;

public static class GlunoTurnActionTypes
{
    /// Resume the exact add that failed. Same place, same day, same key.
    public const string RetryPlaceAdd = "retry_place_add";

    /// Ask for a fresh shortlist, because the old one no longer holds the
    /// place. A new recommendation turn, not a retry of the failed one.
    public const string ShowNewPlaceSuggestions = "show_new_place_suggestions";
}

/// <summary>
/// Something the app may offer to do again, described entirely by ids the
/// server minted.
///
/// WHY THIS IS NOT A NEW ENDPOINT WITH A SIGNED TOKEN. It does not need one.
/// The pair (messageId, optionKey) is already server-generated, already
/// ownership-scoped, and already the route of the add endpoint — every call
/// re-verifies the user, the conversation, membership and the stored Location
/// ID. An action here is a statement that the same route is worth calling
/// again; inventing a second identity for it would be a second thing to keep
/// consistent and a second thing to get wrong.
///
/// WHAT THE CLIENT MAY SEND BACK is exactly what it received: the ids. No place
/// name, no coordinates, no provider id, and no chat message — which is the
/// whole point, because the production failure was the user being asked to
/// retype "lägg till Casas de Pilatos".
///
/// <see cref="Date"/> is what preserves a day the user already chose. Absent
/// when the failure happened before that question, and the retry then continues
/// to the ordinary day choice.
/// </summary>
public sealed class GlunoTurnAction
{
    public required string Type { get; init; }

    /// The turn that showed the place. Null on an action that is not about one.
    public Guid? MessageId { get; init; }

    /// Which card. Null when the failure happened before one was identified —
    /// the app then offers fresh suggestions rather than a retry.
    public string? OptionKey { get; init; }

    /// The day already chosen, so a retry does not ask again.
    public DateOnly? Date { get; init; }

    /// <summary>
    /// Reused verbatim by the retry, so a failure followed by a retry followed
    /// by a slow success cannot produce two proposals.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// The action a failed lookup deserves, or none.
    ///
    /// NOTHING for a permanent failure. A button that cannot work is worse than
    /// no button — it invites a loop, and each press costs another upstream
    /// call against a provider that has already refused.
    /// </summary>
    public static GlunoTurnAction? For(
        GlunoRehydrationStatus status,
        Guid messageId,
        string? optionKey,
        DateOnly? date = null,
        string? idempotencyKey = null)
        => status switch
        {
            // The place is gone from the results. Retrying the same lookup
            // would fail the same way; a fresh shortlist is the way forward.
            GlunoRehydrationStatus.NotFound => new GlunoTurnAction
            {
                Type = GlunoTurnActionTypes.ShowNewPlaceSuggestions,
                MessageId = messageId,
            },

            // Transient: the provider was busy or could not be reached.
            GlunoRehydrationStatus.Busy or GlunoRehydrationStatus.Unavailable
                when optionKey != null => new GlunoTurnAction
                {
                    Type = GlunoTurnActionTypes.RetryPlaceAdd,
                    MessageId = messageId,
                    OptionKey = optionKey,
                    Date = date,
                    IdempotencyKey = idempotencyKey,
                },

            // Transient, but nobody has said which place yet — so there is
            // nothing to retry, only something to ask for again.
            GlunoRehydrationStatus.Busy or GlunoRehydrationStatus.Unavailable => new GlunoTurnAction
            {
                Type = GlunoTurnActionTypes.ShowNewPlaceSuggestions,
                MessageId = messageId,
            },

            _ => null,
        };
}

/// <summary>
/// What Gluno says when a place could not be fetched again.
///
/// FIXED STRINGS, CHECKED BY EVALS. The production failure was a model-written
/// sentence — misspelled, and telling the user to retype the command they had
/// just sent. None of these mention a place name, ask for a message to be
/// repeated, or describe the machinery.
/// </summary>
public static class GlunoPlaceFailureText
{
    public static string For(GlunoRehydrationStatus status, string? language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return status switch
        {
            // The place is no longer in the provider's results. Nothing to
            // retry — the honest next step is a fresh shortlist.
            GlunoRehydrationStatus.NotFound => swedish
                ? "Jag kunde inte hämta platsen igen. Ta fram nya förslag."
                : "I couldn't fetch that place again. Let's find some new suggestions.",

            // Over a rate limit. The one case where waiting genuinely helps, so
            // it is the one case that says so.
            GlunoRehydrationStatus.Busy => swedish
                ? "Jag kunde inte hämta platsen just nu. Försök igen om en liten stund."
                : "I couldn't fetch that place just now. Try again in a moment.",

            _ => swedish
                ? "Jag kunde inte förbereda förslaget just nu."
                : "I couldn't prepare the suggestion just now.",
        };
    }

    /// <summary>
    /// What a failed or empty refresh says.
    ///
    /// SEPARATE FROM THE ADD TEXTS because it is a different promise. "I
    /// couldn't fetch that place" is about one place; this is about the search.
    /// An empty result is not a failure and does not offer a retry — the
    /// provider answered, and the answer was nothing.
    /// </summary>
    public static string ForRefresh(bool busy, bool empty, string? language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        if (empty)
        {
            return swedish
                ? "Jag hittade inga nya förslag just nu."
                : "I couldn't find any new suggestions just now.";
        }

        return busy
            ? swedish
                ? "Jag kunde inte ta fram nya förslag just nu. Försök igen om en liten stund."
                : "I couldn't find new suggestions just now. Try again in a moment."
            : swedish
                ? "Jag kunde inte ta fram nya förslag just nu."
                : "I couldn't find new suggestions just now.";
    }
}
