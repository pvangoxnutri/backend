using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

public static class GlunoPendingActionTypes
{
    /// An offered day plan, waiting for a yes. Resumes into the ordinary
    /// planning pipeline once the Adventure and the day are settled.
    public const string PlanDay = "plan_day";

    /// A failed place-add worth trying again — the durable twin of the live
    /// GlunoTurnAction, so "nudå?" after a failure resumes the same add.
    public const string RetryPlaceAdd = "retry_place_add";

    /// A fresh shortlist from a turn's stored search context.
    public const string ShowNewPlaceSuggestions = "show_new_place_suggestions";
}

/// <summary>
/// The one action this conversation has offered and not yet resolved.
///
/// WHY THIS EXISTS. In production the model asked "Vill du ha den som
/// dagsplan?", the user said "Ja det blir bra", and the yes went back to the
/// model — which reinterpreted the whole conversation and refused because the
/// chat was not scoped to an Adventure. The offer existed only as prose;
/// nothing on the server represented it, so nothing could resume it.
///
/// EVERY FIELD IS SERVER-DERIVED. The Adventure comes from the deterministic
/// resolver or a tapped clarification, the destination from SideQuest's own
/// geography, the ids from rows this backend minted. Nothing here is read out
/// of model text — a model sentence may only ever cause an offer whose facts
/// the server already holds.
///
/// Stored on the conversation's working state: server-owned, per-conversation,
/// and invisible to the client. At most one at a time — a new offer replaces
/// the old, and <see cref="ExpiresAtUtc"/> stops a stale "ja" resuming
/// something from another sitting.
/// </summary>
public sealed class GlunoPendingAction
{
    /// One of <see cref="GlunoPendingActionTypes"/>.
    public string Type { get; set; } = string.Empty;

    /// The assistant turn that made the offer.
    public Guid? OriginMessageId { get; set; }

    /// The Adventure, when it was already resolved. Never mutates the
    /// conversation's own scope — it belongs to this action only.
    public Guid? AdventureId { get; set; }

    /// SideQuest's own resolved geography. Never provider content.
    public string? Destination { get; set; }

    /// yyyy-MM-dd, when a day was already settled.
    public string? Date { get; set; }

    /// Which card, for a retryable add.
    public string? OptionKey { get; set; }

    /// Reused verbatim on resume, so a retry cannot mint a second proposal.
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
}

public static class GlunoPendingActions
{
    /// Long enough to answer after reading, short enough that "ja" tomorrow
    /// morning is a new conversation rather than a resumed one.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// The action, or null when there is none worth honouring. Expiry is
    /// checked here so no caller can forget it.
    public static GlunoPendingAction? Usable(GlunoPendingAction? action, DateTime nowUtc)
        => action == null || string.IsNullOrEmpty(action.Type) || action.ExpiresAtUtc <= nowUtc
            ? null
            : action;

    public static GlunoPendingAction WithLifetime(GlunoPendingAction action, DateTime nowUtc)
    {
        action.CreatedAtUtc = nowUtc;
        action.ExpiresAtUtc = nowUtc + Lifetime;
        return action;
    }
}

/// <summary>
/// Recognises the short phrases that mean "yes, do that".
///
/// These must never be interpreted in isolation while something is pending:
/// "Ja det blir bra" after an offer is an answer to the offer, and sending it
/// to the model to reinterpret the conversation is the production failure this
/// replaces.
///
/// CONSERVATIVE ON PURPOSE. A phrase with a question word in it is a question,
/// a phrase with a negation is a no, and anything longer than a few words is a
/// new request. All of those fall through to the ordinary pipeline — resuming
/// an action on a message that was not a yes is worse than asking again.
/// </summary>
public static class GlunoFollowUps
{
    private const int MaxWords = 5;

    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "ja", "japp", "jo", "jajaman", "yes", "yep", "ok", "okej", "okay",
        "nu", "nuda", "kor", "garna", "perfekt", "perfect", "sure",
    };

    private static readonly string[] Phrases =
    [
        "ja tack", "det blir bra", "later bra", "det later bra", "gor det",
        "kor pa", "kor igang", "lagg in det", "gor sa", "nu da",
        "yes please", "sounds good", "do it", "go ahead", "put it in", "do that",
    ];

    private static readonly string[] Questions =
    [
        "vad", "var", "nar", "hur", "varfor", "vilken", "vilket", "vilka",
        "what", "where", "when", "how", "why", "which", "who",
    ];

    private static readonly string[] Negations =
    [
        "nej", "inte", "aldrig", "no", "not", "dont", "don", "never", "nah",
    ];

    public static bool IsResumptive(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = Normalise(message);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || words.Length > MaxWords) return false;

        // A question is a question and a no is a no, whatever else the
        // sentence contains.
        if (words.Any(word => Questions.Contains(word, StringComparer.Ordinal))) return false;
        if (words.Any(word => Negations.Contains(word, StringComparer.Ordinal))) return false;

        if (Exact.Contains(text)) return true;

        var padded = " " + text + " ";
        if (Phrases.Any(phrase => padded.Contains(" " + phrase + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        // A short sentence that OPENS with a yes is a yes: "ja det blir bra",
        // "yes exactly". The guards above already removed questions and noes.
        return words[0] is "ja" or "jo" or "yes";
    }

    /// Lowercase, accent-folded, punctuation stripped — "Nudå?" and "nuda"
    /// have to be the same token.
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
