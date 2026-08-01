using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;

namespace sidequest.backend.Controllers;

/// <summary>
/// Reading the documents a user has already put in their Adventure.
///
/// Every endpoint is [Authorize] and every one re-checks membership of the
/// document's OWN trip. A document id in a request body proves nothing — it is
/// a Guid anyone can type — so the authorisation query joins document → trip →
/// membership every time, including on reads of a result the same user started.
/// Somebody removed from an Adventure mid-analysis stops being able to read it.
///
/// Nothing here returns a storage path, a signed URL, the provider's raw
/// response, or the document's text.
/// </summary>
[ApiController]
[Route("api/gluno/documents")]
[Authorize]
public class GlunoDocumentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IGlunoDocumentAnalysisService _analysis;
    private readonly GlunoDocumentConfig _config;
    private readonly IGlunoProposalStore _proposals;
    private readonly IGlunoConversationService _conversations;

    public GlunoDocumentsController(
        AppDbContext db,
        IGlunoDocumentAnalysisService analysis,
        GlunoDocumentConfig config,
        IGlunoProposalStore proposals,
        IGlunoConversationService conversations)
    {
        _db = db;
        _analysis = analysis;
        _config = config;
        _proposals = proposals;
        _conversations = conversations;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string Language()
        => Request.Headers.AcceptLanguage.ToString().StartsWith("sv", StringComparison.OrdinalIgnoreCase)
            ? "sv"
            : "en";

    /// <summary>
    /// Whether document analysis can be used at all. The app asks before
    /// showing the action, so a build never offers a button that cannot work.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<GlunoDocumentStatusDto> GetStatus()
        => Ok(new GlunoDocumentStatusDto
        {
            Available = _config.IsEnabled,
            Reason = _config.UnavailableReason,
            // Format support is a server fact — the app should not maintain its
            // own list and drift from ours.
            SupportedFormats = ["pdf", "jpeg", "png", "webp"],
            MaxFileSizeBytes = _config.MaxFileSizeBytes,
        });

    /// <summary>
    /// Starts an analysis, or returns the existing one for the same bytes.
    ///
    /// Idempotent on the file's CONTENT, not on the request: re-reading an
    /// unchanged document costs money and produces the same answer.
    /// </summary>
    [HttpPost("{documentId:guid}/analyze")]
    public async Task<ActionResult<GlunoDocumentAnalysisDto>> Analyze(Guid documentId)
    {
        var result = await _analysis.StartAsync(documentId, GetUserId(), HttpContext.RequestAborted);
        return MapResult(result);
    }

    [HttpGet("analyses/{analysisId:guid}")]
    public async Task<ActionResult<GlunoDocumentAnalysisDto>> GetAnalysis(Guid analysisId)
    {
        var result = await _analysis.GetAsync(analysisId, GetUserId(), HttpContext.RequestAborted);
        return MapResult(result);
    }

    [HttpPost("analyses/{analysisId:guid}/cancel")]
    public async Task<ActionResult<GlunoDocumentAnalysisDto>> Cancel(Guid analysisId)
    {
        var result = await _analysis.CancelAsync(analysisId, GetUserId(), HttpContext.RequestAborted);
        return MapResult(result);
    }

    [HttpPost("analyses/{analysisId:guid}/reviewed")]
    public async Task<ActionResult<GlunoDocumentAnalysisDto>> MarkReviewed(Guid analysisId)
    {
        var result = await _analysis.MarkReviewedAsync(analysisId, GetUserId(), HttpContext.RequestAborted);
        return MapResult(result);
    }

    /// <summary>
    /// Turns the items the user selected into ordinary Gluno proposals.
    ///
    /// The selection is explicit — no "accept all" shortcut. From here the
    /// normal proposal rules apply: review, edit, staleness check, and an
    /// explicit tap to apply. Nothing is written by this endpoint.
    /// </summary>
    [HttpPost("analyses/{analysisId:guid}/proposals")]
    public async Task<ActionResult<List<GlunoProposalDto>>> CreateProposals(
        Guid analysisId, [FromBody] GlunoDocumentProposalRequestDto dto)
    {
        var ct = HttpContext.RequestAborted;
        var userId = GetUserId();

        var result = await _analysis.GetAsync(analysisId, userId, ct);
        if (result.Error == GlunoAnalysisError.Forbidden) return Forbid();
        if (result.Analysis is not { } analysis) return NotFound();

        if (analysis.Status != GlunoDocumentAnalysisStatuses.Completed)
        {
            return BadRequest(new { error = "analysis_not_completed" });
        }

        if (analysis.SupersededAt != null)
        {
            // The document changed. Everything read from the old file describes
            // something that no longer exists.
            return Conflict(new { error = "analysis_superseded" });
        }

        var stored = ReadStored(analysis);
        if (stored == null) return BadRequest(new { error = "analysis_unreadable" });

        var selected = (dto.ItemIds ?? []).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return BadRequest(new { error = "no_items_selected" });

        var proposals = GlunoDocumentProposalMapper.Build(
            stored.Extraction, selected, analysis.TripId, analysis.DocumentId, analysis.Id, Language());

        if (proposals.Count == 0) return BadRequest(new { error = "no_usable_items" });

        // Proposals belong to a conversation, so they show up in the chat
        // alongside everything else Gluno has suggested — one place to review
        // pending changes rather than two.
        var conversation = await _conversations.GetLatestForScopeAsync(userId, analysis.TripId, ct)
            ?? await _conversations.CreateAsync(userId, analysis.TripId, ct);

        var message = await _conversations.AppendAsync(new GlunoMessage
        {
            ConversationId = conversation.Id,
            Role = GlunoMessageRoles.Assistant,
            Text = Language() == "sv"
                ? "Jag har förberett förslag från dokumentet. Granska dem innan du sparar."
                : "I've prepared suggestions from the document. Review them before saving.",
        }, ct);

        var records = new List<GlunoProposalRecord>(proposals.Count);
        foreach (var proposal in proposals)
        {
            records.Add(await _proposals.CreateAsync(conversation, message.Id, proposal, ct));
        }

        // Marking it reviewed here is honest: the user selected specific items,
        // which means they read the result.
        await _analysis.MarkReviewedAsync(analysisId, userId, ct);

        return Ok(records.Select(MapProposal).ToList());
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private ActionResult<GlunoDocumentAnalysisDto> MapResult(GlunoAnalysisResult result)
    {
        switch (result.Error)
        {
            case GlunoAnalysisError.NotConfigured:
                return StatusCode(503, new { error = result.FailureCode ?? "not_configured", retryable = false });
            case GlunoAnalysisError.Forbidden:
                return Forbid();
            case GlunoAnalysisError.NotFound:
                return NotFound();
            case GlunoAnalysisError.UnsupportedFormat:
            case GlunoAnalysisError.FileTooLarge:
                return BadRequest(new { error = result.FailureCode ?? "unsupported_format", retryable = false });
            case GlunoAnalysisError.UsageLimit:
                return StatusCode(429, new { error = result.FailureCode, retryable = false });
            case GlunoAnalysisError.Cancelled:
                return StatusCode(499, new { error = GlunoFailureCodes.Cancelled, retryable = false });
        }

        if (result.Analysis is not { } analysis) return NotFound();

        return Ok(MapAnalysis(analysis, result.WasReplay));
    }

    private GlunoDocumentAnalysisDto MapAnalysis(GlunoDocumentAnalysis analysis, bool wasReplay)
    {
        var stored = ReadStored(analysis);

        return new GlunoDocumentAnalysisDto
        {
            Id = analysis.Id,
            DocumentId = analysis.DocumentId,
            Status = analysis.Status,
            FailureCode = analysis.FailureCode,
            ExtractionVersion = analysis.ExtractionVersion,
            CreatedAt = analysis.CreatedAt,
            CompletedAt = analysis.CompletedAt,
            ReviewedAt = analysis.UserReviewedAt,
            IsSuperseded = analysis.SupersededAt != null,
            WasReplay = wasReplay,
            ContainsQrCode = stored?.Extraction.ContainsQrCode ?? false,
            LinkHosts = stored?.Extraction.LinkHosts.ToList() ?? [],
            RequiresReview = stored?.Validation?.RequiresUserReview ?? false,
            Items = stored?.Extraction.Items.Select(item => MapItem(item, stored.Validation)).ToList() ?? [],
        };
    }

    /// <summary>
    /// One extracted item, as the review screen sees it.
    ///
    /// Note the omissions. The confirmation number is MASKED — the full value
    /// stays server-side, and the last four digits are enough to recognise the
    /// right booking. There is no raw text, no JSON, no per-field internals: a
    /// review screen full of machine output is not a review screen.
    /// </summary>
    private static GlunoDocumentItemDto MapItem(
        GlunoExtractedItem item, GlunoDocumentValidationResult? validation)
        => new()
        {
            Id = item.Id,
            Type = item.Type,
            Title = item.Title,
            Provider = item.Provider,
            MaskedConfirmation = item.MaskedConfirmation(),
            BookingStatus = item.BookingStatus,
            StartDate = item.Start?.NormalisedDate ?? item.CheckIn?.NormalisedDate,
            StartTime = item.Start?.NormalisedTime ?? item.CheckIn?.NormalisedTime,
            EndDate = item.End?.NormalisedDate ?? item.CheckOut?.NormalisedDate,
            EndTime = item.End?.NormalisedTime ?? item.CheckOut?.NormalisedTime,
            TimeZoneId = item.Start?.TimeZoneId,
            // The document's own words, so the user can compare against what we
            // read — the only way an ambiguous date can honestly be resolved.
            StartDateOriginalText = item.Start?.OriginalText ?? item.CheckIn?.OriginalText,
            AlternativeDateReadings = (item.Start ?? item.CheckIn)?.AlternativeReadings.ToList() ?? [],
            DepartureLocation = item.DepartureLocation,
            ArrivalLocation = item.ArrivalLocation,
            Address = item.Address,
            ConfidenceBucket = GlunoDocumentConfidence.Bucket(item.Confidence),
            Warnings = validation?.Warnings
                .Where(warning => warning.ItemId == item.Id)
                .Select(warning => warning.Message)
                .ToList() ?? [],
            Blockers = validation?.Blockers
                .Where(blocker => blocker.ItemId == item.Id)
                .Select(blocker => blocker.Message)
                .ToList() ?? [],
            IsPossibleDuplicate = validation?.PossibleDuplicates.Any(duplicate => duplicate.ItemId == item.Id) ?? false,
        };

    private static GlunoStoredAnalysis? ReadStored(GlunoDocumentAnalysis analysis)
    {
        if (string.IsNullOrWhiteSpace(analysis.StructuredResultJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<GlunoStoredAnalysis>(
                analysis.StructuredResultJson, GlunoJson.Options);
        }
        catch (JsonException)
        {
            // A malformed stored row renders as "no items" rather than breaking
            // the document screen.
            return null;
        }
    }

    private static GlunoProposalDto MapProposal(GlunoProposalRecord record)
    {
        JsonElement payload = default;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(record.PayloadJson);
        }
        catch (JsonException)
        {
            // Same rule as the chat's own mapper: a payload that cannot be read
            // renders as an empty card, never as a crash.
        }

        return new GlunoProposalDto
        {
            Id = record.Id,
            // Everything a document produces is a single Activity — a document
            // never supports a whole day plan, only the bookings it states.
            Kind = "activity",
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
}
