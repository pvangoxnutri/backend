namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How much of the intended pipeline actually ran.
///
/// Ordered from best to worst, and reported honestly: the evidence ledger and
/// the answer must both reflect the level that was really achieved, not the one
/// that was planned.
/// </summary>
public enum GlunoDegradationLevel
{
    /// Everything the plan asked for.
    Full = 0,
    /// One optional enrichment missing — usually routing or weather.
    MinorDegradation = 1,
    /// A primary source missing. Places without routing, or a plan built from
    /// the Adventure alone.
    MajorDegradation = 2,
    /// Only what SideQuest already holds. No external data at all.
    LocalOnly = 3,
    /// Not enough to answer usefully; an honest fallback sentence instead.
    SafeFallback = 4,
}

/// <summary>
/// Tracks which providers failed during a turn, and what that leaves possible.
///
/// TWO RULES THAT MATTER MORE THAN THE LADDER ITSELF.
///
/// A tool failure never restarts the turn. Everything already gathered stays
/// gathered — a failed weather call must not throw away three place lookups
/// that succeeded. Restarting is the intuitive reaction and it triples the cost
/// of a bad minute at a provider.
///
/// A provider that failed is not called again this turn. Once Tripadvisor has
/// timed out, the next call will almost certainly time out too, and the user
/// pays for it in seconds they are sitting there waiting. One failure marks the
/// provider down for the rest of the turn.
///
/// The third rule is stated everywhere in this codebase and bears repeating
/// here: degradation NEVER means substituting invented data. A missing rating
/// stays missing. The ladder is about doing less, never about making more up.
/// </summary>
public sealed class GlunoDegradationTracker
{
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly List<string> _missing = [];

    public GlunoDegradationLevel Level { get; private set; } = GlunoDegradationLevel.Full;

    /// <summary>
    /// Records that a provider failed, and drops the level accordingly.
    /// </summary>
    /// <param name="provider">"tripadvisor", "routing", "weather".</param>
    public void RecordFailure(string provider)
    {
        if (!_failed.Add(provider)) return;

        _missing.Add(provider);

        // Weather and routing are enrichment: a plan without them is a slightly
        // less precise plan. Places are the substance of a recommendation, so
        // losing them costs a level more.
        var cost = provider switch
        {
            "tripadvisor" => GlunoDegradationLevel.MajorDegradation,
            _ => GlunoDegradationLevel.MinorDegradation,
        };

        if (cost > Level) Level = cost;

        // Everything external gone means local data only, whatever the
        // individual costs added up to.
        if (_failed.Count >= 3) Level = GlunoDegradationLevel.LocalOnly;
    }

    /// <summary>
    /// Whether a provider is still worth calling.
    ///
    /// False after one failure. Retrying a provider that has already failed
    /// this turn spends the user's remaining latency budget on the outcome we
    /// have the most evidence for.
    /// </summary>
    public bool ShouldTry(string provider) => !_failed.Contains(provider);

    public IReadOnlyList<string> MissingProviders => _missing;

    public void DropTo(GlunoDegradationLevel level)
    {
        if (level > Level) Level = level;
    }

    /// <summary>
    /// One short clause naming what was missing, for the answer.
    ///
    /// At most two sources — a list of everything that went wrong reads as an
    /// outage report rather than an answer.
    /// </summary>
    public string? Note(string language)
    {
        if (_missing.Count == 0) return null;

        var reason = _missing[0] switch
        {
            "tripadvisor" => GlunoFallbackReason.TripadvisorUnavailable,
            "routing" => GlunoFallbackReason.RoutingUnavailable,
            "weather" => GlunoFallbackReason.WeatherUnavailable,
            _ => GlunoFallbackReason.GroundingFailed,
        };

        return GlunoFallbacks.Note(reason, language);
    }
}
