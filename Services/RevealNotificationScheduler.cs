using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Services;

// Runs every minute, four phases, each isolated so one failing phase doesn't
// take down the others in the same tick:
//   1. Claim due teasers/reveals not yet claimed (creates NotificationLog +
//      PushDeliveryAttempt rows and makes a first send attempt).
//   2. Retry any due PushDeliveryAttempt across ALL four notification types
//      (teaser, reveal, trip invite, chat message) — this is what makes a
//      temporary Expo/network failure recoverable instead of permanent.
//   3. Check Expo delivery receipts for attempts it accepted a ticket for.
//   4. (implicit) Logged summary so a stuck/empty tick is visible in logs,
//      not silent.
//
// RevealAt/TeaserOffsetMinutes are stored as exact UTC instants (the client
// converts from the user's local time when they pick a reveal time), so
// every comparison here is UTC-vs-UTC — no trip/timezone lookup needed.
public class RevealNotificationScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    // No teaser offset in the UI exceeds 1 day (1440 min); 25h gives margin.
    private static readonly TimeSpan TeaserLookahead = TimeSpan.FromHours(25);
    // Stop reconsidering a reveal after this long — if it still has no
    // active push token for a recipient after a day, retrying hourly forever
    // achieves nothing (CreateAttemptsAsync already marked that recipient
    // failed_permanent for lack of a token; nothing here would change).
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
        _logger.LogInformation("RevealNotificationScheduler starting. Poll interval: {Interval}.", PollInterval);

        using var timer = new PeriodicTimer(PollInterval);

        // Run once immediately on startup (covers a restart landing inside
        // an active reveal/teaser window), then on the timer.
        await TickAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TickAsync(stoppingToken);
        }

        _logger.LogInformation("RevealNotificationScheduler stopping.");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var tickStarted = DateTime.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dispatch = scope.ServiceProvider.GetRequiredService<INotificationDispatchService>();

        var revealsClaimed = await RunPhaseAsync("ClaimReveals", () => ProcessRevealsAsync(db, dispatch, ct), ct);
        var teasersClaimed = await RunPhaseAsync("ClaimTeasers", () => ProcessTeasersAsync(db, dispatch, ct), ct);
        await RunPhaseAsync("ProcessPendingAttempts", () => dispatch.ProcessPendingAttemptsAsync(ct), ct);
        await RunPhaseAsync("CheckReceipts", () => dispatch.CheckReceiptsAsync(ct), ct);

        _logger.LogInformation(
            "RevealNotificationScheduler tick complete in {ElapsedMs}ms. Claimed {Reveals} reveal(s), {Teasers} teaser(s).",
            (DateTime.UtcNow - tickStarted).TotalMilliseconds, revealsClaimed, teasersClaimed);
    }

    // Each phase gets its own try/catch — a single bad phase (e.g. Expo's API
    // being down during CheckReceipts) must never prevent the others from
    // running, and must never crash the scheduler itself.
    private async Task<int> RunPhaseAsync(string phaseName, Func<Task<int>> phase, CancellationToken ct)
    {
        try
        {
            return await phase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevealNotificationScheduler phase {Phase} failed.", phaseName);
            return 0;
        }
    }

    private async Task RunPhaseAsync(string phaseName, Func<Task> phase, CancellationToken ct)
    {
        try
        {
            await phase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevealNotificationScheduler phase {Phase} failed.", phaseName);
        }
    }

    private async Task<int> ProcessRevealsAsync(AppDbContext db, INotificationDispatchService dispatch, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lookback = now - RevealLookback;

        var dueActivities = await db.TripActivities
            .Where(a => a.Visibility == "hidden"
                && a.RevealedAt == null // explicitly skip activities already manually revealed
                && a.RevealAt != null
                && a.RevealAt <= now
                && a.RevealAt >= lookback)
            .ToListAsync(ct);

        var claimedCount = 0;
        foreach (var activity in dueActivities)
        {
            var prefix = $"sidequest_revealed:{activity.Id}:";
            var alreadyHandled = await db.NotificationLogs.AnyAsync(n => n.Type == "sidequest_revealed" && n.DedupeKey.StartsWith(prefix), ct);
            if (alreadyHandled) continue;

            await dispatch.SendRevealAsync(activity, ct);

            // Same in-app event a manual "Reveal now" creates (see
            // TripsController.UpdateActivity) — a timed reveal must show up
            // in Home Activity exactly like a manual one does.
            var owner = await db.Users.FindAsync(new object?[] { activity.OwnerId }, ct);
            db.TripEvents.Add(new TripEvent
            {
                TripId = activity.TripId,
                ActorId = activity.OwnerId,
                ActorName = owner?.Name ?? "Someone",
                Type = "sidequest_revealed",
                ActivityId = activity.Id,
                ActivityTitle = activity.Title,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);

            claimedCount++;
        }

        return claimedCount;
    }

    private async Task<int> ProcessTeasersAsync(AppDbContext db, INotificationDispatchService dispatch, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lookaheadCutoff = now + TeaserLookahead;

        var candidates = await db.TripActivities
            .Where(a => a.Visibility == "hidden"
                && a.RevealedAt == null // explicitly skip activities already manually revealed
                && a.RevealAt != null
                && a.RevealAt > now
                && a.RevealAt <= lookaheadCutoff
                && a.TeaserOffsetMinutes != null
                && a.Teaser != null)
            .ToListAsync(ct);

        var claimedCount = 0;
        foreach (var activity in candidates)
        {
            var teaserStart = activity.RevealAt!.Value.AddMinutes(-activity.TeaserOffsetMinutes!.Value);
            if (now < teaserStart) continue; // window not open yet

            var prefix = $"teaser:{activity.Id}:";
            var alreadyHandled = await db.NotificationLogs.AnyAsync(n => n.Type == "teaser" && n.DedupeKey.StartsWith(prefix), ct);
            if (alreadyHandled) continue;

            await dispatch.SendTeaserAsync(activity, ct);
            claimedCount++;
        }

        return claimedCount;
    }
}
