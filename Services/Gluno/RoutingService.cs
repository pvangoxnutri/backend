using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>One leg someone wants a travel time for.</summary>
public sealed record RouteRequest(RoutePoint From, RoutePoint To, TravelMode Mode, DateTime DepartureUtc);

/// <summary>
/// The only thing the rest of Gluno talks to about travel times.
///
/// Nothing above this line knows a routing vendor exists — same containment as
/// AnthropicGlunoAiProvider for the model. Swapping Google for someone else is
/// a Program.cs edit and a new IRoutingProvider; the schedule engine, the
/// actions and the prompt never change.
/// </summary>
public interface IRoutingService
{
    /// <summary>
    /// True when a provider is configured and reachable enough to try. Drives
    /// what /api/gluno/status reports and what the prompt is allowed to claim.
    /// Deliberately says nothing about WHICH provider.
    /// </summary>
    bool HasVerifiedRouting { get; }

    /// <summary>
    /// Answers every request, always. Legs that could not be verified come back
    /// as straight lines with Verified=false and no duration — never as a gap
    /// the caller has to guess about.
    /// </summary>
    Task<IReadOnlyList<RouteLeg>> GetLegsAsync(IReadOnlyList<RouteRequest> requests, CancellationToken ct);

    Task<RouteLeg> GetLegAsync(RoutePoint from, RoutePoint to, TravelMode mode, DateTime departureUtc, CancellationToken ct);
}

/// <summary>
/// Cache, batching, budget and fallback in front of <see cref="IRoutingProvider"/>.
///
/// COST CONTROL, in the order it applies:
///   1. **Coarse geographic filter.** Anything past MaxRoutableKm is an
///      intercity journey, not a walk between stops. We do not route it; the
///      schedule engine handles those separately and Gluno is forbidden from
///      inventing train times.
///   2. **Cache.** Keyed on rounded coordinates, mode and — only where it
///      matters — a departure bucket. Walking between two fixed points is the
///      same today and next month, so it is cached for days; driving depends
///      on traffic and is cached for minutes.
///   3. **Dedupe.** A day plan asks about the same hotel→centre leg several
///      times while comparing orders. It costs one call.
///   4. **Batching.** Whatever survives is asked as a matrix, so comparing
///      orders is one request rather than one per pair.
///   5. **Per-turn budget.** A hard ceiling on provider calls per Gluno turn.
///      This is the backstop: a planner bug that loops cannot run up a bill.
///
/// PARTIAL FAILURE IS THE NORMAL CASE. A provider that returns four of six
/// legs is not an error. The two missing ones become straight lines flagged
/// unverified, the day still gets planned, and the proposal shows which legs
/// are which.
/// </summary>
public sealed class RoutingService : IRoutingService
{
    private readonly IRoutingProvider _provider;
    private readonly TravelDataCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<RoutingService> _logger;

    /// Per-turn, because this service is scoped per request.
    private int _providerCallsUsed;

    public RoutingService(
        IRoutingProvider provider,
        TravelDataCache cache,
        IConfiguration config,
        ILogger<RoutingService> logger)
    {
        _provider = provider;
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public bool HasVerifiedRouting => _provider.IsConfigured;

    /// <summary>
    /// Past this, a leg is a journey between places, not a step within a day.
    /// Routing it would be both expensive and beside the point — an eight-hour
    /// drive is not a gap you slot between two museums.
    /// </summary>
    private double MaxRoutableKm
        => Math.Clamp(_config.GetValue("Routing:MaxRoutableKm", 150.0), 5, 1000);

    private int MaxRequestsPerTurn
        => Math.Clamp(_config.GetValue("Routing:MaxRequestsPerTurn", 4), 1, 20);

    /// Traffic makes today's drive nothing like tomorrow's.
    private TimeSpan DrivingTtl
        => TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Routing:CacheMinutesDriving", 20), 1, 240));

    /// Timetables are stable within a day but not across one.
    private TimeSpan TransitTtl
        => TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Routing:CacheMinutesTransit", 120), 5, 1440));

    /// The walk between two fixed points does not change. Cached for days.
    private TimeSpan WalkingTtl
        => TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Routing:CacheMinutesWalking", 7 * 24 * 60), 10, 30 * 24 * 60));

    public async Task<RouteLeg> GetLegAsync(
        RoutePoint from, RoutePoint to, TravelMode mode, DateTime departureUtc, CancellationToken ct)
    {
        var legs = await GetLegsAsync([new RouteRequest(from, to, mode, departureUtc)], ct);
        return legs[0];
    }

    public async Task<IReadOnlyList<RouteLeg>> GetLegsAsync(
        IReadOnlyList<RouteRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Array.Empty<RouteLeg>();

        var resolved = new RouteLeg?[requests.Count];
        // Indices still needing an upstream answer, grouped by what one matrix
        // call can cover: same mode, same departure bucket.
        var pending = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];

            if (!request.From.IsValid() || !request.To.IsValid())
            {
                resolved[index] = RouteLeg.StraightLine(request.From, request.To, request.Mode, "invalid_point");
                continue;
            }

            if (!HasVerifiedRouting)
            {
                resolved[index] = RouteLeg.StraightLine(request.From, request.To, request.Mode, "no_provider");
                continue;
            }

            // Coarse filter FIRST, before the cache: there is no point storing
            // entries for pairs we will never route.
            var straightLineKm = GeoDistance.KilometresBetween(
                request.From.Latitude, request.From.Longitude, request.To.Latitude, request.To.Longitude);

            if (straightLineKm is { } kilometres && kilometres > MaxRoutableKm)
            {
                resolved[index] = RouteLeg.StraightLine(request.From, request.To, request.Mode, "intercity");
                continue;
            }

            var key = BuildCacheKey(request);
            if (_cache.TryGet<RouteLeg>(key, out var cached) && cached != null)
            {
                resolved[index] = cached;
                continue;
            }

            var bucket = BuildBatchKey(request);
            if (!pending.TryGetValue(bucket, out var indices)) pending[bucket] = indices = [];
            indices.Add(index);
        }

        foreach (var group in pending)
        {
            await ResolveGroupAsync(requests, group.Value, resolved, ct);
        }

        // Anything still unanswered — budget exhausted, provider down, no route
        // in the graph — becomes an honest straight line rather than a hole.
        for (var index = 0; index < requests.Count; index++)
        {
            if (resolved[index] != null) continue;

            var request = requests[index];
            resolved[index] = RouteLeg.StraightLine(request.From, request.To, request.Mode, "provider_failed");
        }

        return resolved.Select(leg => leg!).ToList();
    }

    private async Task ResolveGroupAsync(
        IReadOnlyList<RouteRequest> requests, List<int> indices, RouteLeg?[] resolved, CancellationToken ct)
    {
        if (indices.Count == 0) return;

        if (_providerCallsUsed >= MaxRequestsPerTurn)
        {
            _logger.LogInformation(
                "[GLUNO] routing budget exhausted, {Pending} legs stay unverified", indices.Count);

            foreach (var index in indices)
            {
                var request = requests[index];
                resolved[index] = RouteLeg.StraightLine(request.From, request.To, request.Mode, "budget_exhausted");
            }

            return;
        }

        var mode = requests[indices[0]].Mode;
        var departureUtc = requests[indices[0]].DepartureUtc;

        // Distinct endpoints, so N legs sharing a hotel ask about it once.
        var origins = new List<RoutePoint>();
        var destinations = new List<RoutePoint>();
        var originIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var destinationIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var index in indices)
        {
            AddPoint(requests[index].From, origins, originIndex);
            AddPoint(requests[index].To, destinations, destinationIndex);
        }

        // The matrix is a cross product, so a group can exceed the provider's
        // element cap even when the leg count is modest. Chunk the origins
        // rather than dropping legs.
        var maxOriginsPerCall = Math.Max(1, _provider.MaxMatrixElements / Math.Max(1, destinations.Count));

        for (var offset = 0; offset < origins.Count; offset += maxOriginsPerCall)
        {
            if (_providerCallsUsed >= MaxRequestsPerTurn) break;

            var chunk = origins.Skip(offset).Take(maxOriginsPerCall).ToList();
            _providerCallsUsed++;

            IReadOnlyList<RouteLeg> legs;
            try
            {
                legs = await _provider.ComputeMatrixAsync(chunk, destinations, mode, departureUtc, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[GLUNO] routing provider threw: {Category}", ex.GetType().Name);
                continue;
            }

            // Every returned leg is cached, including pairs nobody asked for.
            // The matrix gave them to us for free, and the next order the
            // planner tries is very likely to want them.
            foreach (var leg in legs)
            {
                _cache.Set(BuildCacheKey(new RouteRequest(leg.Origin, leg.Destination, mode, departureUtc)), leg, TtlFor(mode));
            }

            var lookup = legs.ToDictionary(
                leg => PairKey(leg.Origin, leg.Destination), leg => leg, StringComparer.Ordinal);

            foreach (var index in indices)
            {
                if (resolved[index] != null) continue;
                if (lookup.TryGetValue(PairKey(requests[index].From, requests[index].To), out var leg))
                {
                    resolved[index] = leg;
                }
            }
        }
    }

    private static void AddPoint(RoutePoint point, List<RoutePoint> points, Dictionary<string, int> index)
    {
        var key = point.CacheKey();
        if (index.ContainsKey(key)) return;

        index[key] = points.Count;
        points.Add(point);
    }

    private static string PairKey(RoutePoint from, RoutePoint to) => $"{from.CacheKey()}>{to.CacheKey()}";

    /// <summary>
    /// The cache key.
    ///
    /// Direction is part of it — one-way streets, hills and transit routes all
    /// mean A→B and B→A are genuinely different journeys, and treating them as
    /// one is the kind of shortcut that produces a schedule which only works
    /// in one direction.
    ///
    /// The departure bucket is included ONLY for modes it changes. Bucketing a
    /// walk by hour would multiply cache entries twenty-four-fold for an answer
    /// that is identical every time.
    /// </summary>
    private static string BuildCacheKey(RouteRequest request)
        => string.Join(
            '|',
            "route",
            TravelModes.ToWireValue(request.Mode),
            request.From.CacheKey(),
            request.To.CacheKey(),
            TimeBucket(request.Mode, request.DepartureUtc));

    /// Legs that can share one matrix call.
    private static string BuildBatchKey(RouteRequest request)
        => string.Join('|', TravelModes.ToWireValue(request.Mode), TimeBucket(request.Mode, request.DepartureUtc));

    private static string TimeBucket(TravelMode mode, DateTime departureUtc) => mode switch
    {
        // Traffic and timetables: date plus a two-hour window. Finer would
        // shred the cache; coarser would call a Tuesday rush hour the same as
        // a Tuesday lunchtime.
        TravelMode.Driving or TravelMode.Transit => string.Create(
            CultureInfo.InvariantCulture,
            $"{departureUtc:yyyy-MM-dd}T{departureUtc.Hour / 2 * 2:00}"),
        // Walking and cycling do not care what time it is.
        _ => "any",
    };

    private TimeSpan TtlFor(TravelMode mode) => mode switch
    {
        TravelMode.Driving => DrivingTtl,
        TravelMode.Transit => TransitTtl,
        _ => WalkingTtl,
    };
}
