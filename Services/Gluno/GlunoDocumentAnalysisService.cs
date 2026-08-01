using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

public enum GlunoAnalysisError
{
    None,
    NotConfigured,
    NotFound,
    Forbidden,
    UnsupportedFormat,
    FileTooLarge,
    AlreadyRunning,
    UsageLimit,
    ProviderFailed,
    Cancelled,
}

public sealed record GlunoAnalysisResult(GlunoAnalysisError Error, GlunoDocumentAnalysis? Analysis)
{
    /// A stable code the app localises. Never a provider message.
    public string? FailureCode { get; init; }

    /// True when an existing analysis of the same bytes was returned instead of
    /// running a new one.
    public bool WasReplay { get; init; }
}

public interface IGlunoDocumentAnalysisService
{
    Task<GlunoAnalysisResult> StartAsync(Guid documentId, Guid userId, CancellationToken ct);
    Task<GlunoAnalysisResult> GetAsync(Guid analysisId, Guid userId, CancellationToken ct);
    Task<GlunoAnalysisResult> CancelAsync(Guid analysisId, Guid userId, CancellationToken ct);
    Task<GlunoAnalysisResult> MarkReviewedAsync(Guid analysisId, Guid userId, CancellationToken ct);
}

/// <summary>
/// Reads a document the user explicitly asked Gluno to read.
///
/// THE SHAPE OF THE FLOW, and why it is this shape. The document is already in
/// SideQuest through the ordinary upload path. Nothing happens to it until the
/// user taps "Analyze with Gluno" — analysis is never automatic, never on
/// upload, never in the background. What comes back is a proposal-shaped
/// result the user reviews and selects from. At no point does a document write
/// to an Adventure.
///
/// That is not caution for its own sake. An extraction is a machine's reading
/// of a photograph of a booking; it is right most of the time and confidently
/// wrong the rest, and the failure mode is a wrong flight time in somebody's
/// itinerary that they believe they checked.
///
/// AUTHORISATION IS CHECKED THREE WAYS, every call: the document exists, it
/// belongs to a trip, and the caller is a member of THAT trip. A client-supplied
/// document id proves nothing on its own — it is a Guid someone can type.
///
/// THE FILE NEVER LEAVES AS A URL. It is downloaded server-side through the
/// existing private storage path and passed to the model as bytes. No signed
/// URL, no object path, no storage key reaches the app or the provider.
/// </summary>
public sealed class GlunoDocumentAnalysisService : IGlunoDocumentAnalysisService
{
    private readonly AppDbContext _db;
    private readonly GlunoDocumentConfig _config;
    private readonly ITripDocumentStorageService _storage;
    private readonly IGlunoDocumentReader _reader;
    private readonly GlunoDocumentValidator _validator;
    private readonly GlunoUsageBudget _usage;
    private readonly ILogger<GlunoDocumentAnalysisService> _logger;

    public GlunoDocumentAnalysisService(
        AppDbContext db,
        GlunoDocumentConfig config,
        ITripDocumentStorageService storage,
        IGlunoDocumentReader reader,
        GlunoDocumentValidator validator,
        GlunoUsageBudget usage,
        ILogger<GlunoDocumentAnalysisService> logger)
    {
        _db = db;
        _config = config;
        _storage = storage;
        _reader = reader;
        _validator = validator;
        _usage = usage;
        _logger = logger;
    }

    public async Task<GlunoAnalysisResult> StartAsync(Guid documentId, Guid userId, CancellationToken ct)
    {
        if (!_config.IsEnabled)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.NotConfigured, null)
            {
                FailureCode = _config.UnavailableReason,
            };
        }

        // ── Authorisation, before anything expensive ──────────────────────
        var document = await LoadAuthorisedDocumentAsync(documentId, userId, ct);
        if (document == null) return new GlunoAnalysisResult(GlunoAnalysisError.Forbidden, null);

        // Rate limits BEFORE the download. A user over their ceiling must not
        // cost us a storage round trip, let alone a model call.
        if (_usage.CheckAllowed(userId) != GlunoUsageVerdict.Allowed)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.UsageLimit, null)
            {
                FailureCode = GlunoFailureCodes.UserUsageLimit,
            };
        }

        // One analysis per document at a time. Two concurrent reads of the same
        // file cost twice and produce two results the user has to reconcile.
        var running = await _db.GlunoDocumentAnalyses
            .FirstOrDefaultAsync(
                analysis => analysis.DocumentId == documentId
                    && (analysis.Status == GlunoDocumentAnalysisStatuses.Pending
                        || analysis.Status == GlunoDocumentAnalysisStatuses.Processing),
                ct);

        if (running != null)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.AlreadyRunning, running);
        }

        // ── Fetch and validate the bytes ──────────────────────────────────
        byte[] content;
        try
        {
            content = await _storage.DownloadAsync(document.StoragePath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.Cancelled, null)
            {
                FailureCode = GlunoFailureCodes.Cancelled,
            };
        }
        catch (Exception ex)
        {
            // Category only. A storage exception message carries the object
            // path, and the object path is the private location of somebody's
            // booking confirmation.
            _logger.LogWarning("[GLUNO] document fetch failed: {Category}", ex.GetType().Name);
            return new GlunoAnalysisResult(GlunoAnalysisError.ProviderFailed, null)
            {
                FailureCode = "document_unavailable",
            };
        }

        // The bytes decide the format — never the filename, never the stored
        // content type, both of which the uploader controls.
        var check = GlunoDocumentFile.Inspect(content, _config.MaxFileSizeBytes);
        if (!check.IsSupported)
        {
            _logger.LogInformation(
                "[GLUNO] document rejected reason={Reason} size={SizeBucket}",
                check.RejectionCode, GlunoDocumentFile.SizeBucket(content.LongLength));

            return new GlunoAnalysisResult(
                check.RejectionCode == "too_large"
                    ? GlunoAnalysisError.FileTooLarge
                    : GlunoAnalysisError.UnsupportedFormat,
                null)
            {
                FailureCode = check.RejectionCode,
            };
        }

        // ── Idempotency on the CONTENT ────────────────────────────────────
        //
        // Same bytes, existing completed result: return it. Re-reading an
        // unchanged file costs money and produces the same answer.
        var existing = await _db.GlunoDocumentAnalyses
            .Where(analysis => analysis.DocumentId == documentId
                && analysis.SourceFileHash == check.Sha256
                && analysis.Status == GlunoDocumentAnalysisStatuses.Completed
                && analysis.SupersededAt == null)
            .OrderByDescending(analysis => analysis.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.None, existing) { WasReplay = true };
        }

        // Different bytes: the document was replaced. Everything read from the
        // old file describes something that no longer exists.
        await SupersedePreviousAsync(documentId, check.Sha256!, ct);

        var row = new GlunoDocumentAnalysis
        {
            DocumentId = documentId,
            TripId = document.TripId,
            UserId = userId,
            ExtractionVersion = GlunoDocumentExtraction.CurrentVersion,
            Status = GlunoDocumentAnalysisStatuses.Processing,
            SourceFileHash = check.Sha256!,
            ProviderModel = _config.Model,
        };

        _db.GlunoDocumentAnalyses.Add(row);
        await _db.SaveChangesAsync(ct);

        return await RunAsync(row, document, content, check, ct);
    }

    private async Task<GlunoAnalysisResult> RunAsync(
        GlunoDocumentAnalysis row,
        TripDocument document,
        byte[] content,
        GlunoFileCheck check,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        // The provider's own ceiling, linked to the caller's token so a user
        // who cancels stops the upstream call too.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_config.Timeout);

        try
        {
            var extraction = await _reader.ReadAsync(
                new GlunoDocumentReadRequest
                {
                    Content = content,
                    MediaType = check.MediaType,
                    Format = check.Format,
                    MaxPages = _config.MaxPages,
                    Model = _config.Model!,
                    Language = "en",
                },
                timeout.Token);

            var trip = await _db.Trips.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == row.TripId, ct);

            // Validated against the Adventure it belongs to — a check-out
            // before a check-in, a date outside the trip, a duplicate of an
            // Activity already planned.
            var validation = trip == null
                ? null
                : _validator.Validate(new GlunoDocumentValidationInput
                {
                    Items = extraction.Items,
                    TripStart = trip.StartDate,
                    TripEnd = trip.EndDate,
                    ExistingActivities = await LoadActivityContextAsync(row.TripId, ct),
                    KnownConfirmationNumbers = await LoadKnownConfirmationsAsync(row.TripId, row.Id, ct),
                });

            row.Status = GlunoDocumentAnalysisStatuses.Completed;
            row.CompletedAt = DateTime.UtcNow;
            row.StructuredResultJson = JsonSerializer.Serialize(
                new GlunoStoredAnalysis { Extraction = extraction, Validation = validation },
                GlunoJson.Options);

            if (_config.StoreRawText && extraction.Warnings.Count > 0)
            {
                // Warnings only, never the document. An excerpt of somebody's
                // ticket is still their ticket.
                row.RawTextExcerpt = string.Join("; ", extraction.Warnings).TrimTo(2000);
            }

            await _db.SaveChangesAsync(ct);

            // Buckets and counts. Never a name, a date, a place, a price or a
            // booking reference — the entire content of the document is
            // exactly what must not appear here.
            _logger.LogInformation(
                "[GLUNO] document analysed type={Format} pages={PageBucket} size={SizeBucket} " +
                "items={Items} blockers={Blockers} confidence={Confidence} qr={Qr} injection={Injection} " +
                "durationMs={Duration}",
                check.Format,
                GlunoDocumentFile.PageBucket(extraction.PagesAnalysed),
                GlunoDocumentFile.SizeBucket(content.LongLength),
                extraction.Items.Count,
                validation?.Blockers.Count ?? 0,
                GlunoDocumentConfidence.Bucket(
                    extraction.Items.Count == 0 ? 0 : extraction.Items.Average(item => item.Confidence)),
                extraction.ContainsQrCode,
                extraction.ContainsInjectionAttempt,
                (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);

            return new GlunoAnalysisResult(GlunoAnalysisError.None, row);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user stopped it. Recorded on CancellationToken.None — the
            // request's own token is exactly what was cancelled, and using it
            // here would leave the row stuck at "processing" forever.
            await FinishAsync(row, GlunoDocumentAnalysisStatuses.Cancelled, GlunoFailureCodes.Cancelled);

            return new GlunoAnalysisResult(GlunoAnalysisError.Cancelled, row)
            {
                FailureCode = GlunoFailureCodes.Cancelled,
            };
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(row, GlunoDocumentAnalysisStatuses.Failed, GlunoFailureCodes.AiTimeout);

            return new GlunoAnalysisResult(GlunoAnalysisError.ProviderFailed, row)
            {
                FailureCode = GlunoFailureCodes.AiTimeout,
            };
        }
        catch (Exception ex)
        {
            var code = GlunoFailureCodes.FromException(ex);
            _logger.LogWarning("[GLUNO] document analysis failed: {Category}", ex.GetType().Name);

            await FinishAsync(row, GlunoDocumentAnalysisStatuses.Failed, code);

            return new GlunoAnalysisResult(GlunoAnalysisError.ProviderFailed, row) { FailureCode = code };
        }
        finally
        {
            _usage.Record(row.UserId, new GlunoTurnUsage { ProviderCalls = 1 });
        }
    }

    public async Task<GlunoAnalysisResult> GetAsync(Guid analysisId, Guid userId, CancellationToken ct)
    {
        var analysis = await LoadAuthorisedAnalysisAsync(analysisId, userId, ct);
        if (analysis == null) return new GlunoAnalysisResult(GlunoAnalysisError.Forbidden, null);

        // The document may have been replaced since. A result describing bytes
        // that no longer exist is stale, and saying so is the whole point of
        // keeping the hash.
        var currentHash = await _db.GlunoDocumentAnalyses
            .Where(other => other.DocumentId == analysis.DocumentId && other.CreatedAt > analysis.CreatedAt)
            .AnyAsync(ct);

        if (currentHash && analysis.SupersededAt == null)
        {
            analysis.SupersededAt = DateTime.UtcNow;
            analysis.Status = GlunoDocumentAnalysisStatuses.Superseded;
            await _db.SaveChangesAsync(ct);
        }

        return new GlunoAnalysisResult(GlunoAnalysisError.None, analysis);
    }

    public async Task<GlunoAnalysisResult> CancelAsync(Guid analysisId, Guid userId, CancellationToken ct)
    {
        var analysis = await LoadAuthorisedAnalysisAsync(analysisId, userId, ct);
        if (analysis == null) return new GlunoAnalysisResult(GlunoAnalysisError.Forbidden, null);

        if (!GlunoDocumentAnalysisStatuses.IsTerminal(analysis.Status))
        {
            analysis.Status = GlunoDocumentAnalysisStatuses.Cancelled;
            analysis.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new GlunoAnalysisResult(GlunoAnalysisError.None, analysis);
    }

    /// <summary>
    /// Records that a human has actually read the result.
    ///
    /// Load-bearing rather than cosmetic: until this is set, nothing from the
    /// document may enter Gluno's Adventure context. A machine's reading of a
    /// photograph becomes a fact about the trip only once its owner agrees.
    /// </summary>
    public async Task<GlunoAnalysisResult> MarkReviewedAsync(Guid analysisId, Guid userId, CancellationToken ct)
    {
        var analysis = await LoadAuthorisedAnalysisAsync(analysisId, userId, ct);
        if (analysis == null) return new GlunoAnalysisResult(GlunoAnalysisError.Forbidden, null);

        if (analysis.Status != GlunoDocumentAnalysisStatuses.Completed)
        {
            return new GlunoAnalysisResult(GlunoAnalysisError.NotFound, analysis);
        }

        analysis.UserReviewedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new GlunoAnalysisResult(GlunoAnalysisError.None, analysis);
    }

    // ── Authorisation ─────────────────────────────────────────────────────

    /// <summary>
    /// The document, only if this user is a member of ITS trip.
    ///
    /// Three facts checked together in one query: the document exists, it has a
    /// trip, and the caller is on that trip. A client-supplied document id is a
    /// Guid anyone can type, and on its own proves nothing at all.
    /// </summary>
    private Task<TripDocument?> LoadAuthorisedDocumentAsync(Guid documentId, Guid userId, CancellationToken ct)
        => _db.TripDocuments
            .Where(document => document.Id == documentId)
            .Where(document => _db.TripMembers.Any(member =>
                member.TripId == document.TripId && member.UserId == userId))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The analysis, only if this user is STILL a member.
    ///
    /// Re-checked on every read rather than trusted from the start call:
    /// somebody removed from an Adventure mid-analysis must not keep reading
    /// its documents.
    /// </summary>
    private Task<GlunoDocumentAnalysis?> LoadAuthorisedAnalysisAsync(
        Guid analysisId, Guid userId, CancellationToken ct)
        => _db.GlunoDocumentAnalyses
            .Where(analysis => analysis.Id == analysisId)
            .Where(analysis => _db.TripMembers.Any(member =>
                member.TripId == analysis.TripId && member.UserId == userId))
            .FirstOrDefaultAsync(ct);

    private async Task SupersedePreviousAsync(Guid documentId, string newHash, CancellationToken ct)
    {
        var previous = await _db.GlunoDocumentAnalyses
            .Where(analysis => analysis.DocumentId == documentId
                && analysis.SourceFileHash != newHash
                && analysis.SupersededAt == null)
            .ToListAsync(ct);

        foreach (var analysis in previous)
        {
            analysis.SupersededAt = DateTime.UtcNow;
            analysis.Status = GlunoDocumentAnalysisStatuses.Superseded;
        }

        if (previous.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(GlunoDocumentAnalysis row, string status, string? failureCode)
    {
        row.Status = status;
        row.CompletedAt = DateTime.UtcNow;
        row.FailureCode = failureCode;

        // CancellationToken.None deliberately — see CancelAsync in the
        // idempotency store for the same reasoning.
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<GlunoActivityContext>> LoadActivityContextAsync(
        Guid tripId, CancellationToken ct)
        => await _db.TripActivities
            .AsNoTracking()
            .Where(activity => activity.TripId == tripId)
            .Select(activity => new GlunoActivityContext
            {
                Id = activity.Id,
                Title = activity.Title,
                Date = activity.Date,
                EndDate = activity.EndDate,
                Time = activity.Time,
                Category = activity.Category,
            })
            .ToListAsync(ct);

    /// <summary>
    /// Confirmation numbers already seen in this Adventure's other analyses.
    ///
    /// Read out of the stored results rather than kept in a column: a column of
    /// booking references is a table nobody should have, and this list lives
    /// only for the length of one validation.
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadKnownConfirmationsAsync(
        Guid tripId, Guid excludeAnalysisId, CancellationToken ct)
    {
        var rows = await _db.GlunoDocumentAnalyses
            .AsNoTracking()
            .Where(analysis => analysis.TripId == tripId
                && analysis.Id != excludeAnalysisId
                && analysis.Status == GlunoDocumentAnalysisStatuses.Completed
                && analysis.StructuredResultJson != null)
            .Select(analysis => analysis.StructuredResultJson!)
            .Take(50)
            .ToListAsync(ct);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var json in rows)
        {
            try
            {
                var stored = JsonSerializer.Deserialize<GlunoStoredAnalysis>(json, GlunoJson.Options);
                foreach (var item in stored?.Extraction.Items ?? [])
                {
                    if (item.ConfirmationNumber is { } confirmation) known.Add(confirmation.Trim());
                }
            }
            catch (JsonException)
            {
                // A malformed stored row must not break a new analysis.
            }
        }

        return known;
    }
}

/// <summary>What actually goes in the StructuredResultJson column.</summary>
public sealed class GlunoStoredAnalysis
{
    public GlunoDocumentExtraction Extraction { get; set; } = new();
    public GlunoDocumentValidationResult? Validation { get; set; }
}

internal static class GlunoDocumentStringExtensions
{
    public static string TrimTo(this string value, int max)
        => value.Length <= max ? value : value[..max];
}
