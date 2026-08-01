using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One action Gluno may ask for, described in provider-neutral terms.
///
/// This layer deliberately knows nothing about Anthropic, tools, or the wire
/// format — <see cref="AnthropicGlunoAiProvider"/> translates these into
/// whatever the model API wants. That separation is what keeps the action
/// catalogue reviewable as a security surface on its own: what Gluno can ask
/// for is decided here, and the same catalogue is validated server-side in
/// <see cref="GlunoActionExecutor"/> regardless of what any model sends.
/// </summary>
public sealed class GlunoActionDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// A JSON Schema object. Advisory only: it shapes what the model sends,
    /// it does not decide what the server accepts.
    public required JsonElement InputSchema { get; init; }
    /// True when the action is meaningless without a selected Adventure. Such
    /// actions are not even offered on a global conversation.
    public bool RequiresTrip { get; init; }
    /// True when the action proposes a change. Not offered to a member of a
    /// read-only Adventure — proposing something they could never accept is
    /// worse than not offering it.
    public bool RequiresEditPermission { get; init; }
}

/// <summary>
/// The complete catalogue of what Gluno may ask the backend to do.
///
/// Every action here is READ or PROPOSE. There is no write action, and adding
/// one is not a matter of extending this list — nothing in the Gluno pipeline
/// has a code path that persists a trip change. A "propose_*" action returns a
/// validated preview; the user accepts it in the app through the ordinary,
/// already-authorised endpoints, or it never happens.
///
/// What the model is NOT allowed to supply, in any action:
///   • a user id — always the authenticated caller
///   • a trip id — always the conversation's own scope
///   • an owner, assignee or member — never model-chosen
/// Those are taken from <see cref="GlunoActionScope"/>, so a model that tries
/// to name someone else's trip or act as another user has nowhere to put it.
/// </summary>
public static class GlunoActions
{
    public const string ProposeActivity = "propose_activity";
    public const string ProposeDayPlan = "propose_day_plan";
    public const string ProposeDayLocation = "propose_day_location";
    public const string ProposeActivityMove = "propose_activity_move";
    public const string ProposeTripDateChange = "propose_trip_date_change";
    public const string SearchPlaces = "search_places";
    public const string GetTripOverview = "get_trip_overview";
    public const string RememberPreference = "remember_preference";
    public const string ForgetPreference = "forget_preference";
    public const string SearchSideQuestFeatures = "search_sidequest_features";
    public const string GetSideQuestFeature = "get_sidequest_feature";
    public const string GetAvailableActions = "get_available_actions";
    public const string GetCurrentScreenHelp = "get_current_screen_help";
    public const string NavigateInSideQuest = "navigate_in_sidequest";

    /// A day plan proposes at most this many activities at once. Keeps a
    /// single proposal reviewable on a phone screen.
    public const int MaxDayPlanActivities = 8;

    // ── search_places bounds ─────────────────────────────────────────────
    // Enforced server-side in GlunoActionExecutor, not just declared here:
    // the schema shapes what the model sends, these are the actual limits.

    public const int MaxSearchQueryLength = 200;
    public const int DefaultSearchLimit = 5;
    /// A phone answer with more than a handful of place cards is a wall, and
    /// each result costs an upstream detail call.
    public const int MaxSearchLimit = 8;
    public const double MinSearchRadiusKm = 0.2;
    /// Beyond this "nearby" stops meaning anything, and the provider's own
    /// relevance degrades badly.
    public const double MaxSearchRadiusKm = 25;
    /// Interest keywords are a ranking hint, not a query language.
    public const int MaxSearchInterests = 6;
    /// How many external searches ONE Gluno turn may make. Bounds both spend
    /// and the wait before the user gets an answer.
    public const int MaxSearchesPerTurn = 3;

    private static JsonElement Schema(object schema)
        => JsonSerializer.SerializeToElement(schema);

    private static readonly object DateProperty = new
    {
        type = "string",
        description = "Date in YYYY-MM-DD format. Must fall inside the Adventure's dates.",
    };

    private static readonly object TimeProperty = new
    {
        type = "string",
        description = "Time of day in 24-hour HH:MM format. Omit when the time is not decided.",
    };

    private static readonly object CategoryProperty = new
    {
        type = "string",
        description = "Optional category key, e.g. food, drink, hotel, transport, sight, activity.",
    };

    public static readonly IReadOnlyList<GlunoActionDefinition> All = new List<GlunoActionDefinition>
    {
        new()
        {
            Name = ProposeActivity,
            Description =
                "Propose ONE new Activity for the selected Adventure. Produces a preview the user must "
                + "accept in the app; it does not create anything. Use for a single suggestion.",
            RequiresTrip = true,
            RequiresEditPermission = true,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    date = DateProperty,
                    title = new { type = "string", description = "Short title, at most 120 characters." },
                    description = new { type = "string", description = "Optional detail, at most 2000 characters." },
                    time = TimeProperty,
                    endDate = new { type = "string", description = "Optional end date (YYYY-MM-DD) for a multi-day stay such as a hotel." },
                    endTime = new { type = "string", description = "Optional end time (HH:MM)." },
                    category = CategoryProperty,
                },
                required = new[] { "date", "title" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = ProposeDayPlan,
            Description =
                "Propose a whole day's worth of Activities for ONE date in the selected Adventure. "
                + "Produces a single preview containing every suggested Activity in order; the user "
                + "accepts or discards it in the app. Nothing is created here.\n"
                + "SideQuest lays the day out for you. Give the stops you want and, where you know "
                + "them, coordinates — SideQuest works out start times, how long each stop takes, "
                + "travel between them and whether it all fits, and hands the result back to you. "
                + "Do not compute times yourself: use the schedule that comes back, and read out its "
                + "warnings honestly. Only set a time on a stop that is genuinely fixed (a booking, a "
                + "reservation, a tour that starts at a stated hour).\n"
                + "Include coordinates whenever you have them — from search_places results or the "
                + "Adventure's existing Activities. Without them travel times cannot be verified.",
            RequiresTrip = true,
            RequiresEditPermission = true,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    date = DateProperty,
                    startTime = new
                    {
                        type = "string",
                        description = "When the day should start (HH:MM). Omit unless the user said.",
                    },
                    endTime = new
                    {
                        type = "string",
                        description = "When the day should wind down (HH:MM). Omit unless the user said.",
                    },
                    transportMode = new
                    {
                        type = "string",
                        @enum = new[] { "walking", "driving", "transit", "cycling" },
                        description =
                            "How they are getting around. Omit unless the user told you — SideQuest "
                            + "already knows their stated preference and will not assume a car.",
                    },
                    activities = new
                    {
                        type = "array",
                        description = $"The day's stops in the order you want them, at most {MaxDayPlanActivities}.",
                        maxItems = MaxDayPlanActivities,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string" },
                                description = new { type = "string" },
                                time = new
                                {
                                    type = "string",
                                    description =
                                        "HH:MM. ONLY for a stop with a genuinely fixed time, such as a booking. "
                                        + "Leave it out and SideQuest will schedule the stop.",
                                },
                                category = CategoryProperty,
                                durationMinutes = new
                                {
                                    type = "integer",
                                    description =
                                        "How long this stop takes, if you actually know — for example because "
                                        + "the place data said so. Omit and SideQuest uses its own estimate for "
                                        + "the category.",
                                },
                                latitude = new { type = "number", description = "Latitude, -90 to 90." },
                                longitude = new { type = "number", description = "Longitude, -180 to 180." },
                                locationLabel = new { type = "string", description = "Place name as a traveller would say it." },
                                placeId = new
                                {
                                    type = "string",
                                    description = "The externalId from a search_places result, when this stop came from one.",
                                },
                            },
                            required = new[] { "title" },
                            additionalProperties = false,
                        },
                    },
                },
                required = new[] { "date", "activities" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = ProposeDayLocation,
            Description =
                "Propose where the travellers are on a given date in the selected Adventure — the day's "
                + "town or area, not an Activity. Produces a preview only.",
            RequiresTrip = true,
            RequiresEditPermission = true,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    date = DateProperty,
                    label = new { type = "string", description = "Place name as a traveller would say it, e.g. \"Porto\"." },
                    latitude = new { type = "number", description = "Optional latitude, -90 to 90." },
                    longitude = new { type = "number", description = "Optional longitude, -180 to 180." },
                },
                required = new[] { "date", "label" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = ProposeActivityMove,
            Description =
                "Propose moving an EXISTING Activity of the selected Adventure to another date and/or "
                + "time. The activityId must come from the Adventure's plan in the context. Produces a "
                + "preview only.",
            RequiresTrip = true,
            RequiresEditPermission = true,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    activityId = new { type = "string", description = "Id of an Activity that appears in the context for this Adventure." },
                    toDate = DateProperty,
                    toTime = TimeProperty,
                },
                required = new[] { "activityId", "toDate" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = ProposeTripDateChange,
            Description =
                "Propose changing the selected Adventure's start and/or end date. Set clearEndDate to "
                + "true to propose making it open-ended. Produces a preview only.",
            RequiresTrip = true,
            RequiresEditPermission = true,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    startDate = new { type = "string", description = "Proposed start date (YYYY-MM-DD). Omit to keep the current one." },
                    endDate = new { type = "string", description = "Proposed end date (YYYY-MM-DD). Omit to keep the current one." },
                    clearEndDate = new { type = "boolean", description = "True to propose making the Adventure open-ended (no end date)." },
                },
                required = Array.Empty<string>(),
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = SearchPlaces,
            Description =
                "Look up REAL places — restaurants, attractions, hotels — from SideQuest's external "
                + "travel-data provider. This is the only way to get current ratings, review counts and "
                + "price bands; never state those from memory.\n"
                + "Prefer coordinates: the Adventure's day locations in the context carry latitude and "
                + "longitude, so pass the ones for the day being discussed. Use `near` only when you have "
                + "no coordinates, and give it a real place name (\"Vieux Nice\", \"Hotel Negresco, Nice\") "
                + "— never a phrase like \"the hotel\" or \"here\".\n"
                + "Results come back ranked by SideQuest, not by the provider, with the signals behind that "
                + "order. Returns providerConfigured:false when no provider is available and "
                + "providerFailed:true when the lookup could not be completed — in both cases say so and "
                + "keep helping from the plan and general knowledge.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = $"What to look for, e.g. \"seafood restaurant\". At most {MaxSearchQueryLength} characters." },
                    near = new { type = "string", description = "Town, neighbourhood or named landmark to search around. Only used when no coordinates are given." },
                    latitude = new { type = "number", description = "Latitude to search around, -90 to 90. Use the day location's coordinates from the context when you have them." },
                    longitude = new { type = "number", description = "Longitude to search around, -180 to 180. Must be given together with latitude." },
                    radiusKm = new { type = "number", description = $"Search radius in kilometres, {MinSearchRadiusKm}-{MaxSearchRadiusKm}. Only meaningful with coordinates." },
                    nearDate = new { type = "string", description = "A date (YYYY-MM-DD) in this Adventure. When no coordinates are given, the search is centred on where the travellers are that day." },
                    category = new
                    {
                        type = "string",
                        @enum = new[] { "restaurant", "attraction", "hotel", "general" },
                        description = "What kind of place. Use general when the question does not fit the others.",
                    },
                    limit = new { type = "integer", description = $"How many results, 1-{MaxSearchLimit}. Default {DefaultSearchLimit}." },
                    priceLevel = new { type = "string", description = "Budget hint the user expressed, e.g. \"$\" or \"$$$\". Used for ranking, never as a hard filter." },
                    interests = new
                    {
                        type = "array",
                        description = "Short interest keywords the user mentioned, e.g. [\"seafood\", \"outdoor seating\"]. Ranking only.",
                        items = new { type = "string" },
                    },
                },
                required = new[] { "query" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = RememberPreference,
            Description =
                "Remember how this traveller wants to travel, so you never ask the same question twice. "
                + "Use it the moment they state something planning-relevant — pace, budget, food, what they "
                + "want to avoid, how they are getting around, how far they will walk, who they are "
                + "travelling with.\n"
                + "Store their own words, not a category you invented. Choose the narrowest scope that is "
                + "true: 'conversation' for something about today's planning, 'trip' for something true of "
                + "this whole Adventure (\"we have a car\"), 'global' only for how this person always travels. "
                + "Never store health information; a mobility or age note belongs here only as the planning "
                + "constraint the user themselves described.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    key = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "pace", "budget", "interests", "food", "avoid", "transport",
                            "walking_distance", "start_time", "nightlife", "intent",
                            "accessibility", "group_context",
                        },
                        description = "What kind of preference this is.",
                    },
                    value = new { type = "string", description = "The preference in the user's own words, at most 240 characters." },
                    scope = new
                    {
                        type = "string",
                        @enum = new[] { "conversation", "trip", "global" },
                        description = "How far it should reach. Default conversation.",
                    },
                },
                required = new[] { "key", "value" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = ForgetPreference,
            Description =
                "Forget a remembered preference. Use it whenever the user takes something back — \"forget "
                + "that\", \"we don't want a relaxed pace any more\", \"ignore what I said about budget\". "
                + "To CHANGE a preference, call remember_preference with the new value instead; this is for "
                + "removing one entirely.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    key = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "pace", "budget", "interests", "food", "avoid", "transport",
                            "walking_distance", "start_time", "nightlife", "intent",
                            "accessibility", "group_context",
                        },
                        description = "Which preference to remove.",
                    },
                },
                required = new[] { "key" },
                additionalProperties = false,
            }),
        },
        // ── SideQuest app expertise ──────────────────────────────────────
        // These are the ONLY source of truth about what the app can do. If a
        // feature is not returned by one of them, it does not exist as far as
        // Gluno is concerned — inventing a button is the failure mode these
        // exist to prevent.
        new()
        {
            Name = SearchSideQuestFeatures,
            Description =
                "Find out what SideQuest can actually do. Use this WHENEVER the user asks about the app "
                + "itself — how to do something, where a feature lives, whether something is possible — "
                + "before answering.\n"
                + "Never describe a button, screen, menu or setting that did not come back from here. If "
                + "nothing relevant is returned, say plainly that SideQuest does not do that, and offer the "
                + "closest thing it does do.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "What the user is asking about, in their own words. Either language; misspellings are fine." },
                    limit = new { type = "integer", description = "How many features to return, 1-8. Default 4." },
                },
                required = new[] { "query" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = GetSideQuestFeature,
            Description =
                "Full detail for one feature by its id, including where it lives, who may use it, what it "
                + "cannot do, and whether you can act on it yourself. Use it after a search when you need "
                + "the exact limitations before answering.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    featureId = new { type = "string", description = "A feature id returned by search_sidequest_features." },
                },
                required = new[] { "featureId" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = GetAvailableActions,
            Description =
                "What YOU can do for this user right now, given their role in this Adventure and which "
                + "features are switched on. Use it before promising anything — it is the difference "
                + "between \"I can prepare that for you\" and \"you'll need to do that yourself\".",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>(),
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = GetCurrentScreenHelp,
            Description =
                "What the user can do on the screen they are looking at. Use it when their question is "
                + "about \"this\" or \"here\", so you can answer for where they already are instead of "
                + "telling them to navigate somewhere they are already standing.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>(),
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = NavigateInSideQuest,
            Description =
                "Offer the user a button that opens a screen in SideQuest. Nothing moves until they tap it, "
                + "and nothing is saved or changed by opening a screen — never describe this as making a "
                + "change.\n"
                + "Only the listed targets exist; there is no way to send a path or a link here. Anything "
                + "needing an Adventure is checked against this user's membership first, so a target you "
                + "are not allowed to offer simply comes back rejected.",
            RequiresTrip = false,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new
                {
                    target = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "home", "create_adventure", "adventure_overview", "adventure_feed_day",
                            "adventure_functions", "adventure_settings", "activity_detail",
                            "activity_create", "chat", "documents", "expenses", "packlist",
                            "weather", "travel_tracker", "profile", "previous_adventures", "support",
                        },
                        description = "Which screen to offer.",
                    },
                    activityId = new { type = "string", description = "Required for activity_detail: an Activity id from this Adventure's plan." },
                    date = new { type = "string", description = "Optional date (YYYY-MM-DD) for adventure_feed_day, or to prefill the date on activity_create." },
                    reason = new { type = "string", description = "One short line on why opening it helps. Never says anything was changed." },
                },
                required = new[] { "target" },
                additionalProperties = false,
            }),
        },
        new()
        {
            Name = GetTripOverview,
            Description =
                "Re-read the selected Adventure's plan straight from SideQuest. The context you were "
                + "given already contains it, so use this only to confirm current state before "
                + "proposing a change, or when the context said it was truncated.",
            RequiresTrip = true,
            RequiresEditPermission = false,
            InputSchema = Schema(new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>(),
                additionalProperties = false,
            }),
        },
    };

    /// <summary>
    /// The actions that make sense for this turn. A global conversation gets
    /// only the trip-independent ones; a read-only member is not offered
    /// proposals they could not accept.
    /// </summary>
    public static IReadOnlyList<GlunoActionDefinition> ForContext(GlunoContext context)
    {
        var hasTrip = context.Trip != null;
        var canEdit = context.Trip?.CanEdit == true;

        return All
            .Where(a => !a.RequiresTrip || hasTrip)
            .Where(a => !a.RequiresEditPermission || canEdit)
            .ToList();
    }

    public static GlunoActionDefinition? Find(string name)
        => All.FirstOrDefault(a => a.Name == name);
}
