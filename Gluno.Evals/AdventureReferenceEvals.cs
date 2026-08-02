using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for working out which Adventure a GLOBAL question is about.
///
/// THE BUG THESE EXIST FOR. A global conversation has no trip, so no route was
/// loaded, so the model saw only the Adventure summary — title, the trip-level
/// destination, dates. Asked which cities a Spain trip visits it answered "I
/// only have España and 5–16 August", about a trip SideQuest knew six cities
/// for. Correct code and correct data; nothing had established WHICH trip.
///
/// The invariant: one Adventure clearly meant resolves silently, several
/// plausible ones ask, and the conversation stays global either way.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class AdventureReferenceEvals
{
    private static readonly DateOnly Today = new(2026, 8, 6);

    private static GlunoAdventureCandidate Spain() => new()
    {
        TripId = Guid.NewGuid(),
        Title = "Semester 2026",
        Destination = "España",
        StartDate = new DateOnly(2026, 8, 5),
        EndDate = new DateOnly(2026, 8, 16),
        StopLabels = ["Málaga", "Ronda", "Gibraltar", "Tanger", "Sevilla", "Faro"],
    };

    private static GlunoAdventureCandidate Italy() => new()
    {
        TripId = Guid.NewGuid(),
        Title = "Italien i oktober",
        Destination = "Italia",
        StartDate = new DateOnly(2026, 10, 3),
        EndDate = new DateOnly(2026, 10, 12),
        StopLabels = ["Roma", "Firenze", "Venice"],
    };

    private static GlunoAdventureCandidate Nice() => new()
    {
        TripId = Guid.NewGuid(),
        Title = "Franska rivieran",
        Destination = "France",
        StartDate = new DateOnly(2027, 5, 1),
        EndDate = new DateOnly(2027, 5, 8),
        StopLabels = ["Nice", "Antibes"],
    };

    private static GlunoAdventureResolution Ask(
        string message, params GlunoAdventureCandidate[] trips)
        => GlunoAdventureReferenceResolver.Resolve(message, trips, Today);

    // ── Silent resolution ────────────────────────────────────────────────

    [Fact]
    public void An_exact_title_resolves_the_Adventure()
    {
        var spain = Spain();
        var result = Ask("Vilka städer ska vi till på Semester 2026?", spain, Italy());

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("exact_title", result.Reason);
    }

    [Fact]
    public void A_destination_word_resolves_the_Adventure()
    {
        var italy = Italy();
        var result = Ask("Hur ser rutten ut för Italia-resan?", Spain(), italy);

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(italy.TripId, result.TripId);
    }

    [Fact]
    public void A_city_that_only_one_Adventure_visits_resolves_it()
    {
        var spain = Spain();
        var result = Ask("När är vi i Ronda?", spain, Italy(), Nice());

        // Ronda is in exactly one trip, so the question names its own
        // Adventure without naming it.
        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("unique_stop", result.Reason);
    }

    [Fact]
    public void A_date_inside_only_one_Adventure_resolves_it()
    {
        var spain = Spain();
        var result = Ask("Analysera resan 9 augusti", spain, Italy(), Nice());

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("date_range", result.Reason);
    }

    [Fact]
    public void A_single_Adventure_is_never_asked_about()
    {
        var spain = Spain();
        var result = Ask("Vilka städer ska vi till?", spain);

        // Asking "which Adventure?" of somebody with one is a question whose
        // answer is already on their screen.
        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("only_adventure", result.Reason);
    }

    [Fact]
    public void The_only_Adventure_happening_now_resolves_a_bare_trip_question()
    {
        var spain = Spain();

        // Today is 6 August, inside the Spain trip and nowhere near the others.
        var result = Ask("Vad har vi för planer på resan?", spain, Italy(), Nice());

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("only_active", result.Reason);
    }

    [Fact]
    public void Swedish_inflection_still_resolves_the_Adventure()
    {
        var italy = Italy();

        // "Italia" inside "Italiaresan".
        var result = Ask("Vad gör vi på Italiaresan?", Spain(), italy);

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(italy.TripId, result.TripId);
    }

    // ── The substring bug ────────────────────────────────────────────────

    [Fact]
    public void Nice_does_not_match_Venice()
    {
        var nice = Nice();

        // Venice is a stop on the Italy trip; Nice is a stop on the France
        // trip. A substring check resolves this question to France, and the
        // user gets a confident answer about the wrong holiday.
        var result = Ask("Vad ska vi göra i Venice?", Italy(), nice);

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.NotEqual(nice.TripId, result.TripId);
    }

    [Fact]
    public void A_city_name_inside_a_longer_unrelated_word_does_not_match()
    {
        var result = Ask("Vi bor på Rondavägen hemma i Stockholm", Spain(), Italy());

        // A street is not a stop. It may still resolve — Spain is the only
        // trip running today — but never BECAUSE of "Ronda".
        Assert.NotEqual("unique_stop", result.Reason);
    }

    [Fact]
    public void Short_and_common_words_do_not_identify_a_trip()
    {
        // "resa" is in half of everybody's titles. Matching on it would make
        // every Adventure a candidate for every question.
        var result = Ask("Jag vill boka en resa", Spain(), Italy());

        // It may resolve on the only running trip; it must not resolve because
        // a noise word appeared in a title.
        Assert.DoesNotContain(result.Reason, new[] { "title_word", "exact_title", "destination" });
    }

    // ── Asking rather than guessing ──────────────────────────────────────

    [Fact]
    public void Several_plausible_Adventures_produce_a_choice()
    {
        // Nothing named, nothing active — three trips, all equally plausible.
        var result = GlunoAdventureReferenceResolver.Resolve(
            "Vilka städer ska vi till?",
            [Italy(), Nice()],
            new DateOnly(2026, 6, 1));

        Assert.Equal(GlunoAdventureMatch.Ambiguous, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Two_Adventures_matching_the_same_signal_are_never_guessed_between()
    {
        var first = Spain();
        var second = Spain() with { TripId = Guid.NewGuid(), Title = "Spanien igen" };

        // Both are in España. A weaker signal must not silently break the tie —
        // a date that happens to narrow it is a coincidence, not an intent.
        var result = Ask("Vad gör vi i España?", first, second);

        Assert.Equal(GlunoAdventureMatch.Ambiguous, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void A_question_that_is_not_about_a_trip_produces_nothing()
    {
        // An Adventure chooser in front of "how do I change my password" is
        // pure friction.
        foreach (var message in new[]
        {
            "Hur ändrar jag mitt lösenord?",
            "Vad är SideQuest?",
            "How do I add a photo?",
        })
        {
            var result = Ask(message, Italy(), Nice());

            Assert.Equal(GlunoAdventureMatch.NotApplicable, result.Outcome);
        }
    }

    [Fact]
    public void A_user_with_no_Adventures_produces_nothing()
    {
        Assert.Equal(GlunoAdventureMatch.NotApplicable, Ask("Vilka städer ska vi till?").Outcome);
    }

    // ── Priority ─────────────────────────────────────────────────────────

    [Fact]
    public void A_named_title_beats_a_date_that_happens_to_overlap()
    {
        var italy = Italy();

        // The date is inside Spain; the title names Italy. The stronger signal
        // is what the user actually said.
        var result = Ask("Vad gör vi på Italien i oktober den 9 augusti?", Spain(), italy);

        Assert.Equal(italy.TripId, result.TripId);
    }

    [Fact]
    public void A_named_city_beats_being_the_only_active_trip()
    {
        var italy = Italy();

        // Spain is happening today. The question names an Italian city.
        var result = Ask("Vad ska vi se i Firenze?", Spain(), italy);

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(italy.TripId, result.TripId);
    }

    // ── How the turn uses it ─────────────────────────────────────────────

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    [Fact]
    public void The_turn_resolves_an_Adventure_before_building_context()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var resolveAt = chat.IndexOf("await ResolveAdventureAsync(", StringComparison.Ordinal);
        var buildAt = chat.IndexOf("await _contextBuilder.BuildAsync(", StringComparison.Ordinal);

        Assert.True(resolveAt > 0);
        // Resolving afterwards would be too late: the context was already
        // built without a trip, which is the whole bug.
        Assert.True(resolveAt < buildAt);
    }

    [Fact]
    public void Only_a_conversation_with_no_trip_resolves_one_from_the_text()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // A trip-scoped conversation keeps its own scope; text matching must
        // never move somebody to a different Adventure mid-conversation.
        Assert.Contains("var resolvedTripId = scopeTripId ?? conversation.TripId;", chat);
        Assert.Contains("if (resolvedTripId == null)", chat);
    }

    [Fact]
    public void An_explicit_scope_wins_over_anything_read_from_the_text()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // scopeTripId comes from the Adventure header or a clarification the
        // user answered. Both are decisions; a text match is an inference.
        var index = chat.IndexOf("var resolvedTripId = scopeTripId ?? conversation.TripId;", StringComparison.Ordinal);
        var body = chat[index..(index + 500)];

        Assert.Contains("if (resolvedTripId == null)", body);
        Assert.Contains("resolvedTripId = adventureResolution.TripId;", body);
    }

    [Fact]
    public void The_whole_turn_loads_with_the_resolved_Adventure()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Not a half-variant that loads the route and leaves the rest empty:
        // the load plan and the context builder both take the same id.
        Assert.Contains("GlunoPlanningStrategy.For(\n            intent, resolvedTripId.HasValue, canEdit: true)", chat);
        Assert.Contains("userId, resolvedTripId, conversation.Id,", chat);
    }

    [Fact]
    public void The_conversation_is_never_rewritten_to_a_trip()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var index = chat.IndexOf("var resolvedTripId = scopeTripId ?? conversation.TripId;", StringComparison.Ordinal);
        var body = chat[index..(index + 1400)];

        // Turn-scoped only. A global conversation stays global, and the next
        // message resolves itself again from its own words.
        //
        // Matched on the ASSIGNMENT specifically — the block legitimately reads
        // conversation.TripId in a comparison and in a log line, and a looser
        // check would fail on those.
        Assert.DoesNotContain("conversation.TripId = ", body);
        Assert.DoesNotContain("conversation.TripId =\n", body);
    }

    [Fact]
    public void An_ambiguous_Adventure_asks_rather_than_guessing()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("adventureResolution.Outcome == GlunoAdventureMatch.Ambiguous", chat);
        Assert.Contains("AskWhichAdventureAsync(", chat);
    }

    [Fact]
    public void Candidates_are_read_fresh_from_the_database_every_turn()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoAdventureResolution> ResolveAdventureAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2600)];

        Assert.True(start > 0);
        // A renamed trip, a city added yesterday, a trip deleted an hour ago
        // and a revoked membership all have to be reflected now.
        Assert.Contains("AsNoTracking()", body);
        Assert.Contains("_db.TripDayLocations", body);
    }

    [Fact]
    public void Membership_is_the_query_rather_than_a_check_afterwards()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoAdventureResolution> ResolveAdventureAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 1200)];

        // A trip the user has left is simply not a candidate — there is no
        // branch where a non-member's trip is loaded and then rejected.
        Assert.Contains("_db.TripMembers", body);
        Assert.Contains("member.UserId == userId", body);
    }

    [Fact]
    public void The_diagnostics_carry_no_names_ids_or_dates()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var index = chat.IndexOf("[GLUNO] adventure scope", StringComparison.Ordinal);
        Assert.True(index > 0);

        var line = chat[index..(index + 450)];

        Assert.Contains("global=", line);
        Assert.Contains("resolution=", line);
        Assert.Contains("candidates=", line);
        Assert.DoesNotContain("Title", line);
        Assert.DoesNotContain("TripId}", line);
    }

    // ── History must not outrank the route ───────────────────────────────

    [Fact]
    public void The_prompt_makes_the_current_route_beat_an_older_answer()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        // The exact failure: a conversation containing "I only have España"
        // from before the Adventure was resolved, followed by a turn that has
        // the cities.
        Assert.Contains("The route in this turn's context is what is TRUE NOW", prompt);
        Assert.Contains("do not treat your own previous answer as", prompt);
    }

    [Fact]
    public void The_prompt_still_forbids_the_country_only_answer()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        Assert.Contains("\"I only have España and the dates\" is a bug, not an answer", prompt);
    }
}
