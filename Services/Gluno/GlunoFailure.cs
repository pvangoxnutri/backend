using System.Net;
using Anthropic.Exceptions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Stable failure codes.
///
/// WHY A CLOSED LIST. The mobile app localises these, so they are part of the
/// contract between backend and client — an ad-hoc string invented at a call
/// site becomes an untranslated code on somebody's screen. And a raw SDK or
/// provider error must never cross this boundary: it can carry a request URI
/// (which carries an API key), an internal hostname, or a stack trace.
///
/// WHY RETRYABILITY IS PART OF IT. "Try again" on a permanent configuration
/// error is cruel — the user taps it forever and nothing changes. The
/// distinction belongs here, next to the code, rather than being re-derived by
/// each client.
/// </summary>
public static class GlunoFailureCodes
{
    // ── Configuration: permanent until someone changes a setting ──────────
    public const string AiNotConfigured = "ai_not_configured";
    public const string ModelNotConfigured = "model_not_configured";

    // ── Transient: worth another try ─────────────────────────────────────
    public const string AiTimeout = "ai_timeout";
    public const string AiRateLimited = "ai_rate_limited";
    public const string AiMalformedResponse = "ai_malformed_response";
    public const string TripadvisorUnavailable = "tripadvisor_unavailable";
    public const string RoutingUnavailable = "routing_unavailable";
    public const string WeatherUnavailable = "weather_unavailable";

    // ── The turn ran but could not finish well ───────────────────────────
    public const string AiRefusal = "ai_refusal";
    public const string ToolBudgetExceeded = "tool_budget_exceeded";
    public const string ContextTooLarge = "context_too_large";
    public const string GroundingFailed = "grounding_failed";
    public const string ProposalValidationFailed = "proposal_validation_failed";

    // ── Limits and permissions ───────────────────────────────────────────
    public const string UserUsageLimit = "user_usage_limit";
    public const string GlobalUsageLimit = "global_usage_limit";
    public const string AuthorizationChanged = "authorization_changed";

    /// The user pressed stop. Not an error, and must never render as one.
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Whether offering "try again" is honest.
    ///
    /// False for everything that will fail identically on the next tap: a
    /// missing key, a missing model, a refusal, a limit that has not reset.
    /// </summary>
    public static bool IsRetryable(string? code) => code switch
    {
        AiTimeout or AiRateLimited or AiMalformedResponse => true,
        TripadvisorUnavailable or RoutingUnavailable or WeatherUnavailable => true,
        ToolBudgetExceeded or GroundingFailed => true,
        // A configuration problem, a refusal, a usage limit and a permission
        // change are all stable until something outside this request changes.
        _ => false,
    };

    /// <summary>
    /// Maps an exception to a code WITHOUT letting its message escape.
    ///
    /// Type names and status codes only. An SDK exception message can contain
    /// the request URI, and the request URI can contain the key.
    /// </summary>
    public static string FromException(Exception exception) => exception switch
    {
        TaskCanceledException or OperationCanceledException => AiTimeout,

        // The SDK's own exceptions. It does NOT throw HttpRequestException, so
        // without these every provider failure — a rejected key, a model id
        // that does not exist, a parameter the model does not support — landed
        // on the catch-all below and was reported as a malformed RESPONSE.
        //
        // That was wrong twice over. It told the user to try again for
        // problems no amount of retrying can fix, and it erased the one signal
        // that says which piece of configuration is actually wrong.
        AnthropicApiException api => FromStatus(api.StatusCode),

        HttpRequestException http when (int?)http.StatusCode == 429 => AiRateLimited,
        HttpRequestException http when (int?)http.StatusCode >= 500 => AiTimeout,
        System.Text.Json.JsonException => AiMalformedResponse,
        _ => AiMalformedResponse,
    };

    /// <summary>
    /// A provider status code as something the app can act on.
    ///
    /// The split that matters is retryable versus not. 401, 403, 404 and 400
    /// are all somebody's configuration being wrong — they fail identically on
    /// every attempt, and offering a retry button for them is a lie the user
    /// pays for by tapping it repeatedly.
    /// </summary>
    private static string FromStatus(HttpStatusCode status) => (int)status switch
    {
        // The key was rejected. Not "no key" — but from the app's side both
        // mean the same thing: this environment cannot reach a model, and no
        // user action changes that.
        401 or 403 => AiNotConfigured,

        // The Messages API answers 404 when the model id does not exist.
        // Configuration names a model that is retired, misspelled, or not
        // enabled for this account.
        404 => ModelNotConfigured,

        // An invalid request. In practice this is the configured model
        // refusing something the request carries — adaptive thinking and the
        // effort setting are both model-dependent — so it is a model
        // configuration mismatch rather than a transient fault.
        400 or 422 => ModelNotConfigured,

        429 => AiRateLimited,

        // 529 is Anthropic's "overloaded". Genuinely temporary, like a 5xx.
        >= 500 or 529 => AiTimeout,

        _ => AiMalformedResponse,
    };

    /// The fallback wording for a code, in the user's language.
    public static string UserMessage(string code, string language) => code switch
    {
        AiNotConfigured or ModelNotConfigured =>
            string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
                ? "Gluno är inte påslagen här än."
                : "Gluno isn't switched on here yet.",
        AiTimeout => GlunoFallbacks.Text(GlunoFallbackReason.ProviderTimeout, language),
        AiRefusal => GlunoFallbacks.Text(GlunoFallbackReason.ProviderRefused, language),
        ToolBudgetExceeded => GlunoFallbacks.Text(GlunoFallbackReason.ToolBudgetReached, language),
        GroundingFailed => GlunoFallbacks.Text(GlunoFallbackReason.GroundingFailed, language),
        TripadvisorUnavailable => GlunoFallbacks.Text(GlunoFallbackReason.TripadvisorUnavailable, language),
        RoutingUnavailable => GlunoFallbacks.Text(GlunoFallbackReason.RoutingUnavailable, language),
        WeatherUnavailable => GlunoFallbacks.Text(GlunoFallbackReason.WeatherUnavailable, language),
        AuthorizationChanged => GlunoFallbacks.Text(GlunoFallbackReason.AdventureDataChanged, language),

        UserUsageLimit or GlobalUsageLimit =>
            string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
                ? "Jag har hjälpt till mycket idag och behöver vila lite. Prova igen senare — resten av SideQuest fungerar som vanligt."
                : "I've done a lot of helping today and need a breather. Try again later — the rest of SideQuest works as usual.",

        ContextTooLarge =>
            string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
                ? "Det där blev för mycket att ta in på en gång. Fråga om en dag eller en sak i taget så gör jag den ordentligt."
                : "That's more than I can take in at once. Ask about one day or one thing at a time and I'll do it properly.",

        // Cancellation is the user's own doing. It never renders as a failure.
        Cancelled => string.Empty,

        _ => GlunoFallbacks.Text(GlunoFallbackReason.GroundingFailed, language),
    };
}
