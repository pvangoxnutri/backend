using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Whether Gluno is switched on for this environment, and whether it could
/// actually answer if it were.
///
/// Two separate questions on purpose. "Enabled" is a deployment decision —
/// Development gets Gluno by default, everything else has to opt in explicitly
/// via Gluno__Enabled, so a production deploy cannot start serving an
/// unfinished assistant just because a key happened to be present. "Configured"
/// is whether an AI provider key exists. Both must hold.
/// </summary>
public sealed class GlunoAvailability
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IGlunoAiProvider _provider;
    private readonly ITravelDataRegistry _travelData;

    public GlunoAvailability(
        IConfiguration config,
        IWebHostEnvironment env,
        IGlunoAiProvider provider,
        ITravelDataRegistry travelData)
    {
        _config = config;
        _env = env;
        _provider = provider;
        _travelData = travelData;
    }

    /// Development defaults to on; every other environment must say so.
    public bool IsEnabled => _config.GetValue("Gluno:Enabled", _env.IsDevelopment());

    public bool IsConfigured => _provider.IsConfigured;

    public bool IsAvailable => IsEnabled && IsConfigured;

    public bool HasTravelData => _travelData.HasConfiguredProvider;

    /// <summary>
    /// "disabled" (not on for this environment) or "not_configured" (no key, or
    /// no model id). Deliberately coarse: the app is told Gluno cannot answer,
    /// never WHICH piece of server configuration is missing.
    /// </summary>
    public string? UnavailableReason
        => !IsEnabled ? "disabled"
         : !IsConfigured ? _provider.UnavailableReason ?? "not_configured"
         : null;
}

public enum GlunoTurnError
{
    None,
    Unavailable,
    EmptyMessage,
    ConversationNotFound,
    ConversationArchived,
    NotTripMember,
    ProviderFailed,
    /// Per-user or global usage ceiling. Existing conversations still open.
    UsageLimitReached,
    /// The user stopped the turn. Not an error — nothing is stored, and the
    /// app must not render it as a failure.
    Cancelled,
    /// An identical send is already running. The client waits rather than
    /// starting a second turn.
    DuplicateInFlight,
}

public sealed class GlunoTurnResult
{
    public GlunoTurnError Error { get; init; }
    public GlunoConversation? Conversation { get; init; }
    public GlunoMessage? UserMessage { get; init; }
    public GlunoMessage? AssistantMessage { get; init; }
    public IReadOnlyList<GlunoProposal> Proposals { get; init; } = Array.Empty<GlunoProposal>();
    /// The persisted rows — these carry the ids the app applies against.
    public IReadOnlyList<GlunoProposalRecord> ProposalRecords { get; init; } = Array.Empty<GlunoProposalRecord>();
    public IReadOnlyList<GlunoPlaceCard> Places { get; init; } = Array.Empty<GlunoPlaceCard>();
    public IReadOnlyList<GlunoNavigationCard> Navigations { get; init; } = Array.Empty<GlunoNavigationCard>();

    /// <summary>
    /// A stable code from <see cref="GlunoFailureCodes"/> when something went
    /// wrong. The app localises it — a raw provider or SDK message never
    /// crosses this boundary.
    /// </summary>
    public string? FailureCode { get; init; }

    /// Whether offering "try again" is honest for this failure.
    public bool IsRetryable => GlunoFailureCodes.IsRetryable(FailureCode);
}

/// <summary>
/// What an assistant turn carries besides its text, stored in
/// GlunoMessage.PayloadJson.
///
/// An envelope rather than a bare array so a third kind of attachment can be
/// added later without another format migration. Rows written before this
/// envelope existed hold a bare proposals array; the controller reads both.
/// </summary>
public sealed class GlunoAssistantPayload
{
    public List<GlunoProposal> Proposals { get; set; } = new();
    public List<GlunoPlaceCard> Places { get; set; } = new();
    /// Screens the chat may OFFER to open. Never navigated automatically, and
    /// never a description of something that changed.
    public List<GlunoNavigationCard> Navigations { get; set; } = new();
    /// The compact "Sources" row. Place attribution rides on the place cards
    /// themselves; this covers routing, weather and the user's own plan.
    public List<GlunoSourceCard> Sources { get; set; } = new();
}

public interface IGlunoChatService
{
    /// <param name="screen">
    /// The stable screen id the user opened Gluno from (see
    /// SideQuestScreens), or null when the client did not say. Used only to
    /// make help shorter and more relevant — never to act on.
    /// </param>
    /// <param name="idempotencyKey">
    /// Client-generated, so a retry of the same send does not produce a second
    /// answer, a second charge or a second applicable proposal. Null from an
    /// older client, which simply proceeds unprotected.
    /// </param>
    Task<GlunoTurnResult> SendAsync(
        Guid userId, Guid? conversationId, Guid? tripId, string message, string? screen,
        string? idempotencyKey, CancellationToken ct);
}

/// <summary>
/// The orchestration layer — the only place the other Gluno layers meet.
///
/// One turn, in order: resolve and authorise the conversation, build the
/// SideQuest context for it, store what the user said, hand the model the
/// prompt + context + history + the actions this scope allows, let the AI
/// provider drive its tool loop while the action executor validates each call,
/// then store the answer and the proposals it produced.
///
/// Note where the scope comes from. Membership is checked when a trip-scoped
/// conversation is CREATED and re-checked on every turn through the context
/// builder and the action executor, both of which take the conversation's own
/// TripId. Nothing the model says can change it.
/// </summary>
public sealed class GlunoChatService : IGlunoChatService
{
    /// A single message. Long enough for a real question, short enough that a
    /// pasted document cannot become a prompt-injection surface.
    public const int MaxMessageLength = 2000;

    /// How many external place cards one answer may show. Enough to choose
    /// between, few enough to scroll past on a phone.
    public const int MaxPlaceCardsPerTurn = 5;

    /// A help answer offers one place to go, occasionally two. More than that
    /// is a menu, and the user came here to be told, not to choose.
    public const int MaxNavigationCardsPerTurn = 2;

    private readonly AppDbContext _db;
    private readonly GlunoAvailability _availability;
    private readonly IGlunoConversationService _conversations;
    private readonly IGlunoContextBuilder _contextBuilder;
    private readonly IGlunoActionExecutor _actions;
    private readonly IGlunoAiProvider _ai;
    private readonly IGlunoProposalStore _proposals;
    private readonly IGlunoWorkingStateStore _workingState;
    private readonly IRoutingService _routing;
    private readonly ILiveTravelRegistry _liveTravel;
    private readonly GlunoContextBudget _contextBudget;
    private readonly GlunoQualityGate _qualityGate;
    private readonly GlunoGroundingValidator _grounding;
    private readonly GlunoTurnPlanner _planner;
    private readonly GlunoUsageBudget _usage;
    private readonly IGlunoIdempotencyStore _idempotency;
    private readonly ILogger<GlunoChatService> _logger;

    /// <summary>
    /// This turn's latency tracker, so the outer boundary can report HOW FAR
    /// the turn got. Safe as a field because the service is scoped — one
    /// instance per request, never shared between turns.
    /// </summary>
    private GlunoLatencyTracker? _latency;

    public GlunoChatService(
        AppDbContext db,
        GlunoAvailability availability,
        IGlunoConversationService conversations,
        IGlunoContextBuilder contextBuilder,
        IGlunoActionExecutor actions,
        IGlunoAiProvider ai,
        IGlunoProposalStore proposals,
        IGlunoWorkingStateStore workingState,
        IRoutingService routing,
        ILiveTravelRegistry liveTravel,
        GlunoContextBudget contextBudget,
        GlunoQualityGate qualityGate,
        GlunoGroundingValidator grounding,
        GlunoTurnPlanner planner,
        GlunoUsageBudget usage,
        IGlunoIdempotencyStore idempotency,
        ILogger<GlunoChatService> logger)
    {
        _grounding = grounding;
        _planner = planner;
        _usage = usage;
        _idempotency = idempotency;
        _db = db;
        _availability = availability;
        _conversations = conversations;
        _contextBuilder = contextBuilder;
        _actions = actions;
        _ai = ai;
        _proposals = proposals;
        _workingState = workingState;
        _routing = routing;
        _liveTravel = liveTravel;
        _contextBudget = contextBudget;
        _qualityGate = qualityGate;
        _logger = logger;
    }

    /// <summary>
    /// The outermost boundary for a turn.
    ///
    /// WHY THIS EXISTS. The turn's own try/catch only ever covered the model
    /// call. Everything before it — loading the conversation, building the
    /// context, the history query, persisting the user's message — and
    /// everything after it — the quality gate, grounding, persisting the
    /// answer, telemetry — ran unprotected. An exception in any of those
    /// escaped to the host, and the app got a bare 5xx with NO BODY: no code,
    /// no retry flag, nothing to say to the user beyond a shrug.
    ///
    /// This guarantees the JSON contract for every outcome. It is a safety
    /// net, not a diagnosis — the log line still carries the exception type
    /// and the last stage reached, which is what actually identifies the bug.
    /// </summary>
    public async Task<GlunoTurnResult> SendAsync(
        Guid userId, Guid? conversationId, Guid? tripId, string message, string? screen,
        string? idempotencyKey, CancellationToken ct)
    {
        try
        {
            return await SendCoreAsync(userId, conversationId, tripId, message, screen, idempotencyKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user pressed stop. Handled first and separately so it can
            // never be reported as a failure.
            await ReleaseClaimAsync(userId, idempotencyKey, GlunoFailureCodes.Cancelled, ct);

            return new GlunoTurnResult
            {
                Error = GlunoTurnError.Cancelled,
                FailureCode = GlunoFailureCodes.Cancelled,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Type name only — an exception message can carry a connection
            // string, a request URI, or a row's contents.
            _logger.LogError(
                "[GLUNO] escaped type={Category} stage={Stage}",
                ex.GetType().Name, _latency?.LastStage ?? "before_planning");

            // Best effort, and deliberately not allowed to mask the original
            // failure: the claim would otherwise sit in-flight until its
            // five-minute timeout and block the user's own retry.
            await ReleaseClaimAsync(userId, idempotencyKey, GlunoFailureCodes.AiMalformedResponse, ct);

            // No proposal, no assistant message. The user's question stays in
            // the conversation exactly as the ordinary failure paths leave it.
            return new GlunoTurnResult
            {
                Error = GlunoTurnError.ProviderFailed,
                FailureCode = GlunoFailureCodes.AiMalformedResponse,
            };
        }
    }

    /// <summary>
    /// Marks an in-flight claim failed so the same message can be retried at
    /// once rather than after the in-flight timeout.
    ///
    /// Swallows everything. This runs while another failure is already being
    /// reported, and a cleanup error must not replace it.
    /// </summary>
    private async Task ReleaseClaimAsync(
        Guid userId, string? idempotencyKey, string failureCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;

        try
        {
            _db.ChangeTracker.Clear();

            var claim = await _db.GlunoTurnRequests.FirstOrDefaultAsync(
                row => row.UserId == userId
                    && row.IdempotencyKey == idempotencyKey
                    && row.Status == GlunoTurnRequestStatuses.InFlight,
                CancellationToken.None);

            if (claim == null) return;

            claim.Status = GlunoTurnRequestStatuses.Failed;
            claim.FailureCode = failureCode;
            claim.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GLUNO] claim release failed: {Category}", ex.GetType().Name);
        }
    }

    private async Task<GlunoTurnResult> SendCoreAsync(
        Guid userId, Guid? conversationId, Guid? tripId, string message, string? screen,
        string? idempotencyKey, CancellationToken ct)
    {
        // An unrecognised screen id is dropped rather than trusted — a client
        // sending something this backend has never heard of should degrade to
        // generic help, not to a guess.
        var currentScreen = SideQuestScreens.IsKnown(screen) ? screen : null;

        if (!_availability.IsAvailable)
            return new GlunoTurnResult { Error = GlunoTurnError.Unavailable };

        var text = message?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return new GlunoTurnResult { Error = GlunoTurnError.EmptyMessage };
        if (text.Length > MaxMessageLength)
            text = text[..MaxMessageLength];

        // ── Conversation + scope ──────────────────────────────────────────
        GlunoConversation conversation;
        if (conversationId.HasValue)
        {
            var existing = await _conversations.GetOwnedAsync(conversationId.Value, userId, ct);
            if (existing == null)
                return new GlunoTurnResult { Error = GlunoTurnError.ConversationNotFound };
            if (existing.ArchivedAt != null)
                return new GlunoTurnResult { Error = GlunoTurnError.ConversationArchived };
            conversation = existing;
        }
        else
        {
            // Scope is proven at creation time, once. From here on the
            // conversation's own TripId is the only scope that matters.
            if (tripId.HasValue)
            {
                var isMember = await _db.TripMembers
                    .AnyAsync(tm => tm.TripId == tripId.Value && tm.UserId == userId, ct);
                if (!isMember)
                    return new GlunoTurnResult { Error = GlunoTurnError.NotTripMember };
            }

            conversation = await _conversations.CreateAsync(userId, tripId, ct);
        }

        var telemetry = new GlunoTurnTelemetry { ConversationId = conversation.Id };

        // ── Usage ceiling ─────────────────────────────────────────────────
        //
        // Checked before ANY work, so a user over their limit costs nothing.
        // Note what this does NOT do: it does not close the conversation, hide
        // history, or affect the rest of SideQuest. Gluno stops answering and
        // says so neutrally; everything else keeps working.
        var verdict = _usage.CheckAllowed(userId);
        if (verdict != GlunoUsageVerdict.Allowed)
        {
            telemetry.UsageLimit = verdict.ToString();
            telemetry.FailureCategory = "usage_limit";
            telemetry.Write(_logger);

            return new GlunoTurnResult
            {
                Error = GlunoTurnError.UsageLimitReached,
                FailureCode = verdict == GlunoUsageVerdict.GlobalLimitReached
                    ? GlunoFailureCodes.GlobalUsageLimit
                    : GlunoFailureCodes.UserUsageLimit,
            };
        }

        // ── Idempotency ───────────────────────────────────────────────────
        //
        // A double tap or a retry after a dropped connection must not produce a
        // second answer — and above all not a second applicable proposal, which
        // would let the user save the same day plan twice.
        var claim = await _idempotency.ClaimAsync(idempotencyKey, userId, conversation.Id, ct);

        if (claim.Outcome == GlunoIdempotencyOutcome.AlreadyInFlight)
        {
            telemetry.IdempotencyReplay = "in_flight";
            telemetry.Write(_logger);
            return new GlunoTurnResult { Error = GlunoTurnError.DuplicateInFlight };
        }

        if (claim.Outcome == GlunoIdempotencyOutcome.AlreadyCompleted
            && claim.Existing?.AssistantMessageId is { } completedMessageId)
        {
            // The original answer, replayed. Cheaper than regenerating and —
            // more importantly — identical, so a flaky connection cannot make
            // Gluno say two different things about the same question.
            telemetry.IdempotencyReplay = "completed";
            telemetry.ModelSkipped = true;
            telemetry.Write(_logger);

            var replayed = await _db.GlunoMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(stored => stored.Id == completedMessageId, ct);

            if (replayed != null)
            {
                // The ORIGINAL proposals, not new ones. Regenerating them would
                // mint new ids and let the user apply the same day plan twice.
                var replayedProposals = await _db.GlunoProposals
                    .AsNoTracking()
                    .Where(proposal => proposal.MessageId == completedMessageId)
                    .OrderBy(proposal => proposal.CreatedAt)
                    .ToListAsync(ct);

                return new GlunoTurnResult
                {
                    Conversation = conversation,
                    AssistantMessage = replayed,
                    ProposalRecords = replayedProposals,
                };
            }
        }

        // ── Route before doing any work ───────────────────────────────────
        //
        // Classification comes first because it decides what the rest of the
        // turn is ALLOWED to do. Doing it after loading the context would mean
        // the expensive part has already happened by the time we learn it was
        // unnecessary.
        var workingState = await _workingState.LoadAsync(conversation.Id, ct);
        var routingInput = await BuildIntentInputAsync(text, conversation.TripId, workingState, ct);
        var intent = GlunoIntentRouter.Classify(routingInput);

        telemetry.Intent = intent.PrimaryIntent.ToString();
        telemetry.IntentConfidence = intent.Confidence;
        telemetry.Scope = intent.Scope.ToString();

        // ── Context, narrowed to what this intent needs ───────────────────
        //
        // canEdit is not known until the trip loads, so the workflow is
        // computed twice: once to decide what to LOAD, once with edit rights to
        // decide what to OFFER. Both are pure functions, so this costs nothing.
        var loadPlan = GlunoPlanningStrategy.For(intent, conversation.TripId.HasValue, canEdit: true);

        var context = await _contextBuilder.BuildAsync(
            userId, conversation.TripId, conversation.Id,
            new GlunoContextOptions
            {
                IncludeTrip = loadPlan.NeedsTripContext,
                IncludeWeather = loadPlan.NeedsWeather,
                IncludeAnalysis = loadPlan.NeedsTripAnalysis,
                IncludeDiscussedPlaces = true,
            },
            ct) with { CurrentScreen = currentScreen };

        var workflow = GlunoPlanningStrategy.For(intent, context.Trip != null, context.Trip?.CanEdit != false);

        // ── The turn plan ─────────────────────────────────────────────────
        //
        // Everything this turn may do, fixed before any of it happens: the
        // model tier, the tool allow-list, the budgets, the latency envelope.
        // A tool outside the plan is refused rather than argued with, so the
        // model cannot widen its own budget.
        var plan = _planner.Build(new GlunoTurnPlanRequest
        {
            Intent = intent,
            Workflow = workflow,
            ReferenceResolved = false,
            CanAnswerDeterministically = false,
        });

        var planProblems = plan.Validate();
        if (planProblems.Count > 0)
        {
            // Codes only, and the turn continues on the workflow's own limits.
            // A plan inconsistency is our bug, not the user's problem.
            _logger.LogWarning("[GLUNO] turn plan problems: {Problems}", string.Join(',', planProblems));
        }

        telemetry.PlanType = plan.PlanType;
        telemetry.ModelPolicy = $"{plan.Model.Tier}:{plan.Model.Reason}";

        var latency = new GlunoLatencyTracker(plan.Latency);
        _latency = latency;
        latency.Reached("turn_planned");
        var degradation = new GlunoDegradationTracker();

        // ── Resolve what the message pointed at ───────────────────────────
        //
        // "The second one" becomes a real id here, from memory this
        // conversation already holds — not from a fresh search that would cost
        // money and could come back in a different order.
        latency.Reached("context_built");
        var reference = GlunoReferenceResolver.Resolve(text, workingState, context.Trip, context.User.Language);

        // ── Evidence ──────────────────────────────────────────────────────
        //
        // Everything Gluno is allowed to state, enumerated BEFORE the model
        // runs. What is not in here cannot be asserted afterwards — see
        // GlunoGroundingValidator for why a prompt alone does not achieve this.
        latency.Reached("reference_resolved");
        var ledger = new GlunoEvidenceLedger();
        SeedLedgerFromContext(ledger, context);

        // ── What is happening out there right now ─────────────────────────
        //
        // Strikes, closures, holidays. Only when the answer genuinely changes
        // without it — the planner refuses most turns, because a live search
        // costs money, adds seconds, and pulls untrusted web text into the
        // prompt. Findings merge into the trip's own analysis so a closure
        // from outside SideQuest blocks a day plan exactly like one from
        // inside it.
        context = await AddLiveInformationAsync(
            context, ledger, text, intent, latency, degradation, telemetry, ct);

        latency.Reached("evidence_built");
        var contextJson = BuildContextJson(
            NarrowContext(context, intent, workflow),
            ledger,
            BuildTurnBrief(intent, workflow, reference, workingState, context),
            telemetry);

        latency.Reached("prompt_assembled");
        var history = await _conversations.GetHistoryTurnsAsync(
            conversation.Id, GlunoContextLimits.MaxHistoryTurns, ct);

        var userMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.User,
            Text = text,
        }, ct);
        latency.Reached("user_turn_persisted");

        // ── Model turn ────────────────────────────────────────────────────
        var scope = new GlunoActionScope
        {
            UserId = userId,
            TripId = conversation.TripId,
            ConversationId = conversation.Id,
            CurrentScreen = currentScreen,
            Language = context.User.Language,
        };
        var proposals = new List<GlunoProposal>();
        var places = new List<GlunoPlaceCard>();
        var navigations = new List<GlunoNavigationCard>();

        // ── Can this be answered without a model at all? ──────────────────
        //
        // Some questions have exactly one right answer and it is already in a
        // data structure. Sending those to a model costs money and a second of
        // the user's time to produce a sentence we could write ourselves.
        // Deliberately narrow — a canned reply where the user wanted help is a
        // far worse failure than an unnecessary model call.
        var direct = GlunoDirectAnswer.TryAnswer(new GlunoDirectRequest
        {
            Intent = intent.PrimaryIntent,
            Language = context.User.Language,
            WasRejection = intent.PrimaryIntent == GlunoIntent.ConfirmationOrRejection
                && LooksLikeRejection(text),
            PendingProposalSummary = workingState.Recent.Proposals
                .FirstOrDefault(proposal => proposal.Status == GlunoProposalStatuses.Pending)?.Summary,
        });

        if (direct != null)
        {
            telemetry.ModelSkipped = true;
            telemetry.DirectAnswerReason = direct.Reason;

            var directMessage = await _conversations.AppendAsync(new GlunoMessage
            {
                ConversationId = conversation.Id,
                Role = GlunoMessageRoles.Assistant,
                Text = direct.Text,
                PayloadJson = direct.Navigations.Count > 0
                    ? JsonSerializer.Serialize(
                        new GlunoAssistantPayload { Navigations = direct.Navigations.ToList() }, GlunoJson.Options)
                    : null,
            }, ct);

            if (claim.Existing != null)
            {
                await _idempotency.CompleteAsync(claim.Existing.Id, directMessage.Id, ct);
            }

            telemetry.RecordStages(latency);
            telemetry.Write(_logger);

            return new GlunoTurnResult
            {
                Conversation = conversation,
                UserMessage = userMessage,
                AssistantMessage = directMessage,
                Navigations = direct.Navigations,
            };
        }

        latency.Reached("model_request_started");
        GlunoAiResult result;
        try
        {
            result = await _ai.RunTurnAsync(
                new GlunoAiRequest
                {
                    SystemPrompt = GlunoSystemPrompt.Text,
                    ContextJson = contextJson,
                    History = history,
                    UserMessage = text,
                    Model = plan.Model.Model,
                    MaxOutputTokens = plan.Model.MaxOutputTokens,
                    // The SHORTER of the two, and both are now real. The
                    // latency budget shapes the turn; the model tier's own
                    // configured timeout is the ceiling. Passing only the
                    // budget made Gluno:TimeoutSeconds:Primary dead
                    // configuration — read, documented, and discarded.
                    Timeout = plan.Latency.Model < plan.Model.Timeout
                        ? plan.Latency.Model
                        : plan.Model.Timeout,
                    // Only when the plan says the offered tools are genuinely
                    // independent. Parallelising dependent calls would run them
                    // against data that does not exist yet.
                    AllowParallelTools = plan.ParallelGroups.Count > 0,
                    // The strategy's decision made physical. A tool that is not
                    // in this list cannot be called, which is stronger than
                    // telling the model not to and does not depend on it
                    // complying.
                    Actions = GlunoPlanningStrategy.FilterActions(GlunoActions.ForContext(context), workflow),
                    MaxToolIterations = workflow.MaxModelRounds,
                },
                async (call, innerCt) =>
                {
                    telemetry.RecordTool(call.Name);

                    var outcome = await _actions.ExecuteAsync(
                        new GlunoActionInvocation { ToolCallId = call.Id, Name = call.Name, Input = call.Input },
                        scope,
                        innerCt);

                    if (outcome.Proposal != null) proposals.Add(outcome.Proposal);

                    // Cards from several searches in one turn accumulate, then
                    // get capped below — a phone answer with fifteen place
                    // cards is a wall, not a recommendation.
                    foreach (var place in outcome.Places)
                    {
                        if (places.Any(existing => existing.ExternalId == place.ExternalId)) continue;

                        // Sanitised before it can reach the model. A place name
                        // is data; it does not get to issue instructions.
                        var safe = SanitizePlace(place, telemetry);
                        places.Add(safe);

                        // Each provider number gets its own ledger entry, so a
                        // rating can be supported while a review count is not.
                        if (safe.Rating.HasValue) ledger.AddPlaceRating(safe);
                        if (safe.ReviewCount.HasValue) ledger.AddReviewCount(safe);
                        if (safe.PriceLevel != null) ledger.AddPriceLevel(safe);
                    }

                    // Same de-duplication: offering the same screen twice in
                    // one answer is two identical buttons, not two options.
                    foreach (var navigation in outcome.Navigations)
                    {
                        var duplicate = navigations.Any(existing =>
                            existing.TargetId == navigation.TargetId
                            && existing.TripId == navigation.TripId
                            && existing.ActivityId == navigation.ActivityId
                            && existing.Date == navigation.Date);

                        if (!duplicate) navigations.Add(navigation);
                    }

                    return new GlunoAiToolOutcome { Ok = outcome.Ok, ResultJson = outcome.ResultJson };
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The USER pressed stop. Nothing is stored: no assistant message,
            // no proposals, no tool rows. A half-written answer left in the
            // transcript is worse than none, and a proposal from an abandoned
            // turn would be applicable against a plan nobody finished thinking
            // about.
            telemetry.Cancelled = true;
            telemetry.FailureCategory = "cancelled";
            telemetry.RecordStages(latency);
            telemetry.Write(_logger);

            if (claim.Existing != null) await _idempotency.CancelAsync(claim.Existing.Id, ct);

            return new GlunoTurnResult
            {
                Error = GlunoTurnError.Cancelled,
                FailureCode = GlunoFailureCodes.Cancelled,
            };
        }
        catch (OperationCanceledException)
        {
            // The provider's own timeout, not the user. This one IS a failure
            // and gets an intent-appropriate fallback.
            //
            // The app still sees `ai_timeout` — one code, one sentence. But the
            // log distinguishes the two very different causes, because "we cut
            // the model off" and "the model did not answer" need opposite
            // fixes and were indistinguishable before.
            var modelAllowance = plan.Latency.Model < plan.Model.Timeout
                ? plan.Latency.Model
                : plan.Model.Timeout;

            _logger.LogWarning(
                "[GLUNO] model timed out cause={Cause} allowanceMs={Allowance} elapsedMs={Elapsed} "
                + "budgetMs={Budget} policy={Policy} intent={Intent} stage={Stage}",
                plan.Latency.Model < plan.Model.Timeout ? "turn_budget_exhausted" : "provider_timeout",
                (long)modelAllowance.TotalMilliseconds,
                (long)latency.Elapsed.TotalMilliseconds,
                (long)plan.Latency.Total.TotalMilliseconds,
                $"{plan.Model.Tier}",
                intent.PrimaryIntent,
                latency.LastStage ?? "unknown");

            telemetry.FailureCategory = GlunoFailureCodes.AiTimeout;
            telemetry.RecordStages(latency);
            telemetry.Write(_logger);

            if (claim.Existing != null)
            {
                await _idempotency.FailAsync(claim.Existing.Id, GlunoFailureCodes.AiTimeout, ct);
            }

            return new GlunoTurnResult
            {
                Error = GlunoTurnError.ProviderFailed,
                FailureCode = GlunoFailureCodes.AiTimeout,
            };
        }
        catch (Exception ex)
        {
            // A CATEGORY, never the message. An SDK exception can carry the
            // request URI, and the request URI carries the API key.
            var code = GlunoFailureCodes.FromException(ex);
            _logger.LogWarning("[GLUNO] provider turn failed: {Category}", ex.GetType().Name);

            telemetry.FailureCategory = code;
            telemetry.RecordStages(latency);
            telemetry.Write(_logger);

            if (claim.Existing != null) await _idempotency.FailAsync(claim.Existing.Id, code, ct);

            return new GlunoTurnResult { Error = GlunoTurnError.ProviderFailed, FailureCode = code };
        }

        telemetry.ModelRounds = result.ExecutedCalls.Count + 1;
        telemetry.InputTokens = result.InputTokens;
        telemetry.OutputTokens = result.OutputTokens;
        telemetry.ProposalCreated = proposals.Count > 0;
        if (result.Refused) telemetry.FailureCategory = "refused";
        if (result.HitIterationLimit) telemetry.FailureCategory = "iteration_limit";

        // ── Persist what happened ─────────────────────────────────────────
        // Tool rows first, so the assistant turn that references them is never
        // the earlier row. They are stored for replay and audit; the app does
        // not render them (GlunoMessageRoles.IsRenderable).
        foreach (var executed in result.ExecutedCalls)
        {
            await _conversations.AppendAsync(new GlunoMessage
            {
                ConversationId = conversation.Id,
                Role = GlunoMessageRoles.Tool,
                Text = string.Empty,
                ToolName = executed.Call.Name,
                ToolCallId = executed.Call.Id,
                PayloadJson = executed.Outcome.ResultJson,
            }, ct);
        }

        var assistantText = ResolveAssistantText(result, proposals, context.User.Language);

        var visiblePlaces = places.Take(MaxPlaceCardsPerTurn).ToList();
        var visibleNavigations = navigations.Take(MaxNavigationCardsPerTurn).ToList();
        var sourceCards = BuildSourceCards(ledger, context.User.Language, DateTime.UtcNow);

        // ── Grounding ─────────────────────────────────────────────────────
        //
        // Runs BEFORE the quality gate, because there is no point checking
        // whether a plan is sensible if the numbers in the answer were made up.
        // Route legs and opening hours only exist once a proposal has been
        // built, so the ledger is topped up here rather than earlier.
        SeedLedgerFromProposals(ledger, proposals);

        var groundingAttempts = 0;
        var grounding = RunGrounding(assistantText, ledger, context, intent);
        RecordGrounding(telemetry, grounding, ledger);

        // ONE regeneration, and only when the substance was unsupported. A
        // stray price in an otherwise sound answer is cheaper to delete than to
        // re-run the model for.
        if (grounding.MustRegenerate && workflow.MaxModelRounds > 2)
        {
            groundingAttempts++;
            telemetry.RegenerationCount = groundingAttempts;

            var retry = await TryRegenerateAsync(
                request: new GlunoAiRequest
                {
                    SystemPrompt = GlunoSystemPrompt.Text,
                    ContextJson = contextJson,
                    History = history,
                    UserMessage = text + "\n\n" + RegenerationInstruction(grounding, context.User.Language),
                    Actions = Array.Empty<GlunoActionDefinition>(),
                    MaxToolIterations = 1,
                },
                ct);

            if (!string.IsNullOrWhiteSpace(retry))
            {
                var second = RunGrounding(retry, ledger, context, intent);

                if (second.Passed || !second.MustRegenerate)
                {
                    assistantText = second.SafeCorrections ?? retry;
                    grounding = second;
                    RecordGrounding(telemetry, second, ledger);
                }
                else
                {
                    // Twice unsupported. Stop spending rounds and say something
                    // honest instead — a third attempt tends to produce the
                    // same invention in different words.
                    assistantText = GlunoFallbacks.Text(GlunoFallbackReason.GroundingFailed, context.User.Language);
                    telemetry.FinalFallbackUsed = "grounding_failed";
                    proposals.Clear();
                }
            }
            else
            {
                assistantText = grounding.FallbackResponse
                    ?? GlunoFallbacks.Text(GlunoFallbackReason.GroundingFailed, context.User.Language);
                telemetry.FinalFallbackUsed = "grounding_failed";
                proposals.Clear();
            }
        }
        else if (!grounding.Passed && grounding.SafeCorrections is { } repaired)
        {
            // Deterministic repair: unsupported numbers removed, an unverified
            // travel time demoted to the distance we actually measured. Nothing
            // is substituted, only withdrawn.
            assistantText = repaired;
        }

        // A source that was missing gets one honest clause, not a whole
        // apologetic paragraph.
        assistantText = WithFreshnessNote(assistantText, ledger, workflow, context.User.Language);

        // ── Quality gate ──────────────────────────────────────────────────
        //
        // The last deterministic check before anything reaches the user. A
        // blocker means the answer or the proposal contains something the user
        // could act on and be wrong about — a claim that something was saved, a
        // travel time nothing verified, a stop that clashes with a booking.
        //
        // Blocked proposals are DROPPED, not shown. There is no state in which
        // an unsafe proposal is rendered with a warning next to it: a card with
        // an apply button on it is an invitation, and the user should not have
        // to read a caveat to know not to accept.
        if (workflow.RunsQualityGate)
        {
            var quality = RunQualityGate(assistantText, proposals, context, intent, workflow);

            telemetry.QualityGate = quality.Passed ? "passed" : "blocked";
            telemetry.QualityBlockers = quality.Blockers.Count;
            telemetry.QualityWarnings = quality.Warnings.Count;

            if (!quality.Passed)
            {
                var repaired = ApplyCorrections(proposals, quality);
                proposals.Clear();
                proposals.AddRange(repaired);

                assistantText = WithGateNote(assistantText, quality, context.User.Language);
                telemetry.ProposalCreated = proposals.Count > 0;
            }
            else if (quality.UserFacingNote != null)
            {
                assistantText = WithGateNote(assistantText, quality, context.User.Language);
            }
        }

        // ── Internal review ───────────────────────────────────────────────
        //
        // Deterministic, and only for turns the strategy marked as expensive to
        // get wrong. Its findings shape the working state and the telemetry;
        // they never block, because the gate above is what blocks.
        if (workflow.RunsInternalReview)
        {
            telemetry.ReviewRan = true;

            var review = GlunoResponseReview.Review(new GlunoReviewInput
            {
                AnswerText = assistantText,
                ExpectsProposal = intent.ExpectsProposal,
                ProducedProposal = proposals.Count > 0,
                HasTripContext = context.Trip != null,
                UsedAnyTool = result.ExecutedCalls.Count > 0,
                TargetWordCount = workflow.TargetWordCount,
                PreferencesAlreadyKnown = context.Preferences.Select(preference => preference.Key).ToList(),
            });

            if (!review.Acceptable)
            {
                // Logged as codes, never as text. A revision round is not
                // spent here: the findings are cheap to act on next turn, and
                // a second full model round on every day plan would double the
                // cost of the most expensive turn in the product for a
                // wording improvement.
                _logger.LogInformation(
                    "[GLUNO] review findings: {Findings}",
                    string.Join(',', review.Findings.Select(finding => finding.Code)));
            }
        }

        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = assistantText,
            // Places live in the message payload; PROPOSALS do not. A proposal
            // needs an identity that can be claimed exactly once and a status
            // two devices agree on, so it becomes its own row below.
            PayloadJson = visiblePlaces.Count > 0 || visibleNavigations.Count > 0 || sourceCards.Count > 0
                ? JsonSerializer.Serialize(
                    new GlunoAssistantPayload
                    {
                        Places = visiblePlaces,
                        Navigations = visibleNavigations,
                        Sources = sourceCards,
                    },
                    GlunoJson.Options)
                : null,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
        }, ct);

        // After the message exists, so each proposal can point at the turn it
        // belongs to. The snapshot of the Adventure is taken here too — that
        // is what a later apply compares against to detect that someone else
        // changed the plan in between.
        var records = new List<GlunoProposalRecord>(proposals.Count);
        foreach (var proposal in proposals)
        {
            records.Add(await _proposals.CreateAsync(conversation, assistantMessage.Id, proposal, ct));
        }

        // ── Working memory ────────────────────────────────────────────────
        //
        // What this turn put on the table, so the next one can point at it
        // without another search. Written last, so a failed turn leaves the
        // previous state intact rather than half-updated.
        await UpdateWorkingStateAsync(
            conversation.Id, workingState, intent, reference, context, visiblePlaces, records, text, ct);

        // ── Usage and cost ────────────────────────────────────────────────
        //
        // Counters only, and a coarse cost BUCKET rather than the figure. What
        // a turn costs correlates with how elaborate somebody's trip is, which
        // is closer to personal information than it looks.
        var usage = new GlunoTurnUsage
        {
            InputTokens = result.InputTokens ?? 0,
            OutputTokens = result.OutputTokens ?? 0,
            ModelRounds = telemetry.ModelRounds,
            ProviderCalls = telemetry.ProviderCalls,
            Regenerations = telemetry.RegenerationCount,
        };

        _usage.Record(userId, usage);
        telemetry.CostBucket = _usage.CostBucket(usage);
        telemetry.DegradationLevel = degradation.Level.ToString();
        telemetry.RecordStages(latency);

        if (claim.Existing != null)
        {
            await _idempotency.CompleteAsync(claim.Existing.Id, assistantMessage.Id, ct);
        }

        telemetry.Write(_logger);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            Proposals = proposals,
            ProposalRecords = records,
            Places = visiblePlaces,
            Navigations = visibleNavigations,
        };
    }

    /// <summary>
    /// The cheap facts the router needs, without loading a whole trip context.
    ///
    /// Two small queries against dates and counts. Doing this before the
    /// context is what lets an app-help question skip the context entirely.
    /// </summary>
    private async Task<GlunoIntentInput> BuildIntentInputAsync(
        string message, Guid? tripId, GlunoWorkingState state, CancellationToken ct)
    {
        if (tripId is not { } id)
        {
            return new GlunoIntentInput
            {
                Message = message,
                HasTrip = false,
                HasRecentContext = state.HasReferents(),
            };
        }

        var dates = await _db.Trips
            .AsNoTracking()
            .Where(trip => trip.Id == id)
            .Select(trip => new { trip.StartDate, trip.EndDate })
            .FirstOrDefaultAsync(ct);

        var countsByDate = await _db.TripActivities
            .AsNoTracking()
            .Where(activity => activity.TripId == id)
            .GroupBy(activity => activity.Date)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        return new GlunoIntentInput
        {
            Message = message,
            HasTrip = true,
            TripStartDate = dates?.StartDate,
            TripEndDate = dates?.EndDate,
            Today = DateOnly.FromDateTime(DateTime.UtcNow),
            ActivityCountByDate = countsByDate.ToDictionary(
                entry => entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry => entry.Count,
                StringComparer.Ordinal),
            HasRecentContext = state.HasReferents(),
        };
    }

    /// <summary>
    /// The turn brief that rides alongside the context.
    ///
    /// This is how the routing decision reaches the model. Without it the model
    /// re-derives "the second one" from the transcript every turn — slower,
    /// non-deterministic, and occasionally the wrong restaurant. With it, the
    /// id is simply stated.
    ///
    /// Kept deliberately small. It is a brief, not a second context.
    /// </summary>
    private static object BuildTurnBrief(
        GlunoIntentResult intent,
        GlunoWorkflow workflow,
        GlunoReferenceResolution reference,
        GlunoWorkingState state,
        GlunoContext context)
        => new
        {
            intent = intent.PrimaryIntent.ToString(),
            confidence = intent.Confidence,
            scope = intent.Scope.ToString(),
            referencedDate = reference.Date ?? intent.ReferencedDate,
            // The rules the answer has to obey, stated rather than implied.
            mayProposeChanges = workflow.AllowsProposals,
            maySearchPlaces = workflow.AllowsExternalSearch,
            targetWordCount = workflow.TargetWordCount,
            resolvedReference = reference.Subject == null ? null : new
            {
                kind = reference.Subject.Kind.ToString(),
                id = reference.Subject.Id,
                label = reference.Subject.Label,
            },
            anchor = reference.Anchor == null ? null : new
            {
                kind = reference.Anchor.Kind.ToString(),
                id = reference.Anchor.Id,
                label = reference.Anchor.Label,
                relation = reference.Relation.ToString(),
            },
            // An ambiguous reference is handed over as a QUESTION to ask, not
            // as a list to pick from. Picking would be guessing.
            referenceAmbiguous = reference.IsAmbiguous,
            askInstead = reference.Question,
            referentNoLongerExists = reference.ReferentGone,
            // Working memory, so a follow-up does not re-search and a rejected
            // option is not offered again.
            goal = state.Goal,
            rejectedOptions = state.RejectedOptions
                .Take(5)
                .Select(option => new { option.Kind, option.Id, option.Label, option.Reason })
                .ToList(),
            openQuestions = state.OpenQuestions.Take(3).ToList(),
            placesAlreadyShown = state.Recent.Places
                .Take(GlunoRecentMentions.MaxPlaces)
                .Select(place => new { place.ExternalId, place.Name, place.Position })
                .ToList(),
            pendingProposals = state.Recent.Proposals
                .Where(proposal => proposal.Status == "pending")
                .Select(proposal => new { proposal.Id, proposal.Summary })
                .ToList(),
            conflicts = DetectConflicts(context, intent),
        };

    /// <summary>
    /// Wishes that cannot both be satisfied, worked out before the model
    /// answers.
    ///
    /// Detecting these deterministically means Gluno cannot fail to notice.
    /// Agreeing is the path of least resistance for a language model, and a
    /// relaxed day with eight stops in it reads perfectly well right up until
    /// somebody tries to walk it.
    /// </summary>
    private static IReadOnlyList<object> DetectConflicts(GlunoContext context, GlunoIntentResult intent)
    {
        if (context.Trip == null) return Array.Empty<object>();

        var preferences = context.Preferences.ToDictionary(
            preference => preference.Key, preference => preference.Value, StringComparer.Ordinal);

        var transport = TransportPreferences.From(
            preferences.GetValueOrDefault(Models.GlunoPreferenceKeys.Transport),
            preferences.GetValueOrDefault(Models.GlunoPreferenceKeys.WalkingDistance),
            preferences.GetValueOrDefault(Models.GlunoPreferenceKeys.Accessibility));

        var pace = TripPaces.Parse(preferences.GetValueOrDefault(Models.GlunoPreferenceKeys.Pace));
        var budget = preferences.GetValueOrDefault(Models.GlunoPreferenceKeys.Budget);

        var day = DateOnly.TryParseExact(
            intent.ReferencedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDay)
                ? parsedDay
                : (DateOnly?)null;

        var dayActivities = day == null
            ? Array.Empty<GlunoActivityContext>()
            : context.Trip.Activities.Where(activity => activity.Date == day.Value).ToArray();

        var longestLeg = LongestHopKm(dayActivities);

        var conflicts = GlunoConflictDetector.Detect(new GlunoConflictInput
        {
            Pace = pace,
            RequestedStopCount = dayActivities.Count(activity =>
                ActivityRoles.FromCategory(activity.Category, null) is "activity" or "meal"),
            BudgetIsLow = budget != null && LooksLikeLowBudget(budget),
            Transport = transport,
            LongestLegKm = longestLeg,
            WeatherCondition = day == null
                ? null
                : context.Trip.Weather.FirstOrDefault(entry => entry.Date == day.Value)?.Condition,
            PrecipitationProbability = day == null
                ? null
                : context.Trip.Weather.FirstOrDefault(entry => entry.Date == day.Value)?.PrecipitationProbability,
            OutdoorStopCount = dayActivities.Count(activity =>
                ActivityRoles.FromCategory(activity.Category, null) == "activity"),
            Language = context.User.Language,
        });

        return conflicts
            .Select(conflict => (object)new { conflict.Code, conflict.Explanation, conflict.Alternatives })
            .ToList();
    }

    /// The budget preference is the user's own words, not a number. These are
    /// the phrasings that actually mean "keep it cheap".
    private static bool LooksLikeLowBudget(string value)
    {
        var text = GlunoIntentRouter.Normalise(value);
        return text.Contains("billig", StringComparison.Ordinal)
            || text.Contains("lag", StringComparison.Ordinal)
            || text.Contains("snal", StringComparison.Ordinal)
            || text.Contains("spara", StringComparison.Ordinal)
            || text.Contains("budget", StringComparison.Ordinal)
            || text.Contains("cheap", StringComparison.Ordinal)
            || text.Contains("low", StringComparison.Ordinal)
            || text.Contains("tight", StringComparison.Ordinal);
    }

    private static double? LongestHopKm(IReadOnlyList<GlunoActivityContext> activities)
    {
        double? longest = null;

        for (var index = 1; index < activities.Count; index++)
        {
            var distance = GeoDistance.KilometresBetween(
                activities[index - 1].Latitude, activities[index - 1].Longitude,
                activities[index].Latitude, activities[index].Longitude);

            if (distance is { } value && (longest == null || value > longest)) longest = value;
        }

        return longest;
    }

    /// <summary>
    /// Looks up current conditions, but only when the answer changes without
    /// them.
    ///
    /// Three things happen here, in this order and for a reason. The PLAN is
    /// built from the intent and the dates before anything runs, so the model
    /// cannot ask for a search, widen the window, or name a place the user did
    /// not. The FACTS go into the evidence ledger, so a live claim is
    /// attributable and appears in the sources row like any other. The
    /// FINDINGS merge into the trip's own analysis, so a closure discovered
    /// outside SideQuest blocks a day plan through exactly the same quality
    /// gate as one discovered inside it.
    ///
    /// Every failure path returns the context unchanged. Live information
    /// makes an answer better; SideQuest's own planning is what makes it work.
    /// </summary>
    private async Task<GlunoContext> AddLiveInformationAsync(
        GlunoContext context,
        GlunoEvidenceLedger ledger,
        string message,
        GlunoIntentResult intent,
        GlunoLatencyTracker latency,
        GlunoDegradationTracker degradation,
        GlunoTurnTelemetry telemetry,
        CancellationToken ct)
    {
        var plan = GlunoLiveSearchPlanner.Plan(new GlunoLiveSearchRequest
        {
            Message = message,
            Intent = intent.PrimaryIntent,
            Destination = context.Trip?.Destination,
            WindowStart = ParseDate(intent.ReferencedDate) ?? context.Trip?.StartDate,
            WindowEnd = context.Trip?.EffectiveEndDate,
            ProviderAvailable = _liveTravel.IsAvailable,
        });

        if (!plan.ShouldSearch) return context;

        // A live search is the single slowest optional thing a turn can do.
        // With the budget already spent, skipping it beats an answer that
        // arrives after the user has put the phone down.
        if (!latency.HasRoomFor(TimeSpan.FromSeconds(8)))
        {
            degradation.RecordFailure(GlunoEvidenceSources.LiveTravelInformation);
            return context;
        }

        LiveTravelResult result;
        try
        {
            using var stage = latency.Stage("live_info");
            result = await _liveTravel.SearchAsync(
                plan,
                plan.From ?? DateOnly.FromDateTime(DateTime.UtcNow),
                plan.To,
                context.User.Language,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Category only. A provider exception can carry a query or a URL.
            _logger.LogWarning("[GLUNO] live lookup failed: {Category}", ex.GetType().Name);
            degradation.RecordFailure(GlunoEvidenceSources.LiveTravelInformation);
            return context;
        }

        if (result.ProviderFailed) degradation.RecordFailure(GlunoEvidenceSources.LiveTravelInformation);

        foreach (var fact in result.Facts)
        {
            ledger.AddLiveTravelFact(fact);
        }

        telemetry.LiveSearches = result.SearchesUsed;
        telemetry.LiveFacts = result.Facts.Count;
        telemetry.LiveConflicts = result.Conflicts.Count;

        if (context.Trip == null || result.Facts.Count == 0) return context;

        // Not added to the ledger: the FACTS behind them already are, attributed
        // to the live provider. A finding is SideQuest's reading of those facts
        // against this Adventure, and entering it separately would both
        // double-count the evidence and re-attribute it to ourselves.
        var findings = GlunoLiveFindings.Build(context.Trip, result.Facts, context.User.Language);
        if (findings.Count == 0) return context;

        // Live findings first: a strike on the travel day outranks "this day
        // is a little full".
        return context with
        {
            Trip = context.Trip with
            {
                Findings = findings.Concat(context.Trip.Findings).ToList(),
            },
        };
    }

    /// <summary>
    /// Sends the model the part of the Adventure the question is about.
    ///
    /// The day narrowing is deliberately narrow itself: only when the user
    /// named a date AND the turn is actually scheduling one. "What does my
    /// week look like" mentions no date and keeps the whole trip; an
    /// incidental date in a general question does not trigger it either,
    /// because the workflow for those turns does not run the schedule engine.
    ///
    /// Findings are always ranked and capped. Twenty analysis notes is not
    /// twenty times as useful as six — it is six useful ones plus noise the
    /// model has to read past.
    /// </summary>
    private static GlunoContext NarrowContext(
        GlunoContext context, GlunoIntentResult intent, GlunoWorkflow workflow)
    {
        if (context.Trip == null) return context;

        var trip = workflow.UsesScheduleEngine && ParseDate(intent.ReferencedDate) is { } focus
            ? GlunoContextBudget.NarrowToDate(context.Trip, focus)
            : context.Trip;

        return context with
        {
            Trip = trip with
            {
                Findings = GlunoContextBudget.RelevantFindings(trip.Findings, intent.ReferencedDate),
            },
        };
    }

    /// A date the intent router already validated, or null. Never throws.
    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// Serialises the context, trimmed to the configured token budget.
    ///
    /// The budget is chosen for latency and answer quality rather than as a
    /// technical ceiling — a bloated context is slower, dilutes attention away
    /// from the actual question, and is mostly untrusted external text. What
    /// gets dropped is decided by priority, and critical sections are never
    /// dropped: an answer built on a trimmed-away hard constraint is worse
    /// than a slow one.
    /// </summary>
    private string BuildContextJson(
        GlunoContext context, GlunoEvidenceLedger ledger, object turnBrief, GlunoTurnTelemetry telemetry)
    {
        var fitted = _contextBudget.Fit(
        [
            new GlunoContextSection(
                GlunoContextPriority.CurrentRequest, "turn",
                JsonSerializer.Serialize(turnBrief, GlunoJson.Options))
            { IsCritical = true },
            new GlunoContextSection(
                GlunoContextPriority.RelevantTrip, "context",
                JsonSerializer.Serialize(context, GlunoJson.Options))
            { IsCritical = true },
            // Droppable, and last on purpose. Losing evidence costs Gluno the
            // right to state some things — which is a smaller failure than
            // losing the Adventure it is talking about.
            new GlunoContextSection(
                GlunoContextPriority.Evidence, "evidence",
                JsonSerializer.Serialize(ledger.ForPrompt(DateTime.UtcNow), GlunoJson.Options)),
        ]);

        telemetry.RecordContextTokens(fitted.TokensByCategory);

        if (fitted.ExceedsBudgetEvenAfterTrimming)
        {
            // Codes only. This is our sizing problem, not something the user
            // did wrong, and the turn continues on the oversized context
            // rather than failing.
            _logger.LogWarning(
                "[GLUNO] context over budget tokens={Tokens} dropped={Dropped}",
                fitted.TotalTokens, fitted.DroppedSections.Count);
        }

        return fitted.Json;
    }

    /// <summary>
    /// Everything the Adventure itself entitles Gluno to say.
    ///
    /// Built before the model runs, so the ledger is a description of what is
    /// KNOWN rather than a record of what was claimed.
    /// </summary>
    private static void SeedLedgerFromContext(GlunoEvidenceLedger ledger, GlunoContext context)
    {
        foreach (var preference in context.Preferences)
        {
            ledger.AddPreference(preference.Key, preference.Value);
        }

        if (context.Group is { } group)
        {
            // Shared only. A private preference is in the list above, entitles
            // Gluno to plan around it, and does NOT entitle it to tell four
            // other people about it — the two enter the ledger separately for
            // exactly that reason.
            foreach (var constraint in group.Constraints)
            {
                ledger.AddSharedConstraint(constraint);
            }

            foreach (var decision in group.Decisions)
            {
                ledger.AddGroupDecision(decision);
            }

            foreach (var conflict in group.Conflicts)
            {
                ledger.AddGroupConflict(conflict);
            }
        }

        if (context.Trip is not { } trip) return;

        foreach (var activity in trip.Activities.Take(GlunoEvidenceLedger.MaxEntries / 3))
        {
            ledger.AddActivity(activity, trip.Id);
        }

        foreach (var weather in trip.Weather)
        {
            ledger.AddForecast(weather, trip.Id);
        }

        foreach (var finding in trip.Findings.Take(8))
        {
            ledger.AddFinding(finding, trip.Id);
        }
    }

    /// <summary>
    /// Route legs and opening hours, which only exist once a day plan has been
    /// laid out.
    ///
    /// Read back out of the proposal payload rather than plumbed through from
    /// the planner: the payload is what the user will actually see and apply,
    /// so grounding the answer against IT — not against an intermediate object
    /// that might differ — is the check that matters.
    /// </summary>
    private static void SeedLedgerFromProposals(
        GlunoEvidenceLedger ledger, IReadOnlyList<GlunoProposal> proposals)
    {
        foreach (var proposal in proposals.Where(item => item.Kind == "day_plan"))
        {
            if (!proposal.Payload.TryGetProperty("activities", out var activities)
                || activities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in activities.EnumerateArray())
            {
                if (row.TryGetProperty("travelFromPrevious", out var travel)
                    && travel.ValueKind == JsonValueKind.Object)
                {
                    var verified = travel.TryGetProperty("verified", out var flag)
                        && flag.ValueKind == JsonValueKind.True;

                    ledger.Add(new GlunoEvidence
                    {
                        Id = "pending",
                        Type = "route_leg",
                        Source = verified
                            ? GlunoEvidenceSources.RoutingProvider
                            : GlunoEvidenceSources.SideQuestAnalysis,
                        SourceReference = ReadText(row, "title"),
                        ClaimCategory = verified
                            ? GlunoClaimTypes.VerifiedRouteTime
                            : GlunoClaimTypes.StraightLineDistance,
                        Value = verified
                            ? ReadNumberText(travel, "minutes")
                            : ReadNumberText(travel, "distanceKm"),
                        Unit = verified ? "min" : "km",
                        ValidUntilUtc = verified
                            ? GlunoFreshness.Until(GlunoFreshness.ForMode(
                                TravelModes.Parse(ReadText(travel, "mode"))))
                            : null,
                        Provider = verified ? ReadText(travel, "source") : null,
                        IsVerified = verified,
                        AllowedClaimTypes = verified
                            ? [GlunoClaimTypes.VerifiedRouteTime, GlunoClaimTypes.StraightLineDistance]
                            : [GlunoClaimTypes.StraightLineDistance],
                    });
                }

                if (row.TryGetProperty("openingHours", out var hours)
                    && hours.ValueKind == JsonValueKind.Object
                    && ReadText(hours, "source") is { } hoursSource)
                {
                    ledger.Add(new GlunoEvidence
                    {
                        Id = "pending",
                        Type = "opening_hours",
                        Source = GlunoEvidenceSources.OpeningHoursProvider,
                        SourceReference = ReadText(row, "title"),
                        ClaimCategory = GlunoClaimTypes.VerifiedOpeningHours,
                        Value = $"{ReadText(hours, "opensAt")}-{ReadText(hours, "closesAt")}",
                        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.OpeningHours),
                        Provider = hoursSource,
                        IsVerified = true,
                        AllowedClaimTypes = [GlunoClaimTypes.VerifiedOpeningHours],
                    });
                }
            }
        }
    }

    private GlunoGroundingResult RunGrounding(
        string answerText, GlunoEvidenceLedger ledger, GlunoContext context, GlunoIntentResult intent)
        => _grounding.Validate(new GlunoGroundingInput
        {
            AnswerText = answerText,
            Ledger = ledger,
            NowUtc = DateTime.UtcNow,
            Language = context.User.Language,
            ReferencedDate = intent.ReferencedDate,
            KnownActivityIds = context.Trip?.Activities.Select(activity => activity.Id).ToHashSet()
                ?? new HashSet<Guid>(),
        });

    /// <summary>
    /// Counters only. Never a claim's text, never an evidence value — the whole
    /// point of the ledger is that it holds the user's private travel data.
    /// </summary>
    private static void RecordGrounding(
        GlunoTurnTelemetry telemetry, GlunoGroundingResult grounding, GlunoEvidenceLedger ledger)
    {
        telemetry.UnsupportedClaims = grounding.UnsupportedClaims.Count;
        telemetry.SafeCorrectionsApplied = grounding.SafeCorrections != null ? 1 : 0;
        telemetry.StaleEvidenceUsed = grounding.StaleClaims.Count;
        telemetry.AttributionErrors = grounding.AttributionErrors.Count;
        telemetry.GroundingFailureCategory = grounding.Passed
            ? null
            : grounding.UnsupportedClaims.FirstOrDefault()?.Reason ?? "contradiction";

        telemetry.RecordEvidence(ledger);
    }

    /// <summary>
    /// The instruction for the one retry.
    ///
    /// Names the CATEGORY that failed, never the offending sentence — quoting
    /// the invented number back at the model is a reliable way to get it
    /// repeated. Tools are withheld on the retry: the problem was overreach,
    /// not missing data.
    /// </summary>
    private static string RegenerationInstruction(GlunoGroundingResult grounding, string language)
    {
        var categories = grounding.UnsupportedClaims
            .Select(claim => claim.ClaimType)
            .Distinct()
            .Take(3)
            .ToList();

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var what = categories.Count > 0 ? string.Join(", ", categories) : "facts";

        return swedish
            ? $"[SIDEQUEST] Ditt förra svar innehöll påståenden utan stöd i evidence ({what}). "
              + "Svara igen och använd ENDAST siffror och uppgifter som finns i evidence-listan. "
              + "Säg kort vad du inte kan verifiera och hjälp med resten."
            : $"[SIDEQUEST] Your previous answer contained claims with no evidence behind them ({what}). "
              + "Answer again using ONLY figures and details present in the evidence list. "
              + "Say briefly what you cannot verify and help with the rest.";
    }

    private async Task<string?> TryRegenerateAsync(GlunoAiRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _ai.RunTurnAsync(
                request,
                (_, _) => Task.FromResult(new GlunoAiToolOutcome { Ok = false, ResultJson = "{}" }),
                ct);

            return string.IsNullOrWhiteSpace(result.Text) ? null : result.Text.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GLUNO] regeneration failed: {Category}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// One clause when a source was missing, appended at most once.
    ///
    /// The rule is that a missing provider costs the answer a sentence, not its
    /// usefulness. Only fires for the workflows that would actually have used
    /// the source — a trip review does not need to apologise for not calling a
    /// routing API it was never going to call.
    /// </summary>
    private string WithFreshnessNote(
        string answerText, GlunoEvidenceLedger ledger, GlunoWorkflow workflow, string language)
    {
        var now = DateTime.UtcNow;

        GlunoFallbackReason? reason = null;

        if (workflow.AllowsRouting
            && !_routing.HasVerifiedRouting
            && ledger.Entries.Any(entry => entry.Type == "route_leg"))
        {
            reason = GlunoFallbackReason.RoutingUnavailable;
        }
        else if (ledger.Stale(now).Any(entry => entry.Type == "opening_hours"))
        {
            reason = GlunoFallbackReason.OpeningHoursUnavailable;
        }

        if (reason == null) return answerText;

        var note = GlunoFallbacks.Note(reason.Value, language);
        if (answerText.Contains(note, StringComparison.OrdinalIgnoreCase)) return answerText;

        return $"{answerText.TrimEnd()}\n\n{note}";
    }

    /// <summary>
    /// Strips control characters and caps length on everything a provider
    /// returned, before it can reach the model or the app.
    /// </summary>
    private static GlunoPlaceCard SanitizePlace(GlunoPlaceCard place, GlunoTurnTelemetry telemetry)
    {
        var name = GlunoTextSanitizer.CleanPlaceName(place.Name);
        var address = GlunoTextSanitizer.Clean(place.Address, GlunoTextSanitizer.MaxAddress);
        var review = GlunoTextSanitizer.CleanReviewSummary(place.ReviewSummary);

        if (name.LooksLikeInjection || address.LooksLikeInjection || review.LooksLikeInjection)
        {
            // Counted, never blocked. A restaurant does not deserve to vanish
            // from the results because its description contains the word
            // "system" — and the tool allow-list is code, so nothing in this
            // text could widen what the turn may do anyway.
            telemetry.RecordInjectionSignal(
                name.Signal ?? address.Signal ?? review.Signal ?? "unknown");
        }

        return new GlunoPlaceCard
        {
            Provider = place.Provider,
            ExternalId = place.ExternalId,
            Name = name.Value.Length > 0 ? name.Value : place.ExternalId,
            Category = place.Category,
            CategoryLabel = place.CategoryLabel,
            Address = address.Value.Length > 0 ? address.Value : null,
            Latitude = place.Latitude,
            Longitude = place.Longitude,
            Rating = place.Rating,
            RatingScaleMax = place.RatingScaleMax,
            ReviewCount = place.ReviewCount,
            PriceLevel = place.PriceLevel,
            ImageUrl = place.ImageUrl,
            ProviderUrl = place.ProviderUrl,
            SourceAttribution = place.SourceAttribution,
            DistanceKm = place.DistanceKm,
            OpeningHours = place.OpeningHours,
            // A suspicious review summary is dropped rather than forwarded.
            // The card loses a nice-to-have; nothing else changes.
            ReviewSummary = review.LooksLikeInjection || review.Value.Length == 0 ? null : review.Value,
            Signals = place.Signals,
        };
    }

    /// <summary>
    /// Turns the ledger into the two or three chips a person would want to tap.
    ///
    /// Collapsed by source, not by entry: fourteen route legs are one "Route
    /// data" chip, not fourteen. The chat is a conversation, not a bibliography
    /// — and a wall of citations reads as defensiveness rather than rigour.
    ///
    /// Places are deliberately excluded: their attribution belongs on the card
    /// the user is actually looking at, next to the rating it supports.
    /// </summary>
    private static List<GlunoSourceCard> BuildSourceCards(
        GlunoEvidenceLedger ledger, string language, DateTime nowUtc)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);
        var cards = new List<GlunoSourceCard>();

        var routes = ledger.Entries.Where(entry => entry is { Type: "route_leg", IsVerified: true }).ToList();
        if (routes.Count > 0)
        {
            cards.Add(new GlunoSourceCard
            {
                Kind = "route",
                Label = swedish ? "Ruttdata" : "Route data",
                Supports = swedish ? "Restider mellan stoppen" : "Travel times between stops",
                VerifiedAtUtc = routes.Max(entry => entry.VerifiedAtUtc),
                IsStale = routes.All(entry => !entry.IsFresh(nowUtc)),
            });
        }

        // Weather chips name the DAY and the PLACE. A bare "Weather" label is
        // useless — a forecast is only evidence for the day and town it is for,
        // and hiding which one is how the wrong day's rain ends up in a plan.
        foreach (var forecast in ledger.Entries.Where(entry => entry.Type == "day_forecast").Take(2))
        {
            var parts = (forecast.SourceReference ?? string.Empty).Split('|');
            var date = parts.Length > 0 ? parts[0] : "";
            var place = parts.Length > 1 && parts[1] != "-" ? parts[1] : null;

            cards.Add(new GlunoSourceCard
            {
                Kind = "weather",
                Label = place != null ? $"{date} · {place}" : date,
                Supports = swedish ? "Väderprognos" : "Weather forecast",
                VerifiedAtUtc = forecast.VerifiedAtUtc,
                IsStale = !forecast.IsFresh(nowUtc),
            });
        }

        var hours = ledger.Entries.Where(entry => entry.Type == "opening_hours").ToList();
        if (hours.Count > 0)
        {
            cards.Add(new GlunoSourceCard
            {
                Kind = "hours",
                Label = swedish ? "Öppettider" : "Opening hours",
                Supports = swedish ? "När platserna har öppet" : "When the places are open",
                VerifiedAtUtc = hours.Max(entry => entry.VerifiedAtUtc),
                IsStale = hours.All(entry => !entry.IsFresh(nowUtc)),
                Provider = hours[0].Provider,
            });
        }

        // The user's own plan gets a quiet label rather than a badge — it is
        // their data, and treating it like a third-party citation would be odd.
        if (ledger.Entries.Any(entry => entry.Source == GlunoEvidenceSources.SideQuestDatabase))
        {
            cards.Add(new GlunoSourceCard
            {
                Kind = "plan",
                Label = swedish ? "Från din plan" : "From your plan",
                Supports = swedish ? "Ditt Adventure i SideQuest" : "Your Adventure in SideQuest",
            });
        }

        return cards;
    }

    private static string? ReadText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadNumberText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble().ToString("0.#", CultureInfo.InvariantCulture)
            : null;
    }

    private GlunoQualityResult RunQualityGate(
        string answerText,
        IReadOnlyList<GlunoProposal> proposals,
        GlunoContext context,
        GlunoIntentResult intent,
        GlunoWorkflow workflow)
    {
        var dayPlan = proposals.FirstOrDefault(proposal => proposal.Kind == "day_plan")?.Payload;

        var pace = TripPaces.Parse(context.Preferences
            .FirstOrDefault(preference => preference.Key == Models.GlunoPreferenceKeys.Pace)?.Value);

        return _qualityGate.Check(new GlunoQualityInput
        {
            AnswerText = answerText,
            DayPlan = dayPlan,
            Findings = context.Trip?.Findings ?? Array.Empty<TripFinding>(),
            ProducedProposal = proposals.Count > 0,
            ExpectsProposal = workflow.AllowsProposals && intent.ExpectsProposal,
            SomethingWasApplied = false,
            HasVerifiedTravelTimes = _routing.HasVerifiedRouting,
            HasVerifiedOpeningHours = context.DiscussedPlaces.Count > 0,
            Pace = pace,
            Language = context.User.Language,
            ExistingTitles = context.Trip?.Activities.Select(activity => activity.Title).ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            SuggestedTitles = proposals
                .Select(proposal => proposal.Summary)
                .ToList(),
        });
    }

    /// <summary>
    /// Replaces blocked proposals with their corrected version, or drops them.
    ///
    /// A proposal the gate blocked never reaches the app as something with an
    /// apply button on it. Where the gate produced a safe repair — an optional
    /// stop removed — that repaired version goes out instead, and the note
    /// tells the user what changed.
    /// </summary>
    /// <summary>
    /// Blockers that are about the ANSWER, not the plan.
    ///
    /// A sentence claiming something was already saved is a wording failure.
    /// The proposal beside it may be perfectly good, and dropping it would
    /// leave the user with a caveat and nothing to accept — strictly worse than
    /// what they asked for.
    /// </summary>
    private static readonly HashSet<string> TextOnlyBlockers = new(StringComparer.Ordinal)
    {
        "claims_already_saved",
        "fabricated_travel_time",
        "fabricated_opening_hours",
    };

    private static List<GlunoProposal> ApplyCorrections(
        IReadOnlyList<GlunoProposal> proposals, GlunoQualityResult quality)
    {
        // Nothing wrong with the plan itself — keep it, and let the note fix
        // the wording.
        if (quality.Blockers.All(blocker => TextOnlyBlockers.Contains(blocker.Code)))
        {
            return proposals.ToList();
        }

        if (quality.CorrectedPlan is not { } corrected)
        {
            // The plan is broken and nothing is safe to repair. Every proposal
            // is dropped — the answer still explains the problem, it simply
            // cannot be accepted with one tap.
            return [];
        }

        return proposals
            .Select(proposal => proposal.Kind == "day_plan"
                ? new GlunoProposal
                {
                    ActionName = proposal.ActionName,
                    Kind = proposal.Kind,
                    TripId = proposal.TripId,
                    Summary = proposal.Summary,
                    Payload = corrected,
                }
                : proposal)
            .ToList();
    }

    /// The gate's note, appended once. Never replaces the answer — the model's
    /// own words are what the user came for.
    private static string WithGateNote(string answerText, GlunoQualityResult quality, string language)
    {
        if (quality.UserFacingNote is not { } note) return answerText;
        if (answerText.Contains(note, StringComparison.OrdinalIgnoreCase)) return answerText;

        return string.IsNullOrWhiteSpace(answerText) ? note : $"{answerText.TrimEnd()}\n\n{note}";
    }

    /// <summary>
    /// Records what this turn put on the table.
    ///
    /// Updated only on turns that DECIDED something. A conversation of small
    /// talk would otherwise churn the state and push out the references that
    /// still matter.
    /// </summary>
    private async Task UpdateWorkingStateAsync(
        Guid conversationId,
        GlunoWorkingState state,
        GlunoIntentResult intent,
        GlunoReferenceResolution reference,
        GlunoContext context,
        IReadOnlyList<GlunoPlaceCard> places,
        IReadOnlyList<GlunoProposalRecord> records,
        string userMessage,
        CancellationToken ct)
    {
        var significant = places.Count > 0
            || records.Count > 0
            || intent.PrimaryIntent is GlunoIntent.PreferenceUpdate or GlunoIntent.ForgetPreference
                or GlunoIntent.ConfirmationOrRejection;

        // A rejection is always significant: it is the one thing that must not
        // be forgotten, because forgetting it means offering the same
        // restaurant again two turns later.
        var rejected = intent.PrimaryIntent == GlunoIntent.ConfirmationOrRejection
            && LooksLikeRejection(userMessage);

        if (rejected && reference.Subject is { } subject)
        {
            state.RejectedOptions.RemoveAll(option => option.Id == subject.Id);
            state.RejectedOptions.Insert(0, new RejectedOption(
                subject.Kind.ToString(), subject.Id, subject.Label, null));

            if (state.RejectedOptions.Count > 8) state.RejectedOptions.RemoveRange(8, state.RejectedOptions.Count - 8);
        }

        if (!significant) return;

        // Switching topic clears the referents. Otherwise "the second one"
        // three questions later resolves against a list nobody remembers being
        // shown, which is worse than not resolving at all.
        if (intent.PrimaryIntent is GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest
            or GlunoIntent.GeneralTravelQuestion)
        {
            state.Recent.Places.Clear();
            state.Recent.Proposals.Clear();
        }

        GlunoReferenceResolver.Remember(
            state,
            places,
            records.Select(record => new GlunoProposalRecordSummary(
                record.Id, record.ActionType, record.Summary, record.Status)).ToList(),
            context.Trip?.Activities ?? Array.Empty<GlunoActivityContext>(),
            reference.Date ?? intent.ReferencedDate);

        state.DecidedPreferences = context.Preferences
            // Verbatim. "We don't want to hire a car" compressed to
            // "transport: car" is worse than no summary at all.
            .Select(preference => new GlunoStatePreference(preference.Key, preference.Value))
            .ToList();

        state.PendingProposalIds = records
            .Where(record => record.Status == GlunoProposalStatuses.Pending)
            .Select(record => record.Id)
            .ToList();

        state.Goal = DescribeGoal(intent);

        await _workingState.SaveAsync(conversationId, state, ct);
    }

    /// Short, structural, and derived from the intent rather than from the
    /// user's words — so nothing they typed ends up in a stored summary.
    private static string DescribeGoal(GlunoIntentResult intent) => intent.PrimaryIntent switch
    {
        GlunoIntent.PlanEmptyDay => $"planning {intent.ReferencedDate ?? "a day"}",
        GlunoIntent.ImproveExistingDay => $"improving {intent.ReferencedDate ?? "a day"}",
        GlunoIntent.BuildFullItinerary => "building the full itinerary",
        GlunoIntent.PlaceRecommendation => "choosing where to go",
        GlunoIntent.TripReview => "reviewing the Adventure",
        GlunoIntent.MoveActivity => "moving an Activity",
        GlunoIntent.AddActivity => "adding an Activity",
        GlunoIntent.ChangeAdventureDates => "changing the Adventure dates",
        _ => "general planning",
    };

    private static bool LooksLikeRejection(string message)
    {
        var text = GlunoIntentRouter.Normalise(message);
        return text.StartsWith("nej", StringComparison.Ordinal)
            || text.StartsWith("no", StringComparison.Ordinal)
            || text.Contains("inte den", StringComparison.Ordinal)
            || text.Contains("not that", StringComparison.Ordinal)
            || text.Contains("skippa", StringComparison.Ordinal)
            || text.Contains("skip ", StringComparison.Ordinal);
    }

    /// <summary>
    /// What the user reads when the model produced no usable text.
    ///
    /// A refusal, an exhausted tool loop or an empty reply must still leave a
    /// coherent bubble in the chat — an empty assistant turn reads as the app
    /// being broken. Written in the user's app language, matching the rule the
    /// system prompt gives the model.
    /// </summary>
    private static string ResolveAssistantText(
        GlunoAiResult result, IReadOnlyList<GlunoProposal> proposals, string language)
    {
        if (!string.IsNullOrWhiteSpace(result.Text)) return result.Text.Trim();

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        if (result.Refused)
            return swedish
                ? "Det där kan jag tyvärr inte hjälpa till med. Fråga mig något annat om resan så gör jag mitt bästa."
                : "I can't help with that one, sorry. Ask me something else about the trip and I'll do my best.";

        if (proposals.Count > 0)
            return proposals[0].Summary;

        if (result.HitIterationLimit)
            return swedish
                ? "Jag fastnade på den där. Kan du beskriva vad du vill ha lite mer specifikt?"
                : "I got stuck on that one. Could you tell me a bit more specifically what you're after?";

        return swedish
            ? "Jag har inget bra svar på det just nu. Vill du prova att fråga på ett annat sätt?"
            : "I don't have a good answer for that right now. Want to try asking it a different way?";
    }
}
