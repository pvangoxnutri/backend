using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services.Gluno;

public interface IGlunoContextBuilder
{
    /// <param name="tripId">
    /// The Adventure the conversation is scoped to, or null for a global
    /// conversation. Never taken from the model — the caller passes the
    /// conversation's own TripId.
    /// </param>
    /// <param name="conversationId">
    /// Scopes preferences and previously-discussed places. Null when there is
    /// no conversation yet (the first turn creates one).
    /// </param>
    Task<GlunoContext> BuildAsync(Guid userId, Guid? tripId, Guid? conversationId, CancellationToken ct);

    /// <summary>
    /// The same context, narrowed to what this turn actually needs.
    ///
    /// The planning strategy decides the options. "Where is the packing list?"
    /// does not need a plan loaded, its findings computed or a forecast
    /// fetched — and fetching them anyway costs a weather call, a page of
    /// tokens and a slower answer, none of which can improve the reply.
    /// </summary>
    Task<GlunoContext> BuildAsync(
        Guid userId, Guid? tripId, Guid? conversationId, GlunoContextOptions options, CancellationToken ct);
}

/// <summary>
/// Which parts of the context to assemble.
///
/// Defaults are everything ON, so an option that is never set behaves exactly
/// as the context did before this type existed. Narrowing is always a
/// deliberate act by the caller.
/// </summary>
public sealed record GlunoContextOptions
{
    public bool IncludeTrip { get; init; } = true;
    public bool IncludeWeather { get; init; } = true;
    /// The deterministic findings pass. Cheap, but noise on a turn that is not
    /// about the plan.
    public bool IncludeAnalysis { get; init; } = true;
    public bool IncludeDiscussedPlaces { get; init; } = true;

    public static readonly GlunoContextOptions Full = new();
}

/// <summary>
/// The ONE place SideQuest data becomes Gluno context.
///
/// Centralising it is the security boundary, not a tidiness preference. Gluno
/// in the mobile app has no data access of its own; it cannot assemble a
/// prompt, cannot read a trip it isn't in, and cannot widen its own scope,
/// because everything it is ever shown comes out of this method — behind the
/// same membership check the rest of the API uses.
///
/// Three invariants it enforces:
///
///  • Membership. Every trip that reaches the context was joined through
///    TripMembers by this user. A trip id the caller cannot prove membership
///    of yields a null Trip, not an error and not a partial leak.
///
///  • Other people's surprises stay surprises. A hidden SideQuest that has not
///    been revealed is dropped unless the requesting user created it. Gluno
///    cannot spoil something the app itself would not show.
///
///  • Bounded size. Every collection is capped and ordered deterministically,
///    so a trip with a thousand activities produces the same shaped context as
///    one with ten — and says it was truncated.
/// </summary>
public sealed class GlunoContextBuilder : IGlunoContextBuilder
{
    private readonly AppDbContext _db;
    private readonly IGlunoPreferenceService _preferences;
    private readonly WeatherService _weather;
    private readonly ITripPlanningProfileBuilder _profiles;
    private readonly ILogger<GlunoContextBuilder> _logger;

    public GlunoContextBuilder(
        AppDbContext db,
        IGlunoPreferenceService preferences,
        WeatherService weather,
        ITripPlanningProfileBuilder profiles,
        ILogger<GlunoContextBuilder> logger)
    {
        _db = db;
        _preferences = preferences;
        _weather = weather;
        _profiles = profiles;
        _logger = logger;
    }

    public Task<GlunoContext> BuildAsync(Guid userId, Guid? tripId, Guid? conversationId, CancellationToken ct)
        => BuildAsync(userId, tripId, conversationId, GlunoContextOptions.Full, ct);

    public async Task<GlunoContext> BuildAsync(
        Guid userId, Guid? tripId, Guid? conversationId, GlunoContextOptions options, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var truncated = false;

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new GlunoUserContext { Name = u.Name, Language = u.Language })
            .FirstOrDefaultAsync(ct) ?? new GlunoUserContext();

        // Membership is the filter, not an afterthought: the join below is the
        // only way a trip can enter the context at all.
        var membershipQuery = _db.TripMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == userId)
            .Join(_db.Trips.AsNoTracking(), tm => tm.TripId, t => t.Id, (tm, t) => new { Member = tm, Trip = t });

        var tripRows = await membershipQuery
            .OrderByDescending(x => x.Trip.StartDate)
            .ThenBy(x => x.Trip.Id)
            .Take(GlunoContextLimits.MaxTrips + 1)
            .Select(x => new
            {
                x.Trip.Id,
                x.Trip.Title,
                x.Trip.Destination,
                x.Trip.StartDate,
                x.Trip.EndDate,
                x.Trip.Status,
                x.Member.IsOwner,
            })
            .ToListAsync(ct);

        if (tripRows.Count > GlunoContextLimits.MaxTrips)
        {
            truncated = true;
            tripRows = tripRows.Take(GlunoContextLimits.MaxTrips).ToList();
        }

        var summaries = tripRows
            .Select(t => new GlunoTripSummary
            {
                Id = t.Id,
                Title = t.Title,
                Destination = t.Destination,
                StartDate = t.StartDate,
                EndDate = t.EndDate,
                Status = t.Status,
                IsOwner = t.IsOwner,
            })
            .ToList();

        // Preferences first: the pace they set is what the analyzer measures
        // the plan against, so it has to be known before the trip is analysed.
        var preferences = conversationId.HasValue
            ? await _preferences.GetForContextAsync(userId, conversationId.Value, tripId, ct)
            : new List<Models.GlunoPreference>();

        var pace = TripPaces.Parse(
            preferences.FirstOrDefault(p => p.Key == Models.GlunoPreferenceKeys.Pace)?.Value);

        GlunoTripContext? tripContext = null;
        if (tripId.HasValue && options.IncludeTrip)
        {
            var (built, wasTruncated) = await BuildTripContextAsync(userId, tripId.Value, today, pace, options, ct);
            tripContext = built;
            truncated |= wasTruncated;

            // A trip the user is a member of but which fell outside the
            // summary cap must still appear in the list when it is the one
            // being discussed — otherwise the model sees a focused trip that
            // is absent from "your Adventures" and reasons about the gap.
            if (tripContext != null && summaries.All(s => s.Id != tripContext.Id))
            {
                summaries.Insert(0, new GlunoTripSummary
                {
                    Id = tripContext.Id,
                    Title = tripContext.Title,
                    Destination = tripContext.Destination,
                    StartDate = tripContext.StartDate,
                    EndDate = tripContext.EndDate,
                    Status = tripContext.Status,
                    IsOwner = tripContext.IsOwner,
                });
            }
        }

        return new GlunoContext
        {
            Today = today,
            User = user,
            Trip = tripContext,
            Trips = summaries,
            Preferences = preferences
                .Select(p => new GlunoPreferenceContext { Key = p.Key, Value = p.Value, Scope = p.Scope })
                .ToList(),
            DiscussedPlaces = conversationId.HasValue && options.IncludeDiscussedPlaces
                ? await LoadDiscussedPlacesAsync(conversationId.Value, ct)
                : Array.Empty<GlunoDiscussedPlaceContext>(),
            Group = tripContext != null
                ? await LoadGroupAsync(tripContext.Id, user.Language, ct)
                : null,
            Truncated = truncated,
        };
    }

    /// <summary>
    /// The shared half of a group Adventure.
    ///
    /// Returns null on a solo trip and on a group where nobody has shared
    /// anything: group machinery on a trip of one is noise, and an empty
    /// profile in the prompt invites Gluno to talk about "the group" when
    /// there is nothing to say.
    ///
    /// A failure here degrades to solo planning rather than to a failed turn.
    /// Group awareness makes an answer better; it is not what makes it work.
    /// </summary>
    private async Task<GlunoGroupContext?> LoadGroupAsync(Guid tripId, string language, CancellationToken ct)
    {
        try
        {
            var profile = await _profiles.BuildAsync(tripId, ct);

            if (profile.IsSoloTrip) return null;
            if (profile.Constraints.Count == 0 && profile.Decisions.Count == 0) return null;

            return new GlunoGroupContext
            {
                GroupSize = profile.GroupSize,
                ContributingMembers = profile.ContributingMembers,
                // Hard first: a mobility requirement must be the first thing
                // read, not the eleventh.
                Constraints = profile.Hard.Concat(profile.Soft).ToList(),
                Decisions = profile.Decisions,
                Conflicts = GroupPreferenceConflictDetector.Detect(profile, language),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GLUNO] group profile failed: {Category}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// External places already shown in this conversation.
    ///
    /// Read back out of the assistant turns rather than kept in a second
    /// table: the chat already stores exactly what the user was shown, and a
    /// parallel store could drift from it. Newest first, capped, so "the
    /// second one you mentioned" resolves without another provider call.
    /// </summary>
    private async Task<IReadOnlyList<GlunoDiscussedPlaceContext>> LoadDiscussedPlacesAsync(
        Guid conversationId, CancellationToken ct)
    {
        var payloads = await _db.GlunoMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                        && m.Role == Models.GlunoMessageRoles.Assistant
                        && m.PayloadJson != null)
            .OrderByDescending(m => m.CreatedAt)
            .Take(GlunoContextLimits.MaxDiscussedPlaceTurns)
            .Select(m => m.PayloadJson!)
            .ToListAsync(ct);

        var places = new List<GlunoDiscussedPlaceContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var payload in payloads)
        {
            GlunoAssistantPayload? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<GlunoAssistantPayload>(payload, GlunoJson.Options);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            foreach (var place in parsed?.Places ?? [])
            {
                if (places.Count >= GlunoContextLimits.MaxDiscussedPlaces) return places;
                if (!seen.Add(place.ExternalId)) continue;

                places.Add(new GlunoDiscussedPlaceContext
                {
                    Provider = place.Provider,
                    ExternalId = place.ExternalId,
                    Name = place.Name,
                    Category = place.CategoryLabel ?? place.Category,
                    Address = place.Address,
                    Rating = place.Rating,
                    ReviewCount = place.ReviewCount,
                    Latitude = place.Latitude,
                    Longitude = place.Longitude,
                    SourceAttribution = place.SourceAttribution,
                });
            }
        }

        return places;
    }

    private async Task<(GlunoTripContext? Context, bool Truncated)> BuildTripContextAsync(
        Guid userId, Guid tripId, DateOnly today, TripPace pace, GlunoContextOptions options, CancellationToken ct)
    {
        var membership = await _db.TripMembers
            .AsNoTracking()
            .Where(tm => tm.TripId == tripId && tm.UserId == userId)
            .Select(tm => new { tm.IsOwner })
            .FirstOrDefaultAsync(ct);

        // Not a member → no trip context. Returning null rather than throwing
        // keeps a stale conversation (trip deleted, membership removed) usable
        // as a global one instead of hard-failing every turn.
        if (membership == null) return (null, false);

        var trip = await _db.Trips
            .AsNoTracking()
            .Where(t => t.Id == tripId)
            .FirstOrDefaultAsync(ct);
        if (trip == null) return (null, false);

        var truncated = false;

        var memberRows = await _db.TripMembers
            .AsNoTracking()
            .Where(tm => tm.TripId == tripId)
            .OrderByDescending(tm => tm.IsOwner)
            .ThenBy(tm => tm.JoinedAt)
            .ThenBy(tm => tm.UserId)
            .Take(GlunoContextLimits.MaxMembers + 1)
            .Select(tm => new { tm.UserId, tm.IsOwner, Name = tm.User.Name })
            .ToListAsync(ct);

        if (memberRows.Count > GlunoContextLimits.MaxMembers)
        {
            truncated = true;
            memberRows = memberRows.Take(GlunoContextLimits.MaxMembers).ToList();
        }

        // Someone else's unrevealed hidden SideQuest never enters the context.
        // IsHidden is the app's "not revealed yet" flag; the creator sees their
        // own, everyone else does not — exactly as the trip screens behave.
        var activityRows = await _db.TripActivities
            .AsNoTracking()
            .Where(a => a.TripId == tripId)
            .Where(a => !a.IsHidden || a.OwnerId == userId)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.SortIndex)
            .ThenBy(a => a.Id)
            .Take(GlunoContextLimits.MaxActivities + 1)
            .Select(a => new
            {
                a.Id,
                a.Date,
                a.Title,
                a.Description,
                a.Time,
                a.EndDate,
                a.EndTime,
                a.SortIndex,
                a.Category,
                a.CustomCategoryLabel,
                a.IsHidden,
            })
            .ToListAsync(ct);

        if (activityRows.Count > GlunoContextLimits.MaxActivities)
        {
            truncated = true;
            activityRows = activityRows.Take(GlunoContextLimits.MaxActivities).ToList();
        }

        // The location parse happens here, in memory, because the app stores an
        // activity's place inside its description (see ActivityLocationMarkers)
        // and that is not something SQL can unpick. It is also what turns the
        // whole geographic half of TripAnalyzer on: without coordinates there
        // is nothing to measure.
        var activities = activityRows
            .Select(a =>
            {
                var location = ActivityLocationMarkers.Read(a.Description);
                return new GlunoActivityContext
                {
                    Id = a.Id,
                    Date = a.Date,
                    Title = a.Title,
                    // Markers stripped: Gluno must never see, or repeat, the
                    // raw "[map-location]:" syntax.
                    Description = ActivityLocationMarkers.StripMarkers(a.Description),
                    Time = a.Time,
                    EndDate = a.EndDate,
                    EndTime = a.EndTime,
                    SortIndex = a.SortIndex,
                    Category = a.Category,
                    CustomCategoryLabel = a.CustomCategoryLabel,
                    LocationLabel = location.Label,
                    PlaceId = location.PlaceId,
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    Role = ActivityRoles.FromCategory(a.Category, a.EndDate),
                    IsOwnHiddenSurprise = a.IsHidden,
                };
            })
            .ToList();

        var dayLocationRows = await _db.TripDayLocations
            .AsNoTracking()
            .Where(d => d.TripId == tripId)
            .OrderBy(d => d.StartDate)
            .ThenBy(d => d.SortIndex)
            .Take(GlunoContextLimits.MaxDayLocations + 1)
            .Select(d => new GlunoDayLocationContext
            {
                Date = d.StartDate,
                SortIndex = d.SortIndex,
                Label = d.LocationLabel,
                Latitude = d.Latitude,
                Longitude = d.Longitude,
            })
            .ToListAsync(ct);

        if (dayLocationRows.Count > GlunoContextLimits.MaxDayLocations)
        {
            truncated = true;
            dayLocationRows = dayLocationRows.Take(GlunoContextLimits.MaxDayLocations).ToList();
        }

        var latestContentDate = LatestContentDate(activities, dayLocationRows);

        var memberCount = await _db.TripMembers.CountAsync(tm => tm.TripId == tripId, ct);

        // Expenses as a total per currency and nothing else. Gluno plans
        // trips; who paid what is not its business.
        var budget = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.TripId == tripId)
            .GroupBy(e => e.Currency)
            .Select(g => new GlunoBudgetContext
            {
                Currency = g.Key,
                TotalSpent = g.Sum(e => e.TotalAmount),
                ExpenseCount = g.Count(),
            })
            .ToListAsync(ct);

        // What Gluno has already done here, so it does not offer the same
        // thing a second time.
        var appliedChanges = await _db.GlunoProposals
            .AsNoTracking()
            .Where(p => p.TripId == tripId
                        && p.UserId == userId
                        && p.Status == Models.GlunoProposalStatuses.Applied)
            .OrderByDescending(p => p.AppliedAt)
            .Take(GlunoContextLimits.MaxAppliedChanges)
            .Select(p => new GlunoAppliedChangeContext
            {
                Kind = p.ActionType,
                Summary = p.Summary,
                AppliedAt = p.AppliedAt!.Value,
            })
            .ToListAsync(ct);

        var effectiveEnd = TripDateRange.EffectiveEnd(trip.StartDate, trip.EndDate, today, latestContentDate);
        // Weather is an external call. A turn that could not use a forecast
        // must not pay for one.
        var weather = options.IncludeWeather
            ? await LoadWeatherAsync(dayLocationRows, trip, effectiveEnd, ct)
            : Array.Empty<GlunoWeatherContext>();

        var tripContext = new GlunoTripContext
        {
            Id = trip.Id,
            Title = trip.Title,
            Description = trip.Description,
            Destination = trip.Destination,
            DestinationLatitude = trip.DestinationLatitude,
            DestinationLongitude = trip.DestinationLongitude,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            EffectiveEndDate = effectiveEnd,
            IsOpenEnded = trip.EndDate == null,
            Status = trip.Status,
            IsOwner = membership.IsOwner,
            MembersCanEdit = trip.MembersCanEdit,
            CanEdit = membership.IsOwner || trip.MembersCanEdit,
            Members = memberRows
                .Select(m => new GlunoMemberContext
                {
                    Name = m.Name,
                    IsOwner = m.IsOwner,
                    IsYou = m.UserId == userId,
                })
                .ToList(),
            MemberCount = memberCount,
            Activities = activities,
            DayLocations = dayLocationRows,
            Weather = weather,
            Budget = budget,
            AppliedChanges = appliedChanges,
        };

        // The analysis runs LAST, over the finished context, so it sees exactly
        // what the model will see — and so a finding can never reference an
        // activity that was truncated out of the context.
        return (
            options.IncludeAnalysis
                ? tripContext with { Findings = TripAnalyzer.Analyze(tripContext, pace, weather) }
                : tripContext,
            truncated);
    }

    /// <summary>
    /// SideQuest's own forecast for the days the trip actually covers.
    ///
    /// Same source the Weather screen uses, so Gluno and the app can never
    /// disagree about the weather. Absence is meaningful: a date past the
    /// provider's horizon simply produces no entry, and TripAnalyzer raises no
    /// weather finding for it rather than assuming sunshine.
    ///
    /// Bounded to a handful of locations, and a provider failure degrades to
    /// "no weather" — never to a failed turn.
    /// </summary>
    private async Task<IReadOnlyList<GlunoWeatherContext>> LoadWeatherAsync(
        IReadOnlyList<GlunoDayLocationContext> dayLocations,
        Models.Trip trip,
        DateOnly effectiveEnd,
        CancellationToken ct)
    {
        // The day's MAIN location is what its weather is about; an extra stop
        // a few kilometres away has the same forecast.
        var anchors = dayLocations
            .Where(d => d.SortIndex == 0)
            .GroupBy(d => (Math.Round(d.Latitude, 2), Math.Round(d.Longitude, 2)))
            .Take(GlunoContextLimits.MaxWeatherLocations)
            .ToList();

        // No day locations at all: fall back to the destination, which is
        // where the trip is by definition.
        if (anchors.Count == 0)
        {
            if (trip.DestinationLatitude is not { } lat || trip.DestinationLongitude is not { } lon)
                return Array.Empty<GlunoWeatherContext>();

            return await ForecastForAsync(lat, lon, trip.Destination, trip.StartDate, effectiveEnd, ct);
        }

        var weather = new List<GlunoWeatherContext>();
        foreach (var anchor in anchors)
        {
            var days = anchor.OrderBy(d => d.Date).ToList();
            var first = days[0];
            // Each anchor covers from its first date until the next anchor
            // takes over — beyond that the travellers are somewhere else.
            var last = days[^1].Date;

            weather.AddRange(await ForecastForAsync(
                first.Latitude, first.Longitude, first.Label, first.Date, last, ct));
        }

        return weather;
    }

    private async Task<List<GlunoWeatherContext>> ForecastForAsync(
        double latitude, double longitude, string? label, DateOnly from, DateOnly to, CancellationToken ct)
    {
        try
        {
            var result = await _weather.GetForecastAsync(latitude, longitude, ct);
            if (result.Forecast == null) return new List<GlunoWeatherContext>();

            return result.Forecast.Days
                .Where(day => day.IsForecastAvailable && day.Date >= from && day.Date <= to)
                .Select(day => new GlunoWeatherContext
                {
                    Date = day.Date,
                    Condition = day.Code,
                    TempMinC = day.TempMinC,
                    TempMaxC = day.TempMaxC,
                    PrecipitationProbability = day.PrecipitationProbability,
                    LocationLabel = label,
                })
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Weather is an enrichment. Never a reason for a Gluno turn to
            // fail, and never a place to log coordinates.
            _logger.LogWarning("[GLUNO] weather lookup failed: {Category}", ex.GetType().Name);
            return new List<GlunoWeatherContext>();
        }
    }

    private static DateOnly? LatestContentDate(
        IReadOnlyList<GlunoActivityContext> activities,
        IReadOnlyList<GlunoDayLocationContext> dayLocations)
    {
        DateOnly? latest = null;
        foreach (var a in activities)
        {
            var end = a.EndDate ?? a.Date;
            if (latest == null || end > latest.Value) latest = end;
        }
        foreach (var d in dayLocations)
        {
            if (latest == null || d.Date > latest.Value) latest = d.Date;
        }
        return latest;
    }
}
