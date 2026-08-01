using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What the user is actually asking for.
///
/// The list is closed and exhaustive on purpose: every message lands on exactly
/// one of these, and <see cref="Unclear"/> is a real answer rather than a
/// dumping ground — it is what triggers a clarifying question instead of a
/// confident guess.
/// </summary>
public enum GlunoIntent
{
    /// "Is Nice worth it in August?" — knowledge, no plan changes.
    GeneralTravelQuestion,
    /// "Where should we go in October?" — no destination decided yet.
    DestinationRecommendation,
    /// "Find us a seafood place near the hotel." — needs a place provider.
    PlaceRecommendation,
    /// "What's missing from the trip?" — read the plan, report.
    TripReview,
    /// "Plan Saturday for us." — a day with nothing in it.
    PlanEmptyDay,
    /// "Friday feels thin, can you improve it?" — a day that exists.
    ImproveExistingDay,
    /// "Plan the whole week." — several days at once.
    BuildFullItinerary,
    MoveActivity,
    AddActivity,
    ChangeAdventureDates,
    /// "How do I add a photo?" — about the app, never about travel.
    SideQuestHelp,
    /// "Open the packing list." — take me somewhere.
    NavigationRequest,
    /// "We're vegetarian." — remember this.
    PreferenceUpdate,
    /// "Forget what I said about the budget."
    ForgetPreference,
    /// "The second one." — only meaningful against what was just said.
    FollowUpClarification,
    /// "Yes, do that." / "No, not that one."
    ConfirmationOrRejection,
    Unclear,
}

/// <summary>How far the question reaches.</summary>
public enum GlunoIntentScope
{
    /// No Adventure involved — general knowledge or app help.
    Global,
    /// The whole Adventure.
    Trip,
    /// One date.
    Day,
    /// One Activity.
    Activity,
}

/// <summary>
/// The classification of one user message, and the budget it earns.
///
/// The four booleans at the bottom are the point of this type. They are what
/// stops "how do I add a photo?" from loading a trip context, calling
/// Tripadvisor, computing a route matrix and running a schedule engine — work
/// that costs money and seconds and cannot possibly improve the answer.
/// </summary>
public sealed record GlunoIntentResult
{
    public required GlunoIntent PrimaryIntent { get; init; }

    /// <summary>
    /// 0–1. Below <see cref="GlunoIntentRouter.LowConfidence"/> the router is
    /// guessing, and the strategy widens what it loads rather than acting on a
    /// coin flip.
    /// </summary>
    public required double Confidence { get; init; }

    public required GlunoIntentScope Scope { get; init; }

    /// yyyy-MM-dd, resolved against the Adventure's own dates. Null when the
    /// message named no day.
    public string? ReferencedDate { get; init; }

    /// A REAL Activity id, resolved from the conversation or the plan. Never a
    /// guess — see <see cref="GlunoReferenceResolver"/>.
    public Guid? ReferencedActivityId { get; init; }

    /// Namespaced provider id ("tripadvisor:12345") of a place already
    /// discussed. Never a place the user has not been shown.
    public string? ReferencedPlaceId { get; init; }

    /// Needs data that goes stale — weather, opening hours, live availability.
    public required bool RequiresCurrentData { get; init; }

    /// Needs a place provider. False for every app-help and plan-reading
    /// question, which is what keeps Tripadvisor out of "how do I invite
    /// someone?".
    public required bool RequiresExternalSearch { get; init; }

    /// The user is asking for a CHANGE. When false, producing a proposal is
    /// itself a bug: they asked a question and got an offer to edit their trip.
    public required bool ExpectsProposal { get; init; }

    /// The message cannot be acted on as written.
    public required bool RequiresClarification { get; init; }

    /// A concrete question to ask, when clarification is needed. Never "tell me
    /// more" — see <see cref="GlunoReferenceResolver"/>.
    public string? ClarificationQuestion { get; init; }

    /// Why it classified this way. Diagnostics and evals only; never shown.
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Classifies a message before Gluno picks any tool.
///
/// WHY THIS IS NOT THE MODEL'S JOB. It could be — but then every turn pays for
/// a model round before we know whether the turn needs one, and the routing
/// decision becomes something no test can pin down. Routing is also exactly the
/// kind of decision where being wrong is expensive in both directions: an app
/// help question that runs the planning pipeline wastes seconds and money, and
/// a planning request classified as chat produces a confident answer with no
/// data behind it.
///
/// So it is deterministic, bilingual, and every case below is pinned by an
/// eval. The model still decides everything downstream; this only decides what
/// it is allowed to reach for.
///
/// The classifier is deliberately conservative about
/// <see cref="GlunoIntentResult.ExpectsProposal"/>: a question stays a question
/// unless the user used a verb that asks for a change. Offering to edit
/// somebody's trip because they asked what time a museum opens is worse than
/// missing an edit they wanted, because they can always ask again.
/// </summary>
public static class GlunoIntentRouter
{
    /// Below this the router is not confident enough to narrow the workflow.
    public const double LowConfidence = 0.45;

    /// <summary>
    /// Words that ask for a CHANGE rather than an answer.
    ///
    /// This list is the difference between an assistant and a nuisance. Without
    /// it every question about the trip ends with an unsolicited proposal card.
    /// </summary>
    private static readonly string[] ChangeVerbs =
    [
        // Swedish
        "lagg till", "lagg in", "boka in", "planera", "flytta", "byt", "andra",
        "ta bort", "stryk", "skjut", "fyll", "gor om", "satt in", "schemalagg",
        // English
        "add", "put", "book", "plan", "move", "swap", "change", "remove",
        "delete", "reschedule", "shift", "fill", "redo", "schedule", "set up",
    ];

    private static readonly string[] HelpMarkers =
    [
        // Swedish — about the APP, not the trip.
        "i appen", "sidequest", "knapp", "knappen", "var hittar", "hur gor jag",
        "hur lagger jag", "hur andrar jag", "vart klickar", "funkar det", "fungerar det",
        "installning", "installningar", "notis", "notiser", "inbjudan",
        // Inflected forms too — "bjuder in" is what people actually type, and
        // whole-word matching on the infinitive finds none of them.
        "bjuda in", "bjuder in", "bjud in", "appen", "i sidequest",
        "packlista", "packlistan", "utgifter", "bildspel", "slideshow",
        "dokument", "medlem", "medlemmar",
        // English
        "in the app", "button", "where do i find", "how do i", "how can i",
        "setting", "settings", "notification", "notifications", "invite",
        "packing list", "expenses", "documents", "member", "members",
    ];

    private static readonly string[] NavigationMarkers =
    [
        "oppna", "visa mig", "ta mig till", "ga till", "navigera",
        "open", "take me to", "show me the", "go to", "jump to",
    ];

    private static readonly string[] PreferenceMarkers =
    [
        "vi ar", "jag ar", "vi vill", "vi foredrar", "vi gillar", "vi ogillar",
        "vi har", "vi tanker", "kom ihag", "vi brukar", "vi taler inte", "vi undviker",
        "we are", "i am", "we prefer", "we like", "we don't like", "we dont like",
        "we have", "remember that", "we usually", "we avoid", "we're",
    ];

    private static readonly string[] ForgetMarkers =
    [
        "glom", "strunta i det jag sa", "ignorera det jag sa", "ta bort det jag sa",
        "forget", "ignore what i said", "scratch that", "never mind that",
    ];

    private static readonly string[] ConfirmMarkers =
    [
        "ja tack", "ja gor det", "kor pa", "kor", "det later bra", "gor det", "perfekt",
        "yes please", "yes do that", "go ahead", "sounds good", "do it", "perfect",
    ];

    private static readonly string[] RejectMarkers =
    [
        "nej tack", "nej", "inte den", "hellre inte", "skippa den", "inte den dar",
        "no thanks", "no", "not that one", "rather not", "skip that", "drop that",
    ];

    private static readonly string[] ReviewMarkers =
    [
        "vad saknas", "hur ser resan ut", "kolla igenom", "granska", "ser det bra ut",
        "nagot jag missat", "vad tycker du om resan", "gar det ihop",
        "what's missing", "whats missing", "how does the trip look", "review",
        "look over", "does it look ok", "anything i missed", "does it work",
    ];

    private static readonly string[] PlaceMarkers =
    [
        "restaurang", "restauranger", "ata", "ate", "lunch", "middag", "frukost",
        "kafe", "bar", "museum", "museer", "sevardhet", "sevardheter", "strand",
        "hotell", "boende", "rekommendera", "tips pa", "forslag pa", "hitta",
        "restaurant", "restaurants", "eat", "dinner", "lunch", "breakfast",
        "cafe", "coffee", "bar", "museum", "museums", "attraction", "attractions",
        "beach", "hotel", "recommend", "suggest", "find me", "find us", "where should we eat",
    ];

    private static readonly string[] DestinationMarkers =
    [
        "vart ska vi", "vart borde vi", "vilket land", "vilken stad", "vart aker",
        "where should we go", "which country", "which city", "where to travel",
        "destination", "resmal",
    ];

    private static readonly string[] ItineraryMarkers =
    [
        "hela resan", "hela veckan", "alla dagar", "hela vistelsen", "veckoplan",
        "whole trip", "whole week", "all the days", "entire stay", "full itinerary",
        "every day",
    ];

    private static readonly string[] DateChangeMarkers =
    [
        "andra datum", "flytta resan", "byta datum", "forlanga resan", "korta resan",
        "change the dates", "move the trip", "extend the trip", "shorten the trip",
        "different dates",
    ];

    private static readonly string[] MoveMarkers =
    [
        "flytta", "skjut", "byt dag", "flytta tillbaka", "lagg om",
        "move", "shift", "reschedule", "move back", "push",
    ];

    /// Pronouns and ordinals that only mean something against what was just
    /// said. Their presence is the strongest signal a message is a follow-up.
    private static readonly string[] AnaphoraMarkers =
    [
        "den andra", "den forsta", "den tredje", "den dar", "den har", "den",
        "det dar", "samma", "den sista", "dom", "de dar",
        "the second", "the first", "the third", "that one", "this one",
        "the last one", "the other", "same", "it", "them",
    ];

    public static GlunoIntentResult Classify(GlunoIntentInput input)
    {
        var text = Normalise(input.Message);
        var signals = new List<string>();

        if (text.Length == 0)
        {
            return new GlunoIntentResult
            {
                PrimaryIntent = GlunoIntent.Unclear,
                Confidence = 1,
                Scope = GlunoIntentScope.Global,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = true,
                Signals = ["empty"],
            };
        }

        var scores = new Dictionary<GlunoIntent, double>();
        void Score(GlunoIntent intent, double weight, string signal)
        {
            if (weight <= 0) return;
            scores[intent] = scores.GetValueOrDefault(intent) + weight;
            signals.Add(signal);
        }

        var wantsChange = ContainsAny(text, ChangeVerbs);
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // ── App help wins outright when it matches ────────────────────────
        //
        // Deliberately scored above everything else. "How do I add an
        // activity?" contains "add" and "activity" and would otherwise look
        // like a planning request — the failure mode is Gluno proposing a
        // change when the user only wanted to be told where the button is.
        if (ContainsAny(text, HelpMarkers)) Score(GlunoIntent.SideQuestHelp, 5, "help_marker");
        if (ContainsAny(text, NavigationMarkers)) Score(GlunoIntent.NavigationRequest, 4, "navigation_marker");

        if (ContainsAny(text, ForgetMarkers)) Score(GlunoIntent.ForgetPreference, 6, "forget_marker");

        // A short bare yes/no is an answer to what Gluno just asked, not a new
        // request. Length matters: "no, we don't want a car" is a preference.
        if (wordCount <= 4)
        {
            if (ContainsAny(text, ConfirmMarkers)) Score(GlunoIntent.ConfirmationOrRejection, 5, "confirm_short");
            if (ContainsAny(text, RejectMarkers)) Score(GlunoIntent.ConfirmationOrRejection, 5, "reject_short");
        }

        if (ContainsAny(text, PreferenceMarkers) && !wantsChange)
            Score(GlunoIntent.PreferenceUpdate, 3, "preference_marker");

        if (ContainsAny(text, ReviewMarkers)) Score(GlunoIntent.TripReview, 5, "review_marker");
        if (ContainsAny(text, DateChangeMarkers)) Score(GlunoIntent.ChangeAdventureDates, 5, "date_change_marker");
        if (ContainsAny(text, DestinationMarkers)) Score(GlunoIntent.DestinationRecommendation, 4, "destination_marker");
        if (ContainsAny(text, ItineraryMarkers) && wantsChange) Score(GlunoIntent.BuildFullItinerary, 5, "itinerary_marker");

        if (ContainsAny(text, MoveMarkers) && input.HasTrip) Score(GlunoIntent.MoveActivity, 4, "move_marker");

        if (ContainsAny(text, PlaceMarkers))
        {
            Score(GlunoIntent.PlaceRecommendation, wantsChange ? 2 : 3.5, "place_marker");
            if (wantsChange) Score(GlunoIntent.AddActivity, 2.5, "place_and_change");
        }

        // ── Day planning ──────────────────────────────────────────────────
        var referencedDate = ResolveDate(text, input);

        if (wantsChange && input.HasTrip && (referencedDate != null || ContainsAny(text, ["dagen", "the day", "dag ", "day "])))
        {
            // Planning an EMPTY day and improving a full one are different
            // workflows — one generates, the other critiques — so the router
            // separates them using what is actually on the day rather than
            // what the sentence sounds like.
            var hasActivities = referencedDate != null
                && input.ActivityCountByDate.TryGetValue(referencedDate, out var count)
                && count > 0;

            Score(hasActivities ? GlunoIntent.ImproveExistingDay : GlunoIntent.PlanEmptyDay, 4.5,
                hasActivities ? "improve_day" : "plan_empty_day");
        }

        if (wantsChange && !scores.ContainsKey(GlunoIntent.MoveActivity) && input.HasTrip)
            Score(GlunoIntent.AddActivity, 1.5, "change_verb");

        // ── Follow-ups ────────────────────────────────────────────────────
        //
        // A short message full of pronouns, right after Gluno offered
        // something, is the single most common shape in this chat. Treating it
        // as a fresh question is how an assistant ends up re-running a search
        // it already ran.
        var hasAnaphora = ContainsAnyWord(text, AnaphoraMarkers);
        if (hasAnaphora && input.HasRecentContext)
        {
            Score(GlunoIntent.FollowUpClarification, wordCount <= 12 ? 4 : 2, "anaphora");
        }

        if (scores.Count == 0)
        {
            // Nothing matched. A question mark or a question word still makes
            // it a question; anything else is genuinely unclear.
            var looksLikeAQuestion = input.Message.Contains('?')
                || ContainsAny(text, ["vad", "var", "nar", "hur", "vilken", "vilket", "varfor",
                    "what", "where", "when", "how", "which", "why", "is ", "are ", "can "]);

            Score(looksLikeAQuestion ? GlunoIntent.GeneralTravelQuestion : GlunoIntent.Unclear,
                looksLikeAQuestion ? 2 : 1,
                looksLikeAQuestion ? "question_shape" : "no_signal");
        }

        var best = scores.OrderByDescending(pair => pair.Value)
            .ThenBy(pair => (int)pair.Key)
            .First();

        var total = scores.Values.Sum();
        var confidence = Math.Round(Math.Clamp(best.Value / Math.Max(total, 0.0001), 0, 1), 3);

        var intent = best.Key;

        return new GlunoIntentResult
        {
            PrimaryIntent = intent,
            Confidence = confidence,
            Scope = ResolveScope(intent, referencedDate, input),
            ReferencedDate = referencedDate,
            RequiresCurrentData = NeedsCurrentData(intent),
            RequiresExternalSearch = NeedsExternalSearch(intent),
            ExpectsProposal = ProducesProposal(intent),
            RequiresClarification = intent == GlunoIntent.Unclear,
            Signals = signals,
        };
    }

    /// <summary>
    /// Which intents may spend money on a place provider.
    ///
    /// Everything about the app, everything about the plan the user already
    /// has, and every preference update answers from data SideQuest already
    /// holds. Only the three that genuinely need places outside SideQuest get
    /// to call one.
    /// </summary>
    private static bool NeedsExternalSearch(GlunoIntent intent) => intent switch
    {
        GlunoIntent.PlaceRecommendation => true,
        GlunoIntent.PlanEmptyDay => true,
        GlunoIntent.BuildFullItinerary => true,
        // Improving a day usually means reordering what is there. It may
        // search, but it does not start by assuming it needs to.
        _ => false,
    };

    /// Data that is only true right now: weather, opening hours, whether a
    /// place still exists.
    private static bool NeedsCurrentData(GlunoIntent intent) => intent switch
    {
        GlunoIntent.PlanEmptyDay => true,
        GlunoIntent.ImproveExistingDay => true,
        GlunoIntent.BuildFullItinerary => true,
        GlunoIntent.PlaceRecommendation => true,
        GlunoIntent.TripReview => true,
        _ => false,
    };

    /// <summary>
    /// Which intents are ASKING FOR A CHANGE.
    ///
    /// Note what is missing: TripReview, GeneralTravelQuestion,
    /// PlaceRecommendation, SideQuestHelp. Someone asking what is missing from
    /// their trip wants to be told, not handed an edit to approve.
    /// </summary>
    private static bool ProducesProposal(GlunoIntent intent) => intent switch
    {
        GlunoIntent.PlanEmptyDay => true,
        GlunoIntent.ImproveExistingDay => true,
        GlunoIntent.BuildFullItinerary => true,
        GlunoIntent.MoveActivity => true,
        GlunoIntent.AddActivity => true,
        GlunoIntent.ChangeAdventureDates => true,
        _ => false,
    };

    private static GlunoIntentScope ResolveScope(
        GlunoIntent intent, string? referencedDate, GlunoIntentInput input)
    {
        if (intent is GlunoIntent.SideQuestHelp or GlunoIntent.NavigationRequest
            or GlunoIntent.GeneralTravelQuestion or GlunoIntent.DestinationRecommendation)
        {
            return GlunoIntentScope.Global;
        }

        if (!input.HasTrip) return GlunoIntentScope.Global;
        if (intent == GlunoIntent.MoveActivity) return GlunoIntentScope.Activity;
        if (referencedDate != null) return GlunoIntentScope.Day;

        return GlunoIntentScope.Trip;
    }

    /// <summary>
    /// Finds the day a message is about.
    ///
    /// Handles an explicit ISO date, a weekday name resolved forward inside the
    /// Adventure's own range, and "idag"/"imorgon". Anything else returns null
    /// rather than a guess — planning the wrong day is worse than asking which.
    /// </summary>
    private static string? ResolveDate(string text, GlunoIntentInput input)
    {
        var iso = Regex.Match(text, @"\b(\d{4})-(\d{2})-(\d{2})\b");
        if (iso.Success) return iso.Value;

        if (input.TripStartDate is not { } start) return null;
        var end = input.TripEndDate ?? start.AddDays(30);

        if (ContainsAnyWord(text, ["idag", "today"])) return Clamp(input.Today, start, end);
        if (ContainsAnyWord(text, ["imorgon", "tomorrow"])) return Clamp(input.Today.AddDays(1), start, end);

        var weekdays = new (string[] Names, DayOfWeek Day)[]
        {
            (["mandag", "monday"], DayOfWeek.Monday),
            (["tisdag", "tuesday"], DayOfWeek.Tuesday),
            (["onsdag", "wednesday"], DayOfWeek.Wednesday),
            (["torsdag", "thursday"], DayOfWeek.Thursday),
            (["fredag", "friday"], DayOfWeek.Friday),
            (["lordag", "saturday"], DayOfWeek.Saturday),
            (["sondag", "sunday"], DayOfWeek.Sunday),
        };

        foreach (var (names, day) in weekdays)
        {
            // Stem matching, because Swedish inflects: "lördag", "lördagen",
            // "lördagens" are all the same day, and whole-word matching finds
            // none of them once the definite article is attached.
            if (!ContainsAnyStem(text, names)) continue;

            // The first matching weekday inside the Adventure. A trip spanning
            // two Saturdays is genuinely ambiguous — the reference resolver
            // asks about that rather than this classifier silently picking one.
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (date.DayOfWeek == day) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string Clamp(DateOnly value, DateOnly start, DateOnly end)
        => (value < start ? start : value > end ? end : value)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool ContainsAny(string text, IReadOnlyList<string> needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    /// <summary>
    /// A token that STARTS with one of the stems and is at most three letters
    /// longer.
    ///
    /// The bound matters: an unbounded prefix match turns "fre" into a match
    /// for "fredagsmys" and worse. Three letters covers Swedish definite and
    /// genitive endings ("-en", "-ens") without reaching into unrelated words.
    /// </summary>
    private static bool ContainsAnyStem(string text, IReadOnlyList<string> stems)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token =>
            stems.Any(stem =>
                token.StartsWith(stem, StringComparison.Ordinal) && token.Length <= stem.Length + 3));

    /// <summary>
    /// Whole-word matching, for needles short enough to appear inside other
    /// words. "den" inside "denna" is not a reference to anything, and matching
    /// it as one sends the whole turn down the wrong path.
    /// </summary>
    private static bool ContainsAnyWord(string text, IReadOnlyList<string> needles)
    {
        var padded = " " + text + " ";
        return needles.Any(needle => padded.Contains(" " + needle + " ", StringComparison.Ordinal)
            || padded.Contains(" " + needle + ",", StringComparison.Ordinal)
            || padded.Contains(" " + needle + ".", StringComparison.Ordinal));
    }

    /// Lowercase, accent-folded, single-spaced. "Ändra" and "andra" have to be
    /// the same token or half the Swedish vocabulary misses — and yes, that
    /// means "ändra" and "andra" collide, which is why every rule that uses
    /// them also looks at a change verb or a noun beside it.
    internal static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) || character == '-' ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Everything the router needs that is not the message itself. Passed in rather
/// than fetched so the classifier stays pure and testable.
/// </summary>
public sealed class GlunoIntentInput
{
    public required string Message { get; init; }
    public bool HasTrip { get; init; }
    public DateOnly? TripStartDate { get; init; }
    public DateOnly? TripEndDate { get; init; }
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// How many Activities sit on each date, keyed yyyy-MM-dd. Distinguishes
    /// "plan this empty day" from "improve this day".
    public IReadOnlyDictionary<string, int> ActivityCountByDate { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// True when the previous turn offered something a pronoun could refer to.
    public bool HasRecentContext { get; init; }
}
