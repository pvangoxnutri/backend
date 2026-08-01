namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Decides whether a live fact applies to the dates somebody is travelling.
///
/// THE MISTAKE THIS FILE EXISTS TO PREVENT. An article published this morning
/// about last spring's rail strike is fresh, prominent, well-sourced, and
/// completely irrelevant — and any system that sorts or filters by publication
/// date will put it at the top of the answer. The date that matters is when the
/// THING happens, not when someone wrote about it.
///
/// The reverse error is just as real: a government page last edited in 2019
/// describing a visa rule that is still in force is old and still true. Age of
/// the page says nothing about validity of the rule.
///
/// So: effective dates decide, publication is a weak fallback, and anything
/// that cannot be established is <see cref="LiveRecency.Unclear"/> — which is
/// never presented as current.
/// </summary>
public static class GlunoLiveRecency
{
    /// <summary>
    /// How long after publication a dateless news item may still be treated as
    /// describing something current.
    ///
    /// Short on purpose. Without an effective date, publication is all we have,
    /// and a two-week-old report of a disruption is not evidence that the
    /// disruption is happening now.
    /// </summary>
    private static readonly TimeSpan DatelessNewsWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// How long an open-ended fact is assumed to keep running.
    ///
    /// A closure with no stated end date is NOT permanent — museums reopen and
    /// roads get fixed, and nobody publishes a "we are open again" notice. Past
    /// this, the fact drops to unclear rather than quietly continuing to shape
    /// plans forever.
    /// </summary>
    private static readonly TimeSpan OpenEndedAssumption = TimeSpan.FromDays(90);

    /// <summary>
    /// Classifies a fact against the window the user is asking about.
    /// </summary>
    /// <param name="windowStart">First date the traveller cares about.</param>
    /// <param name="windowEnd">Last date, or null for an open-ended Adventure.</param>
    public static LiveRecency Classify(
        LiveTravelFact fact, DateOnly windowStart, DateOnly? windowEnd, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var end = windowEnd ?? windowStart.AddDays(30);

        // ── The source explicitly resolved it ─────────────────────────────
        //
        // "resolved" or "cancelled" from the source itself beats any date
        // arithmetic: the operator saying the strike is over is better
        // evidence than a schedule saying it should still be running.
        if (fact.OfficialStatus is "resolved" or "cancelled") return LiveRecency.Expired;

        // ── Effective dates: the real answer ──────────────────────────────
        if (fact.EffectiveFrom is { } from)
        {
            var until = fact.EffectiveUntil;

            if (until is { } to)
            {
                if (to < today && to < windowStart) return LiveRecency.Expired;

                // Overlaps the traveller's window at all.
                return from <= end && to >= windowStart
                    ? LiveRecency.Current
                    : from > end ? LiveRecency.Upcoming : LiveRecency.Expired;
            }

            // No stated end. Assumed to run for a bounded period — long enough
            // to be useful, short enough that a forgotten notice stops shaping
            // plans a year later.
            var assumedEnd = from.AddDays(OpenEndedAssumption.Days);

            if (assumedEnd < windowStart) return LiveRecency.Unclear;
            if (from > end) return LiveRecency.Upcoming;

            return LiveRecency.Current;
        }

        // ── No effective date at all ──────────────────────────────────────
        //
        // An official page describing a standing rule is still valid however
        // old it is — visa requirements do not expire because the page did.
        if (fact.IsOfficial && fact.Category is LiveTravelCategories.BorderInformation
            or LiveTravelCategories.TemporaryRule or LiveTravelCategories.TravelAdvisory)
        {
            return LiveRecency.Current;
        }

        // Everything else falls back to publication, weakly and briefly.
        if (fact.PublishedAt is { } published && nowUtc - published <= DatelessNewsWindow)
        {
            return LiveRecency.Unclear;
        }

        return LiveRecency.Unclear;
    }

    /// <summary>
    /// Applies the classification and attaches the warnings that follow from it.
    /// </summary>
    public static LiveTravelFact WithRecency(
        LiveTravelFact fact, DateOnly windowStart, DateOnly? windowEnd, DateTime nowUtc)
    {
        var recency = Classify(fact, windowStart, windowEnd, nowUtc);
        var warnings = fact.Warnings.ToList();

        if (fact.EffectiveUntil == null && fact.EffectiveFrom != null && !warnings.Contains("no_end_date"))
            warnings.Add("no_end_date");

        if (recency == LiveRecency.Unclear && !warnings.Contains("date_unclear"))
            warnings.Add("date_unclear");

        if (!fact.IsOfficial && LiveTravelCategories.IsCritical(fact.Category)
            && !warnings.Contains("secondary_source_only"))
        {
            warnings.Add("secondary_source_only");
        }

        return fact with { Recency = recency, Warnings = warnings };
    }

    /// <summary>
    /// Sorts what to show first.
    ///
    /// Official before reported, current before upcoming, critical before
    /// informational. Expired and unclear sink — they are kept because "we
    /// found something but cannot date it" is honest, and dropped from the top
    /// because it is not actionable.
    /// </summary>
    public static IReadOnlyList<LiveTravelFact> Rank(IEnumerable<LiveTravelFact> facts)
        => facts
            .OrderBy(fact => fact.Recency switch
            {
                LiveRecency.Current => 0,
                LiveRecency.Upcoming => 1,
                LiveRecency.Unclear => 2,
                _ => 3,
            })
            .ThenBy(fact => LiveTravelCategories.IsCritical(fact.Category) ? 0 : 1)
            .ThenBy(fact => (int)fact.SourceTier)
            .ThenByDescending(fact => fact.EffectiveFrom ?? DateOnly.MinValue)
            .ThenBy(fact => fact.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Finds sources that disagree.
    ///
    /// Same category, overlapping dates, one official and one not, with
    /// contradictory status. Kept as a conflict rather than resolved — a news
    /// site reporting a cancellation while the operator shows normal service is
    /// exactly the uncertainty a traveller needs to see.
    /// </summary>
    public static IReadOnlyList<LiveTravelConflict> FindConflicts(IReadOnlyList<LiveTravelFact> facts)
    {
        var conflicts = new List<LiveTravelConflict>();

        foreach (var official in facts.Where(fact => fact.IsOfficial))
        {
            foreach (var reported in facts.Where(fact => !fact.IsOfficial))
            {
                if (official.Category != reported.Category) continue;
                if (!Overlaps(official, reported)) continue;
                if (!Contradicts(official.OfficialStatus, reported.OfficialStatus)) continue;

                conflicts.Add(new LiveTravelConflict(official, reported, "official_vs_reported")
                {
                    // The operator's own word about its own service wins the
                    // lead — but the report is kept and shown, not discarded.
                    Preferred = official,
                });
            }
        }

        return conflicts;
    }

    private static bool Overlaps(LiveTravelFact left, LiveTravelFact right)
    {
        if (left.EffectiveFrom is not { } leftFrom || right.EffectiveFrom is not { } rightFrom) return true;

        var leftUntil = left.EffectiveUntil ?? leftFrom.AddDays(7);
        var rightUntil = right.EffectiveUntil ?? rightFrom.AddDays(7);

        return leftFrom <= rightUntil && rightFrom <= leftUntil;
    }

    private static bool Contradicts(string? left, string? right)
    {
        if (left == null || right == null) return false;

        var resolved = new[] { "resolved", "cancelled", "normal" };
        var active = new[] { "active", "planned", "ongoing" };

        return (resolved.Contains(left) && active.Contains(right))
            || (active.Contains(left) && resolved.Contains(right));
    }
}
