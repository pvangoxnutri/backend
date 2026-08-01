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
        ILogger<GlunoController> logger)
    {
        _liveTravel = liveTravel;
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
        });
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

        return messages
            .Select(m => MapMessage(m, byMessage.GetValueOrDefault(m.Id, Array.Empty<Models.GlunoProposalRecord>())))
            .ToList();
    }

    private static GlunoMessageDto MapMessage(
        GlunoMessage m, IReadOnlyList<Models.GlunoProposalRecord> proposalRecords)
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
                payload.Places.Select(MapPlace).ToList(),
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

    private static GlunoPlaceDto MapPlace(GlunoPlaceCard place) => new()
    {
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
