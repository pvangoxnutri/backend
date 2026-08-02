using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;

namespace sidequest.backend.Controllers;

/// <summary>
/// The mobile app's entire surface onto Gluno.
///
/// Everything Gluno needs — context, prompt, model, actions, external data —
/// happens behind these endpoints. The app sends a message and renders what
/// comes back; it holds no prompt, no key, and no data-gathering logic of its
/// own. That is the point of the split: Gluno's behaviour can change here
/// without an app release, and the app cannot change Gluno's rules.
///
/// Every endpoint is [Authorize] and scoped to the caller. A conversation is
/// private to the user who had it, even on a shared Adventure.
/// </summary>
[ApiController]
[Route("api/gluno")]
[Authorize]
public class GlunoController : ControllerBase
{
    private readonly GlunoAvailability _availability;
    private readonly IGlunoChatService _chat;
    private readonly IGlunoConversationService _conversations;
    private readonly IGlunoProposalStore _proposals;
    private readonly IGlunoProposalApplyService _apply;
    private readonly IRoutingService _routing;
    private readonly IDayPlanPlanner _dayPlanner;
    private readonly ILiveTravelRegistry _liveTravel;
    private readonly IGlunoClarificationService _clarifications;
    private readonly IGlunoContextBuilder _contextBuilder;
    private readonly ILogger<GlunoController> _logger;

    public GlunoController(
        GlunoAvailability availability,
        IGlunoChatService chat,
        IGlunoConversationService conversations,
        IGlunoProposalStore proposals,
        IGlunoProposalApplyService apply,
        IRoutingService routing,
        IDayPlanPlanner dayPlanner,
        ILiveTravelRegistry liveTravel,
        IGlunoClarificationService clarifications,
        IGlunoContextBuilder contextBuilder,
        ILogger<GlunoController> logger)
    {
        _liveTravel = liveTravel;
        _clarifications = clarifications;
        _contextBuilder = contextBuilder;
        _logger = logger;
        _availability = availability;
        _chat = chat;
        _conversations = conversations;
        _proposals = proposals;
        _apply = apply;
        _routing = routing;
        _dayPlanner = dayPlanner;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Whether Gluno can answer at all. The app calls this before showing its
    /// entry point, so a build with the feature flag on never opens a chat
    /// panel against an environment that has Gluno switched off.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<GlunoStatusDto> GetStatus()
        => Ok(new GlunoStatusDto
        {
            // Core only. Everything below this line is an optional extra and
            // none of them may pull `Available` down — see GlunoStatusDto.
            Available = _availability.IsAvailable,
            Enabled = _availability.IsEnabled,
            AiConfigured = _availability.IsConfigured,
            Reason = _availability.UnavailableReason,
            SystemPromptVersion = GlunoSystemPrompt.Version,
            TravelDataAvailable = _availability.HasTravelData,
            // Whether travel times can be verified at all — a boolean and
            // nothing else. Which provider does it, and on what credentials, is
            // not the mobile app's business and is never in a response body.
            VerifiedTravelTimes = _routing.HasVerifiedRouting,
            // Whether current travel information can be fetched at all — a
            // boolean, and nothing about which provider or on what key.
            LiveTravelInfoAvailable = _liveTravel.IsAvailable,
        });

    /// <summary>
    /// The caller's own conversations. <paramref name="tripId"/> narrows to one
    /// Adventure; omitting it returns global conversations too.
    /// </summary>
    [HttpGet("conversations")]
    public async Task<ActionResult<List<GlunoConversationDto>>> ListConversations([FromQuery] Guid? tripId)
    {
        var conversations = await _conversations.ListAsync(GetUserId(), tripId, HttpContext.RequestAborted);
        return Ok(conversations.Select(MapConversation).ToList());
    }

    /// <summary>
    /// The conversation to reopen for this scope, with its newest page.
    ///
    /// Returns 204 when the user has never talked to Gluno in this scope. That
    /// is what keeps opening the screen from minting an empty conversation
    /// every time: nothing is created until the first message is actually sent.
    ///
    /// A null <paramref name="tripId"/> means the GLOBAL conversation — the two
    /// scopes never share a history.
    /// </summary>
    [HttpGet("conversations/current")]
    public async Task<ActionResult<GlunoConversationDetailDto>> GetCurrentConversation([FromQuery] Guid? tripId)
    {
        var conversation = await _conversations.GetLatestForScopeAsync(GetUserId(), tripId, HttpContext.RequestAborted);
        if (conversation == null) return NoContent();

        return Ok(await BuildDetailAsync(conversation));
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<ActionResult<GlunoConversationDetailDto>> GetConversation(Guid conversationId)
    {
        var conversation = await _conversations.GetOwnedAsync(conversationId, GetUserId(), HttpContext.RequestAborted);
        if (conversation == null) return NotFound();

        return Ok(await BuildDetailAsync(conversation));
    }

    /// <summary>
    /// Older messages, walking back from <paramref name="before"/>.
    ///
    /// Ownership is re-checked here rather than trusted from the conversation
    /// id — the id alone is not a capability.
    /// </summary>
    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<GlunoMessagePageDto>> GetMessages(
        Guid conversationId, [FromQuery] DateTime? before, [FromQuery] int? limit)
    {
        var conversation = await _conversations.GetOwnedAsync(conversationId, GetUserId(), HttpContext.RequestAborted);
        if (conversation == null) return NotFound();

        var page = await _conversations.GetMessagePageAsync(
            conversation.Id, before, limit ?? 0, HttpContext.RequestAborted);

        return Ok(new GlunoMessagePageDto
        {
            Messages = await MapMessagesAsync(page.Messages),
            HasMore = page.HasMore,
        });
    }

    private async Task<GlunoConversationDetailDto> BuildDetailAsync(Models.GlunoConversation conversation)
    {
        var page = await _conversations.GetMessagePageAsync(
            conversation.Id, before: null, limit: 0, HttpContext.RequestAborted);

        return new GlunoConversationDetailDto
        {
            Conversation = MapConversation(conversation),
            Messages = await MapMessagesAsync(page.Messages),
            HasMore = page.HasMore,
        };
    }

    // ── Proposals ─────────────────────────────────────────────────────────

    /// <summary>
    /// One proposal, for the review screen. Ownership is part of the lookup,
    /// so somebody else's proposal is simply not found.
    /// </summary>
    [HttpGet("proposals/{proposalId:guid}")]
    public async Task<ActionResult<GlunoProposalDto>> GetProposal(Guid proposalId)
    {
        var proposal = await _proposals.GetOwnedAsync(proposalId, GetUserId(), HttpContext.RequestAborted);
        if (proposal == null) return NotFound();

        return Ok(MapProposalRecord(proposal));
    }

    /// <summary>
    /// Records the user's edits to a pending proposal.
    ///
    /// This does NOT apply anything — it only replaces what apply will later
    /// validate. A proposal that has already been applied, rejected or gone
    /// stale is never editable back into pending.
    ///
    /// A DAY PLAN is re-laid by the schedule engine before it is stored. Moving
    /// a stop or stretching one by an hour changes every travel time after it,
    /// and a review screen that let someone edit their way into two overlapping
    /// Activities and then save would defeat the entire point of validating in
    /// the first place. The edited payload goes back through the same engine
    /// that produced the original, warnings and all.
    /// </summary>
    [HttpPatch("proposals/{proposalId:guid}")]
    public async Task<ActionResult<GlunoApplyResponseDto>> UpdateProposal(
        Guid proposalId, [FromBody] UpdateGlunoProposalDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var payload = dto.Payload;

        var existing = await _proposals.GetOwnedAsync(proposalId, GetUserId(), ct);
        if (existing == null) return NotFound();

        if (existing.ActionType == GlunoActions.ProposeDayPlan && existing.TripId is { } tripId)
        {
            var revalidated = await _dayPlanner.RevalidateAsync(tripId, payload, LanguageOf(), ct);
            payload = revalidated.Payload;
        }

        var result = await _apply.UpdatePayloadAsync(proposalId, GetUserId(), payload, ct);
        return MapApplyResult(result);
    }

    /// The caller's app language, for provider-localised text and mode labels.
    /// Header only — never a client-supplied field in a body.
    private string LanguageOf()
        => Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase)
            ? "sv"
            : "en";

    /// <summary>
    /// Applies a proposal — the only endpoint in SideQuest that turns a Gluno
    /// suggestion into a real change, and only ever on an explicit user action.
    ///
    /// Safe to call twice: the proposal id is the idempotency key, so a retry
    /// or a double tap returns the original result instead of creating a
    /// second Activity.
    /// </summary>
    [HttpPost("proposals/{proposalId:guid}/apply")]
    public async Task<ActionResult<GlunoApplyResponseDto>> ApplyProposal(Guid proposalId)
    {
        var result = await _apply.ApplyAsync(proposalId, GetUserId(), HttpContext.RequestAborted);
        return MapApplyResult(result);
    }

    [HttpPost("proposals/{proposalId:guid}/reject")]
    public async Task<ActionResult<GlunoApplyResponseDto>> RejectProposal(Guid proposalId)
    {
        var result = await _apply.RejectAsync(proposalId, GetUserId(), HttpContext.RequestAborted);
        return MapApplyResult(result);
    }

    private ActionResult<GlunoApplyResponseDto> MapApplyResult(GlunoApplyResult result)
    {
        if (result.Error == GlunoApplyError.NotFound) return NotFound();
        if (result.Error == GlunoApplyError.Forbidden) return Forbid();

        var body = new GlunoApplyResponseDto
        {
            Proposal = MapProposalRecord(result.Proposal!),
            Changes = MapChanges(result.Changes),
            Message = result.Message,
        };

        return result.Error switch
        {
            // Replaying a successful apply is success, not an error — the
            // caller asked for a state that already holds.
            GlunoApplyError.AlreadyApplied => Ok(body),
            // 409: the proposal's state, not the request, is the problem.
            GlunoApplyError.InProgress or GlunoApplyError.AlreadyRejected or GlunoApplyError.Stale
                => Conflict(body),
            GlunoApplyError.Invalid or GlunoApplyError.Failed
                => UnprocessableEntity(body),
            _ => Ok(body),
        };
    }

    /// <summary>
    /// One turn. Creates the conversation when no id is given.
    ///
    /// Nothing here writes to a trip: an assistant turn may carry proposals,
    /// and accepting one is a separate, ordinary call to the trip endpoints
    /// from the app.
    /// </summary>
    [HttpPost("messages")]
    public async Task<ActionResult<GlunoTurnResponseDto>> SendMessage([FromBody] SendGlunoMessageDto dto)
    {
        // The first thing this endpoint does. If a 502 appears in the log with
        // no matching line here, the request never reached the controller —
        // which makes it a container or proxy problem rather than a Gluno one.
        // No ids, no message, nothing about the caller.
        _logger.LogInformation("[GLUNO] message endpoint entered");

        GlunoTurnResult result;

        try
        {
            result = await _chat.SendAsync(
                GetUserId(), dto.ConversationId, dto.TripId, dto.Message ?? string.Empty,
                dto.Screen, dto.IdempotencyKey, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(499, new { error = GlunoFailureCodes.Cancelled, retryable = false });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The last resort. The service has its own boundary; this one
            // covers the case where that boundary itself fails, so there is no
            // path at all by which this endpoint answers without the envelope
            // the app depends on.
            _logger.LogError(
                "[GLUNO] escaped type={Category} stage=controller", ex.GetType().Name);

            return StatusCode(502, new { error = "unknown", retryable = true });
        }

        // Every exit from here is logged once — the pair of lines is what
        // proves the request completed inside the process rather than being
        // cut off above it. Codes only.
        if (result.Error != GlunoTurnError.None)
        {
            _logger.LogInformation(
                "[GLUNO] message endpoint returning outcome={Outcome} error={Error}",
                result.Error, result.FailureCode ?? "none");
        }

        switch (result.Error)
        {
            // Every branch below carries BOTH fields. The app decides what to
            // say from `error` and whether to offer a retry from `retryable`,
            // and a branch that omits either leaves it guessing — which in
            // practice meant a generic "try again" on failures no retry could
            // fix.
            case GlunoTurnError.Unavailable:
                // 503, not 404: the endpoint exists, Gluno just isn't on here.
                return StatusCode(503, new
                {
                    error = "gluno_unavailable",
                    reason = _availability.UnavailableReason,
                    retryable = false,
                });
            case GlunoTurnError.EmptyMessage:
                return BadRequest(new { error = "empty_message", retryable = false });
            case GlunoTurnError.ConversationNotFound:
                return NotFound(new { error = "conversation_not_found", retryable = false });
            case GlunoTurnError.ConversationArchived:
                return BadRequest(new { error = "conversation_archived", retryable = false });
            case GlunoTurnError.NotTripMember:
                // An explicit body rather than Forbid(): that returns 403 with
                // NOTHING, so the app had only a status code to work from.
                return StatusCode(403, new
                {
                    error = GlunoFailureCodes.AuthorizationChanged,
                    retryable = false,
                });

            // 499 is the client-closed convention. The app treats it as "the
            // user pressed stop" and shows nothing — a cancellation is not a
            // failure and must never render as a red bubble.
            case GlunoTurnError.Cancelled:
                return StatusCode(499, new { error = GlunoFailureCodes.Cancelled, retryable = false });

            // 409: an identical send is already running. The app waits for the
            // first one rather than starting a second.
            case GlunoTurnError.DuplicateInFlight:
                return Conflict(new { error = "duplicate_in_flight", retryable = false });

            // 429 with a code the app localises. Existing conversations still
            // open and scroll; only new turns are refused.
            case GlunoTurnError.UsageLimitReached:
                return StatusCode(429, new
                {
                    error = result.FailureCode ?? GlunoFailureCodes.UserUsageLimit,
                    retryable = false,
                });

            case GlunoTurnError.ProviderFailed:
                return StatusCode(502, new
                {
                    error = result.FailureCode ?? GlunoFailureCodes.AiMalformedResponse,
                    // Whether "try again" is honest. A missing key fails
                    // identically on every tap; a timeout might not.
                    retryable = result.IsRetryable,
                });
        }

        _logger.LogInformation("[GLUNO] message endpoint returning status=200 error=none");

        return Ok(new GlunoTurnResponseDto
        {
            Conversation = MapConversation(result.Conversation!),
            UserMessage = MapMessage(result.UserMessage!, Array.Empty<Models.GlunoProposalRecord>()),
            // The records just written carry the ids the app applies against.
            AssistantMessage = MapMessage(result.AssistantMessage!, result.ProposalRecords),
            Clarification = MapClarification(result.Clarification),
        });
    }

    /// <summary>
    /// Answers a clarification and carries on with the original question.
    ///
    /// The user does not resend anything. The clarification remembers which
    /// turn it was asking about, so this endpoint resumes THAT question with
    /// the choice applied — which is the whole point of the feature.
    /// </summary>
    [HttpPost("clarifications/{clarificationId:guid}/resolve")]
    public async Task<ActionResult<GlunoTurnResponseDto>> ResolveClarification(
        Guid clarificationId, [FromBody] GlunoClarificationResolveDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(dto.OptionKey))
            return BadRequest(new { error = "missing_option", retryable = false });

        var resolved = await _clarifications.ResolveAsync(clarificationId, userId, dto.OptionKey!, ct);

        switch (resolved.Error)
        {
            case GlunoClarificationError.NotFound:
                return NotFound(new { error = "clarification_not_found", retryable = false });
            case GlunoClarificationError.Forbidden:
                return StatusCode(403, new { error = GlunoFailureCodes.AuthorizationChanged, retryable = false });
            case GlunoClarificationError.NotAnswerable:
                return Conflict(new { error = "clarification_closed", retryable = false });
            case GlunoClarificationError.OptionStale:
                // The Adventure went, or they left the group. Answerable again
                // only by asking afresh — never by honouring a stale button.
                return Conflict(new { error = "clarification_stale", retryable = false });
            case GlunoClarificationError.UnknownOption:
                return BadRequest(new { error = "unknown_option", retryable = false });
        }

        var clarification = resolved.Clarification!;
        var option = resolved.Selected;

        if (option == null)
            return Conflict(new { error = "clarification_closed", retryable = false });

        // A repeat tap returns the FIRST answer rather than running the turn
        // again — the user sees the same reply appear, which is what tapping
        // twice should look like.
        if (resolved.WasAlreadyResolved && clarification.ContinuationMessageId is { } existing)
        {
            var replay = await _conversations.GetMessageAsync(existing, userId, ct);
            if (replay != null)
            {
                return Ok(new GlunoTurnResponseDto
                {
                    Conversation = MapConversation(clarification.Conversation),
                    UserMessage = MapMessage(replay, Array.Empty<Models.GlunoProposalRecord>()),
                    AssistantMessage = MapMessage(replay, Array.Empty<Models.GlunoProposalRecord>()),
                    Clarification = MapClarification(clarification),
                });
            }
        }

        // ── Two continuations, and they are not interchangeable ───────────
        //
        // An ordinary clarification answers a question the turn could not
        // answer alone, so the turn is REPLAYED with the answer supplied.
        //
        // A proposal conflict has already produced a plan. Replaying it would
        // spend a model round re-deriving something the draft already holds,
        // and could come back with a different plan than the one the user was
        // looking at when they tapped. It takes its own path: a deterministic
        // fix applied to the draft, then the same quality gate again.
        //
        // Routed on the DRAFT BINDING, not on the type. A conflict can produce
        // a day or a time chooser, and those are ordinary `day` and
        // `activity_time` cards — but they answer about a draft, so they belong
        // on the draft path too. Routing on the type alone would send them
        // through the model and lose the plan they were fixing.
        var result = clarification.DraftId.HasValue
            ? await _chat.ContinueFromDraftAsync(userId, clarification, option, ct)
            : await _chat.ContinueFromClarificationAsync(
                userId, clarification, option, dto.IdempotencyKey, ct);

        if (result.Error != GlunoTurnError.None)
        {
            return StatusCode(502, new
            {
                error = result.FailureCode ?? GlunoFailureCodes.AiMalformedResponse,
                retryable = result.IsRetryable,
            });
        }

        return Ok(new GlunoTurnResponseDto
        {
            Conversation = MapConversation(result.Conversation!),
            UserMessage = MapMessage(result.UserMessage!, Array.Empty<Models.GlunoProposalRecord>()),
            AssistantMessage = MapMessage(result.AssistantMessage!, result.ProposalRecords),
            Clarification = MapClarification(result.Clarification),
        });
    }

    /// <summary>
    /// "Something else" — searches for an option that was not on the list.
    ///
    /// Scoped to what the caller can already see, per clarification type: their
    /// own Adventures, this trip's days, its Activities, its stops, or the
    /// provider results already shown in this conversation. Never a general
    /// query, never an external provider, never a model.
    ///
    /// The options it returns are new rows with new keys, built server-side —
    /// the client sends a string and gets back things it may tap, and no id
    /// travels in either direction.
    /// </summary>
    // ── Recommended places ────────────────────────────────────────────────

    /// <summary>
    /// One recommended place, in full, for the detail card.
    ///
    /// The turn that produced it already persisted everything the provider
    /// returned — name, rating, hours, image, coordinates — in its own message
    /// payload. So this reads that back rather than searching again: a second
    /// lookup could return different data, and the card would then show
    /// something the user was never recommended.
    ///
    /// <paramref name="optionKey"/> is positional and scoped to the message.
    /// A key from another conversation, or one nobody rendered, resolves to
    /// nothing.
    /// </summary>
    [HttpGet("messages/{messageId:guid}/places/{optionKey}")]
    public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace(Guid messageId, string optionKey)
    {
        var ct = HttpContext.RequestAborted;

        // Ownership is the lookup. A message from somebody else's conversation
        // is simply not found.
        var message = await _conversations.GetMessageAsync(messageId, GetUserId(), ct);
        if (message == null) return NotFound(new { error = "message_not_found", retryable = false });

        var place = GlunoPlaceOptions.Resolve(message, optionKey);
        if (place == null) return NotFound(new { error = "place_not_found", retryable = false });

        var index = GlunoPlaceOptions.IndexOf(optionKey);

        return Ok(MapPlace(place, index));
    }

    /// <summary>
    /// Turns a recommended place into a proposal the user can approve.
    ///
    /// NOTHING IS WRITTEN HERE. This creates the same kind of proposal a chat
    /// turn would, so it goes through the same review, the same conflict
    /// checks and the same explicit Apply. Tapping "Add" on a recommendation
    /// must not be a shortcut past the one place where a change gets agreed.
    ///
    /// No model runs: the place is already known, and which one the user meant
    /// is a lookup rather than a judgement.
    /// </summary>
    [HttpPost("messages/{messageId:guid}/places/{optionKey}/add")]
    public async Task<ActionResult<GlunoTurnResponseDto>> AddRecommendedPlace(
        Guid messageId, string optionKey, [FromBody] GlunoAddPlaceDto? dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var message = await _conversations.GetMessageAsync(messageId, userId, ct);
        if (message == null) return NotFound(new { error = "message_not_found", retryable = false });

        var place = GlunoPlaceOptions.Resolve(message, optionKey);
        if (place == null) return NotFound(new { error = "place_not_found", retryable = false });

        var result = await _chat.AddRecommendedPlaceAsync(
            userId, message, place, dto?.Date, dto?.IdempotencyKey, ct);

        if (result.Error != GlunoTurnError.None)
        {
            return StatusCode(502, new
            {
                error = result.FailureCode ?? GlunoFailureCodes.AiMalformedResponse,
                retryable = result.IsRetryable,
            });
        }

        return Ok(new GlunoTurnResponseDto
        {
            Conversation = MapConversation(result.Conversation!),
            UserMessage = MapMessage(result.UserMessage!, Array.Empty<Models.GlunoProposalRecord>()),
            AssistantMessage = MapMessage(result.AssistantMessage!, result.ProposalRecords),
            Clarification = MapClarification(result.Clarification),
        });
    }

    [HttpPost("clarifications/{clarificationId:guid}/search")]
    public async Task<ActionResult<GlunoClarificationDto>> SearchClarification(
        Guid clarificationId, [FromBody] GlunoClarificationSearchDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        if (!GlunoClarificationSearch.IsUsable(dto.Query))
            return BadRequest(new { error = "query_too_short", retryable = false });

        var clarification = await _clarifications.GetOwnedAsync(clarificationId, userId, ct);
        if (clarification == null) return NotFound(new { error = "clarification_not_found", retryable = false });

        if (!clarification.IsAnswerable)
            return Conflict(new { error = "clarification_closed", retryable = false });

        // The context is rebuilt under the caller's own membership, which is
        // what bounds the search. A clarification cannot widen its own scope by
        // being searched.
        var context = await _contextBuilder.BuildAsync(
            userId, clarification.TripId, clarification.ConversationId, ct);

        var options = clarification.Type switch
        {
            GlunoClarificationTypes.Adventure => GlunoClarificationSearch.Adventures(
                context.Trips
                    .Select(trip => new TripChoice(trip.Id, trip.Title, trip.StartDate, trip.EndDate))
                    .ToList(),
                dto.Query!, context.Today, context.User.Language),

            GlunoClarificationTypes.Day when context.Trip is { } dayTrip
                => GlunoClarificationSearch.Days(
                    dayTrip.Destinations ?? EmptyDestinations(dayTrip),
                    dayTrip.StartDate, dayTrip.EffectiveEndDate,
                    dto.Query!, context.User.Language),

            GlunoClarificationTypes.Activity when context.Trip is { } activityTrip
                => GlunoClarificationSearch.Activities(
                    activityTrip.Activities, dto.Query!, context.User.Language),

            GlunoClarificationTypes.Place when context.Trip?.Destinations is { } destinations
                => GlunoClarificationSearch.Destinations(destinations, dto.Query!),

            // A place clarification with no trip is about the provider results
            // already shown — the snapshot, never a fresh search.
            GlunoClarificationTypes.Place
                => GlunoClarificationSearch.DiscussedPlaces(context.DiscussedPlaces, dto.Query!),

            _ => Array.Empty<GlunoOptionDraft>(),
        };

        var updated = await _clarifications.ReplaceOptionsAsync(clarification.Id, userId, options, ct);

        // An empty result is a valid answer, not an error: the card says so and
        // the user can search again or go back to the original options.
        return Ok(MapClarification(updated ?? clarification));
    }

    private static TripDestinationSummary EmptyDestinations(GlunoTripContext trip) => new()
    {
        Title = trip.Title,
        StartDate = trip.StartDate.ToString("yyyy-MM-dd"),
    };

    [HttpPost("clarifications/{clarificationId:guid}/cancel")]
    public async Task<IActionResult> CancelClarification(Guid clarificationId)
    {
        var error = await _clarifications.CancelAsync(
            clarificationId, GetUserId(), HttpContext.RequestAborted);

        return error == GlunoClarificationError.NotFound ? NotFound() : NoContent();
    }

    /// Null on anything unreadable. A card that renders without its subtitle is
    /// better than a turn that fails over one.
    private static GlunoConflictDto? ReadConflictMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<GlunoConflictDto>(
                json, Services.Gluno.GlunoJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static GlunoClarificationDto? MapClarification(Models.GlunoClarification? clarification)
    {
        if (clarification == null) return null;

        return new GlunoClarificationDto
        {
            Id = clarification.Id,
            Type = clarification.Type,
            Question = clarification.Question,
            AllowFreeText = clarification.AllowFreeText,
            MultiSelect = clarification.MultiSelect,
            Status = clarification.Status,
            ExpiresAt = clarification.ExpiresAt,
            SelectedKey = clarification.Options
                .FirstOrDefault(option => option.Id == clarification.SelectedOptionId)?.OptionKey,
            // The day, the times and the titles — never the draft id or either
            // version. Those are what the resolve is checked against, and a
            // number the client can see is a number the client can send back.
            Conflict = ReadConflictMeta(clarification.ConflictMetaJson),
            // Keys and labels only. Every entity id stays server-side.
            Options = clarification.Options
                .OrderBy(option => option.SortIndex)
                .Select(option => new GlunoClarificationOptionDto
                {
                    Key = option.OptionKey,
                    Label = option.Label,
                    Description = option.Description,
                    Icon = option.Icon,
                    Disabled = option.Disabled,
                    DisabledReason = option.DisabledReason,
                })
                .ToList(),
        };
    }

    [HttpPost("conversations/{conversationId:guid}/archive")]
    public async Task<IActionResult> ArchiveConversation(Guid conversationId)
    {
        var conversation = await _conversations.GetOwnedAsync(conversationId, GetUserId(), HttpContext.RequestAborted);
        if (conversation == null) return NotFound();

        await _conversations.ArchiveAsync(conversation, HttpContext.RequestAborted);
        return NoContent();
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private static GlunoConversationDto MapConversation(GlunoConversation c) => new()
    {
        Id = c.Id,
        TripId = c.TripId,
        TripTitle = c.Trip?.Title,
        Title = c.Title,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };

    /// <summary>
    /// Maps a page of messages, loading every proposal row for them in ONE
    /// query rather than per message.
    /// </summary>
    private async Task<List<GlunoMessageDto>> MapMessagesAsync(IReadOnlyList<GlunoMessage> messages)
    {
        var assistantIds = messages
            .Where(m => m.Role == GlunoMessageRoles.Assistant)
            .Select(m => m.Id)
            .ToList();

        var records = assistantIds.Count == 0
            ? new List<Models.GlunoProposalRecord>()
            : await _proposals.ListForMessagesAsync(assistantIds, HttpContext.RequestAborted);

        var byMessage = records.GroupBy(r => r.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<Models.GlunoProposalRecord>)g.ToList());

        // The tappable questions these turns asked. Without this a reloaded
        // conversation shows "Which Adventure is this about?" with no options
        // under it — a question the user could have answered with one tap
        // becomes one they have to retype.
        var clarifications = await _clarifications.ListForMessagesAsync(
            assistantIds, GetUserId(), HttpContext.RequestAborted);

        return messages
            .Select(m => MapMessage(
                m,
                byMessage.GetValueOrDefault(m.Id, Array.Empty<Models.GlunoProposalRecord>()),
                clarifications.GetValueOrDefault(m.Id)))
            .ToList();
    }

    private static GlunoMessageDto MapMessage(
        GlunoMessage m,
        IReadOnlyList<Models.GlunoProposalRecord> proposalRecords,
        Models.GlunoClarification? clarification = null)
    {
        var (legacyProposals, places, navigations, sources) = ReadPayload(m);

        // Rows win. The payload copy only still exists for conversations that
        // predate proposals becoming rows; those render as read-only history
        // (Guid.Empty id, stale status) because there is nothing to apply.
        var proposals = proposalRecords.Count > 0
            ? proposalRecords.Select(MapProposalRecord).ToList()
            : legacyProposals;

        return new GlunoMessageDto
        {
            Id = m.Id,
            Role = m.Role,
            Text = m.Text,
            // Computed here rather than stored, so the rule lives in exactly
            // one place and a role added later cannot start leaking into the
            // chat.
            IsRenderable = GlunoMessageRoles.IsRenderable(m.Role),
            Proposals = proposals,
            Places = places,
            Navigations = navigations,
            Sources = sources,
            // So a reloaded conversation renders its cards again instead of
            // showing a question with nothing under it.
            Clarification = MapClarification(clarification),
            CreatedAt = m.CreatedAt,
        };
    }

    private static GlunoProposalDto MapProposalRecord(Models.GlunoProposalRecord record)
    {
        JsonElement payload = default;
        try
        {
            using var document = JsonDocument.Parse(record.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // An unreadable payload still renders as a card with its summary —
            // it just cannot be reviewed field by field.
        }

        return new GlunoProposalDto
        {
            Id = record.Id,
            Kind = KindForAction(record.ActionType),
            ActionType = record.ActionType,
            TripId = record.TripId ?? Guid.Empty,
            Summary = record.Summary,
            Payload = payload,
            PayloadVersion = record.PayloadVersion,
            Status = record.Status,
            FailureCode = record.FailureCode,
            AppliedAt = record.AppliedAt,
        };
    }

    /// The card shape for an action name. Kept here rather than stored, so a
    /// renamed card kind does not need a data migration.
    private static string KindForAction(string actionType) => actionType switch
    {
        GlunoActions.ProposeActivity => "activity",
        GlunoActions.ProposeDayPlan => "day_plan",
        GlunoActions.ProposeDayLocation => "day_location",
        GlunoActions.ProposeActivityMove => "activity_move",
        GlunoActions.ProposeTripDateChange => "trip_dates",
        _ => "unknown",
    };

    private static GlunoApplyChangesDto MapChanges(GlunoApplyChanges changes) => new()
    {
        TripId = changes.TripId,
        CreatedActivityIds = changes.CreatedActivityIds,
        UpdatedActivityIds = changes.UpdatedActivityIds,
        CreatedDayLocationIds = changes.CreatedDayLocationIds,
        UpdatedDayLocationIds = changes.UpdatedDayLocationIds,
        TripDatesChanged = changes.TripDatesChanged,
        AffectedDates = changes.AffectedDates,
    };

    /// <summary>
    /// Reads an assistant turn's attachments.
    ///
    /// Two stored shapes: the current envelope ({proposals, places}) and the
    /// bare proposals array written before places existed. Both are read, so
    /// an existing conversation keeps rendering its proposal cards — history
    /// is never rewritten, only read forgivingly.
    /// </summary>
    private static (
        List<GlunoProposalDto> Proposals,
        List<GlunoPlaceDto> Places,
        List<GlunoNavigationDto> Navigations,
        List<GlunoSourceDto> Sources)
        ReadPayload(GlunoMessage message)
    {
        var empty = (
            new List<GlunoProposalDto>(),
            new List<GlunoPlaceDto>(),
            new List<GlunoNavigationDto>(),
            new List<GlunoSourceDto>());

        if (message.Role != GlunoMessageRoles.Assistant || string.IsNullOrWhiteSpace(message.PayloadJson))
            return empty;

        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Legacy: a bare proposals array.
                var legacy = JsonSerializer.Deserialize<List<GlunoProposal>>(message.PayloadJson, GlunoJson.Options);
                return (
                    legacy?.Select(MapProposal).ToList() ?? new List<GlunoProposalDto>(),
                    new List<GlunoPlaceDto>(),
                    new List<GlunoNavigationDto>(),
                    new List<GlunoSourceDto>());
            }

            var payload = JsonSerializer.Deserialize<GlunoAssistantPayload>(message.PayloadJson, GlunoJson.Options);
            if (payload == null) return empty;

            return (
                payload.Proposals.Select(MapProposal).ToList(),
                // Indexed, so each place carries a stable server-generated key
                // and the app never has to send a provider id back.
                payload.Places.Select((place, index) => MapPlace(place, index)).ToList(),
                // A target this build no longer knows is dropped rather than
                // sent on — an unopenable button is worse than no button.
                payload.Navigations
                    .Where(navigation => GlunoNavigationTargets.IsKnown(navigation.TargetId))
                    .Select(MapNavigation)
                    .ToList(),
                payload.Sources.Select(MapSource).ToList());
        }
        catch (JsonException)
        {
            // A payload written by an older or malformed shape must never
            // break the chat history — the turn's text still renders.
            return empty;
        }
    }

    private static GlunoSourceDto MapSource(GlunoSourceCard card) => new()
    {
        Kind = card.Kind,
        Label = card.Label,
        Supports = card.Supports,
        VerifiedAt = card.VerifiedAtUtc,
        IsStale = card.IsStale,
        Provider = card.Provider,
    };

    private static GlunoNavigationDto MapNavigation(GlunoNavigationCard card) => new()
    {
        TargetId = card.TargetId,
        Label = card.Label,
        Reason = card.Reason,
        TripId = card.TripId,
        ActivityId = card.ActivityId,
        Date = card.Date,
    };

    /// A proposal from before proposals became rows. It has no id to apply
    /// against, so it is surfaced as stale: visible as history, never
    /// actionable.
    private static GlunoProposalDto MapProposal(GlunoProposal p) => new()
    {
        Id = Guid.Empty,
        Kind = p.Kind,
        ActionType = p.ActionName,
        TripId = p.TripId,
        Summary = p.Summary,
        Payload = p.Payload,
        PayloadVersion = 0,
        Status = Models.GlunoProposalStatuses.Stale,
    };

    private static GlunoPlaceDto MapPlace(GlunoPlaceCard place, int index) => new()
    {
        // Positional and scoped to this message. A tap sends this back, never
        // a provider id or a name, so it cannot reach a place the conversation
        // never showed.
        OptionKey = GlunoPlaceOptions.KeyFor(index),
        Provider = place.Provider,
        ExternalId = place.ExternalId,
        Name = place.Name,
        Category = place.Category,
        CategoryLabel = place.CategoryLabel,
        Address = place.Address,
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
        OpeningHours = place.OpeningHours.ToList(),
        ReviewSummary = place.ReviewSummary,
        Signals = place.Signals.ToList(),
    };
}
