using System.Globalization;

namespace sidequest.backend.Services.Gluno;

public enum GlunoReferenceKind
{
    None,
    Activity,
    Place,
    Proposal,
    Date,
    DayLocation,
}

/// <summary>Where a referenced thing sits relative to an anchor.</summary>
public enum GlunoRelation
{
    None,
    /// "efter hotellet", "after the museum"
    After,
    /// "innan middagen", "before dinner"
    Before,
    /// "samma dag som", "the same day as"
    SameDay,
}

/// <summary>
/// One thing a message pointed at, resolved to something real.
///
/// <see cref="Id"/> is always a stable identity — an Activity's Guid, a
/// namespaced provider id, a proposal's Guid, an ISO date. Never a title, never
/// a position, never something reconstructed from prose.
/// </summary>
public sealed record GlunoResolvedReference(
    GlunoReferenceKind Kind,
    string Id,
    string Label)
{
    /// The expression that produced it ("den andra", "efter hotellet").
    public string? Phrase { get; init; }
}

public sealed class GlunoReferenceResolution
{
    public GlunoResolvedReference? Subject { get; init; }

    /// The thing the subject is positioned against, for "after the hotel".
    public GlunoResolvedReference? Anchor { get; init; }

    public GlunoRelation Relation { get; init; }

    /// yyyy-MM-dd when the message settled on a day.
    public string? Date { get; init; }

    /// <summary>
    /// True when the message pointed at something but more than one thing fits
    /// AND the choice changes the outcome.
    ///
    /// Note the second half. Two candidates that would produce the same answer
    /// are not ambiguous in any way worth a turn of the user's time.
    /// </summary>
    public bool IsAmbiguous { get; init; }

    /// A concrete question naming the actual candidates. Never "which one?".
    public string? Question { get; init; }

    /// The candidates, so the caller can render or name them.
    public IReadOnlyList<GlunoResolvedReference> Candidates { get; init; } = Array.Empty<GlunoResolvedReference>();

    /// True when the message clearly pointed at something and nothing matched —
    /// usually because it has since been deleted.
    public bool ReferentGone { get; init; }

    public static GlunoReferenceResolution Empty => new();
}

/// <summary>
/// Turns "the second one", "after the hotel", "on Friday" into real ids.
///
/// THE RULE THIS FILE EXISTS FOR: never guess an id. An Activity id or a
/// proposal id that is merely plausible is worse than no id at all, because the
/// resulting change looks completely legitimate — the right shape, the right
/// wording, applied to the wrong object. Everywhere below, an unresolvable
/// reference produces either nothing or a question, never a best guess.
///
/// The second rule: a deleted or superseded object is not a candidate. The
/// resolver is always handed the CURRENT plan and filters its own memory
/// against it, so "move it back" cannot resurrect an Activity somebody removed
/// two turns ago.
///
/// Everything here is deterministic and testable. Ordinals, pronouns and
/// prepositions are a small closed vocabulary in two languages — exactly the
/// kind of problem where a model would be slower, more expensive, and
/// occasionally confidently wrong.
/// </summary>
public static class GlunoReferenceResolver
{
    /// <summary>
    /// Ordinal expressions, mapped to a zero-based position in the list the
    /// user was last shown.
    /// </summary>
    private static readonly (string[] Phrases, int Index)[] Ordinals =
    [
        (["den forsta", "det forsta", "forsta", "the first", "first one"], 0),
        (["den andra", "det andra", "andra", "the second", "second one"], 1),
        (["den tredje", "det tredje", "tredje", "the third", "third one"], 2),
        (["den fjarde", "the fourth", "fourth one"], 3),
        // -1 means "the last one", resolved against the list length.
        (["den sista", "det sista", "sista", "the last", "last one"], -1),
    ];

    /// Bare pronouns: "den", "det", "it", "that one". They mean the single most
    /// recent thing, and only when there IS a single most recent thing.
    private static readonly string[] BarePronouns =
    [
        "den", "det", "denna", "detta", "den har", "den dar", "det dar",
        "it", "that", "this", "that one", "this one",
    ];

    private static readonly string[] AfterMarkers =
    [
        "efter", "after", "efterat", "following",
    ];

    private static readonly string[] BeforeMarkers =
    [
        "innan", "fore", "before", "prior to",
    ];

    private static readonly string[] SameDayMarkers =
    [
        "samma dag", "same day", "den dagen", "that day",
    ];

    /// "där" — the place we were just talking about.
    private static readonly string[] ThereMarkers = ["dar", "there", "pa platsen", "at that spot"];

    public static GlunoReferenceResolution Resolve(
        string message,
        GlunoWorkingState state,
        GlunoTripContext? trip,
        string language)
    {
        var text = GlunoIntentRouter.Normalise(message);
        if (text.Length == 0) return GlunoReferenceResolution.Empty;

        // Memory is filtered against the CURRENT plan first. Everything below
        // then works on objects that are known to still exist.
        var liveActivities = LiveActivities(state, trip);
        var livePlaces = state.Recent.Places;

        var relation = ResolveRelation(text);
        var anchor = relation == GlunoRelation.None ? null : ResolveAnchor(text, liveActivities, state);

        var date = ResolveDate(text, state, trip, anchor);

        // ── Ordinal reference: "the second one" ───────────────────────────
        var ordinal = MatchOrdinal(text);
        if (ordinal != null)
        {
            // Ordinals point at the most recent LIST. Places are the usual
            // case (a search result), Activities the fallback.
            var list = livePlaces.Count > 0
                ? livePlaces.OrderBy(place => place.Position)
                    .Select(place => new GlunoResolvedReference(
                        GlunoReferenceKind.Place, place.ExternalId, place.Name) { Phrase = ordinal.Phrase })
                    .ToList()
                : liveActivities
                    .Select(activity => new GlunoResolvedReference(
                        GlunoReferenceKind.Activity, activity.Id.ToString(), activity.Title) { Phrase = ordinal.Phrase })
                    .ToList();

            if (list.Count == 0)
            {
                return new GlunoReferenceResolution
                {
                    Relation = relation,
                    Anchor = anchor,
                    Date = date,
                    ReferentGone = true,
                };
            }

            var index = ordinal.Index < 0 ? list.Count - 1 : ordinal.Index;
            if (index >= list.Count)
            {
                // "The fourth one" against three results. Asking is the only
                // honest move — picking the third would be inventing intent.
                return new GlunoReferenceResolution
                {
                    Relation = relation,
                    Anchor = anchor,
                    Date = date,
                    IsAmbiguous = true,
                    Candidates = list,
                    Question = AskWhich(list, language),
                };
            }

            return new GlunoReferenceResolution
            {
                Subject = list[index],
                Anchor = anchor,
                Relation = relation,
                Date = date,
                Candidates = list,
            };
        }

        // ── Named reference: the message says the title ───────────────────
        var byName = ResolveByName(text, liveActivities, livePlaces);
        if (byName.Count == 1)
        {
            return new GlunoReferenceResolution
            {
                Subject = byName[0],
                Anchor = anchor,
                Relation = relation,
                Date = date ?? DateOf(byName[0], liveActivities),
                Candidates = byName,
            };
        }

        if (byName.Count > 1)
        {
            // Two Activities genuinely called "Lunch". The choice changes which
            // one moves, so it is worth a question — and the question names
            // both so the user can answer in one word.
            return new GlunoReferenceResolution
            {
                Anchor = anchor,
                Relation = relation,
                Date = date,
                IsAmbiguous = true,
                Candidates = byName,
                Question = AskWhich(byName, language),
            };
        }

        // ── Bare pronoun: "it", "den" ─────────────────────────────────────
        if (ContainsWord(text, BarePronouns))
        {
            var recent = MostRecentSingle(state, liveActivities, livePlaces);

            if (recent.Count == 1)
            {
                return new GlunoReferenceResolution
                {
                    Subject = recent[0],
                    Anchor = anchor,
                    Relation = relation,
                    Date = date ?? DateOf(recent[0], liveActivities),
                    Candidates = recent,
                };
            }

            if (recent.Count > 1)
            {
                return new GlunoReferenceResolution
                {
                    Anchor = anchor,
                    Relation = relation,
                    Date = date,
                    IsAmbiguous = true,
                    Candidates = recent,
                    Question = AskWhich(recent, language),
                };
            }

            // A pronoun with nothing behind it. Either the conversation moved
            // on or the thing was deleted; both need a question, not a guess.
            return new GlunoReferenceResolution
            {
                Relation = relation,
                Anchor = anchor,
                Date = date,
                ReferentGone = true,
            };
        }

        // ── "there" — the last place or day location ──────────────────────
        if (ContainsWord(text, ThereMarkers))
        {
            var location = state.Recent.DayLocations.FirstOrDefault();
            if (location != null)
            {
                return new GlunoReferenceResolution
                {
                    Subject = new GlunoResolvedReference(
                        GlunoReferenceKind.DayLocation, location.Date, location.Label) { Phrase = "there" },
                    Relation = relation,
                    Anchor = anchor,
                    Date = date ?? location.Date,
                };
            }
        }

        return new GlunoReferenceResolution
        {
            Anchor = anchor,
            Relation = relation,
            Date = date,
        };
    }

    /// <summary>
    /// Filters remembered Activities against the plan as it is NOW.
    ///
    /// This is requirement five made concrete: an Activity the user deleted is
    /// not a candidate for "move it back", however recently it was discussed.
    /// </summary>
    private static List<MentionedActivity> LiveActivities(GlunoWorkingState state, GlunoTripContext? trip)
    {
        if (trip == null) return [];

        var live = trip.Activities.ToDictionary(activity => activity.Id, activity => activity);

        return state.Recent.Activities
            .Where(mentioned => live.ContainsKey(mentioned.Id))
            // The remembered title can be stale after a rename; the plan wins.
            .Select(mentioned => mentioned with
            {
                Title = live[mentioned.Id].Title,
                Date = Iso(live[mentioned.Id].Date),
            })
            .ToList();
    }

    private sealed record OrdinalMatch(int Index, string Phrase);

    private static OrdinalMatch? MatchOrdinal(string text)
    {
        foreach (var (phrases, index) in Ordinals)
        {
            var hit = phrases.FirstOrDefault(phrase => ContainsWord(text, [phrase]));
            if (hit != null) return new OrdinalMatch(index, hit);
        }

        return null;
    }

    private static GlunoRelation ResolveRelation(string text)
    {
        if (ContainsWord(text, SameDayMarkers)) return GlunoRelation.SameDay;
        if (ContainsWord(text, BeforeMarkers)) return GlunoRelation.Before;
        if (ContainsWord(text, AfterMarkers)) return GlunoRelation.After;
        return GlunoRelation.None;
    }

    /// <summary>
    /// The thing a relation is measured from: "after THE HOTEL", "before
    /// DINNER".
    ///
    /// Matched against Activities the conversation has touched and against the
    /// two categories people name generically — the hotel and the meal.
    /// </summary>
    private static GlunoResolvedReference? ResolveAnchor(
        string text, IReadOnlyList<MentionedActivity> activities, GlunoWorkingState state)
    {
        // A named Activity is the strongest anchor.
        foreach (var activity in activities)
        {
            if (ContainsPhrase(text, GlunoIntentRouter.Normalise(activity.Title)))
            {
                return new GlunoResolvedReference(
                    GlunoReferenceKind.Activity, activity.Id.ToString(), activity.Title);
            }
        }

        // "the hotel" — resolve by role rather than by name.
        if (ContainsWord(text, ["hotellet", "hotel", "the hotel", "boendet", "vart boende"]))
        {
            var hotel = state.Recent.Hotels.FirstOrDefault()
                ?? activities.FirstOrDefault(activity => activity.Role == "stay");

            if (hotel != null)
            {
                return new GlunoResolvedReference(GlunoReferenceKind.Activity, hotel.Id.ToString(), hotel.Title);
            }
        }

        // "dinner", "lunch" — the meal on the day in question.
        var mealWords = new[] { "middagen", "middag", "lunchen", "lunch", "frukosten", "frukost",
            "dinner", "the dinner", "the lunch", "breakfast" };

        if (ContainsWord(text, mealWords))
        {
            var meal = activities.FirstOrDefault(activity => activity.Role == "meal");
            if (meal != null)
            {
                return new GlunoResolvedReference(GlunoReferenceKind.Activity, meal.Id.ToString(), meal.Title);
            }
        }

        return null;
    }

    private static string? ResolveDate(
        string text, GlunoWorkingState state, GlunoTripContext? trip, GlunoResolvedReference? anchor)
    {
        // "the same day" borrows the anchor's date, or the last date discussed.
        if (ContainsWord(text, SameDayMarkers))
        {
            if (anchor is { Kind: GlunoReferenceKind.Activity }
                && Guid.TryParse(anchor.Id, out var anchorId)
                && trip?.Activities.FirstOrDefault(activity => activity.Id == anchorId) is { } anchorActivity)
            {
                return Iso(anchorActivity.Date);
            }

            return state.Recent.Dates.FirstOrDefault();
        }

        // An anchor with a date settles the day without anything being said.
        if (anchor is { Kind: GlunoReferenceKind.Activity }
            && Guid.TryParse(anchor.Id, out var id)
            && trip?.Activities.FirstOrDefault(activity => activity.Id == id) is { } activityWithDate)
        {
            return Iso(activityWithDate.Date);
        }

        return null;
    }

    /// Dates cross this boundary as strings, because everything downstream —
    /// the turn brief, the working state, the JSON payloads — speaks ISO.
    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Titles mentioned outright. Returns every match, because two Activities
    /// with the same name is a real situation that has to become a question
    /// rather than a coin flip.
    /// </summary>
    private static List<GlunoResolvedReference> ResolveByName(
        string text,
        IReadOnlyList<MentionedActivity> activities,
        IReadOnlyList<MentionedPlace> places)
    {
        var matches = new List<GlunoResolvedReference>();

        foreach (var activity in activities)
        {
            var title = GlunoIntentRouter.Normalise(activity.Title);
            // Two-character titles would match half the alphabet.
            if (title.Length < 3 || !ContainsPhrase(text, title)) continue;

            matches.Add(new GlunoResolvedReference(
                GlunoReferenceKind.Activity, activity.Id.ToString(), activity.Title) { Phrase = title });
        }

        foreach (var place in places)
        {
            var name = GlunoIntentRouter.Normalise(place.Name);
            if (name.Length < 3 || !ContainsPhrase(text, name)) continue;
            if (matches.Any(match => match.Label.Equals(place.Name, StringComparison.OrdinalIgnoreCase))) continue;

            matches.Add(new GlunoResolvedReference(
                GlunoReferenceKind.Place, place.ExternalId, place.Name) { Phrase = name });
        }

        return matches;
    }

    /// <summary>
    /// What a bare "it" could mean.
    ///
    /// Returns ONE candidate only when the last turn left exactly one thing on
    /// the table. Three restaurants and an Activity means "it" is ambiguous,
    /// and the caller asks.
    /// </summary>
    private static List<GlunoResolvedReference> MostRecentSingle(
        GlunoWorkingState state,
        IReadOnlyList<MentionedActivity> activities,
        IReadOnlyList<MentionedPlace> places)
    {
        // A pending proposal is the most likely referent right after Gluno
        // offered one — "apply it", "change it".
        var proposals = state.Recent.Proposals
            .Where(proposal => proposal.Status == "pending")
            .Select(proposal => new GlunoResolvedReference(
                GlunoReferenceKind.Proposal, proposal.Id.ToString(), proposal.Summary))
            .ToList();

        if (proposals.Count == 1) return proposals;

        if (places.Count == 1)
        {
            return [new GlunoResolvedReference(GlunoReferenceKind.Place, places[0].ExternalId, places[0].Name)];
        }

        if (places.Count == 0 && activities.Count == 1)
        {
            return [new GlunoResolvedReference(
                GlunoReferenceKind.Activity, activities[0].Id.ToString(), activities[0].Title)];
        }

        // Several things on the table. Hand them all back as candidates so the
        // question can name them.
        var candidates = places
            .Select(place => new GlunoResolvedReference(GlunoReferenceKind.Place, place.ExternalId, place.Name))
            .Concat(activities.Select(activity => new GlunoResolvedReference(
                GlunoReferenceKind.Activity, activity.Id.ToString(), activity.Title)))
            .Take(4)
            .ToList();

        return candidates;
    }

    private static string? DateOf(GlunoResolvedReference reference, IReadOnlyList<MentionedActivity> activities)
    {
        if (reference.Kind != GlunoReferenceKind.Activity) return null;
        if (!Guid.TryParse(reference.Id, out var id)) return null;

        return activities.FirstOrDefault(activity => activity.Id == id)?.Date;
    }

    /// <summary>
    /// A question that names the actual options.
    ///
    /// "Which one do you mean?" costs the user a turn and tells them nothing.
    /// "Do you mean Le Bistrot or the one at the harbour?" can be answered in
    /// one word.
    /// </summary>
    private static string AskWhich(IReadOnlyList<GlunoResolvedReference> candidates, string language)
    {
        var names = candidates.Take(3).Select(candidate => candidate.Label).ToList();
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        var joined = names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => swedish ? $"{names[0]} eller {names[1]}" : $"{names[0]} or {names[1]}",
            _ => swedish
                ? $"{string.Join(", ", names.Take(names.Count - 1))} eller {names[^1]}"
                : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}",
        };

        return swedish ? $"Menar du {joined}?" : $"Do you mean {joined}?";
    }

    private static bool ContainsWord(string text, IReadOnlyList<string> needles)
    {
        var padded = " " + text + " ";
        return needles.Any(needle => padded.Contains(" " + needle + " ", StringComparison.Ordinal));
    }

    /// A multi-word phrase, matched on token boundaries so "bar" cannot match
    /// inside "barcelona".
    private static bool ContainsPhrase(string text, string phrase)
    {
        if (phrase.Length == 0) return false;
        return (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal)
            || (" " + text + " ").Contains(" " + phrase + "s ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records what this turn put on the table, so the next one can point at it.
    /// </summary>
    public static void Remember(
        GlunoWorkingState state,
        IReadOnlyList<GlunoPlaceCard> places,
        IReadOnlyList<GlunoProposalRecordSummary> proposals,
        IReadOnlyList<GlunoActivityContext> touchedActivities,
        string? date)
    {
        for (var index = 0; index < places.Count; index++)
        {
            var place = places[index];
            GlunoRecentMentions.Promote(
                state.Recent.Places,
                new MentionedPlace(place.ExternalId, place.Name, place.Category)
                {
                    Latitude = place.Latitude,
                    Longitude = place.Longitude,
                    Position = index,
                    FetchedAtUtc = DateTime.UtcNow,
                },
                mentioned => mentioned.ExternalId,
                GlunoRecentMentions.MaxPlaces);
        }

        foreach (var proposal in proposals)
        {
            GlunoRecentMentions.Promote(
                state.Recent.Proposals,
                new MentionedProposal(proposal.Id, proposal.Kind, proposal.Summary, proposal.Status),
                mentioned => mentioned.Id.ToString(),
                GlunoRecentMentions.MaxProposals);
        }

        foreach (var activity in touchedActivities)
        {
            var role = ActivityRoles.FromCategory(activity.Category, activity.EndDate);
            var mentioned = new MentionedActivity(
                activity.Id, activity.Title, Iso(activity.Date), activity.Category, role)
            {
                Latitude = activity.Latitude,
                Longitude = activity.Longitude,
            };

            GlunoRecentMentions.Promote(
                state.Recent.Activities, mentioned, item => item.Id.ToString(), GlunoRecentMentions.MaxActivities);

            if (role == "stay")
            {
                GlunoRecentMentions.Promote(
                    state.Recent.Hotels, mentioned, item => item.Id.ToString(), GlunoRecentMentions.MaxHotels);
            }
        }

        if (date != null)
        {
            GlunoRecentMentions.Promote(state.Recent.Dates, date, value => value, GlunoRecentMentions.MaxDates);
        }
    }

}

/// <summary>Just enough of a proposal for working memory.</summary>
public sealed record GlunoProposalRecordSummary(Guid Id, string Kind, string Summary, string Status);
