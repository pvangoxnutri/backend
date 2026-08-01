namespace sidequest.backend.Services.Gluno;

/// <summary>
/// SideQuest's own ordering of external place results.
///
/// WHY THIS EXISTS. A provider's raw rating is the wrong sort key on its own:
/// a 5.0 from two reviews outranks a 4.8 from four thousand, which is exactly
/// backwards for someone deciding where to eat tonight. Sorting purely by
/// rating also ignores everything SideQuest knows that the provider does not —
/// where the traveller actually is that day, what they asked for, what they
/// said about budget.
///
/// WHAT IT IS NOT. This ranking is SideQuest's, not the provider's. It is
/// never presented as an official provider ranking, and Gluno is instructed
/// (see GlunoSystemPrompt) never to claim the provider recommends this order.
/// <see cref="RankedTravelPlace.Signals"/> exists so an explanation can be
/// grounded in the actual reasons rather than invented after the fact.
///
/// The confidence problem is handled with Bayesian shrinkage rather than a
/// minimum-review cutoff: a place with few reviews is pulled toward the mean
/// instead of being thrown away, so a genuinely good new restaurant can still
/// surface — it just has to beat the average on other signals too.
///
/// A missing optional field NEVER removes a result. Price level, photos,
/// opening hours and review text are all absent from plenty of legitimate
/// listings; treating absence as disqualifying would quietly hide half the
/// map. Missing values score neutrally.
/// </summary>
public static class TravelPlaceRanker
{
    /// Prior strength, in reviews. A place needs roughly this many reviews
    /// before its own average dominates the prior. 50 is deliberately modest:
    /// enough to defuse the two-review 5.0, not so much that a well-liked
    /// neighbourhood place needs thousands to compete.
    private const double ReviewPriorWeight = 50;

    /// The rating a place is assumed to have before its reviews are counted.
    /// Set near the middle of the realistic band (most listings sit 3.5–4.5),
    /// not at the scale's midpoint, which would over-punish everything.
    private const double PriorRatingFraction = 0.78;

    /// Distance at which the proximity score has decayed to half. About a
    /// fifteen-minute walk — the point where "nearby" stops being true.
    private const double HalfDistanceKm = 1.2;

    private const double RatingWeight = 0.45;
    private const double DistanceWeight = 0.25;
    private const double CategoryWeight = 0.10;
    private const double BudgetWeight = 0.10;
    private const double InterestWeight = 0.10;

    public static IReadOnlyList<RankedTravelPlace> Rank(
        IReadOnlyList<TravelPlace> places, TravelPlaceQuery query)
    {
        var ranked = new List<RankedTravelPlace>(places.Count);

        foreach (var place in places)
        {
            var signals = new List<string>();

            var rating = ScoreRating(place, signals);
            var distance = ScoreDistance(place, signals);
            var category = ScoreCategory(place, query, signals);
            var budget = ScoreBudget(place, query, signals);
            var interest = ScoreInterests(place, query, signals);

            var score =
                rating * RatingWeight +
                distance * DistanceWeight +
                category * CategoryWeight +
                budget * BudgetWeight +
                interest * InterestWeight;

            ranked.Add(new RankedTravelPlace
            {
                Place = place,
                Score = Math.Round(score, 4),
                Signals = signals,
            });
        }

        return ranked
            .OrderByDescending(r => r.Score)
            // Deterministic tiebreak, so the same query never returns two
            // different orders and an explanation stays reproducible.
            .ThenByDescending(r => r.Place.ReviewCount ?? 0)
            .ThenBy(r => r.Place.ExternalId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Rating, shrunk toward a prior by review volume.
    ///
    ///     adjusted = (v·R + m·C) / (v + m)
    ///
    /// where R is the place's rating, v its review count, m the prior weight
    /// and C the prior rating. With v ≫ m this is just R; with v ≈ 0 it is
    /// just C. That is the whole 5.0-with-two-reviews fix, in one line.
    /// </summary>
    private static double ScoreRating(TravelPlace place, List<string> signals)
    {
        var scaleMax = place.RatingScaleMax is > 0 ? place.RatingScaleMax.Value : 5;
        var prior = scaleMax * PriorRatingFraction;

        if (place.Rating is not { } rating)
        {
            // No rating at all is neutral, not disqualifying.
            signals.Add("no_rating");
            return Normalise(prior, scaleMax);
        }

        var reviews = Math.Max(0, place.ReviewCount ?? 0);
        var adjusted = ((rating * reviews) + (prior * ReviewPriorWeight)) / (reviews + ReviewPriorWeight);

        if (rating >= scaleMax * 0.9 && reviews >= 200) signals.Add("highly_rated");
        else if (rating >= scaleMax * 0.9) signals.Add("high_rating_few_reviews");

        if (reviews >= 1000) signals.Add("very_many_reviews");
        else if (reviews >= 200) signals.Add("many_reviews");
        else if (reviews > 0 && reviews < 25) signals.Add("few_reviews");

        return Normalise(adjusted, scaleMax);
    }

    /// A ratio, so 1 km is half as good as being on the doorstep rather than
    /// falling off a cliff at some arbitrary radius.
    private static double ScoreDistance(TravelPlace place, List<string> signals)
    {
        if (place.DistanceKm is not { } distance)
        {
            // Unknown distance must not beat a known-close place, nor lose to
            // a known-far one.
            return 0.5;
        }

        if (distance <= 0.4) signals.Add("very_close");
        else if (distance <= 1.5) signals.Add("walkable");
        else if (distance >= 8) signals.Add("far");

        return 1.0 / (1.0 + (Math.Max(0, distance) / HalfDistanceKm));
    }

    private static double ScoreCategory(TravelPlace place, TravelPlaceQuery query, List<string> signals)
    {
        if (query.Category == TravelPlaceCategory.General) return 0.5;

        var wanted = TravelPlaceCategories.ToWireValue(query.Category);
        if (!string.Equals(place.Category, wanted, StringComparison.OrdinalIgnoreCase)) return 0.25;

        signals.Add("matches_category");
        return 1.0;
    }

    private static double ScoreBudget(TravelPlace place, TravelPlaceQuery query, List<string> signals)
    {
        if (string.IsNullOrWhiteSpace(query.PriceLevel)) return 0.5;
        if (string.IsNullOrWhiteSpace(place.PriceLevel))
        {
            // The provider simply doesn't publish a band for this listing.
            // Neutral — not a reason to hide it.
            return 0.5;
        }

        // Price bands are provider-specific strings ("$$ - $$$"), so this is a
        // containment check, not arithmetic: comparing them numerically across
        // providers would be inventing a scale that doesn't exist.
        var wanted = query.PriceLevel.Trim();
        var actual = place.PriceLevel.Trim();

        if (actual.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.Contains(actual, StringComparison.OrdinalIgnoreCase))
        {
            signals.Add("matches_budget");
            return 1.0;
        }

        return 0.3;
    }

    private static double ScoreInterests(TravelPlace place, TravelPlaceQuery query, List<string> signals)
    {
        if (query.Interests.Count == 0 && string.IsNullOrWhiteSpace(query.Query)) return 0.5;

        var haystack = string.Join(
            ' ',
            new[] { place.Name, place.CategoryLabel, place.ReviewSummary, place.Address }
                .Where(part => !string.IsNullOrWhiteSpace(part)))
            .ToLowerInvariant();

        var terms = query.Interests
            .Concat(query.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(term => term.ToLowerInvariant())
            // Two-character tokens match everything and mean nothing.
            .Where(term => term.Length > 2)
            .Distinct()
            .ToList();

        if (terms.Count == 0) return 0.5;

        var hits = terms.Count(term => haystack.Contains(term, StringComparison.Ordinal));
        if (hits > 0) signals.Add("matches_request");

        return Math.Clamp((double)hits / terms.Count, 0, 1);
    }

    /// Maps a rating onto 0–1 across the usable part of its scale. Ratings
    /// start at 1 on a 5-point scale, so 1 must map to 0, not 0.2.
    private static double Normalise(double rating, double scaleMax)
        => Math.Clamp((rating - 1) / Math.Max(0.0001, scaleMax - 1), 0, 1);
}

/// <summary>
/// Great-circle distance. Straight-line, deliberately: SideQuest has no
/// routing data, and presenting a walking time it cannot compute would be the
/// same kind of invention the whole provider layer avoids.
/// </summary>
public static class GeoDistance
{
    private const double EarthRadiusKm = 6371.0;

    public static double? KilometresBetween(
        double? fromLatitude, double? fromLongitude, double? toLatitude, double? toLongitude)
    {
        if (fromLatitude is not { } lat1 || fromLongitude is not { } lon1) return null;
        if (toLatitude is not { } lat2 || toLongitude is not { } lon2) return null;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
              + (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        return Math.Round(EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)), 2);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
