namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Where a duration estimate came from. Carried all the way to the proposal
/// card, because the honest sentence differs for each of them.
/// </summary>
public static class DurationSources
{
    /// SideQuest's own table below. An assumption, never a fact.
    public const string CategoryEstimate = "category_estimate";

    /// The place provider told us how long a visit takes. Quotable, WITH
    /// attribution.
    public const string Provider = "provider";

    /// The user typed it, or the Activity already had an end time. Authoritative.
    public const string User = "user";
}

/// <summary>
/// How long things take, by category.
///
/// WHY A TABLE AND NOT THE MODEL. Ask a language model how long a museum takes
/// and it will answer confidently and differently each time — two hours in one
/// turn, ninety minutes in the next, for the same museum. A schedule built on
/// that is not reproducible, cannot be tested, and cannot be explained. These
/// numbers are boring, deterministic and adjustable, and every one of them is
/// pinned by an eval.
///
/// WHAT THEY ARE NOT. They are not facts about any specific place. The Louvre
/// and a one-room village museum are both "attraction". Gluno is required to
/// present these as planning assumptions ("I've set aside about two hours") and
/// never as knowledge ("the museum takes two hours"), and every one of them is
/// editable in proposal review before anything is saved.
///
/// Overridable per deployment via <c>Planning:Durations:&lt;category&gt;</c>.
/// </summary>
public sealed class ActivityDurationTable
{
    private readonly IConfiguration _config;

    public ActivityDurationTable(IConfiguration config) => _config = config;

    /// <summary>
    /// Base minutes per category, at a balanced pace.
    ///
    /// Chosen to be defensible rather than optimal: long enough that the day
    /// does not collapse when one stop runs over, short enough that a traveller
    /// does not feel the plan is padded.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["museum"] = 120,
        ["attraction"] = 90,
        ["sightseeing"] = 60,
        ["landmark"] = 45,
        ["park"] = 75,
        ["beach"] = 150,
        ["hike"] = 210,
        ["viewpoint"] = 30,
        ["shopping"] = 90,
        ["market"] = 60,
        ["gallery"] = 90,
        ["show"] = 150,
        ["tour"] = 150,
        ["spa"] = 120,
        ["sport"] = 120,
        ["nightlife"] = 150,

        // Meals. Separate entries because "lunch" and "dinner" are genuinely
        // different lengths, and a plan that gives dinner forty minutes reads
        // as a plan written by someone who has never had dinner.
        ["breakfast"] = 45,
        ["lunch"] = 60,
        ["dinner"] = 90,
        ["cafe"] = 40,
        ["restaurant"] = 75,
        ["food"] = 75,

        // Logistics.
        ["checkin"] = 30,
        ["checkout"] = 30,
        ["transport"] = 60,
        ["flight"] = 120,

        // The catch-all. Deliberately modest: an unknown stop that turns out to
        // be short costs a gap, one that was assumed short costs the rest of
        // the day.
        ["activity"] = 90,
        ["other"] = 60,
    };

    /// <summary>
    /// Pace multipliers.
    ///
    /// A relaxed traveller does not do fewer things faster — they linger. A
    /// packed day is not the same stops rushed, but it does mean less dwelling.
    /// Bounded tightly: a pace setting must never turn two hours into twenty
    /// minutes.
    /// </summary>
    private static double PaceFactor(string? pace) => pace?.Trim().ToLowerInvariant() switch
    {
        "relaxed" or "lugnt" or "slow" => 1.25,
        "packed" or "intensivt" or "fast" => 0.8,
        _ => 1.0,
    };

    /// <summary>
    /// Minutes to set aside for one stop.
    /// </summary>
    /// <param name="category">Activity category, custom label, or free text.</param>
    /// <param name="pace">The trip's pace, when known.</param>
    /// <param name="providerMinutes">
    /// A duration the place provider supplied. Wins over the table — it is
    /// about this specific place rather than its category — and changes the
    /// source so the proposal can attribute it.
    /// </param>
    public (int Minutes, string Source) Estimate(string? category, string? pace, int? providerMinutes = null)
    {
        if (providerMinutes is > 0 and <= 24 * 60)
        {
            return (RoundToQuarterHour(providerMinutes.Value), DurationSources.Provider);
        }

        var key = Normalise(category);
        var baseMinutes = _config.GetValue<int?>($"Planning:Durations:{key}")
            ?? (Defaults.TryGetValue(key, out var configured) ? configured : Defaults["activity"]);

        var adjusted = baseMinutes * PaceFactor(pace);

        // Floor at 15 minutes: below that a stop is not a stop, and a schedule
        // full of ten-minute blocks is a schedule nobody can follow.
        return (RoundToQuarterHour((int)Math.Round(Math.Clamp(adjusted, 15, 12 * 60))), DurationSources.CategoryEstimate);
    }

    /// <summary>
    /// Slack after a stop before the next one begins.
    ///
    /// Real days contain paying the bill, finding the exit, a queue, a photo.
    /// Zero-buffer plans are the single most common way a generated itinerary
    /// turns out to be impossible in practice, and the failure compounds — by
    /// the fourth stop the plan is an hour out.
    /// </summary>
    public int BufferMinutes(string? pace) => pace?.Trim().ToLowerInvariant() switch
    {
        "relaxed" or "lugnt" => Math.Clamp(_config.GetValue("Planning:BufferMinutes:relaxed", 30), 0, 120),
        "packed" or "intensivt" => Math.Clamp(_config.GetValue("Planning:BufferMinutes:packed", 10), 0, 120),
        _ => Math.Clamp(_config.GetValue("Planning:BufferMinutes:balanced", 20), 0, 120),
    };

    /// <summary>
    /// A rough travel time when nothing verified it.
    ///
    /// Used ONLY to lay a timeline out so the day has a shape — never quoted,
    /// never saved, and always paired with Verified=false so the proposal and
    /// the prompt both call it what it is. The speeds are deliberately
    /// pessimistic and the straight-line distance is inflated by a detour
    /// factor, because real routes are not straight and a plan that is too
    /// generous is merely loose while one that is too tight is wrong.
    /// </summary>
    public static int? EstimateTravelMinutes(double? straightLineKm, TravelMode mode)
    {
        if (straightLineKm is not { } kilometres || kilometres < 0) return null;

        // Streets, rivers and one-way systems: the walked distance is reliably
        // longer than the crow's flight.
        const double DetourFactor = 1.35;

        var kilometresPerHour = mode switch
        {
            TravelMode.Driving => 28.0,   // urban driving, including parking
            TravelMode.Transit => 16.0,   // including waiting and access walks
            TravelMode.Cycling => 13.0,
            _ => 4.5,                     // walking
        };

        var minutes = kilometres * DetourFactor / kilometresPerHour * 60;

        // Anything under a couple of minutes is noise; call it 5 so the
        // timeline still reflects that moving between places takes time.
        return Math.Max(5, (int)Math.Ceiling(minutes / 5.0) * 5);
    }

    /// Quarter hours read as a plan; 83 minutes reads as a machine.
    private static int RoundToQuarterHour(int minutes) => Math.Max(15, (int)Math.Round(minutes / 15.0) * 15);

    private static string Normalise(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "activity";

        var value = category.Trim().ToLowerInvariant();

        return value switch
        {
            "sightseeing" or "sights" or "sevärdhet" or "sevardhet" => "sightseeing",
            "mat" or "meal" => "food",
            "frukost" => "breakfast",
            "middag" => "dinner",
            "resa" or "travel" or "transfer" => "transport",
            "boende" or "stay" or "hotel" or "accommodation" => "checkin",
            "shopping" or "handla" => "shopping",
            "natur" or "nature" or "outdoor" => "park",
            "vandring" => "hike",
            "strand" => "beach",
            "museum" or "muséum" => "museum",
            _ => value,
        };
    }
}
