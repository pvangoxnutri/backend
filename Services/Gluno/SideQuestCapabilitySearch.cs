using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

public sealed class CapabilityMatch
{
    public required SideQuestCapability Capability { get; init; }
    public required double Score { get; init; }
    /// Why it matched — "name", "synonym", "fuzzy", "screen". Useful when a
    /// result looks wrong and someone has to work out why.
    public required string MatchedOn { get; init; }
}

/// <summary>
/// Deterministic search over the capability registry.
///
/// WHY NOT JUST SEND THE WHOLE REGISTRY. It is thousands of tokens, on every
/// turn, mostly irrelevant — and a model handed twenty-odd capabilities will
/// cheerfully blend two of them into a feature that does not exist. Narrowing
/// to a handful of genuinely relevant entries is both cheaper and more honest.
///
/// WHY NOT EMBEDDINGS. This is a fixed, small, hand-written vocabulary in two
/// languages. Token overlap plus a short edit-distance pass covers "kostnader"
/// → Expenses and "activty" → Activities without a model, a network call, or
/// any nondeterminism — and a deterministic matcher is one the evals can pin
/// down exactly.
/// </summary>
public static class SideQuestCapabilitySearch
{
    /// Below this a token is too short for fuzzy matching to mean anything —
    /// most short words are within two edits of most other short words.
    private const int MinFuzzyLength = 5;

    /// <summary>
    /// A near-match is worth LESS than an exact one.
    ///
    /// Learned the hard way: at parity, a capability matching two words
    /// approximately outranked one matching a word exactly, and "how do I move
    /// an Activity" landed on rescheduling instead of moving.
    /// </summary>
    private const double FuzzyHitWeight = 0.75;

    /// <summary>
    /// Words that carry no signal about which feature is meant. Without this,
    /// "var hittar jag …" matches every capability whose description happens
    /// to contain "var", and an unrelated question quietly returns results.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "the", "and", "for", "with", "how", "what", "where", "can", "does", "did",
        "you", "your", "our", "this", "that", "there", "here", "from", "into", "get",
        "add", "see", "use", "want", "need", "any", "all", "one", "her", "his", "its",
        // Swedish
        "och", "att", "for", "med", "hur", "vad", "var", "kan", "jag", "vi",
        "min", "mitt", "mina", "den", "det", "dem", "som", "har", "ska", "vill",
        "till", "pa", "ett", "en", "ar", "man", "sig", "nar", "dar", "hit",
    };

    public static IReadOnlyList<CapabilityMatch> Search(
        string query, string language, string? currentScreen = null, int limit = 4)
    {
        // Two views of the query. Phrase matching needs every word in order
        // ("trip dates" must not match "dates" alone); overlap and fuzzy
        // matching drop the short filler words that match everything.
        var allTokens = Tokenise(query, minLength: 1);
        var significantTokens = Tokenise(query, minLength: 3);

        var matches = new List<CapabilityMatch>();

        foreach (var capability in SideQuestCapabilities.All)
        {
            var (score, matchedOn) = ScoreCapability(capability, allTokens, significantTokens, query, currentScreen);
            if (score <= 0) continue;

            matches.Add(new CapabilityMatch { Capability = capability, Score = Math.Round(score, 3), MatchedOn = matchedOn });
        }

        // A question with no recognisable feature word in it ("how does this
        // work?") is still answerable when we know where they are standing.
        if (matches.Count == 0 && currentScreen != null)
        {
            return ForScreen(currentScreen, limit);
        }

        return matches
            .OrderByDescending(m => m.Score)
            // Stable tiebreak so the same question always returns the same
            // order — an eval can then assert on it.
            .ThenBy(m => m.Capability.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 8))
            .ToList();
    }

    /// <summary>
    /// What is worth mentioning on a given screen, with no query at all —
    /// "what can I do here?".
    /// </summary>
    public static IReadOnlyList<CapabilityMatch> ForScreen(string screen, int limit = 5)
        => SideQuestCapabilities.All
            .Where(capability => capability.Screens.Contains(screen))
            .Select(capability => new CapabilityMatch { Capability = capability, Score = 1, MatchedOn = "screen" })
            .Take(Math.Clamp(limit, 1, 8))
            .ToList();

    private static (double Score, string MatchedOn) ScoreCapability(
        SideQuestCapability capability,
        IReadOnlyList<string> allTokens,
        IReadOnlyList<string> significantTokens,
        string rawQuery,
        string? currentScreen)
    {
        if (allTokens.Count == 0) return (0, "none");

        // An exact id is unambiguous — nothing should outrank it.
        if (string.Equals(capability.Id, rawQuery.Trim(), StringComparison.OrdinalIgnoreCase))
            return (10, "id");

        double score = 0;
        var matchedOn = "none";

        // Phrase matches are scored by LENGTH. "showing in the slideshow"
        // is far stronger evidence than the bare word "slideshow", and
        // without this the generic capability always beats the specific one.
        //
        // Matching is on token boundaries, not substrings: "dra" must not
        // match inside "ändrar", which is exactly the kind of accident that
        // made an unrelated feature win.
        foreach (var name in new[] { capability.NameEn, capability.NameSv })
        {
            var phrase = Tokenise(name, minLength: 1);
            if (phrase.Count == 0 || !ContainsPhrase(allTokens, phrase)) continue;

            score = Math.Max(score, 6 + phrase.Count);
            matchedOn = "name";
        }

        foreach (var synonym in capability.Synonyms)
        {
            var phrase = Tokenise(synonym, minLength: 1);
            if (phrase.Count == 0 || !ContainsPhrase(allTokens, phrase)) continue;

            score = Math.Max(score, 5 + phrase.Count);
            if (matchedOn == "none") matchedOn = "synonym";
        }

        var haystack = Tokenise(string.Join(
            ' ',
            capability.NameEn, capability.NameSv,
            capability.DescriptionEn, capability.DescriptionSv,
            string.Join(' ', capability.Synonyms)),
            minLength: 3);

        var overlap = significantTokens.Count(token => haystack.Contains(token));

        // Misspellings and inflections count too, worth less than an exact
        // hit. Running this alongside overlap rather than only as a fallback
        // is what lets "flyttar" find "flytta" while "aktivitet" matches
        // several capabilities equally.
        var fuzzyHits = significantTokens
            .Where(token => token.Length >= MinFuzzyLength && !haystack.Contains(token))
            .Count(token => haystack.Any(candidate =>
                candidate.Length >= MinFuzzyLength && EditDistanceWithin(token, candidate, EditBudget(token))));

        if (overlap > 0 || fuzzyHits > 0)
        {
            score = Math.Max(score, 2 + overlap + (FuzzyHitWeight * fuzzyHits));
            if (matchedOn == "none") matchedOn = overlap > 0 ? "tokens" : "fuzzy";
        }

        // A small nudge for what the user is looking at right now. Deliberately
        // small: the question still decides, the screen only breaks ties.
        if (score > 0 && currentScreen != null && capability.Screens.Contains(currentScreen))
        {
            score += 1.5;
        }

        return (score, matchedOn);
    }

    /// <summary>
    /// True when <paramref name="phrase"/> appears as a consecutive run of
    /// whole tokens in <paramref name="tokens"/>.
    ///
    /// Whole tokens, not substrings. Substring matching is how a three-letter
    /// synonym ends up matching inside an unrelated word and quietly outranks
    /// the capability the user actually asked about.
    /// </summary>
    private static bool ContainsPhrase(IReadOnlyList<string> tokens, IReadOnlyList<string> phrase)
    {
        if (phrase.Count == 0 || phrase.Count > tokens.Count) return false;

        for (var start = 0; start <= tokens.Count - phrase.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < phrase.Count; offset++)
            {
                if (string.Equals(tokens[start + offset], phrase[offset], StringComparison.Ordinal)) continue;
                matched = false;
                break;
            }

            if (matched) return true;
        }

        return false;
    }

    /// <summary>
    /// Lowercase, accent-stripped, punctuation-free. "Kostnadsdelning?" and
    /// "kostnadsdelning" have to be the same word, or half the Swedish
    /// vocabulary misses.
    /// </summary>
    private static string Normalise(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <param name="minLength">
    /// 1 keeps every word, for phrase matching where order and completeness
    /// matter. 3 drops the filler that matches everything, for overlap and
    /// fuzzy scoring.
    /// </param>
    private static IReadOnlyList<string> Tokenise(string value, int minLength)
    {
        var tokens = Normalise(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= minLength)
            // Stop words are kept for phrase matching (a phrase is only a
            // phrase in full) and dropped from scoring, where they would match
            // everything.
            .Where(token => minLength == 1 || !StopWords.Contains(token))
            .ToList();

        // Phrase matching needs position, so only the scoring view dedupes.
        return minLength == 1 ? tokens : tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// How many edits a token may be off by.
    ///
    /// Scaled with length: two edits on a six-letter word reaches half the
    /// dictionary, which is how "poster" ended up matching unrelated features.
    /// Long words can afford the slack; short ones cannot.
    /// </summary>
    private static int EditBudget(string token) => token.Length >= 8 ? 2 : 1;

    /// <summary>
    /// Levenshtein with an early exit. Bounded work: if the lengths differ by
    /// more than the budget the answer is already no.
    /// </summary>
    private static bool EditDistanceWithin(string left, string right, int budget)
    {
        if (Math.Abs(left.Length - right.Length) > budget) return false;
        if (left == right) return true;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            // Every remaining path can only get longer than this row's best.
            if (rowBest > budget) return false;

            (previous, current) = (current, previous);
        }

        return previous[right.Length] <= budget;
    }
}
