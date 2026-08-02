using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
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

    /// A question with tappable answers, when the turn could not proceed
    /// without a choice. See GlunoClarificationService.
    public GlunoClarification? Clarification { get; init; }

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
    Task<GlunoTurnResult> ContinueFromClarificationAsync(
        Guid userId, GlunoClarification clarification, GlunoClarificationOption option,
        string? idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Answers a proposal-conflict card.
    ///
    /// SEPARATE FROM THE ORDINARY CONTINUATION ON PURPOSE. That one replays the
    /// original question through the model; this one applies a deterministic
    /// fix to the stored draft and revalidates it. Routing a conflict answer
    /// through the ordinary path would spend a model round re-deriving a plan
    /// that already exists — and could return a different one, so the user
    /// would have answered about one suggestion and been given another.
    ///
    /// No idempotency key: the clarification's own resolve claim is the
    /// idempotency boundary, and a repeat tap replays the recorded answer.
    /// </summary>
    Task<GlunoTurnResult> ContinueFromDraftAsync(
        Guid userId, GlunoClarification clarification, GlunoClarificationOption option,
        CancellationToken ct);

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
    private readonly IGlunoClarificationService _clarifications;
    private readonly IGlunoProposalDraftService _drafts;
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
        IGlunoClarificationService clarifications,
        IGlunoProposalDraftService drafts,
        ILogger<GlunoChatService> logger)
    {
        _grounding = grounding;
        _planner = planner;
        _usage = usage;
        _idempotency = idempotency;
        _clarifications = clarifications;
        _drafts = drafts;
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

    /// <param name="scopeTripId">
    /// An Adventure chosen for THIS TURN only — either resolved deterministically
    /// or picked from a clarification. It scopes the context; it does not change
    /// the conversation's own scope, so a global conversation stays global.
    /// Membership is verified before it is ever passed.
    /// </param>
    private async Task<GlunoTurnResult> SendCoreAsync(
        Guid userId, Guid? conversationId, Guid? tripId, string message, string? screen,
        string? idempotencyKey, CancellationToken ct, Guid? scopeTripId = null,
        (string Type, string Value)? answered = null)
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

            // The caller opened Gluno from an Adventure but handed us a
            // conversation belonging to a different scope — usually a global
            // one left in the client's cache.
            //
            // Continuing silently is the worst option available: the app shows
            // an Adventure scope pill, and the backend answers with no trip
            // context at all. Gluno then knows nothing about the trip the user
            // is looking at, and there is no signal anywhere that says why.
            if (tripId.HasValue && existing.TripId != tripId)
            {
                _logger.LogInformation(
                    "[GLUNO] conversation scope mismatch: requested trip-scoped, conversation is {Scope}",
                    existing.TripId.HasValue ? "another trip" : "global");

                return new GlunoTurnResult { Error = GlunoTurnError.ConversationNotFound };
            }

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
        var loadPlan = GlunoPlanningStrategy.For(
            intent, (scopeTripId ?? conversation.TripId).HasValue, canEdit: true);

        var context = await _contextBuilder.BuildAsync(
            userId, scopeTripId ?? conversation.TripId, conversation.Id,
            new GlunoContextOptions
            {
                IncludeTrip = loadPlan.NeedsTripContext,
                IncludeWeather = loadPlan.NeedsWeather,
                IncludeAnalysis = loadPlan.NeedsTripAnalysis,
                IncludeDiscussedPlaces = true,
            },
            ct) with { CurrentScreen = currentScreen };

        // ── Does this question need an Adventure we do not have? ──────────
        //
        // Checked HERE, before the turn plan, before any provider and before
        // the model. A question that cannot be answered without knowing which
        // trip it is about must not spend a model round guessing, and must not
        // start a search scoped to nothing.
        //
        // Deterministic first: one Adventure, or one the question names, is
        // resolved silently. Asking when the answer is already knowable is the
        // fastest way to make this feature annoying.
        if (context.Trip == null && scopeTripId == null && NeedsAnAdventure(intent))
        {
            var choices = TripChoicesFrom(context);
            var single = GlunoClarificationBuilder.ResolveSingle(choices, text, context.Today);

            if (single != null)
            {
                // Answer it with that Adventure's context, without touching
                // the conversation's own scope — a global conversation stays
                // global.
                return await SendCoreAsync(
                    userId, conversationId, tripId, message, screen, idempotencyKey, ct,
                    scopeTripId: single.Id, answered: answered);
            }

            if (choices.Count > 1)
            {
                return await AskWhichAdventureAsync(
                    conversation, userId, text, intent, choices, context, ct);
            }
        }

        var workflow = GlunoPlanningStrategy.For(intent, context.Trip != null, context.Trip?.CanEdit != false);

        // ── Is a decisive choice missing? ─────────────────────────────────
        //
        // One detector, run once, BEFORE the turn plan, the providers, the
        // model and anything that writes. Scattering these checks through the
        // turn would let one run before a provider call and another after, and
        // nobody could say from reading the code what a turn will do.
        //
        // It resolves silently wherever the data settles it — one Friday, a
        // named museum, a saved pace — and only asks when the choice genuinely
        // changes the answer.
        var detection = GlunoClarificationDetector.Detect(new GlunoDetectionInput
        {
            Message = text,
            Intent = intent,
            Context = context,
            Workflow = workflow,
            Today = context.Today,
            Language = context.User.Language,
        });

        // A question already answered is never asked again — see the
        // continuation path for why this matters.
        if (detection.Outcome == GlunoDetectionOutcome.NeedsClarification
            && detection.Type != answered?.Type)
        {
            telemetry.FailureCategory = null;

            return await AskClarificationAsync(
                conversation, userId, text, intent, detection, context, ct);
        }

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
                // ── The second clarification point ────────────────────────
                //
                // The gate blocked. Before silently repairing the suggestion
                // away, check whether the blocker is something the USER can
                // choose about — a clash with a booking, a day outside the
                // trip, a duplicate.
                //
                // Stripping those quietly is the behaviour this replaces: the
                // user asked for something, Gluno decided alone that it could
                // not be done, and the answer arrived with a caveat instead of
                // a plan. Asking costs one tap and keeps the decision theirs.
                //
                // Nothing here writes to the Adventure. The draft is a
                // conversation about a change, not the change.
                var conflictPlan = proposals.FirstOrDefault(item => item.Kind == "day_plan")?.Payload;

                var conflicts = GlunoConflictMapper.From(
                    quality,
                    conflictVersion: 1,
                    // The plan itself answers what each clash collides WITH —
                    // a booking, a check-in, another suggestion — which is what
                    // decides whether the existing item may be touched at all.
                    dayPlan: conflictPlan,
                    destinationMismatches: DestinationMismatches(conflictPlan, context));

                var conflict = GlunoConflictMapper.MostBlocking(conflicts);

                if (conflict != null && proposals.Count > 0 && conflict.AllowedStrategies.Count > 1)
                {
                    return await AskAboutConflictAsync(
                        conversation, userId, text, intent, proposals[0], conflict, context, ct);
                }

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
        var records = await CreateProposalsAsync(conversation, assistantMessage.Id, proposals, ct);

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
    /// Carries on with the question the clarification was asking about.
    ///
    /// The user does not resend anything and the original question is NOT
    /// stored a second time — the clarification remembers which message it was
    /// about, and this replays that text with the choice applied. What the
    /// chat shows for the choice is a small selection row, not a fresh user
    /// message repeating a question already on screen.
    /// </summary>
    public async Task<GlunoTurnResult> ContinueFromClarificationAsync(
        Guid userId,
        GlunoClarification clarification,
        GlunoClarificationOption option,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var original = await _conversations.GetMessageAsync(clarification.OriginalUserMessageId, userId, ct);
        if (original == null)
            return new GlunoTurnResult { Error = GlunoTurnError.ConversationNotFound };

        // Only the Adventure choice changes what the turn can see. Every other
        // type answers a question inside a scope that is already settled.
        Guid? scopeTripId = option.EntityType == GlunoClarificationEntityTypes.Trip
            ? option.EntityId
            : null;

        var result = await SendCoreAsync(
            userId,
            clarification.ConversationId,
            tripId: null,
            message: original.Text,
            screen: null,
            // A NEW claim: this is a new turn, bound to the clarification
            // rather than to the original send, so it cannot collide with the
            // send that asked the question.
            idempotencyKey: idempotencyKey ?? $"clar-{clarification.Id:N}",
            ct,
            scopeTripId: scopeTripId,
            // WITHOUT THIS THE TURN ASKS AGAIN. The continuation re-runs the
            // same message through the same detector, which would find the
            // same gap, ask the same question, and loop forever. The answer
            // has to travel with it.
            answered: (clarification.Type, option.Value));

        // Remembered so a repeat tap replays this answer instead of running
        // the whole turn a second time.
        if (result.Error == GlunoTurnError.None && result.AssistantMessage != null)
        {
            await _clarifications.RecordContinuationAsync(
                clarification.Id, result.AssistantMessage.Id, ct);
        }

        return result;
    }

    /// <summary>
    /// Whether this question is unanswerable without knowing which Adventure.
    ///
    /// Deliberately narrow. "What should I pack for Japan" needs no trip;
    /// "what have we got on Friday" is meaningless without one. Getting this
    /// wrong in the generous direction produces a chooser in front of every
    /// general question, which is worse than the occasional clarifying
    /// sentence.
    /// </summary>
    private static bool NeedsAnAdventure(GlunoIntentResult intent) => intent.PrimaryIntent
        is GlunoIntent.TripReview
        or GlunoIntent.PlanEmptyDay
        or GlunoIntent.ImproveExistingDay
        or GlunoIntent.BuildFullItinerary
        or GlunoIntent.MoveActivity
        or GlunoIntent.AddActivity
        or GlunoIntent.ChangeAdventureDates
        // A trip-scoped question the router recognised as being about the
        // user's own plan rather than about travel in general.
        || intent.Scope == GlunoIntentScope.Trip;

    /// The user's Adventures, as the ranker and the option builder see them.
    private static IReadOnlyList<TripChoice> TripChoicesFrom(GlunoContext context)
        => context.Trips
            .Select(trip => new TripChoice(trip.Id, trip.Title, trip.StartDate, trip.EndDate))
            .ToList();

    /// <summary>
    /// Asks which Adventure, and stops.
    ///
    /// No model round, no providers, no proposal. The whole turn is a question
    /// built from rows the user is already a member of — which is what makes
    /// it fast enough to be worth doing rather than guessing.
    /// </summary>
    /// <summary>
    /// Asks the question the detector produced, and stops.
    ///
    /// No model round, no providers, no proposal, no write. The whole turn is
    /// a question built from data the user already has access to — which is
    /// what makes asking cheap enough to be better than guessing.
    /// </summary>
    private async Task<GlunoTurnResult> AskClarificationAsync(
        GlunoConversation conversation,
        Guid userId,
        string text,
        GlunoIntentResult intent,
        GlunoDetection detection,
        GlunoContext context,
        CancellationToken ct)
    {
        var userMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.User,
            Text = text,
        }, ct);

        var question = GlunoClarificationBuilder.QuestionFor(detection.Type!, context.User.Language);

        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = question,
        }, ct);

        var clarification = await _clarifications.CreateAsync(
            new GlunoClarification
            {
                ConversationId = conversation.Id,
                UserId = userId,
                TripId = context.Trip?.Id,
                OriginalUserMessageId = userMessage.Id,
                MessageId = assistantMessage.Id,
                Type = detection.Type!,
                Question = question,
                OriginalIntent = intent.PrimaryIntent.ToString(),
                AllowFreeText = detection.AllowFreeText,
            },
            detection.Options,
            ct);

        _logger.LogInformation(
            "[GLUNO] clarification needed type={Type} reason={Reason} options={Count}",
            detection.Type, detection.Reason, detection.Options.Count);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            Clarification = clarification,
        };
    }

    /// <summary>
    /// Stores the suggestion as a draft and asks how to resolve the clash.
    ///
    /// The proposal is NOT created. A proposal is something with an Apply
    /// button on it, and putting one in front of somebody while it still
    /// conflicts with their plan is an invitation to write a broken day. The
    /// draft holds it until the conflict is answered.
    /// </summary>
    private async Task<GlunoTurnResult> AskAboutConflictAsync(
        GlunoConversation conversation,
        Guid userId,
        string text,
        GlunoIntentResult intent,
        GlunoProposal proposal,
        GlunoProposalConflict conflict,
        GlunoContext context,
        CancellationToken ct)
    {
        var userMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.User,
            Text = text,
        }, ct);

        // One sentence naming what does not fit. The card shows the affected
        // day and items separately, so this stays short by contract.
        var explanation = GlunoConflictStrategies.Explain(
            conflict.ConflictType, context.User.Language);

        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = explanation,
        }, ct);

        var draft = await _drafts.CreateAsync(new GlunoProposalDraft
        {
            ConversationId = conversation.Id,
            UserId = userId,
            TripId = context.Trip!.Id,
            OriginalUserMessageId = userMessage.Id,
            OriginalIntent = intent.PrimaryIntent.ToString(),
            ActionType = proposal.ActionName,
            PayloadJson = JsonSerializer.Serialize(proposal.Payload, GlunoJson.Options),
            Status = GlunoProposalDraftStatuses.AwaitingClarification,
            ConflictVersion = conflict.ConflictVersion,
        }, ct);

        var clarification = await _clarifications.CreateAsync(
            new GlunoClarification
            {
                ConversationId = conversation.Id,
                UserId = userId,
                TripId = context.Trip.Id,
                OriginalUserMessageId = userMessage.Id,
                MessageId = assistantMessage.Id,
                Type = GlunoClarificationTypes.ProposalConflict,
                Question = explanation,
                OriginalIntent = intent.PrimaryIntent.ToString(),
                // The strategies are the answers. A free-text escape here
                // would invite the user to describe a fix the validator has
                // no way to check.
                AllowFreeText = false,
                // ── What the tap will be checked against ──────────────────
                //
                // All four written by the server, none of them ever sent by
                // the client. The versions are the whole staleness mechanism:
                // a tap that arrives after the draft moved is answering about
                // a plan that no longer exists, and honouring it would fix
                // the wrong thing.
                DraftId = draft.Id,
                DraftVersion = draft.DraftVersion,
                ConflictVersion = draft.ConflictVersion,
                ConflictType = conflict.ConflictType,
                ConflictMetaJson = ConflictMetaJson(conflict, proposal.Payload, context),
            },
            GlunoConflictMapper.Options(conflict, context.User.Language),
            ct);

        _logger.LogInformation(
            "[GLUNO] proposal conflict conflict={Conflict} strategies={Count} draftVersion={DraftVersion}",
            conflict.ConflictType, conflict.AllowedStrategies.Count, draft.DraftVersion);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            Clarification = clarification,
        };
    }

    /// <summary>
    /// The ONLY place a chat turn turns suggestions into proposals.
    ///
    /// Both the ordinary path and the conflict continuation come through here,
    /// so there is exactly one call to the store — a second creation path would
    /// be a way to produce something with an Apply button on it that never went
    /// past the draft flow.
    /// </summary>
    private async Task<List<GlunoProposalRecord>> CreateProposalsAsync(
        GlunoConversation conversation,
        Guid messageId,
        IReadOnlyList<GlunoProposal> proposals,
        CancellationToken ct,
        Guid? draftId = null,
        int? draftVersion = null)
    {
        var records = new List<GlunoProposalRecord>(proposals.Count);

        foreach (var proposal in proposals)
        {
            records.Add(await _proposals.CreateAsync(
                conversation, messageId, proposal, ct, draftId, draftVersion));
        }

        return records;
    }

    // ── The proposal-conflict continuation ────────────────────────────────

    /// <summary>
    /// Answers a conflict card: applies the chosen fix to the DRAFT, revalidates
    /// against the Adventure as it stands now, and either asks the next question
    /// or produces the proposal.
    ///
    /// WHY THIS IS SEPARATE FROM THE ORDINARY CONTINUATION. That one replays the
    /// original question through the whole pipeline — router, providers, model.
    /// Doing that here would be wrong twice over. It would spend a model round
    /// to re-derive a plan that already exists in the draft, and the model would
    /// be free to produce a DIFFERENT plan, so the user would have answered a
    /// question about one suggestion and received another.
    ///
    /// NO MODEL RUNS ON THIS PATH AT ALL. Every supported strategy is a
    /// deterministic edit to a JSON document, followed by the same quality gate
    /// the original turn ran. That makes the outcome of a tap predictable, which
    /// is what a tappable answer has to be.
    ///
    /// AND NOTHING HERE WRITES TO THE ADVENTURE. The draft changes; the plan
    /// does not, until a proposal is approved.
    /// </summary>
    public async Task<GlunoTurnResult> ContinueFromDraftAsync(
        Guid userId,
        GlunoClarification clarification,
        GlunoClarificationOption option,
        CancellationToken ct)
    {
        // Everything the tap is checked against comes off the clarification
        // row, which only the server has ever written. The client sent an id
        // and an option key.
        if (clarification.DraftId is not { } draftId
            || clarification.DraftVersion is not { } draftVersion
            || clarification.ConflictVersion is not { } conflictVersion
            || clarification.ConflictType is not { } conflictType)
        {
            return new GlunoTurnResult { Error = GlunoTurnError.ConversationNotFound };
        }

        var conversation = await _conversations.GetOwnedAsync(clarification.ConversationId, userId, ct);
        if (conversation == null)
            return new GlunoTurnResult { Error = GlunoTurnError.ConversationNotFound };

        // Read once, up front. The refusal paths below run before any context
        // is built, and a message in the wrong language is a worse failure than
        // the one it is reporting.
        var language = await _db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.Language)
            .FirstOrDefaultAsync(ct) ?? "en";

        // ── What is being answered ────────────────────────────────────────
        //
        // Three card types come through here and they carry different values.
        // A conflict card's option IS the strategy. A day card's option is a
        // date and a time card's option is a time — for those, the STRATEGY is
        // implied by the card type, because that is what produced it.
        //
        // Reading the strategy from the card rather than from the tap is what
        // stops a date arriving where a strategy was expected.
        var strategy = clarification.Type switch
        {
            GlunoClarificationTypes.Day => GlunoConflictStrategies.ChooseAnotherDay,
            GlunoClarificationTypes.ActivityTime => GlunoConflictStrategies.ChooseAnotherTime,
            _ => option.Value,
        };

        // ── Every check, before any work ──────────────────────────────────
        //
        // Ownership, usability, TTL, status, both versions, the rebuild limit
        // and the repeat guard. Ordered so the cheap refusals happen first and
        // nothing is spent on a tap that was never going to be honoured.
        var validated = await _drafts.ValidateResolveAsync(
            draftId, userId, draftVersion, conflictVersion, conflictType, strategy, ct);

        if (validated.Error != GlunoDraftError.None || validated.Draft is not { } draft)
            return await ConflictStoppedAsync(conversation, clarification, validated.Error, language, ct);

        // Membership NOW, not when the card was built. An hour is long enough
        // to leave a group, and a stale button must not be an access path.
        if (!await _db.TripMembers.AnyAsync(
            member => member.TripId == draft.TripId && member.UserId == userId, ct))
        {
            return await ConflictStoppedAsync(
                conversation, clarification, GlunoDraftError.Forbidden, language, ct);
        }

        // ── Backing out ───────────────────────────────────────────────────
        //
        // No revalidation, no gate, no proposal. The user said no; the fastest
        // correct thing is to stop.
        if (strategy == GlunoConflictStrategies.Cancel)
        {
            await _drafts.SetStatusAsync(
                draftId, userId, GlunoProposalDraftStatuses.Cancelled, null, ct);

            return await ConflictClosedAsync(
                conversation, clarification,
                string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
                    ? "Okej, jag lämnar planen som den är."
                    : "Fine — I've left the plan as it was.",
                ct);
        }

        // ── Revalidate against the Adventure as it stands now ─────────────
        //
        // Re-read BEFORE the fix is applied, not after. Between the question
        // and the answer somebody else may have moved the very booking this
        // clash was about, and the days and times offered next have to describe
        // the plan as it is, not as it was an hour ago.
        var context = await _contextBuilder.BuildAsync(
            userId, draft.TripId, conversation.Id,
            new GlunoContextOptions { IncludeTrip = true, IncludeDiscussedPlaces = true },
            ct);

        if (context.Trip == null)
        {
            return await ConflictStoppedAsync(
                conversation, clarification, GlunoDraftError.NotUsable, language, ct);
        }

        // ── Apply the chosen fix to the draft ─────────────────────────────
        var outcome = await ApplyStrategyAsync(
            userId, draft, clarification, conflictType, strategy, option, context, ct);

        // The strategy produced a QUESTION rather than a change — "which day?",
        // "which time?". Nothing has moved yet, so no version moves and no
        // rebuild is spent: the user has not chosen anything yet.
        if (outcome.NextCard is { } nextCard)
        {
            return await AskSubQuestionAsync(
                conversation, userId, clarification, draft, conflictType, nextCard, ct);
        }

        if (outcome.PayloadJson == null && !outcome.AcceptedInPlace)
        {
            // Nothing on offer could fix it. A controlled stop, never a silent
            // no-op that leaves the draft claiming to be fixed.
            await _drafts.SetStatusAsync(
                draftId, userId, GlunoProposalDraftStatuses.Cancelled, null, ct);

            return await ConflictClosedAsync(
                conversation, clarification,
                outcome.Message ?? (string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
                    ? "Då blir det inget kvar att föreslå. Säg till om du vill att jag försöker igen."
                    : "That leaves nothing to suggest. Tell me if you'd like me to try again."),
                ct);
        }

        if (outcome.AcceptedInPlace)
        {
            // Content is unchanged by design — the user decided the clash is
            // acceptable. What changes is the draft's own record of that, so
            // the gate does not ask the same question again the moment it
            // re-runs.
            draft = await _drafts.AcceptConflictAsync(draftId, userId, conflictType, ct) ?? draft;
        }
        else
        {
            draft = await _drafts.UpdatePayloadAsync(draftId, userId, outcome.PayloadJson!, ct) ?? draft;
        }

        // Counted whatever the strategy was: a fix that produced no progress is
        // still an attempt, and the limit exists to stop exactly the case where
        // each one reintroduces the last.
        draft = await _drafts.RecordRebuildAsync(draftId, userId, conflictType, strategy, ct) ?? draft;

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(draft.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await _drafts.SetStatusAsync(draftId, userId, GlunoProposalDraftStatuses.Failed, null, ct);
            return await ConflictStoppedAsync(
                conversation, clarification, GlunoDraftError.NotUsable, language, ct);
        }

        var proposal = new GlunoProposal
        {
            ActionName = draft.ActionType,
            Kind = DraftKind(draft.ActionType),
            TripId = draft.TripId,
            Summary = GlunoDraftSummary(payload, context.User.Language),
            Payload = payload,
        };

        // The SAME gate the original turn ran, on the edited payload. A second
        // implementation of "does this day work" would drift from the first,
        // and a plan one accepts and the other blocks can never be applied and
        // never be fixed.
        var quality = _qualityGate.Check(new GlunoQualityInput
        {
            AnswerText = string.Empty,
            DayPlan = proposal.Kind == "day_plan" ? payload : null,
            Findings = context.Trip?.Findings ?? Array.Empty<TripFinding>(),
            ProducedProposal = true,
            ExpectsProposal = true,
            SomethingWasApplied = false,
            HasVerifiedTravelTimes = _routing.HasVerifiedRouting,
            HasVerifiedOpeningHours = context.DiscussedPlaces.Count > 0,
            Pace = TripPaces.Parse(context.Preferences
                .FirstOrDefault(preference => preference.Key == Models.GlunoPreferenceKeys.Pace)?.Value),
            Language = context.User.Language,
            ExistingTitles = context.Trip?.Activities.Select(activity => activity.Title).ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            SuggestedTitles = [proposal.Summary],
        });

        var remaining = GlunoConflictMapper
            .From(quality, draft.ConflictVersion + 1,
                dayPlan: payload,
                destinationMismatches: DestinationMismatches(payload, context))
            // What the user has already accepted is not asked again. Anything
            // NEW still is — accepting a full day says nothing about a clash
            // with a booking that appeared since.
            .Where(item => !draft.HasAccepted(item.ConflictType))
            .ToList();

        // Moves ConflictVersion and sets the status from what was found, in one
        // place, so the version and the verdict can never disagree.
        draft = await _drafts.RecordConflictsAsync(draftId, userId, remaining.Count > 0, ct) ?? draft;

        // ── The next question, or the proposal ────────────────────────────
        var next = GlunoConflictMapper.MostBlocking(remaining);

        if (next != null && GlunoConflictMapper.Options(next, context.User.Language).Count > 1)
        {
            if (draft.IsOutOfRebuilds)
            {
                return await ConflictStoppedAsync(
                    conversation, clarification, GlunoDraftError.OutOfRebuilds, language, ct);
            }

            // One conflict at a time. Resolving the worst often removes the
            // rest, and three questions about one suggestion is how a chat
            // becomes a wizard.
            return await AskNextConflictAsync(conversation, userId, clarification, draft, next, context, ct);
        }

        if (remaining.Count > 0)
        {
            // Still conflicting, and nothing left to offer about it.
            await _drafts.SetStatusAsync(draftId, userId, GlunoProposalDraftStatuses.Failed, null, ct);
            return await ConflictStoppedAsync(
                conversation, clarification, GlunoDraftError.OutOfRebuilds, language, ct);
        }

        return await ReadyForApprovalAsync(conversation, userId, clarification, draft, proposal, context, ct);
    }

    /// <summary>
    /// What applying a strategy produced.
    ///
    /// Exactly one of three things, and the caller branches on which: a new
    /// payload, an acceptance that changed no content, or a further question.
    /// All null means nothing could be done — which is an answer too, and a
    /// better one than a silent no-op.
    /// </summary>
    private sealed record GlunoStrategyOutcome
    {
        public string? PayloadJson { get; init; }
        public bool AcceptedInPlace { get; init; }
        public GlunoSubQuestion? NextCard { get; init; }
        /// A specific reason, when there is one worth saying.
        public string? Message { get; init; }

        public static GlunoStrategyOutcome Changed(string payloadJson) => new() { PayloadJson = payloadJson };
        public static GlunoStrategyOutcome Accepted() => new() { AcceptedInPlace = true };
        public static GlunoStrategyOutcome Ask(GlunoSubQuestion card) => new() { NextCard = card };
        public static GlunoStrategyOutcome Impossible(string? message = null) => new() { Message = message };
    }

    /// A day or time chooser, built from real candidates.
    private sealed record GlunoSubQuestion(
        string Type, string Question, IReadOnlyList<GlunoOptionDraft> Options);

    /// <summary>
    /// Turns a chosen strategy into a change to the draft — deterministically.
    ///
    /// EVERY BRANCH HERE IS ARITHMETIC OVER DATA THE BACKEND ALREADY HAS: the
    /// trip's dates, what is booked on each day, how long the journey takes,
    /// when a place is open. None of it is a judgement call, which is why none
    /// of it is a model call.
    ///
    /// A model asked to "move this to a better time" would be guessing at all
    /// of that, and would sometimes guess a slot the scheduler then rejects —
    /// so the user taps, waits, and gets the same card back. Worse: two
    /// identical taps could produce different plans, and a conflict answer has
    /// to be predictable to be worth offering.
    /// </summary>
    private async Task<GlunoStrategyOutcome> ApplyStrategyAsync(
        Guid userId,
        GlunoProposalDraft draft,
        GlunoClarification clarification,
        string conflictType,
        string strategy,
        GlunoClarificationOption option,
        GlunoContext context,
        CancellationToken ct)
    {
        var swedish = string.Equals(context.User.Language, "sv", StringComparison.OrdinalIgnoreCase);

        if (ReadPayload(draft) is not { } payload)
            return GlunoStrategyOutcome.Impossible();

        var rows = GlunoDraftPlan.Rows(payload);
        var target = GlunoDraftPlan.NewestSuggestion(rows);
        var date = GlunoDraftPlan.DateOf(payload);

        // Every strategy below acts on the row Gluno just suggested. Without
        // one there is nothing this suggestion may touch — everything left in
        // the plan belongs to the user.
        if (target == null) return GlunoStrategyOutcome.Impossible();

        switch (strategy)
        {
            // ── Answers to the sub-questions ──────────────────────────────

            case GlunoConflictStrategies.ChooseAnotherDay
                when clarification.Type == GlunoClarificationTypes.Day:
            {
                // The option's value is an ISO date the backend put there. It is
                // re-parsed rather than trusted as a string, and re-checked
                // against the trip, because the card may be an hour old.
                if (!DateOnly.TryParseExact(
                        option.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var chosen)
                    || !TripDateRange.Contains(context.Trip!.StartDate, context.Trip.EndDate, chosen))
                {
                    return GlunoStrategyOutcome.Impossible();
                }

                return GlunoDraftPlan.WithDate(draft.PayloadJson, chosen) is { } moved
                    ? GlunoStrategyOutcome.Changed(moved)
                    : GlunoStrategyOutcome.Impossible();
            }

            case GlunoConflictStrategies.ChooseAnotherTime
                when clarification.Type == GlunoClarificationTypes.ActivityTime:
            {
                if (GlunoDraftPlan.ParseTime(option.Value) is not { } chosen)
                    return GlunoStrategyOutcome.Impossible();

                return GlunoDraftPlan.WithTime(
                        draft.PayloadJson, target.Index, chosen, target.EffectiveDuration) is { } moved
                    ? GlunoStrategyOutcome.Changed(moved)
                    : GlunoStrategyOutcome.Impossible();
            }

            // ── The choosers ──────────────────────────────────────────────

            case GlunoConflictStrategies.ChooseAnotherDay:
                return DayQuestion(context, target, date, swedish);

            case GlunoConflictStrategies.ChooseAnotherTime:
                return TimeQuestion(context, rows, target, date, swedish);

            // ── Move the new one ──────────────────────────────────────────
            //
            // The same arithmetic as the time chooser, without the tap: Gluno
            // was asked to sort it out, so it takes the first slot that works.
            // Falling back to the day chooser when the day is full is a real
            // next step, not a failure — the activity has to go somewhere.
            case GlunoConflictStrategies.MoveNew:
            {
                var times = AvailableTimesFor(context, rows, target, date);

                if (times.Count == 0) return DayQuestion(context, target, date, swedish);

                return GlunoDraftPlan.WithTime(
                        draft.PayloadJson, target.Index, times[0], target.EffectiveDuration) is { } moved
                    ? GlunoStrategyOutcome.Changed(moved)
                    : GlunoStrategyOutcome.Impossible();
            }

            // ── Make it shorter ───────────────────────────────────────────
            case GlunoConflictStrategies.Shorten:
            {
                var shortened = ShortestThatFits(context, rows, target, date);

                if (shortened == null)
                {
                    return GlunoStrategyOutcome.Impossible(swedish
                        ? "Den går inte att korta så mycket att den får plats."
                        : "It can't be shortened enough to fit.");
                }

                return GlunoDraftPlan.WithShortened(draft.PayloadJson, target.Index, shortened.Value) is { } trimmed
                    ? GlunoStrategyOutcome.Changed(trimmed)
                    : GlunoStrategyOutcome.Impossible();
            }

            // ── Touching something that already exists ────────────────────
            //
            // Both of these act on the user's own plan, so neither happens now.
            // The intent is recorded on the draft and carried out inside the
            // apply transaction, behind the button, against the live row.
            case GlunoConflictStrategies.MoveExisting:
                return MoveExistingOperation(draft, rows, target, date, context, swedish);

            case GlunoConflictStrategies.ReplaceExisting:
                return ReplaceExistingOperation(draft, rows, target, swedish);

            // ── The two that were already deterministic ───────────────────
            case GlunoConflictStrategies.KeepBoth:
                return GlunoStrategyOutcome.Accepted();

            case GlunoConflictStrategies.RemoveNew:
                return GlunoProposalDraftService.ApplyDeterministic(
                        draft.PayloadJson, strategy, [target.Index]) is { } removed
                    ? GlunoStrategyOutcome.Changed(removed)
                    : GlunoStrategyOutcome.Impossible();

            default:
                return GlunoStrategyOutcome.Impossible();
        }
    }

    /// <summary>
    /// The day chooser, or an honest stop when no day could hold it.
    ///
    /// Only days that pass every deterministic check are offered, so a shown
    /// day is a day that works.
    /// </summary>
    private static GlunoStrategyOutcome DayQuestion(
        GlunoContext context, GlunoDraftRow target, DateOnly? currentDate, bool swedish)
    {
        var (_, maxStops) = TripPaces.DayStopRange(TripPaces.Parse(context.Preferences
            .FirstOrDefault(preference => preference.Key == Models.GlunoPreferenceKeys.Pace)?.Value));

        var days = GlunoDraftPlan.AvailableDays(context.Trip!, target, currentDate, maxStops);

        if (days.Count == 0)
        {
            return GlunoStrategyOutcome.Impossible(swedish
                ? "Det finns ingen annan dag på resan där den får plats."
                : "There's no other day on the trip where it fits.");
        }

        // The same builder the ordinary day clarification uses, so a date row
        // looks the same wherever it appears — and carries its destination, so
        // two Fridays on a roadtrip are told apart.
        var options = GlunoClarificationBuilder.DayOptions(
            context.Trip!.Destinations ?? EmptyDestinations(context.Trip),
            days,
            context.User.Language);

        return GlunoStrategyOutcome.Ask(new GlunoSubQuestion(
            GlunoClarificationTypes.Day,
            swedish ? "Vilken dag passar istället?" : "Which day suits instead?",
            options));
    }

    /// <summary>
    /// The time chooser, falling back to the day chooser when the day is full.
    ///
    /// Never claims success. If no time works and no day works either, the
    /// caller stops and says so — pretending a strategy succeeded is the one
    /// outcome worse than admitting it did not.
    /// </summary>
    private static GlunoStrategyOutcome TimeQuestion(
        GlunoContext context,
        IReadOnlyList<GlunoDraftRow> rows,
        GlunoDraftRow target,
        DateOnly? date,
        bool swedish)
    {
        var times = AvailableTimesFor(context, rows, target, date);

        if (times.Count == 0) return DayQuestion(context, target, date, swedish);

        var options = times.Select((time, index) => new GlunoOptionDraft(
            $"time-{index}",
            time.ToString("HH:mm", CultureInfo.InvariantCulture))
        {
            Description = $"{time:HH\\:mm}–{time.AddMinutes(target.EffectiveDuration):HH\\:mm}",
            // A fixed vocabulary value. Nothing behind it to rot, and nothing
            // the model had any part in producing.
            EntityType = GlunoClarificationEntityTypes.Enum,
            Value = time.ToString("HH:mm", CultureInfo.InvariantCulture),
            Icon = "time-outline",
        }).ToList();

        return GlunoStrategyOutcome.Ask(new GlunoSubQuestion(
            GlunoClarificationTypes.ActivityTime,
            swedish ? "Vilken tid passar istället?" : "What time suits instead?",
            options));
    }

    /// <summary>
    /// Valid start times for the affected row.
    ///
    /// The window is a plausible waking day, narrowed by the opening hours the
    /// schedule engine already resolved onto the row. Not fetched again: the
    /// engine did the provider call and wrote the answer, and asking twice
    /// risks two answers to one question.
    /// </summary>
    private static IReadOnlyList<TimeOnly> AvailableTimesFor(
        GlunoContext context, IReadOnlyList<GlunoDraftRow> rows, GlunoDraftRow target, DateOnly? date)
        => date == null
            ? Array.Empty<TimeOnly>()
            : GlunoDraftPlan.AvailableTimes(
                rows, target, dayStart: new TimeOnly(8, 0), dayEnd: new TimeOnly(22, 0));

    /// <summary>
    /// The shortest length that makes the affected row fit, or null.
    ///
    /// Walks DOWN from just under its current length in half-hour steps and
    /// stops at the first that clears every neighbour — so the activity is
    /// trimmed as little as possible rather than cut to the floor. Never goes
    /// below the minimum, and never touches a locked row.
    /// </summary>
    private static int? ShortestThatFits(
        GlunoContext context, IReadOnlyList<GlunoDraftRow> rows, GlunoDraftRow target, DateOnly? date)
    {
        if (target.IsLocked || target.Start is not { } start || date is not { } planDate) return null;

        var others = rows.Where(row => row.Index != target.Index).ToList();

        for (var minutes = target.EffectiveDuration - 30;
             minutes >= GlunoDraftPlan.MinimumDurationMinutes;
             minutes -= 30)
        {
            var trimmed = target with { DurationMinutes = minutes, End = start.AddMinutes(minutes) };

            // Does the shortened version still fit where it already is? The
            // window is exactly its own slot, so this asks "does it clear its
            // neighbours now" and nothing else.
            var candidate = GlunoDraftPlan.AvailableTimes(
                others.Append(trimmed).ToList(), trimmed,
                dayStart: start, dayEnd: start.AddMinutes(minutes));

            if (candidate.Count > 0) return minutes;
        }

        return null;
    }

    /// <summary>
    /// Records an intended move of an Activity that already exists.
    ///
    /// NO WRITE HAPPENS. The user tapped an option on a suggestion they have
    /// not approved; moving their booking now would be exactly the write the
    /// draft flow exists to defer.
    /// </summary>
    private static GlunoStrategyOutcome MoveExistingOperation(
        GlunoProposalDraft draft,
        IReadOnlyList<GlunoDraftRow> rows,
        GlunoDraftRow target,
        DateOnly? date,
        GlunoContext context,
        bool swedish)
    {
        var existing = CollidingExisting(rows, target);

        if (existing?.ExistingActivityId is not { } activityId)
            return GlunoStrategyOutcome.Impossible();

        // A booking with a time belongs to somebody else's system. Offering to
        // move it would be offering something Gluno cannot do, and doing it
        // would desynchronise the plan from a real reservation.
        if (existing.IsFixed)
        {
            return GlunoStrategyOutcome.Impossible(swedish
                ? "Den bokningen går inte att flytta."
                : "That booking can't be moved.");
        }

        var live = context.Trip!.Activities.FirstOrDefault(activity => activity.Id == activityId);

        // Gone since the card was built. Stale rather than guessed at.
        if (live == null) return GlunoStrategyOutcome.Impossible();

        // Somewhere the moved item does not clash with the suggestion.
        var slot = FreeSlotAfter(rows, target);
        if (slot is not { } newStart) return GlunoStrategyOutcome.Impossible();

        var operation = new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = activityId,
            ToDate = (date ?? live.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ToTime = newStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            // What it looked like when the user answered, so apply can tell
            // "still as it was" from "somebody changed it since".
            FromDate = live.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FromTime = live.Time,
            FromTitle = live.Title,
        };

        return GlunoDraftPlan.WithOperation(draft.PayloadJson, operation) is { } updated
            ? GlunoStrategyOutcome.Changed(updated)
            : GlunoStrategyOutcome.Impossible();
    }

    /// <summary>
    /// Records that an existing Activity should be replaced by the suggestion.
    ///
    /// Offered only for a duplicate, where the two are the same thing twice —
    /// and never for anything locked, because a booking with a reference number
    /// is not something to quietly delete.
    /// </summary>
    private static GlunoStrategyOutcome ReplaceExistingOperation(
        GlunoProposalDraft draft,
        IReadOnlyList<GlunoDraftRow> rows,
        GlunoDraftRow target,
        bool swedish)
    {
        var existing = CollidingExisting(rows, target);

        if (existing?.ExistingActivityId is not { } activityId)
            return GlunoStrategyOutcome.Impossible();

        if (existing.IsFixed)
        {
            return GlunoStrategyOutcome.Impossible(swedish
                ? "Den bokningen går inte att ersätta."
                : "That booking can't be replaced.");
        }

        var operation = new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.ReplaceExisting,
            ActivityId = activityId,
            FromTime = existing.Start?.ToString("HH:mm", CultureInfo.InvariantCulture),
            FromTitle = existing.Title,
        };

        return GlunoDraftPlan.WithOperation(draft.PayloadJson, operation) is { } updated
            ? GlunoStrategyOutcome.Changed(updated)
            : GlunoStrategyOutcome.Impossible();
    }

    /// The existing row the suggestion collides with — the nearest earlier one
    /// with a time, which is exactly what the quality gate compared against.
    private static GlunoDraftRow? CollidingExisting(
        IReadOnlyList<GlunoDraftRow> rows, GlunoDraftRow target)
    {
        for (var index = target.Index - 1; index >= 0; index--)
        {
            if (rows[index].Start != null && rows[index].IsLocked) return rows[index];
        }

        return rows.FirstOrDefault(row => row.IsLocked && row.ExistingActivityId.HasValue);
    }

    /// The first half hour after the suggestion ends. Where a displaced item
    /// goes when the suggestion takes its slot.
    private static TimeOnly? FreeSlotAfter(IReadOnlyList<GlunoDraftRow> rows, GlunoDraftRow target)
    {
        if (target.Start is not { } start) return null;

        var after = start.AddMinutes(target.EffectiveDuration);
        // Not past a sensible end of day: an activity pushed to 23:30 is not
        // rescheduled, it is buried.
        return after < new TimeOnly(21, 0) ? after : null;
    }

    /// A trip with no destination summary loaded still produces readable day
    /// rows — the date is the answer, the place is the helpful extra.
    private static TripDestinationSummary EmptyDestinations(GlunoTripContext trip)
        => new()
        {
            Title = trip.Title,
            StartDate = trip.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EndDate = trip.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Asks the day or time question the strategy produced.
    ///
    /// Bound to the SAME draft, carrying the versions as they stand. Nothing
    /// has changed yet — the user picked a way to fix it, not a fix — so no
    /// version moves and no rebuild is spent.
    /// </summary>
    private async Task<GlunoTurnResult> AskSubQuestionAsync(
        GlunoConversation conversation,
        Guid userId,
        GlunoClarification previous,
        GlunoProposalDraft draft,
        string conflictType,
        GlunoSubQuestion card,
        CancellationToken ct)
    {
        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = card.Question,
        }, ct);

        var clarification = await _clarifications.CreateAsync(
            new GlunoClarification
            {
                ConversationId = conversation.Id,
                UserId = userId,
                TripId = draft.TripId,
                // The SAME original turn. The user typed it once.
                OriginalUserMessageId = previous.OriginalUserMessageId,
                MessageId = assistantMessage.Id,
                Type = card.Type,
                Question = card.Question,
                OriginalIntent = previous.OriginalIntent,
                // The options ARE the answers. A free-text escape would invite
                // a date or a time the validator never checked.
                AllowFreeText = false,
                DraftId = draft.Id,
                DraftVersion = draft.DraftVersion,
                ConflictVersion = draft.ConflictVersion,
                // Carried through, so the eventual change is recorded against
                // the conflict it was fixing.
                ConflictType = conflictType,
                ConflictMetaJson = previous.ConflictMetaJson,
            },
            card.Options,
            ct);

        await _clarifications.RecordContinuationAsync(previous.Id, assistantMessage.Id, ct);

        _logger.LogInformation(
            "[GLUNO] conflict sub-question type={Type} options={Count}", card.Type, card.Options.Count);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = assistantMessage,
            AssistantMessage = assistantMessage,
            Clarification = clarification,
        };
    }

    /// <summary>
    /// The draft validated clean. It becomes a proposal — and only now.
    ///
    /// Still no write to the Adventure: a proposal is a card with an Apply
    /// button, and the button is the user's to press.
    /// </summary>
    private async Task<GlunoTurnResult> ReadyForApprovalAsync(
        GlunoConversation conversation,
        Guid userId,
        GlunoClarification clarification,
        GlunoProposalDraft draft,
        GlunoProposal proposal,
        GlunoContext context,
        CancellationToken ct)
    {
        var swedish = string.Equals(context.User.Language, "sv", StringComparison.OrdinalIgnoreCase);

        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = swedish ? "Då blir planen så här." : "Here's the plan then.",
        }, ct);

        // Status before the proposal, so a failure between the two leaves a
        // draft that cannot become a second proposal on a retry.
        var ready = await _drafts.SetStatusAsync(
            draft.Id, userId, GlunoProposalDraftStatuses.ReadyForApproval, null, ct) ?? draft;

        var records = await CreateProposalsAsync(
            conversation, assistantMessage.Id, [proposal], ct,
            // The binding apply re-checks. A proposal whose draft has moved on
            // since is describing a plan the user never saw.
            draftId: ready.Id, draftVersion: ready.DraftVersion);

        if (records.Count > 0)
        {
            await _drafts.SetStatusAsync(
                draft.Id, userId, GlunoProposalDraftStatuses.ReadyForApproval, records[0].Id, ct);
        }

        await _clarifications.RecordContinuationAsync(clarification.Id, assistantMessage.Id, ct);

        _logger.LogInformation(
            "[GLUNO] draft ready for approval draftVersion={DraftVersion} rebuilds={Rebuilds}",
            ready.DraftVersion, ready.RebuildCount);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = assistantMessage,
            AssistantMessage = assistantMessage,
            ProposalRecords = records,
        };
    }

    /// <summary>
    /// Asks about the conflict that is still there after the last fix.
    ///
    /// A NEW clarification bound to the SAME draft, carrying the versions as
    /// they stand now. The original user message is referenced, never appended
    /// again — the user typed it once.
    /// </summary>
    private async Task<GlunoTurnResult> AskNextConflictAsync(
        GlunoConversation conversation,
        Guid userId,
        GlunoClarification previous,
        GlunoProposalDraft draft,
        GlunoProposalConflict conflict,
        GlunoContext context,
        CancellationToken ct)
    {
        var explanation = GlunoConflictStrategies.Explain(conflict.ConflictType, context.User.Language);

        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = explanation,
        }, ct);

        var clarification = await _clarifications.CreateAsync(
            new GlunoClarification
            {
                ConversationId = conversation.Id,
                UserId = userId,
                TripId = draft.TripId,
                // The SAME original turn. Appending the question again would
                // put it in the history twice for one thing the user asked.
                OriginalUserMessageId = previous.OriginalUserMessageId,
                MessageId = assistantMessage.Id,
                Type = GlunoClarificationTypes.ProposalConflict,
                Question = explanation,
                OriginalIntent = previous.OriginalIntent,
                AllowFreeText = false,
                DraftId = draft.Id,
                DraftVersion = draft.DraftVersion,
                ConflictVersion = draft.ConflictVersion,
                ConflictType = conflict.ConflictType,
                ConflictMetaJson = ConflictMetaJson(conflict, ReadPayload(draft), context),
            },
            GlunoConflictMapper.Options(conflict, context.User.Language),
            ct);

        await _clarifications.RecordContinuationAsync(previous.Id, assistantMessage.Id, ct);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = assistantMessage,
            AssistantMessage = assistantMessage,
            Clarification = clarification,
        };
    }

    /// <summary>
    /// Gluno could not resolve the plan. Says so plainly, once.
    ///
    /// Neutral by contract: no version numbers, no internal error names, no
    /// blame. The user asked for something and it did not work out, and a
    /// technical explanation would not help them decide what to do next.
    /// </summary>
    private async Task<GlunoTurnResult> ConflictStoppedAsync(
        GlunoConversation conversation,
        GlunoClarification clarification,
        GlunoDraftError error,
        string language,
        CancellationToken ct)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var text = error switch
        {
            GlunoDraftError.Stale or GlunoDraftError.NotUsable => swedish
                ? "Planen har hunnit ändras sedan jag frågade. Fråga mig igen så tittar jag på nytt."
                : "The plan changed since I asked. Ask me again and I'll take a fresh look.",

            GlunoDraftError.Forbidden => swedish
                ? "Du har inte längre tillgång till den resan."
                : "You no longer have access to that Adventure.",

            _ => swedish
                ? "Jag lyckades inte få ihop planen automatiskt. Säg hur du vill ha det så gör jag om den."
                : "I couldn't make the plan work automatically. Tell me how you'd like it and I'll redo it.",
        };

        // Code only — never which version, which draft or which trip.
        _logger.LogInformation("[GLUNO] conflict continuation stopped reason={Reason}", error);

        return await ConflictClosedAsync(conversation, clarification, text, ct);
    }

    /// One assistant message, bound to the clarification so a repeat tap
    /// replays it rather than running the continuation twice.
    private async Task<GlunoTurnResult> ConflictClosedAsync(
        GlunoConversation conversation,
        GlunoClarification clarification,
        string text,
        CancellationToken ct)
    {
        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = text,
        }, ct);

        await _clarifications.RecordContinuationAsync(clarification.Id, assistantMessage.Id, ct);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = assistantMessage,
            AssistantMessage = assistantMessage,
        };
    }

    /// <summary>
    /// Which rows of the draft the conflict is about.
    ///
    /// Recomputed from the draft's CURRENT payload rather than carried on the
    /// clarification: the indexes are positions in an array, and an array that
    /// has had a row removed since would make a remembered index point at the
    /// wrong activity.
    /// </summary>
    private static IReadOnlyList<int> ConflictIndexesFor(GlunoProposalDraft draft, string conflictType)
    {
        try
        {
            using var document = JsonDocument.Parse(draft.PayloadJson);

            if (!document.RootElement.TryGetProperty("activities", out var activities)
                || activities.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<int>();
            }

            // The last row Gluno itself added: what "skip it" means when the
            // clash is with something that was already in the plan.
            var index = 0;
            var candidate = -1;

            foreach (var row in activities.EnumerateArray())
            {
                var isFixed = row.TryGetProperty("isFixed", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                var isExisting = row.TryGetProperty("existingActivityId", out var existing)
                    && existing.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(existing.GetString());

                if (!isFixed && !isExisting) candidate = index;
                index++;
            }

            return candidate >= 0 ? [candidate] : Array.Empty<int>();
        }
        catch (JsonException)
        {
            return Array.Empty<int>();
        }
    }

    /// Rows demonstrably in the wrong town, from stored coordinates only.
    /// Empty whenever the trip context is absent — an unanswerable question
    /// produces no conflict rather than a guessed one.
    private static IReadOnlyList<int> DestinationMismatches(JsonElement? payload, GlunoContext context)
        => payload is { } plan && context.Trip is { } trip
            ? GlunoDestinationCheck.Mismatched(plan, trip)
            : Array.Empty<int>();

    /// The draft's payload, or null when it cannot be read. Never throws: this
    /// runs while building a card, and an unreadable payload costs a detail on
    /// screen, not the turn.
    private static JsonElement? ReadPayload(GlunoProposalDraft draft)
    {
        try
        {
            using var document = JsonDocument.Parse(draft.PayloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the conflict card shows, built server-side.
    ///
    /// Titles come from the Adventure and from the draft itself — both things
    /// the user can already see. Nothing here is an id or a version.
    /// </summary>
    private static string ConflictMetaJson(
        GlunoProposalConflict conflict, JsonElement? payload, GlunoContext context)
    {
        var titles = new List<string>();

        // What it clashes with, named. "That clashes with something already
        // planned" is a worse sentence than one that says which booking.
        foreach (var activityId in conflict.AffectedExistingActivityIds)
        {
            var match = context.Trip?.Activities.FirstOrDefault(activity => activity.Id == activityId);
            if (match != null && !string.IsNullOrWhiteSpace(match.Title)) titles.Add(match.Title);
        }

        // And the suggested row the clash is about.
        if (payload is { ValueKind: JsonValueKind.Object } plan
            && plan.TryGetProperty("activities", out var activities)
            && activities.ValueKind == JsonValueKind.Array)
        {
            var rows = activities.EnumerateArray().ToList();

            foreach (var index in conflict.AffectedDraftItemIndexes)
            {
                if (index < 0 || index >= rows.Count) continue;
                if (!rows[index].TryGetProperty("title", out var title)) continue;
                if (title.ValueKind != JsonValueKind.String) continue;

                var text = title.GetString();
                if (!string.IsNullOrWhiteSpace(text) && !titles.Contains(text)) titles.Add(text);
            }
        }

        return JsonSerializer.Serialize(new GlunoConflictDto
        {
            Type = conflict.ConflictType,
            Date = conflict.Date,
            StartTime = conflict.StartTime,
            EndTime = conflict.EndTime,
            // Not "we chose not to offer it" — the item genuinely is not ours
            // to move, and the card says which.
            ExistingIsLocked = conflict.ConflictType
                is GlunoConflictTypes.LockedBooking
                or GlunoConflictTypes.CheckInConflict
                or GlunoConflictTypes.CheckOutConflict,
            // Clamped at zero: a negative shortfall is not a shortfall, and
            // "-5 minutes short" is the kind of sentence that makes somebody
            // stop believing the rest of the card.
            MissingTravelMinutes = Math.Max(0, conflict.RequiredTravelMinutes - conflict.AvailableMinutes),
            AffectedTitles = titles,
        }, GlunoJson.Options);
    }

    /// The card shape behind an action name. A draft only ever holds an
    /// allow-listed action, so an unknown one is a build error, not input.
    private static string DraftKind(string actionType) => actionType switch
    {
        GlunoActions.ProposeDayPlan => "day_plan",
        GlunoActions.ProposeActivity => "activity",
        GlunoActions.ProposeDayLocation => "day_location",
        GlunoActions.ProposeActivityMove => "activity_move",
        GlunoActions.ProposeTripDateChange => "trip_dates",
        _ => "activity",
    };

    /// <summary>
    /// The proposal card's heading, rebuilt from the payload.
    ///
    /// The date and the number of stops, and nothing else. Reusing the model's
    /// original sentence would describe the plan BEFORE the fix — a summary
    /// naming a stop that has since been removed is worse than a plain one.
    /// </summary>
    private static string GlunoDraftSummary(JsonElement payload, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var count = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("activities", out var activities)
            && activities.ValueKind == JsonValueKind.Array
                ? activities.GetArrayLength()
                : 0;

        if (count == 0) return swedish ? "Uppdaterad plan" : "Updated plan";

        return swedish
            ? $"Uppdaterad plan med {count} {(count == 1 ? "aktivitet" : "aktiviteter")}"
            : $"Updated plan with {count} {(count == 1 ? "activity" : "activities")}";
    }

    private async Task<GlunoTurnResult> AskWhichAdventureAsync(
        GlunoConversation conversation,
        Guid userId,
        string text,
        GlunoIntentResult intent,
        IReadOnlyList<TripChoice> choices,
        GlunoContext context,
        CancellationToken ct)
    {
        var ranked = GlunoClarificationBuilder.RankTrips(choices, text, context.Today);
        var options = GlunoClarificationBuilder.TripOptions(ranked, context.Today, context.User.Language);

        var userMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.User,
            Text = text,
        }, ct);

        var question = GlunoClarificationBuilder.QuestionFor(
            GlunoClarificationTypes.Adventure, context.User.Language);

        // The card carries the question. The message text is the same
        // sentence so a client that has never heard of clarifications still
        // renders something sensible rather than an empty turn.
        var assistantMessage = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = question,
        }, ct);

        var clarification = await _clarifications.CreateAsync(
            new GlunoClarification
            {
                ConversationId = conversation.Id,
                UserId = userId,
                TripId = null,
                OriginalUserMessageId = userMessage.Id,
                MessageId = assistantMessage.Id,
                Type = GlunoClarificationTypes.Adventure,
                Question = question,
                OriginalIntent = intent.PrimaryIntent.ToString(),
                // More Adventures than fit: the rest are reachable by saying
                // which, rather than by scrolling a list of twenty.
                AllowFreeText = ranked.Count > GlunoClarificationBuilder.MaxOptions,
            },
            options,
            ct);

        return new GlunoTurnResult
        {
            Conversation = conversation,
            UserMessage = userMessage,
            AssistantMessage = assistantMessage,
            Clarification = clarification,
        };
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
            // Where the trip GOES, as its own section above everything else
            // about it. Small, and the one thing whose absence makes Gluno ask
            // a question the Adventure already answers — so it is never a
            // casualty of trimming, even when the rest of the trip is.
            new GlunoContextSection(
                GlunoContextPriority.RelevantTrip, "destinations",
                JsonSerializer.Serialize(context.Trip?.Destinations, GlunoJson.Options))
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
