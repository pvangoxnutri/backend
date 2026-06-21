using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services;

// Polls for hidden activities whose teaser window has opened or whose
// RevealAt has passed, and dispatches the corresponding push. Runs every
// minute — RevealAt/TeaserOffsetMinutes are stored as exact UTC instants (the
// client already converts from the user's local time when they pick a
// reveal time), so no trip/timezone lookup is needed here, just UTC-now
// comparisons.
//
// The actual "never send twice" guarantee is the unique DedupeKey index in
// NotificationLog (see NotificationDispatchService) — the StartsWith checks
// here are just to avoid re-querying trip members and re-attempting already-
// claimed inserts every single minute for activities that were fully
// processed ages ago.
public class RevealNotificationScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    // No teaser offset in the UI exceeds 1 day (1440 min); 25h gives margin.
    private static readonly TimeSpan TeaserLookahead = TimeSpan.FromHours(25);
    // Stop reconsidering a reveal after this long — if it still has no
    // active push token for a recipient after a day, retrying hourly forever
    // achieves nothing.
    private static readonly TimeSpan RevealLookback = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RevealNotificationScheduler> _logger;

    public RevealNotificationScheduler(IServiceScopeFactory scopeFactory, ILogger<RevealNotificationScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        // Run once immediately on startup, then on the timer.
        await TickAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TickAsync(stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dispatch = scope.ServiceProvider.GetRequiredService<INotificationDispatchService>();

            await ProcessRevealsAsync(db, dispatch, ct);
            await ProcessTeasersAsync(db, dispatch, ct);
        }
        catch (Exception ex)
        {
            // A single bad tick must never kill the scheduler — log and try
            // again next minute.
            _logger.LogError(ex, "RevealNotificationScheduler tick failed.");
        }
    }

    private async Task ProcessRevealsAsync(AppDbContext db, INotificationDispatchService dispatch, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lookback = now - RevealLookback;

        var dueActivities = await db.TripActivities
            .Where(a => a.Visibility == "hidden" && a.RevealAt != null && a.RevealAt <= now && a.RevealAt >= lookback)
            .ToListAsync(ct);

        foreach (var activity in dueActivities)
        {
            var prefix = $"reveal:{activity.Id}:";
            var alreadyHandled = await db.NotificationLogs.AnyAsync(n => n.Type == "reveal" && n.DedupeKey.StartsWith(prefix), ct);
            if (alreadyHandled) continue;

            await dispatch.SendRevealAsync(activity, ct);
        }
    }

    private async Task ProcessTeasersAsync(AppDbContext db, INotificationDispatchService dispatch, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lookaheadCutoff = now + TeaserLookahead;

        var candidates = await db.TripActivities
            .Where(a => a.Visibility == "hidden"
                && a.RevealAt != null
                && a.RevealAt > now
                && a.RevealAt <= lookaheadCutoff
                && a.TeaserOffsetMinutes != null
                && a.Teaser != null)
            .ToListAsync(ct);

        foreach (var activity in candidates)
        {
            var teaserStart = activity.RevealAt!.Value.AddMinutes(-activity.TeaserOffsetMinutes!.Value);
            if (now < teaserStart) continue; // window not open yet

            var prefix = $"teaser:{activity.Id}:";
            var alreadyHandled = await db.NotificationLogs.AnyAsync(n => n.Type == "teaser" && n.DedupeKey.StartsWith(prefix), ct);
            if (alreadyHandled) continue;

            await dispatch.SendTeaserAsync(activity, ct);
        }
    }
}
