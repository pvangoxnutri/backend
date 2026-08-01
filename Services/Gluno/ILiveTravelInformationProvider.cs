namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What to look for. Deliberately narrow — see GlunoLiveSearchPlanner for why
/// the traveller's plan never reaches a search provider.
/// </summary>
public sealed class LiveTravelQuery
{
    /// A town, region or country. Never coordinates alone, never an Adventure
    /// title.
    public string? Destination { get; init; }

    /// <see cref="LiveTravelCategories"/>.
    public required string Category { get; init; }

    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }

    /// Used only to rank what came back — never sent upstream as the query.
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// Sources to read per search. Bounded by configuration.
    public int MaxSources { get; init; } = 4;

    public string Language { get; init; } = "en";
}

/// <summary>
/// One source of live travel information.
///
/// Deliberately an interface with no vendor in its shape. Gluno's orchestration
/// must not be able to tell whether the answer came from a search API, a
/// government feed, or a future partner integration — swapping one for another
/// is a Program.cs edit and a new implementation, exactly like the routing and
/// place layers.
///
/// Credentials are server-side only. Nothing here accepts a URL from a caller,
/// and nothing above this line ever sees one that has not been validated.
/// </summary>
public interface ILiveTravelInformationProvider
{
    string Provider { get; }

    /// False when there is no key or the integration is switched off. Gluno
    /// then plans from what SideQuest already holds and says it could not check.
    bool IsConfigured { get; }

    Task<IReadOnlyList<LiveTravelFact>> SearchAsync(LiveTravelQuery query, CancellationToken ct);
}

/// <summary>
/// The façade the rest of Gluno talks to.
///
/// Adds what a single provider should not own: caching with per-category
/// lifetimes, in-flight deduplication, a per-turn budget, recency
/// classification against the traveller's own dates, and conflict detection
/// between sources that disagree.
/// </summary>
public interface ILiveTravelRegistry
{
    /// <summary>
    /// True when live information can be fetched at all. Drives what
    /// /api/gluno/status reports — a boolean, never the provider's identity.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs the planned searches and returns ranked, date-classified facts.
    ///
    /// Never throws on a provider failure: an empty result and a recorded
    /// degradation is the correct outcome, because SideQuest's own planning
    /// still works without it.
    /// </summary>
    Task<LiveTravelResult> SearchAsync(
        GlunoLiveSearchPlan plan, DateOnly windowStart, DateOnly? windowEnd, string language, CancellationToken ct);
}

public sealed class LiveTravelResult
{
    public IReadOnlyList<LiveTravelFact> Facts { get; init; } = Array.Empty<LiveTravelFact>();

    /// Sources that disagree. Kept rather than resolved — see LiveTravelConflict.
    public IReadOnlyList<LiveTravelConflict> Conflicts { get; init; } = Array.Empty<LiveTravelConflict>();

    /// True when the provider failed or was skipped. The answer degrades to
    /// SideQuest's own planning and says so.
    public bool ProviderFailed { get; init; }

    public int SearchesUsed { get; init; }
    public bool WasCacheHit { get; init; }

    public static LiveTravelResult Empty => new();
    public static LiveTravelResult Failed => new() { ProviderFailed = true };
}
