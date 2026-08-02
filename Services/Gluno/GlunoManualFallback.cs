namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Stops Gluno telling somebody to do by hand the thing it was asked to do.
///
/// THE PRODUCTION FAILURE. A user with "Semester 2026" already selected asked
/// Gluno to add a place, and got back "Öppna Semester 2026 och lägg till
/// manuellt." The model did not invent that sentence — SideQuest handed it
/// over. The capability catalogue answers "how do I add an Activity?" with
/// "Open the Adventure and use the add button on the day you want", which is
/// correct as documentation and useless as a reply to "add this one".
///
/// SO THE REAL FIX IS UPSTREAM: an add request is resolved deterministically
/// and never reaches the model. This is the last line of defence, for the paths
/// nobody has thought of yet — and it is deliberately a REPLACEMENT rather than
/// a filter, because a half-scrubbed instruction is still an instruction.
///
/// WHAT IS NOT BLOCKED: genuine app help. Somebody asking "how do I add an
/// Activity myself?" deserves exactly the catalogue's answer. The difference is
/// the INTENT of the turn, which is why the caller passes it in — a guard that
/// matched on wording alone would break the feature it borrows its phrases
/// from.
/// </summary>
public static class GlunoManualFallback
{
    /// <summary>
    /// Phrases that tell the user to go and do it themselves.
    ///
    /// Normalised before matching — lowercased, accents folded — so "Öppna"
    /// and "oppna" are the same phrase. Deliberately short fragments: the model
    /// paraphrases, and "open the adventure" catches every sentence built
    /// around it.
    /// </summary>
    private static readonly string[] Phrases =
    [
        // Swedish
        "lagg till manuellt", "lagga till manuellt", "lagg till den sjalv",
        "lagg till det sjalv", "gor det sjalv", "skapa aktiviteten sjalv",
        "oppna aventyret", "oppna resan", "ga till resan", "ga till aventyret",
        "jag kan inte lagga till", "kan inte lagga till det harifran",
        "du far lagga till", "du kan lagga till den sjalv",

        // English
        "add it manually", "add it yourself", "add them yourself",
        "do it manually", "do it yourself", "create the activity yourself",
        "open the adventure", "open the trip", "go to the adventure",
        "go to the trip", "i can't add", "i cannot add", "i am unable to add",
    ];

    /// <summary>
    /// Intents where a "do it yourself" instruction is never the right answer,
    /// because the user asked Gluno to act rather than to explain.
    /// </summary>
    public static bool IsActionIntent(GlunoIntent intent) => intent
        is GlunoIntent.AddActivity
        or GlunoIntent.MoveActivity
        or GlunoIntent.PlanEmptyDay
        or GlunoIntent.ImproveExistingDay
        or GlunoIntent.BuildFullItinerary
        or GlunoIntent.PlaceRecommendation;

    public static bool Mentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalised = GlunoIntentRouter.Normalise(text);

        return Phrases.Any(phrase => normalised.Contains(phrase, StringComparison.Ordinal));
    }

    /// <summary>
    /// The answer, or a replacement when it told the user to do it by hand.
    ///
    /// REPLACED WHOLE, not edited. An answer that reached for a manual
    /// instruction has already decided it cannot help, and the rest of it is
    /// built around that — cutting the sentence out leaves a paragraph that
    /// still means the same thing.
    ///
    /// The replacement is short and asks for the one thing that would let the
    /// deterministic path take over.
    /// </summary>
    public static string Clean(string text, GlunoIntent intent, string? language)
    {
        if (!IsActionIntent(intent) || !Mentions(text)) return text;

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return swedish
            ? "Vilken plats vill du lägga till?"
            : "Which place would you like to add?";
    }
}
