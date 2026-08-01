using System.Collections.Concurrent;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What one turn consumed.
/// </summary>
public sealed record GlunoTurnUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int ModelRounds { get; init; }
    public int ProviderCalls { get; init; }
    public int RoutingElements { get; init; }
    public int PlaceHydrations { get; init; }
    public int Regenerations { get; init; }
}

public enum GlunoUsageVerdict
{
    Allowed,
    /// This user has had a lot today. Their existing conversations still open.
    UserLimitReached,
    /// The whole deployment is over its ceiling. A circuit breaker, not a tier.
    GlobalLimitReached,
}

/// <summary>
/// Usage and cost control.
///
/// WHAT THIS IS NOT: a paywall, a plan tier, or anything the user should ever
/// see priced. It is a runaway backstop. The failure it exists to prevent is a
/// loop, a bug, or one enthusiastic afternoon turning into a bill nobody
/// noticed until the invoice.
///
/// WHY PRICES ARE CONFIGURATION. Model prices change, and a hardcoded rate
/// silently becomes a lie — the estimate keeps looking authoritative while
/// drifting further from the truth. Unset prices mean cost is simply not
/// estimated, which is honest; a wrong number would be worse than none.
///
/// WHAT HAPPENS AT THE LIMIT, and this is the part worth getting right: Gluno
/// stops answering. Nothing else changes. Existing conversations open and
/// scroll, every other part of SideQuest works exactly as before, and the
/// message the user sees is neutral and human. A usage ceiling must never look
/// like the app is broken.
///
/// In-memory, matching the presence throttle and the travel-data cache: single
/// instance, and a counter that resets on deploy is the correct trade for a
/// backstop nobody should be hitting.
/// </summary>
public sealed class GlunoUsageBudget
{
    private sealed class Window
    {
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public int Turns;
        public long InputTokens;
        public long OutputTokens;
    }

    private readonly ConcurrentDictionary<Guid, Window> _hourly = new();
    private readonly ConcurrentDictionary<Guid, Window> _daily = new();
    private readonly Window _global = new();
    private readonly object _globalLock = new();

    private readonly IConfiguration _config;
    private readonly ILogger<GlunoUsageBudget> _logger;

    public GlunoUsageBudget(IConfiguration config, ILogger<GlunoUsageBudget> logger)
    {
        _config = config;
        _logger = logger;
    }

    // Development defaults: generous enough that nobody testing hits them by
    // accident, finite enough that a runaway loop stops within one coffee.
    private int UserHourlyTurns => Math.Max(1, _config.GetValue("Gluno:Usage:UserHourlyTurns", 60));
    private int UserDailyTurns => Math.Max(1, _config.GetValue("Gluno:Usage:UserDailyTurns", 300));
    private long GlobalDailyOutputTokens
        => Math.Max(1000, _config.GetValue<long>("Gluno:Usage:GlobalDailyOutputTokens", 5_000_000));

    /// Per million tokens. Unset means cost is not estimated at all.
    private decimal? InputPricePerMillion => _config.GetValue<decimal?>("Gluno:Pricing:InputPerMillion");
    private decimal? OutputPricePerMillion => _config.GetValue<decimal?>("Gluno:Pricing:OutputPerMillion");

    /// <summary>
    /// Whether this user may start a turn.
    ///
    /// Checked BEFORE any work, so a user over their limit costs nothing.
    /// </summary>
    public GlunoUsageVerdict CheckAllowed(Guid userId)
    {
        lock (_globalLock)
        {
            RollIfExpired(_global, TimeSpan.FromDays(1));
            if (_global.OutputTokens >= GlobalDailyOutputTokens)
            {
                _logger.LogWarning("[GLUNO] global daily usage ceiling reached");
                return GlunoUsageVerdict.GlobalLimitReached;
            }
        }

        var hourly = _hourly.GetOrAdd(userId, _ => new Window());
        lock (hourly)
        {
            RollIfExpired(hourly, TimeSpan.FromHours(1));
            if (hourly.Turns >= UserHourlyTurns) return GlunoUsageVerdict.UserLimitReached;
        }

        var daily = _daily.GetOrAdd(userId, _ => new Window());
        lock (daily)
        {
            RollIfExpired(daily, TimeSpan.FromDays(1));
            if (daily.Turns >= UserDailyTurns) return GlunoUsageVerdict.UserLimitReached;
        }

        return GlunoUsageVerdict.Allowed;
    }

    /// <summary>
    /// Records what a finished turn consumed.
    ///
    /// Counters only. Nothing about WHAT was asked or answered — the usage
    /// ledger must not become a shadow copy of the conversation.
    /// </summary>
    public void Record(Guid userId, GlunoTurnUsage usage)
    {
        Add(_hourly.GetOrAdd(userId, _ => new Window()), TimeSpan.FromHours(1), usage);
        Add(_daily.GetOrAdd(userId, _ => new Window()), TimeSpan.FromDays(1), usage);

        lock (_globalLock)
        {
            RollIfExpired(_global, TimeSpan.FromDays(1));
            _global.Turns++;
            _global.InputTokens += usage.InputTokens;
            _global.OutputTokens += usage.OutputTokens;
        }
    }

    /// <summary>
    /// A rough cost, in whatever currency the prices are configured in.
    ///
    /// Null when prices are not configured — an unpriced deployment gets no
    /// estimate rather than a made-up one.
    /// </summary>
    public decimal? EstimateCost(GlunoTurnUsage usage)
    {
        if (InputPricePerMillion is not { } inputPrice || OutputPricePerMillion is not { } outputPrice)
            return null;

        return (usage.InputTokens / 1_000_000m * inputPrice)
             + (usage.OutputTokens / 1_000_000m * outputPrice);
    }

    /// <summary>
    /// A coarse bucket for telemetry — "nano", "micro", "small", "medium",
    /// "large".
    ///
    /// A bucket rather than the number, because per-turn cost correlates with
    /// how much someone is planning and how elaborate their trip is. That is
    /// closer to personal information than it looks, and a bucket answers every
    /// operational question the exact figure would.
    /// </summary>
    public string CostBucket(GlunoTurnUsage usage)
    {
        var cost = EstimateCost(usage);
        if (cost == null) return "unpriced";

        return cost switch
        {
            < 0.005m => "nano",
            < 0.02m => "micro",
            < 0.10m => "small",
            < 0.40m => "medium",
            _ => "large",
        };
    }

    private static void Add(Window window, TimeSpan length, GlunoTurnUsage usage)
    {
        lock (window)
        {
            RollIfExpired(window, length);
            window.Turns++;
            window.InputTokens += usage.InputTokens;
            window.OutputTokens += usage.OutputTokens;
        }
    }

    /// A fixed window rather than a sliding one: cheaper, and precision at the
    /// boundary does not matter for a backstop.
    private static void RollIfExpired(Window window, TimeSpan length)
    {
        if (DateTime.UtcNow - window.StartedAtUtc < length) return;

        window.StartedAtUtc = DateTime.UtcNow;
        window.Turns = 0;
        window.InputTokens = 0;
        window.OutputTokens = 0;
    }
}
