namespace sidequest.backend.Services.Gluno;

/// <summary>One candidate the group might do.</summary>
public sealed record GroupCandidate(string Id, string Label)
{
    /// Neutral member refs who named this as something they want.
    public IReadOnlyList<string> WantedBy { get; init; } = Array.Empty<string>();

    /// Refs who said they do not want it. A hard veto here is decisive.
    public IReadOnlyList<string> VetoedBy { get; init; } = Array.Empty<string>();

    /// True when at least one of the vetoes was stated as a hard requirement.
    public bool HasHardVeto { get; init; }

    /// True when it breaks a hard constraint — a walking limit, no car.
    public bool BreaksHardConstraint { get; init; }

    /// Refs for whom this is a stated "must".
    public IReadOnlyList<string> MustFor { get; init; } = Array.Empty<string>();
}

public sealed record GroupRankedCandidate(
    GroupCandidate Candidate,
    /// Internal only. Never rendered, never explained numerically.
    double Score,
    /// Machine codes: "hard_veto", "breaks_hard_constraint", "already_favoured".
    IReadOnlyList<string> Signals)
{
    /// Excluded outright rather than merely ranked low.
    public bool IsExcluded { get; init; }
}

/// <summary>
/// Ranks candidates for a group without letting the majority steamroll anyone.
///
/// THE PROBLEM WITH SIMPLE VOTING. Four people want the hike, one cannot manage
/// the distance — count the votes and the hike wins every time, on every day,
/// and one person spends the holiday sitting out. Majority preference is a
/// reasonable input and a terrible rule.
///
/// SO THE ORDER IS: hard constraints, then vetoes, then breadth of interest,
/// then how much each person has already had. The last one is what stops the
/// same member's favourites winning every single day — someone whose choices
/// have already been honoured twice is scored slightly lower on the third, not
/// because their preference matters less but because a week where one person
/// gets everything is not a group trip.
///
/// THIS IS NOT AN OBJECTIVELY FAIR ALGORITHM and must never be described as
/// one. It is a defensible compromise, and Gluno is required to call it that.
/// The numbers below are a heuristic, they are never shown to anyone, and the
/// explanation the user reads is prose about the trade-off rather than a score.
/// </summary>
public static class GlunoGroupFairness
{
    /// <summary>
    /// How much each already-satisfied priority reduces a member's weight on
    /// the next one.
    ///
    /// Gentle on purpose. Strong enough that the fourth pick spreads to someone
    /// new, weak enough that a person with genuinely strong preferences is not
    /// punished for having them.
    /// </summary>
    private const double SatisfactionDecay = 0.35;

    /// A stated "must" outweighs a casual "sounds nice" — but not a hard
    /// constraint, and not a veto.
    private const double MustWeight = 2.5;

    public static IReadOnlyList<GroupRankedCandidate> Rank(
        IReadOnlyList<GroupCandidate> candidates,
        IReadOnlyDictionary<string, int> alreadySatisfiedByMember)
    {
        var ranked = new List<GroupRankedCandidate>();

        foreach (var candidate in candidates)
        {
            var signals = new List<string>();

            // ── Exclusions come first, and are absolute ───────────────────
            //
            // A hard veto or a broken hard constraint is not a low score, it is
            // a no. Scoring them low would let enough enthusiasm outweigh
            // somebody's mobility requirement, which is exactly the failure
            // this whole file exists to prevent.
            if (candidate.HasHardVeto)
            {
                ranked.Add(new GroupRankedCandidate(candidate, double.MinValue, ["hard_veto"])
                {
                    IsExcluded = true,
                });
                continue;
            }

            if (candidate.BreaksHardConstraint)
            {
                ranked.Add(new GroupRankedCandidate(
                    candidate, double.MinValue, ["breaks_hard_constraint"])
                {
                    IsExcluded = true,
                });
                continue;
            }

            double score = 0;

            foreach (var member in candidate.WantedBy.Distinct(StringComparer.Ordinal))
            {
                var isMust = candidate.MustFor.Contains(member, StringComparer.Ordinal);
                var weight = isMust ? MustWeight : 1.0;

                // Somebody who has already had several priorities honoured
                // counts a little less on this one. Not because their view
                // matters less — because a week where one person gets
                // everything is not a group trip.
                var alreadyHad = alreadySatisfiedByMember.GetValueOrDefault(member);
                var decayed = weight / (1 + SatisfactionDecay * alreadyHad);

                score += decayed;

                if (alreadyHad >= 2 && !signals.Contains("already_favoured")) signals.Add("already_favoured");
            }

            // A soft objection is a real cost, and smaller than a want. One
            // person mildly against does not cancel one person keen.
            score -= candidate.VetoedBy.Count * 0.6;

            if (candidate.MustFor.Count > 0) signals.Add("is_a_must_for_someone");
            if (candidate.WantedBy.Count >= 3) signals.Add("broadly_wanted");

            ranked.Add(new GroupRankedCandidate(candidate, Math.Round(score, 3), signals));
        }

        return ranked
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Candidate.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Spreads individual priorities across the available days.
    ///
    /// Round-robin over MEMBERS rather than greedy over scores: taking the
    /// highest-scoring candidate for every day tends to give one person their
    /// whole list before anyone else gets a look in, because whoever has the
    /// strongest preferences also has the highest scores.
    /// </summary>
    public static IReadOnlyDictionary<string, List<GroupCandidate>> SpreadAcrossDays(
        IReadOnlyList<GroupRankedCandidate> ranked,
        IReadOnlyList<string> days,
        int perDay)
    {
        var plan = days.ToDictionary(day => day, _ => new List<GroupCandidate>(), StringComparer.Ordinal);
        if (days.Count == 0) return plan;

        var satisfied = new Dictionary<string, int>(StringComparer.Ordinal);
        var remaining = ranked.Where(entry => !entry.IsExcluded).ToList();

        for (var round = 0; round < perDay; round++)
        {
            foreach (var day in days)
            {
                if (remaining.Count == 0) return plan;

                // Re-rank each time so the decay from the previous pick is
                // reflected — this is what makes the spread actually spread.
                var next = Rank(remaining.Select(entry => entry.Candidate).ToList(), satisfied)
                    .FirstOrDefault(entry => !entry.IsExcluded);

                if (next == null) return plan;

                plan[day].Add(next.Candidate);
                remaining.RemoveAll(entry => entry.Candidate.Id == next.Candidate.Id);

                foreach (var member in next.Candidate.WantedBy.Distinct(StringComparer.Ordinal))
                {
                    satisfied[member] = satisfied.GetValueOrDefault(member) + 1;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// One sentence about the trade-off, for the user.
    ///
    /// Prose about WHAT the plan protects, never a score and never a name.
    /// "This keeps two of the group's shared favourites and adds a quieter stop
    /// in the afternoon" is the whole genre.
    /// </summary>
    public static string ExplainCompromise(
        IReadOnlyList<GroupRankedCandidate> ranked, int keptCount, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var protectedHard = ranked.Any(entry => entry.IsExcluded);
        var broadlyWanted = ranked.Count(entry => entry.Signals.Contains("broadly_wanted"));

        if (protectedHard)
        {
            return swedish
                ? $"Planen håller sig inom gruppens krav och behåller {keptCount} av de gemensamma prioriteringarna."
                : $"The plan stays within the group's requirements and keeps {keptCount} of the shared priorities.";
        }

        if (broadlyWanted > 0)
        {
            return swedish
                ? $"Planen behåller {broadlyWanted} av gruppens gemensamma favoriter och sprider resten över dagarna."
                : $"The plan keeps {broadlyWanted} of the group's shared favourites and spreads the rest across the days.";
        }

        return swedish
            ? "Det här är en kompromiss — den tar med lite av varje önskemål snarare än allt av ett."
            : "This is a compromise — a little of each wish rather than all of one.";
    }
}
