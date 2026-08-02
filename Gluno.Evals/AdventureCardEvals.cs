using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the Adventure card actually existing when Gluno points at one.
///
/// THE BUG THESE EXIST FOR. In production Gluno wrote "Which Adventure is this
/// about? Pick Semester 2026 below and I'll have the whole day plan" — and no
/// card was rendered. Nothing had created a clarification: the message named no
/// trip and contained none of the trip words, so the resolver returned
/// NotApplicable, no card path fired, and the MODEL wrote the question. It
/// told the user to tap something that did not exist.
///
/// Two invariants come out of that. Text may never promise a choice the turn
/// did not produce. And a follow-up in a conversation that already settled on
/// an Adventure must keep it rather than asking again.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class AdventureCardEvals
{
    private static readonly DateOnly Today = new(2026, 8, 6);

    private static GlunoAdventureCandidate Trip(string title, int startMonth) => new()
    {
        TripId = Guid.NewGuid(),
        Title = title,
        Destination = "España",
        StartDate = new DateOnly(2026, startMonth, 5),
        EndDate = new DateOnly(2026, startMonth, 16),
    };

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    // ── The promise invariant ────────────────────────────────────────────

    [Theory]
    [InlineData("Väljer du Semester 2026 här nedanför får jag hela dagsplanen.")]
    [InlineData("Vilket äventyr gäller det? Välj nedan.")]
    [InlineData("Pick one of the options below.")]
    [InlineData("Tap Semester 2026 and I'll load the plan.")]
    [InlineData("Klicka på ett av alternativen.")]
    public void Text_that_points_at_a_button_is_detected(string text)
    {
        Assert.True(GlunoUiPromise.PromisesAChoice(text));
    }

    [Theory]
    [InlineData("Vilket Adventure gäller det?")]
    [InlineData("Which Adventure is this about?")]
    [InlineData("Ni är i Ronda den 9 augusti.")]
    [InlineData("You could choose a different day if that suits better.")]
    [InlineData("Det ligger nedanom byn.")]
    public void Ordinary_text_is_not_mistaken_for_a_promise(string text)
    {
        // "You could choose a different day" is a suggestion about the plan.
        // "Choose one below" is a claim about the screen. Only the second is a
        // promise, and conflating them would strip good sentences.
        Assert.False(GlunoUiPromise.PromisesAChoice(text));
    }

    [Fact]
    public void Only_the_promising_sentence_is_removed()
    {
        var trimmed = GlunoUiPromise.WithoutPromises(
            "Ni är i Ronda den 9 augusti. Välj nedan för att se mer.");

        // One bad clause must not cost an otherwise good answer.
        Assert.Equal("Ni är i Ronda den 9 augusti.", trimmed);
    }

    [Fact]
    public void An_answer_that_is_nothing_but_a_promise_becomes_empty()
    {
        var trimmed = GlunoUiPromise.WithoutPromises("Välj ett av alternativen nedan.");

        // The caller substitutes a plain question. An empty answer is better
        // than a false instruction.
        Assert.Equal(string.Empty, trimmed);
    }

    [Fact]
    public void The_turn_strips_a_promise_when_no_card_was_produced()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("GlunoUiPromise.PromisesAChoice(assistantText)", chat);
        Assert.Contains("answer promised a choice or explained its own plumbing", chat);
        // Trimmed rather than failed.
        Assert.Contains("GlunoUiPromise.WithoutPromises(assistantText)", chat);
    }

    [Fact]
    public void The_prompt_forbids_referring_to_buttons()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        Assert.Contains("NEVER refer to buttons, options or anything \"below\"", prompt);
        // Wrapped and indented in the prompt text, so whitespace is collapsed
        // before matching rather than the assertion being weakened.
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        Assert.Contains("SideQuest attaches the choices when it has them", flat);
        Assert.Contains("ask in one short sentence", flat);
    }

    // ── The follow-up that started it ────────────────────────────────────

    [Fact]
    public void A_follow_up_keeps_the_Adventure_the_conversation_settled_on()
    {
        var spain = Trip("Semester 2026", 8);
        var italy = Trip("Italien", 10);

        // The exact production message. It names no trip, no city and no date,
        // and contains none of the trip words.
        var result = GlunoAdventureReferenceResolver.Resolve(
            "Ser du nu?", [spain, italy], Today, lastDiscussed: spain.TripId);

        Assert.Equal(GlunoAdventureMatch.Resolved, result.Outcome);
        Assert.Equal(spain.TripId, result.TripId);
        Assert.Equal("last_discussed", result.Reason);
    }

    [Fact]
    public void The_settled_Adventure_is_consulted_before_the_trip_word_gate()
    {
        var resolver = Source("Services", "Gluno", "GlunoAdventureReference.cs");

        var settledAt = resolver.IndexOf("if (lastDiscussed is { } previous", StringComparison.Ordinal);
        var gateAt = resolver.IndexOf("if (!MentionsAny(text, TripWords))", StringComparison.Ordinal);

        Assert.True(settledAt > 0 && gateAt > 0);
        // The ordering IS the fix. "Ser du nu?" fails the gate, so checking it
        // first meant the turn ran with no Adventure one message after
        // answering about one.
        Assert.True(settledAt < gateAt);
    }

    [Fact]
    public void A_settled_Adventure_the_user_can_no_longer_see_is_ignored()
    {
        var italy = Trip("Italien", 10);
        var france = Trip("Frankrike", 11);
        var deleted = Guid.NewGuid();

        // TWO candidates, so the "only one Adventure" shortcut does not fire
        // and the stale reference is genuinely the thing under test.
        var result = GlunoAdventureReferenceResolver.Resolve(
            "Ser du nu?", [italy, france], Today, lastDiscussed: deleted);

        // Not in the candidate list, so not a candidate. The list comes from
        // the membership join, so this covers a deleted trip and a revoked
        // membership alike.
        Assert.NotEqual(GlunoAdventureMatch.Resolved, result.Outcome);
    }

    [Fact]
    public void A_named_Adventure_still_beats_the_settled_one()
    {
        var spain = Trip("Semester 2026", 8);
        var italy = Trip("Italien", 10);

        var result = GlunoAdventureReferenceResolver.Resolve(
            "Vad gör vi på Italien?", [spain, italy], Today, lastDiscussed: spain.TripId);

        // The weakest signal must never override what the user just said.
        Assert.Equal(italy.TripId, result.TripId);
    }

    [Fact]
    public void The_settled_Adventure_is_only_written_from_a_loaded_trip()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // context.Trip is a trip the builder resolved and loaded, which means
        // membership was already verified. The model has no way to produce a
        // trip id at all.
        Assert.Contains("if (context.Trip is { } settled && state.Recent.LastAdventureId != settled.Id)", chat);
        Assert.Contains("state.Recent.LastAdventureId = settled.Id;", chat);
    }

    [Fact]
    public void The_settled_Adventure_is_stored_even_on_an_unremarkable_turn()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var writeAt = chat.IndexOf("state.Recent.LastAdventureId = settled.Id;", StringComparison.Ordinal);
        var gateAt = chat.IndexOf("if (!significant) return;", StringComparison.Ordinal);

        Assert.True(writeAt > 0 && gateAt > 0);
        // A turn that answered about Semester 2026 and produced no places and
        // no proposals is not "significant" — and is exactly the turn whose
        // Adventure the next message means.
        Assert.True(writeAt < gateAt);
    }

    // ── The short contract ───────────────────────────────────────────────

    [Fact]
    public void The_Adventure_question_is_short_in_both_languages()
    {
        foreach (var language in new[] { "sv", "en" })
        {
            var question = GlunoClarificationBuilder.QuestionFor(
                GlunoClarificationTypes.Adventure, language);

            Assert.True(question.Split(' ').Length <= 6, question);
            // No scope talk, no explanation of what Gluno can and cannot see.
            foreach (var word in new[] { "kopplat", "scope", "context", "global", "tripId" })
            {
                Assert.DoesNotContain(word, question, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_card_and_the_message_carry_the_same_single_question()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var index = chat.IndexOf("var question = GlunoClarificationBuilder.QuestionFor(", StringComparison.Ordinal);
        var body = chat[index..(index + 1200)];

        // One string, used for both the message text and the card. Two
        // separately-written questions is how the same thing gets asked twice
        // in one turn.
        Assert.Contains("Text = question,", body);
        Assert.Contains("Question = question,", body);
    }

    // ── "Not sure yet" ───────────────────────────────────────────────────

    [Fact]
    public void The_Adventure_card_offers_a_way_past_the_question()
    {
        var choices = new[]
        {
            new TripChoice(Guid.NewGuid(), "Semester 2026", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)),
            new TripChoice(Guid.NewGuid(), "Italien", new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 12)),
        };

        var options = GlunoClarificationBuilder.WithNoAdventureOption(
            GlunoClarificationBuilder.TripOptions(choices, Today, "sv"), "sv");

        Assert.Equal("Vet inte än", options[^1].Label);
        Assert.Equal(GlunoClarificationBuilder.NoAdventureKey, options[^1].Value);
        // A fixed vocabulary token, not a trip id — so the continuation can
        // tell it apart and refuse to load any trip context.
        Assert.Equal(GlunoClarificationEntityTypes.Enum, options[^1].EntityType);
    }

    [Fact]
    public void The_way_out_exists_in_English_too()
    {
        var choices = new[]
        {
            new TripChoice(Guid.NewGuid(), "Semester 2026", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)),
        };

        var options = GlunoClarificationBuilder.WithNoAdventureOption(
            GlunoClarificationBuilder.TripOptions(choices, Today, "en"), "en");

        Assert.Equal("Not sure yet", options[^1].Label);
    }

    [Fact]
    public void A_search_result_does_not_get_the_way_out_appended()
    {
        var choices = new[]
        {
            new TripChoice(Guid.NewGuid(), "Semester 2026", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)),
        };

        var options = GlunoClarificationBuilder.TripOptions(choices, Today, "sv");

        // A search that found one Adventure should show that one. Adding an
        // escape hatch to a list the user just narrowed reads as the search
        // having failed.
        Assert.Single(options);
        Assert.DoesNotContain(options, option => option.Value == GlunoClarificationBuilder.NoAdventureKey);
    }

    [Fact]
    public void Choosing_not_sure_loads_no_trip_and_runs_no_model()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> ContinueWithoutAdventureAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 1800)];

        Assert.True(start > 0);
        // No context build, no route, no providers, no model round. A
        // one-line acknowledgement is the whole answer.
        Assert.DoesNotContain("_contextBuilder", body);
        Assert.DoesNotContain("SendCoreAsync", body);
        Assert.Contains("Okej — skriv vad du vill ha hjälp med ändå.", body);
        Assert.Contains("Fine — tell me what you'd like help with anyway.", body);
    }

    [Fact]
    public void Choosing_not_sure_is_routed_before_the_scope_is_read()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var notSureAt = chat.IndexOf(
            "if (option.Value == GlunoClarificationBuilder.NoAdventureKey)", StringComparison.Ordinal);
        var scopeAt = chat.IndexOf(
            "Guid? scopeTripId = option.EntityType == GlunoClarificationEntityTypes.Trip", StringComparison.Ordinal);

        Assert.True(notSureAt > 0 && scopeAt > 0);
        Assert.True(notSureAt < scopeAt);
    }

    // ── The card survives a reload ───────────────────────────────────────

    [Fact]
    public void A_message_carries_its_clarification_back_to_the_app()
    {
        var dtos = Source("Dtos", "GlunoDtos.cs");

        // Without this a reopened conversation shows the question with nothing
        // under it, and a one-tap answer becomes one the user has to retype.
        Assert.Contains("public GlunoClarificationDto? Clarification { get; set; }", dtos);
    }

    [Fact]
    public void History_loads_clarifications_alongside_proposals()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        Assert.Contains("_clarifications.ListForMessagesAsync(", controller);
        Assert.Contains("clarifications.GetValueOrDefault(m.Id)", controller);
    }

    [Fact]
    public void The_history_lookup_is_scoped_to_the_caller()
    {
        var service = Source("Services", "Gluno", "GlunoClarificationService.cs");

        var start = service.IndexOf("public async Task<IReadOnlyDictionary<Guid, GlunoClarification>> ListForMessagesAsync", StringComparison.Ordinal);
        var body = service[start..(start + 1400)];

        Assert.True(start > 0);
        // Scoped in the QUERY, like every other read here.
        Assert.Contains("row.UserId == userId", body);
        Assert.Contains("Include(row => row.Options)", body);
    }

    [Fact]
    public void The_app_restores_a_clarification_from_history()
    {
        var screen = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "mobile", "app", "gluno.tsx"));

        Assert.Contains("clarification: message.clarification ?? undefined,", screen);
    }

    [Fact]
    public void The_app_type_allows_a_clarification_on_a_message()
    {
        var types = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "mobile", "lib", "gluno.ts"));

        Assert.Contains("clarification?: GlunoClarification | null;", types);
    }

    // ── Asking still happens before anything expensive ───────────────────

    [Fact]
    public void The_Adventure_card_is_built_before_the_model_runs()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var askAt = chat.IndexOf("AskWhichAdventureAsync(", StringComparison.Ordinal);
        var modelAt = chat.IndexOf("RunModelAsync", StringComparison.Ordinal);

        Assert.True(askAt > 0);
        // The model must not first write a long explanation and then hope the
        // UI attaches a card.
        if (modelAt > 0) Assert.True(askAt < modelAt);
    }

    [Fact]
    public void The_Adventure_card_carries_real_options()
    {
        var choices = new[]
        {
            new TripChoice(Guid.NewGuid(), "Semester 2026", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)),
            new TripChoice(Guid.NewGuid(), "Italien", new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 12)),
        };

        var options = GlunoClarificationBuilder.WithNoAdventureOption(
            GlunoClarificationBuilder.TripOptions(choices, Today, "sv"), "sv");

        // Two real Adventures plus the way out. Every trip option points at a
        // verified id the backend produced.
        Assert.Equal(3, options.Count);
        Assert.Equal(2, options.Count(option => option.EntityType == GlunoClarificationEntityTypes.Trip));
        Assert.All(
            options.Where(option => option.EntityType == GlunoClarificationEntityTypes.Trip),
            option => Assert.NotNull(option.EntityId));
    }
}
