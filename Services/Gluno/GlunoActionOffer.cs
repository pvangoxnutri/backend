using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Keeps the model's promises true.
///
/// THE RULE. A sentence like "Vill du ha den som dagsplan?" is a claim that a
/// yes will do something. That is only true when the server holds structured
/// state a yes can resume — a proposal, a clarification, or a pending action.
/// A model turn that ends on an offer with none of those behind it is writing
/// a cheque the backend cannot cash, and in production the user's "Ja det blir
/// bra" bounced off the model with an excuse about conversation scope.
///
/// ENFORCED IN RESPONSE BUILDING, NOT IN THE PROMPT. A prompt is a request;
/// this is a check. Either the turn backs the offer with a pending action
/// built from facts the server already resolved, or the offer sentence does
/// not ship.
///
/// Trims rather than fails, like <see cref="GlunoUiPromise"/>: one false
/// clause must not cost an otherwise good answer.
/// </summary>
public static class GlunoActionOffer
{
    /// <summary>
    /// Phrases that offer to ACT on a yes. Deliberately about Gluno doing
    /// something — "you could visit in the morning" is advice, "vill du att
    /// jag lägger in det" is an offer.
    /// </summary>
    private static readonly string[] Offers =
    [
        // Swedish
        "vill du att jag", "vill du att vi", "ska jag ", "ska vi lagga",
        "vill du ha den som dagsplan", "vill du ha en dagsplan", "vill du ha dagsplan",
        "vill du ha det som dagsplan", "sag till sa", "sag bara till",
        // English
        "want me to", "shall i ", "would you like me to", "do you want me to",
        "say the word and", "just say the word", "want it as a day plan",
        "would you like it as a day plan",
    ];

    private static readonly string[] DayPlanWords =
    [
        "dagsplan", "dagplan", "dagsschema", "planera dagen", "planerar dagen",
        "day plan", "plan the day", "plan your day", "plan that day",
    ];

    public static bool ContainsOffer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalised = Normalise(text);

        return Offers.Any(phrase => normalised.Contains(phrase, StringComparison.Ordinal));
    }

    /// An offer whose subject is a day plan — the one offer the backend can
    /// back with a pending action, because the Adventure resolver and the
    /// trip's own days supply every fact it needs.
    public static bool IsDayPlanOffer(string? text)
        => ContainsOffer(text)
            && DayPlanWords.Any(word => Normalise(text!).Contains(word, StringComparison.Ordinal));

    /// <summary>
    /// The text with offer sentences removed, sentence by sentence.
    ///
    /// When every sentence was an offer, a short neutral line replaces the
    /// answer — an empty bubble is not an answer, and neither was the offer.
    /// </summary>
    public static string Strip(string text, string? language)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var kept = Regex.Split(text, @"(?<=[.!?])\s+")
            .Where(sentence => !ContainsOffer(sentence))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        if (kept.Count > 0) return string.Join(' ', kept).Trim();

        return string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
            ? "Säg till vad du vill att jag tar fram."
            : "Tell me what you'd like me to put together.";
    }

    private static string Normalise(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
