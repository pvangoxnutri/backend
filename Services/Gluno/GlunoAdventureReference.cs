using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

public enum GlunoAdventureMatch
{
    /// The question is not about a particular Adventure at all.
    NotApplicable,
    /// Exactly one Adventure is clearly meant.
    Resolved,
    /// Several fit. Ask, never guess.
    Ambiguous,
    /// The question names something, and none of their Adventures is it.
    NotFound,
}

/// <summary>
/// One Adventure as the resolver sees it.
///
/// Deliberately not the entity: matching must not depend on anything the user
/// cannot see. Every field here is something shown on their own screen.
/// </summary>
public sealed record GlunoAdventureCandidate
{
    public required Guid TripId { get; init; }
    public required string Title { get; init; }
    /// The trip-level destination — "España". Coarse, and often a country.
    public string Destination { get; init; } = string.Empty;
    public required DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    /// <summary>
    /// The cities stored against this trip's days.
    ///
    /// The signal that makes "when are we in Ronda?" answerable from a global
    /// chat: Ronda appears in one Adventure and no other, so the question names
    /// its own trip without naming it.
    /// </summary>
    public IReadOnlyList<string> StopLabels { get; init; } = Array.Empty<string>();
}

public sealed record GlunoAdventureResolution
{
    public static readonly GlunoAdventureResolution NotApplicable =
        new() { Outcome = GlunoAdventureMatch.NotApplicable };

    public GlunoAdventureMatch Outcome { get; init; }

    /// Set only when <see cref="Outcome"/> is Resolved.
    public Guid? TripId { get; init; }

    /// The Adventures worth asking about, when more than one fits.
    public IReadOnlyList<GlunoAdventureCandidate> Candidates { get; init; }
        = Array.Empty<GlunoAdventureCandidate>();

    /// Machine reason for telemetry: "exact_title", "unique_stop", "date_range".
    public string? Reason { get; init; }
}

/// <summary>
/// Works out which Adventure a message from a GLOBAL conversation is about.
///
/// THE PROBLEM THIS SOLVES. A global Gluno conversation has no trip, so no
/// route is loaded, so the model sees only the Adventure summary — title,
/// trip-level destination, dates. Asked which cities a Spain trip visits it
/// could answer "I only have España and 5–16 August" while SideQuest knew six
/// cities. Correct code, correct data, and the route never fetched, because
/// nothing had established WHICH trip the question was about.
///
/// So the question is read for a trip first. When exactly one Adventure is
/// clearly meant, that turn loads it in full. When several fit, the user is
/// asked — because picking the most recent when two are plausible is how
/// somebody gets a confident answer about the wrong holiday.
///
/// THE MODEL HAS NO PART IN THIS. It cannot produce a trip id and cannot pick
/// an Adventure; every candidate here came from the user's own memberships and
/// every match is a word comparison the backend performed.
///
/// MATCHING IS ON WORD BOUNDARIES over an accent-stripped form, with a bounded
/// suffix for Swedish inflection. "Nice" must not match "Venice"; "Spanien"
/// must match "Spanienresan". That bug class has been reintroduced repeatedly
/// in this codebase.
/// </summary>
public static class GlunoAdventureReferenceResolver
{
    /// <summary>
    /// Words too short or too common to identify a trip on their own.
    ///
    /// Without this "resa" matches every Adventure the user has, and the
    /// resolver reports ambiguity on a question that named nothing.
    /// </summary>
    private const int MinimumSignificantWord = 4;

    private static readonly string[] Noise =
    [
        "resa", "resan", "resor", "trip", "tour", "vacation", "holiday",
        "semester", "adventure", "aventyr", "vart", "var", "our", "the",
    ];

    /// <summary>
    /// Phrases that make a message about a trip at all.
    ///
    /// A message with no trip signal and no name resolves to NotApplicable
    /// rather than to an ambiguity — "what is SideQuest?" must not produce an
    /// Adventure chooser.
    /// </summary>
    private static readonly string[] TripWords =
    [
        "resa", "resan", "trip", "adventure", "aventyr", "stad", "stader",
        "city", "cities", "stopp", "stop", "rutt", "route", "dag", "day",
        "vi ", "we ", "our", "var ", "hotell", "hotel", "boka", "plan",
    ];

    /// <summary>
    /// Which Adventure the message means.
    /// </summary>
    /// <param name="lastDiscussed">
    /// The Adventure this conversation last settled on. A weak signal, used
    /// only to break a tie that nothing stronger resolved.
    /// </param>
    public static GlunoAdventureResolution Resolve(
        string message,
        IReadOnlyList<GlunoAdventureCandidate> trips,
        DateOnly today,
        Guid? lastDiscussed = null)
    {
        if (trips.Count == 0) return GlunoAdventureResolution.NotApplicable;

        var text = Normalise(message);

        // ── One Adventure, nothing to choose between ──────────────────────
        //
        // Checked before anything else. Asking "which Adventure?" of somebody
        // with one is a question whose answer is already on the screen.
        if (trips.Count == 1)
        {
            return new GlunoAdventureResolution
            {
                Outcome = GlunoAdventureMatch.Resolved,
                TripId = trips[0].TripId,
                Reason = "only_adventure",
            };
        }

        // ── Strongest signal first ────────────────────────────────────────
        //
        // Each tier is tried in full before the next: a message naming a title
        // outright must not be decided by a date that happens to overlap
        // something else.
        foreach (var (candidates, reason) in new (List<GlunoAdventureCandidate>, string)[]
        {
            (trips.Where(trip => MatchesTitleExactly(text, trip)).ToList(), "exact_title"),
            (trips.Where(trip => MatchesTitleWord(text, trip)).ToList(), "title_word"),
            (trips.Where(trip => MatchesStop(text, trip)).ToList(), "unique_stop"),
            (trips.Where(trip => MatchesDestination(text, trip)).ToList(), "destination"),
            (trips.Where(trip => MatchesDateRange(text, trip)).ToList(), "date_range"),
        })
        {
            if (candidates.Count == 1)
            {
                return new GlunoAdventureResolution
                {
                    Outcome = GlunoAdventureMatch.Resolved,
                    TripId = candidates[0].TripId,
                    Reason = reason,
                };
            }

            // Several match at the SAME strength. That is a real choice, and
            // the weaker signals below cannot break it honestly — a date that
            // narrows two equally-named trips is a coincidence, not an intent.
            if (candidates.Count > 1)
            {
                return new GlunoAdventureResolution
                {
                    Outcome = GlunoAdventureMatch.Ambiguous,
                    Candidates = candidates,
                    Reason = $"ambiguous_{reason}",
                };
            }
        }

        // ── The conversation already settled this ─────────────────────────
        //
        // Checked BEFORE the "is this about a trip at all" gate, and that
        // ordering is the whole fix for the reported bug. "Ser du nu?" names
        // no trip, no city and no date, and contains none of the trip words —
        // so the gate below rejected it and the turn ran with no Adventure,
        // one message after answering about Semester 2026.
        //
        // Following what the conversation last settled on is not a guess. It
        // was verified when it was stored, and it is verified again here by
        // being in `trips` at all — a trip since deleted, or one the user has
        // left, is not in that list.
        if (lastDiscussed is { } previous && trips.Any(trip => trip.TripId == previous))
        {
            return new GlunoAdventureResolution
            {
                Outcome = GlunoAdventureMatch.Resolved,
                TripId = previous,
                Reason = "last_discussed",
            };
        }

        // ── Nothing named, and nothing settled ────────────────────────────
        //
        // Is this even about a trip? "How do I change my password" is not, and
        // an Adventure chooser in front of it is pure friction.
        if (!MentionsAny(text, TripWords)) return GlunoAdventureResolution.NotApplicable;

        // Exactly one Adventure is happening right now and nothing else was
        // named. "What have we got on Friday" during a trip means that one.
        var active = trips.Where(trip => IsActive(trip, today)).ToList();

        if (active.Count == 1)
        {
            return new GlunoAdventureResolution
            {
                Outcome = GlunoAdventureMatch.Resolved,
                TripId = active[0].TripId,
                Reason = "only_active",
            };
        }

        // A trip question with no trip identified. Ask — choosing the most
        // recent when several are plausible is how somebody gets a confident
        // answer about the wrong holiday.
        return new GlunoAdventureResolution
        {
            Outcome = GlunoAdventureMatch.Ambiguous,
            Candidates = trips,
            Reason = "trip_question_no_name",
        };
    }

    // ── The signals ──────────────────────────────────────────────────────

    /// The whole title appears, as words. "Semester 2026" in a longer sentence.
    private static bool MatchesTitleExactly(string text, GlunoAdventureCandidate trip)
    {
        var title = Normalise(trip.Title);
        return title.Length >= MinimumSignificantWord && IndexOfWord(text, title) >= 0;
    }

    /// <summary>
    /// A significant word from the title appears.
    ///
    /// Noise words are excluded: "resan" is in half the titles somebody has,
    /// and matching on it makes every Adventure a candidate for every question.
    /// </summary>
    private static bool MatchesTitleWord(string text, GlunoAdventureCandidate trip)
        => SignificantWords(trip.Title).Any(word => IndexOfWord(text, word) >= 0);

    /// <summary>
    /// A city stored against one of the trip's days.
    ///
    /// What makes "when are we in Ronda?" work from a global chat. The stop
    /// names come from the same rows the weather screen reads.
    /// </summary>
    private static bool MatchesStop(string text, GlunoAdventureCandidate trip)
        => trip.StopLabels
            .Select(Normalise)
            .Where(label => label.Length >= MinimumSignificantWord)
            .Any(label => IndexOfWord(text, label) >= 0);

    private static bool MatchesDestination(string text, GlunoAdventureCandidate trip)
        => SignificantWords(trip.Destination).Any(word => IndexOfWord(text, word) >= 0);

    /// <summary>
    /// A date the message names falls inside the trip.
    ///
    /// Both an explicit range ("5–16 augusti") and a single date resolve here;
    /// either way the test is containment, so a trip that merely starts in the
    /// same month does not match.
    /// </summary>
    private static bool MatchesDateRange(string text, GlunoAdventureCandidate trip)
    {
        var end = trip.EndDate ?? trip.StartDate.AddYears(1);

        foreach (var date in DatesIn(text, trip.StartDate.Year))
        {
            if (date >= trip.StartDate && date <= end) return true;
        }

        return false;
    }

    /// <summary>
    /// Dates written in the message, in either language and either order.
    ///
    /// Read as adjacent TOKENS rather than one regex: a single pattern over
    /// "den 9 augusti" matches "den 9" first, yields no month, and consumes the
    /// day so the real date never matches.
    /// </summary>
    private static IEnumerable<DateOnly> DatesIn(string text, int year)
    {
        var tokens = System.Text.RegularExpressions.Regex
            .Matches(text, @"[a-z]+|\d{1,4}")
            .Select(match => match.Value)
            .ToList();

        for (var index = 0; index < tokens.Count - 1; index++)
        {
            var left = tokens[index];
            var right = tokens[index + 1];

            var dayText = char.IsDigit(left[0]) ? left : char.IsDigit(right[0]) ? right : null;
            var monthText = char.IsDigit(left[0]) ? right : char.IsDigit(right[0]) ? left : null;

            if (dayText == null || monthText == null) continue;
            if (!int.TryParse(dayText, out var day) || day is < 1 or > 31) continue;

            var month = MonthNumber(monthText);
            if (month == 0) continue;

            DateOnly parsed;
            try
            {
                parsed = new DateOnly(year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            yield return parsed;
        }
    }

    private static int MonthNumber(string word) => word switch
    {
        var w when w.StartsWith("jan") => 1,
        var w when w.StartsWith("feb") => 2,
        var w when w.StartsWith("mar") => 3,
        var w when w.StartsWith("apr") => 4,
        var w when w.StartsWith("maj") || w.StartsWith("may") => 5,
        var w when w.StartsWith("jun") => 6,
        var w when w.StartsWith("jul") => 7,
        var w when w.StartsWith("aug") => 8,
        var w when w.StartsWith("sep") => 9,
        var w when w.StartsWith("okt") || w.StartsWith("oct") => 10,
        var w when w.StartsWith("nov") => 11,
        var w when w.StartsWith("dec") => 12,
        _ => 0,
    };

    private static bool IsActive(GlunoAdventureCandidate trip, DateOnly today)
        => trip.StartDate <= today && (trip.EndDate == null || trip.EndDate >= today);

    private static IEnumerable<string> SignificantWords(string value)
        => Normalise(value)
            .Split([' ', '-', ',', '.', '/', '&', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= MinimumSignificantWord)
            .Where(word => !Noise.Contains(word, StringComparer.Ordinal));

    private static bool MentionsAny(string text, IReadOnlyList<string> phrases)
        => phrases.Any(phrase => text.Contains(Normalise(phrase), StringComparison.Ordinal));

    /// <summary>
    /// Swedish compounds a place name is routinely glued to.
    ///
    /// "Spanienresan", "Italienturen", "Málagasemestern" are one word, and the
    /// tail is five or six letters — past any inflection allowance. Listing the
    /// tails explicitly is what lets those match while "Rondavägen" does not:
    /// a street is not a trip.
    /// </summary>
    private static readonly string[] CompoundTails =
    [
        "resan", "resa", "resor", "turen", "tur", "semestern", "semester",
        "aventyret", "vistelsen", "besoket",
    ];

    /// <summary>
    /// Word-boundary search with a bounded inflection allowance.
    ///
    /// THE BOUNDARY BEFORE THE MATCH is what stops "Nice" matching "Venice" —
    /// the single most damaging failure this resolver can have, because it
    /// answers confidently about the wrong holiday.
    ///
    /// AFTER the match, three characters of inflection are allowed ("Ronda" →
    /// "Rondas"), plus one of the compound tails above. Anything else longer is
    /// a different word.
    /// </summary>
    private static int IndexOfWord(string text, string word)
    {
        if (word.Length == 0) return -1;

        var from = 0;

        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0) return -1;

            var startsClean = at == 0 || !char.IsLetterOrDigit(text[at - 1]);

            if (startsClean)
            {
                var after = at + word.Length;
                var extra = 0;

                while (after + extra < text.Length && char.IsLetter(text[after + extra])) extra++;

                if (extra <= 3) return at;

                // A longer tail is only allowed when it is a trip word. That
                // keeps "Spanienresan" while refusing "Rondavägen".
                var tail = text.Substring(after, extra);
                if (CompoundTails.Contains(tail, StringComparer.Ordinal)) return at;
            }

            from = at + 1;
        }

        return -1;
    }

    /// Lower-cased and accent-stripped, so "Málaga" matches "malaga".
    private static string Normalise(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
