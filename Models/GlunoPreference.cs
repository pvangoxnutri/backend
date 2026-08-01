namespace sidequest.backend.Models;

/// <summary>
/// How far a remembered preference reaches.
///
/// The distinction is the whole point of this table. "We want a relaxed pace"
/// said while planning a city break must not quietly govern next year's hiking
/// trip, and "I'm vegetarian" should not have to be repeated in every new
/// conversation. Getting this wrong in either direction is what makes an
/// assistant feel either forgetful or presumptuous.
/// </summary>
public static class GlunoPreferenceScopes
{
    /// This conversation only. The default — a thing said in passing while
    /// planning one afternoon.
    public const string Conversation = "conversation";
    /// This Adventure, across conversations. "We have a car on this trip."
    public const string Trip = "trip";
    /// The user, everywhere. Only for durable facts about how they travel.
    public const string Global = "global";

    public static bool IsKnown(string scope)
        => scope is Conversation or Trip or Global;
}

/// <summary>
/// Who may see a preference.
///
/// SCOPE AND VISIBILITY ARE DIFFERENT QUESTIONS, and conflating them is the bug
/// this type exists to prevent. Scope asks "how long does this apply?" —
/// visibility asks "whose eyes may it reach?". A trip-scoped preference is
/// still PRIVATE by default: "I'd rather not do the hike" said to Gluno in a
/// one-to-one conversation applies to this Adventure and is nobody else's
/// business.
///
/// The default is private, always. Sharing is a deliberate act by the person
/// whose preference it is, never an inference from the fact that it would be
/// useful to the planner.
/// </summary>
public static class GlunoPreferenceVisibility
{
    /// <summary>
    /// This user's own Gluno conversations. The DEFAULT, and the only value a
    /// preference gets without the user explicitly choosing otherwise.
    /// </summary>
    public const string Private = "private";

    /// <summary>
    /// Contributed to this Adventure's shared planning profile.
    ///
    /// Reachable by the group's planning, and still not attributed by name —
    /// the profile carries a neutral member id so a constraint can be honoured
    /// without announcing whose it is.
    /// </summary>
    public const string TripShared = "trip_shared";

    /// <summary>
    /// Private to the user, but carried across every Adventure. "I'm
    /// vegetarian" — durable, personal, never shared.
    /// </summary>
    public const string GlobalPrivate = "global_private";

    public static bool IsKnown(string visibility)
        => visibility is Private or TripShared or GlobalPrivate;

    /// <summary>
    /// Whether a preference may enter the group's planning profile.
    ///
    /// Exactly one value qualifies. Everything else — including a preference
    /// that would obviously improve the group plan — stays with its owner.
    /// </summary>
    public static bool IsSharedWithGroup(string? visibility)
        => visibility == TripShared;
}

/// <summary>
/// The allow-listed set of things Gluno may remember.
///
/// A closed list, not free-form keys. Two reasons: an open store would drift
/// into holding whatever the model felt like writing down, and this is the
/// only place to state plainly what travel planning is allowed to retain.
///
/// Nothing here is a health record. "Limited mobility" is expressed through
/// <see cref="Accessibility"/> as a planning constraint the user chose to
/// state in this conversation — it is scoped, deletable on request, and never
/// promoted to a global fact about the person.
/// </summary>
public static class GlunoPreferenceKeys
{
    public const string Pace = "pace";
    public const string Budget = "budget";
    public const string Interests = "interests";
    public const string Food = "food";
    public const string Avoid = "avoid";
    public const string Transport = "transport";
    public const string WalkingDistance = "walking_distance";
    public const string StartTime = "start_time";
    public const string Nightlife = "nightlife";
    /// "inspiration" | "booking" — whether they want ideas or to commit.
    public const string Intent = "intent";
    /// Travelling with children, older travellers, limited mobility — as the
    /// user described it, for planning only.
    public const string Accessibility = "accessibility";
    /// Group makeup when it changes recommendations (e.g. "two adults, one
    /// toddler").
    public const string GroupContext = "group_context";

    public static readonly IReadOnlyList<string> All =
    [
        Pace, Budget, Interests, Food, Avoid, Transport,
        WalkingDistance, StartTime, Nightlife, Intent, Accessibility, GroupContext,
    ];

    public static bool IsKnown(string key) => All.Contains(key);
}

/// <summary>
/// One planning preference the user told Gluno.
///
/// Written only when the user actually states something, removed when they say
/// to forget it, and never inferred from behaviour. The point is to stop Gluno
/// asking the same question twice — not to build a profile.
/// </summary>
public class GlunoPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// Set for conversation-scoped preferences.
    public Guid? ConversationId { get; set; }
    /// Set for trip-scoped preferences.
    public Guid? TripId { get; set; }

    /// One of <see cref="GlunoPreferenceKeys"/>.
    public string Key { get; set; } = string.Empty;

    /// The user's own words, normalised only for length. Not an enum: "about
    /// 20 minutes' walk" carries more than any bucket would.
    public string Value { get; set; } = string.Empty;

    /// One of <see cref="GlunoPreferenceScopes"/>.
    public string Scope { get; set; } = GlunoPreferenceScopes.Conversation;

    /// <summary>
    /// One of <see cref="GlunoPreferenceVisibility"/>. Private unless the user
    /// explicitly shared it — see that type for why the default matters.
    /// </summary>
    public string Visibility { get; set; } = GlunoPreferenceVisibility.Private;

    /// <summary>
    /// True when the user stated this as a HARD requirement rather than a
    /// wish.
    ///
    /// Load-bearing in group planning: a soft preference can lose a vote, and a
    /// hard constraint cannot. Somebody who needs short walking distances is
    /// not outvoted by four people who fancy a hike.
    /// </summary>
    public bool IsHardConstraint { get; set; }

    /// <summary>
    /// When the user last confirmed this still holds. Drives whether the group
    /// profile treats it as current or asks again.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
