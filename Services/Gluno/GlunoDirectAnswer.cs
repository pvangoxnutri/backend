namespace sidequest.backend.Services.Gluno;

public sealed record GlunoDirectResult(string Text)
{
    /// Screens to offer, when the answer is "it's over there".
    public IReadOnlyList<GlunoNavigationCard> Navigations { get; init; } = Array.Empty<GlunoNavigationCard>();

    /// Short machine reason for telemetry: "navigation", "feature_missing",
    /// "proposal_status", "rejection_confirmed".
    public required string Reason { get; init; }
}

/// <summary>
/// Turns that can be answered correctly from structured data, with no model
/// call at all.
///
/// WHY THIS IS WORTH HAVING. Some questions have exactly one right answer and
/// it is already in a data structure. "Open the packing list" resolves to a
/// navigation target; "does SideQuest do flight tracking?" resolves to the
/// capability registry saying no. Sending those to a model costs money and a
/// second of the user's time to produce a sentence we could have written
/// ourselves — and occasionally to produce a slightly different sentence,
/// which is worse.
///
/// WHY IT IS DELIBERATELY NARROW. The failure mode on the other side is far
/// uglier: a canned response where the user wanted help. A templated answer
/// reads as a bot, and one of those in ten turns undoes the impression that
/// there is an expert on the other end. So this only fires where the answer is
/// genuinely a lookup, and everything with any judgement in it goes to a model.
///
/// The wording below is Gluno's — short, direct, no hedging, no apology, and
/// localised. It should be indistinguishable from a good model answer, because
/// for these questions a good model answer is exactly this.
/// </summary>
public static class GlunoDirectAnswer
{
    /// <summary>
    /// Answers the turn without a model, or returns null to let the model run.
    ///
    /// Null is the common case and the safe default: when in doubt, spend the
    /// call.
    /// </summary>
    public static GlunoDirectResult? TryAnswer(GlunoDirectRequest request)
    {
        var swedish = string.Equals(request.Language, "sv", StringComparison.OrdinalIgnoreCase);

        // ── A pure navigation request with exactly one obvious target ─────
        //
        // "Open the packing list" is not a question, it is a request to be
        // taken somewhere. One capability matched, it has a screen, and the
        // button does the explaining.
        if (request.Intent == GlunoIntent.NavigationRequest
            && request.NavigationTarget is { } target
            && GlunoNavigationTargets.IsKnown(target.TargetId))
        {
            return new GlunoDirectResult(
                swedish ? $"Här är {target.Label.ToLowerInvariant()}." : $"Here's {target.Label.ToLowerInvariant()}.")
            {
                Navigations = [target],
                Reason = "navigation",
            };
        }

        // ── A feature that does not exist ─────────────────────────────────
        //
        // The registry is the whole truth about what SideQuest does, so "no"
        // is a fact rather than a judgement. Saying it directly also removes
        // the turn where a model is most tempted to invent a button.
        if (request.Intent == GlunoIntent.SideQuestHelp && request.FeatureDefinitelyMissing)
        {
            return new GlunoDirectResult(
                swedish
                    ? "Det finns inte i SideQuest. Säg vad du försöker få gjort så hittar vi ett annat sätt."
                    : "SideQuest doesn't do that. Tell me what you're trying to get done and we'll find another way.")
            {
                Reason = "feature_missing",
            };
        }

        // ── Confirming a rejection ────────────────────────────────────────
        //
        // The user said no. There is nothing to reason about, and a model turn
        // here reliably produces an unwanted "would you like me to suggest
        // something else instead?".
        if (request.Intent == GlunoIntent.ConfirmationOrRejection && request.WasRejection)
        {
            return new GlunoDirectResult(
                swedish ? "Okej, jag stryker det." : "Okay, dropping that one.")
            {
                Reason = "rejection_confirmed",
            };
        }

        // ── Status of a pending proposal ──────────────────────────────────
        if (request.Intent == GlunoIntent.FollowUpClarification
            && request.PendingProposalSummary is { } summary
            && request.AsksAboutProposalStatus)
        {
            return new GlunoDirectResult(
                swedish
                    ? $"Förslaget ligger kvar och väntar på dig: {summary}. Inget är sparat förrän du godkänner det."
                    : $"The suggestion is still waiting for you: {summary}. Nothing is saved until you accept it.")
            {
                Reason = "proposal_status",
            };
        }

        // ── Forgetting a preference ───────────────────────────────────────
        //
        // The write already happened through the action executor. All that is
        // left is to confirm it, and a model adds nothing but latency.
        if (request.Intent == GlunoIntent.ForgetPreference && request.PreferenceWasForgotten)
        {
            return new GlunoDirectResult(
                swedish ? "Glömt. Jag utgår inte från det längre." : "Forgotten. I won't assume that any more.")
            {
                Reason = "preference_forgotten",
            };
        }

        return null;
    }
}

public sealed class GlunoDirectRequest
{
    public required GlunoIntent Intent { get; init; }
    public string Language { get; init; } = "en";

    /// The single navigation target, when the request resolved to exactly one.
    public GlunoNavigationCard? NavigationTarget { get; init; }

    /// <summary>
    /// True only when the capability search returned NOTHING for a clear
    /// feature question — not merely when it returned something unexpected.
    /// A near-miss is a case for a model, which can explain the closest real
    /// feature.
    /// </summary>
    public bool FeatureDefinitelyMissing { get; init; }

    public bool WasRejection { get; init; }
    public bool AsksAboutProposalStatus { get; init; }
    public string? PendingProposalSummary { get; init; }
    public bool PreferenceWasForgotten { get; init; }
}
