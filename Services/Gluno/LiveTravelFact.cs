namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What kind of live information a fact is.
///
/// Closed list. <see cref="Unknown"/> is a real answer — something travel-shaped
/// that does not fit is better labelled unknown than forced into "closure",
/// because the category decides how strongly the planner reacts to it.
/// </summary>
public static class LiveTravelCategories
{
    public const string Event = "event";
    public const string Closure = "closure";
    public const string Strike = "strike";
    public const string TransportDisruption = "transport_disruption";
    public const string RoadDisruption = "road_disruption";
    public const string BorderInformation = "border_information";
    public const string TravelAdvisory = "travel_advisory";
    public const string WeatherWarning = "weather_warning";
    public const string PublicHoliday = "public_holiday";
    public const string TemporaryRule = "temporary_rule";
    public const string SafetyNotice = "safety_notice";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All =
    [
        Event, Closure, Strike, TransportDisruption, RoadDisruption,
        BorderInformation, TravelAdvisory, WeatherWarning, PublicHoliday,
        TemporaryRule, SafetyNotice, Unknown,
    ];

    public static bool IsKnown(string? value) => value != null && All.Contains(value);

    /// <summary>
    /// Categories where being wrong can strand somebody or put them in danger.
    ///
    /// These get the strictest treatment everywhere: an official first-party
    /// source is strongly preferred, the freshness window is short, and Gluno
    /// is required to point at the operator or authority for the final check
    /// rather than answering for them.
    /// </summary>
    public static bool IsCritical(string category) => category is Strike
        or TransportDisruption or BorderInformation or TravelAdvisory
        or WeatherWarning or SafetyNotice;
}

/// <summary>
/// How much weight a source carries.
///
/// The ORDER is the point. A transport operator saying its own ferry is
/// cancelled is a fact about that ferry; a news site reporting the same thing
/// is a report about a fact; a forum post is somebody's impression. All three
/// can be true and they are not the same kind of claim, and collapsing them is
/// how "someone on Reddit said the border is closed" becomes travel advice.
/// </summary>
public enum LiveSourceTier
{
    /// A government, ministry, embassy or civil-protection agency.
    OfficialAuthority = 0,
    /// The operator of the thing itself — the railway, the ferry line, the bus company.
    TransportOperator = 1,
    /// Airports, stations, ports — infrastructure speaking about itself.
    InfrastructureOperator = 2,
    /// A city's or region's own tourism and events site.
    OfficialDestination = 3,
    /// The organiser of the event, on their own channel.
    VerifiedOrganiser = 4,
    /// Established press. Used when no first-party source has spoken.
    TrustedNews = 5,
    /// Everything else. Always labelled secondary, never the sole basis of a
    /// critical claim.
    Secondary = 6,
}

public static class LiveSourceTiers
{
    /// Tiers that speak with first-party authority about their own domain.
    public static bool IsOfficial(LiveSourceTier tier) => tier <= LiveSourceTier.OfficialDestination;

    /// <summary>
    /// Whether a tier alone is enough to state a critical fact.
    ///
    /// News is deliberately excluded. Reporting that a strike is planned is
    /// real information, and it is not the operator confirming its own
    /// timetable — which is what somebody deciding whether to leave for the
    /// station actually needs.
    /// </summary>
    public static bool CanCarryCriticalClaim(LiveSourceTier tier) => tier <= LiveSourceTier.InfrastructureOperator;

    public static string ToWireValue(LiveSourceTier tier) => tier switch
    {
        LiveSourceTier.OfficialAuthority => "official_authority",
        LiveSourceTier.TransportOperator => "transport_operator",
        LiveSourceTier.InfrastructureOperator => "infrastructure_operator",
        LiveSourceTier.OfficialDestination => "official_destination",
        LiveSourceTier.VerifiedOrganiser => "verified_organiser",
        LiveSourceTier.TrustedNews => "trusted_news",
        _ => "secondary",
    };
}

/// <summary>
/// Whether a fact applies to the dates somebody is actually travelling.
/// </summary>
public enum LiveRecency
{
    /// In effect now, and over the traveller's dates.
    Current,
    /// Starts after the question's window but still relevant.
    Upcoming,
    /// Over. A strike that ended last month is not travel information.
    Expired,
    /// The dates could not be established. Never presented as current.
    Unclear,
}

/// <summary>
/// One piece of live travel information, normalised away from any provider.
///
/// TWO DATES, AND THEY ARE NOT THE SAME DATE. <see cref="PublishedAt"/> is when
/// somebody wrote it down; <see cref="EffectiveFrom"/> is when the thing
/// actually happens. Conflating them is the single most common way live travel
/// data goes wrong: an article published this morning about last spring's rail
/// strike is fresh, prominent, and completely irrelevant — and a system that
/// sorts by publication date will put it at the top.
/// </summary>
public sealed record LiveTravelFact
{
    public required string Id { get; init; }

    /// <see cref="LiveTravelCategories"/>.
    public required string Category { get; init; }

    /// Short, sanitised, capped. Untrusted text from outside SideQuest.
    public required string Title { get; init; }

    /// A few sentences at most. Also untrusted, also capped.
    public required string Summary { get; init; }

    public string? LocationLabel { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// "Catalonia", "Nice city centre", "national". Free text from the source.
    public string? AffectedArea { get; init; }

    /// <summary>
    /// When the thing itself starts. This — not the publication date — is what
    /// decides whether it matters to a traveller.
    /// </summary>
    public DateOnly? EffectiveFrom { get; init; }

    /// <summary>
    /// Null means the source did not say. It does NOT mean "forever": an
    /// open-ended closure that quietly stays active in the plan for a year is
    /// worse than one the user is told has no stated end.
    /// </summary>
    public DateOnly? EffectiveUntil { get; init; }

    public DateTime? PublishedAt { get; init; }

    /// When SideQuest last confirmed this from the source.
    public DateTime VerifiedAt { get; init; } = DateTime.UtcNow;

    /// "info" | "low" | "medium" | "high". Internal — never rendered as a code.
    public string Severity { get; init; } = "info";

    /// What the source itself says the status is: "active", "planned",
    /// "resolved", "cancelled". Null when it did not say.
    public string? OfficialStatus { get; init; }

    public required string SourceName { get; init; }
    public required LiveSourceTier SourceTier { get; init; }

    /// Validated https, on a public host. See <see cref="GlunoUrlGuard"/>.
    public string? SourceUrl { get; init; }

    /// ISO country of the source, when known. Helps judge whose authority it is.
    public string? SourceCountry { get; init; }

    public string Language { get; init; } = "en";

    public double Confidence { get; init; } = 0.5;

    public bool IsOfficial => LiveSourceTiers.IsOfficial(SourceTier);

    /// Set by <see cref="GlunoLiveRecency"/> against the traveller's dates.
    public LiveRecency Recency { get; init; } = LiveRecency.Unclear;

    public bool IsCurrent => Recency == LiveRecency.Current;

    /// walking | driving | transit | cycling, when the fact is about one.
    public string? RelatedTransportMode { get; init; }

    /// Namespaced place id, when the fact is about a place we already know.
    public string? RelatedPlaceExternalId { get; init; }

    /// Machine codes: "no_end_date", "secondary_source_only", "date_unclear".
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Two sources that disagree about the same thing.
///
/// Kept rather than resolved. A news site reporting a cancellation while the
/// operator's own page shows normal service is genuinely useful to a traveller
/// — and picking one silently would hide exactly the uncertainty they need to
/// act on.
/// </summary>
public sealed record LiveTravelConflict(
    LiveTravelFact Official,
    LiveTravelFact Reported,
    string Kind)
{
    /// The one to lead with, when precedence is clear. Null when it genuinely
    /// needs the user to check.
    public LiveTravelFact? Preferred { get; init; }
}
