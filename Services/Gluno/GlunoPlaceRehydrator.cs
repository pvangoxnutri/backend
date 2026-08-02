namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How a re-fetch ended.
///
/// Three of these produce different sentences, which is the whole reason the
/// enum exists — a place that is genuinely gone and a provider that is over its
/// rate limit are the same empty result and must not be the same message.
/// </summary>
public enum GlunoRehydrationStatus
{
    /// At least one reference came back.
    Ok,
    /// The provider answered and did not include the id, twice.
    NotFound,
    /// Over a rate limit or a quota. Worth trying again shortly.
    Busy,
    /// The provider could not be reached, or there is nothing to re-fetch from.
    Unavailable,
}

public sealed class GlunoRehydration
{
    public required GlunoRehydrationStatus Status { get; init; }

    /// <summary>
    /// Fresh places by option key.
    ///
    /// ONLY IDS THAT WERE ACTUALLY SHOWN. The re-run returns whatever the
    /// provider recommends today, and most of it is not what this user was
    /// offered; everything without a stored reference is dropped before this
    /// dictionary is built. So a user can only ever act on something they were
    /// shown, even though the data behind it is new.
    /// </summary>
    public required IReadOnlyDictionary<string, TravelPlace> Places { get; init; }

    /// Upstream calls spent. One normally, two when the fallback ran.
    public required int ProviderCalls { get; init; }

    public static GlunoRehydration Empty(GlunoRehydrationStatus status, int calls = 0) => new()
    {
        Status = status,
        Places = new Dictionary<string, TravelPlace>(StringComparer.Ordinal),
        ProviderCalls = calls,
    };
}

public static class GlunoPlaceCards
{
    /// <summary>
    /// A freshly fetched place as a card.
    ///
    /// Ranking signals are absent because a lookup by id was not ranked against
    /// anything, and claiming otherwise would put SideQuest's reasons on a
    /// result that never went through them.
    /// </summary>
    public static GlunoPlaceCard From(TravelPlace place) => new()
    {
        Provider = place.Provider,
        ExternalId = place.ExternalId,
        ProviderPlaceId = place.ProviderPlaceId,
        Name = place.Name,
        Category = place.Category,
        CategoryLabel = place.CategoryLabel,
        Address = place.Address,
        Latitude = place.Latitude,
        Longitude = place.Longitude,
        Rating = place.Rating,
        RatingScaleMax = place.RatingScaleMax,
        ReviewCount = place.ReviewCount,
        PriceLevel = place.PriceLevel,
        ImageUrl = place.ImageUrl,
        ProviderUrl = place.ProviderUrl,
        SourceAttribution = place.SourceAttribution,
        OpeningHours = place.OpeningHours,
        ReviewSummary = place.ReviewSummary,
        AllowsContentPersistence = place.AllowsContentPersistence,
        AllowsIdentityPersistence = place.AllowsIdentityPersistence,
    };

    /// <summary>
    /// A card read back out of a stored payload, with the two facts that do not
    /// survive the round trip put back.
    ///
    /// NEITHER FLAG IS SERIALISED — they decide what gets written, so writing
    /// them would be pointless, and a stored copy of a permission is a
    /// permission somebody could edit. But a card that is IN a payload was
    /// allowed to be there: that is the only way it could have been written.
    /// Its own presence is the proof, so content persistence is restored as
    /// true rather than defaulted to false.
    ///
    /// Getting this wrong is not a subtle failure. A card read back as
    /// unstorable would send the whole legacy add flow down the identity-only
    /// path, where it would look for a location id that was never serialised
    /// either — so the id is recovered from the namespaced one, which is.
    /// </summary>
    public static GlunoPlaceCard Restored(GlunoPlaceCard stored)
    {
        var providerPlaceId = stored.ProviderPlaceId;

        if (string.IsNullOrWhiteSpace(providerPlaceId)
            && TravelPlaceIds.TrySplit(stored.ExternalId, out _, out var parsed))
        {
            providerPlaceId = parsed;
        }

        return new GlunoPlaceCard
        {
            Provider = stored.Provider,
            ExternalId = stored.ExternalId,
            ProviderPlaceId = providerPlaceId,
            Name = stored.Name,
            Category = stored.Category,
            CategoryLabel = stored.CategoryLabel,
            Address = stored.Address,
            Latitude = stored.Latitude,
            Longitude = stored.Longitude,
            Rating = stored.Rating,
            RatingScaleMax = stored.RatingScaleMax,
            ReviewCount = stored.ReviewCount,
            PriceLevel = stored.PriceLevel,
            ImageUrl = stored.ImageUrl,
            ProviderUrl = stored.ProviderUrl,
            SourceAttribution = stored.SourceAttribution,
            DistanceKm = stored.DistanceKm,
            OpeningHours = stored.OpeningHours,
            ReviewSummary = stored.ReviewSummary,
            Signals = stored.Signals,
            AllowsContentPersistence = true,
            AllowsIdentityPersistence = true,
        };
    }
}

public interface IGlunoPlaceRehydrator
{
    /// <param name="requiredOptionKey">
    /// The one the user is acting on, when there is one. It decides whether the
    /// single permitted fallback call is worth making — a missing key nobody
    /// asked about is not worth a second call.
    /// </param>
    Task<GlunoRehydration> RehydrateAsync(
        IReadOnlyList<GlunoPlaceReference> references,
        GlunoPlaceSearchContext context,
        string? requiredOptionKey,
        CancellationToken ct);
}

/// <summary>
/// Fetches places again from ids, when their content was never stored.
///
/// THE PROBLEM. Tripadvisor Terra permits keeping a Location ID and forbids
/// keeping content. A stored id alone cannot build a proposal — a proposal
/// needs a title, a category and a place to put on a map. So the content has to
/// come from somewhere at the moment the user acts, and the only lawful
/// somewhere is the provider itself.
///
/// THE SHAPE OF THE CALL. Terra's id-addressable endpoints are governed by an
/// account allowlist, so asking for one location by id would 404 for any city
/// nobody registered in advance. The endpoint that found the place is
/// `recommendations/search`, which takes a query and a geography — so the way
/// to find it again is to make the same request again, with SideQuest's own
/// stored search context, and look for the id.
///
/// WHAT IS NOT DONE HERE, and each for a reason:
///
///  • No cache. A short-lived copy of the content would be exactly the storage
///    the policy forbids, and a time limit does not make storing something
///    permitted. The extra call is the price.
///
///  • No matching by name, position, distance or similarity. The fresh response
///    is a different list from a different day; the second entry today is not
///    the second entry from last week, and two places in one city share a name
///    more often than is comfortable. The id is exact or it is a miss.
///
///  • No retry loop. One call, and one fallback when the wanted id is missing.
///    An add that fails is a sentence; an add that quietly spends ten upstream
///    calls is a bill.
/// </summary>
public sealed class GlunoPlaceRehydrator : IGlunoPlaceRehydrator
{
    private readonly ITravelDataRegistry _travelData;
    private readonly ILogger<GlunoPlaceRehydrator> _logger;

    /// <summary>
    /// How wide the fallback asks.
    ///
    /// The realistic reason an id does not come back is that it slid down a
    /// recommendation list that is re-ranked upstream, so the lever that
    /// actually helps is depth, not a cleverer phrase — and depth is the only
    /// lever available, since narrowing the query would need the place's name
    /// and the name is the thing that was never stored.
    /// </summary>
    private const int FallbackLimit = 10;

    public GlunoPlaceRehydrator(ITravelDataRegistry travelData, ILogger<GlunoPlaceRehydrator> logger)
    {
        _travelData = travelData;
        _logger = logger;
    }

    public async Task<GlunoRehydration> RehydrateAsync(
        IReadOnlyList<GlunoPlaceReference> references,
        GlunoPlaceSearchContext context,
        string? requiredOptionKey,
        CancellationToken ct)
    {
        if (references.Count == 0 || !context.IsUsable)
        {
            // Nothing to look up, or nowhere to look. Distinct from "the
            // provider said no": there was no request to make.
            return GlunoRehydration.Empty(GlunoRehydrationStatus.Unavailable);
        }

        var first = await LookUpAsync(references, context, context.Limit, ct);

        if (Satisfies(first.Matched, requiredOptionKey))
        {
            Log(context, first.Status, first.Matched.Count, references.Count, calls: 1, fallback: false);

            return new GlunoRehydration
            {
                Status = GlunoRehydrationStatus.Ok,
                Places = first.Matched,
                ProviderCalls = 1,
            };
        }

        // ── The one permitted second call ─────────────────────────────────
        //
        // Only when the provider actually answered. Asking a rate-limited or
        // unreachable provider the same question twice in a row is not a
        // fallback, it is a second failure.
        if (first.Status != TravelSearchStatus.Ok && first.Status != TravelSearchStatus.Unknown)
        {
            Log(context, first.Status, first.Matched.Count, references.Count, calls: 1, fallback: false);

            return GlunoRehydration.Empty(
                first.Status == TravelSearchStatus.RateLimited
                    ? GlunoRehydrationStatus.Busy
                    : GlunoRehydrationStatus.Unavailable,
                calls: 1);
        }

        var second = await LookUpAsync(references, context, FallbackLimit, ct);

        // Whatever either call found. The first result is not thrown away just
        // because the second one ran — a list assembled from both is still only
        // ids this user was shown.
        var merged = new Dictionary<string, TravelPlace>(first.Matched, StringComparer.Ordinal);

        foreach (var (key, place) in second.Matched) merged[key] = place;

        Log(context, second.Status, merged.Count, references.Count, calls: 2, fallback: true);

        if (Satisfies(merged, requiredOptionKey))
        {
            return new GlunoRehydration
            {
                Status = GlunoRehydrationStatus.Ok,
                Places = merged,
                ProviderCalls = 2,
            };
        }

        return new GlunoRehydration
        {
            Status = second.Status == TravelSearchStatus.RateLimited
                ? GlunoRehydrationStatus.Busy
                : second.Status == TravelSearchStatus.Failed
                    ? GlunoRehydrationStatus.Unavailable
                    // The provider answered, twice, and the place was not in
                    // either answer. It is gone from the recommendations, and
                    // saying so is the honest end of this.
                    : GlunoRehydrationStatus.NotFound,
            Places = merged,
            ProviderCalls = 2,
        };
    }

    /// <summary>
    /// Whether the result is good enough to stop.
    ///
    /// With a required key, that key and nothing else — the other five coming
    /// back is no help to somebody trying to add the sixth. Without one, any
    /// match will do, because the caller is working out which of the shown
    /// places the user meant and can only offer the ones that came back.
    /// </summary>
    private static bool Satisfies(IReadOnlyDictionary<string, TravelPlace> matched, string? requiredOptionKey)
        => requiredOptionKey != null ? matched.ContainsKey(requiredOptionKey) : matched.Count > 0;

    private async Task<(Dictionary<string, TravelPlace> Matched, TravelSearchStatus Status)> LookUpAsync(
        IReadOnlyList<GlunoPlaceReference> references,
        GlunoPlaceSearchContext context,
        int limit,
        CancellationToken ct)
    {
        var matched = new Dictionary<string, TravelPlace>(StringComparer.Ordinal);

        TravelSearchResult result;

        try
        {
            result = await _travelData.SearchAllAsync(
                new TravelPlaceQuery
                {
                    // The stored context, replayed. The same builder turns it
                    // into the same upstream query the original search used, so
                    // this is the same question asked again rather than a new
                    // one that happens to be similar.
                    Query = context.Query ?? string.Empty,
                    Near = context.Near,
                    Category = TravelPlaceCategories.Parse(context.Category),
                    Limit = limit,
                    Language = context.Language,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[GLUNO] place rehydration call failed: {Category}", ex.GetType().Name);
            return (matched, TravelSearchStatus.Failed);
        }

        // ── Exact id, and only exact id ───────────────────────────────────
        //
        // Provider AND bare id both have to agree. The provider part matters
        // because two products from the same company can be behind one provider
        // name; the bare id is the identity.
        foreach (var reference in references)
        {
            var place = result.Places.FirstOrDefault(candidate =>
                string.Equals(candidate.Provider, reference.ProviderId, StringComparison.Ordinal)
                && string.Equals(candidate.ProviderPlaceId, reference.LocationId, StringComparison.Ordinal));

            if (place != null) matched[reference.OptionKey] = place;
        }

        return (matched, result.Status);
    }

    /// Counts, a category and a status. Never an id, never a name, never the
    /// query — the geography is the user's own destination and stays out too.
    private void Log(
        GlunoPlaceSearchContext context,
        TravelSearchStatus status,
        int matched,
        int wanted,
        int calls,
        bool fallback)
        => _logger.LogInformation(
            "[GLUNO] place rehydration status={Status} category={Category} matched={Matched}/{Wanted} "
            + "calls={Calls} fallback={Fallback} ageMinutes={Age}",
            status, context.Category, matched, wanted, calls, fallback,
            (int)(DateTime.UtcNow - context.SearchedAtUtc).TotalMinutes);
}
