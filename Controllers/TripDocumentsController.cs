using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Authorize]
[Route("api/trips/{tripId:guid}/documents")]
public class TripDocumentsController : ControllerBase
{
    private const long MaxFileBytes = 10L * 1024L * 1024L;
    private const long MaxRequestBytes = 11L * 1024L * 1024L;

    private static readonly HashSet<string> AllowedCategories =
    [
        "car_rental",
        "flight",
        "train",
        "ferry",
        "accommodation",
        "insurance",
        "booking",
        "identification_visa",
        "other",
    ];

    private readonly AppDbContext _db;
    private readonly ILogger<TripDocumentsController> _logger;

    public TripDocumentsController(
        AppDbContext db,
        ILogger<TripDocumentsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<TripDocumentDto>>> GetDocuments(
        Guid tripId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], cancellationToken);
        if (trip == null) return NotFound();
        if (!await IsMemberAsync(tripId, userId, cancellationToken)) return Forbid();

        var canEditTrip = await CanEditTripAsync(trip, userId, cancellationToken);
        var documents = await _db.TripDocuments
            .AsNoTracking()
            .Where(d => d.TripId == tripId)
            .Include(d => d.CreatedBy)
            .Include(d => d.Activity)
            .OrderByDescending(d => d.Date)
            .ThenByDescending(d => d.UploadedAt)
            .ToListAsync(cancellationToken);

        return Ok(documents
            .Select(d => MapDocument(d, userId, canEditTrip))
            .ToList());
    }

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<ActionResult<TripDocumentDto>> CreateDocument(
        Guid tripId,
        [FromForm] CreateTripDocumentDto dto,
        [FromServices] ITripDocumentStorageService storage,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], cancellationToken);
        if (trip == null) return NotFound();
        if (!await IsMemberAsync(tripId, userId, cancellationToken)) return Forbid();

        if (dto.File == null || dto.File.Length == 0)
            return BadRequest(new { error = "document_file_required" });
        if (dto.File.Length > MaxFileBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                error = "document_too_large",
                maxBytes = MaxFileBytes,
            });

        var name = NormalizeRequired(dto.Name);
        if (name == null || name.Length > 200)
            return BadRequest(new { error = "invalid_document_name" });
        if (!TryNormalizeCategory(dto.Category, out var category))
            return BadRequest(new { error = "invalid_document_category" });

        TripActivity? activity = null;
        if (dto.ActivityId.HasValue)
        {
            activity = await FindActivityAsync(tripId, dto.ActivityId.Value, cancellationToken);
            if (activity == null)
                return BadRequest(new { error = "invalid_document_activity" });
        }

        ValidatedTripDocumentFile? validatedFile;
        await using (var validationStream = dto.File.OpenReadStream())
        {
            validatedFile = await TripDocumentFileValidator.DetectAsync(validationStream, cancellationToken);
        }

        if (validatedFile == null)
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new
            {
                error = "invalid_document_type",
                allowedTypes = new[] { "application/pdf", "image/jpeg", "image/png", "image/webp" },
            });
        }

        var creator = await _db.Users.FindAsync([userId], cancellationToken);
        if (creator == null) return Unauthorized();

        var document = new TripDocument
        {
            Id = Guid.NewGuid(),
            TripId = tripId,
            Trip = trip,
            Name = name,
            Category = category,
            FileType = validatedFile.ContentType,
            FileSize = dto.File.Length,
            Date = dto.Date,
            ActivityId = activity?.Id,
            Activity = activity,
            Note = NormalizeOptional(dto.Note),
            BookingReference = NormalizeOptional(dto.BookingReference),
            CreatedByUserId = userId,
            CreatedBy = creator,
            UploadedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        document.StoragePath = BuildStoragePath(tripId, document.Id, validatedFile.Extension);

        var uploaded = false;
        try
        {
            await using var uploadStream = dto.File.OpenReadStream();
            await storage.UploadAsync(
                uploadStream,
                document.FileType,
                document.StoragePath,
                cancellationToken);
            uploaded = true;

            _db.TripDocuments.Add(document);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (TripDocumentStorageException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "document_storage_unavailable",
            });
        }
        catch (DbUpdateException)
        {
            await RollBackUploadedObjectAsync(document, uploaded, storage);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "document_metadata_save_failed",
            });
        }
        catch (OperationCanceledException)
        {
            await RollBackUploadedObjectAsync(document, uploaded, storage);
            throw;
        }
        catch
        {
            await RollBackUploadedObjectAsync(document, uploaded, storage);
            _logger.LogError("Document metadata save failed after upload.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "document_metadata_save_failed",
            });
        }

        var canEditTrip = await CanEditTripAsync(trip, userId, cancellationToken);
        return StatusCode(
            StatusCodes.Status201Created,
            MapDocument(document, userId, canEditTrip));
    }

    [HttpPatch("{documentId:guid}")]
    public async Task<ActionResult<TripDocumentDto>> UpdateDocument(
        Guid tripId,
        Guid documentId,
        [FromBody] UpdateTripDocumentDto dto,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], cancellationToken);
        if (trip == null) return NotFound();
        if (!await IsMemberAsync(tripId, userId, cancellationToken)) return Forbid();

        var document = await _db.TripDocuments
            .Include(d => d.CreatedBy)
            .Include(d => d.Activity)
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.TripId == tripId,
                cancellationToken);
        if (document == null) return NotFound();

        var canEditTrip = await CanEditTripAsync(trip, userId, cancellationToken);
        if (document.CreatedByUserId != userId && !canEditTrip) return Forbid();

        var name = NormalizeRequired(dto.Name);
        if (name == null || name.Length > 200)
            return BadRequest(new { error = "invalid_document_name" });
        if (!TryNormalizeCategory(dto.Category, out var category))
            return BadRequest(new { error = "invalid_document_category" });

        TripActivity? activity = null;
        if (dto.ActivityId.HasValue)
        {
            activity = await FindActivityAsync(tripId, dto.ActivityId.Value, cancellationToken);
            if (activity == null)
                return BadRequest(new { error = "invalid_document_activity" });
        }

        document.Name = name;
        document.Category = category;
        document.Date = dto.Date;
        document.ActivityId = activity?.Id;
        document.Activity = activity;
        document.Note = NormalizeOptional(dto.Note);
        document.BookingReference = NormalizeOptional(dto.BookingReference);
        document.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapDocument(document, userId, canEditTrip));
    }

    [HttpDelete("{documentId:guid}")]
    public async Task<ActionResult> DeleteDocument(
        Guid tripId,
        Guid documentId,
        [FromServices] ITripDocumentStorageService storage,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var trip = await _db.Trips.FindAsync([tripId], cancellationToken);
        if (trip == null) return NotFound();
        if (!await IsMemberAsync(tripId, userId, cancellationToken)) return Forbid();

        var document = await _db.TripDocuments
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.TripId == tripId,
                cancellationToken);
        if (document == null) return NotFound();

        var canEditTrip = await CanEditTripAsync(trip, userId, cancellationToken);
        if (document.CreatedByUserId != userId && !canEditTrip) return Forbid();

        // Storage first: on a transient provider error the row remains and
        // the user can retry. If DB deletion then fails, a retry treats the
        // already-missing object as success and removes the stale row.
        if (!await storage.DeleteAsync(document.StoragePath, cancellationToken))
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "document_storage_delete_failed",
            });
        }

        try
        {
            _db.TripDocuments.Remove(document);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _logger.LogError(
                "Document metadata deletion failed after object deletion.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "document_metadata_delete_failed",
            });
        }

        return NoContent();
    }

    [HttpPost("{documentId:guid}/open")]
    public async Task<ActionResult<TripDocumentOpenResponseDto>> OpenDocument(
        Guid tripId,
        Guid documentId,
        [FromServices] ITripDocumentStorageService storage,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var userId = GetUserId();
        var tripExists = await _db.Trips.AnyAsync(t => t.Id == tripId, cancellationToken);
        if (!tripExists) return NotFound();
        if (!await IsMemberAsync(tripId, userId, cancellationToken)) return Forbid();

        var document = await _db.TripDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.TripId == tripId,
                cancellationToken);
        if (document == null) return NotFound();

        try
        {
            var signed = await storage.CreateSignedReadUrlAsync(
                document.StoragePath,
                cancellationToken);
            return Ok(new TripDocumentOpenResponseDto
            {
                Url = signed.Url,
                ExpiresAt = signed.ExpiresAt,
            });
        }
        catch (TripDocumentStorageException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "document_open_failed",
            });
        }
    }

    private Guid GetUserId()
        => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> IsMemberAsync(
        Guid tripId,
        Guid userId,
        CancellationToken cancellationToken)
        => await _db.TripMembers.AnyAsync(
            tm => tm.TripId == tripId && tm.UserId == userId,
            cancellationToken);

    private async Task<bool> CanEditTripAsync(
        Trip trip,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (trip.MembersCanEdit) return true;
        return await _db.TripMembers.AnyAsync(
            tm => tm.TripId == trip.Id && tm.UserId == userId && tm.IsOwner,
            cancellationToken);
    }

    private async Task<TripActivity?> FindActivityAsync(
        Guid tripId,
        Guid activityId,
        CancellationToken cancellationToken)
        => await _db.TripActivities.FirstOrDefaultAsync(
            a => a.TripId == tripId && a.Id == activityId,
            cancellationToken);

    private async Task RollBackUploadedObjectAsync(
        TripDocument document,
        bool uploaded,
        ITripDocumentStorageService storage)
    {
        if (!uploaded) return;
        if (!await storage.DeleteAsync(document.StoragePath, CancellationToken.None))
        {
            _logger.LogError(
                "Private document upload rollback could not remove its object.");
        }
    }

    private static TripDocumentDto MapDocument(
        TripDocument document,
        Guid userId,
        bool canEditTrip)
        => new()
        {
            Id = document.Id,
            TripId = document.TripId,
            Name = document.Name,
            Category = document.Category,
            FileType = document.FileType,
            FileSize = document.FileSize,
            Date = document.Date,
            ActivityId = document.ActivityId,
            ActivityTitle = CanViewActivityTitle(document.Activity, userId)
                ? document.Activity!.Title
                : null,
            Note = document.Note,
            BookingReference = document.BookingReference,
            CreatedByUserId = document.CreatedByUserId,
            CreatedByName = document.CreatedBy?.Name ?? string.Empty,
            UploadedAt = document.UploadedAt,
            CanEdit = document.CreatedByUserId == userId || canEditTrip,
        };

    private static bool CanViewActivityTitle(TripActivity? activity, Guid userId)
        => activity != null
           && (activity.OwnerId == userId
               || activity.Visibility == "public"
               || (activity.RevealAt.HasValue && DateTime.UtcNow >= activity.RevealAt.Value));

    private static string BuildStoragePath(
        Guid tripId,
        Guid documentId,
        string extension)
        => $"{tripId:N}/{documentId:N}/{documentId:N}{extension}";

    private static bool TryNormalizeCategory(string? value, out string category)
    {
        category = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return AllowedCategories.Contains(category);
    }

    private static string? NormalizeRequired(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
