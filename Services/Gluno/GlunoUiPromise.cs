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
    /// Ways of describing SideQuest's own plumbing to the person using it.
    ///
    /// Gluno is one feature of SideQuest, not a model commenting on an app it
    /// happens to live in. Where the model ends and the backend begins is not
    /// the user's problem, and telling them "the app refuses" or "I can only
    /// write text" is both unhelpful and — in the case that produced this
    /// list — untrue: the card it was explaining away could have been built on
    /// that very turn.
    /// </summary>
    private static readonly string[] Disclaimers =
    [
        // English
        "i can't put out buttons", "i cannot put out buttons",
        "i can't create buttons", "i cannot create buttons",
        "i can only write text", "i can only reply with text",
        "the app does that", "sidequest does that", "the app refuses",
        "the app won't", "not attached to", "isn't linked to",
        "i can't open an adventure", "i cannot open an adventure",
        "this conversation isn't", "this conversation is not",
        // Swedish
        "jag kan inte lagga ut knappar", "jag kan inte skapa knappar",
        "det gor appen", "det gor sidequest",
        // Both word orders. Swedish puts the verb second, so "just nu vägrar
        // appen…" inverts the subject and a single form misses it — which is
        // exactly how the reported sentence got through.
        "appen vagrar", "vagrar appen", "appen later inte", "later appen inte",
        "jag kan bara skriva text", "inte kopplat till", "inte kopplad till",
        "samtalet maste kopplas", "jag kan inte oppna ett adventure",
        "jag kan inte oppna nagot adventure", "samtalet ar inte",
    ];

    /// <summary>
    /// True when the text explains SideQuest's internals rather than answering.
    ///
    /// Separate from <see cref="PromisesAChoice"/> because the fix differs: a
    /// false promise is removed, whereas a disclaimer means the turn asked the
    /// wrong question of itself and should have produced a card.
    /// </summary>
    public static bool ExplainsItsOwnPlumbing(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalised = Normalise(text);

        return Disclaimers.Any(phrase => normalised.Contains(Normalise(phrase), StringComparison.Ordinal));
    }

    /// <summary>
    /// Ways of explaining WHY data is missing instead of WHAT that means.
    ///
    /// "I couldn't fetch current ratings — no providers are responding. This
    /// is from my own knowledge" tells somebody planning a holiday three
    /// things they cannot use and one thing that actively undermines the
    /// answer they are reading.
    ///
    /// The useful half of that sentence is "I can't confirm current ratings" —
    /// how much to trust what follows. Which service was called, whether it
    /// timed out, and where the remainder came from are internal facts, and
    /// naming a provider in a failure also attaches its brand to a bad moment
    /// it had no part in.
    /// </summary>
    private static readonly string[] SourceTalk =
    [
        // English
        "no providers", "provider unavailable", "provider failure",
        "providers are", "the provider", "my own knowledge", "my training data",
        "training data", "general knowledge", "couldn't fetch live",
        "could not fetch live", "live data", "the api", "api isn't",
        "api is not", "the integration", "the backend", "the service isn't",
        "the service is not", "didn't respond", "did not respond",
        "tripadvisor didn't", "tripadvisor did not",
        // Swedish
        "leverantor", "leverantorer", "ingen leverantor", "inga leverantorer",
        "min egen kunskap", "egen kunskap", "mina traningsdata", "traningsdata",
        "allman kunskap", "hamta live", "live-data", "livedata",
        "apiet", "api:t", "integrationen", "backenden",
        "tjansten svarar inte", "svarar inte just nu", "appen kan inte hamta",
        "tekniskt fel", "tripadvisor svarade",
    ];

    /// <summary>
    /// True when the text explains where data came from or why it is missing.
    ///
    /// Deliberately does NOT fire on somebody asking how SideQuest works — see
    /// the caller, which only applies this to Gluno's own answers, and on
    /// sentences rather than whole replies.
    /// </summary>
    public static bool ExplainsItsSources(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalised = Normalise(text);

        return SourceTalk.Any(phrase => normalised.Contains(Normalise(phrase), StringComparison.Ordinal));
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
            .Where(sentence => !PromisesAChoice(sentence) && !ExplainsItsOwnPlumbing(sentence))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        return string.Join(' ', kept).Trim();
    }

    /// <summary>
    /// Removes sentences that explain sources, and replaces them with one
    /// short line about what it means for the answer.
    ///
    /// REPLACED RATHER THAN DELETED, because the offending sentence usually
    /// carries the only useful half: "I couldn't fetch current ratings — no
    /// providers are responding" becomes nothing at all if simply dropped, and
    /// the user is left with a confident-looking answer and no idea a lookup
    /// failed. The caution has to survive; only the explanation goes.
    /// </summary>
    public static string WithoutSourceTalk(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sentences = Regex.Split(text, @"(?<=[.!?])\s+");

        var kept = sentences
            .Where(sentence => !ExplainsItsSources(sentence))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        // Nothing was removed — the answer never mentioned its sources.
        if (kept.Count == sentences.Length) return text;

        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var caution = swedish
            ? "Jag kan inte bekräfta aktuella uppgifter just nu, så kontrollera dem innan ni åker."
            : "I can't confirm current details just now, so check them before you go.";

        return kept.Count == 0
            ? caution
            : $"{string.Join(' ', kept).Trim()}\n\n{caution}";
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
