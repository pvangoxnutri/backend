using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for a failed add that can be tried again without retyping anything.
///
/// THE PRODUCTION FAILURE. Somebody tried to add Casas de Pilatos and Gluno
/// answered, in misspelled Swedish, that it could not prepare a suggestion just
/// now and that they should write "lägg till Casas de Pilatos" again in a
/// moment.
///
/// THAT SENTENCE WAS THE MODEL'S. It got the chance to write one because
/// RefetchShownPlacesAsync returned null for every unhappy ending — a provider
/// that could not be reached looked exactly like a turn that had shown nothing.
/// The caller kept searching older turns, ran out, returned null, and the add
/// request fell through to the model.
///
/// TWO FIXES, and the second one matters as much as the first:
///
///  • A failed lookup is an ANSWER. Fixed text, and a server-owned action the
///    app renders as a button — every id needed to retry was already known.
///
///  • A PARTIAL re-fetch is useful. The list used to be discarded whole if any
///    one reference did not come back, which threw away the place the user had
///    just named. A name identifies a place wherever it sits; only ordinals
///    need the full list.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class PlaceAddRetryEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string Chat() => Source("Services", "Gluno", "GlunoChatService.cs");

    private static readonly Guid Message = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ── 1. The action ────────────────────────────────────────────────────

    [Fact]
    public void A_transient_failure_offers_a_retry_of_the_same_add()
    {
        var action = GlunoTurnAction.For(
            GlunoRehydrationStatus.Unavailable, Message, "place-2",
            new DateOnly(2026, 8, 14), "place-abc-2");

        Assert.NotNull(action);
        Assert.Equal(GlunoTurnActionTypes.RetryPlaceAdd, action!.Type);
        Assert.Equal(Message, action.MessageId);
        Assert.Equal("place-2", action.OptionKey);
        // The day the user already chose, so the retry does not ask again.
        Assert.Equal(new DateOnly(2026, 8, 14), action.Date);
        // Verbatim, so a retry cannot produce a second proposal.
        Assert.Equal("place-abc-2", action.IdempotencyKey);
    }

    [Fact]
    public void A_missing_place_offers_new_suggestions_rather_than_a_retry()
    {
        // Retrying the same lookup would fail the same way. A fresh shortlist
        // is the only thing that can help.
        var action = GlunoTurnAction.For(GlunoRehydrationStatus.NotFound, Message, "place-0");

        Assert.Equal(GlunoTurnActionTypes.ShowNewPlaceSuggestions, action!.Type);
        Assert.Null(action.OptionKey);
    }

    [Fact]
    public void A_transient_failure_with_no_identified_place_asks_for_suggestions()
    {
        // Nothing to retry: the failure happened while working out WHICH place.
        var action = GlunoTurnAction.For(GlunoRehydrationStatus.Busy, Message, optionKey: null);

        Assert.Equal(GlunoTurnActionTypes.ShowNewPlaceSuggestions, action!.Type);
    }

    [Fact]
    public void A_healthy_lookup_offers_nothing()
    {
        // A button that cannot work is worse than no button — it invites a
        // loop, and each press costs another upstream call.
        Assert.Null(GlunoTurnAction.For(GlunoRehydrationStatus.Ok, Message, "place-0"));
    }

    [Fact]
    public void The_action_carries_ids_and_nothing_else()
    {
        var properties = typeof(GlunoTurnAction).GetProperties().Select(p => p.Name).ToList();

        Assert.Equal(5, properties.Count);
        foreach (var field in new[] { "Type", "MessageId", "OptionKey", "Date", "IdempotencyKey" })
        {
            Assert.Contains(field, properties);
        }

        // Never a place name, a coordinate or a provider id — the route the
        // client sends these back to already knows all three.
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("Latitude", properties);
        Assert.DoesNotContain("LocationId", properties);
    }

    // ── 2-5. The texts ───────────────────────────────────────────────────

    [Theory]
    [InlineData(GlunoRehydrationStatus.Unavailable, "Jag kunde inte förbereda förslaget just nu.")]
    [InlineData(GlunoRehydrationStatus.Busy, "Jag kunde inte hämta platsen just nu. Försök igen om en liten stund.")]
    [InlineData(GlunoRehydrationStatus.NotFound, "Jag kunde inte hämta platsen igen. Ta fram nya förslag.")]
    public void The_swedish_texts_are_exact(GlunoRehydrationStatus status, string expected)
    {
        Assert.Equal(expected, GlunoPlaceFailureText.For(status, "sv"));
    }

    [Theory]
    [InlineData(GlunoRehydrationStatus.Unavailable)]
    [InlineData(GlunoRehydrationStatus.Busy)]
    [InlineData(GlunoRehydrationStatus.NotFound)]
    public void Every_status_has_an_english_text_too(GlunoRehydrationStatus status)
    {
        var english = GlunoPlaceFailureText.For(status, "en");

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.NotEqual(GlunoPlaceFailureText.For(status, "sv"), english);
    }

    [Theory]
    [InlineData(GlunoRehydrationStatus.Unavailable)]
    [InlineData(GlunoRehydrationStatus.Busy)]
    [InlineData(GlunoRehydrationStatus.NotFound)]
    public void No_failure_text_asks_the_user_to_retype_anything(GlunoRehydrationStatus status)
    {
        foreach (var language in new[] { "sv", "en" })
        {
            var text = GlunoPlaceFailureText.For(status, language);

            // The exact production wording, and the family it belongs to.
            foreach (var banned in new[]
            {
                "Skriv", "skriv \"", "igen om en liten stund, så",
                "samma meddelande", "Try adding it again", "Ask me to add it again",
                "type", "again if",
            })
            {
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
            }

            // And never the place itself.
            Assert.DoesNotContain("Casas", text);
            Assert.DoesNotContain("Pilatos", text);
        }
    }

    [Fact]
    public void The_texts_are_fixed_strings_rather_than_the_models()
    {
        var source = Source("Services", "Gluno", "GlunoTurnAction.cs");

        // Nothing here interpolates, formats or takes a place — the only
        // inputs are a status and a language.
        Assert.DoesNotContain("GlunoPlaceCard", source);
        Assert.DoesNotContain("place.Name", source);
        Assert.DoesNotContain("$\"", source);
    }

    // ── The first failure point ──────────────────────────────────────────

    [Fact]
    public void A_failed_lookup_is_an_answer_rather_than_a_reason_to_keep_looking()
    {
        var chat = Chat();

        // THE BUG. `if (refetched == null) continue;` made a provider failure
        // and an empty turn the same thing, and the add request ended up at the
        // model.
        // Asserted positively: both call sites now branch on the status. The
        // old `if (refetched == null) continue;` cannot coexist with that,
        // and checking for its absence would only match the comment above it
        // explaining what it used to do.
        Assert.Equal(2, chat.Split(
            "if (refetched.Status != GlunoRehydrationStatus.Ok)").Length - 1);
        Assert.Contains("PlaceLookupFailedAsync(\n                        conversation, userId, message, refetched.Status, ct)",
            chat.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_partial_refetch_still_finds_a_named_place()
    {
        var chat = Chat();

        // Terra re-ranks between calls, so one of six sliding out is ordinary.
        // Discarding the whole list because of it threw away the place the user
        // had just named.
        Assert.DoesNotContain(
            "if (!rehydrated.Places.TryGetValue(reference.OptionKey, out var place)) return null;", chat);
        Assert.Contains(
            "if (!rehydrated.Places.TryGetValue(reference.OptionKey, out var place)) continue;", chat);
        Assert.Contains("places.Count == references.Count", chat);
    }

    [Fact]
    public void A_short_list_never_renumbers_the_cards()
    {
        var options = Source("Services", "Gluno", "GlunoPlaceOptions.cs");
        var chat = Chat();

        // "The fourth one" means the fourth CARD. Counting positions in a short
        // list would silently point at a different place.
        Assert.Contains("if (!allowOrdinals) return Array.Empty<int>();", options);
        Assert.Contains("GlunoPlaceOptions.Match(places, text, allowOrdinals: complete)", chat);
    }

    [Fact]
    public void The_option_key_comes_from_the_reference_not_the_position()
    {
        var chat = Chat();

        // With a short list `KeyFor(index)` points at the wrong card — which
        // would have added a place the user never named.
        Assert.Contains("keys[matches[0]]", chat);

        // Scoped to the RE-FETCHED path, where the list can come back short.
        // The recovery search's list is persisted dense in the same breath,
        // so its positional key is the reference key by construction.
        var start = chat.IndexOf(
            "private async Task<GlunoTurnResult?> AddNamedPlaceAsync(", StringComparison.Ordinal);
        var end = chat.IndexOf(
            "private async Task<GlunoTurnResult> PlaceLookupFailedAsync(", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start);
        Assert.DoesNotContain("GlunoPlaceOptions.KeyFor(matches[0])", chat[start..end]);
    }

    // ── 6-13. What a retry reuses ────────────────────────────────────────

    [Fact]
    public void The_retry_goes_back_to_the_same_route_with_the_same_ids()
    {
        var screen = Mobile("app", "gluno.tsx");

        var start = screen.IndexOf("const handleTurnAction = useCallback", StringComparison.Ordinal);
        var body = screen[start..(start + 1200)];

        Assert.True(start > 0);
Assert.Contains("action.type === 'retry_place_add'", body);
        Assert.Contains("runAddPlace(action.messageId, action.optionKey, {", body);
        Assert.Contains("date: action.date,", body);
        Assert.Contains("idempotencyKey: action.idempotencyKey,", body);
    }

    [Fact]
    public void The_retry_never_sends_a_chat_message()
    {
        var screen = Mobile("app", "gluno.tsx");

        var start = screen.IndexOf("const handleTurnAction = useCallback", StringComparison.Ordinal);
        var body = screen[start..(start + 1200)];

        // No composer, no new user row — the add route is not a chat turn.
        Assert.DoesNotContain("sendGlunoMessage", body);
        Assert.DoesNotContain("setDraft", body);
        Assert.DoesNotContain("createLocalId", body);
        Assert.DoesNotContain("role: 'user'", body);
    }

    [Fact]
    public void The_retry_shares_one_code_path_with_the_button()
    {
        var screen = Mobile("app", "gluno.tsx");

        // A retry IS the same call. Two implementations would be two places for
        // the idempotency key to drift.
        Assert.Contains("const runAddPlace = useCallback(", screen);
        Assert.Contains("(messageId: string, place: GlunoPlace) => runAddPlace(messageId, place.optionKey)", screen);
    }

    [Fact]
    public void A_double_press_runs_at_most_one_attempt()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // The state that draws the spinner is the state that blocks the press.
        Assert.Contains("if (actionBusy || !message.action || !onTurnAction) return;", row);
        Assert.Contains("disabled={actionBusy}", row);
        Assert.Contains("accessibilityState={{ disabled: actionBusy, busy: actionBusy }}", row);
    }

    [Fact]
    public void The_same_idempotency_key_survives_the_round_trip()
    {
        var chat = Chat();
        var client = Mobile("lib", "gluno.ts");

        // Server hands it back on the failure...
        Assert.Contains("GlunoTurnAction.For(status, message.Id, optionKey, date, idempotencyKey)", chat);
        // ...and the app returns it unchanged, so at most one proposal exists
        // even if the first attempt eventually succeeded.
        Assert.Contains("idempotencyKey: options?.idempotencyKey ?? null,", client);
    }

    [Fact]
    public void The_action_is_live_only_and_never_stored()
    {
        var chat = Chat();
        var cache = Mobile("lib", "gluno-cache.ts");

        // It is rebuilt from ids the server owns, so a reload simply does not
        // offer it — rather than offering a retry for a failure nobody
        // remembers.
        Assert.Contains("Action = action,", chat);
        Assert.DoesNotContain("PayloadJson = JsonSerializer.Serialize(action", chat);
        Assert.Contains("NOT CACHED", cache);
    }

    // ── The button ───────────────────────────────────────────────────────

    [Fact]
    public void Only_an_actionable_type_is_drawn()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // show_new_place_suggestions arrives but has no handler yet, so it is
        // not drawn rather than drawn dead.
Assert.Contains("retry_place_add: 'gluno.error.retry'", row);
        Assert.Contains("{actionLabel && onTurnAction ? (", row);
    }

    [Fact]
    public void The_button_shows_progress_on_itself()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        var start = row.IndexOf("style={styles.actionButton}", StringComparison.Ordinal);
        var body = row[start..(start + 700)];

        Assert.True(start > 0);
        Assert.Contains("<ActivityIndicator", body);
    }

    // ── No model, ever ───────────────────────────────────────────────────

    [Fact]
    public void Nothing_on_the_retry_path_runs_a_model()
    {
        var chat = Chat();

        foreach (var method in new[]
        {
            "private async Task<GlunoTurnResult> PlaceLookupFailedAsync",
            "private async Task<GlunoTurnResult> AddPlaceFromKeyAsync",
        })
        {
            var start = chat.IndexOf(method, StringComparison.Ordinal);
            var body = chat[start..(start + 2400)];

            Assert.True(start > 0, method);
            Assert.DoesNotContain("_ai.", body);
            Assert.DoesNotContain("RunTurnAsync", body);
            Assert.DoesNotContain("SendCoreAsync", body);
        }
    }

    // ── 8. Diagnostics ───────────────────────────────────────────────────

    [Fact]
    public void The_failure_is_logged_as_categories_only()
    {
        var chat = Chat();

        Assert.Contains("place add lookup failed status={Status} action={Action}", chat);

        var start = chat.IndexOf("place add lookup failed", StringComparison.Ordinal);
        var line = chat[start..(start + 220)];

        // A status and an action type. Never the place, never the query.
        Assert.DoesNotContain("{Name}", line);
        Assert.DoesNotContain("{Query}", line);
        Assert.DoesNotContain("{LocationId}", line);
    }

    // ── 17-19. Rehydration is unchanged where it was right ───────────────

    [Fact]
    public void The_exact_location_id_is_still_the_only_identity_match()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        Assert.Contains(
            "string.Equals(candidate.ProviderPlaceId, reference.LocationId, StringComparison.Ordinal)",
            rehydrator);
        Assert.DoesNotContain("GlunoPlaceOptions.Match", rehydrator);
    }

    [Fact]
    public void The_fallback_still_runs_at_most_once_and_not_after_a_provider_failure()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        Assert.Equal(2, rehydrator.Split("await LookUpAsync(").Length - 1);
        Assert.DoesNotContain("while (", rehydrator);
        Assert.Contains(
            "if (first.Status != TravelSearchStatus.Ok && first.Status != TravelSearchStatus.Unknown)",
            rehydrator);
    }
}
