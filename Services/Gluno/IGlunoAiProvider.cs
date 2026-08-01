using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One turn of conversation as the AI provider layer sees it. Deliberately
/// model-agnostic: no content blocks, no provider ids, nothing that would tie
/// the conversation store or the chat orchestrator to a particular vendor.
/// </summary>
public sealed class GlunoTurn
{
    /// <see cref="Models.GlunoMessageRoles.User"/> or
    /// <see cref="Models.GlunoMessageRoles.Assistant"/>.
    public required string Role { get; init; }
    public required string Text { get; init; }
}

public sealed class GlunoAiToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Input { get; init; }
}

/// <summary>
/// What the executor gave back for one tool call. The provider only needs the
/// JSON and whether it failed — what the action meant is not its business.
/// </summary>
public sealed class GlunoAiToolOutcome
{
    public required bool Ok { get; init; }
    public required string ResultJson { get; init; }
}

public sealed class GlunoAiRequest
{
    public required string SystemPrompt { get; init; }
    /// Serialised SIDEQUEST_CONTEXT for this turn.
    public required string ContextJson { get; init; }
    /// Prior turns, oldest first. Text only — tool traffic is not replayed.
    public required IReadOnlyList<GlunoTurn> History { get; init; }
    public required string UserMessage { get; init; }
    public required IReadOnlyList<GlunoActionDefinition> Actions { get; init; }

    /// <summary>
    /// Model rounds this turn may spend, tool loops included. Null uses the
    /// provider's own default.
    ///
    /// Set per turn by the planning strategy: an app-help question gets two, a
    /// full itinerary gets five. Without it every turn carries the worst case's
    /// ceiling, and a model that misreads a tool result can spend all of it on
    /// the cheapest question in the product.
    /// </summary>
    public int? MaxToolIterations { get; init; }

    /// <summary>
    /// The model id for this turn, from <see cref="GlunoModelPolicy"/>. Null
    /// uses the configured primary.
    ///
    /// Server-side only. It never appears in a response, and the app has no
    /// concept of which model answered.
    /// </summary>
    public string? Model { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// How long this turn's model calls may take in total.
    ///
    /// Distinct from the caller's CancellationToken: that one fires because the
    /// USER pressed stop, this one because the model is slow. Downstream they
    /// need different handling — one is silent, the other is a fallback.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// True when tool calls in one assistant turn may run concurrently.
    ///
    /// Only safe when the turn plan says the offered tools are independent. The
    /// model can emit several tool_use blocks at once, and running dependent
    /// ones in parallel would execute them against data that does not exist yet.
    /// </summary>
    public bool AllowParallelTools { get; init; }
}

/// <summary>
/// One tool call as it actually happened, kept so the orchestrator can persist
/// the audit trail without the provider needing to know about the database.
/// </summary>
public sealed class GlunoAiExecutedCall
{
    public required GlunoAiToolCall Call { get; init; }
    public required GlunoAiToolOutcome Outcome { get; init; }
}

public sealed class GlunoAiResult
{
    public required string Text { get; init; }
    public IReadOnlyList<GlunoAiExecutedCall> ExecutedCalls { get; init; } = Array.Empty<GlunoAiExecutedCall>();
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    /// True when the model declined the request outright. The orchestrator
    /// turns this into a plain apology, not an error.
    public bool Refused { get; init; }
    /// True when the tool loop hit its iteration ceiling before the model
    /// produced a final answer.
    public bool HitIterationLimit { get; init; }
}

/// <summary>
/// The AI provider layer.
///
/// Everything vendor-specific lives behind this interface: model ids, content
/// blocks, tool wire format, token accounting, the tool loop's mechanics.
/// Nothing above it — the chat orchestrator, the action executor, the context
/// builder, the controller, the app — names a model vendor.
///
/// Note the shape of <c>RunTurnAsync</c>: the provider drives the tool loop
/// because looping is a property of the model API, but it executes nothing
/// itself. Each call the model asks for is handed to <c>executeAction</c>,
/// which is where authorisation and validation live. A provider therefore
/// cannot widen what Gluno is allowed to do, only ask.
/// </summary>
public interface IGlunoAiProvider
{
    /// False when no API key OR no model id is configured. The API then answers
    /// with a clear "assistant unavailable" instead of failing per request.
    bool IsConfigured { get; }

    /// A machine reason when unavailable — "not_configured". Never says which
    /// piece is missing; that is deployment detail.
    string? UnavailableReason { get; }

    Task<GlunoAiResult> RunTurnAsync(
        GlunoAiRequest request,
        Func<GlunoAiToolCall, CancellationToken, Task<GlunoAiToolOutcome>> executeAction,
        CancellationToken ct);
}
