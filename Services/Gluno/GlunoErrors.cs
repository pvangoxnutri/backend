namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The one shape a failed Gluno request comes back as.
///
/// THE BUG THIS CLOSES. A production failure reached the app as HTTP 502 with
/// no code and no retry flag, and the chat rendered "code: missing,
/// retry: missing" next to a sentence that ran off the edge of the screen. The
/// backend's own failures always carried both — so that body did not come from
/// the backend at all. It came from the edge, which knows nothing about this
/// contract and never will.
///
/// Which is the point: a client cannot rely on the server always being the one
/// that answers. So the contract is enforced on BOTH sides. Here, every failure
/// is built by one function that cannot omit a field. In the app, a response
/// that does not match is mapped to a generic failure rather than rendered
/// field by field.
///
/// WHY `message` IS ENGLISH AND NOT LOCALISED. The app localises the CODE — it
/// has the user's language and the copy. This string is the last resort for a
/// client that does not recognise the code, and one plain sentence is better
/// than an empty bubble. It is never preferred over the app's own copy.
///
/// WHY `error` IS STILL THERE. Clients built against the earlier contract read
/// it. Sending both costs a few bytes and means the rollout does not have to be
/// ordered.
/// </summary>
public static class GlunoErrors
{
    /// <summary>
    /// A failure body. Every field is filled, always.
    ///
    /// Returns object rather than a DTO so it serialises with the same
    /// camelCase settings as every other response, and so nothing can construct
    /// a partial one by leaving a property unset.
    ///
    /// `responseOrigin` and `requestId` are diagnostics for the app's debug
    /// export: which branch produced the failure, and the id that joins it to
    /// the backend's own log lines. Both are fixed-vocabulary values or ids —
    /// never text — and both are optional so older call sites keep compiling.
    /// </summary>
    public static object Body(
        string code, bool retryable, string? responseOrigin = null, string? requestId = null) => new
    {
        code,
        // The earlier field name, same value.
        error = code,
        message = Fallback(code),
        retryable,
        responseOrigin,
        requestId,
    };

    /// <summary>
    /// The HTTP status a code is reported with.
    ///
    /// CENTRAL, so a code cannot arrive as 502 from one endpoint and 409 from
    /// another. Anything unrecognised is a 502 that may be retried — unknown
    /// means unknown, and refusing a retry for something we cannot classify is
    /// the wrong way to be wrong.
    /// </summary>
    public static int StatusFor(string code) => code switch
    {
        GlunoFailureCodes.Cancelled => 499,

        "empty_message" or "conversation_archived" or "unknown_option"
            or "missing_option" or "query_too_short" => 400,

        GlunoFailureCodes.AuthorizationChanged => 403,

        "conversation_not_found" or "clarification_not_found" or "message_not_found"
            or "place_not_found" or "place_not_available" => 404,

        "duplicate_in_flight" or "clarification_closed" or "clarification_stale"
            or "place_not_retained" => 409,

        // The USER's own ceiling only. A model provider rate-limiting us is not
        // the user hitting a limit, and reporting it as 429 would have the app
        // tell them to slow down for something they did not do.
        GlunoFailureCodes.UserUsageLimit => 429,

        "gluno_unavailable" or "place_lookup_busy" => 503,

        _ => 502,
    };

    /// <summary>
    /// Whether offering "try again" is honest for this code.
    ///
    /// A missing key fails identically on every tap, and a button that never
    /// works is worse than no button.
    /// </summary>
    public static bool IsRetryable(string code) => code switch
    {
        "place_lookup_busy" or "place_lookup_failed" or "duplicate_in_flight" => true,
        _ => GlunoFailureCodes.IsRetryable(code),
    };

    /// <summary>
    /// A short English sentence per code, for a client that has no copy for it.
    ///
    /// Deliberately plain and deliberately about the user's situation. No
    /// status, no provider, no exception — the app shows this to a person.
    /// </summary>
    public static string Fallback(string code) => code switch
    {
        "empty_message" => "There was nothing to send.",
        "conversation_not_found" => "That conversation could not be found.",
        "conversation_archived" => "That conversation is closed.",
        "duplicate_in_flight" => "That is already being sent.",
        GlunoFailureCodes.AuthorizationChanged => "You no longer have access to that Adventure.",
        GlunoFailureCodes.UserUsageLimit => "You have reached your limit for now.",
        GlunoFailureCodes.Cancelled => "Cancelled.",
        "gluno_unavailable" => "Gluno is unavailable right now.",

        "clarification_not_found" or "clarification_closed" or "clarification_stale"
            => "That question is no longer open. Ask Gluno again.",
        "unknown_option" or "missing_option" => "That choice is no longer available.",
        "query_too_short" => "Type a little more to search.",

        "message_not_found" or "place_not_found" or "place_not_retained" or "place_not_available"
            => "That suggestion is no longer available. Ask Gluno again.",
        "place_lookup_busy" => "I couldn't fetch place suggestions just now. Try again in a moment.",
        "place_lookup_failed" => "I couldn't fetch place suggestions just now. Try again.",

        _ => "I couldn't answer just now. Try again.",
    };
}
