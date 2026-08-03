namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Fixed per-request metadata for one Gluno HTTP request.
///
/// WHY THIS EXISTS. A production turn failed and the only visible fact was the
/// app's generic fallback line — nothing tied the mobile symptom to a backend
/// log line, and nothing said which branch the request died in. This carries
/// one id from the middleware through the controller, the service and the
/// provider, so the summary line and every stage line can be joined by eye.
///
/// SCOPED, one instance per HTTP request: the middleware creates the id, the
/// controller and service stamp facts onto it, the middleware writes the one
/// summary line whatever happens — including when the request escapes as an
/// exception.
///
/// EVERY FIELD IS A FIXED VOCABULARY VALUE, AN ID OR A NUMBER. Never free
/// text, never the user's message, never a header, never provider content.
/// </summary>
public sealed class GlunoRequestDiagnostics
{
    /// Short and unguessable enough to join logs by; never used for auth.
    public string RequestId { get; } = Guid.NewGuid().ToString("N")[..12];

    /// SideQuest's own conversation id — an id this backend minted, never
    /// provider data.
    public Guid? ConversationId { get; set; }

    /// "global" or "adventure" — which kind of chat the turn ran in.
    public string ScopeType { get; set; } = "-";

    /// The first deterministic branch that claimed the request —
    /// direct_place_search, discovery_followup, destination_answer,
    /// model_turn, … Same vocabulary as GlunoResponseOrigins where they
    /// overlap.
    public string IntentBranch { get; set; } = "-";

    /// The producing branch as the turn result reported it.
    public string? ResponseOrigin { get; set; }

    /// The stable failure code, when the turn failed.
    public string? ErrorCode { get; set; }

    /// The travel provider's own verdict for this request, when one ran.
    public string ProviderStatus { get; set; } = "-";

    /// True only when the turn produced a normal answer.
    public bool Completed { get; set; }

    private readonly long _startedAt = Environment.TickCount64;

    public long ElapsedMs => Environment.TickCount64 - _startedAt;

    /// <summary>
    /// The one summary line per request. Structure only — the values are ids,
    /// enums, booleans and durations, and the format string is the contract.
    /// </summary>
    public void WriteSummary(ILogger logger, int httpStatus)
        => logger.LogInformation(
            "[GLUNO] request done requestId={RequestId} conversationId={ConversationId} "
            + "scopeType={ScopeType} intentBranch={IntentBranch} responseOrigin={ResponseOrigin} "
            + "httpStatus={HttpStatus} errorCode={ErrorCode} providerStatus={ProviderStatus} "
            + "completed={Completed} in {Elapsed}ms",
            RequestId,
            ConversationId?.ToString() ?? "-",
            ScopeType,
            IntentBranch,
            ResponseOrigin ?? "-",
            httpStatus,
            ErrorCode ?? "-",
            ProviderStatus,
            Completed,
            ElapsedMs);
}
