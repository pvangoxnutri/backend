using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Gluno's AI provider, on the official Anthropic SDK.
///
/// This is the ONLY file in SideQuest that knows a model vendor exists. The
/// API key it reads is server-side configuration and is never exposed through
/// any endpoint — the mobile app talks to /api/gluno, never to a model API.
///
/// It drives the tool loop but decides nothing: every tool call the model
/// makes is handed straight to the caller's <c>executeAction</c> delegate,
/// which is where membership, scope and parameter validation happen. A model
/// asking for something it may not do gets a rejection back as a tool result
/// and has to answer around it.
///
/// Logging policy: never the prompt, never the context, never the reply,
/// never the key. Only counts and stop reasons.
/// </summary>
public sealed class AnthropicGlunoAiProvider : IGlunoAiProvider
{
    /// How many model → tool → model round trips one user message may cause.
    /// Bounds worst-case latency and spend for a single chat turn.
    private const int MaxToolIterations = 4;

    /// <summary>
    /// Concurrent tool calls inside one assistant turn.
    ///
    /// Small on purpose. These hit a rate-limited place API and the database,
    /// and three at once already captures the useful parallelism for a single
    /// chat turn — beyond that the wins are marginal and the 429s are not.
    /// </summary>
    private const int MaxParallelTools = 3;

    private readonly IConfiguration _config;
    private readonly GlunoModelPolicy _models;
    private readonly ILogger<AnthropicGlunoAiProvider> _logger;
    private readonly AnthropicClient? _client;

    public AnthropicGlunoAiProvider(
        IConfiguration config, GlunoModelPolicy models, ILogger<AnthropicGlunoAiProvider> logger)
    {
        _config = config;
        _models = models;
        _logger = logger;

        var apiKey = config["Gluno:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new AnthropicClient { ApiKey = apiKey };
        }
    }

    /// <summary>
    /// A key AND a model id. Both are configuration, and a deployment with one
    /// but not the other must report itself unavailable rather than failing
    /// mid-turn with a provider 400 the user cannot act on.
    /// </summary>
    public bool IsConfigured => _client != null && _models.IsConfigured;

    /// Machine reason for /api/gluno/status — "not_configured" either way. The
    /// app is never told WHICH piece is missing.
    public string? UnavailableReason
        => _client == null ? "not_configured" : _models.UnavailableReason;

    private int MaxTokens => _config.GetValue("Gluno:MaxTokens", 4096);

    /// Gluno answers in a chat panel on a phone, where a reply that lands in a
    /// couple of seconds beats a marginally better one that takes fifteen.
    /// Medium is the starting point, not a ceiling — raise it here without a
    /// deploy via Gluno__Effort.
    private Effort EffortLevel => (_config["Gluno:Effort"] ?? "medium").ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    public async Task<GlunoAiResult> RunTurnAsync(
        GlunoAiRequest request,
        Func<GlunoAiToolCall, CancellationToken, Task<GlunoAiToolOutcome>> executeAction,
        CancellationToken ct)
    {
        if (_client == null)
            throw new InvalidOperationException("Gluno AI provider is not configured.");

        var messages = BuildMessages(request);
        var tools = BuildTools(request.Actions);
        var executed = new List<GlunoAiExecutedCall>();

        var totalInput = 0;
        var totalOutput = 0;

        // Per-turn ceiling from the planning strategy, bounded by this
        // provider's own limit so a caller cannot ask for an unbounded loop.
        var maxIterations = Math.Clamp(request.MaxToolIterations ?? MaxToolIterations, 1, MaxToolIterations);

        // The model choice comes from the caller's plan, so a cheap turn uses a
        // cheap model. Falling back to the configured primary keeps every
        // existing call site working unchanged.
        var model = request.Model ?? _models.Choose(new GlunoModelRequest
        {
            Intent = GlunoIntent.GeneralTravelQuestion,
            IntentConfidence = 1,
        }).Model;

        var maxTokens = request.MaxOutputTokens ?? MaxTokens;

        // The provider's own timeout, separate from the caller's token. The
        // caller's token cancels because the USER stopped; this one fires
        // because the model is taking too long, and the two need different
        // handling downstream.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout is { } budget) timeout.CancelAfter(budget);
        var callToken = timeout.Token;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = maxTokens,
                // Adaptive is the default on Opus 5; stated explicitly so a
                // model change in configuration cannot silently turn thinking
                // off. Display stays omitted — Gluno's reasoning is not
                // something the chat panel shows or stores.
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = EffortLevel },
                System = new List<TextBlockParam>
                {
                    new()
                    {
                        Text = request.SystemPrompt,
                        // The system prompt and the tool list are identical on
                        // every turn, so this prefix is worth caching; the
                        // per-turn context deliberately lives in the user turn
                        // below so it cannot invalidate it.
                        CacheControl = new CacheControlEphemeral(),
                    },
                },
                Messages = messages,
                // Never empty in practice — search_places needs neither an
                // Adventure nor edit rights, so every scope keeps at least one.
                Tools = tools,
            };

            var response = await _client.Messages.Create(parameters, cancellationToken: callToken);

            totalInput += (int)response.Usage.InputTokens;
            totalOutput += (int)response.Usage.OutputTokens;

            // Typed comparison rather than a string one: a safety decline is
            // a normal 200 with empty content, and a check that silently never
            // matched would surface as Gluno answering with nothing.
            if (response.StopReason == StopReason.Refusal)
            {
                _logger.LogInformation("[GLUNO] model declined the request (iteration {Iteration}).", iteration);
                return new GlunoAiResult
                {
                    Text = string.Empty,
                    ExecutedCalls = executed,
                    InputTokens = totalInput,
                    OutputTokens = totalOutput,
                    Refused = true,
                };
            }

            var (assistantContent, text, toolUses) = ReadResponse(response);

            if (toolUses.Count == 0)
            {
                return new GlunoAiResult
                {
                    Text = text,
                    ExecutedCalls = executed,
                    InputTokens = totalInput,
                    OutputTokens = totalOutput,
                };
            }

            // Echo the assistant turn back verbatim — thinking blocks keep
            // their signatures, tool_use blocks keep their ids — then answer
            // every one of them in a single user turn, which is what keeps
            // parallel tool use working on later turns.
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            var calls = toolUses
                .Select(toolUse => new GlunoAiToolCall
                {
                    Id = toolUse.ID,
                    Name = toolUse.Name,
                    Input = ToJson(toolUse.Input),
                })
                .ToList();

            var outcomes = request.AllowParallelTools && calls.Count > 1
                ? await ExecuteInParallelAsync(calls, executeAction, callToken)
                : await ExecuteInSequenceAsync(calls, executeAction, callToken);

            // Results are assembled by INDEX, never by completion order. The
            // Messages API pairs a tool_result to its tool_use by id, and a
            // list whose order depends on which provider answered first would
            // make the same question produce a different conversation
            // transcript on every run — untestable, and occasionally wrong.
            var toolResults = new List<ContentBlockParam>(calls.Count);
            for (var index = 0; index < calls.Count; index++)
            {
                executed.Add(new GlunoAiExecutedCall { Call = calls[index], Outcome = outcomes[index] });

                toolResults.Add(new ToolResultBlockParam
                {
                    ToolUseID = calls[index].Id,
                    Content = outcomes[index].ResultJson,
                    IsError = !outcomes[index].Ok,
                });
            }

            messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        _logger.LogWarning("[GLUNO] tool loop hit its {Limit}-iteration ceiling.", maxIterations);
        return new GlunoAiResult
        {
            Text = string.Empty,
            ExecutedCalls = executed,
            InputTokens = totalInput,
            OutputTokens = totalOutput,
            HitIterationLimit = true,
        };
    }

    /// <summary>
    /// Runs independent tool calls concurrently.
    ///
    /// The model already emits several tool_use blocks in one turn when it
    /// wants a place search and a trip overview together. Running those in
    /// sequence means the user waits for the sum; running them together means
    /// they wait for the slowest.
    ///
    /// TWO THINGS THIS GETS RIGHT AND A NAIVE VERSION DOES NOT.
    ///
    /// A failure in one call does not take down the others. Each is wrapped
    /// individually, and a thrown exception becomes a failed tool RESULT the
    /// model can reason about — losing a weather lookup must not discard a
    /// place search that succeeded.
    ///
    /// Concurrency is capped. Unbounded fan-out from a model that decided to
    /// call six tools is a burst at a rate-limited provider, which produces
    /// 429s that look like the provider being down.
    /// </summary>
    private static async Task<GlunoAiToolOutcome[]> ExecuteInParallelAsync(
        IReadOnlyList<GlunoAiToolCall> calls,
        Func<GlunoAiToolCall, CancellationToken, Task<GlunoAiToolOutcome>> executeAction,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxParallelTools);
        var outcomes = new GlunoAiToolOutcome[calls.Count];

        await Task.WhenAll(calls.Select(async (call, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                outcomes[index] = await executeAction(call, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The turn is over. Rethrowing here would be caught by
                // Task.WhenAll and surface as one arbitrary exception; the
                // caller's own token check handles it coherently instead.
                outcomes[index] = Failed();
            }
            catch (Exception)
            {
                // A category, not a message. Tool result text goes back to the
                // model, and an exception message can carry a request URI.
                outcomes[index] = Failed();
            }
            finally
            {
                gate.Release();
            }
        }));

        return outcomes;
    }

    private static async Task<GlunoAiToolOutcome[]> ExecuteInSequenceAsync(
        IReadOnlyList<GlunoAiToolCall> calls,
        Func<GlunoAiToolCall, CancellationToken, Task<GlunoAiToolOutcome>> executeAction,
        CancellationToken ct)
    {
        var outcomes = new GlunoAiToolOutcome[calls.Count];

        for (var index = 0; index < calls.Count; index++)
        {
            outcomes[index] = await executeAction(calls[index], ct);
        }

        return outcomes;
    }

    private static GlunoAiToolOutcome Failed() => new()
    {
        Ok = false,
        ResultJson = """{"ok":false,"error":"tool_failed","note":"That lookup did not complete. Answer without it."}""",
    };

    // ── Request assembly ──────────────────────────────────────────────────

    private static List<MessageParam> BuildMessages(GlunoAiRequest request)
    {
        var messages = new List<MessageParam>();

        foreach (var turn in request.History)
        {
            if (string.IsNullOrWhiteSpace(turn.Text)) continue;
            messages.Add(new MessageParam
            {
                Role = turn.Role == Models.GlunoMessageRoles.Assistant ? Role.Assistant : Role.User,
                Content = turn.Text,
            });
        }

        // The context rides on the current user turn rather than in the system
        // prompt: it changes every turn, and putting it in the system block
        // would invalidate the cached prefix on every single request.
        messages.Add(new MessageParam
        {
            Role = Role.User,
            Content = new List<ContentBlockParam>
            {
                new TextBlockParam
                {
                    Text = $"<SIDEQUEST_CONTEXT>\n{request.ContextJson}\n</SIDEQUEST_CONTEXT>",
                },
                new TextBlockParam { Text = request.UserMessage },
            },
        });

        return messages;
    }

    private static List<ToolUnion> BuildTools(IReadOnlyList<GlunoActionDefinition> actions)
    {
        var tools = new List<ToolUnion>(actions.Count);

        foreach (var action in actions)
        {
            var properties = new Dictionary<string, JsonElement>();
            if (action.InputSchema.TryGetProperty("properties", out var props)
                && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in props.EnumerateObject())
                {
                    properties[property.Name] = property.Value.Clone();
                }
            }

            var required = new List<string>();
            if (action.InputSchema.TryGetProperty("required", out var req)
                && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in req.EnumerateArray())
                {
                    var name = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) required.Add(name);
                }
            }

            tools.Add(new Tool
            {
                Name = action.Name,
                Description = action.Description,
                InputSchema = new()
                {
                    Properties = properties,
                    Required = required,
                },
            });
        }

        return tools;
    }

    // ── Response reading ──────────────────────────────────────────────────

    private static (List<ContentBlockParam> AssistantContent, string Text, List<ToolUseBlock> ToolUses) ReadResponse(
        Message response)
    {
        var assistantContent = new List<ContentBlockParam>();
        var toolUses = new List<ToolUseBlock>();
        var text = new System.Text.StringBuilder();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(textBlock!.Text);
                assistantContent.Add(new TextBlockParam { Text = textBlock.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                // Signature must survive untouched or the next request in the
                // loop is rejected.
                assistantContent.Add(new ThinkingBlockParam
                {
                    Thinking = thinking!.Thinking,
                    Signature = thinking.Signature,
                });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted!.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
            {
                toolUses.Add(toolUse!);
                assistantContent.Add(new ToolUseBlockParam
                {
                    ID = toolUse!.ID,
                    Name = toolUse.Name,
                    Input = toolUse.Input,
                });
            }
        }

        return (assistantContent, text.ToString().Trim(), toolUses);
    }

    private static JsonElement ToJson(object? input)
    {
        if (input is JsonElement element) return element.Clone();
        return JsonSerializer.SerializeToElement(input ?? new { });
    }
}
