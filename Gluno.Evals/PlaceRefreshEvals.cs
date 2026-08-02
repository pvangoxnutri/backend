using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for "show me new suggestions" — the one case a retry cannot fix.
///
/// WHEN THIS IS THE RIGHT ANSWER. A place the user tried to add is no longer in
/// the provider's results. Retrying that lookup fails identically every time,
/// so the honest offer is a current shortlist rather than a button that cannot
/// work.
///
/// WHAT THE CLIENT SENDS: a message id and an idempotency key. The destination,
/// the category, the search words, the language and the limit all come from the
/// context that message already stored — SideQuest's own request, not the
/// provider's answer — so the client cannot widen the search, move it, or aim
/// it at somewhere the user never asked about.
///
/// NO MODEL. This repeats the SEARCH, not the reasoning.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class PlaceRefreshEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string Chat() => Source("Services", "Gluno", "GlunoChatService.cs");

    private static string Refresh()
    {
        var chat = Chat();
        var start = chat.IndexOf(
            "public async Task<GlunoTurnResult> RefreshPlaceSuggestionsAsync", StringComparison.Ordinal);

        Assert.True(start > 0, "the refresh path is missing");
        return chat[start..(start + 9000)];
    }

    // ── 1-2. The action reaches the app ──────────────────────────────────

    [Fact]
    public void A_place_that_is_gone_offers_new_suggestions()
    {
        var action = GlunoTurnAction.For(
            GlunoRehydrationStatus.NotFound, Guid.NewGuid(), "place-0");

        Assert.Equal(GlunoTurnActionTypes.ShowNewPlaceSuggestions, action!.Type);
        // No option key: the old one points at a place that is no longer there,
        // and reusing it would ask for exactly what just failed.
        Assert.Null(action.OptionKey);
        Assert.NotNull(action.MessageId);
    }

    [Fact]
    public void The_app_draws_a_button_for_it()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        var translations = Mobile("components", "i18n-provider.tsx");

        Assert.Contains("show_new_place_suggestions: 'gluno.action.newSuggestions'", row);
        Assert.Contains("'gluno.action.newSuggestions': 'Visa nya förslag',", translations);
        Assert.Contains("'gluno.action.newSuggestions': 'Show new suggestions',", translations);
    }

    [Fact]
    public void An_unknown_action_type_draws_nothing()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // The safe direction: a backend adding an action this build cannot
        // perform must not leave an unresponsive control in the chat.
        Assert.Contains("const actionLabel = ACTION_COPY[message.action?.type ?? ''] ?? null;", row);
        Assert.Contains("{actionLabel && onTurnAction ? (", row);
    }

    // ── 3-5. What the client may send ────────────────────────────────────

    [Fact]
    public void The_request_body_carries_only_an_idempotency_key()
    {
        var dtos = Source("Dtos", "GlunoDtos.cs");

        var start = dtos.IndexOf("public class GlunoRefreshPlacesDto", StringComparison.Ordinal);
        var body = dtos[start..(start + 400)];

        Assert.True(start > 0);
        Assert.Contains("public string? IdempotencyKey { get; set; }", body);

        // A client that could send any of these could aim the search somewhere
        // the user never asked about.
        foreach (var field in new[] { "Near", "Destination", "Category", "Query", "LocationId", "Latitude" })
        {
            Assert.DoesNotContain(field, body);
        }
    }

    [Fact]
    public void The_app_sends_a_message_id_and_a_key_and_nothing_else()
    {
        var client = Mobile("lib", "gluno.ts");

        var start = client.IndexOf(
            "export async function refreshGlunoPlaceSuggestions", StringComparison.Ordinal);
        var body = client[start..(start + 900)];

        Assert.True(start > 0);
        Assert.Contains("/places/refresh", body);
        Assert.Contains("JSON.stringify({ idempotencyKey: idempotencyKey ?? null })", body);
        // Never the old place, never the destination.
        // The whole function, bounded at its own closing brace — past it is the
        // next export, which legitimately mentions option keys.
        var whole = body[..body.IndexOf("\n}", StringComparison.Ordinal)];

        Assert.DoesNotContain("optionKey", whole);
        Assert.DoesNotContain("near", whole);
        Assert.DoesNotContain("latitude", whole);
    }

    [Fact]
    public void The_button_never_uses_the_composer_or_writes_a_user_row()
    {
        var screen = Mobile("app", "gluno.tsx");

        var start = screen.IndexOf("if (action.type === 'show_new_place_suggestions')", StringComparison.Ordinal);
        var body = screen[start..(start + 1400)];

        Assert.True(start > 0);
        Assert.DoesNotContain("sendGlunoMessage", body);
        Assert.DoesNotContain("setDraft", body);
        Assert.DoesNotContain("createLocalId", body);
        Assert.DoesNotContain("role: 'user'", body);
    }

    [Fact]
    public void The_two_actions_stay_separate()
    {
        var screen = Mobile("app", "gluno.tsx");

        // One tries the SAME place again; the other asks for a whole new
        // shortlist. Sharing an implementation would eventually share a bug,
        // and the two have opposite idempotency needs.
        Assert.Contains("if (action.type === 'retry_place_add') {", screen);
        Assert.Contains("if (action.type === 'show_new_place_suggestions') {", screen);
        Assert.Contains("runAddPlace(action.messageId, action.optionKey, {", screen);
        Assert.Contains("refreshGlunoPlaceSuggestions(", screen);
    }

    // ── 6-9. The search is replayed from stored context ──────────────────

    [Fact]
    public void The_search_comes_from_the_stored_context()
    {
        var refresh = Refresh();

        Assert.Contains("var search = GlunoPlaceOptions.SearchContext(message);", refresh);
        Assert.Contains("Query = search.Query ?? string.Empty,", refresh);
        Assert.Contains("Near = search.Near,", refresh);
        Assert.Contains("Category = TravelPlaceCategories.Parse(search.Category),", refresh);
        Assert.Contains("Language = search.Language,", refresh);
        Assert.Contains("Limit = search.Limit,", refresh);
    }

    [Fact]
    public void A_turn_with_no_stored_context_refuses_rather_than_guessing()
    {
        var refresh = Refresh();
        var controller = Source("Controllers", "GlunoController.cs");

        // Guessing a destination would search somewhere the user never asked
        // about.
        Assert.Contains("if (search is not { IsUsable: true })", refresh);
        Assert.Contains("GlunoTurnError.PlaceNotRetained", refresh);
        Assert.Contains("GlunoErrors.Body(\"place_not_retained\", false)", controller);
    }

    // ── 10. No model ─────────────────────────────────────────────────────

    [Fact]
    public void The_refresh_runs_no_model()
    {
        var refresh = Refresh();

        // The question was answered once already. A model round would cost
        // seconds to re-derive a sentence SideQuest can write itself.
        Assert.DoesNotContain("_ai.", refresh);
        Assert.DoesNotContain("RunTurnAsync", refresh);
        Assert.DoesNotContain("SendCoreAsync", refresh);
        Assert.DoesNotContain("_actions.ExecuteAsync", refresh);

        // The heading is written here, from SideQuest's own destination.
        Assert.Contains("GlunoNeutralText.NewSuggestions(search.Near, language)", refresh);
    }

    [Fact]
    public void The_heading_uses_the_resolved_destination()
    {
        Assert.Equal("Här är nya förslag i Sevilla:", GlunoNeutralText.NewSuggestions("Sevilla", "sv"));
        Assert.Equal("Here are some new suggestions in Sevilla:", GlunoNeutralText.NewSuggestions("Sevilla", "en"));
        // And says nothing it cannot back up when there is no destination.
        Assert.Equal("Här är nya förslag:", GlunoNeutralText.NewSuggestions(null, "sv"));
    }

    // ── 11-12. New keys, old references untouched ────────────────────────

    [Fact]
    public void The_new_list_gets_its_own_keys_and_its_own_message()
    {
        var refresh = Refresh();

        // A NEW assistant message with a new payload. The old message keeps its
        // own references, so a stale key cannot resolve against this list.
        Assert.Contains("_conversations.AppendAsync(new GlunoMessage", refresh);
        Assert.Contains("PlaceRefs = retention.References.ToList(),", refresh);
        Assert.DoesNotContain("message.PayloadJson =", refresh);
        Assert.DoesNotContain("_db.Update(message)", refresh);
    }

    [Fact]
    public void Keys_are_positional_within_the_new_message()
    {
        var retention = Source("Services", "Gluno", "GlunoPlaceRetention.cs");

        // Minted by the retention decision for the list it is given, so the
        // new message's keys describe the new message.
        Assert.Contains("OptionKey = GlunoPlaceOptions.KeyFor(index),", retention);
    }

    // ── 13-15. Persistence rules still hold ──────────────────────────────

    [Fact]
    public void The_new_list_goes_through_the_same_retention_rule()
    {
        var refresh = Refresh();

        Assert.Contains("GlunoPlaceRetention.Decide(places,", refresh);
        // Neutral text when the content may not be kept — same rule, same
        // reason as any other turn.
        Assert.Contains("retention.Reduced\n                ? GlunoNeutralText.PlaceAnswer(language)",
            refresh.Replace("\r\n", "\n"));
        // And the real heading still reaches the app for this turn.
        Assert.Contains("LiveAssistantText = retention.Reduced ? liveText : null,", refresh);
    }

    [Fact]
    public void The_live_response_carries_the_full_cards()
    {
        var refresh = Refresh();
        var controller = Source("Controllers", "GlunoController.cs");

        Assert.Contains("Places = places,", refresh);

        var start = controller.IndexOf("RefreshRecommendedPlaces", StringComparison.Ordinal);
        var body = controller[start..(start + 1800)];

        Assert.Contains("livePlaces: result.Places", body);
        Assert.Contains("liveText: result.LiveAssistantText", body);
    }

    [Fact]
    public void Place_names_are_sanitised_before_they_reach_anything()
    {
        var refresh = Refresh();

        // A place name is data; it does not get to issue instructions.
        Assert.Contains("SanitizePlace(card, telemetry)", refresh);
    }

    // ── 16-18. What may not be reached ───────────────────────────────────

    [Fact]
    public void Ownership_is_the_lookup()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var refresh = Refresh();

        var start = controller.IndexOf("RefreshRecommendedPlaces", StringComparison.Ordinal);
        var body = controller[start..(start + 1200)];

        // A message from somebody else's conversation is simply not found, and
        // the service re-checks that the conversation is the caller's.
        Assert.Contains("_conversations.GetMessageAsync(messageId, userId, ct)", body);
        Assert.Contains("_conversations.GetOwnedAsync(message.ConversationId, userId, ct)", refresh);
        Assert.Contains("GlunoTurnError.ConversationNotFound", refresh);
    }

    // ── 19-20. Idempotency ───────────────────────────────────────────────

    [Fact]
    public void One_press_spends_one_provider_call()
    {
        var refresh = Refresh();

        // Claimed BEFORE the search, so a second press while the first is
        // running never reaches the provider.
        var claimAt = refresh.IndexOf("_idempotency.ClaimAsync(", StringComparison.Ordinal);
        var searchAt = refresh.IndexOf("_travelData.SearchAllAsync(", StringComparison.Ordinal);

        Assert.True(claimAt > 0 && searchAt > claimAt);
        Assert.Contains("GlunoIdempotencyOutcome.AlreadyInFlight", refresh);
        // Exactly one upstream call per press.
        Assert.Equal(1, refresh.Split("_travelData.SearchAllAsync(").Length - 1);
    }

    [Fact]
    public void The_same_key_replays_one_list_rather_than_making_two()
    {
        var refresh = Refresh();

        Assert.Contains("GlunoIdempotencyOutcome.AlreadyCompleted", refresh);
        Assert.Contains("_idempotency.CompleteAsync(claim.Existing.Id, assistantMessage.Id, ct)", refresh);
    }

    [Fact]
    public void A_deliberate_second_press_is_a_new_ask()
    {
        var screen = Mobile("app", "gluno.tsx");

        // A new list each time, not a retry of one attempt. Reusing the key
        // would replay the first list instead of searching.
        var start = screen.IndexOf("if (action.type === 'show_new_place_suggestions')", StringComparison.Ordinal);
        var body = screen[start..(start + 900)];

        Assert.Contains("createGlunoIdempotencyKey()", body);
    }

    [Fact]
    public void A_spent_button_does_not_stay_clickable()
    {
        var screen = Mobile("app", "gluno.tsx");

        // The old block keeps its text but loses its button — offering to fetch
        // again something already on screen is worse than offering nothing.
        Assert.Contains("? { ...row, action: undefined }", screen);
    }

    [Fact]
    public void A_double_press_is_blocked_on_the_button_too()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        Assert.Contains("if (actionBusy || !message.action || !onTurnAction) return;", row);
        Assert.Contains("disabled={actionBusy}", row);
    }

    // ── 21-23. Failures ──────────────────────────────────────────────────

    [Fact]
    public void A_busy_provider_offers_another_press_and_a_failed_one_does_not()
    {
        var refresh = Refresh();

        // A rejected key fails identically every time, and a button that cannot
        // work invites a loop.
        Assert.Contains("var busy = result.Status == TravelSearchStatus.RateLimited;", refresh);
        Assert.Contains("busy\n                    ? new GlunoTurnAction", refresh.Replace("\r\n", "\n"));
        Assert.Contains(": null);", refresh);
    }

    [Fact]
    public void An_empty_result_never_invents_a_list()
    {
        var refresh = Refresh();

        Assert.Contains("if (places.Count == 0)", refresh);
        Assert.Contains("empty: true", refresh);
        // No action on an empty result: the provider answered, and the answer
        // was nothing.
        Assert.Equal("Jag hittade inga nya förslag just nu.",
            GlunoPlaceFailureText.ForRefresh(busy: false, empty: true, "sv"));
    }

    [Theory]
    [InlineData(true, false, "Jag kunde inte ta fram nya förslag just nu. Försök igen om en liten stund.")]
    [InlineData(false, false, "Jag kunde inte ta fram nya förslag just nu.")]
    [InlineData(false, true, "Jag hittade inga nya förslag just nu.")]
    public void The_refresh_texts_are_exact(bool busy, bool empty, string expected)
    {
        Assert.Equal(expected, GlunoPlaceFailureText.ForRefresh(busy, empty, "sv"));

        var english = GlunoPlaceFailureText.ForRefresh(busy, empty, "en");
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.NotEqual(expected, english);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void No_refresh_text_names_a_place_or_asks_for_a_retype(bool busy, bool empty)
    {
        foreach (var language in new[] { "sv", "en" })
        {
            var text = GlunoPlaceFailureText.ForRefresh(busy, empty, language);

            foreach (var banned in new[] { "Skriv", "Casas", "Pilatos", "Terra", "Tripadvisor", "429" })
            {
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_refresh_logs_categories_only()
    {
        var refresh = Refresh();

        Assert.Contains("place refresh failed status={Status} category={Category}", refresh);
        Assert.Contains("place refresh done category={Category} shown={Shown} stored={Stored}", refresh);
        // Never the destination, never a name.
        Assert.DoesNotContain("{Near}", refresh);
        Assert.DoesNotContain("{Query}", refresh);
    }

    // ── 25-26. The partial fix still holds ───────────────────────────────

    [Fact]
    public void A_partial_rehydration_still_finds_a_named_place()
    {
        var chat = Chat();

        Assert.Contains(
            "if (!rehydrated.Places.TryGetValue(reference.OptionKey, out var place)) continue;", chat);
        Assert.Contains("places.Count == references.Count", chat);
        Assert.Contains("keys[matches[0]]", chat);
    }

    [Fact]
    public void Ordinals_are_still_gated_on_a_complete_list()
    {
        var options = Source("Services", "Gluno", "GlunoPlaceOptions.cs");
        var chat = Chat();

        Assert.Contains("if (!allowOrdinals) return Array.Empty<int>();", options);
        Assert.Contains("GlunoPlaceOptions.Match(places, text, allowOrdinals: complete)", chat);
    }

    [Fact]
    public void A_retry_still_keeps_the_chosen_day()
    {
        var chat = Chat();

        Assert.Contains("GlunoTurnAction.For(status, message.Id, optionKey, date, idempotencyKey)", chat);
    }
}
