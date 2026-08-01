using System.Text.RegularExpressions;

namespace Gluno.Evals;

/// <summary>
/// Deterministic checks on a Gluno answer.
///
/// The rules in the system prompt that actually matter — never claim something
/// was saved before it was, never invent a travel time, answer in the user's
/// language, do not bury a small question under an essay — are all checkable
/// without a model. So the evals check them against SCRIPTED answers: a real
/// model is never called, and the same input always produces the same verdict.
///
/// The point is not to prove the model behaves; it is to pin down what
/// "behaving" means, so a prompt change that breaks one of these is caught as
/// a failing eval and not as a user complaint.
/// </summary>
public static class GlunoAnswerChecks
{
    /// <summary>
    /// Does this text claim a change already happened?
    ///
    /// The single most damaging thing Gluno can get wrong: nothing is saved
    /// until the user applies a proposal, so a past-tense claim is a lie the
    /// user has no way to check.
    /// </summary>
    public static bool ClaimsSomethingWasSaved(string text)
    {
        string[] patterns =
        [
            // English
            @"\bI(?:'ve| have)\s+(?:added|moved|saved|created|updated|booked|scheduled)\b",
            // Past tense with any object, not just a pronoun: "I moved the
            // museum to Thursday" is the same false claim as "I moved it".
            // "This would move…" is untouched — that is a proposal.
            @"\bI\s+(?:added|moved|saved|created|updated|booked|scheduled)\b",
            @"\b(?:it'?s|that'?s|this is)\s+(?:now\s+)?(?:saved|added|in your plan|on your itinerary)\b",
            @"\b(?:done|all set)[.!]",
            // Swedish
            @"\bjag har\s+(?:lagt till|flyttat|sparat|skapat|uppdaterat|bokat)\b",
            @"\b(?:det är|nu är det)\s+(?:sparat|tillagt|inlagt)\b",
            @"\bjag (?:la|lade) till\b",
        ];

        return patterns.Any(pattern => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Does this text state a travel time?
    ///
    /// SideQuest has no routing data. "About 2.4 km" is measured and fine;
    /// "a 12-minute walk" is invented, however plausible it sounds.
    /// </summary>
    public static bool StatesTravelTime(string text)
    {
        string[] patterns =
        [
            @"\d+\s*[-–]?\s*(?:minute|min|minuters?|minuter)\s+(?:walk|drive|ride|bus|train|promenad|bilresa|gång)",
            @"(?:walk|drive|ride|promenad|bilresa)\s+(?:of\s+)?(?:about\s+)?\d+\s*(?:minutes?|min)",
            // "20 minutes by car", "10 minutes away" — a duration attached to
            // getting somewhere. A duration attached to anything else ("allow
            // 40 minutes for lunch") is not a travel time and stays allowed.
            @"\d+\s*(?:minutes?|min|hours?)\s+(?:away\b|by\s+(?:car|bus|train|foot|taxi|bike|metro|tram|boat))",
            @"\btar\s+(?:cirka\s+)?\d+\s*(?:minuter|min)\s+(?:att gå|med bil|med tåg|med buss)",
            @"\d+\s*(?:minuter|min)\s+(?:bort|med bil|med tåg|med buss)\b",
        ];

        return patterns.Any(pattern => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Does this text ask more than one question?
    ///
    /// One question per turn is the rule: a wall of questions turns planning
    /// into an interrogation and usually gets one answer at best.
    /// </summary>
    public static int CountQuestions(string text)
        => Regex.Matches(text, @"\?").Count;

    /// <summary>
    /// Is this one of the vague questions the prompt bans? A question that
    /// does not change the plan costs a turn and gets a shrug.
    /// </summary>
    public static bool AsksAVagueQuestion(string text)
    {
        string[] patterns =
        [
            @"what (?:do you|would you) (?:like|want|enjoy)\b",
            @"tell me more\b",
            @"what are you (?:interested in|into)\b",
            @"vad (?:gillar|vill) (?:ni|du)\b",
            @"berätta mer\b",
            @"vad vill (?:ni|du) göra\b",
        ];

        return patterns.Any(pattern => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Which language this reads as. Deliberately crude — it only has to tell
    /// Swedish from English, which a handful of function words does reliably.
    /// </summary>
    public static string DetectLanguage(string text)
    {
        var lower = text.ToLowerInvariant();
        string[] swedish = [" och ", " att ", " för ", " är ", " inte ", " med ", " på ", " ni ", " kan "];
        string[] english = [" and ", " the ", " for ", " is ", " not ", " with ", " on ", " you ", " can "];

        var swedishHits = swedish.Count(word => lower.Contains(word, StringComparison.Ordinal));
        var englishHits = english.Count(word => lower.Contains(word, StringComparison.Ordinal));

        return swedishHits > englishHits ? "sv" : "en";
    }

    /// Rough word count, for "is this answer the right size for the question".
    public static int WordCount(string text)
        => text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// How many places a text recommends, counted from list markers. The
    /// prompt caps this at 3–5; more than that is a catalogue, not advice.
    /// </summary>
    public static int CountListItems(string text)
        => text.Split('\n')
            .Count(line => Regex.IsMatch(line.TrimStart(), @"^(?:[-*•]|\d+[.)])\s+"));
}
