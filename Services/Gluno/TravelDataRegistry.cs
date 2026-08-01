namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The composition point for external travel data.
///
/// Callers ask the registry, never a provider directly, so "which provider"
/// stays a composition-root decision (Program.cs) and no caller has to handle
/// the zero-providers case itself. Today Tripadvisor is the only entry; adding
/// a second is one DI registration, and nothing above this line changes.
///
/// Two responsibilities beyond fan-out:
///
///  • **Ranking.** Provider order is never SideQuest's order. Results from all
///    providers are merged and re-ranked by <see cref="TravelPlaceRanker"/>,
///    so a provider's own idea of relevance cannot leak out as if it were
///    SideQuest's recommendation.
///
///  • **Routing by namespace.** A details lookup is dispatched on the id's
///    provider prefix, so one provider's id can never be sent to another.
///
/// Failure policy: a provider that throws is skipped, never propagated. An
/// external lookup failing must degrade Gluno's answer, never end the turn.
/// </summary>
public sealed class TravelDataRegistry : ITravelDataRegistry
{
    private readonly IReadOnlyList<ITravelDataProvider> _providers;
    private readonly ILogger<TravelDataRegistry> _logger;

    public TravelDataRegistry(IEnumerable<ITravelDataProvider> providers, ILogger<TravelDataRegistry> logger)
    {
        // Materialised once, but IsConfigured is re-read per call below —
        // configuration can be reloaded at runtime, and a provider that was
        // keyless at startup should start working when a key appears.
        _providers = providers.ToList();
        _logger = logger;
    }

    public bool HasConfiguredProvider => _providers.Any(provider => provider.IsConfigured);

    public async Task<IReadOnlyList<RankedTravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
    {
        var configured = _providers.Where(provider => provider.IsConfigured).ToList();
        if (configured.Count == 0) return Array.Empty<RankedTravelPlace>();

        var collected = new List<TravelPlace>();
        foreach (var provider in configured)
        {
            try
            {
                collected.AddRange(await provider.SearchPlacesAsync(query, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Provider name and failure category only — never the query,
                // never a key, never a response body.
                _logger.LogWarning(
                    "[GLUNO] travel provider {Provider} search failed: {Category}",
                    provider.Provider, ex.GetType().Name);
            }
        }

        if (collected.Count == 0) return Array.Empty<RankedTravelPlace>();

        // Same place from two providers stays two results — they carry
        // different ratings and attribution, and silently merging them would
        // mix one provider's numbers with another's name.
        var deduped = collected
            .GroupBy(place => place.ExternalId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        return TravelPlaceRanker.Rank(deduped, query)
            .Take(Math.Clamp(query.Limit, 1, 10))
            .ToList();
    }

    public async Task<TravelPlace?> GetPlaceDetailsAsync(string externalId, string language, CancellationToken ct)
    {
        if (!TravelPlaceIds.TrySplit(externalId, out var providerId, out var providerPlaceId)) return null;

        var provider = _providers.FirstOrDefault(candidate =>
            candidate.IsConfigured
            && string.Equals(candidate.Provider, providerId, StringComparison.OrdinalIgnoreCase));

        if (provider == null) return null;

        try
        {
            return await provider.GetPlaceDetailsAsync(providerPlaceId, language, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[GLUNO] travel provider {Provider} details failed: {Category}",
                provider.Provider, ex.GetType().Name);
            return null;
        }
    }
}
