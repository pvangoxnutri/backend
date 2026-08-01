using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services;

public sealed record ChatImageBackfillResult(
    int Examined,
    int Migrated,
    int SkippedNotInPublicBucket,
    int Failed,
    bool MoreRemaining);

/// <summary>
/// Moves pre-migration chat photos out of the public bucket and into the
/// private one.
///
/// THIS DOES NOT RUN ON ITS OWN. Nothing schedules it, no startup path calls
/// it, and its only trigger is a Development-gated endpoint that a person has
/// to invoke deliberately (ChatImageBackfillController). Making chat images
/// private for NEW messages required no data movement, so moving the existing
/// ones is a separate decision to be taken when someone can watch it happen.
///
/// ORDER MATTERS, and it is the whole design:
///
///     download → upload to private → update the database row → delete public
///
/// The public object is deleted LAST, only after the copy has been verified and
/// the row already points at the new location. Any earlier failure leaves the
/// message pointing at a public object that still exists — the photo keeps
/// working and the next run retries it. The reverse order would turn a
/// mid-flight crash into a permanently broken image.
///
/// IDEMPOTENT AND RESUMABLE. It selects only rows whose ImageUrl still looks
/// like a public URL, so re-running skips everything already migrated. It works
/// in bounded batches and reports whether more remain, so an interrupted run is
/// simply started again.
///
/// It never deletes a message, never nulls an ImageUrl, and never touches a row
/// it did not successfully copy.
/// </summary>
public sealed class ChatImageBackfillService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISupabaseStorageService _publicStorage;
    private readonly IChatImageStorageService _chatImages;
    private readonly ILogger<ChatImageBackfillService> _logger;

    public ChatImageBackfillService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ISupabaseStorageService publicStorage,
        IChatImageStorageService chatImages,
        ILogger<ChatImageBackfillService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _publicStorage = publicStorage;
        _chatImages = chatImages;
        _logger = logger;
    }

    /// <param name="batchSize">
    /// How many messages to process in this pass. Kept small by default so a
    /// run can be stopped at any moment with at most one message in flight.
    /// </param>
    /// <param name="dryRun">
    /// Reports what would be migrated without copying, writing or deleting
    /// anything. Run this first.
    /// </param>
    public async Task<ChatImageBackfillResult> RunAsync(
        int batchSize = 50,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);

        // Only legacy rows. A private reference does not start with http, so
        // anything already migrated is invisible to this query — that is what
        // makes re-running safe.
        var candidates = await _db.ChatMessages
            .Where(m => m.ImageUrl != null
                        && (m.ImageUrl.StartsWith("http://") || m.ImageUrl.StartsWith("https://")))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize + 1)
            .ToListAsync(cancellationToken);

        var moreRemaining = candidates.Count > batchSize;
        if (moreRemaining) candidates.RemoveAt(candidates.Count - 1);

        var migrated = 0;
        var skipped = 0;
        var failed = 0;

        var http = _httpClientFactory.CreateClient();

        foreach (var message in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var publicUrl = message.ImageUrl!;

            // Chat images that were never ours (an external host, or a legacy
            // /uploads/ path) have nothing to copy and no public object to
            // delete. Leave them exactly as they are.
            if (!_publicStorage.IsOwnedPublicUrl(publicUrl))
            {
                skipped++;
                continue;
            }

            if (dryRun)
            {
                migrated++;
                continue;
            }

            try
            {
                var extension = Path.GetExtension(new Uri(publicUrl).AbsolutePath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

                using var download = await http.GetAsync(publicUrl, cancellationToken);
                if (!download.IsSuccessStatusCode)
                {
                    // The object is already gone. The row still points at a
                    // dead URL, but that is the state it was in before this ran
                    // — rewriting it to a private reference would only turn one
                    // broken image into a different broken image.
                    failed++;
                    continue;
                }

                // Buffered so the upload has a seekable stream and so a
                // half-read network response can never become a truncated
                // object in the private bucket.
                using var buffer = new MemoryStream();
                await download.Content.CopyToAsync(buffer, cancellationToken);
                if (buffer.Length == 0)
                {
                    failed++;
                    continue;
                }
                buffer.Position = 0;

                // Re-validate on the way in. These bytes were accepted years
                // ago by a check that trusted the client's Content-Type header,
                // so this is the first time some of them are actually inspected.
                var detected = await ImageFileValidator.DetectAsync(buffer, cancellationToken);
                if (detected == null)
                {
                    _logger.LogWarning(
                        "Chat image backfill skipped message {MessageId}: stored bytes are not a supported image.",
                        message.Id);
                    failed++;
                    continue;
                }
                buffer.Position = 0;

                var objectPath = ChatImageReference.BuildObjectPath(message.TripId, detected.Extension);
                await _chatImages.UploadAsync(buffer, detected.ContentType, objectPath, cancellationToken);

                // Verify the copy landed before anything destructive happens.
                // Signing is the cheapest proof the object is really readable
                // in the private bucket; the URL is used for nothing and never
                // logged or returned.
                _ = await _chatImages.CreateSignedReadUrlAsync(objectPath, cancellationToken);

                // Point the row at the new object. Until this commits, the
                // message still resolves to the public URL that is still there.
                message.ImageUrl = ChatImageReference.Scheme + objectPath;
                await _db.SaveChangesAsync(cancellationToken);

                // Only now is the public object removed. A failure here leaves
                // a harmless orphan in the public bucket rather than a message
                // with no image.
                if (!await _publicStorage.DeleteByUrlAsync(publicUrl, cancellationToken))
                {
                    _logger.LogWarning(
                        "Chat image backfill migrated message {MessageId} but could not remove its public object.",
                        message.Id);
                }

                migrated++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Message id only — no URL, no object path. The row is
                // untouched unless SaveChanges already succeeded, and either
                // way the next run picks it up again.
                _logger.LogWarning(ex, "Chat image backfill failed for message {MessageId}.", message.Id);
                failed++;
            }
        }

        _logger.LogInformation(
            "Chat image backfill pass complete (dryRun={DryRun}). Examined {Examined}, migrated {Migrated}, skipped {Skipped}, failed {Failed}, moreRemaining={More}.",
            dryRun, candidates.Count, migrated, skipped, failed, moreRemaining);

        return new ChatImageBackfillResult(candidates.Count, migrated, skipped, failed, moreRemaining);
    }
}
