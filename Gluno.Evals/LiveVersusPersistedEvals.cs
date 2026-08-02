using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the difference between what the user reads and what is written
/// down.
///
/// THE PROBLEM THESE CLOSE. Keeping a provider's content out of the payload is
/// not enough if the same content is in the sentence above it. "Real Alcázar and
/// Metropol Parasol are the pick of Sevilla" is a provider's names written as
/// prose, and so is "Which day should Real Alcázar go on?", and so is a row
/// label on a chooser, and so is a proposal's heading. Moving a name out of a
/// structured field and into a paragraph does not change what it is.
///
/// THE APPROACH, AND THE ONE THAT WAS REJECTED. Both texts are chosen where they
/// are produced, before either is written. The rejected alternative is to write
/// the real sentence and strip names out of it afterwards — a pattern-match
/// against text, which stores exactly the thing it exists to prevent every time
/// it misses. There is no version of it that fails safe.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class LiveVersusPersistedEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Chat() => Source("Services", "Gluno", "GlunoChatService.cs");
    private static string Controller() => Source("Controllers", "GlunoController.cs");

    // ── 1-3. The answer itself ───────────────────────────────────────────

    [Fact]
    public void The_live_answer_is_the_one_the_model_wrote()
    {
        var chat = Chat();
        var controller = Controller();

        // The turn carries both, and the response prefers the live one.
        Assert.Contains("LiveAssistantText = ReferenceEquals(persistedText, assistantText) ? null : assistantText,", chat);
        Assert.Contains("Text = liveText ?? m.Text,", controller);
Assert.Equal(4, controller.Split("liveText: result.LiveAssistantText").Length - 1);
    }

    [Fact]
    public void The_persisted_answer_is_a_sentence_sidequest_wrote()
    {
        var chat = Chat();

        // Chosen before either is written, from a fixed source — never derived
        // from the model's text.
        Assert.Contains(
            "var persistedText = retention.Reduced && visiblePlaces.Count > 0\n"
            + "            ? GlunoNeutralText.PlaceAnswer(context.User.Language)\n"
            + "            : assistantText;",
            chat.Replace("\r\n", "\n"));

        Assert.Contains("Text = persistedText,", chat);
    }

    [Fact]
    public void No_place_name_can_reach_the_neutral_text()
    {
        var neutral = Source("Services", "Gluno", "GlunoNeutralText.cs");

        // Every sentence is a constant. Nothing here takes a place, a card or a
        // provider value — the only parameters are a language and a row number.
        Assert.DoesNotContain("GlunoPlaceCard", neutral);
        Assert.DoesNotContain("TravelPlace", neutral);
        Assert.DoesNotContain("place.Name", neutral);
        Assert.DoesNotContain("{place", neutral);

        Assert.All(
            typeof(GlunoNeutralText).GetMethods().Where(method => method.DeclaringType == typeof(GlunoNeutralText)),
            method => Assert.All(
                method.GetParameters(),
                parameter => Assert.Contains(parameter.ParameterType, new[] { typeof(string), typeof(int) })));

        Assert.Equal("Jag tog fram några platsförslag för er resa.", GlunoNeutralText.PlaceAnswer("sv"));
        Assert.Equal("Vilken dag vill du lägga till platsen?", GlunoNeutralText.DayQuestion("sv"));
        Assert.Equal("Which day should the place go on?", GlunoNeutralText.DayQuestion("en"));
    }

    [Fact]
    public void The_persisted_text_is_never_produced_by_editing_the_live_one()
    {
        var chat = Chat();
        var neutral = Source("Services", "Gluno", "GlunoNeutralText.cs");

        // A stripper that misses one name stores it. The two texts have
        // separate sources and never touch.
        foreach (var pattern in new[] { "Regex", "Replace(", ".Remove(", "Sanitize(", "Redact" })
        {
            Assert.DoesNotContain(pattern, neutral);
        }

        var start = chat.IndexOf("var persistedText =", StringComparison.Ordinal);
        var body = chat[start..(start + 300)];

        Assert.DoesNotContain("Replace", body);
        Assert.DoesNotContain("Regex", body);
    }

    // ── 4-6. The questions ───────────────────────────────────────────────

    [Fact]
    public void The_live_day_question_names_the_place()
    {
        var chat = Chat();

        Assert.Contains("$\"Vilken dag vill du lägga till {place.Name}?\"", chat);
        Assert.Contains("LiveAssistantText = identityOnly ? question : null,", chat);
        // The named version reaches the response as a detached view of the row.
        Assert.Contains("? LiveView(clarification, question, null)", chat);
    }

    [Fact]
    public void The_persisted_day_question_names_nothing()
    {
        var chat = Chat();

        Assert.Contains(
            "var storedQuestion = identityOnly ? GlunoNeutralText.DayQuestion(language) : question;", chat);
        Assert.Contains("Question = storedQuestion,", chat);
        // And the user message the card hangs off, which used to be the name on
        // its own.
        Assert.Contains("Text = identityOnly ? GlunoNeutralText.ThePlace(language) : place.Name,", chat);
    }

    [Fact]
    public void The_live_view_is_a_new_object_not_the_tracked_row()
    {
        var chat = Chat();

        var start = chat.IndexOf("private static GlunoClarification LiveView(", StringComparison.Ordinal);
        var body = chat[start..(start + 2000)];

        Assert.True(start > 0);
        // Mutating the entity would save the real wording on the next
        // SaveChanges — the exact thing being avoided.
        Assert.Contains("IReadOnlyList<string>? labels) => new()", body);
        Assert.Contains("Id = stored.Id,", body);
        Assert.Contains("new GlunoClarificationOption", body);
        Assert.DoesNotContain("stored.Question =", body);
        Assert.DoesNotContain("option.Label =", body);
    }

    [Fact]
    public void Persisted_option_labels_carry_no_provider_text()
    {
        var chat = Chat();

        // Numbered rows and nothing else. The VALUE is still the id, so either
        // version resolves to the same place.
        Assert.Contains("GlunoNeutralText.PlaceOptionLabel(language, index)", chat);

        var start = chat.IndexOf("var options = identityOnly", StringComparison.Ordinal);
        var body = chat[start..(start + 600)];

        Assert.True(start > 0);
        Assert.DoesNotContain("place.Name", body);
        Assert.DoesNotContain("place.Address", body);
        Assert.DoesNotContain("Description =", body);

        Assert.Equal("Alternativ 1", GlunoNeutralText.PlaceOptionLabel("sv", 0));
        Assert.Equal("Option 2", GlunoNeutralText.PlaceOptionLabel("en", 1));
    }

    [Fact]
    public void A_numbered_chooser_is_not_rebuilt_after_a_reload()
    {
        var chat = Chat();
        var controller = Controller();

        // "Which of the places do you mean?" over rows reading "Option 1" and
        // "Option 2" is not a question anybody can answer, and rendering it
        // would leave a live Apply path under a meaningless card.
        Assert.Contains("ContentSuppressed = identityOnly,", chat);
        Assert.Contains("Renderable(clarifications.GetValueOrDefault(m.Id))", controller);
        Assert.Contains("clarification is { ContentSuppressed: true } ? null : clarification", controller);
    }

    [Fact]
    public void The_day_card_stays_answerable_after_a_reload()
    {
        var chat = Chat();

        // Its rows are days and cities out of the user's own Adventure, so only
        // the heading loses a name. Suppressing it would strand somebody who
        // tapped Add and then closed the app.
        var start = chat.IndexOf("private async Task<GlunoTurnResult> AskPlaceDayAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2200)];

        Assert.True(start > 0);
        Assert.DoesNotContain("ContentSuppressed", body);
    }

    // ── 7. Working memory ────────────────────────────────────────────────

    [Fact]
    public void Working_memory_gets_only_what_may_be_stored()
    {
        var chat = Chat();

        // It remembers a place by name and coordinate so "the second one"
        // resolves next turn. Same rule, same reason.
        Assert.Contains("visiblePlaces, retention.Places, records, text, ct);", chat);
        Assert.Contains(
            "GlunoReferenceResolver.Remember(\n            state,\n            rememberable,",
            chat.Replace("\r\n", "\n"));
    }

    // ── 8-9. The proposal ────────────────────────────────────────────────

    [Fact]
    public void A_proposal_under_those_terms_stores_an_identity_not_a_place()
    {
        var chat = Chat();

        var start = chat.IndexOf("var storedPayload = identityOnly", StringComparison.Ordinal);
        var body = chat[start..(start + 900)];

        Assert.True(start > 0);
        // The user's own decisions stay in the clear, because they are the
        // user's: which day, which category, which card they tapped.
        Assert.Contains("date = isoDate,", body);
        Assert.Contains("locationId = place.ProviderPlaceId", body);
        Assert.Contains("optionKey,", body);

        // And nothing the provider wrote.
        Assert.DoesNotContain("title =", body);
        Assert.DoesNotContain("place.Name", body);
        Assert.DoesNotContain("place.Address", body);
        Assert.DoesNotContain("place.Latitude", body);
        Assert.DoesNotContain("ReviewSummary", body);
    }

    [Fact]
    public void The_row_takes_the_persisted_versions()
    {
        var store = Source("Services", "Gluno", "GlunoProposalStore.cs");

        Assert.Contains("var storedPayload = proposal.PersistedPayload ?? proposal.Payload;", store);
        Assert.Contains("Summary = proposal.PersistedSummary ?? proposal.Summary,", store);
        Assert.Contains("PayloadJson = storedPayload.GetRawText(),", store);
        // The card the user is looking at still has its heading.
        Assert.Contains("Summary = live?.Summary ?? record.Summary,", Controller());
        Assert.Contains("Payload = live?.Payload ?? payload,", Controller());
    }

    [Fact]
    public void The_snapshot_is_built_from_what_will_actually_be_applied()
    {
        var store = Source("Services", "Gluno", "GlunoProposalStore.cs");

        Assert.Contains(
            "var snapshot = await BuildSnapshotAsync(proposal.TripId, proposal.Kind, storedPayload, ct);",
            store);
    }

    [Fact]
    public void The_snapshot_holds_only_the_users_own_adventure()
    {
        var properties = typeof(GlunoProposalSnapshot).GetProperties().Select(p => p.Name).ToList();

        // Trip dates and activity signatures — ids, dates and sort order out of
        // the user's own plan. No provider field has anywhere to go.
        Assert.Equal(3, properties.Count);
        Assert.Contains("TripDates", properties);
        Assert.Contains("Activities", properties);
        Assert.Contains("DayLocations", properties);

        var store = Source("Services", "Gluno", "GlunoProposalStore.cs");
        var start = store.IndexOf("public async Task<GlunoProposalSnapshot> BuildSnapshotAsync", StringComparison.Ordinal);
        var body = store[start..];

        // A day location's own label is the user's — "Sevilla", off their
        // route — and belongs in a signature. Nothing a provider wrote does.
        foreach (var field in new[] { "rating", "review", "openingHours", "attribution", "title" })
        {
            Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 10. What an approved Activity gets ───────────────────────────────

    [Fact]
    public void An_activity_never_gets_a_rating_a_review_or_opening_hours()
    {
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        var start = apply.IndexOf("private async Task CreateActivityAsync", StringComparison.Ordinal);
        var body = apply[start..(start + 2200)];

        Assert.True(start > 0);
        // There is no field for any of them, and none is smuggled into the
        // description either.
        foreach (var field in new[] { "Rating", "ReviewCount", "ReviewSummary", "OpeningHours" })
        {
            Assert.DoesNotContain(field, body);
        }

        var draft = typeof(GlunoProposalApplyService).Assembly
            .GetType("sidequest.backend.Services.Gluno.GlunoActivityDraft")!
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Rating", draft);
        Assert.DoesNotContain("OpeningHours", draft);
    }

    [Fact]
    public void The_proposal_no_longer_carries_a_review_snippet_into_the_plan()
    {
        var chat = Chat();

        var start = chat.IndexOf("var livePayload = JsonSerializer.SerializeToElement(new", StringComparison.Ordinal);
        var body = chat[start..(start + 500)];

        Assert.True(start > 0);
        // It used to become the Activity's own description — review text in
        // somebody's itinerary, kept indefinitely. An Activity records where
        // the user is going, not what strangers said about it.
        Assert.DoesNotContain("description", body);
        Assert.DoesNotContain("ReviewSummary", body);
    }

    [Fact]
    public void Apply_fills_in_the_title_from_the_provider_at_apply_time()
    {
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        var start = apply.IndexOf("private async Task<(JsonElement Payload, (string, string)? Error)> ResolvePlacePayloadAsync", StringComparison.Ordinal);
        var body = apply[start..(start + 3600)];

        Assert.True(start > 0);
        Assert.Contains("_rehydrator.RehydrateAsync(references, search, optionKey, ct)", body);
        Assert.Contains("[\"title\"] = fresh.Name,", body);
        Assert.Contains("[\"locationLabel\"] = fresh.Address ?? fresh.Name,", body);
        // Never the description.
        Assert.DoesNotContain("\"description\"", body);
        Assert.DoesNotContain("ReviewSummary", body);
    }

    [Fact]
    public void Apply_refuses_rather_than_writing_a_half_known_activity()
    {
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        var start = apply.IndexOf("private async Task<(JsonElement Payload, (string, string)? Error)> ResolvePlacePayloadAsync", StringComparison.Ordinal);
        var body = apply[start..(start + 3600)];

        Assert.Contains("(\"place_unavailable\"", body);
        Assert.Contains("(\"place_lookup_busy\"", body);
        // Exact id, both halves.
        Assert.Contains("!string.Equals(fresh.ProviderPlaceId, locationId, StringComparison.Ordinal)", body);
        Assert.Contains("!string.Equals(fresh.Provider, providerId, StringComparison.Ordinal)", body);
    }

    [Fact]
    public void An_ordinary_proposal_is_untouched_by_the_resolver()
    {
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        var start = apply.IndexOf("private async Task<(JsonElement Payload, (string, string)? Error)> ResolvePlacePayloadAsync", StringComparison.Ordinal);
        var body = apply[start..(start + 600)];

        // The marker is a "place" object, and nothing but the identity-only
        // path writes one.
        Assert.Contains("if (!payload.TryGetProperty(\"place\", out var reference)", body);
        Assert.Contains("return (payload, null);", body);
    }

    [Fact]
    public void Apply_cannot_reach_a_message_outside_the_callers_conversations()
    {
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        // Ownership is part of the lookup, not a check afterwards.
        Assert.Contains("_db.GlunoConversations.Where(conversation => conversation.UserId == userId)", apply);
    }

    // ── 11-12. The detail endpoint ───────────────────────────────────────

    [Fact]
    public void The_detail_endpoint_rehydrates_instead_of_404ing()
    {
        var controller = Controller();

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 2600)];

        Assert.True(start > 0);
        Assert.Contains("GlunoPlaceOptions.ResolveReference(message, optionKey)", body);
        Assert.Contains("_rehydrator.RehydrateAsync(", body);
        Assert.Contains("string.Equals(fresh.ProviderPlaceId, reference.LocationId, StringComparison.Ordinal)", body);
    }

    [Fact]
    public void The_detail_endpoint_stores_nothing_it_fetched()
    {
        var controller = Controller();

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 2600)];

        // Rendered and forgotten: mapped straight into the response, never
        // written back into the message it came from.
        Assert.Contains("return Ok(MapPlace(GlunoPlaceCards.From(fresh), index));", body);
        Assert.DoesNotContain("AppendAsync", body);
        Assert.DoesNotContain("SaveChanges", body);
        Assert.DoesNotContain("PayloadJson", body);
    }

    [Fact]
    public void The_detail_endpoint_answers_every_failure_with_a_code()
    {
        var controller = Controller();

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 2600)];

        // 401, 403 and a timeout all arrive as one category from the provider
        // layer and none of them are the user's problem; only a rate limit is
        // worth a retry.
        Assert.Contains("GlunoErrors.Body(\"place_lookup_busy\", true)", body);
        Assert.Contains("GlunoErrors.Body(\"place_not_available\", false)", body);
        Assert.Contains("GlunoErrors.Body(\"place_lookup_failed\", true)", body);
        Assert.Contains("GlunoErrors.Body(\"message_not_found\", false)", body);
    }

    // ── 13-14. What may not be reached ───────────────────────────────────

    [Fact]
    public void The_detail_endpoint_is_scoped_to_the_caller_and_the_message()
    {
        var controller = Controller();

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 2600)];

        // Ownership is the lookup, so a key from another conversation resolves
        // against a message that is simply not found.
        Assert.Contains("_conversations.GetMessageAsync(messageId, GetUserId(), ct)", body);
        // And the key itself is parsed strictly, never coerced.
        Assert.Contains("GlunoPlaceOptions.IndexOf(optionKey)", body);
    }

    [Fact]
    public void A_day_option_that_is_not_a_day_is_refused()
    {
        var controller = Controller();

        var start = controller.IndexOf("private async Task<GlunoTurnResult> AddPlaceOnChosenDayAsync", StringComparison.Ordinal);
        var body = controller[start..(start + 1800)];

        Assert.True(start > 0);
        Assert.Contains("DateOnly.TryParseExact(", body);
        Assert.Contains("GlunoTurnError.PlaceNotRetained", body);
    }

    // ── 15-16. The day continuation ──────────────────────────────────────

    [Fact]
    public void Choosing_a_day_resumes_the_add_flow_without_a_model_round()
    {
        var controller = Controller();

        var start = controller.IndexOf("private async Task<GlunoTurnResult> AddPlaceOnChosenDayAsync", StringComparison.Ordinal);
        var body = controller[start..(start + 1800)];

        // Straight back into the add flow. The ordinary continuation replays
        // the original message through the model, which would cost a round to
        // re-derive something the row already knows.
        Assert.Contains("_chat.AddRecommendedPlaceAsync(", body);
        Assert.DoesNotContain("ContinueFromClarificationAsync", body);
        Assert.DoesNotContain("SendCoreAsync", body);
    }

    [Fact]
    public void The_continuation_reads_the_place_from_the_row_not_the_request()
    {
        var controller = Controller();

        // Every input is server-owned: the place from the clarification, the
        // day from the option's own value, which the backend wrote.
        Assert.Contains(
            "{ PlaceMessageId: { } placeMessageId, PlaceOptionKey: { } placeOptionKey }",
            controller);
        Assert.Contains("when option.EntityType == GlunoClarificationEntityTypes.Date", controller);
    }

    [Fact]
    public void The_row_records_which_card_the_question_is_about()
    {
        var chat = Chat();

        Assert.Contains("PlaceMessageId = sourceMessageId,", chat);
        Assert.Contains("PlaceOptionKey = optionKey,", chat);
    }

    [Fact]
    public void Choosing_the_day_twice_adds_the_place_once()
    {
        var controller = Controller();

        var start = controller.IndexOf("private async Task<GlunoTurnResult> AddPlaceOnChosenDayAsync", StringComparison.Ordinal);
        var body = controller[start..(start + 1800)];

        // Bound to the clarification rather than to the original send, so it
        // cannot collide with the turn that asked the question.
        Assert.Contains("idempotencyKey ?? $\"clar-{clarification.Id:N}\"", body);
        Assert.Contains("_clarifications.RecordContinuationAsync(", body);
    }

    // ── 17-18. Failures and reload ───────────────────────────────────────

    [Fact]
    public void A_rate_limited_lookup_says_try_again_and_writes_nothing()
    {
        var chat = Chat();
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        // Fixed strings, now in one place — see GlunoPlaceFailureText.
        Assert.Equal(
            "Jag kunde inte hämta platsen just nu. Försök igen om en liten stund.",
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.Busy, "sv"));
        Assert.Contains("Try again in a moment.", GlunoPlaceFailureText.For(GlunoRehydrationStatus.Busy, "en"));
        Assert.Contains("Try saving again in a moment.", apply);

        // No proposal on that path, and no Activity.
        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult> PlaceLookupFailedAsync", StringComparison.Ordinal);
        Assert.DoesNotContain("CreateProposalsAsync", chat[start..(start + 900)]);
    }

    [Fact]
    public void Reload_shows_the_neutral_line_and_no_cards()
    {
        var controller = Controller();

        // History reads the row: neutral text, payload.Places (empty under
        // those terms), and a clarification only when it can still be asked.
        Assert.Contains(
            "byMessage.GetValueOrDefault(m.Id, Array.Empty<Models.GlunoProposalRecord>()),\n"
            + "                Renderable(clarifications.GetValueOrDefault(m.Id))))",
            controller.Replace("\r\n", "\n"));

        Assert.Contains("payload.Places.Select((place, index) => MapPlace(place, index)).ToList()", controller);
    }

    // ── 19-21. Unchanged elsewhere ───────────────────────────────────────

    [Fact]
    public void A_storable_provider_writes_exactly_what_it_always_did()
    {
        var chat = Chat();

        // identityOnly is false throughout, so every neutral branch is skipped
        // and both texts are the same object — which is what makes
        // LiveAssistantText null and leaves the response untouched.
        Assert.Contains("var identityOnly = !place.AllowsContentPersistence;", chat);
        Assert.Contains("Text = identityOnly ? GlunoNeutralText.PlaceProposed(language) : liveText,", chat);
        Assert.Contains("PersistedSummary = identityOnly ? GlunoNeutralText.ProposalSummary(language) : null,", chat);
        Assert.Contains("PersistedPayload = identityOnly ? storedPayload : null,", chat);
    }

    [Fact]
    public void A_card_read_back_from_a_payload_is_still_storable()
    {
        // THE BUG THIS CLOSES. Neither flag is serialised — they decide what
        // gets written, and a stored copy of a permission is one somebody could
        // edit. So a card straight out of JSON claims it may not be stored,
        // while sitting in the payload that stored it. Left alone, every legacy
        // add would take the identity-only path and look for a location id that
        // was not serialised either.
        var stored = new GlunoPlaceCard
        {
            Provider = "tripadvisor",
            ExternalId = "tripadvisor:187443",
            Name = "Real Alcázar",
            Category = "attraction",
            SourceAttribution = "Data provided by Tripadvisor",
        };

        Assert.False(stored.AllowsContentPersistence);

        var restored = GlunoPlaceCards.Restored(stored);

        Assert.True(restored.AllowsContentPersistence);
        Assert.True(restored.AllowsIdentityPersistence);
        // Recovered from the namespaced id, which IS serialised.
        Assert.Equal("187443", restored.ProviderPlaceId);
        Assert.Equal("Real Alcázar", restored.Name);
    }

    [Fact]
    public void Both_payload_readers_restore_what_they_read()
    {
        var options = Source("Services", "Gluno", "GlunoPlaceOptions.cs");

        Assert.Contains("GlunoPlaceCards.Restored(places[index])", options);
        Assert.Contains("payload?.Places.Select(GlunoPlaceCards.Restored).ToList()", Chat());
    }

    [Fact]
    public void The_decision_is_the_capability_never_the_provider_name()
    {
        foreach (var file in new[] { "GlunoNeutralText.cs", "GlunoPlaceRetention.cs" })
        {
            var source = Source("Services", "Gluno", file);

            Assert.DoesNotContain("\"terra\"", source);
            Assert.DoesNotContain("\"tripadvisor\"", source);
            Assert.DoesNotContain("TerraTravelProvider", source);
        }

        // Every branch reads a flag the provider stamped on the result, and no
        // branch anywhere compares a provider name.
        var chat = Chat();

        Assert.Contains("AllowsContentPersistence", chat);
        Assert.DoesNotContain("== \"terra\"", chat);
        Assert.DoesNotContain("== TerraTravelProvider.ProviderId", chat);
        Assert.DoesNotContain("Provider == \"tripadvisor\"", chat);
    }

    [Fact]
    public void No_key_and_no_raw_payload_is_ever_written()
    {
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        Assert.DoesNotContain("_apiKey", terra);
        Assert.Contains("request.Headers.Add(\"X-API-Key\", ApiKey);", terra);
        Assert.DoesNotContain("TravelDataCache", terra);

        // The apply path holds the fetched place only long enough to build the
        // Activity's own fields.
        var start = apply.IndexOf("private async Task<(JsonElement Payload, (string, string)? Error)> ResolvePlacePayloadAsync", StringComparison.Ordinal);
        var body = apply[start..(start + 3600)];

        Assert.DoesNotContain("MemoryCache", body);
        Assert.DoesNotContain("_db.GlunoMessages.Update", body);
    }
}
