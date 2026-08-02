namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Which piece of data an uncertainty is about.
///
/// The whole point of the type: an uncertainty belongs to a FIELD. A rating
/// that could not be fetched says nothing about opening hours, and a caution
/// about the wrong field is worse than none — it sends somebody to check
/// something that was never in doubt while leaving the real gap unmentioned.
/// </summary>
public enum GlunoDataField
{
    Rating,
    OpeningHours,
    Price,
    TravelTime,
    Weather,
}

/// <summary>
/// How much a field can be trusted.
/// </summary>
public enum GlunoFieldStatus
{
    /// Nobody asked for it. NEVER mentioned to the user — "I didn't look up
    /// the price" is noise on an answer that was not about price.
    NotRequested,
    /// Fetched and current.
    Verified,
    /// Fetched, but old enough that it may have moved.
    Stale,
    /// The lookup did not produce it.
    Unavailable,
}

/// <summary>
/// Builds the one short caution sentence an answer is allowed to carry.
///
/// THE BUG THIS EXISTS FOR. Gluno told somebody: "Betygen kan jag inte
/// kontrollera just nu, så kolla öppettiderna innan ni går." Two clauses about
/// two different fields, and the second does not follow from the first at all.
///
/// It was not one sentence. The model wrote about ratings, and the backend
/// separately appended a note about opening hours — chosen by a first-match
/// chain that fired whenever any opening-hours entry was stale, regardless of
/// what the answer had been about. Two correct halves, concatenated into
/// something false.
///
/// So the note is now built from the actual per-field statuses, names only the
/// fields that are genuinely uncertain, and never advises checking a field that
/// was verified.
/// </summary>
public static class GlunoFieldUncertainty
{
    /// <summary>
    /// The caution for a set of field statuses, or null when there is nothing
    /// honest to say.
    ///
    /// Only Stale and Unavailable produce text. Verified needs no caution, and
    /// NotRequested is not a gap — it is a question nobody asked.
    /// </summary>
    public static string? Note(
        IReadOnlyDictionary<GlunoDataField, GlunoFieldStatus> statuses, string language)
    {
        var uncertain = statuses
            .Where(entry => entry.Value is GlunoFieldStatus.Stale or GlunoFieldStatus.Unavailable)
            // Fixed order, so the same two fields always read the same way
            // rather than depending on dictionary iteration.
            .Select(entry => entry.Key)
            .OrderBy(field => (int)field)
            .ToList();

        if (uncertain.Count == 0) return null;

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var names = uncertain.Select(field => Name(field, swedish)).ToList();
        var joined = Join(names, swedish);

        // ── One clause, and only about what is actually uncertain ─────────
        //
        // The "check before you go" advice belongs to opening hours alone. On
        // a rating it would be advice about the wrong thing; on a price it
        // would imply the place might be shut.
        var advise = uncertain.Contains(GlunoDataField.OpeningHours);

        if (swedish)
        {
            return advise
                ? $"Jag kan inte bekräfta {joined} just nu, så kontrollera innan ni går."
                : $"Jag kan inte bekräfta {joined} just nu.";
        }

        return advise
            ? $"I can't confirm {joined} just now, so check before you go."
            : $"I can't confirm {joined} just now.";
    }

    private static string Name(GlunoDataField field, bool swedish) => field switch
    {
        GlunoDataField.Rating => swedish ? "aktuella betyg" : "current ratings",
        GlunoDataField.OpeningHours => swedish ? "dagens öppettider" : "today's opening hours",
        GlunoDataField.Price => swedish ? "aktuellt pris" : "current prices",
        GlunoDataField.TravelTime => swedish ? "restiderna" : "travel times",
        GlunoDataField.Weather => swedish ? "prognosen" : "the forecast",
        _ => swedish ? "uppgifterna" : "the details",
    };

    /// "a, b or c" — with the last joined by "or" rather than "and", because
    /// these are things that MIGHT be wrong, not a list of things that are.
    private static string Join(IReadOnlyList<string> names, bool swedish)
    {
        if (names.Count == 1) return names[0];

        var last = names[^1];
        var rest = string.Join(", ", names.Take(names.Count - 1));

        return swedish ? $"{rest} eller {last}" : $"{rest} or {last}";
    }
}
