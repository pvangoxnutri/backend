using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Where a piece of evidence came from.
///
/// The distinction that matters most is first-party versus third-party.
/// SideQuest's own database is authoritative about the user's plan and nothing
/// else; Tripadvisor is authoritative about a place's rating and nothing else.
/// Collapsing the two is how "your hotel is rated 4.5" ends up being said about
/// an Activity the user typed in themselves.
/// </summary>
public static class GlunoEvidenceSources
{
    /// The user's own Adventure, read from our database.
    public const string SideQuestDatabase = "sidequest_db";
    /// TripAnalyzer's measured findings. SideQuest's reading, not a fact about
    /// the world.
    public const string SideQuestAnalysis = "sidequest_analysis";
    /// Something the user said, this conversation.
    public const string UserMessage = "user_message";
    /// A preference they stated and we stored.
    public const string StoredPreference = "stored_preference";
    public const string Tripadvisor = "tripadvisor";
    public const string WeatherProvider = "weather_provider";
    public const string RoutingProvider = "routing_provider";
    /// Opening hours specifically — separate from the place provider because it
    /// ages far faster than a rating does.
    public const string OpeningHoursProvider = "opening_hours_provider";
    public const string CapabilityRegistry = "capability_registry";
    /// Gluno's own reasoning. Evidence that it THOUGHT something, never that
    /// something is true.
    public const string PlanningInference = "planning_inference";

    /// <summary>
    /// A booking document the user uploaded and reviewed. Their own data, read
    /// by a machine — authoritative about their booking, and only after review.
    /// </summary>
    public const string DocumentAnalysis = "document_analysis";

    /// <summary>
    /// Current information from outside SideQuest — strikes, closures, events.
    ///
    /// Its own source type because it ages differently from everything else
    /// here and carries an official-versus-reported distinction that no other
    /// source has.
    /// </summary>
    public const string LiveTravelInformation = "live_travel_information";

    public static readonly IReadOnlyList<string> All =
    [
        SideQuestDatabase, SideQuestAnalysis, UserMessage, StoredPreference,
        Tripadvisor, WeatherProvider, RoutingProvider, OpeningHoursProvider,
        CapabilityRegistry, PlanningInference, DocumentAnalysis, LiveTravelInformation,
    ];

    /// True for sources outside SideQuest. These carry attribution obligations
    /// and freshness limits that first-party data does not.
    public static bool IsExternal(string source) => source is Tripadvisor
        or WeatherProvider or RoutingProvider or OpeningHoursProvider or LiveTravelInformation;
}

/// <summary>
/// What kind of thing Gluno is asserting.
///
/// Every sentence with a fact in it is one of these, and each has different
/// rules about what may back it. The whole grounding system is built on the
/// premise that these are genuinely different kinds of statement — "it is 4.2
/// km away" and "it is an 18-minute drive" look alike and are not alike at all.
/// </summary>
public static class GlunoClaimTypes
{
    /// Something about the user's own Adventure. Only SideQuest's database.
    public const string TripFact = "trip_fact";
    /// A rating, review count, price band, address. Only a place provider.
    public const string ProviderFact = "provider_fact";
    /// What the weather is doing RIGHT NOW. Almost never available.
    public const string CurrentWeather = "current_weather";
    /// What the weather is expected to do, on a stated date and place.
    public const string Forecast = "forecast";
    /// A travel time. Only a routing provider.
    public const string VerifiedRouteTime = "verified_route_time";
    /// A distance measured from coordinates. Never a time.
    public const string StraightLineDistance = "straight_line_distance";
    public const string VerifiedOpeningHours = "verified_opening_hours";
    /// Something the user told us about how they travel.
    public const string UserPreference = "user_preference";
    /// What SideQuest the app can do. Only the capability registry.
    public const string AppCapability = "app_capability";
    /// Gluno's judgement about a plan. Valuable, and not a fact.
    public const string PlanningAssessment = "planning_assessment";
    /// Something Gluno is taking for granted, stated as such.
    public const string Assumption = "assumption";
    /// A recommendation.
    public const string Suggestion = "suggestion";
    /// Explicitly not known. Must never be phrased as a confident fact.
    public const string Unknown = "unknown";

    /// <summary>
    /// A disruption, closure, strike or event from outside SideQuest.
    ///
    /// Separate from <see cref="ProviderFact"/> because the honest sentence is
    /// different: a rating is simply true, while "the ferry is cancelled" is
    /// true AS OF a moment, according to somebody, and needs both attached.
    /// </summary>
    public const string LiveTravelFact = "live_travel_fact";

    /// Something read out of the user's own booking document, after review.
    public const string DocumentFact = "document_fact";

    /// <summary>
    /// A preference another member explicitly SHARED with the group.
    ///
    /// Never a private one, and never attributed by name — the claim is that
    /// the group has a constraint, not whose it is.
    /// </summary>
    public const string SharedMemberPreference = "shared_member_preference";

    /// A group decision that actually reached "accepted". Pending is not a
    /// decision, and saying it is manufactures consensus.
    public const string ConfirmedGroupDecision = "confirmed_group_decision";

    /// A tally counted from vote rows.
    public const string PollResult = "poll_result";

    /// Two shared constraints that cannot both be satisfied.
    public const string GroupConflict = "group_conflict";

    /// Gluno's own trade-off. A judgement, never "the fair answer".
    public const string PlanningCompromise = "planning_compromise";

    /// <summary>
    /// Claims that MUST have a ledger entry behind them.
    ///
    /// The test is not "is it important" but "could the user tell if it were
    /// wrong". Nobody can check a rating or a travel time from their sofa, so
    /// those need evidence. A suggestion is self-evidently an opinion.
    /// </summary>
    public static readonly IReadOnlySet<string> RequireEvidence = new HashSet<string>(StringComparer.Ordinal)
    {
        TripFact, ProviderFact, CurrentWeather, Forecast,
        VerifiedRouteTime, StraightLineDistance, VerifiedOpeningHours,
        UserPreference, AppCapability, LiveTravelFact, DocumentFact,
        SharedMemberPreference, ConfirmedGroupDecision, PollResult, GroupConflict,
    };

    /// Claims that are Gluno's own voice. Evidence-free by nature, but they
    /// must never be dressed up as provider facts.
    public static readonly IReadOnlySet<string> AreOpinions = new HashSet<string>(StringComparer.Ordinal)
    {
        PlanningAssessment, Assumption, Suggestion, PlanningCompromise,
    };
}

/// <summary>
/// One thing Gluno is allowed to say, and what entitles it to say so.
///
/// The three booleans near the bottom are not decoration. <c>IsVerified</c>
/// means a source outside Gluno's own reasoning confirmed it.
/// <c>IsUserProvided</c> means the user is the authority and we should not
/// argue. <c>IsPlanningInference</c> means Gluno made it up — legitimately, as
/// judgement, but it may never be quoted as fact.
/// </summary>
public sealed record GlunoEvidence
{
    /// Short, stable within the turn: "E1", "E2". This is what the model cites
    /// and what the validator resolves back.
    public required string Id { get; init; }

    /// A short machine label for the KIND of thing: "place_rating",
    /// "route_leg", "day_forecast", "activity", "capability".
    public required string Type { get; init; }

    /// <see cref="GlunoEvidenceSources"/>.
    public required string Source { get; init; }

    /// <summary>
    /// How to find it again in its source: a Tripadvisor location id, a finding
    /// type, a preference key. NEVER a URL, never a query, never a raw payload.
    /// </summary>
    public string? SourceReference { get; init; }

    /// The claim type this entry primarily supports.
    public required string ClaimCategory { get; init; }

    /// <summary>
    /// The value, as short text. "4.5", "09:00-18:00", "18", "Nice".
    ///
    /// Kept as text rather than typed because the ledger carries ratings,
    /// times, distances and place names side by side, and a discriminated
    /// union of all of them would be read exactly once — here — before being
    /// turned back into a string for the model.
    /// </summary>
    public string? Value { get; init; }

    /// "min", "km", "°C", "of 5". Null when the value speaks for itself.
    public string? Unit { get; init; }

    public DateTime VerifiedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this stops being quotable as current. Null means it does not go
    /// stale — a straight-line distance between two fixed points is true
    /// forever.
    /// </summary>
    public DateTime? ValidUntilUtc { get; init; }

    /// 0–1. Below 1 for anything the source itself hedged.
    public double Confidence { get; init; } = 1;

    public Guid? TripId { get; init; }
    public Guid? ActivityId { get; init; }
    public Guid? DayLocationId { get; init; }

    /// Provider id when external — "tripadvisor". Drives attribution.
    public string? Provider { get; init; }
    /// Namespaced external id, when there is one.
    public string? ExternalId { get; init; }

    /// A source outside Gluno's own reasoning stands behind this.
    public bool IsVerified { get; init; }
    /// The user said it. They are the authority; do not contradict them.
    public bool IsUserProvided { get; init; }
    /// Gluno's own reasoning. Never quotable as fact.
    public bool IsPlanningInference { get; init; }

    /// <summary>
    /// Every claim type this entry may back. Usually one; a place detail can
    /// legitimately support both a provider fact and an address.
    /// </summary>
    public IReadOnlyList<string> AllowedClaimTypes { get; init; } = Array.Empty<string>();

    public bool IsFresh(DateTime nowUtc) => ValidUntilUtc == null || ValidUntilUtc > nowUtc;

    /// <summary>
    /// What identifies the same FACT, for deduplication.
    ///
    /// Deliberately excludes the value: two entries with this key and different
    /// values are a CONFLICT, not a duplicate, and collapsing them silently is
    /// exactly the failure this whole file exists to prevent.
    ///
    /// Deliberately excludes the SOURCE too, which is subtler and matters more.
    /// The most important conflict in this system is "our stored plan says one
    /// hotel, the user just said another" — and those two entries have
    /// different sources by definition. Keying on source would make exactly the
    /// disagreement we most need to notice invisible, because the two entries
    /// would never be compared. The same applies to two providers reporting
    /// different ratings for one place.
    /// </summary>
    public string SubjectKey() => string.Join(
        '|',
        Type,
        SourceReference ?? "-",
        ExternalId ?? "-",
        ActivityId?.ToString() ?? "-",
        DayLocationId?.ToString() ?? "-");
}

/// <summary>
/// Two entries that describe the same thing and disagree.
///
/// Kept as a first-class object rather than resolved silently. Picking a winner
/// without a trace is how a plan ends up built on the hotel the user changed
/// away from three messages ago.
/// </summary>
public sealed record GlunoEvidenceConflict(
    GlunoEvidence Left,
    GlunoEvidence Right,
    /// "provider_disagreement", "user_correction", "cache_vs_fresh",
    /// "location_mismatch".
    string Kind)
{
    /// The entry that should win, when precedence is unambiguous. Null when the
    /// conflict genuinely needs the user to settle it.
    public GlunoEvidence? Preferred { get; init; }
}

/// <summary>
/// Everything Gluno is allowed to use in ONE turn.
///
/// WHY A LEDGER AT ALL. Before this, the model was handed a context and some
/// tool results and trusted to only assert what they supported. That trust is
/// misplaced in a specific, well-documented way: a language model asked about a
/// restaurant will produce a rating whether or not it was given one, and the
/// number will look exactly like a real one. There is no prompt that fixes
/// this, because the failure is not disobedience — it is that fluent text
/// containing a plausible number is the model's default output.
///
/// So instead: every fact Gluno may state is enumerated here first, given an
/// id, and the answer is checked against the list afterwards. A number that is
/// not in the ledger does not reach the user.
///
/// SCOPE. One turn. It is built at the start, added to as tools run, read by
/// the validator, and thrown away. Only the minimal references needed to
/// re-check a proposal later are persisted.
/// </summary>
public sealed class GlunoEvidenceLedger
{
    private readonly List<GlunoEvidence> _entries = [];
    private readonly List<GlunoEvidenceConflict> _conflicts = [];
    private int _nextId = 1;

    /// <summary>
    /// A hard cap. The ledger is serialised into the prompt, and an unbounded
    /// one would push the actual conversation out of the context window — which
    /// would degrade the answer in the name of grounding it.
    /// </summary>
    public const int MaxEntries = 60;

    public IReadOnlyList<GlunoEvidence> Entries => _entries;
    public IReadOnlyList<GlunoEvidenceConflict> Conflicts => _conflicts;

    /// <summary>
    /// Adds an entry, or returns the existing one it duplicates.
    ///
    /// Deduplication is on subject AND value. Same subject, different value
    /// records a conflict and keeps BOTH — the caller decides, or the user is
    /// asked.
    /// </summary>
    public GlunoEvidence Add(GlunoEvidence entry)
    {
        var subject = entry.SubjectKey();

        foreach (var existing in _entries)
        {
            if (existing.SubjectKey() != subject) continue;

            if (string.Equals(existing.Value, entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                // Genuinely the same fact, learned twice. Keep the fresher
                // verification time so freshness reflects the latest check.
                return entry.VerifiedAtUtc > existing.VerifiedAtUtc
                    ? Replace(existing, entry with { Id = existing.Id })
                    : existing;
            }

            // Same subject, different value.
            var stored = Store(entry);
            _conflicts.Add(new GlunoEvidenceConflict(existing, stored, ClassifyConflict(existing, stored))
            {
                Preferred = Prefer(existing, stored),
            });
            return stored;
        }

        return Store(entry);
    }

    private GlunoEvidence Store(GlunoEvidence entry)
    {
        if (_entries.Count >= MaxEntries) return entry;

        var stored = entry with { Id = $"E{_nextId++}" };
        _entries.Add(stored);
        return stored;
    }

    private GlunoEvidence Replace(GlunoEvidence existing, GlunoEvidence replacement)
    {
        _entries[_entries.IndexOf(existing)] = replacement;
        return replacement;
    }

    public GlunoEvidence? Find(string id)
        => _entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every entry that could back a given claim type, still fresh.
    ///
    /// The freshness filter is the point: a rating from six months ago is in
    /// the ledger, and it is not evidence for "it is rated 4.5" today.
    /// </summary>
    public IReadOnlyList<GlunoEvidence> Supporting(string claimType, DateTime nowUtc)
        => _entries
            .Where(entry => entry.AllowedClaimTypes.Contains(claimType) || entry.ClaimCategory == claimType)
            .Where(entry => entry.IsFresh(nowUtc))
            .ToList();

    public bool HasAny(string claimType, DateTime nowUtc) => Supporting(claimType, nowUtc).Count > 0;

    /// Entries past their freshness window but still present. Quotable only
    /// with an explicit "as of" — never as "right now".
    public IReadOnlyList<GlunoEvidence> Stale(DateTime nowUtc)
        => _entries.Where(entry => !entry.IsFresh(nowUtc)).ToList();

    /// <summary>
    /// What goes into the prompt.
    ///
    /// Compact by design: id, kind, source, value, and freshness. NOT the
    /// provider payload, not coordinates, not internal ids. The model needs to
    /// know that E4 is "rating 4.5 of 5 from Tripadvisor, verified today" — it
    /// does not need the location id, the review text or the photo URLs, and
    /// every one of those is another thousand tokens and another surface for
    /// injected text.
    /// </summary>
    public IReadOnlyList<object> ForPrompt(DateTime nowUtc) => _entries
        .Select(entry => (object)new
        {
            id = entry.Id,
            what = entry.Type,
            source = entry.Source,
            claim = entry.ClaimCategory,
            value = entry.Unit == null ? entry.Value : $"{entry.Value} {entry.Unit}",
            verified = entry.IsVerified,
            // Told plainly rather than as a timestamp the model has to compare.
            freshness = entry.IsFresh(nowUtc) ? "current" : "outdated",
            provider = entry.Provider,
        })
        .ToList();

    /// <summary>
    /// Which conflict a disagreement is, so the caller can decide whether it
    /// needs the user or can be settled by precedence.
    /// </summary>
    private static string ClassifyConflict(GlunoEvidence left, GlunoEvidence right)
    {
        if (left.IsUserProvided || right.IsUserProvided) return "user_correction";
        if (left.Provider != null && right.Provider != null && left.Provider != right.Provider)
            return "provider_disagreement";
        if (left.Source == right.Source && left.VerifiedAtUtc != right.VerifiedAtUtc) return "cache_vs_fresh";

        return "value_mismatch";
    }

    /// <summary>
    /// Precedence, where it is genuinely unambiguous.
    ///
    /// 1. What the user just told us. They know where they are staying; we do
    ///    not get to overrule them with a stale row.
    /// 2. Fresher first-party data over older first-party data.
    /// 3. Nothing else. Two providers disagreeing about a rating is not
    ///    something precedence can settle, and pretending otherwise would be
    ///    picking a winner without a trace.
    /// </summary>
    private static GlunoEvidence? Prefer(GlunoEvidence left, GlunoEvidence right)
    {
        if (left.IsUserProvided != right.IsUserProvided) return left.IsUserProvided ? left : right;

        if (left.Source == right.Source && !GlunoEvidenceSources.IsExternal(left.Source))
            return left.VerifiedAtUtc >= right.VerifiedAtUtc ? left : right;

        return null;
    }

    // ── Builders ──────────────────────────────────────────────────────────
    //
    // One per source, so callers cannot assemble a half-populated entry and so
    // the freshness policy is applied in exactly one place per data type.

    public GlunoEvidence AddActivity(GlunoActivityContext activity, Guid tripId) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "activity",
        Source = GlunoEvidenceSources.SideQuestDatabase,
        SourceReference = activity.Id.ToString(),
        ClaimCategory = GlunoClaimTypes.TripFact,
        Value = activity.Title,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
        TripId = tripId,
        ActivityId = activity.Id,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.TripFact],
    });

    public GlunoEvidence AddPlaceRating(GlunoPlaceCard place) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "place_rating",
        Source = GlunoEvidenceSources.Tripadvisor,
        SourceReference = place.ExternalId,
        ClaimCategory = GlunoClaimTypes.ProviderFact,
        Value = place.Rating?.ToString("0.#", CultureInfo.InvariantCulture),
        Unit = place.RatingScaleMax.HasValue
            ? $"of {place.RatingScaleMax.Value.ToString("0.#", CultureInfo.InvariantCulture)}"
            : null,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.PlaceRating),
        Provider = place.Provider,
        ExternalId = place.ExternalId,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.ProviderFact],
    });

    public GlunoEvidence AddReviewCount(GlunoPlaceCard place) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "place_review_count",
        Source = GlunoEvidenceSources.Tripadvisor,
        SourceReference = place.ExternalId,
        ClaimCategory = GlunoClaimTypes.ProviderFact,
        Value = place.ReviewCount?.ToString(CultureInfo.InvariantCulture),
        Unit = "reviews",
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.ReviewCount),
        Provider = place.Provider,
        ExternalId = place.ExternalId,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.ProviderFact],
    });

    public GlunoEvidence AddPriceLevel(GlunoPlaceCard place) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "place_price_level",
        Source = GlunoEvidenceSources.Tripadvisor,
        SourceReference = place.ExternalId,
        ClaimCategory = GlunoClaimTypes.ProviderFact,
        Value = place.PriceLevel,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.PriceLevel),
        Provider = place.Provider,
        ExternalId = place.ExternalId,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.ProviderFact],
    });

    public GlunoEvidence AddRouteLeg(RouteLeg leg) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "route_leg",
        Source = leg.Verified ? GlunoEvidenceSources.RoutingProvider : GlunoEvidenceSources.SideQuestAnalysis,
        SourceReference = $"{leg.Origin.CacheKey()}>{leg.Destination.CacheKey()}",
        // The whole point: an unverified leg backs a DISTANCE claim and never a
        // time claim, whatever number happens to be attached to it.
        ClaimCategory = leg.Verified ? GlunoClaimTypes.VerifiedRouteTime : GlunoClaimTypes.StraightLineDistance,
        Value = leg.Verified
            ? leg.DurationMinutes?.ToString(CultureInfo.InvariantCulture)
            : leg.DistanceKm?.ToString("0.#", CultureInfo.InvariantCulture),
        Unit = leg.Verified ? "min" : "km",
        VerifiedAtUtc = leg.ComputedAt,
        ValidUntilUtc = leg.Verified
            ? GlunoFreshness.Until(GlunoFreshness.ForMode(leg.Mode), leg.ComputedAt)
            // A straight line between two fixed points does not expire.
            : null,
        Provider = leg.Verified ? leg.Source : null,
        IsVerified = leg.Verified,
        AllowedClaimTypes = leg.Verified
            ? [GlunoClaimTypes.VerifiedRouteTime, GlunoClaimTypes.StraightLineDistance]
            : [GlunoClaimTypes.StraightLineDistance],
    });

    public GlunoEvidence AddForecast(GlunoWeatherContext weather, Guid tripId) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "day_forecast",
        Source = GlunoEvidenceSources.WeatherProvider,
        // Date AND place. A forecast without both is not evidence for anything
        // — "it'll rain" is meaningless if it is the wrong town.
        SourceReference = $"{weather.Date:yyyy-MM-dd}|{weather.LocationLabel ?? "-"}",
        ClaimCategory = GlunoClaimTypes.Forecast,
        Value = weather.Condition,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.Forecast),
        TripId = tripId,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.Forecast],
    });

    public GlunoEvidence AddOpeningHours(string externalId, string description, DateTime fetchedAtUtc)
        => Add(new GlunoEvidence
        {
            Id = "pending",
            Type = "opening_hours",
            Source = GlunoEvidenceSources.OpeningHoursProvider,
            SourceReference = externalId,
            ClaimCategory = GlunoClaimTypes.VerifiedOpeningHours,
            Value = description,
            VerifiedAtUtc = fetchedAtUtc,
            ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.OpeningHours, fetchedAtUtc),
            Provider = TripadvisorTravelProvider.ProviderId,
            ExternalId = externalId,
            IsVerified = true,
            AllowedClaimTypes = [GlunoClaimTypes.VerifiedOpeningHours],
        });

    public GlunoEvidence AddPreference(string key, string value) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "preference",
        Source = GlunoEvidenceSources.StoredPreference,
        SourceReference = key,
        ClaimCategory = GlunoClaimTypes.UserPreference,
        Value = value,
        VerifiedAtUtc = DateTime.UtcNow,
        IsVerified = true,
        IsUserProvided = true,
        AllowedClaimTypes = [GlunoClaimTypes.UserPreference],
    });

    public GlunoEvidence AddFinding(TripFinding finding, Guid tripId) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "finding",
        Source = GlunoEvidenceSources.SideQuestAnalysis,
        SourceReference = finding.Type,
        // SideQuest's reading of the plan, not a fact about the world. Marked
        // as an assessment so it can never be quoted as external truth.
        ClaimCategory = GlunoClaimTypes.PlanningAssessment,
        Value = finding.Type,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
        TripId = tripId,
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.PlanningAssessment, GlunoClaimTypes.TripFact],
    });

    public GlunoEvidence AddCapability(SideQuestCapability capability) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "capability",
        Source = GlunoEvidenceSources.CapabilityRegistry,
        SourceReference = capability.Id,
        ClaimCategory = GlunoClaimTypes.AppCapability,
        Value = capability.NameEn,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.CapabilityRegistry),
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.AppCapability],
    });

    /// <summary>
    /// A live fact — a strike, a closure, an event.
    ///
    /// Two things make this entry different from every other external one.
    ///
    /// Its lifetime comes from the fact's OWN effective dates, not from a fixed
    /// window: a closure that ends on Friday stops being evidence on Friday,
    /// however recently we fetched it.
    ///
    /// And <see cref="GlunoEvidence.IsVerified"/> tracks the SOURCE TIER, not
    /// merely whether a provider answered. A news report of a cancellation is
    /// real information and is not the operator confirming its own timetable —
    /// so only first-party sources mark it verified, and the prompt is
    /// permitted a stronger sentence for those.
    /// </summary>
    public GlunoEvidence AddLiveTravelFact(LiveTravelFact fact) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "live_" + fact.Category,
        Source = GlunoEvidenceSources.LiveTravelInformation,
        // The source's NAME, never its URL — a ledger entry is not a link.
        SourceReference = fact.SourceName,
        ClaimCategory = GlunoClaimTypes.LiveTravelFact,
        Value = fact.Title,
        VerifiedAtUtc = fact.VerifiedAt,
        // Expiry follows the event, not the fetch. An expired or undated fact
        // is already past its window the moment it enters the ledger, which is
        // what stops it being quoted as current.
        ValidUntilUtc = fact.Recency switch
        {
            LiveRecency.Expired or LiveRecency.Unclear => DateTime.UtcNow.AddSeconds(-1),
            _ => fact.EffectiveUntil?.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)
                 ?? GlunoFreshness.Until(GlunoFreshness.LiveTravelFact, fact.VerifiedAt),
        },
        Confidence = fact.Confidence,
        Provider = LiveSourceTiers.ToWireValue(fact.SourceTier),
        IsVerified = fact.IsOfficial,
        AllowedClaimTypes = [GlunoClaimTypes.LiveTravelFact],
    });

    /// <summary>
    /// Something read out of the user's own booking document.
    ///
    /// Only ever added for a REVIEWED analysis — an extraction a human has not
    /// looked at is a machine's reading of a photograph, and it does not become
    /// a fact about somebody's trip until its owner agrees.
    /// </summary>
    public GlunoEvidence AddDocumentFact(string type, string reference, string value, DateTime reviewedAtUtc)
        => Add(new GlunoEvidence
        {
            Id = "pending",
            Type = "document_" + type,
            Source = GlunoEvidenceSources.DocumentAnalysis,
            SourceReference = reference,
            ClaimCategory = GlunoClaimTypes.DocumentFact,
            Value = value,
            VerifiedAtUtc = reviewedAtUtc,
            ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
            IsVerified = true,
            IsUserProvided = true,
            AllowedClaimTypes = [GlunoClaimTypes.DocumentFact, GlunoClaimTypes.TripFact],
        });

    /// <summary>
    /// A constraint a member SHARED with the group.
    ///
    /// The member ref is deliberately neutral and the value deliberately
    /// present — the planner needs to honour "short walking distances", and
    /// nobody needs to know whose it is. Nothing private ever reaches this
    /// method: only trip_shared preferences are loaded in the first place.
    /// </summary>
    public GlunoEvidence AddSharedConstraint(GroupConstraint constraint) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "shared_" + constraint.Key,
        Source = GlunoEvidenceSources.StoredPreference,
        // The neutral ref, never a user id and never a name.
        SourceReference = constraint.MemberRef,
        ClaimCategory = GlunoClaimTypes.SharedMemberPreference,
        Value = constraint.Value,
        VerifiedAtUtc = constraint.ConfirmedAt ?? DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
        Confidence = constraint.Confidence,
        IsVerified = true,
        IsUserProvided = true,
        AllowedClaimTypes = [GlunoClaimTypes.SharedMemberPreference, GlunoClaimTypes.UserPreference],
    });

    /// <summary>
    /// A group decision, and ONLY once it actually settled.
    ///
    /// A pending decision enters the ledger already expired, so it cannot back
    /// a "the group has decided" claim. That is the mechanism that stops Gluno
    /// announcing a consensus while people are still voting.
    /// </summary>
    public GlunoEvidence AddGroupDecision(GroupDecisionSummary decision) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "group_decision_" + decision.Kind,
        Source = GlunoEvidenceSources.SideQuestDatabase,
        SourceReference = decision.Id.ToString(),
        ClaimCategory = GlunoClaimTypes.ConfirmedGroupDecision,
        Value = decision.AcceptedOptionLabel,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = decision.IsSettled
            ? GlunoFreshness.Until(GlunoFreshness.AdventureData)
            // Pending, rejected or superseded: already out of date on arrival.
            : DateTime.UtcNow.AddSeconds(-1),
        IsVerified = decision.IsSettled,
        AllowedClaimTypes = [GlunoClaimTypes.ConfirmedGroupDecision],
    });

    /// <summary>
    /// A poll tally, counted from vote rows.
    ///
    /// Counts only. Which member chose which option never enters the ledger,
    /// because it never needs to and because a model with that data will
    /// eventually mention it.
    /// </summary>
    public GlunoEvidence AddPollResult(Guid decisionId, GlunoPollResult result) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "poll_result",
        Source = GlunoEvidenceSources.SideQuestDatabase,
        SourceReference = decisionId.ToString(),
        ClaimCategory = GlunoClaimTypes.PollResult,
        Value = $"{result.Responded}/{result.GroupSize} responded",
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
        // A tie is a real outcome and is NOT a decision.
        IsVerified = !result.IsTie && result.WinningOptionId != null,
        AllowedClaimTypes = [GlunoClaimTypes.PollResult],
    });

    /// <summary>
    /// A clash between shared constraints. The TYPE, never whose they are.
    /// </summary>
    public GlunoEvidence AddGroupConflict(GroupConflict conflict) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = "group_conflict",
        Source = GlunoEvidenceSources.SideQuestAnalysis,
        SourceReference = conflict.Type,
        ClaimCategory = GlunoClaimTypes.GroupConflict,
        Value = conflict.Type,
        VerifiedAtUtc = DateTime.UtcNow,
        ValidUntilUtc = GlunoFreshness.Until(GlunoFreshness.AdventureData),
        IsVerified = true,
        AllowedClaimTypes = [GlunoClaimTypes.GroupConflict, GlunoClaimTypes.PlanningAssessment],
    });

    /// <summary>
    /// Something the user just told us that contradicts what we have stored.
    ///
    /// Recorded as evidence rather than acted on, because the user saying "we
    /// changed hotels" does not change their Adventure — only an applied
    /// proposal does that. But it absolutely changes what Gluno should say for
    /// the rest of the conversation.
    /// </summary>
    public GlunoEvidence AddUserStatement(string type, string reference, string value) => Add(new GlunoEvidence
    {
        Id = "pending",
        Type = type,
        Source = GlunoEvidenceSources.UserMessage,
        SourceReference = reference,
        ClaimCategory = GlunoClaimTypes.TripFact,
        Value = value,
        VerifiedAtUtc = DateTime.UtcNow,
        IsVerified = false,
        IsUserProvided = true,
        AllowedClaimTypes = [GlunoClaimTypes.TripFact, GlunoClaimTypes.UserPreference],
    });
}
