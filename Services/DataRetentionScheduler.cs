using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services;

// Keeps two tables from growing forever: TripEvents (the home tab's
// "Activity" feed — joined/left/added-a-sidequest entries) and ChatMessages.
// Mirrors the limits already used when reading this data (30-day cutoff +
// 50-per-query for events in GetMyTripEvents; 80-message initial page for
// chat) — this is what actually enforces them in storage instead of just
// filtering them out of a query, so old rows don't sit in Postgres forever.
//
// Runs every 6 hours — this is housekeeping, not time-critical, so no need
// for RevealNotificationScheduler's 60-second cadence. Independent of
// Push:Enabled; it has nothing to do with push delivery.
public class DataRetentionScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);
    private const int MaxEventsPerTrip = 50;
    private const int MaxEventAgeDays = 30;
    private const int MaxChatMessagesPerTrip = 80;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionScheduler> _logger;

    public DataRetentionScheduler(IServiceScopeFactory scopeFactory, ILogger<DataRetentionScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataRetentionScheduler starting. Poll interval: {Interval}.", PollInterval);

        using var timer = new PeriodicTimer(PollInterval);

        await TickAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TickAsync(stoppingToken);
        }

        _logger.LogInformation("DataRetentionScheduler stopping.");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-MaxEventAgeDays);
            var deletedOldEvents = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM "TripEvents" WHERE "CreatedAt" < {cutoff}""", ct);

            var deletedExcessEvents = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "TripId" ORDER BY "CreatedAt" DESC) AS rn
                    FROM "TripEvents"
                )
                DELETE FROM "TripEvents" WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > {MaxEventsPerTrip})
                """, ct);

            // The images attached to the messages this pass removes have to go
            // with them. Retention used to drop the rows only, which left every
            // aged-out photo sitting in storage — and with the row gone,
            // nothing in the database pointed at the file any more, so it could
            // never be found and cleaned up later. Six-hourly, that is a slowly
            // growing pile of user photos that outlive the conversation they
            // belonged to.
            //
            // The references are read first rather than via RETURNING so the
            // delete statement itself is untouched, and they are only used to
            // call storage — they are never logged.
            var orphanedImageRefs = await db.ChatMessages
                .FromSqlInterpolated($"""
                    WITH ranked AS (
                        SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "TripId" ORDER BY "CreatedAt" DESC) AS rn
                        FROM "ChatMessages"
                    )
                    SELECT * FROM "ChatMessages" WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > {MaxChatMessagesPerTrip})
                    """)
                .AsNoTracking()
                .Where(m => m.ImageUrl != null)
                .Select(m => m.ImageUrl)
                .ToListAsync(ct);

            var deletedExcessChat = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "TripId" ORDER BY "CreatedAt" DESC) AS rn
                    FROM "ChatMessages"
                )
                DELETE FROM "ChatMessages" WHERE "Id" IN (SELECT "Id" FROM ranked WHERE rn > {MaxChatMessagesPerTrip})
                """, ct);

            // Which bucket an image lives in is decided per row, from the
            // reference itself — a single trip's history can contain both
            // pre-migration public URLs and private references.
            var privatePaths = new List<string>();
            var legacyPublicUrls = new List<string?>();
            foreach (var reference in orphanedImageRefs)
            {
                if (ChatImageReference.TryGetObjectPathForCleanup(reference, out var objectPath))
                    privatePaths.Add(objectPath);
                else
                    legacyPublicUrls.Add(reference);
            }

            if (privatePaths.Count > 0)
            {
                var chatImages = scope.ServiceProvider.GetRequiredService<IChatImageStorageService>();
                if (!await chatImages.DeleteManyAsync(privatePaths, ct))
                {
                    // Count only. The next tick re-selects nothing (the rows are
                    // gone), so this line is the sole record that some objects
                    // may have survived — it still must not name them.
                    _logger.LogWarning(
                        "DataRetentionScheduler could not delete every private chat image in this pass ({Count} attempted).",
                        privatePaths.Count);
                }
            }

            if (legacyPublicUrls.Count > 0)
            {
                var storage = scope.ServiceProvider.GetRequiredService<ISupabaseStorageService>();
                await storage.DeleteManyByUrlAsync(legacyPublicUrls, ct);
            }

            _logger.LogInformation(
                "DataRetentionScheduler tick complete. Deleted {OldEvents} event(s) older than {Days}d, {ExcessEvents} event(s) beyond top {MaxEvents}/trip, {ExcessChat} chat message(s) beyond top {MaxChat}/trip ({PrivateImages} private image(s), {LegacyImages} legacy image(s)).",
                deletedOldEvents, MaxEventAgeDays, deletedExcessEvents, MaxEventsPerTrip, deletedExcessChat, MaxChatMessagesPerTrip, privatePaths.Count, legacyPublicUrls.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DataRetentionScheduler tick failed.");
        }
    }
}
