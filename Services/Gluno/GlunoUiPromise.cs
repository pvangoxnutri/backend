using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Stops Gluno pointing at buttons that are not there.
///
/// THE FAILURE THIS EXISTS FOR. In a global conversation the model wrote
/// "Which Adventure is this about? Pick Semester 2026 below and I'll have the
/// whole day plan" — and no card was rendered, because no clarification had
/// been created. The user was told to tap something that did not exist.
///
/// The card is ALWAYS built by the backend. When one exists it carries its own
/// question and its own options; when none exists there is nothing to point at,
/// and any sentence promising a choice is false. So the rule is mechanical: a
/// promise of an on-screen choice is only allowed in a turn that actually
/// produced one.
///
/// This trims rather than fails. A turn that is otherwise a good answer must
/// not be thrown away over one bad clause — but the clause does not ship.
/// </summary>
public static class GlunoUiPromise
{
    /// <summary>
    /// Phrases that promise something tappable.
    ///
    /// Both languages, and deliberately about POSITION and ACTION rather than
    /// about choice in general: "you could pick a different day" is a
    /// suggestion, "pick one below" is a claim about the screen.
    /// </summary>
    private static readonly string[] Promises =
    [
        "below", "here below", "underneath", "tap ", "click ", "press ",
        "choose one of", "pick one of", "the options", "these options",
        "har nedanfor", "nedanfor", "nedan", "harunder", "tryck ", "klicka ",
        "valj nedan", "valj en av", "alternativen", "knapparna",
    ];

    /// <summary>
    /// True when the text promises an on-screen choice.
    ///
    /// Matched on a normalised copy so Swedish accents do not hide a promise,
    /// and on word boundaries so "nedan" does not fire inside an unrelated
    /// word.
    /// </summary>
    public static bool PromisesAChoice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalised = Normalise(text);

        return Promises.Any(phrase => ContainsWord(normalised, Normalise(phrase)));
    }

    /// <summary>
    /// The text with any promise of a choice removed, sentence by sentence.
    ///
    /// Sentences rather than the whole answer: "Here's what I found. Tap one
    /// below." should keep its first half. If every sentence promised
    /// something, the caller gets an empty string back and substitutes its own
    /// line — an empty answer is better than a false instruction.
    /// </summary>
    public static string WithoutPromises(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Split on sentence ends, keeping the punctuation with its sentence.
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");

        var kept = sentences
            .Where(sentence => !PromisesAChoice(sentence))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        return string.Join(' ', kept).Trim();
    }

    private static bool ContainsWord(string text, string phrase)
    {
        var at = text.IndexOf(phrase, StringComparison.Ordinal);

        while (at >= 0)
        {
            var startsClean = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var after = at + phrase.Length;

            // A trailing space in the phrase already ends the word; otherwise
            // the next character has to be a non-letter.
            var endsClean = phrase.EndsWith(' ')
                || after >= text.Length
                || !char.IsLetter(text[after]);

            if (startsClean && endsClean) return true;

            at = text.IndexOf(phrase, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Normalise(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
