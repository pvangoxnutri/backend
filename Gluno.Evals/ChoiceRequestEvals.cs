using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for answering "give me something I can tap" with something tappable.
///
/// THE FAILURE THESE EXIST FOR. Somebody told Gluno to give them clickable
/// options. It replied that it could not put out buttons itself, that SideQuest
/// does that, and that the app was refusing to open an Adventure because the
/// conversation was not attached to one.
///
/// Every clause was wrong to say. Gluno is one feature of SideQuest, not a
/// model narrating the app it lives in — and the card it was explaining away
/// could have been built on that very turn: the Adventures existed, nothing
/// needed opening, and a global conversation shows its Adventures perfectly
/// well.
///
/// The branch that allowed it: NeedsAnAdventure returned false for a Global
/// scope, no card path fired, and the model answered. A model asked "can you
/// give me buttons?" will always answer in the first person about its own
/// abilities, so the only reliable fix is for it never to see the question.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ChoiceRequestEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    // ── Detecting the request ────────────────────────────────────────────

    [Theory]
    [InlineData("Ge mig alternativ jag kan klicka på")]
    [InlineData("Visa val")]
    [InlineData("Kan du ge knappar?")]
    [InlineData("Visa mina Adventures")]
    [InlineData("Ge mig resorna som alternativ")]
    [InlineData("Låt mig välja")]
    [InlineData("Gör dem klickbara")]
    public void Swedish_requests_for_something_tappable_are_caught(string message)
    {
        Assert.True(GlunoChoiceRequest.IsAskingForChoices(message));
    }

    [Theory]
    [InlineData("Let me choose")]
    [InlineData("Show me options")]
    [InlineData("Give me buttons")]
    [InlineData("Make them clickable")]
    [InlineData("Can I click on them?")]
    [InlineData("Give me the options please")]
    public void English_requests_for_something_tappable_are_caught(string message)
    {
        Assert.True(GlunoChoiceRequest.IsAskingForChoices(message));
    }

    [Theory]
    [InlineData("Vilken av dem tycker du att vi ska välja?")]
    [InlineData("Which restaurant should I pick?")]
    [InlineData("Vad ska vi göra i Ronda?")]
    [InlineData("Hur ser rutten ut?")]
    public void A_question_about_the_plan_is_not_a_request_for_an_interface(string message)
    {
        // "Which should I pick?" is a question about a holiday. "Give me
        // something to pick from" is a request for an interface. Conflating
        // them would put a card in front of every ordinary question.
        Assert.False(GlunoChoiceRequest.IsAskingForChoices(message));
    }

    [Fact]
    public void Accents_are_not_required()
    {
        // A phone keyboard without them must behave the same.
        Assert.True(GlunoChoiceRequest.IsAskingForChoices("lat mig valja"));
        Assert.True(GlunoChoiceRequest.IsAskingForChoices("Låt mig välja"));
    }

    [Fact]
    public void An_empty_message_asks_for_nothing()
    {
        Assert.False(GlunoChoiceRequest.IsAskingForChoices(null));
        Assert.False(GlunoChoiceRequest.IsAskingForChoices("   "));
    }

    // ── It runs before the model ─────────────────────────────────────────

    [Fact]
    public void The_request_is_handled_before_the_model_runs()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var requestAt = chat.IndexOf("GlunoChoiceRequest.IsAskingForChoices(text)", StringComparison.Ordinal);
        var modelAt = chat.IndexOf("RunModelAsync", StringComparison.Ordinal);

        Assert.True(requestAt > 0);
        // A model asked "can you give me buttons?" answers in the first person
        // about its own abilities. It must never see the question.
        if (modelAt > 0) Assert.True(requestAt < modelAt);
    }

    [Fact]
    public void The_request_is_handled_after_the_ordinary_detector()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var detectAt = chat.IndexOf("var detection = GlunoClarificationDetector.Detect(", StringComparison.Ordinal);
        var requestAt = chat.IndexOf("GlunoChoiceRequest.IsAskingForChoices(text)", StringComparison.Ordinal);

        // A turn that already knows what it needs to ask does not need this
        // path at all.
        Assert.True(detectAt > 0 && detectAt < requestAt);
    }

    [Fact]
    public void Nothing_in_the_choice_path_calls_a_provider()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> BuildRequestedChoicesAsync", StringComparison.Ordinal);
        var end = chat.IndexOf(
            "private async Task<GlunoTurnResult> ContinueWithoutAdventureAsync", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start);

        var body = chat[start..end];

        Assert.DoesNotContain("_provider", body);
        Assert.DoesNotContain("_routing", body);
        Assert.DoesNotContain("RunModelAsync", body);
    }

    // ── The priority order ───────────────────────────────────────────────

    [Fact]
    public void A_card_already_waiting_is_shown_again_rather_than_rebuilt()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> BuildRequestedChoicesAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 1600)];

        // "Give me the buttons" right after a card was asked almost always
        // means the card was lost. A second, slightly different question would
        // leave two live ones.
        Assert.Contains("_clarifications.GetForConversationAsync(conversation.Id, userId, ct)", body);
        Assert.Contains("pending is { Options.Count: > 0 } && pending.IsAnswerable", body);
    }

    [Fact]
    public void Re_showing_a_pending_card_creates_no_second_row()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "[GLUNO] re-showing pending clarification", StringComparison.Ordinal);
        var body = chat[start..(start + 700)];

        Assert.True(start > 0);
        // The existing clarification is returned as it stands. Nothing is
        // created, so the options cannot drift and the id stays answerable.
        Assert.Contains("Clarification = pending,", body);
        Assert.DoesNotContain("_clarifications.CreateAsync", body);
    }

    [Fact]
    public void The_turn_falls_back_to_the_Adventure_card_when_nothing_else_fits()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> BuildRequestedChoicesAsync", StringComparison.Ordinal);
        var end = chat.IndexOf(
            "private async Task<GlunoTurnResult> ContinueWithoutAdventureAsync", StringComparison.Ordinal);
        var body = chat[start..end];

        // A global conversation does not need to be attached to an Adventure
        // to show its Adventures. Nothing has to be "opened" first.
        Assert.Contains("var choices = TripChoicesFrom(context);", body);
        Assert.Contains("AskWhichAdventureAsync(", body);
    }

    [Fact]
    public void The_scoped_detector_runs_before_falling_back_to_Adventures()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> BuildRequestedChoicesAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 3600)];

        var scopedAt = body.IndexOf("var scoped = GlunoClarificationDetector.Detect(", StringComparison.Ordinal);
        var adventureAt = body.IndexOf("var choices = TripChoicesFrom(context);", StringComparison.Ordinal);

        Assert.True(scopedAt > 0 && adventureAt > 0);
        // "Give me the cities as options" is a question about cities wearing a
        // request for an interface — the city card, not the Adventure card.
        Assert.True(scopedAt < adventureAt);
    }

    [Fact]
    public void Nothing_to_offer_produces_one_short_line()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("Jag hittar inga tillgängliga Adventures just nu.", chat);
        Assert.Contains("I can't find any Adventures available right now.", chat);
    }

    // ── The forbidden explanations ───────────────────────────────────────

    [Theory]
    [InlineData("Jag kan inte lägga ut knappar själv.")]
    [InlineData("Det gör SideQuest, inte jag.")]
    [InlineData("Just nu vägrar appen öppna ett Adventure härifrån.")]
    [InlineData("Samtalet är inte kopplat till något Adventure.")]
    [InlineData("Jag kan bara skriva text.")]
    [InlineData("I can't create buttons myself.")]
    [InlineData("The app does that, not me.")]
    [InlineData("This conversation isn't attached to an Adventure.")]
    [InlineData("I can only write text.")]
    public void Explaining_SideQuests_own_plumbing_is_detected(string text)
    {
        Assert.True(GlunoUiPromise.ExplainsItsOwnPlumbing(text));
    }

    [Theory]
    [InlineData("Vilket Adventure gäller det?")]
    [InlineData("Ni är i Ronda den 9 augusti.")]
    [InlineData("Jag hittar inga tillgängliga Adventures just nu.")]
    [InlineData("I can't find any Adventures available right now.")]
    public void An_ordinary_answer_is_not_mistaken_for_a_disclaimer(string text)
    {
        // "I can't find any Adventures" is a fact about their data. "I can't
        // create buttons" is a fact about the implementation. Only the second
        // is forbidden.
        Assert.False(GlunoUiPromise.ExplainsItsOwnPlumbing(text));
    }

    [Fact]
    public void The_production_answer_is_stripped_entirely()
    {
        var actual =
            "Du har rätt i att jag borde kunna, men jag kan inte lägga ut knappar själv. "
            + "Det gör SideQuest, och just nu vägrar appen öppna ett Adventure härifrån "
            + "eftersom samtalet inte är kopplat till något.";

        var trimmed = GlunoUiPromise.WithoutPromises(actual);

        // Every sentence was about the implementation. The caller substitutes
        // the plain question, which is what should have been asked.
        Assert.DoesNotContain("knappar", trimmed);
        Assert.DoesNotContain("SideQuest", trimmed);
        Assert.DoesNotContain("kopplat", trimmed);
    }

    [Fact]
    public void The_guard_runs_on_every_answer()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("GlunoUiPromise.ExplainsItsOwnPlumbing(assistantText)", chat);
    }

    [Fact]
    public void The_prompt_forbids_narrating_the_implementation()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        Assert.Contains("NEVER describe how SideQuest is built", flat);
        Assert.Contains("You are one feature of this app", flat);
        Assert.Contains("SideQuest turns it into a card", flat);
    }

    [Fact]
    public void The_prompt_names_the_exact_sentences_that_failed()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        // A rule listing the wording that actually shipped is much harder to
        // drift from than a general principle.
        Assert.Contains("Never say you cannot produce buttons", flat);
        Assert.Contains("that the app is refusing", flat);
        Assert.Contains("that you can only write text", flat);
    }

    // ── The card that gets built ─────────────────────────────────────────

    [Fact]
    public void The_Adventure_card_is_short_and_carries_real_options()
    {
        var choices = new[]
        {
            new TripChoice(Guid.NewGuid(), "Semester 2026", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)),
            new TripChoice(Guid.NewGuid(), "Italien", new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 12)),
        };

        var question = GlunoClarificationBuilder.QuestionFor(
            GlunoClarificationTypes.Adventure, "sv");

        var options = GlunoClarificationBuilder.WithNoAdventureOption(
            GlunoClarificationBuilder.TripOptions(choices, new DateOnly(2026, 8, 6), "sv"), "sv");

        // The whole answer.
        Assert.Equal("Vilket Adventure gäller det?", question);

        Assert.Equal(3, options.Count);
        Assert.Equal("Vet inte än", options[^1].Label);
        // Every trip option points at a verified id the backend produced; the
        // model has no way to add one.
        Assert.All(
            options.Where(option => option.EntityType == GlunoClarificationEntityTypes.Trip),
            option => Assert.NotNull(option.EntityId));
    }

    [Fact]
    public void The_card_is_capped_at_five_Adventures()
    {
        var choices = Enumerable.Range(0, 12)
            .Select(index => new TripChoice(
                Guid.NewGuid(), $"Trip {index}", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 16)))
            .ToList();

        var options = GlunoClarificationBuilder.TripOptions(choices, new DateOnly(2026, 8, 6), "sv");

        Assert.Equal(GlunoClarificationBuilder.MaxOptions, options.Count);
    }

    [Fact]
    public void Showing_the_card_does_not_change_the_conversations_scope()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AskWhichAdventureAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2200)];

        Assert.True(start > 0);
        // The card is created with no TripId of its own, and nothing writes
        // the conversation's. A global conversation stays global.
        Assert.Contains("TripId = null,", body);
        Assert.DoesNotContain("conversation.TripId = ", body);
    }

    [Fact]
    public void Choosing_an_Adventure_scopes_only_that_turn()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // The continuation passes scopeTripId for the replayed turn; the
        // conversation row is untouched.
        Assert.Contains("scopeTripId: single.Id, answered: answered);", chat);
    }
}
