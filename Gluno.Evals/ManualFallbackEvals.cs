using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for Gluno never telling somebody to do the thing it was asked to do.
///
/// THE PRODUCTION FAILURE. A user with "Semester 2026" already selected asked
/// Gluno to add a place and was told: "Öppna Semester 2026 och lägg till
/// manuellt."
///
/// THE MODEL DID NOT INVENT IT. SideQuest's own capability catalogue answers
/// "how do I add an Activity?" with "Öppna äventyret och använd lägg
/// till-knappen på den dag du vill" — correct as documentation, and exactly
/// wrong as a reply to "add this one". The add request reached the model at
/// all because the deterministic path returned nothing and the branch below it
/// fell through.
///
/// THREE LAYERS, in the order they matter: the request is resolved
/// deterministically, the prompt forbids the sentence, and the guard replaces
/// it if it appears anyway. Only the first is a fix; the other two are for the
/// paths nobody has thought of yet.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ManualFallbackEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    // ── 2-3, 26. The phrases ─────────────────────────────────────────────

    [Theory]
    // The exact production answer, and the paraphrases around it.
    [InlineData("Öppna Semester 2026 och lägg till manuellt.")]
    [InlineData("Öppna äventyret och använd lägg till-knappen på den dag du vill.")]
    [InlineData("Du får lägga till den själv i resan.")]
    [InlineData("Gå till resan och skapa aktiviteten där.")]
    [InlineData("Jag kan inte lägga till det härifrån.")]
    [InlineData("Open the Adventure and add it manually.")]
    [InlineData("Go to the trip and create the activity yourself.")]
    [InlineData("I can't add that from here — add it yourself.")]
    [InlineData("You'll need to do it manually.")]
    public void A_manual_instruction_is_replaced_on_an_action_turn(string answer)
    {
        var cleaned = GlunoManualFallback.Clean(answer, GlunoIntent.AddActivity, "sv");

        Assert.Equal("Vilken plats vill du lägga till?", cleaned);
        Assert.NotEqual(answer, cleaned);
    }

    [Fact]
    public void The_replacement_is_whole_rather_than_edited()
    {
        // An answer that reached for a manual instruction has already decided
        // it cannot help, and the rest of it is built around that. Cutting the
        // sentence out leaves a paragraph that still means the same thing.
        var answer = "Jag hittade Real Alcázar men kan inte lägga till det härifrån. "
            + "Öppna äventyret så finns det på dagens vy.";

        var cleaned = GlunoManualFallback.Clean(answer, GlunoIntent.AddActivity, "sv");

        Assert.DoesNotContain("Öppna", cleaned);
        Assert.DoesNotContain("Real Alcázar", cleaned);
        Assert.Equal("Vilken plats vill du lägga till?", cleaned);
    }

    [Fact]
    public void Genuine_app_help_is_left_alone()
    {
        // "How do I add an Activity myself?" deserves exactly the catalogue's
        // answer. A guard that matched on wording alone would break the feature
        // it borrows its phrases from.
        var help = "Öppna äventyret och använd lägg till-knappen på den dag du vill.";

        Assert.Equal(help, GlunoManualFallback.Clean(help, GlunoIntent.SideQuestHelp, "sv"));
        Assert.Equal(help, GlunoManualFallback.Clean(help, GlunoIntent.NavigationRequest, "sv"));

        Assert.False(GlunoManualFallback.IsActionIntent(GlunoIntent.SideQuestHelp));
        Assert.True(GlunoManualFallback.IsActionIntent(GlunoIntent.AddActivity));
        Assert.True(GlunoManualFallback.IsActionIntent(GlunoIntent.PlaceRecommendation));
    }

    [Fact]
    public void An_ordinary_answer_passes_through_untouched()
    {
        var answer = "Real Alcázar och Metropol Parasol passar bra på torsdagen.";

        Assert.Same(answer, GlunoManualFallback.Clean(answer, GlunoIntent.AddActivity, "sv"));
        Assert.False(GlunoManualFallback.Mentions(answer));
    }

    [Fact]
    public void The_guard_folds_accents_before_matching()
    {
        // "Öppna" and "oppna" are the same phrase, and the model paraphrases.
        Assert.True(GlunoManualFallback.Mentions("oppna aventyret sa fixar du det"));
        Assert.True(GlunoManualFallback.Mentions("ÖPPNA ÄVENTYRET"));
    }

    [Fact]
    public void The_guard_runs_on_every_turn_before_the_answer_is_kept()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var guardAt = chat.IndexOf("GlunoManualFallback.Clean(", StringComparison.Ordinal);
        var persistAt = chat.IndexOf("var persistedText =", StringComparison.Ordinal);

        Assert.True(guardAt > 0, "the guard is missing");
        Assert.True(guardAt < persistAt, "the guard must run before the answer is written down");
        // Codes only — the text that triggered it is the model's answer.
        Assert.Contains("manual fallback replaced intent={Intent}", chat);
    }

    [Fact]
    public void The_prompt_forbids_it_in_words_too()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        Assert.Contains("NEVER TELL THE USER TO DO IT THEMSELVES", prompt);
        Assert.Contains("open the Adventure and\n        add it manually", prompt.Replace("\r\n", "\n"));
        // And says why the catalogue's answer is not a substitute.
        Assert.Contains("different questions", prompt);
    }

    // ── 1, 13-16. The deterministic path ─────────────────────────────────

    [Fact]
    public void An_add_request_never_falls_through_to_the_model()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("if (GlunoPlaceOptions.IsAddRequest(text))", StringComparison.Ordinal);
        // To the end of the branch rather than a fixed char count — the
        // deterministic recovery lengthened it without changing the invariant.
        var end = chat.IndexOf("A pure place question", start, StringComparison.Ordinal);
        var body = chat[start..end];

        Assert.True(start > 0 && end > start);
        // THE BRANCH THAT WAS THE BUG. It used to end at `if (added != null)`
        // and fall through; now an unresolved place asks which one.
        // The router is no longer consulted — it could not recognise a place
        // named outright, and that gap was the way through to the model.
        Assert.Contains("if (LooksLikePlaceAdd(text))", body);
        Assert.Contains("return await AskWhichPlaceToAddAsync(conversation, userId, text, ct);", body);
    }

    [Theory]
    [InlineData("Lägg till Real Alcázar", true)]
    [InlineData("Lägg till den första", true)]
    [InlineData("add the second one", true)]
    [InlineData("Lägg till Metropol Parasol på torsdag", true)]
    // Itinerary edits, which belong to the model — a false positive here would
    // answer a planning question with "which place did you mean?".
    [InlineData("Lägg till en vilodag", false)]
    [InlineData("lägg till en timme", false)]
    [InlineData("add a rest day", false)]
    [InlineData("Add a note about the ferry", false)]
    public void Only_a_place_shaped_add_asks_which_place(string message, bool expected)
    {
        Assert.Equal(expected, GlunoPlaceOptions.PointsAtSomethingShown(message));
    }

    [Fact]
    public void Both_text_forms_reach_the_same_resolved_add()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> AddNamedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 4200)];

        // A name and an ordinal are the same lookup against the same shortlist,
        // and both end at the option key rather than at a described place.
        Assert.Contains("GlunoPlaceOptions.Match(places, text, allowOrdinals: complete)", body);
        Assert.Contains("keys[matches[0]]", body);
        // After a reload the names are gone, so the list is fetched again from
        // the ids that were kept.
        Assert.Contains("RefetchShownPlacesAsync(message, ct)", body);
    }

    [Fact]
    public void An_unresolved_place_is_offered_as_tappable_choices()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AskWhichPlaceToAddAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2200)];

        Assert.True(start > 0);
        // Every row is something this conversation actually offered, and
        // tapping one goes straight to the add flow.
        Assert.Contains("AskWhichPlaceAsync(conversation, userId, text, places, ct)", body);
        Assert.Contains("RefetchShownPlacesAsync(message, ct)", body);
    }

    [Fact]
    public void With_nothing_ever_shown_it_still_asks_rather_than_refers()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("Vilken plats vill du lägga till? Be mig ta fram förslag först.", chat);
        Assert.Contains("Which place would you like to add? Ask me for suggestions first.", chat);

        // And that sentence would itself survive the guard.
        Assert.False(GlunoManualFallback.Mentions(
            "Vilken plats vill du lägga till? Be mig ta fram förslag först."));
    }

    [Fact]
    public void A_failed_lookup_says_so_instead_of_referring_the_user_away()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        foreach (var status in new[]
        {
            GlunoRehydrationStatus.NotFound, GlunoRehydrationStatus.Busy,
            GlunoRehydrationStatus.Unavailable,
        })
        {
            foreach (var language in new[] { "sv", "en" })
            {
                var line = GlunoPlaceFailureText.For(status, language);
                Assert.False(GlunoManualFallback.Mentions(line), line);
            }
        }
    }

    // ── 4-6. The button ──────────────────────────────────────────────────

    [Fact]
    public void The_add_button_calls_the_structured_endpoint()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        var screen = Mobile("app", "gluno.tsx");

        // The card hands back the message id and the place; the screen turns
        // that into the add call. Never the composer.
        Assert.Contains("await onAddPlace(message.id, place);", row);
        Assert.Contains("runAddPlace(messageId, place.optionKey)", screen);
        Assert.Contains("addGlunoRecommendedPlace(messageId, optionKey, {", screen);
    }

    [Fact]
    public void The_add_button_never_sends_a_chat_message()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        var start = row.IndexOf("if (!onAddPlace || addingKey !== null) return;", StringComparison.Ordinal);
        var body = row[start..(start + 700)];

        Assert.True(start > 0);
        // "Lägg till Real Alcázar" as free text would take the slower path and
        // depend on matching a name the backend may no longer have.
        Assert.DoesNotContain("sendGlunoMessage", body);
        Assert.DoesNotContain("handleSend", body);
        Assert.DoesNotContain("setDraft", body);
        Assert.DoesNotContain("gluno.place.add'", body);
    }

    [Fact]
    public void The_request_carries_the_message_the_key_and_an_idempotency_key()
    {
        var client = Mobile("lib", "gluno.ts");
        var screen = Mobile("app", "gluno.tsx");

        Assert.Contains("/places/${encodeURIComponent(optionKey)}/add", client);
        Assert.Contains("idempotencyKey: options?.idempotencyKey ?? null,", client);
        // Stable per card, so a double tap cannot add twice.
        Assert.Contains("`place-${messageId}-${optionKey}`", screen);
        // And nothing about the place itself travels.
        Assert.DoesNotContain("name: place.name", screen);
        Assert.DoesNotContain("latitude", client[client.IndexOf(
            "export async function addGlunoRecommendedPlace", StringComparison.Ordinal)..][..900]);
    }

    [Fact]
    public void A_double_tap_on_add_is_blocked_locally_too()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        Assert.Contains("if (!onAddPlace || addingKey !== null) return;", row);
    }

    // ── 8, 10. No model round ────────────────────────────────────────────

    [Fact]
    public void The_add_endpoint_runs_no_model()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AddPlaceFromKeyAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2400)];

        Assert.True(start > 0);
        // Which place was meant is a lookup, and which day is arithmetic over
        // the Adventure. Neither is a judgement.
        Assert.DoesNotContain("_ai.", body);
        Assert.DoesNotContain("RunTurnAsync", body);
        Assert.DoesNotContain("SendCoreAsync", body);
    }

    [Fact]
    public void Choosing_a_day_runs_no_model()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        var start = controller.IndexOf(
            "private async Task<GlunoTurnResult> AddPlaceOnChosenDayAsync", StringComparison.Ordinal);
        var body = controller[start..(start + 1800)];

        Assert.True(start > 0);
        Assert.Contains("_chat.AddRecommendedPlaceAsync(", body);
        Assert.DoesNotContain("ContinueFromClarificationAsync", body);
    }

    [Fact]
    public void The_whole_resolved_add_path_touches_no_model()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 9000)];

        // Adventure choice, membership, day choice, proposal — all of it.
        Assert.DoesNotContain("_ai.", body);
        Assert.DoesNotContain("RunTurnAsync", body);
    }

    // ── 1, 5, 18. Trip scope ─────────────────────────────────────────────

    [Fact]
    public void A_scoped_conversation_uses_its_own_verified_trip()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 1400)];

        // The conversation's own scope first, then the Adventure the chat last
        // settled on. The client never supplies one.
        Assert.Contains("var tripId = conversation.TripId ?? workingState.Recent.LastAdventureId;", body);
        // Membership is re-checked when the button is pressed, not when the
        // card was rendered.
        Assert.Contains("_db.TripMembers.AnyAsync(", chat[start..(start + 3000)]);
    }

    [Fact]
    public void Only_a_scopeless_turn_asks_which_adventure()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2400)];

        // Guarded on the absence of a trip, so a selected Adventure is never
        // asked about again.
        Assert.Contains("if (tripId == null)", body);
        Assert.Contains("AskWhichAdventureAsync(", body);
    }

    // ── 9, 11-12. The proposal ───────────────────────────────────────────

    [Fact]
    public void Several_days_produce_a_day_card_rather_than_a_guess()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 6000)];

        Assert.Contains("OnlySensibleDay(trip, context.Route)", body);
        Assert.Contains("AskPlaceDayAsync(", body);
    }

    [Fact]
    public void The_proposal_reaches_the_direct_response_and_the_history()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Built and returned on the turn itself...
        Assert.Contains("Proposals = [proposal],", chat);
        Assert.Contains("ProposalRecords = records,", chat);
        Assert.Contains("liveProposals: result.Proposals", controller);

        // ...and read back from its own rows afterwards, so a reload renders
        // the same approval card.
        Assert.Contains("_proposals.ListForMessagesAsync(assistantIds", controller);
        Assert.Contains("byMessage.GetValueOrDefault(m.Id", controller);
    }

    [Fact]
    public void The_add_turn_returns_a_conversation_the_response_can_map()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("_logger.LogInformation(\n            \"[GLUNO] recommended place added as proposal",
            StringComparison.Ordinal);

        Assert.True(start > 0);

        var body = chat.Replace("\r\n", "\n");
        var at = body.IndexOf("[GLUNO] recommended place added as proposal", StringComparison.Ordinal);
        var tail = body[at..(at + 600)];

        // A null conversation here is a 500 with no body — the failure mode the
        // turn boundary exists to prevent.
        Assert.Contains("Conversation = conversation,", tail);
        Assert.Contains("AssistantMessage = assistantMessage,", tail);
    }
}
