namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Two things the group wants that cannot both happen.
///
/// <see cref="Explanation"/> describes the CLASH, never the people. "The
/// walking limit and the number of stops don't fit in one day" — not "member 2
/// is holding this up". Whose constraint it is is never part of the sentence,
/// because a planner that assigns blame turns a scheduling problem into an
/// argument.
/// </summary>
public sealed record GroupConflict(
    /// Stable machine code — "pace_mismatch", "walking_vs_distance".
    string Type,
    /// The preference keys involved.
    IReadOnlyList<string> AffectedKeys,
    /// "info" | "warning" | "blocking"
    string Severity,
    /// One sentence naming what does not fit. Never who.
    string Explanation)
{
    /// <summary>
    /// True when scheduling alone can resolve it — spreading priorities across
    /// days, moving a stop to the morning. False means the group has to choose.
    /// </summary>
    public bool ResolvableBySchedule { get; init; }

    /// One to three realistic ways forward. Never more: a list of five options
    /// is the decision handed back rather than a recommendation.
    public IReadOnlyList<string> Compromises { get; init; } = Array.Empty<string>();

    /// The group genuinely has to settle this; Gluno cannot.
    public bool RequiresGroupDecision { get; init; }
}

/// <summary>
/// Finds shared constraints that fight each other.
///
/// WHY DETERMINISTIC RATHER THAN LEFT TO THE MODEL. Agreeing is the path of
/// least resistance, and a group plan that quietly satisfies the loudest
/// preference reads perfectly well. Detecting the clash in code means Gluno
/// cannot fail to notice — it only has to decide how to say it.
///
/// EVERY CONFLICT COMES WITH COMPROMISES. Naming a problem without a way
/// forward is not help; it is a complaint with extra steps. The compromises are
/// deliberately concrete ("keep the three that matter most today, move the rest
/// to Thursday") rather than procedural ("discuss it as a group").
///
/// HARD CONSTRAINTS ARE NOT NEGOTIABLE HERE. A conflict between a hard
/// accessibility requirement and a soft wish is not a fifty-fifty trade-off,
/// and none of the compromises below suggest dropping the hard side.
/// </summary>
public static class GroupPreferenceConflictDetector
{
    public static IReadOnlyList<GroupConflict> Detect(TripPlanningProfile profile, string language)
    {
        // A single traveller has no group conflicts by definition.
        if (profile.IsSoloTrip) return Array.Empty<GroupConflict>();

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);
        var conflicts = new List<GroupConflict>();

        DetectPace(profile, swedish, conflicts);
        DetectBudget(profile, swedish, conflicts);
        DetectWalking(profile, swedish, conflicts);
        DetectNightlife(profile, swedish, conflicts);
        DetectTransport(profile, swedish, conflicts);
        DetectFood(profile, swedish, conflicts);
        DetectVetoes(profile, swedish, conflicts);

        return conflicts;
    }

    private static void DetectPace(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var paces = profile.ForKey(Models.GlunoPreferenceKeys.Pace)
            .Select(constraint => TripPaces.Parse(constraint.Value))
            .Distinct()
            .ToList();

        if (!paces.Contains(TripPace.Relaxed) || !paces.Contains(TripPace.Packed)) return;

        conflicts.Add(new GroupConflict(
            "pace_mismatch",
            [Models.GlunoPreferenceKeys.Pace],
            "warning",
            swedish
                ? "Gruppen har både ett lugnt och ett fullspäckat tempo bland önskemålen."
                : "The group has asked for both a relaxed and a packed pace.")
        {
            // Genuinely fixable by scheduling: one quiet day and one full day
            // gives both, where averaging them gives neither.
            ResolvableBySchedule = true,
            Compromises = swedish
                ? [
                    "Lägg en lugn dag och en intensiv dag i stället för att blanda varje dag.",
                    "Behåll ett balanserat tempo med ett valfritt extra stopp för den som vill.",
                  ]
                : [
                    "Make one day quiet and one day full, rather than blending every day.",
                    "Keep a balanced pace with one optional extra stop for whoever wants it.",
                  ],
        });
    }

    private static void DetectBudget(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var budgets = profile.ForKey(Models.GlunoPreferenceKeys.Budget).ToList();
        if (budgets.Count < 2) return;

        var low = budgets.Any(constraint => LooksLow(constraint.Value));
        var high = budgets.Any(constraint => LooksHigh(constraint.Value));

        if (!low || !high) return;

        conflicts.Add(new GroupConflict(
            "budget_mismatch",
            [Models.GlunoPreferenceKeys.Budget],
            "warning",
            swedish
                ? "Budgetönskemålen ligger långt isär inom gruppen."
                : "The group's budget expectations are far apart.")
        {
            ResolvableBySchedule = false,
            RequiresGroupDecision = true,
            Compromises = swedish
                ? [
                    "Håll de flesta måltiderna enkla och lägg en kväll på något dyrare.",
                    "Välj ställen där man kan äta olika dyrt vid samma bord.",
                  ]
                : [
                    "Keep most meals simple and spend on one evening.",
                    "Pick places where people can spend differently at the same table.",
                  ],
        });
    }

    private static void DetectWalking(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var limits = profile.ForKey(Models.GlunoPreferenceKeys.WalkingDistance).ToList();
        if (limits.Count == 0) return;

        // A hard walking limit next to anyone wanting a lot of walking. The
        // hard side is not up for negotiation, so this is about how to satisfy
        // BOTH rather than which to drop.
        var hardLimit = limits.FirstOrDefault(constraint => constraint.IsHard);
        var wantsMore = limits.Any(constraint => !constraint.IsHard && LooksLong(constraint.Value));

        if (hardLimit == null || !wantsMore) return;

        conflicts.Add(new GroupConflict(
            "walking_vs_distance",
            [Models.GlunoPreferenceKeys.WalkingDistance],
            // Blocking: this is exactly the case where a majority must not win.
            "blocking",
            swedish
                ? "Ett av gruppens krav är korta gångsträckor, och delar av planen bygger på längre promenader."
                : "One of the group's requirements is short walking distances, and parts of the plan rely on longer walks.")
        {
            ResolvableBySchedule = true,
            Compromises = swedish
                ? [
                    "Planera dagen tätt och lägg den längre promenaden som ett frivilligt tillägg.",
                    "Använd kollektivtrafik eller taxi mellan de stopp som ligger långt isär.",
                  ]
                : [
                    "Keep the day compact and make the longer walk an optional extra.",
                    "Use transport between the stops that are far apart.",
                  ],
        });
    }

    private static void DetectNightlife(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var wantsNightlife = profile.ForKey(Models.GlunoPreferenceKeys.Nightlife)
            .Any(constraint => !LooksNegative(constraint.Value));

        var wantsEarly = profile.ForKey(Models.GlunoPreferenceKeys.StartTime)
            .Any(constraint => LooksEarly(constraint.Value));

        if (!wantsNightlife || !wantsEarly) return;

        conflicts.Add(new GroupConflict(
            "nightlife_vs_early_start",
            [Models.GlunoPreferenceKeys.Nightlife, Models.GlunoPreferenceKeys.StartTime],
            "info",
            swedish
                ? "Sena kvällar och tidiga morgnar finns båda bland önskemålen."
                : "Late evenings and early starts are both in the group's wishes.")
        {
            ResolvableBySchedule = true,
            Compromises = swedish
                ? [
                    "Lägg de sena kvällarna på dagar utan tidig start dagen efter.",
                    "Gör kvällen frivillig så att den som vill kan gå hem tidigare.",
                  ]
                : [
                    "Put the late evenings on days without an early start after.",
                    "Make the evening optional so anyone can head back earlier.",
                  ],
        });
    }

    private static void DetectTransport(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var transport = profile.ForKey(Models.GlunoPreferenceKeys.Transport).ToList();
        if (transport.Count < 2) return;

        var avoidsCar = transport.Any(constraint =>
            constraint.IsHard && LooksAvoidCar(constraint.Value));
        var assumesCar = transport.Any(constraint => LooksCar(constraint.Value));

        if (!avoidsCar || !assumesCar) return;

        conflicts.Add(new GroupConflict(
            "car_vs_no_car",
            [Models.GlunoPreferenceKeys.Transport],
            "blocking",
            swedish
                ? "Delar av planen förutsätter bil, och gruppen har ett krav på att slippa bil."
                : "Parts of the plan assume a car, and the group has a requirement to avoid one.")
        {
            ResolvableBySchedule = false,
            RequiresGroupDecision = true,
            Compromises = swedish
                ? [
                    "Planera dagarna kring platser som går att nå med kollektivtrafik.",
                    "Lägg de bilberoende stoppen som ett frivilligt alternativ för en del av gruppen.",
                  ]
                : [
                    "Plan the days around places reachable by public transport.",
                    "Make the car-dependent stops an optional extra for part of the group.",
                  ],
        });
    }

    private static void DetectFood(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var food = profile.ForKey(Models.GlunoPreferenceKeys.Food).ToList();
        if (food.Count < 2) return;

        // Distinct hard dietary requirements — not a mismatch to resolve, but a
        // constraint set every restaurant choice has to satisfy at once.
        var hard = food.Where(constraint => constraint.IsHard).ToList();
        if (hard.Count < 2) return;

        var distinct = hard.Select(constraint => GlunoIntentRouter.Normalise(constraint.Value))
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (distinct < 2) return;

        conflicts.Add(new GroupConflict(
            "dietary_requirements",
            [Models.GlunoPreferenceKeys.Food],
            "warning",
            swedish
                ? "Gruppen har flera olika matkrav som alla behöver fungera på samma ställe."
                : "The group has several dietary requirements that all need to work at the same place.")
        {
            ResolvableBySchedule = false,
            Compromises = swedish
                ? [
                    "Välj ställen med bred meny i stället för specialiserade kök.",
                    "Boka bord där menyn går att kontrollera i förväg.",
                  ]
                : [
                    "Pick places with a broad menu rather than a specialised kitchen.",
                    "Choose somewhere whose menu can be checked in advance.",
                  ],
        });
    }

    private static void DetectVetoes(TripPlanningProfile profile, bool swedish, List<GroupConflict> conflicts)
    {
        var vetoes = profile.ForKey(Models.GlunoPreferenceKeys.Avoid)
            .Where(constraint => constraint.IsHard)
            .ToList();

        var interests = profile.ForKey(Models.GlunoPreferenceKeys.Interests).ToList();

        foreach (var veto in vetoes)
        {
            var vetoText = GlunoIntentRouter.Normalise(veto.Value);
            if (vetoText.Length < 3) continue;

            var clashes = interests.Any(interest =>
                GlunoIntentRouter.Normalise(interest.Value).Contains(vetoText, StringComparison.Ordinal));

            if (!clashes) continue;

            conflicts.Add(new GroupConflict(
                "veto_vs_priority",
                [Models.GlunoPreferenceKeys.Avoid, Models.GlunoPreferenceKeys.Interests],
                "blocking",
                swedish
                    ? "Något på gruppens önskelista står på någons lista över saker att undvika."
                    : "Something on the group's wish list is on someone's list of things to avoid.")
            {
                ResolvableBySchedule = true,
                RequiresGroupDecision = true,
                Compromises = swedish
                    ? [
                        "Lägg det som ett frivilligt stopp så att alla inte behöver följa med.",
                        "Byt ut det mot något i samma anda som fungerar för hela gruppen.",
                      ]
                    : [
                        "Make it an optional stop so not everyone has to come along.",
                        "Swap it for something similar that works for the whole group.",
                      ],
            });
        }
    }

    // ── Text readers ─────────────────────────────────────────────────────
    //
    // Preferences are stored in the user's own words, so these read intent
    // rather than parse a vocabulary. Conservative on purpose: a missed
    // conflict is a plan the group has to fix themselves, while a false one is
    // Gluno inventing an argument.

    private static bool LooksLow(string value)
        => Contains(value, "billig", "lag", "spara", "budget", "cheap", "low", "tight", "snal",
            "haller nere", "hall nere", "kostnaderna nere", "keep costs down", "keep it cheap");

    private static bool LooksHigh(string value)
        => Contains(value, "lyx", "exklusiv", "fin", "dyr", "unna", "luxury", "fine dining", "splash", "upscale");

    private static bool LooksLong(string value)
        => Contains(value, "garna ga", "mycket", "langa promenader", "walk a lot", "long walks", "happy to walk");

    private static bool LooksEarly(string value)
        => Contains(value, "tidig", "early", "gryning", "sunrise", "06", "07");

    private static bool LooksNegative(string value)
        => Contains(value, "inte", "ingen", "slipper", "no ", "not ", "avoid");

    private static bool LooksAvoidCar(string value)
        => Contains(value, "inte bil", "ingen bil", "utan bil", "slippa kora", "slippa bil",
            "inte kora", "vill inte kora", "no car", "without a car", "avoid driving", "rather not drive");

    private static bool LooksCar(string value)
        => Contains(value, "hyrbil", "har bil", "kor sjalva", "rental car", "we have a car", "road trip");

    private static bool Contains(string value, params string[] needles)
    {
        var text = GlunoIntentRouter.Normalise(value);
        return needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
    }
}
