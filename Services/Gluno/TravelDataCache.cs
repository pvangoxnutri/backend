using System.Collections.Concurrent;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The cache in front of every external travel-data call.
///
/// It exists for three separate reasons, and all three matter:
///
///  • **Quota.** A single Gluno turn can fan out into a search plus a details
///    call per result. Without a cache, two people asking about the same city
///    on the same evening pay for it twice.
///  • **Latency.** A cached answer turns a multi-second provider round trip
///    into nothing, inside a chat where the user is already waiting.
///  • **Stampedes.** The dedupe below means N concurrent identical requests
///    produce exactly ONE upstream call — the same Lazy&lt;Task&gt; pattern
///    WeatherService uses, and for the same reason: the moment a cache entry
///    expires is precisely when every caller arrives at once.
///
/// Deliberately in-memory: single instance (same assumption as the presence
/// throttle in Program.cs), and nothing here is worth the operational cost of
/// a distributed cache.
///
/// A failed lookup is NOT cached. Caching "no result" would turn one provider
/// blip into minutes of empty answers.
/// </summary>
public sealed class TravelDataCache
{
    private sealed record Entry(object Value, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlight = new();

    /// Bounds worst-case memory: place data is small, but an unbounded
    /// dictionary keyed by free-text queries is a slow leak.
    private const int MaxEntries = 2000;

    public async Task<T?> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> factory, CancellationToken ct)
        where T : class
    {
        var now = DateTime.UtcNow;

        if (_entries.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt > now) return cached.Value as T;
            _entries.TryRemove(key, out _);
        }

        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await factory()));
        try
        {
            // WaitAsync so a caller's cancellation abandons the wait without
            // cancelling the shared upstream call other callers are riding on.
            var value = await lazy.Value.WaitAsync(ct);
            if (value is T typed)
            {
                Store(key, typed, ttl);
                return typed;
            }
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// A synchronous peek, for callers that batch.
    ///
    /// GetOrAddAsync answers one question at a time, which is the wrong shape
    /// for a route matrix: the point of a matrix is to ask for the twenty legs
    /// we still need in ONE upstream call. That requires knowing which legs are
    /// already cached before deciding what to ask for.
    /// </summary>
    public bool TryGet<T>(string key, out T? value) where T : class
    {
        value = null;

        if (!_entries.TryGetValue(key, out var cached)) return false;
        if (cached.ExpiresAt <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = cached.Value as T;
        return value != null;
    }

    /// Companion to <see cref="TryGet{T}"/>: store what a batch call returned.
    public void Set<T>(string key, T value, TimeSpan ttl) where T : class
        => Store(key, value, ttl);

    private void Store(string key, object value, TimeSpan ttl)
    {
        if (_entries.Count >= MaxEntries) EvictExpired();
        if (_entries.Count >= MaxEntries) return;

        _entries[key] = new Entry(value, DateTime.UtcNow.Add(ttl));
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now) _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// Builds a cache key from its parts.
    ///
    /// Every field that changes the ANSWER belongs in here — provider, the
    /// normalised query, the area or coordinates, the category and the
    /// language. Leaving one out means serving a Swedish answer to an English
    /// request, or Lisbon's restaurants for a Porto query; both have happened
    /// to people who keyed a cache on the query string alone.
    ///
    /// Coordinates are rounded to ~100 m so that two phones standing in the
    /// same square share an entry instead of each minting their own.
    /// </summary>
    public static string BuildKey(string provider, string operation, TravelPlaceQuery query)
    {
        var normalisedQuery = NormaliseText(query.Query);
        var area = NormaliseText(query.Near);
        var coordinates = query.Latitude.HasValue && query.Longitude.HasValue
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{Math.Round(query.Latitude.Value, 3)},{Math.Round(query.Longitude.Value, 3)}")
            : "-";
        var radius = query.RadiusKm.HasValue
            ? Math.Round(query.RadiusKm.Value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        return string.Join(
            '|',
            provider,
            operation,
            normalisedQuery,
            area,
            coordinates,
            radius,
            TravelPlaceCategories.ToWireValue(query.Category),
            query.Language.ToLowerInvariant(),
            query.Limit);
    }

    public static string BuildDetailsKey(string provider, string providerPlaceId, string language)
        => string.Join('|', provider, "details", providerPlaceId, language.ToLowerInvariant());

    /// Case, surrounding space and repeated whitespace must not each produce
    /// their own cache entry for what is obviously the same question.
    private static string NormaliseText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return string.Join(' ', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
