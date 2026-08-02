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
        var startedAt = DateTime.UtcNow;

        // ── One provider per place-id namespace ───────────────────────────
        //
        // Terra and the Content API are two products from the same company and
        // issue the SAME location ids, so both being configured would mean two
        // upstream calls, two bills and a deduplicated list where each place
        // came from whichever answered first — with the other's ratings
        // possibly attached.
        //
        // Registration order is the priority, and Terra is registered first:
        // Content API keys stop working 2026-08-31, so the newer platform wins
        // wherever both are available.
        var configured = _providers
            .Where(provider => provider.IsConfigured)
            .GroupBy(provider => provider.Provider, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        // ── Not configured is not "no results" ────────────────────────────
        //
        // THE BUG THIS LOGS. An unconfigured provider returned an empty list,
        // and so did a provider that answered with nothing, and so did one
        // that timed out. Four different situations, one indistinguishable
        // outcome — so a production Gluno that had never had a Tripadvisor key
        // looked exactly like one whose search found nothing in Sevilla, and
        // the only visible symptom was prose where place cards should have
        // been.
        //
        // Counts and a category. Never the query, never a key, never a body.
        if (configured.Count == 0)
        {
            _logger.LogWarning(
                "[GLUNO] place search skipped reason=not_configured providers={Total}",
                _providers.Count);

            return Array.Empty<RankedTravelPlace>();
        }

        var collected = new List<TravelPlace>();
        var failed = 0;

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
                failed++;

                // Provider name and failure category only — never the query,
                // never a key, never a response body.
                _logger.LogWarning(
                    "[GLUNO] travel provider {Provider} search failed: {Category}",
                    provider.Provider, ex.GetType().Name);
            }
        }

        var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

        if (collected.Count == 0)
        {
            // Every provider threw, versus every provider genuinely answered
            // with nothing. The first is an outage; the second is a real
            // answer about Sevilla, and they need different fixes.
            _logger.LogInformation(
                "[GLUNO] place search empty reason={Reason} providers={Providers} "
                + "failed={Failed} category={Category} in {Elapsed}ms",
                failed == configured.Count ? "all_providers_failed" : "provider_returned_zero",
                configured.Count, failed, query.Category, elapsed);

            return Array.Empty<RankedTravelPlace>();
        }

        // Same place from two providers stays two results — they carry
        // different ratings and attribution, and silently merging them would
        // mix one provider's numbers with another's name.
        var deduped = collected
            .GroupBy(place => place.ExternalId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var ranked = TravelPlaceRanker.Rank(deduped, query)
            .Take(Math.Clamp(query.Limit, 1, 10))
            .ToList();

        // ── The other silent empty ────────────────────────────────────────
        //
        // "The provider found nothing" and "the provider found things and our
        // own code discarded all of them" look identical from outside and have
        // completely different fixes. Distinguishing them is the difference
        // between chasing an API key and chasing a mapping bug.
        _logger.LogInformation(
            "[GLUNO] place search done raw={Raw} deduped={Deduped} ranked={Ranked} "
            + "category={Category} in {Elapsed}ms",
            collected.Count, deduped.Count, ranked.Count, query.Category, elapsed);

        if (ranked.Count == 0)
        {
            _logger.LogWarning(
                "[GLUNO] place search dropped every result raw={Raw} reason=mapping_or_ranking",
                collected.Count);
        }

        return ranked;
    }

    /// <summary>
    /// Everything the configured providers returned, in their own order.
    ///
    /// NOT RANKED AND NOT TRIMMED, unlike the search above. This exists for
    /// looking a known id back up, where SideQuest's relevance score is beside
    /// the point and trimming to the requested count could throw away the one
    /// result the caller came for.
    ///
    /// The status is the worst thing that happened: if one provider was rate
    /// limited and another simply had nothing, the caller must not be told
    /// "nothing" — it would be the difference between "try again shortly" and
    /// "that place is gone".
    /// </summary>
    public async Task<TravelSearchResult> SearchAllAsync(TravelPlaceQuery query, CancellationToken ct)
    {
        var configured = _providers
            .Where(provider => provider.IsConfigured)
            .GroupBy(provider => provider.Provider, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (configured.Count == 0)
        {
            return new TravelSearchResult
            {
                Places = Array.Empty<TravelPlace>(),
                Status = TravelSearchStatus.Failed,
            };
        }

        var collected = new List<TravelPlace>();
        var status = TravelSearchStatus.Ok;

        foreach (var provider in configured)
        {
            try
            {
                var result = await provider.SearchPlacesWithStatusAsync(query, ct);

                collected.AddRange(result.Places);
                status = Worse(status, result.Status);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status = Worse(status, TravelSearchStatus.Failed);

                _logger.LogWarning(
                    "[GLUNO] travel provider {Provider} lookup failed: {Category}",
                    provider.Provider, ex.GetType().Name);
            }
        }

        return new TravelSearchResult
        {
            Places = collected
                .GroupBy(place => place.ExternalId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList(),
            Status = status,
        };
    }

    /// <summary>
    /// The more serious of two statuses.
    ///
    /// Rate limited outranks failed: it is the only one that means "later", and
    /// a caller that hears "failed" would stop suggesting the retry that would
    /// have worked.
    /// </summary>
    private static TravelSearchStatus Worse(TravelSearchStatus current, TravelSearchStatus next)
    {
        if (current == TravelSearchStatus.RateLimited || next == TravelSearchStatus.RateLimited)
            return TravelSearchStatus.RateLimited;

        if (current == TravelSearchStatus.Failed || next == TravelSearchStatus.Failed)
            return TravelSearchStatus.Failed;

        // Unknown means the provider does not report. It is not evidence of
        // health, so it never upgrades a status.
        return current == TravelSearchStatus.Unknown || next == TravelSearchStatus.Unknown
            ? TravelSearchStatus.Unknown
            : TravelSearchStatus.Ok;
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
