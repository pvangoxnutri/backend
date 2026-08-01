namespace sidequest.backend.Services.Gluno;

public sealed record GlunoReviewFinding(string Code, string Detail);

public sealed class GlunoReviewResult
{
    /// Nothing worth another pass.
    public required bool Acceptable { get; init; }

    public required IReadOnlyList<GlunoReviewFinding> Findings { get; init; }

    /// <summary>
    /// Instructions for a revision pass, or null when none is warranted.
    ///
    /// Null is the common case, and deliberately so — a second model round
    /// doubles the cost and the wait, and most answers do not need one.
    /// </summary>
    public string? RevisionInstruction { get; init; }

    public static GlunoReviewResult Fine => new()
    {
        Acceptable = true,
        Findings = Array.Empty<GlunoReviewFinding>(),
    };
}

/// <summary>
/// A structured second look at an answer before it is sent.
///
/// WHAT THIS IS NOT: a model grading itself. That costs a full round, is
/// unreliable in exactly the cases that matter, and mostly produces agreement.
/// Every check below is deterministic — measurable properties of the text and
/// the turn, not opinions about it.
///
/// WHEN IT RUNS. Only for turns the strategy marked as expensive to get wrong:
/// day plans, itineraries, anything carrying a proposal. An app-help answer and
/// a one-line factual reply are not worth reviewing, and running a review on
/// them would be pure overhead on the fastest, most common turns.
///
/// WHAT A FINDING MEANS. Findings feed a REVISION instruction, not a rejection.
/// The quality gate is what blocks; this improves. Keeping the two separate
/// matters — a review that can block would eventually block something correct
/// for being oddly worded.
/// </summary>
public static class GlunoResponseReview
{
    /// <summary>
    /// Past this, an answer has stopped answering and started lecturing. Set
    /// generously — the target word counts in the workflow are the real
    /// guidance, this is the point at which it is worth spending a round.
    /// </summary>
    private const double LengthOverrunFactor = 2.2;

    /// Openers that carry no information. Every one of these is a sentence the
    /// user has to read before reaching the answer.
    private static readonly string[] Filler =
    [
        "great question", "that's a great question", "i'd be happy to", "i would be happy to",
        "certainly", "absolutely", "of course", "let me help you", "sure thing",
        "vilken bra fraga", "vad kul att du fragar", "sjalvklart", "absolut",
        "jag hjalper garna till", "sa klart",
    ];

    /// Hedges that promise an answer instead of giving one.
    private static readonly string[] EmptyPromises =
    [
        "let me know if", "feel free to ask", "hope that helps", "hope this helps",
        "sag till om", "hor av dig om", "hoppas det hjalper", "beratta garna mer",
    ];

    public static GlunoReviewResult Review(GlunoReviewInput input)
    {
        var findings = new List<GlunoReviewFinding>();
        var text = input.AnswerText ?? string.Empty;
        var normalised = GlunoIntentRouter.Normalise(text);

        if (text.Trim().Length == 0)
        {
            return new GlunoReviewResult
            {
                Acceptable = false,
                Findings = [new GlunoReviewFinding("empty_answer", "The turn produced no text.")],
                RevisionInstruction = "Answer the user's question directly, in two or three sentences.",
            };
        }

        // ── Did it answer what was asked? ─────────────────────────────────
        //
        // Approximated by whether a proposal was expected and produced, and by
        // whether an app-help question got app words back. Crude on purpose —
        // a precise version needs a model, and a model here would cost a round
        // on every planning turn.
        if (input.ExpectsProposal && !input.ProducedProposal)
        {
            findings.Add(new GlunoReviewFinding(
                "no_proposal_for_change_request",
                "The user asked for a change and nothing was proposed."));
        }

        if (!input.ExpectsProposal && input.ProducedProposal)
        {
            findings.Add(new GlunoReviewFinding(
                "unrequested_proposal",
                "The user asked a question and got a proposed change."));
        }

        // ── Was the context used? ─────────────────────────────────────────
        if (input.HasTripContext && input.ExpectsProposal && !input.UsedAnyTool)
        {
            findings.Add(new GlunoReviewFinding(
                "context_not_used",
                "A planning answer was written without reading the Adventure."));
        }

        // ── Length ────────────────────────────────────────────────────────
        var words = CountWords(text);
        if (input.TargetWordCount > 0 && words > input.TargetWordCount * LengthOverrunFactor)
        {
            findings.Add(new GlunoReviewFinding(
                "too_long",
                $"{words} words against a target of about {input.TargetWordCount}."));
        }

        // ── Filler ────────────────────────────────────────────────────────
        if (Filler.Any(phrase => normalised.Contains(phrase, StringComparison.Ordinal)))
        {
            findings.Add(new GlunoReviewFinding("filler_opening", "The answer opens with a stock phrase."));
        }

        if (EmptyPromises.Any(phrase => normalised.Contains(phrase, StringComparison.Ordinal)))
        {
            findings.Add(new GlunoReviewFinding("empty_closing", "The answer closes with a stock offer to help."));
        }

        // ── Unnecessary follow-up questions ───────────────────────────────
        var questions = text.Count(character => character == '?');
        if (questions > 1)
        {
            findings.Add(new GlunoReviewFinding(
                "too_many_questions",
                $"{questions} questions in one answer; ask at most one."));
        }

        if (questions > 0 && input.PreferencesAlreadyKnown.Count > 0)
        {
            var askedAnyway = input.PreferencesAlreadyKnown
                .Where(key => MentionsPreference(normalised, key))
                .ToList();

            if (askedAnyway.Count > 0)
            {
                findings.Add(new GlunoReviewFinding(
                    "asks_for_known_preference",
                    $"Asks about {string.Join(", ", askedAnyway)}, which the user already stated."));
            }
        }

        // ── Claims beyond the sources ─────────────────────────────────────
        if (input.QualityBlockers.Count > 0)
        {
            findings.Add(new GlunoReviewFinding(
                "quality_blockers",
                string.Join("; ", input.QualityBlockers.Take(3))));
        }

        if (findings.Count == 0) return GlunoReviewResult.Fine;

        return new GlunoReviewResult
        {
            Acceptable = false,
            Findings = findings,
            RevisionInstruction = BuildInstruction(findings, input),
        };
    }

    /// <summary>
    /// Turns findings into something the model can act on in one pass.
    ///
    /// Written as concrete instructions rather than criticism — "cut it to
    /// about 90 words" is actionable in a way that "too long" is not.
    /// </summary>
    private static string BuildInstruction(IReadOnlyList<GlunoReviewFinding> findings, GlunoReviewInput input)
    {
        var instructions = new List<string>();

        foreach (var finding in findings)
        {
            instructions.Add(finding.Code switch
            {
                "too_long" => $"Cut the answer to about {input.TargetWordCount} words. Keep the specifics, drop the framing.",
                "filler_opening" => "Delete the opening pleasantry and start with the answer.",
                "empty_closing" => "Delete the closing offer to help.",
                "too_many_questions" => "Ask at most one question, and make it concrete.",
                "asks_for_known_preference" => $"Do not ask about {finding.Detail}. It is already in the context — use it.",
                "unrequested_proposal" => "The user asked a question. Answer it; do not propose a change.",
                "no_proposal_for_change_request" => "The user asked for a change. Make the proposal.",
                "context_not_used" => "Read the Adventure before answering — use get_trip_overview.",
                "quality_blockers" => $"Fix these before answering: {finding.Detail}",
                _ => finding.Detail,
            });
        }

        return string.Join(" ", instructions);
    }

    /// Whether a question is fishing for something already known.
    private static bool MentionsPreference(string normalisedText, string preferenceKey) => preferenceKey switch
    {
        "budget" => normalisedText.Contains("budget", StringComparison.Ordinal)
            || normalisedText.Contains("kostar", StringComparison.Ordinal)
            || normalisedText.Contains("spend", StringComparison.Ordinal),
        "pace" => normalisedText.Contains("tempo", StringComparison.Ordinal)
            || normalisedText.Contains("pace", StringComparison.Ordinal),
        "transport" => normalisedText.Contains(" bil", StringComparison.Ordinal)
            || normalisedText.Contains(" car", StringComparison.Ordinal),
        "food" => normalisedText.Contains("ata", StringComparison.Ordinal)
            || normalisedText.Contains("mat", StringComparison.Ordinal)
            || normalisedText.Contains("eat", StringComparison.Ordinal)
            || normalisedText.Contains("food", StringComparison.Ordinal),
        _ => false,
    };

    private static int CountWords(string text)
        => text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed class GlunoReviewInput
{
    public string? AnswerText { get; init; }
    public bool ExpectsProposal { get; init; }
    public bool ProducedProposal { get; init; }
    public bool HasTripContext { get; init; }
    public bool UsedAnyTool { get; init; }
    public int TargetWordCount { get; init; }

    /// Preference keys already stored for this user. Asking about one of these
    /// again is the thing that makes an assistant feel like a form.
    public IReadOnlyList<string> PreferencesAlreadyKnown { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> QualityBlockers { get; init; } = Array.Empty<string>();
}
