namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The sentences that get written down when the real ones may not be.
///
/// WHY THESE ARE WRITTEN HERE RATHER THAN DERIVED. A provider that licenses its
/// content for the answer and not for storage makes the answer itself
/// unstorable: "Real Alcázar, Metropol Parasol and the cathedral are the pick of
/// Sevilla" is provider content in prose form, and moving it from a payload
/// field into a paragraph does not change what it is.
///
/// The alternative — writing the model's sentence and then stripping names out
/// of it afterwards — is a pattern-match against text, and a pattern-match that
/// misses stores exactly the thing it was there to prevent. There is no version
/// of it that fails safe.
///
/// So the persisted sentence is not a redaction of the live one. It is a
/// different sentence, chosen before either is written, saying only what
/// SideQuest itself knows: that this turn produced suggestions.
///
/// THE COST, STATED PLAINLY. A turn that answered about places AND about
/// something else loses the something else from its history too, because the
/// two are one paragraph by the time they get here and separating them would be
/// the pattern-match again. Reopening such a conversation shows the neutral
/// line instead of the full answer.
/// </summary>
public static class GlunoNeutralText
{
    private static bool Swedish(string? language)
        => string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

    /// What an answer that recommended places becomes in the history.
    public static string PlaceAnswer(string? language) => Swedish(language)
        ? "Jag tog fram några platsförslag för er resa."
        : "I put together a few place suggestions for your trip.";

    /// The stand-in for a place's name inside a sentence.
    public static string ThePlace(string? language) => Swedish(language) ? "platsen" : "the place";

    public static string DayQuestion(string? language) => Swedish(language)
        ? "Vilken dag vill du lägga till platsen?"
        : "Which day should the place go on?";

    /// What "Here's Real Alcázar as a suggestion." becomes.
    public static string PlaceProposed(string? language) => Swedish(language)
        ? "Här är platsen som förslag."
        : "Here's the place as a suggestion.";

    /// The proposal's own one-line summary, shown on the card and stored on the
    /// row. A title is the provider's name for the place.
    public static string ProposalSummary(string? language) => Swedish(language)
        ? "Plats från ett förslag"
        : "Place from a suggestion";

    /// <summary>
    /// A row label for one of several places the user is choosing between.
    ///
    /// Only ever stored, never shown: the live card carries the real names, and
    /// this question is not rebuilt after a reload — see
    /// GlunoClarification.ContentSuppressed.
    /// </summary>
    public static string PlaceOptionLabel(string? language, int index) => Swedish(language)
        ? $"Alternativ {index + 1}"
        : $"Option {index + 1}";

    /// The question "which of these did you mean", with no names in it.
    public static string WhichPlaceQuestion(string? language) => Swedish(language)
        ? "Vilken av platserna menar du?"
        : "Which of the places do you mean?";
}
