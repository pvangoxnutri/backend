namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The allow-list of places Gluno may offer to open.
///
/// These are stable TARGET IDS, not routes. The model never sees a path, never
/// sends one, and could not use one if it did — the mobile app owns the
/// mapping from target id to screen. That is what makes route injection
/// impossible here by construction rather than by sanitising a string: there
/// is no field anywhere in the tool schema that accepts a URL or a path.
///
/// An unknown target id resolves to nothing and is dropped, so a target added
/// in a newer backend simply does not render on an older client.
/// </summary>
public static class GlunoNavigationTargets
{
    public const string Home = "home";
    public const string CreateAdventure = "create_adventure";
    public const string AdventureOverview = "adventure_overview";
    public const string AdventureFeedDay = "adventure_feed_day";
    public const string AdventureFunctions = "adventure_functions";
    public const string AdventureSettings = "adventure_settings";
    public const string ActivityDetail = "activity_detail";
    public const string ActivityCreate = "activity_create";
    public const string Chat = "chat";
    public const string Documents = "documents";
    public const string Expenses = "expenses";
    public const string Packlist = "packlist";
    public const string Weather = "weather";
    public const string TravelTracker = "travel_tracker";
    public const string Profile = "profile";
    public const string PreviousAdventures = "previous_adventures";
    public const string Support = "support";

    /// <summary>
    /// What each target needs. Used to decide whether Gluno may offer it at
    /// all, and what has to be verified before it does.
    /// </summary>
    public sealed record TargetRules(bool RequiresTrip, bool RequiresActivity, bool AcceptsDate);

    private static readonly Dictionary<string, TargetRules> Rules = new(StringComparer.Ordinal)
    {
        [Home] = new(false, false, false),
        [CreateAdventure] = new(false, false, false),
        [AdventureOverview] = new(true, false, false),
        [AdventureFeedDay] = new(true, false, true),
        [AdventureFunctions] = new(true, false, false),
        [AdventureSettings] = new(true, false, false),
        [ActivityDetail] = new(true, true, false),
        // A date may be prefilled into the create form; nothing is saved by
        // opening it.
        [ActivityCreate] = new(true, false, true),
        [Chat] = new(true, false, false),
        [Documents] = new(true, false, false),
        [Expenses] = new(true, false, false),
        [Packlist] = new(true, false, false),
        [Weather] = new(true, false, false),
        [TravelTracker] = new(false, false, false),
        [Profile] = new(false, false, false),
        [PreviousAdventures] = new(false, false, false),
        [Support] = new(false, false, false),
    };

    public static readonly IReadOnlyList<string> All = Rules.Keys.ToList();

    public static bool IsKnown(string? targetId) => targetId != null && Rules.ContainsKey(targetId);

    public static TargetRules? RulesFor(string targetId)
        => Rules.TryGetValue(targetId, out var rules) ? rules : null;
}

/// <summary>
/// A verified place the app can offer to open.
///
/// Everything in it has been checked against the database: the trip exists and
/// the user is a member, the Activity belongs to that trip and is visible to
/// them, the date parses. The app can navigate on it without re-checking, and
/// — critically — WITHOUT the user ever having been navigated automatically:
/// this is a card with a button, not a redirect.
/// </summary>
public sealed class GlunoNavigationCard
{
    public required string TargetId { get; init; }
    /// What the button opens, in the user's language. Comes from the
    /// capability registry, so it matches what the app actually calls it.
    public required string Label { get; init; }
    /// Why opening it helps. One short line; never "this has been changed".
    public string? Reason { get; init; }

    public Guid? TripId { get; init; }
    public Guid? ActivityId { get; init; }
    /// ISO date, for a feed day or a prefilled create form.
    public string? Date { get; init; }
}
