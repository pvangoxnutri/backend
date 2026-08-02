using System.Globalization;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Whether a suggested activity is on a day the trip spends somewhere else.
///
/// WHY THIS IS COORDINATES AND NOTHING ELSE. The obvious implementation reads
/// the activity's description and looks for a place name — and that is exactly
/// how Gluno acquires a destination nobody chose. "Dinner somewhere near the
/// old town in Ronda" is prose; the trip may be in Málaga all week and that
/// sentence is a suggestion, not a location. Flagging on it would produce a
/// conflict card about a mismatch that does not exist, and the user would be
/// asked to fix something that was never wrong.
///
/// So this reads two stored coordinate pairs and measures between them. Both
/// sides have to be real:
///
///   - The DAY'S location has to be explicit, from the same resolved timeline
///     the Feed renders. A day that merely carries forward from an earlier one
///     is not a statement about where the trip is that day.
///   - The ACTIVITY has to carry coordinates, which only happens when a real
///     place was attached to it.
///
/// Missing either half means no answer, and no answer means no conflict. That
/// asymmetry is deliberate: a missed mismatch costs a warning nobody saw, and a
/// false one costs the user's trust in every card that follows.
/// </summary>
public static class GlunoDestinationCheck
{
    /// <summary>
    /// How far apart counts as a different place.
    ///
    /// 120 km, which is roughly an hour and a half by road. Well beyond a large
    /// city and its day trips — Málaga to Ronda is about 100 km and is a
    /// perfectly ordinary day out from a Málaga base, so it must NOT flag.
    /// Málaga to Barcelona is 800 km and is a different holiday.
    ///
    /// Set generously on purpose. The cost of a false positive is a card asking
    /// somebody to fix a plan that was right.
    /// </summary>
    public const double MismatchKilometres = 120;

    /// <summary>
    /// The rows that are demonstrably in the wrong town, by index.
    ///
    /// Empty whenever the question cannot be answered from stored data, which
    /// is most of the time and is the correct outcome.
    /// </summary>
    public static IReadOnlyList<int> Mismatched(
        JsonElement payload,
        GlunoTripContext trip)
    {
        var date = GlunoDraftPlan.DateOf(payload);
        if (date == null) return Array.Empty<int>();

        // ── Where the trip actually is that day ───────────────────────────
        //
        // From the destination summary, which comes from the shared timeline
        // resolver — so carry-forward and explicitness behave exactly as the
        // Feed shows them. IsExplicit is the load-bearing part: a day inheriting
        // its location from three days earlier is not evidence of anything.
        var iso = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var stop = trip.Destinations?.Stops.FirstOrDefault(candidate =>
            candidate.IsExplicit
            && string.CompareOrdinal(candidate.From, iso) <= 0
            && string.CompareOrdinal(candidate.To, iso) >= 0);

        if (stop == null) return Array.Empty<int>();

        // The stop carries a label, not coordinates. The stored day-location row
        // behind it does — matched on the date, which is what the timeline was
        // built from.
        var anchor = trip.DayLocations
            .Where(location => location.SortIndex == 0 && location.Date <= date.Value)
            .OrderByDescending(location => location.Date)
            .FirstOrDefault();

        if (anchor == null) return Array.Empty<int>();

        // Only the day the anchor actually names. An anchor from an earlier day
        // is a carry-forward, and this check does not act on inference.
        if (anchor.Date != date.Value) return Array.Empty<int>();

        var mismatched = new List<int>();

        foreach (var row in GlunoDraftPlan.Rows(payload))
        {
            // Already in the plan, or a booking. Neither is this suggestion's
            // doing, so neither is its mistake.
            if (row.IsLocked) continue;

            var distance = GeoDistance.KilometresBetween(
                anchor.Latitude, anchor.Longitude, row.Latitude, row.Longitude);

            // No coordinates on the row: the activity has no place attached, so
            // there is nothing to compare and nothing to claim.
            if (distance is not { } kilometres) continue;

            if (kilometres > MismatchKilometres) mismatched.Add(row.Index);
        }

        return mismatched;
    }
}
