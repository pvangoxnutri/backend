namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The SideQuest context Gluno is allowed to reason about, as a structured
/// object rather than a prose dump.
///
/// Two rules govern everything in this file.
///
/// 1. It is a WHITELIST, not a projection. Nothing here is "the entity minus a
///    few fields" — every property was added deliberately. That is why there is
///    no e-mail address, no push token, no auth metadata, no storage path and
///    no invite code anywhere below: those were never added, so no future
///    refactor of the entities can quietly introduce them.
///
/// 2. It is bounded. Every collection has a hard cap (see
///    <see cref="GlunoContextLimits"/>) and <see cref="GlunoContext.Truncated"/>
///    says so honestly when a cap was hit, so the model can tell "there is
///    nothing else" apart from "I was only shown part of it".
/// </summary>
public static class GlunoContextLimits
{
    /// Activities included for the selected Adventure. Enough for a full
    /// itinerary of any realistic trip without the context growing with the
    /// trip's age.
    public const int MaxActivities = 120;

    /// Day locations included for the selected Adventure.
    public const int MaxDayLocations = 60;

    /// Members listed for the selected Adventure.
    public const int MaxMembers = 25;

    /// Other Adventures summarised alongside the selected one (or, with no
    /// Adventure selected, the whole of what Gluno sees).
    public const int MaxTrips = 20;

    /// Recent conversation turns replayed to the model. Older turns are
    /// dropped from the request, never from the database.
    public const int MaxHistoryTurns = 30;

    /// Previously applied Gluno changes listed for the Adventure.
    public const int MaxAppliedChanges = 15;

    /// How far back to look for places already shown in this conversation,
    /// and how many to carry forward.
    public const int MaxDiscussedPlaceTurns = 8;
    public const int MaxDiscussedPlaces = 12;

    /// Distinct locations weather is fetched for. Each is one provider call
    /// (cached), so a trip that hops through ten towns does not turn one chat
    /// turn into ten upstream requests.
    public const int MaxWeatherLocations = 4;
}

public sealed class GlunoUserContext
{
    /// Display name only. Deliberately no e-mail, no id, no auth metadata —
    /// the model never needs to identify the user, only to address them.
    public string Name { get; init; } = string.Empty;
    /// "en" | "sv" — so Gluno answers in the language the app is set to.
    public string Language { get; init; } = "en";
}

public sealed class GlunoMemberContext
{
    public string Name { get; init; } = string.Empty;
    public bool IsOwner { get; init; }
    /// True for the person Gluno is talking to right now.
    public bool IsYou { get; init; }
}

public sealed class GlunoActivityContext
{
    public Guid Id { get; init; }
    public DateOnly Date { get; init; }
    public string Title { get; init; } = string.Empty;
    /// The user's own text, with the app's location markers stripped out —
    /// Gluno must never see or repeat the raw marker syntax.
    public string? Description { get; init; }
    public string? Time { get; init; }
    /// Set only on a multi-day stay: Date/Time is check-in, EndDate/EndTime
    /// check-out. There is no duration field on an ordinary activity, so its
    /// length is genuinely unknown and must never be guessed.
    public DateOnly? EndDate { get; init; }
    public string? EndTime { get; init; }
    public int SortIndex { get; init; }
    public string? Category { get; init; }
    public string? CustomCategoryLabel { get; init; }

    /// Where this activity is, read from the description markers the app
    /// writes (see ActivityLocationMarkers). Null when the user never picked a
    /// place — which is common, and is why nothing may assume coordinates.
    public string? LocationLabel { get; init; }
    public string? PlaceId { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// Coarse role, derived from the category so the analyzer and the model
    /// agree on what counts as a hotel, a meal or a transfer.
    /// "stay" | "meal" | "transport" | "activity"
    public string Role { get; init; } = "activity";

    /// True when this is the requesting user's own not-yet-revealed hidden
    /// SideQuest. Someone else's unrevealed surprise is never in the context
    /// at all — see GlunoContextBuilder.
    public bool IsOwnHiddenSurprise { get; init; }
}

/// <summary>
/// One day's forecast, from SideQuest's own weather data (Open-Meteo via
/// WeatherService). Present only for dates the provider actually covers —
/// never extrapolated, and never invented for a date beyond the horizon.
/// </summary>
public sealed class GlunoWeatherContext
{
    public DateOnly Date { get; init; }
    /// SideQuest's own condition vocabulary: clear, partly_cloudy, cloudy,
    /// fog, rain, heavy_rain, snow, thunderstorm.
    public string? Condition { get; init; }
    public double? TempMinC { get; init; }
    public double? TempMaxC { get; init; }
    public int? PrecipitationProbability { get; init; }
    /// Which place this forecast is for — a multi-city day has one entry per
    /// location, and saying "rain" without saying where would be useless.
    public string? LocationLabel { get; init; }
}

/// <summary>
/// What the group has already spent, per currency. Summary only: no payers,
/// no participants, no per-expense detail — Gluno plans trips, it does not
/// audit anyone's spending.
/// </summary>
public sealed class GlunoBudgetContext
{
    public string Currency { get; init; } = string.Empty;
    public decimal TotalSpent { get; init; }
    public int ExpenseCount { get; init; }
}

/// <summary>
/// A change Gluno previously suggested that the user actually applied. Lets it
/// remember what it has already done for this Adventure instead of proposing
/// the same thing twice.
/// </summary>
public sealed class GlunoAppliedChangeContext
{
    public string Kind { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTime AppliedAt { get; init; }
}

/// <summary>
/// An external place already surfaced in this conversation. Carried forward so
/// the user can say "book the second one" without a second provider call — and
/// so Gluno keeps the attribution attached.
/// </summary>
public sealed class GlunoDiscussedPlaceContext
{
    public string Provider { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Category { get; init; }
    public string? Address { get; init; }
    public double? Rating { get; init; }
    public int? ReviewCount { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string SourceAttribution { get; init; } = string.Empty;
}

/// <summary>
/// A planning preference the user stated. See GlunoPreferences for the keys
/// and the scoping rules.
/// </summary>
public sealed class GlunoPreferenceContext
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    /// "conversation" | "trip" | "global"
    public string Scope { get; init; } = "conversation";
}

public sealed class GlunoDayLocationContext
{
    public DateOnly Date { get; init; }
    public int SortIndex { get; init; }
    public string Label { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
}

/// <summary>
/// A trip Gluno knows about but is not currently focused on. Summary only —
/// no activities, no members, no coordinates.
/// </summary>
public sealed class GlunoTripSummary
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    /// Null means the traveller has not decided when it ends. Gluno must say
    /// "open-ended", never invent a date — see TripDateRange.
    public DateOnly? EndDate { get; init; }
    public string Status { get; init; } = "active";
    public bool IsOwner { get; init; }
}

/// <summary>
/// The selected Adventure in full (bounded) detail.
/// </summary>
// A record, not a class, for one reason: the analysis needs the finished
// context to run over, so the findings are attached with `with` after it is
// built rather than being threaded through construction.
public sealed record GlunoTripContext
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Destination { get; init; } = string.Empty;
    public double? DestinationLatitude { get; init; }
    public double? DestinationLongitude { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    /// The finite last day derived from an open-ended trip, so the model has
    /// something concrete to plan against without EndDate being faked.
    public DateOnly EffectiveEndDate { get; init; }
    public bool IsOpenEnded { get; init; }
    public string Status { get; init; } = "active";
    public bool IsOwner { get; init; }
    /// Whether non-owners are allowed to change this trip's plan at all. Drives
    /// what Gluno may propose to this particular user.
    public bool MembersCanEdit { get; init; }
    public bool CanEdit { get; init; }

    public IReadOnlyList<GlunoMemberContext> Members { get; init; } = Array.Empty<GlunoMemberContext>();
    /// Count rather than making the model count a truncated list.
    public int MemberCount { get; init; }
    public IReadOnlyList<GlunoActivityContext> Activities { get; init; } = Array.Empty<GlunoActivityContext>();
    public IReadOnlyList<GlunoDayLocationContext> DayLocations { get; init; } = Array.Empty<GlunoDayLocationContext>();
    public IReadOnlyList<GlunoWeatherContext> Weather { get; init; } = Array.Empty<GlunoWeatherContext>();
    public IReadOnlyList<GlunoBudgetContext> Budget { get; init; } = Array.Empty<GlunoBudgetContext>();
    public IReadOnlyList<GlunoAppliedChangeContext> AppliedChanges { get; init; } = Array.Empty<GlunoAppliedChangeContext>();

    /// <summary>
    /// SideQuest's own read of this plan — empty days, clashes, geography that
    /// does not work. Computed deterministically before the model is called
    /// (see <see cref="TripAnalyzer"/>) so Gluno reasons about findings rather
    /// than having to rediscover them from raw rows every turn.
    /// </summary>
    public IReadOnlyList<TripFinding> Findings { get; init; } = Array.Empty<TripFinding>();
}

/// <summary>
/// Everything Gluno is given about SideQuest for one turn.
///
/// <see cref="Trip"/> is null when the conversation is global. That is a
/// first-class state, not a degraded one: Gluno still knows who it is talking
/// to, what today is, and which Adventures exist in summary — enough to answer
/// travel questions and to ask which trip the user means.
/// </summary>
/// <summary>
/// The shared half of a group Adventure, as the model may see it.
///
/// A deliberately thin projection of <see cref="TripPlanningProfile"/>. What is
/// NOT here is the point: no names, no user ids, no private preferences, and no
/// indication of which member said what beyond the neutral refs the profile
/// already assigns. Somebody reading Gluno's answer in a five-person Adventure
/// must not be able to work out who asked for the cheap hotel.
/// </summary>
public sealed record GlunoGroupContext
{
    public required int GroupSize { get; init; }

    /// How many members have shared anything at all. Low means Gluno should
    /// say it is planning on partial information rather than implying consensus.
    public required int ContributingMembers { get; init; }

    /// Shared constraints, hard ones first. Never a private preference.
    public IReadOnlyList<GroupConstraint> Constraints { get; init; } = Array.Empty<GroupConstraint>();

    /// What the group actually settled — never a pending poll.
    public IReadOnlyList<GroupDecisionSummary> Decisions { get; init; } = Array.Empty<GroupDecisionSummary>();

    /// Shared wishes that cannot all be satisfied, with concrete ways forward.
    public IReadOnlyList<GroupConflict> Conflicts { get; init; } = Array.Empty<GroupConflict>();
}

public sealed record GlunoContext
{
    public DateOnly Today { get; init; }
    /// <summary>
    /// The stable screen id the user opened Gluno from, or null when the
    /// client did not say. Purely to make help shorter — Gluno must never act
    /// on it, and must never tell someone how to reach the screen they are
    /// already standing on.
    /// </summary>
    public string? CurrentScreen { get; init; }
    /// Which version of the capability registry this answer was built against.
    public int AppCapabilityVersion { get; init; } = SideQuestCapabilities.Version;
    public GlunoUserContext User { get; init; } = new();
    public GlunoTripContext? Trip { get; init; }
    public IReadOnlyList<GlunoTripSummary> Trips { get; init; } = Array.Empty<GlunoTripSummary>();
    /// What the user has told Gluno about how they want to travel. Present so
    /// it never asks a question it already has the answer to.
    public IReadOnlyList<GlunoPreferenceContext> Preferences { get; init; } = Array.Empty<GlunoPreferenceContext>();
    /// External places already shown in THIS conversation.
    public IReadOnlyList<GlunoDiscussedPlaceContext> DiscussedPlaces { get; init; } = Array.Empty<GlunoDiscussedPlaceContext>();
    /// <summary>
    /// What the GROUP has shared, on a multi-member Adventure. Null on a solo
    /// trip, and null when nobody has shared anything — group machinery on a
    /// trip of one is noise.
    ///
    /// Only <c>trip_shared</c> preferences reach this, filtered in the query
    /// rather than afterwards. A private preference belongs to the person who
    /// set it even when Gluno is planning for five people.
    /// </summary>
    public GlunoGroupContext? Group { get; init; }
    /// True when any cap in <see cref="GlunoContextLimits"/> was hit. Surfaced
    /// to the model so it can say it may not be seeing everything rather than
    /// asserting completeness it cannot back up.
    public bool Truncated { get; init; }
}
