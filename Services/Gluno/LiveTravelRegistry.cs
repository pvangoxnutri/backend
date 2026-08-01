using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Cache, budget, recency and conflict handling in front of the live provider.
///
/// COST CONTROL, in the order it applies:
///   1. **The planner already said yes.** Most turns never reach here at all —
///      see GlunoLiveSearchPlanner.
///   2. **Cache, with per-category lifetimes.** A public holiday is the same
///      fact for months; a rail disruption is a different fact in twenty
///      minutes. One TTL for both would either serve stale disruption data or
///      re-fetch a calendar nobody changed.
///   3. **Dedupe.** Two people asking about the same city on the same evening
///      pay for it once.
///   4. **Per-turn budget.** A hard ceiling, so a planning bug cannot run up a
///      bill or a wait.
///
/// A PROVIDER FAILURE IS NOT A TURN FAILURE. Everything here degrades to an
/// empty result with <c>ProviderFailed</c> set. SideQuest's own analysis,
/// routing and place data all still work, and the answer says it could not
/// check rather than pretending it did.
/// </summary>
public sealed class LiveTravelRegistry : ILiveTravelRegistry
{
    private readonly ILiveTravelInformationProvider _provider;
    private readonly TravelDataCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<LiveTravelRegistry> _logger;

    /// Per-turn, because this service is scoped per request.
    private int _searchesUsed;

    public LiveTravelRegistry(
        ILiveTravelInformationProvider provider,
        TravelDataCache cache,
        IConfiguration config,
        ILogger<LiveTravelRegistry> logger)
    {
        _provider = provider;
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public bool IsAvailable => _provider.IsConfigured;

    private int MaxSearchesPerTurn
        => Math.Clamp(_config.GetValue("Gluno:LiveInfo:MaxSearchesPerTurn", 2), 1, 5);

    /// <summary>
    /// How long each category may be reused.
    ///
    /// The spread is the point. A national holiday calendar does not change;
    /// a strike does, hourly, and serving a twenty-minute-old "trains are
    /// running" is how somebody ends up at a station during a walkout.
    /// Events sit in between — stable for weeks, then worth re-checking as the
    /// date approaches, which <see cref="RevalidateNearDate"/> handles.
    /// </summary>
    private TimeSpan CacheDurationFor(string category) => category switch
    {
        LiveTravelCategories.Strike or LiveTravelCategories.TransportDisruption =>
            TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Gluno:LiveInfo:Cache:DisruptionMinutes", 20), 1, 240)),

        LiveTravelCategories.RoadDisruption or LiveTravelCategories.WeatherWarning
            or LiveTravelCategories.SafetyNotice =>
            TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Gluno:LiveInfo:Cache:UrgentMinutes", 60), 5, 720)),

        LiveTravelCategories.Closure =>
            TimeSpan.FromHours(Math.Clamp(_config.GetValue("Gluno:LiveInfo:Cache:ClosureHours", 6), 1, 72)),

        LiveTravelCategories.Event =>
            TimeSpan.FromHours(Math.Clamp(_config.GetValue("Gluno:LiveInfo:Cache:EventHours", 24), 1, 336)),

        // Calendars and standing rules. Long, because they genuinely do not move.
        LiveTravelCategories.PublicHoliday or LiveTravelCategories.BorderInformation
            or LiveTravelCategories.TemporaryRule =>
            TimeSpan.FromHours(Math.Clamp(_config.GetValue("Gluno:LiveInfo:Cache:StableHours", 168), 1, 720)),

        _ => TimeSpan.FromHours(6),
    };

    /// <summary>
    /// Close to the date, a cached event result stops being good enough.
    ///
    /// A festival cached three weeks ago was accurate then. Two days before
    /// somebody travels, "it was on when we last looked" is exactly the claim
    /// that should be re-checked.
    /// </summary>
    private static bool RevalidateNearDate(string category, DateOnly? from, DateOnly today)
        => category == LiveTravelCategories.Event
            && from is { } date
            && (date.DayNumber - today.DayNumber) is >= 0 and <= 3;

    public async Task<LiveTravelResult> SearchAsync(
        GlunoLiveSearchPlan plan,
        DateOnly windowStart,
        DateOnly? windowEnd,
        string language,
        CancellationToken ct)
    {
        if (!plan.ShouldSearch || !IsAvailable) return LiveTravelResult.Empty;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var facts = new List<LiveTravelFact>();
        var anyFailure = false;
        var cacheHits = 0;
        var budget = Math.Min(plan.MaxSearches, MaxSearchesPerTurn);

        foreach (var category in plan.Categories)
        {
            if (_searchesUsed >= budget) break;

            var query = new LiveTravelQuery
            {
                Destination = plan.Destination,
                Category = category,
                From = plan.From,
                To = plan.To,
                Language = language,
                MaxSources = Math.Clamp(_config.GetValue("Gluno:LiveInfo:MaxSourcesPerSearch", 4), 1, 8),
            };

            var key = BuildCacheKey(query);
            var forceRefresh = RevalidateNearDate(category, plan.From, today);

            if (!forceRefresh && _cache.TryGet<List<LiveTravelFact>>(key, out var cached) && cached != null)
            {
                cacheHits++;
                facts.AddRange(cached);
                continue;
            }

            _searchesUsed++;

            List<LiveTravelFact> fetched;
            try
            {
                // The provider's own token is passed through, so a user who
                // stops the turn stops the search too.
                fetched = (await _provider.SearchAsync(query, ct)).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[GLUNO] live provider threw: {Category}", ex.GetType().Name);
                anyFailure = true;
                continue;
            }

            // An empty result is NOT cached. "We found nothing" is often a
            // provider blip, and caching it turns one bad minute into hours of
            // silence on a question the user keeps asking.
            if (fetched.Count > 0) _cache.Set(key, fetched, CacheDurationFor(category));

            facts.AddRange(fetched);
        }

        // Classified against the traveller's OWN dates — the same fact is
        // current for one trip and expired for another.
        var dated = facts
            .Select(fact => GlunoLiveRecency.WithRecency(fact, windowStart, windowEnd, DateTime.UtcNow))
            .ToList();

        var deduped = Deduplicate(dated);

        return new LiveTravelResult
        {
            Facts = GlunoLiveRecency.Rank(deduped),
            Conflicts = GlunoLiveRecency.FindConflicts(deduped),
            ProviderFailed = anyFailure && facts.Count == 0,
            SearchesUsed = _searchesUsed,
            WasCacheHit = cacheHits > 0,
        };
    }

    /// <summary>
    /// Removes the same fact reported twice.
    ///
    /// Keyed on category plus title plus effective date, NOT on source: two
    /// outlets reporting one strike is one fact with two sources, and showing
    /// it twice makes a single disruption look like two. The higher-tier source
    /// survives, so the operator's own notice outranks the news write-up.
    /// </summary>
    private static List<LiveTravelFact> Deduplicate(IReadOnlyList<LiveTravelFact> facts)
    {
        var best = new Dictionary<string, LiveTravelFact>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            var key = string.Join(
                '|',
                fact.Category,
                GlunoIntentRouter.Normalise(fact.Title),
                fact.EffectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-");

            if (!best.TryGetValue(key, out var existing) || fact.SourceTier < existing.SourceTier)
            {
                best[key] = fact;
            }
        }

        return best.Values.ToList();
    }

    /// <summary>
    /// The cache key.
    ///
    /// Everything that changes the ANSWER: category, destination, the date
    /// window and the language. Leaving the window out would serve August's
    /// events for a September question; leaving the language out would answer a
    /// Swedish question with an English summary.
    /// </summary>
    private static string BuildCacheKey(LiveTravelQuery query)
        => string.Join(
            '|',
            "live",
            query.Category,
            GlunoIntentRouter.Normalise(query.Destination) is { Length: > 0 } destination ? destination : "-",
            query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-",
            query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-",
            query.Language);
}
