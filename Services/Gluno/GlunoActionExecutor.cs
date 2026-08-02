using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Who and what an action is allowed to touch. Built from the authenticated
/// request and the conversation row — never from anything the model sent.
/// </summary>
public sealed class GlunoActionScope
{
    public required Guid UserId { get; init; }
    /// The conversation's Adventure, or null for a global conversation.
    public Guid? TripId { get; init; }
    /// The conversation these actions belong to. Scopes remembered
    /// preferences and previously-discussed places.
    public Guid ConversationId { get; init; }
    /// The screen the user opened Gluno from, as a stable id (see
    /// SideQuestScreens). Null when the client did not say — help then falls
    /// back to generic instructions rather than guessing.
    public string? CurrentScreen { get; init; }
    /// The user's app language, passed to external providers so their
    /// localised text comes back in the language Gluno answers in.
    public string Language { get; init; } = "en";
}

public sealed class GlunoActionInvocation
{
    public required string ToolCallId { get; init; }
    public required string Name { get; init; }
    public required JsonElement Input { get; init; }
}

/// <summary>
/// A validated, previewable change. Handed to the mobile app so the user can
/// accept it through the ordinary trip endpoints — this object is never itself
/// applied to the database.
///
/// <see cref="Payload"/> shape by <see cref="Kind"/>:
///   activity       { date, title, description?, time?, endDate?, endTime?, category? }
///   day_plan       { date, pace, transportMode, routingVerified, feasible, utilisation,
///                    warnings[], dropped[], saveTravelAsActivities,
///                    activities: [{ title, time, endTime, durationMinutes, durationSource,
///                                   isFixed, existingActivityId?, latitude?, longitude?,
///                                   travelFromPrevious?, openingHours?, warnings[] }] }
///   day_location   { date, label, latitude?, longitude? }
///   activity_move  { activityId, title, fromDate, fromTime?, toDate, toTime? }
///   trip_dates     { startDate, endDate|null, clearEndDate }
/// </summary>
public sealed class GlunoProposal
{
    /// The allow-listed action that produced this, e.g. "propose_activity".
    /// Stored on the proposal row and used to dispatch the apply — a proposal
    /// whose action name is not on the list is refused rather than guessed at.
    public required string ActionName { get; init; }
    /// The card shape the app renders. Narrower than the action name on
    /// purpose: several actions could produce the same visual kind.
    public required string Kind { get; init; }
    public required Guid TripId { get; init; }
    /// One-line human summary, used as the proposal card's heading.
    public required string Summary { get; init; }
    public required JsonElement Payload { get; init; }

    /// <summary>
    /// What goes on the row instead, when the real summary may not be stored.
    ///
    /// Null on every ordinary proposal, and then the summary above is used for
    /// both. Set only where the heading is a provider's name for a place and
    /// that provider does not licence its content for storage.
    /// </summary>
    public string? PersistedSummary { get; init; }

    /// <summary>
    /// What goes on the row instead of <see cref="Payload"/>.
    ///
    /// Same rule, same reason. A proposal waits for review and is applied
    /// later, possibly from another device — so under those terms the waiting
    /// copy carries the place's IDENTITY and the user's own decisions, and the
    /// content is fetched again at Apply.
    ///
    /// Both are chosen where the proposal is built, never derived from each
    /// other by removing fields afterwards.
    /// </summary>
    public JsonElement? PersistedPayload { get; init; }
}

/// <summary>
/// One external place, as the chat renders it.
///
/// This is the app-facing shape, not the provider's: the raw payload never
/// leaves the backend, and every field here is either something the provider
/// actually returned or null. <see cref="Signals"/> carries SideQuest's own
/// ranking reasons so a card can say why it is being suggested without
/// implying the provider ranked it.
/// </summary>
public sealed class GlunoPlaceCard
{
    public required string Provider { get; init; }
    /// Namespaced ("tripadvisor:12345") — the handle a future propose_activity
    /// stores so a suggested place can be traced back to its source.
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? CategoryLabel { get; init; }
    public string? Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Rating { get; init; }
    public double? RatingScaleMax { get; init; }
    public int? ReviewCount { get; init; }
    public string? PriceLevel { get; init; }
    public string? ImageUrl { get; init; }
    public string? ProviderUrl { get; init; }
    /// Attribution stays on the individual result, never on the batch.
    public required string SourceAttribution { get; init; }
    public double? DistanceKm { get; init; }
    public IReadOnlyList<string> OpeningHours { get; init; } = Array.Empty<string>();
    public string? ReviewSummary { get; init; }
    /// SideQuest's ranking signals ("highly_rated", "walkable", …).
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The provider's own bare id, without the namespace.
    ///
    /// Never serialised, and never sent to the model or the app on its own —
    /// they get <see cref="ExternalId"/>. This is here so the minimal reference
    /// that replaces the card in storage can be built without re-parsing.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProviderPlaceId { get; init; }

    /// <summary>
    /// Whether this card's CONTENT may be written into the stored payload.
    ///
    /// Stamped by the provider that produced it. Terra's terms permit storing
    /// only the location id, so its cards travel to the app for the turn that
    /// fetched them and are not kept.
    ///
    /// Never serialised — see GlunoAssistantPayload. The flag exists to decide
    /// what gets written, so writing it would be pointless.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowsContentPersistence { get; init; }

    /// <summary>
    /// Whether this card's IDENTITY may be kept when its content may not.
    ///
    /// The two together decide which of three things is stored for this card:
    /// the whole card, a bare reference, or nothing at all.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowsIdentityPersistence { get; init; }
}

/// <summary>
/// One line in the chat's "Sources" row.
///
/// Deliberately NOT the evidence ledger. The ledger is dozens of entries with
/// ids, claim categories and freshness windows — internal machinery. This is
/// the two or three things a person would want to tap: where the travel time
/// came from, which day's forecast this rests on, that a detail is from their
/// own plan.
///
/// Nothing here exposes an evidence id, a database id, a tool name or a prompt.
/// </summary>
public sealed class GlunoSourceCard
{
    /// "route" | "weather" | "plan" | "provider" | "hours". Drives the icon.
    public required string Kind { get; init; }

    /// Already localised: "Ruttdata", "Route data", "From your plan".
    public required string Label { get; init; }

    /// What it supports, in a few words: "Travel times between stops".
    public required string Supports { get; init; }

    /// When it was verified. Null when the question does not arise.
    public DateTime? VerifiedAtUtc { get; init; }

    /// True when past its freshness window — the app labels it rather than
    /// hiding it.
    public bool IsStale { get; init; }

    /// Provider brand for attribution, when there is one. Never a key, never
    /// a URL.
    public string? Provider { get; init; }
}

public sealed class GlunoActionOutcome
{
    public required bool Ok { get; init; }
    /// Machine-readable failure code, e.g. "date_out_of_range". Null on success.
    public string? ErrorCode { get; init; }
    /// Exactly what goes back to the model as the tool result. Always JSON.
    public required string ResultJson { get; init; }
    /// The proposal to render in the app. Null for read-only actions and for
    /// every failure.
    public GlunoProposal? Proposal { get; init; }
    /// External places to render as cards in the chat. Empty for everything
    /// except a successful place search.
    public IReadOnlyList<GlunoPlaceCard> Places { get; init; } = Array.Empty<GlunoPlaceCard>();

    /// <summary>
    /// SideQuest's own request behind <see cref="Places"/>.
    ///
    /// Carried out of the executor because this is the only place that knows
    /// it: by the time the turn decides what to store, the query, the resolved
    /// geography and the category are gone. Needed so a place whose content
    /// cannot be kept can still be asked for again later.
    /// </summary>
    public GlunoPlaceSearchContext? PlaceSearch { get; init; }
    /// Verified screens the chat may offer to open. Never navigated
    /// automatically — the app renders a button and waits for a tap.
    public IReadOnlyList<GlunoNavigationCard> Navigations { get; init; } = Array.Empty<GlunoNavigationCard>();
}

public interface IGlunoActionExecutor
{
    Task<GlunoActionOutcome> ExecuteAsync(GlunoActionInvocation invocation, GlunoActionScope scope, CancellationToken ct);
}

/// <summary>
/// Validates and executes Gluno's actions — independently of the model.
///
/// The model's parameters are treated as untrusted input in the same way a
/// request body is. Every constraint is re-checked here even though the tool
/// schema states it: the schema is a hint to the model, this is the rule. A
/// malformed, out-of-range or cross-trip parameter produces a structured
/// failure the model is told about, never a partially applied change — and
/// never an exception that ends the turn.
///
/// Two things the model can never influence, by construction rather than by
/// validation: the acting user (always <see cref="GlunoActionScope.UserId"/>)
/// and the Adventure (always <see cref="GlunoActionScope.TripId"/>). Neither
/// appears in any action schema, so there is nowhere for a model to put one.
///
/// And nothing here writes. Every "propose_*" path ends in a
/// <see cref="GlunoProposal"/>; there is no SaveChanges in this file.
/// </summary>
public sealed class GlunoActionExecutor : IGlunoActionExecutor
{
    private const int MaxTitleLength = 120;
    private const int MaxDescriptionLength = 2000;
    private const int MaxLabelLength = 120;
    private const int MaxCategoryLength = 40;

    private static readonly Regex TimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IGlunoContextBuilder _contextBuilder;
    private readonly ITravelDataRegistry _travelData;
    private readonly GlunoUsageLimiter _usageLimiter;
    private readonly IGlunoPreferenceService _preferences;
    private readonly IDayPlanPlanner _dayPlanner;

    /// <summary>
    /// External searches already made this turn.
    ///
    /// The executor is scoped, and one HTTP request is one Gluno turn, so this
    /// counter IS the per-turn budget — a model that decides to search five
    /// times in one answer is stopped after the third and told why, instead of
    /// quietly running up the provider bill and the user's wait.
    /// </summary>
    private int _searchesUsed;

    public GlunoActionExecutor(
        AppDbContext db,
        IGlunoContextBuilder contextBuilder,
        ITravelDataRegistry travelData,
        GlunoUsageLimiter usageLimiter,
        IGlunoPreferenceService preferences,
        IDayPlanPlanner dayPlanner)
    {
        _db = db;
        _contextBuilder = contextBuilder;
        _travelData = travelData;
        _usageLimiter = usageLimiter;
        _preferences = preferences;
        _dayPlanner = dayPlanner;
    }

    public async Task<GlunoActionOutcome> ExecuteAsync(
        GlunoActionInvocation invocation, GlunoActionScope scope, CancellationToken ct)
    {
        var definition = GlunoActions.Find(invocation.Name);
        if (definition == null)
            return Failure("unknown_action", $"There is no action called \"{invocation.Name}\".");

        if (definition.RequiresTrip && scope.TripId == null)
            return Failure("no_adventure_selected",
                "This action needs an Adventure. This conversation is not scoped to one — ask the user to open Gluno from an Adventure.");

        TripGuard? guard = null;
        if (definition.RequiresTrip)
        {
            guard = await LoadTripGuardAsync(scope.UserId, scope.TripId!.Value, ct);
            if (guard == null)
                return Failure("not_a_member", "That Adventure is not available to this user.");

            if (definition.RequiresEditPermission && !guard.CanEdit)
                return Failure("read_only",
                    "This user can only view this Adventure — its owner has turned off member editing. Do not propose changes to it.");
        }

        try
        {
            return invocation.Name switch
            {
                GlunoActions.ProposeActivity => ProposeActivity(invocation.Input, guard!),
                GlunoActions.ProposeDayPlan => await ProposeDayPlanAsync(invocation.Input, guard!, scope, ct),
                GlunoActions.ProposeDayLocation => ProposeDayLocation(invocation.Input, guard!),
                GlunoActions.ProposeActivityMove => await ProposeActivityMoveAsync(invocation.Input, guard!, scope.UserId, ct),
                GlunoActions.ProposeTripDateChange => await ProposeTripDateChangeAsync(invocation.Input, guard!, ct),
                GlunoActions.SearchPlaces => await SearchPlacesAsync(invocation.Input, guard, scope, ct),
                GlunoActions.SearchSideQuestFeatures => SearchFeatures(invocation.Input, scope),
                GlunoActions.GetSideQuestFeature => GetFeature(invocation.Input, scope),
                GlunoActions.GetAvailableActions => await GetAvailableActionsAsync(scope, guard, ct),
                GlunoActions.GetCurrentScreenHelp => GetCurrentScreenHelp(scope),
                GlunoActions.NavigateInSideQuest => await NavigateAsync(invocation.Input, scope, ct),
                GlunoActions.RememberPreference => await RememberPreferenceAsync(invocation.Input, scope, ct),
                GlunoActions.ForgetPreference => await ForgetPreferenceAsync(invocation.Input, scope, ct),
                GlunoActions.GetTripOverview => await GetTripOverviewAsync(scope, ct),
                _ => Failure("unknown_action", $"There is no action called \"{invocation.Name}\"."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The model gets a generic failure; the exception itself is never
            // echoed back, since it can carry internal detail.
            return Failure("action_failed", "That action could not be completed. Try a different approach or ask the user.");
        }
    }

    // ── Trip guard ────────────────────────────────────────────────────────

    private sealed class TripGuard
    {
        public required Guid TripId { get; init; }
        public required string Title { get; init; }
        public required string Destination { get; init; }
        public double? DestinationLatitude { get; init; }
        public double? DestinationLongitude { get; init; }
        public required DateOnly StartDate { get; init; }
        public required DateOnly? EndDate { get; init; }
        public required bool CanEdit { get; init; }
    }

    private async Task<TripGuard?> LoadTripGuardAsync(Guid userId, Guid tripId, CancellationToken ct)
    {
        var row = await _db.TripMembers
            .AsNoTracking()
            .Where(tm => tm.TripId == tripId && tm.UserId == userId)
            .Join(_db.Trips.AsNoTracking(), tm => tm.TripId, t => t.Id, (tm, t) => new
            {
                t.Id,
                t.Title,
                t.Destination,
                t.DestinationLatitude,
                t.DestinationLongitude,
                t.StartDate,
                t.EndDate,
                t.MembersCanEdit,
                tm.IsOwner,
            })
            .FirstOrDefaultAsync(ct);

        if (row == null) return null;

        return new TripGuard
        {
            TripId = row.Id,
            Title = row.Title,
            Destination = row.Destination,
            DestinationLatitude = row.DestinationLatitude,
            DestinationLongitude = row.DestinationLongitude,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            CanEdit = row.IsOwner || row.MembersCanEdit,
        };
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private GlunoActionOutcome ProposeActivity(JsonElement input, TripGuard trip)
    {
        if (!TryReadDate(input, "date", out var date, out var dateError))
            return Failure("invalid_date", dateError!);

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        if (!TryReadText(input, "title", MaxTitleLength, required: true, out var title, out var titleError))
            return Failure("invalid_title", titleError!);

        if (!TryReadText(input, "description", MaxDescriptionLength, required: false, out var description, out var descError))
            return Failure("invalid_description", descError!);

        if (!TryReadTime(input, "time", out var time, out var timeError))
            return Failure("invalid_time", timeError!);

        if (!TryReadTime(input, "endTime", out var endTime, out var endTimeError))
            return Failure("invalid_time", endTimeError!);

        DateOnly? endDate = null;
        if (HasValue(input, "endDate"))
        {
            if (!TryReadDate(input, "endDate", out var parsedEnd, out var endDateError))
                return Failure("invalid_date", endDateError!);
            if (parsedEnd < date)
                return Failure("invalid_date", "endDate cannot be before date.");
            if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, parsedEnd))
                return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));
            endDate = parsedEnd;
        }

        if (!TryReadText(input, "category", MaxCategoryLength, required: false, out var category, out var categoryError))
            return Failure("invalid_category", categoryError!);

        var payload = JsonSerializer.SerializeToElement(new
        {
            date = Format(date),
            title,
            description,
            time,
            endDate = endDate.HasValue ? Format(endDate.Value) : null,
            endTime,
            category,
        });

        var proposal = new GlunoProposal
        {
            ActionName = GlunoActions.ProposeActivity,
            Kind = "activity",
            TripId = trip.TripId,
            Summary = $"Add \"{title}\" on {Format(date)}",
            Payload = payload,
        };

        return Success(proposal, new
        {
            status = "proposed",
            note = "A preview was shown to the user. Nothing was created — they have to accept it.",
            proposal = payload,
        });
    }

    /// <summary>
    /// A day plan. The model supplies WHAT and WHY; SideQuest supplies WHEN.
    ///
    /// Everything after validation is deterministic: existing bookings become
    /// anchors, the routing layer is asked for real travel times, opening hours
    /// are checked, and the schedule engine lays the day out or reports that it
    /// cannot. The model then gets the finished schedule back — including what
    /// did not fit and which travel times are estimates — so its answer is
    /// written against what SideQuest will actually save, not against times it
    /// guessed at.
    /// </summary>
    private async Task<GlunoActionOutcome> ProposeDayPlanAsync(
        JsonElement input, TripGuard trip, GlunoActionScope scope, CancellationToken ct)
    {
        if (!TryReadDate(input, "date", out var date, out var dateError))
            return Failure("invalid_date", dateError!);

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        if (!input.TryGetProperty("activities", out var activitiesEl) || activitiesEl.ValueKind != JsonValueKind.Array)
            return Failure("invalid_activities", "activities must be an array.");

        var count = activitiesEl.GetArrayLength();
        if (count == 0)
            return Failure("invalid_activities", "activities cannot be empty.");
        if (count > GlunoActions.MaxDayPlanActivities)
            return Failure("too_many_activities",
                $"A day plan may propose at most {GlunoActions.MaxDayPlanActivities} Activities. Split it or propose fewer.");

        if (!TryReadTime(input, "startTime", out var startTime, out var startError))
            return Failure("invalid_time", startError!);
        if (!TryReadTime(input, "endTime", out var endTime, out var endError))
            return Failure("invalid_time", endError!);

        var items = new List<DayPlanItem>(count);
        var index = 0;
        foreach (var item in activitiesEl.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
                return Failure("invalid_activities", $"Entry {index} of activities is not an object.");

            if (!TryReadText(item, "title", MaxTitleLength, required: true, out var title, out var titleError))
                return Failure("invalid_title", $"Entry {index}: {titleError}");
            if (!TryReadText(item, "description", MaxDescriptionLength, required: false, out var description, out var descError))
                return Failure("invalid_description", $"Entry {index}: {descError}");
            if (!TryReadTime(item, "time", out var time, out var timeError))
                return Failure("invalid_time", $"Entry {index}: {timeError}");
            if (!TryReadText(item, "category", MaxCategoryLength, required: false, out var category, out var categoryError))
                return Failure("invalid_category", $"Entry {index}: {categoryError}");
            if (!TryReadText(item, "locationLabel", MaxLabelLength, required: false, out var locationLabel, out var labelError))
                return Failure("invalid_location", $"Entry {index}: {labelError}");

            var latitude = ReadOptionalNumber(item, "latitude");
            var longitude = ReadOptionalNumber(item, "longitude");

            // Coordinates are validated here rather than trusted onward: a
            // latitude of 900 from the model must never reach a routing
            // provider, and a half-supplied pair is worse than none.
            if ((latitude.HasValue || longitude.HasValue)
                && !TripEditRules.IsValidCoordinate(latitude, longitude))
            {
                return Failure("invalid_coordinates",
                    $"Entry {index}: latitude and longitude must both be present and in range.");
            }

            var durationMinutes = ReadOptionalNumber(item, "durationMinutes");
            if (durationMinutes is < 5 or > 12 * 60)
            {
                return Failure("invalid_duration", $"Entry {index}: durationMinutes must be between 5 and 720.");
            }

            items.Add(new DayPlanItem(
                title!,
                description,
                ParseTimeOrNull(time),
                category,
                durationMinutes.HasValue ? (int)durationMinutes.Value : null,
                latitude,
                longitude,
                locationLabel,
                ReadOptionalText(item, "placeId", 120)));
        }

        var preferences = await _preferences.GetForContextAsync(scope.UserId, scope.ConversationId, trip.TripId, ct);
        var transport = TransportPreferences.From(
            ValueOf(preferences, Models.GlunoPreferenceKeys.Transport),
            ValueOf(preferences, Models.GlunoPreferenceKeys.WalkingDistance),
            ValueOf(preferences, Models.GlunoPreferenceKeys.Accessibility));

        var result = await _dayPlanner.PlanAsync(new DayPlanInput
        {
            TripId = trip.TripId,
            Date = date,
            Items = items,
            Pace = TripPaces.Parse(ValueOf(preferences, Models.GlunoPreferenceKeys.Pace)),
            Transport = transport,
            StartTime = ParseTimeOrNull(startTime) ?? ParseTimeOrNull(ValueOf(preferences, Models.GlunoPreferenceKeys.StartTime)),
            EndTime = ParseTimeOrNull(endTime),
            RequestedMode = HasValue(input, "transportMode")
                ? TravelModes.Parse(ReadOptionalText(input, "transportMode", 20))
                : null,
            Language = scope.Language,
        }, ct);

        var proposal = new GlunoProposal
        {
            ActionName = GlunoActions.ProposeDayPlan,
            Kind = "day_plan",
            TripId = trip.TripId,
            Summary = result.Summary,
            Payload = result.Payload,
        };

        return Success(proposal, new
        {
            status = "proposed",
            note =
                "A preview of the whole day was shown to the user. Nothing was created. "
                + "The schedule below is what SideQuest will save if they accept it — use ITS times, "
                + "not your own. Travel times are only real where verified is true.",
            // The model needs the warnings verbatim so its answer matches the
            // card: "these two didn't fit" has to be said out loud, not hidden
            // in a payload the user has to notice.
            feasible = result.Schedule.Feasible,
            verifiedTravelTimes = result.RoutingVerified,
            proposal = result.Payload,
        });
    }

    private static string? ValueOf(IReadOnlyList<Models.GlunoPreference> preferences, string key)
        => preferences.FirstOrDefault(preference => preference.Key == key)?.Value;

    private static TimeOnly? ParseTimeOrNull(string? value)
        => TimeOnly.TryParseExact(value, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static double? ReadOptionalNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static string? ReadOptionalText(JsonElement element, string name, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private GlunoActionOutcome ProposeDayLocation(JsonElement input, TripGuard trip)
    {
        if (!TryReadDate(input, "date", out var date, out var dateError))
            return Failure("invalid_date", dateError!);

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, date))
            return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        if (!TryReadText(input, "label", MaxLabelLength, required: true, out var label, out var labelError))
            return Failure("invalid_label", labelError!);

        double? latitude = null;
        double? longitude = null;
        if (HasValue(input, "latitude") || HasValue(input, "longitude"))
        {
            if (!TryReadNumber(input, "latitude", out var lat) || !TryReadNumber(input, "longitude", out var lon))
                return Failure("invalid_coordinates", "latitude and longitude must both be given, or both omitted.");
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
                return Failure("invalid_coordinates", "latitude must be between -90 and 90 and longitude between -180 and 180.");
            latitude = lat;
            longitude = lon;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            date = Format(date),
            label,
            latitude,
            longitude,
        });

        var proposal = new GlunoProposal
        {
            ActionName = GlunoActions.ProposeDayLocation,
            Kind = "day_location",
            TripId = trip.TripId,
            Summary = $"{label} on {Format(date)}",
            Payload = payload,
        };

        return Success(proposal, new
        {
            status = "proposed",
            note = "A preview was shown to the user. The day's location was not changed.",
            proposal = payload,
        });
    }

    private async Task<GlunoActionOutcome> ProposeActivityMoveAsync(
        JsonElement input, TripGuard trip, Guid userId, CancellationToken ct)
    {
        if (!TryReadText(input, "activityId", 64, required: true, out var rawId, out var idError))
            return Failure("invalid_activity", idError!);
        if (!Guid.TryParse(rawId, out var activityId))
            return Failure("invalid_activity", "activityId is not a valid id from this Adventure's plan.");

        if (!TryReadDate(input, "toDate", out var toDate, out var dateError))
            return Failure("invalid_date", dateError!);

        if (!TripDateRange.Contains(trip.StartDate, trip.EndDate, toDate))
            return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

        if (!TryReadTime(input, "toTime", out var toTime, out var timeError))
            return Failure("invalid_time", timeError!);

        // Scoped to THIS Adventure and to what this user may see — an id
        // belonging to another trip, or to somebody else's unrevealed
        // SideQuest, simply does not resolve.
        var activity = await _db.TripActivities
            .AsNoTracking()
            .Where(a => a.Id == activityId && a.TripId == trip.TripId)
            .Where(a => !a.IsHidden || a.OwnerId == userId)
            .Select(a => new { a.Id, a.Title, a.Date, a.Time })
            .FirstOrDefaultAsync(ct);

        if (activity == null)
            return Failure("activity_not_found", "That Activity is not part of this Adventure's plan.");

        var payload = JsonSerializer.SerializeToElement(new
        {
            activityId = activity.Id,
            title = activity.Title,
            fromDate = Format(activity.Date),
            fromTime = activity.Time,
            toDate = Format(toDate),
            toTime,
        });

        var proposal = new GlunoProposal
        {
            ActionName = GlunoActions.ProposeActivityMove,
            Kind = "activity_move",
            TripId = trip.TripId,
            Summary = $"Move \"{activity.Title}\" to {Format(toDate)}",
            Payload = payload,
        };

        return Success(proposal, new
        {
            status = "proposed",
            note = "A preview was shown to the user. The Activity has not moved.",
            proposal = payload,
        });
    }

    private async Task<GlunoActionOutcome> ProposeTripDateChangeAsync(
        JsonElement input, TripGuard trip, CancellationToken ct)
    {
        var clearEndDate = input.TryGetProperty("clearEndDate", out var clearEl)
            && clearEl.ValueKind == JsonValueKind.True;

        DateOnly startDate = trip.StartDate;
        if (HasValue(input, "startDate"))
        {
            if (!TryReadDate(input, "startDate", out startDate, out var startError))
                return Failure("invalid_date", startError!);
        }

        DateOnly? endDate = trip.EndDate;
        if (clearEndDate)
        {
            if (HasValue(input, "endDate"))
                return Failure("conflicting_dates", "Give either endDate or clearEndDate, not both.");
            endDate = null;
        }
        else if (HasValue(input, "endDate"))
        {
            if (!TryReadDate(input, "endDate", out var parsedEnd, out var endError))
                return Failure("invalid_date", endError!);
            endDate = parsedEnd;
        }

        if (endDate.HasValue && endDate.Value < startDate)
            return Failure("invalid_date", "The end date cannot be before the start date.");

        if (startDate == trip.StartDate && endDate == trip.EndDate)
            return Failure("no_change", "Those are already the Adventure's dates — nothing to propose.");

        // An independent check the model cannot see: a narrowed range that
        // would strand existing plans is rejected here rather than shown to
        // the user as an accept-and-break preview.
        var earliest = await _db.TripActivities
            .Where(a => a.TripId == trip.TripId)
            .Select(a => (DateOnly?)a.Date)
            .MinAsync(ct);
        var latestActivity = await _db.TripActivities
            .Where(a => a.TripId == trip.TripId)
            .Select(a => (DateOnly?)(a.EndDate ?? a.Date))
            .MaxAsync(ct);
        var earliestLocation = await _db.TripDayLocations
            .Where(d => d.TripId == trip.TripId)
            .Select(d => (DateOnly?)d.StartDate)
            .MinAsync(ct);
        var latestLocation = await _db.TripDayLocations
            .Where(d => d.TripId == trip.TripId)
            .Select(d => (DateOnly?)d.StartDate)
            .MaxAsync(ct);

        var earliestContent = Min(earliest, earliestLocation);
        var latestContent = Max(latestActivity, latestLocation);

        if (earliestContent.HasValue && earliestContent.Value < startDate)
            return Failure("would_strand_content",
                $"The Adventure already has plans on {Format(earliestContent.Value)}, before that start date. Propose a start date on or before it, or suggest moving those first.");

        if (endDate.HasValue && latestContent.HasValue && latestContent.Value > endDate.Value)
            return Failure("would_strand_content",
                $"The Adventure already has plans on {Format(latestContent.Value)}, after that end date. Propose a later end date, or suggest moving those first.");

        var payload = JsonSerializer.SerializeToElement(new
        {
            startDate = Format(startDate),
            endDate = endDate.HasValue ? Format(endDate.Value) : null,
            clearEndDate,
        });

        var summary = endDate.HasValue
            ? $"Change dates to {Format(startDate)} – {Format(endDate.Value)}"
            : $"Change dates to {Format(startDate)} – open-ended";

        var proposal = new GlunoProposal
        {
            ActionName = GlunoActions.ProposeTripDateChange,
            Kind = "trip_dates",
            TripId = trip.TripId,
            Summary = summary,
            Payload = payload,
        };

        return Success(proposal, new
        {
            status = "proposed",
            note = "A preview was shown to the user. The Adventure's dates are unchanged.",
            proposal = payload,
        });
    }

    /// <summary>
    /// The bridge between Gluno and the external provider layer.
    ///
    /// Everything the model sends is re-validated here — length, ranges,
    /// counts — and the per-turn search budget is enforced before any upstream
    /// call. What actually leaves SideQuest is only the search term, an area
    /// or a coordinate pair, a category and a language: the traveller's plan,
    /// companions and conversation stay on this side.
    ///
    /// A provider failure is a RESULT, not an exception. The model is told the
    /// lookup did not work and keeps the turn, because a timed-out restaurant
    /// search must never take the conversation down with it.
    /// </summary>
    private async Task<GlunoActionOutcome> SearchPlacesAsync(
        JsonElement input, TripGuard? trip, GlunoActionScope scope, CancellationToken ct)
    {
        if (!TryReadText(input, "query", GlunoActions.MaxSearchQueryLength, required: true, out var query, out var queryError))
            return Failure("invalid_query", queryError!);

        if (!_travelData.HasConfiguredProvider)
        {
            // Explicit, not an empty list: "no source" and "no results" mean
            // very different things and the model must not conflate them.
            return ReadResult(new
            {
                providerConfigured = false,
                results = Array.Empty<object>(),
                note = "SideQuest has no external travel-data provider configured. Tell the user you cannot look this up right now, then keep helping from their plan and your own travel knowledge — and say which of the two you are using.",
            });
        }

        if (_searchesUsed >= GlunoActions.MaxSearchesPerTurn)
        {
            return Failure("search_budget_exhausted",
                $"You have already made {GlunoActions.MaxSearchesPerTurn} place searches in this turn, which is the limit. Answer with what you already have, or ask the user to narrow it down.");
        }

        TryReadText(input, "near", 120, required: false, out var near, out _);
        TryReadText(input, "priceLevel", 12, required: false, out var priceLevel, out _);

        var category = TravelPlaceCategories.Parse(ReadOptionalString(input, "category"));

        var limit = GlunoActions.DefaultSearchLimit;
        if (TryReadNumber(input, "limit", out var rawLimit))
            limit = Math.Clamp((int)rawLimit, 1, GlunoActions.MaxSearchLimit);

        // Coordinates are all-or-nothing: half a pair is a bug, not a hint.
        double? latitude = null;
        double? longitude = null;
        if (HasValue(input, "latitude") || HasValue(input, "longitude"))
        {
            if (!TryReadNumber(input, "latitude", out var lat) || !TryReadNumber(input, "longitude", out var lon))
                return Failure("invalid_coordinates", "latitude and longitude must both be given, or both omitted.");
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
                return Failure("invalid_coordinates", "latitude must be between -90 and 90 and longitude between -180 and 180.");
            latitude = lat;
            longitude = lon;
        }

        double? radiusKm = null;
        if (TryReadNumber(input, "radiusKm", out var rawRadius))
        {
            if (rawRadius < GlunoActions.MinSearchRadiusKm || rawRadius > GlunoActions.MaxSearchRadiusKm)
                return Failure("invalid_radius",
                    $"radiusKm must be between {GlunoActions.MinSearchRadiusKm} and {GlunoActions.MaxSearchRadiusKm}.");
            radiusKm = rawRadius;
        }

        var interests = ReadStringArray(input, "interests", GlunoActions.MaxSearchInterests, MaxCategoryLength);

        // Geography: use what the model gave, otherwise derive an origin from
        // the Adventure. Coordinates beat a label — a label is a guess the
        // provider has to re-resolve.
        var origin = await ResolveOriginAsync(input, trip, latitude, longitude, near, ct);

        // Two independent ceilings: the per-turn one above bounds a single
        // answer, this one bounds a single account over an hour. Claimed
        // before the call, so a failing provider cannot become free retries.
        if (!_usageLimiter.TryClaimExternalSearch(scope.UserId))
        {
            return ReadResult(new
            {
                providerConfigured = true,
                providerFailed = false,
                rateLimited = true,
                results = Array.Empty<object>(),
                note = "This user has reached their hourly limit for external place lookups. Say that current recommendations are unavailable for now, and keep helping from their plan and general knowledge.",
            });
        }

        _searchesUsed++;

        IReadOnlyList<RankedTravelPlace> ranked;
        try
        {
            ranked = await _travelData.SearchPlacesAsync(new TravelPlaceQuery
            {
                Query = query!,
                Near = origin.Label,
                Latitude = origin.Latitude,
                Longitude = origin.Longitude,
                RadiusKm = radiusKm,
                Category = category,
                Limit = limit,
                Language = scope.Language,
                PriceLevel = priceLevel,
                Interests = interests,
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Never the provider's own error text — it can carry request
            // detail, and the user has no use for it either way.
            return ReadResult(new
            {
                providerConfigured = true,
                providerFailed = true,
                results = Array.Empty<object>(),
                note = "The external lookup did not come back. Tell the user you could not fetch current recommendations, then continue helping from their plan and general knowledge.",
            });
        }

        var cards = ranked.Select(ToPlaceCard).ToList();

        return new GlunoActionOutcome
        {
            Ok = true,
            Places = cards,
            // SideQuest's own request, not the provider's answer — a resolved
            // destination, our category vocabulary, our search words stripped
            // to search words. The user's sentence is not in it.
            PlaceSearch = new GlunoPlaceSearchContext
            {
                Near = origin.Label ?? string.Empty,
                Category = TravelPlaceCategories.ToWireValue(category),
                Query = GlunoPlaceSearchContexts.Sanitise(query),
                Language = scope.Language,
                Limit = limit,
                OriginSource = origin.Source,
                SearchedAtUtc = DateTime.UtcNow,
            },
            ResultJson = JsonSerializer.Serialize(new
            {
                providerConfigured = true,
                providerFailed = false,
                searchedAround = new
                {
                    label = origin.Label,
                    latitude = origin.Latitude,
                    longitude = origin.Longitude,
                    radiusKm,
                    source = origin.Source,
                },
                // Stated in the payload so the model cannot mistake this
                // ordering for the provider's own.
                rankedBy = "sidequest",
                results = ranked.Select(r => new
                {
                    provider = r.Place.Provider,
                    externalId = r.Place.ExternalId,
                    name = r.Place.Name,
                    category = r.Place.Category,
                    categoryLabel = r.Place.CategoryLabel,
                    address = r.Place.Address,
                    rating = r.Place.Rating,
                    ratingScaleMax = r.Place.RatingScaleMax,
                    reviewCount = r.Place.ReviewCount,
                    priceLevel = r.Place.PriceLevel,
                    distanceKm = r.Place.DistanceKm,
                    openingHours = r.Place.OpeningHours,
                    reviewSummary = r.Place.ReviewSummary,
                    providerUrl = r.Place.ProviderUrl,
                    attribution = r.Place.SourceAttribution,
                    rankingSignals = r.Signals,
                }),
                note =
                    "These results are already shown to the user as cards, so do not repeat every field in your text. "
                    + "Pick 3-5, say in one line why each fits, and attribute the provider when you quote a rating, "
                    + "review count or price. The order is SideQuest's ranking, not the provider's — never say the "
                    + "provider recommends this order. A field that is null was not returned: do not fill it in, and "
                    + "never call a place open now unless openingHours actually says so.",
            }, GlunoJson.Options),
        };
    }

    private static GlunoPlaceCard ToPlaceCard(RankedTravelPlace ranked) => new()
    {
        Provider = ranked.Place.Provider,
        ExternalId = ranked.Place.ExternalId,
        ProviderPlaceId = ranked.Place.ProviderPlaceId,
        Name = ranked.Place.Name,
        Category = ranked.Place.Category,
        CategoryLabel = ranked.Place.CategoryLabel,
        Address = ranked.Place.Address,
        Latitude = ranked.Place.Latitude,
        Longitude = ranked.Place.Longitude,
        Rating = ranked.Place.Rating,
        RatingScaleMax = ranked.Place.RatingScaleMax,
        ReviewCount = ranked.Place.ReviewCount,
        PriceLevel = ranked.Place.PriceLevel,
        ImageUrl = ranked.Place.ImageUrl,
        ProviderUrl = ranked.Place.ProviderUrl,
        SourceAttribution = ranked.Place.SourceAttribution,
        DistanceKm = ranked.Place.DistanceKm,
        OpeningHours = ranked.Place.OpeningHours,
        ReviewSummary = ranked.Place.ReviewSummary,
        Signals = ranked.Signals,
        AllowsContentPersistence = ranked.Place.AllowsContentPersistence,
        AllowsIdentityPersistence = ranked.Place.AllowsIdentityPersistence,
    };

    private sealed record SearchOrigin(string? Label, double? Latitude, double? Longitude, string Source);

    /// <summary>
    /// Where to search around.
    ///
    /// Order of preference, and why:
    ///   1. coordinates the model supplied — it read them off a day location
    ///      in the context, so they are exact
    ///   2. a place label the model supplied — a real name the provider can
    ///      resolve ("Vieux Nice"), never a phrase like "the hotel"
    ///   3. the Adventure's day location for the date being discussed, which
    ///      is where the travellers actually are that day
    ///   4. the Adventure's destination coordinates
    ///   5. the destination's name, as a last resort
    ///
    /// Nothing else about the trip is involved. The origin is a point or a
    /// place name — the plan itself never crosses the provider boundary.
    /// </summary>
    private async Task<SearchOrigin> ResolveOriginAsync(
        JsonElement input, TripGuard? trip, double? latitude, double? longitude, string? near, CancellationToken ct)
    {
        if (latitude.HasValue && longitude.HasValue)
            return new SearchOrigin(near, latitude, longitude, "model_coordinates");

        if (!string.IsNullOrWhiteSpace(near))
            return new SearchOrigin(near, null, null, "model_area");

        if (trip == null) return new SearchOrigin(null, null, null, "none");

        DateOnly? anchorDate = null;
        if (HasValue(input, "nearDate") && TryReadDate(input, "nearDate", out var parsedDate, out _))
            anchorDate = parsedDate;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var dayLocations = _db.TripDayLocations
            .AsNoTracking()
            .Where(d => d.TripId == trip.TripId && d.SortIndex == 0);

        // The day being discussed, then today, then the trip's first known
        // location — each a strictly better guess than the one after it.
        var match = anchorDate.HasValue
            ? await dayLocations
                .Where(d => d.StartDate <= anchorDate.Value)
                .OrderByDescending(d => d.StartDate)
                .Select(d => new { d.LocationLabel, d.Latitude, d.Longitude })
                .FirstOrDefaultAsync(ct)
            : null;

        match ??= await dayLocations
            .Where(d => d.StartDate <= today)
            .OrderByDescending(d => d.StartDate)
            .Select(d => new { d.LocationLabel, d.Latitude, d.Longitude })
            .FirstOrDefaultAsync(ct);

        match ??= await dayLocations
            .OrderBy(d => d.StartDate)
            .Select(d => new { d.LocationLabel, d.Latitude, d.Longitude })
            .FirstOrDefaultAsync(ct);

        if (match != null)
            return new SearchOrigin(match.LocationLabel, match.Latitude, match.Longitude, "day_location");

        if (trip.DestinationLatitude.HasValue && trip.DestinationLongitude.HasValue)
        {
            return new SearchOrigin(
                trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude, "trip_destination");
        }

        return string.IsNullOrWhiteSpace(trip.Destination)
            ? new SearchOrigin(null, null, null, "none")
            : new SearchOrigin(trip.Destination, null, null, "trip_destination_label");
    }

    // ── SideQuest app expertise ───────────────────────────────────────────

    /// <summary>
    /// The registry is the only description of the app Gluno gets.
    ///
    /// Deliberately narrowed to a handful of entries rather than dumped whole:
    /// twenty-odd capabilities in context is both expensive and an invitation
    /// to blend two of them into a feature that does not exist.
    /// </summary>
    private GlunoActionOutcome SearchFeatures(JsonElement input, GlunoActionScope scope)
    {
        if (!TryReadText(input, "query", 200, required: true, out var query, out var queryError))
            return Failure("invalid_query", queryError!);

        var limit = TryReadNumber(input, "limit", out var rawLimit) ? (int)rawLimit : 4;
        var matches = SideQuestCapabilitySearch.Search(query!, scope.Language, scope.CurrentScreen, limit);

        if (matches.Count == 0)
        {
            return ReadResult(new
            {
                registryVersion = SideQuestCapabilities.Version,
                features = Array.Empty<object>(),
                note = "SideQuest has nothing matching that. Say so plainly — do not describe a button, screen or setting that was not returned here — and offer the closest thing the app does do.",
            });
        }

        return ReadResult(new
        {
            registryVersion = SideQuestCapabilities.Version,
            currentScreen = scope.CurrentScreen,
            features = matches.Select(match => Summarise(match.Capability, scope.Language, match.MatchedOn)),
            note = "Answer from these only. Give at most 2-4 concrete steps using the wording in 'where', and offer navigate_in_sidequest when there is a target.",
        });
    }

    private GlunoActionOutcome GetFeature(JsonElement input, GlunoActionScope scope)
    {
        var featureId = ReadOptionalString(input, "featureId");
        var capability = SideQuestCapabilities.Find(featureId);

        // An id from an older registry version simply does not resolve. That
        // is a normal answer, not an error — old conversations must not break
        // when the registry changes.
        if (capability == null)
        {
            return ReadResult(new
            {
                registryVersion = SideQuestCapabilities.Version,
                found = false,
                note = "There is no such feature in this version of SideQuest. Do not describe it — search for what the user actually wants instead.",
            });
        }

        return ReadResult(new
        {
            registryVersion = SideQuestCapabilities.Version,
            found = true,
            feature = Detail(capability, scope.Language),
        });
    }

    /// <summary>
    /// What Gluno may actually do for THIS user, right now.
    ///
    /// The honesty check behind "I can prepare that for you". A capability
    /// with no Gluno actions, or one this user's role does not reach, comes
    /// back as something they have to do themselves.
    /// </summary>
    private async Task<GlunoActionOutcome> GetAvailableActionsAsync(
        GlunoActionScope scope, TripGuard? guard, CancellationToken ct)
    {
        var trip = guard;
        if (trip == null && scope.TripId.HasValue)
        {
            trip = await LoadTripGuardAsync(scope.UserId, scope.TripId.Value, ct);
        }

        var isOwner = trip != null
            && await _db.TripMembers.AnyAsync(
                tm => tm.TripId == trip.TripId && tm.UserId == scope.UserId && tm.IsOwner, ct);

        var available = GlunoActions.All
            .Where(action => !action.RequiresTrip || trip != null)
            .Where(action => !action.RequiresEditPermission || trip?.CanEdit == true)
            .Select(action => action.Name)
            .ToList();

        return ReadResult(new
        {
            registryVersion = SideQuestCapabilities.Version,
            adventureSelected = trip != null,
            canEditAdventure = trip?.CanEdit ?? false,
            isAdventureOwner = isOwner,
            actionsYouCanTake = available,
            note =
                "These are the only things you can do yourself. Anything else in SideQuest the user has to "
                + "do in the app — say so directly rather than implying you will handle it. Owner-only "
                + "features are not something to instruct a non-owner to use.",
        });
    }

    /// <summary>
    /// Help for the screen the user is already looking at.
    ///
    /// The point is brevity: someone standing on the Documents screen asking
    /// where documents are should be told they are already here, not walked
    /// through the navigation.
    /// </summary>
    private GlunoActionOutcome GetCurrentScreenHelp(GlunoActionScope scope)
    {
        if (!SideQuestScreens.IsKnown(scope.CurrentScreen))
        {
            return ReadResult(new
            {
                currentScreen = (string?)null,
                features = Array.Empty<object>(),
                note = "The app did not say which screen they are on. Give the normal short instructions instead of assuming.",
            });
        }

        var matches = SideQuestCapabilitySearch.ForScreen(scope.CurrentScreen!);

        return ReadResult(new
        {
            currentScreen = scope.CurrentScreen,
            features = matches.Select(match => Summarise(match.Capability, scope.Language, "screen")),
            note = "The user is ALREADY on this screen. Do not tell them how to get here — answer for what they can do from where they are.",
        });
    }

    /// <summary>
    /// Verifies a navigation target and turns it into a card the app can offer.
    ///
    /// Two things make route injection impossible rather than merely unlikely:
    /// the target must be on the allow-list (there is no field for a path or a
    /// URL anywhere in the schema), and every id that reaches it is re-checked
    /// against this user's membership. Offering a screen is not a change, and
    /// nothing navigates until the user taps.
    /// </summary>
    private async Task<GlunoActionOutcome> NavigateAsync(
        JsonElement input, GlunoActionScope scope, CancellationToken ct)
    {
        var target = ReadOptionalString(input, "target");
        if (!GlunoNavigationTargets.IsKnown(target))
            return Failure("unknown_target", "That is not a screen SideQuest has.");

        var rules = GlunoNavigationTargets.RulesFor(target!)!;

        TripGuard? trip = null;
        if (rules.RequiresTrip)
        {
            if (scope.TripId == null)
                return Failure("no_adventure_selected", "That screen belongs to an Adventure, and this conversation is not scoped to one.");

            // Membership re-checked here, not trusted from the conversation.
            trip = await LoadTripGuardAsync(scope.UserId, scope.TripId.Value, ct);
            if (trip == null)
                return Failure("not_a_member", "That Adventure is not available to this user.");
        }

        Guid? activityId = null;
        if (rules.RequiresActivity)
        {
            if (!TryReadText(input, "activityId", 64, required: true, out var rawId, out var idError))
                return Failure("invalid_activity", idError!);
            if (!Guid.TryParse(rawId, out var parsedId))
                return Failure("invalid_activity", "That is not an Activity id from this Adventure.");

            // Scoped to this trip AND to what this user may see — a deleted
            // Activity, or somebody else's unrevealed SideQuest, resolves to
            // nothing rather than to a broken screen.
            var exists = await _db.TripActivities.AnyAsync(
                a => a.Id == parsedId
                     && a.TripId == trip!.TripId
                     && (!a.IsHidden || a.OwnerId == scope.UserId),
                ct);

            if (!exists)
                return Failure("activity_not_found", "That Activity is no longer part of this Adventure.");

            activityId = parsedId;
        }

        string? date = null;
        if (rules.AcceptsDate && HasValue(input, "date"))
        {
            if (!TryReadDate(input, "date", out var parsedDate, out var dateError))
                return Failure("invalid_date", dateError!);

            if (trip != null && !TripDateRange.Contains(trip.StartDate, trip.EndDate, parsedDate))
                return Failure("date_out_of_range", TripDateRange.OutOfRangeMessage(trip.StartDate, trip.EndDate));

            date = Format(parsedDate);
        }

        TryReadText(input, "reason", 160, required: false, out var reason, out _);

        var label = LabelForTarget(target!, scope.Language);

        var card = new GlunoNavigationCard
        {
            TargetId = target!,
            Label = label,
            Reason = reason,
            TripId = trip?.TripId,
            ActivityId = activityId,
            Date = date,
        };

        return new GlunoActionOutcome
        {
            Ok = true,
            Navigations = [card],
            ResultJson = JsonSerializer.Serialize(new
            {
                status = "offered",
                target = card.TargetId,
                label = card.Label,
                note =
                    "The user now has a button to open this. Nothing has been changed or saved — do not say "
                    + "otherwise. Mention it in one short line; the button speaks for itself.",
            }, GlunoJson.Options),
        };
    }

    /// The capability that owns a target supplies its user-facing name, so the
    /// button says what the app calls the screen rather than a route id.
    private static string LabelForTarget(string target, string language)
    {
        var capability = SideQuestCapabilities.All.FirstOrDefault(c => c.NavigationTarget == target);
        if (capability != null) return capability.Name(language);

        return language == "sv" ? "Öppna i SideQuest" : "Open in SideQuest";
    }

    private static object Summarise(SideQuestCapability capability, string language, string matchedOn)
        => new
        {
            id = capability.Id,
            name = capability.Name(language),
            description = capability.Description(language),
            where = capability.Where(language),
            audience = capability.Audience,
            navigationTarget = capability.NavigationTarget,
            glunoCanDoThis = capability.GlunoActions.Count > 0,
            featureFlag = capability.FeatureFlag,
            matchedOn,
        };

    private static object Detail(SideQuestCapability capability, string language)
        => new
        {
            id = capability.Id,
            name = capability.Name(language),
            description = capability.Description(language),
            where = capability.Where(language),
            audience = capability.Audience,
            prerequisites = capability.Prerequisites,
            screens = capability.Screens,
            navigationTarget = capability.NavigationTarget,
            glunoActions = capability.GlunoActions,
            glunoCanDoThis = capability.GlunoActions.Count > 0,
            limitations = capability.Limitations(language),
            featureFlag = capability.FeatureFlag,
            minClientVersion = capability.MinClientVersion,
        };

    /// <summary>
    /// Records a planning preference.
    ///
    /// The key is checked against an allow-list, not trusted: this store must
    /// not become a place the model writes whatever it finds interesting. The
    /// scope is checked too, and a trip scope without a trip falls back to the
    /// conversation rather than silently becoming global.
    /// </summary>
    private async Task<GlunoActionOutcome> RememberPreferenceAsync(
        JsonElement input, GlunoActionScope scope, CancellationToken ct)
    {
        if (scope.ConversationId == Guid.Empty)
            return Failure("no_conversation", "There is no conversation to attach this to.");

        var key = ReadOptionalString(input, "key");
        if (key == null || !Models.GlunoPreferenceKeys.IsKnown(key))
            return Failure("unknown_preference", "That is not a preference SideQuest stores.");

        if (!TryReadText(input, "value", GlunoPreferenceService.MaxValueLength, required: true, out var value, out var valueError))
            return Failure("invalid_value", valueError!);

        var requestedScope = ReadOptionalString(input, "scope") ?? Models.GlunoPreferenceScopes.Conversation;
        if (!Models.GlunoPreferenceScopes.IsKnown(requestedScope))
            requestedScope = Models.GlunoPreferenceScopes.Conversation;

        var stored = await _preferences.RememberAsync(
            scope.UserId, scope.ConversationId, scope.TripId, key, value!, requestedScope, ct);

        return ReadResult(new
        {
            status = "remembered",
            key = stored.Key,
            value = stored.Value,
            scope = stored.Scope,
            note = "This is now in the context on every later turn. Do not ask about it again, and do not tell the user you 'saved' anything — just carry on using it.",
        });
    }

    private async Task<GlunoActionOutcome> ForgetPreferenceAsync(
        JsonElement input, GlunoActionScope scope, CancellationToken ct)
    {
        if (scope.ConversationId == Guid.Empty)
            return Failure("no_conversation", "There is no conversation to forget this from.");

        var key = ReadOptionalString(input, "key");
        if (key == null || !Models.GlunoPreferenceKeys.IsKnown(key))
            return Failure("unknown_preference", "That is not a preference SideQuest stores.");

        var removed = await _preferences.ForgetAsync(scope.UserId, scope.ConversationId, scope.TripId, key, ct);

        return ReadResult(new
        {
            status = removed > 0 ? "forgotten" : "not_stored",
            key,
            note = removed > 0
                ? "Gone. Stop applying it, and ask again next time it matters."
                : "There was nothing stored for that, so nothing changed.",
        });
    }

    private async Task<GlunoActionOutcome> GetTripOverviewAsync(GlunoActionScope scope, CancellationToken ct)
    {
        // Deliberately the same builder the turn's context came from, so an
        // overview can never show more than the context rules allow.
        var context = await _contextBuilder.BuildAsync(scope.UserId, scope.TripId, scope.ConversationId, ct);
        if (context.Trip == null)
            return Failure("not_a_member", "That Adventure is not available to this user.");

        return ReadResult(new { trip = context.Trip, truncated = context.Truncated });
    }

    // ── Result helpers ────────────────────────────────────────────────────

    private static GlunoActionOutcome Success(GlunoProposal proposal, object modelResult) => new()
    {
        Ok = true,
        ResultJson = JsonSerializer.Serialize(modelResult, GlunoJson.Options),
        Proposal = proposal,
    };

    private static GlunoActionOutcome ReadResult(object modelResult) => new()
    {
        Ok = true,
        ResultJson = JsonSerializer.Serialize(modelResult, GlunoJson.Options),
    };

    private static GlunoActionOutcome Failure(string code, string message) => new()
    {
        Ok = false,
        ErrorCode = code,
        ResultJson = JsonSerializer.Serialize(new { status = "rejected", error = code, message }, GlunoJson.Options),
    };

    // ── Parsing helpers ───────────────────────────────────────────────────

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? Min(DateOnly? a, DateOnly? b)
        => a.HasValue && b.HasValue ? (a.Value <= b.Value ? a : b) : a ?? b;

    private static DateOnly? Max(DateOnly? a, DateOnly? b)
        => a.HasValue && b.HasValue ? (a.Value >= b.Value ? a : b) : a ?? b;

    private static bool HasValue(JsonElement input, string name)
        => input.ValueKind == JsonValueKind.Object
           && input.TryGetProperty(name, out var el)
           && el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
           && !(el.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(el.GetString()));

    private static bool TryReadDate(JsonElement input, string name, out DateOnly value, out string? error)
    {
        value = default;
        if (!HasValue(input, name))
        {
            error = $"{name} is required and must be a date in YYYY-MM-DD format.";
            return false;
        }

        var raw = input.GetProperty(name).ValueKind == JsonValueKind.String
            ? input.GetProperty(name).GetString()
            : null;

        if (raw == null || !DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            error = $"{name} must be a date in YYYY-MM-DD format.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadTime(JsonElement input, string name, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!HasValue(input, name)) return true;

        var el = input.GetProperty(name);
        var raw = el.ValueKind == JsonValueKind.String ? el.GetString()?.Trim() : null;
        if (raw == null || !TimePattern.IsMatch(raw))
        {
            error = $"{name} must be a 24-hour time in HH:MM format.";
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryReadText(
        JsonElement input, string name, int maxLength, bool required, out string? value, out string? error)
    {
        value = null;
        error = null;

        if (!HasValue(input, name))
        {
            if (!required) return true;
            error = $"{name} is required.";
            return false;
        }

        var el = input.GetProperty(name);
        var raw = el.ValueKind == JsonValueKind.String ? el.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!required) return true;
            error = $"{name} is required.";
            return false;
        }

        if (raw.Length > maxLength)
        {
            error = $"{name} must be at most {maxLength} characters.";
            return false;
        }

        value = raw;
        return true;
    }

    private static string? ReadOptionalString(JsonElement input, string name)
    {
        if (!HasValue(input, name)) return null;
        var element = input.GetProperty(name);
        return element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
    }

    /// <summary>
    /// A bounded list of short strings. Over-long entries are trimmed away
    /// rather than rejecting the whole call — these are ranking hints, and one
    /// malformed keyword should not cost the user their search.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(
        JsonElement input, string name, int maxCount, int maxLength)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (values.Count >= maxCount) break;
            if (item.ValueKind != JsonValueKind.String) continue;

            var text = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength) continue;

            values.Add(text);
        }

        return values;
    }

    private static bool TryReadNumber(JsonElement input, string name, out double value)
    {
        value = 0;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Number) return false;
        value = el.GetDouble();
        return true;
    }
}

/// <summary>
/// One JSON configuration for everything Gluno serialises, so the context the
/// model sees and the payloads the app receives never drift apart in casing.
/// </summary>
public static class GlunoJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
