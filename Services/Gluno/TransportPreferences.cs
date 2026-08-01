using System.Globalization;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How this group actually gets around, derived from what they told Gluno.
///
/// WHY DERIVED RATHER THAN STORED AS FLAGS. The preference store keeps the
/// user's own words ("vi har hyrbil men vill helst slippa köra i stan"). Those
/// words are what gets shown back to them, what they can ask to be forgotten,
/// and what stays scoped to a conversation or a trip. Turning them into booleans
/// at read time means there is exactly one copy of the truth, and forgetting the
/// preference forgets the constraint with it — no orphan flag surviving in a
/// column somewhere.
///
/// WHAT IS NOT HERE. Nothing about anyone's health. <see cref="HasAccessibilityNeed"/>
/// is a planning constraint the user chose to state — it changes the default
/// walking limit and the primary mode, it is conversation- or trip-scoped, and
/// it is never promoted to a global fact about a person.
///
/// THE ASSUMPTION THIS FILE EXISTS TO PREVENT. Distance is not evidence of a
/// car. A stop 30 km out means the plan needs a way to get there — it does not
/// mean the group has a rental, and quietly assuming one produces a day that is
/// impossible for someone on trains. <see cref="CarAvailable"/> is true only
/// when somebody said so.
/// </summary>
public sealed class TransportPreferences
{
    /// What to route with when nothing says otherwise.
    public TravelMode PrimaryMode { get; init; } = TravelMode.Walking;

    /// True only if the user said they have one.
    public bool CarAvailable { get; init; }

    public bool AvoidCar { get; init; }
    public bool AvoidTransit { get; init; }
    public bool AcceptsTaxi { get; init; }
    public bool HasAccessibilityNeed { get; init; }

    /// <summary>
    /// The furthest they will walk between two stops, in km. Null means no
    /// stated limit — which is NOT the same as unlimited, and the planner
    /// treats it as "use the default and do not claim they agreed to it".
    /// </summary>
    public double? MaxWalkKm { get; init; }

    public int? MaxWalkMinutes { get; init; }

    /// True when this came from something the user actually said, rather than
    /// from defaults. Drives whether Gluno should ask — asking twice about the
    /// car is one of the fastest ways to feel like a form rather than an expert.
    public bool IsStated { get; init; }

    /// <summary>
    /// Default walking tolerance, used when the user has not said.
    ///
    /// 1.5 km is roughly a twenty-minute walk: far enough that a city day works
    /// on foot, short enough that nobody is surprised.
    /// </summary>
    public const double DefaultMaxWalkKm = 1.5;

    public double EffectiveMaxWalkKm => MaxWalkKm ?? (HasAccessibilityNeed ? 0.7 : DefaultMaxWalkKm);

    /// <summary>
    /// The mode to use for one specific leg.
    ///
    /// Walking is the default within a city, but a leg past the group's walking
    /// tolerance has to become something else — and which something depends on
    /// what they told us, not on what would be convenient.
    /// </summary>
    /// <summary>
    /// Short enough that nobody gets in a car for it.
    ///
    /// Having a rental does not mean driving two blocks — by the time you have
    /// found the car and somewhere to park you would have arrived. A planner
    /// that routes every leg by the primary mode produces days that read as
    /// written by someone who has never been to a city.
    /// </summary>
    private const double AlwaysWalkableKm = 0.8;

    public TravelMode ModeForLeg(double? straightLineKm)
    {
        if (straightLineKm is not { } kilometres) return PrimaryMode;

        // Someone who told us they cannot walk far is the exception: for them a
        // short leg is not automatically a walk.
        if (!HasAccessibilityNeed && kilometres <= Math.Min(AlwaysWalkableKm, EffectiveMaxWalkKm))
            return TravelMode.Walking;

        if (PrimaryMode != TravelMode.Walking) return PrimaryMode;
        if (kilometres <= EffectiveMaxWalkKm) return TravelMode.Walking;

        // Past walking range. Prefer what they have, then what they accept.
        if (CarAvailable && !AvoidCar) return TravelMode.Driving;
        if (!AvoidTransit) return TravelMode.Transit;
        if (AcceptsTaxi) return TravelMode.Driving;

        // They rejected everything that would cover this distance. Route it as
        // a walk so the plan shows honestly how long it would take, and let the
        // schedule warnings say the leg is beyond their stated limit.
        return TravelMode.Walking;
    }

    /// <summary>
    /// Reads the structured constraints out of the stored preference text.
    /// </summary>
    /// <param name="transport">The <c>transport</c> preference, verbatim.</param>
    /// <param name="walkingDistance">The <c>walking_distance</c> preference, verbatim.</param>
    /// <param name="accessibility">The <c>accessibility</c> preference, verbatim.</param>
    public static TransportPreferences From(string? transport, string? walkingDistance, string? accessibility)
    {
        var stated = !string.IsNullOrWhiteSpace(transport)
            || !string.IsNullOrWhiteSpace(walkingDistance)
            || !string.IsNullOrWhiteSpace(accessibility);

        var text = Normalise(string.Join(' ', new[] { transport, walkingDistance, accessibility }
            .Where(part => !string.IsNullOrWhiteSpace(part))));

        var accessibilityNeed = !string.IsNullOrWhiteSpace(accessibility)
            || ContainsAny(text, "rullstol", "wheelchair", "begransad rorlighet", "limited mobility",
                "svart att ga", "hard to walk", "barnvagn", "stroller", "kryckor", "crutches");

        // "Inte bil", "ingen bil", "slippa köra", "don't want to drive".
        var avoidCar = ContainsAny(text, "inte bil", "ingen bil", "utan bil", "slippa kora", "slippa bil",
            "vill inte kora", "no car", "without a car", "dont want to drive", "don t want to drive",
            "rather not drive", "avoid driving", "inte hyrbil");

        var avoidTransit = ContainsAny(text, "inte kollektiv", "ingen kollektiv", "undvik kollektiv",
            "inte buss", "inte tunnelbana", "no public transport", "avoid public transport", "no metro", "no bus");

        // Explicit possession only. "Vi hyr bil", "we have a rental car",
        // "kommer med bil" — never inferred from a distant stop.
        var carAvailable = !avoidCar && ContainsAny(text, "har bil", "hyrbil", "hyr bil", "hyra bil",
            "egen bil", "kommer med bil", "tar bilen", "kor sjalva", "rental car", "hire car", "we have a car",
            "have a car", "driving there", "road trip", "bilsemester");

        var acceptsTaxi = ContainsAny(text, "taxi", "uber", "bolt", "taxibil", "cab");

        var primaryMode = ResolvePrimaryMode(text, carAvailable, avoidCar, avoidTransit, accessibilityNeed);

        var (maxWalkKm, maxWalkMinutes) = ReadWalkingLimit(Normalise(walkingDistance) + " " + text);

        return new TransportPreferences
        {
            PrimaryMode = primaryMode,
            CarAvailable = carAvailable,
            AvoidCar = avoidCar,
            AvoidTransit = avoidTransit,
            AcceptsTaxi = acceptsTaxi,
            HasAccessibilityNeed = accessibilityNeed,
            MaxWalkKm = maxWalkKm,
            MaxWalkMinutes = maxWalkMinutes,
            IsStated = stated,
        };
    }

    private static TravelMode ResolvePrimaryMode(
        string text, bool carAvailable, bool avoidCar, bool avoidTransit, bool accessibilityNeed)
    {
        // An explicit statement always wins over anything inferred.
        if (ContainsAny(text, "cykel", "cyklar", "bike", "bicycle", "cycling")) return TravelMode.Cycling;
        if (ContainsAny(text, "helst till fots", "gar garna", "walk everywhere", "on foot", "prefer walking"))
            return TravelMode.Walking;
        if (!avoidTransit && ContainsAny(text, "kollektivt", "kollektivtrafik", "tunnelbana", "metro",
                "public transport", "buss", "sparvagn", "tram", "tag"))
        {
            return TravelMode.Transit;
        }

        if (carAvailable && !avoidCar) return TravelMode.Driving;

        // Someone who has said they cannot walk far is not primarily walking,
        // even without a stated mode.
        if (accessibilityNeed && !avoidTransit) return TravelMode.Transit;

        return TravelMode.Walking;
    }

    /// <summary>
    /// Pulls a number out of "max 2 km", "inte mer än 20 minuters promenad",
    /// "we'll walk up to about a mile".
    ///
    /// Only patterns with an explicit unit are read. A bare number in free text
    /// is far more likely to be a party size or a budget than a walking limit.
    /// </summary>
    private static (double? Km, int? Minutes) ReadWalkingLimit(string text)
    {
        double? kilometres = null;
        int? minutes = null;

        var kmMatch = Regex.Match(text, @"(\d+(?:[.,]\d+)?)\s*(km|kilometer|kilometre|kilometres|kilometers)\b");
        if (kmMatch.Success && double.TryParse(
                kmMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var km))
        {
            kilometres = Math.Clamp(km, 0.1, 30);
        }

        var metreMatch = Regex.Match(text, @"(\d{3,5})\s*(m|meter|metres|meters)\b");
        if (kilometres == null && metreMatch.Success && double.TryParse(
                metreMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres))
        {
            kilometres = Math.Clamp(metres / 1000.0, 0.1, 30);
        }

        var minuteMatch = Regex.Match(text, @"(\d{1,3})\s*(min|minut|minuter|minutes|minute)\b");
        if (minuteMatch.Success && int.TryParse(minuteMatch.Groups[1].Value, out var parsedMinutes))
        {
            minutes = Math.Clamp(parsedMinutes, 5, 240);

            // A stated time is a stated distance too, at ordinary walking pace.
            // Better than leaving the planner with a limit it cannot compare
            // against a straight-line kilometre count.
            kilometres ??= Math.Clamp(minutes.Value / 60.0 * 4.5, 0.1, 30);
        }

        return (kilometres, minutes);
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    /// Lowercase, accent-folded, single-spaced — so "kör" matches "kor" and
    /// "Hyrbil." matches "hyrbil".
    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or ',' ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
