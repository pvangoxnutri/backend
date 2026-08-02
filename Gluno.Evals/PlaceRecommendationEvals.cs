using System.Text.Json;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for tapping a recommendation instead of retyping it.
///
/// THE PROBLEM. Gluno could suggest Real Alcázar and five others, and they
/// arrived as prose. The information was there and the action was not: to act
/// on one, somebody had to read the paragraph, pick a name, and type it back
/// into the chat.
///
/// THE DESIGN DECISION WORTH KNOWING. No new table. The turn that recommends a
/// place already persists everything the provider returned — name, rating,
/// hours, image, coordinates — in its own message payload, and that already
/// survives a reload and is already ownership-scoped. A parallel
/// recommendation-set table would be a second copy of the same truth, and the
/// two would drift.
///
/// THE SECURITY DECISION. A tap sends back a positional key scoped to the
/// message, never a provider id. "tripadvisor:12345" identifies a place in the
/// world and says nothing about whether this user was shown it; "place-1" says
/// "the second thing you showed me", which the server can check.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class PlaceRecommendationEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static GlunoPlaceCard Place(string name) => new()
    {
        Provider = "tripadvisor",
        ExternalId = $"tripadvisor:{name.GetHashCode():X}",
        Name = name,
        Category = "attraction",
        SourceAttribution = "Data provided by Tripadvisor",
    };

    private static GlunoMessage MessageWith(params GlunoPlaceCard[] places) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Role = GlunoMessageRoles.Assistant,
        Text = "De här passar bäst i Sevilla:",
        PayloadJson = JsonSerializer.Serialize(
            new GlunoAssistantPayload { Places = places.ToList() }, GlunoJson.Options),
    };

    // ── The key ──────────────────────────────────────────────────────────

    [Fact]
    public void Every_place_gets_a_server_generated_positional_key()
    {
        Assert.Equal("place-0", GlunoPlaceOptions.KeyFor(0));
        Assert.Equal("place-3", GlunoPlaceOptions.KeyFor(3));
    }

    [Fact]
    public void A_key_resolves_to_the_place_at_that_position()
    {
        var message = MessageWith(Place("Real Alcázar"), Place("Catedral"), Place("Metropol"));

        Assert.Equal("Real Alcázar", GlunoPlaceOptions.Resolve(message, "place-0")?.Name);
        Assert.Equal("Metropol", GlunoPlaceOptions.Resolve(message, "place-2")?.Name);
    }

    [Fact]
    public void A_manipulated_key_resolves_to_nothing()
    {
        var message = MessageWith(Place("Real Alcázar"));

        foreach (var key in new[] { "place-9", "place--1", "place-", "tripadvisor:123", "0", "", null })
        {
            Assert.Null(GlunoPlaceOptions.Resolve(message, key));
        }
    }

    [Fact]
    public void A_key_is_parsed_strictly_rather_than_coerced()
    {
        // The only source of a valid key is a card this backend rendered.
        Assert.Equal(-1, GlunoPlaceOptions.IndexOf("place-abc"));
        Assert.Equal(-1, GlunoPlaceOptions.IndexOf("PLACE-0"));
        Assert.Equal(-1, GlunoPlaceOptions.IndexOf("place 0"));
        Assert.Equal(0, GlunoPlaceOptions.IndexOf("place-0"));
    }

    [Fact]
    public void A_message_from_another_conversation_carries_its_own_places_only()
    {
        var mine = MessageWith(Place("Real Alcázar"));
        var theirs = MessageWith(Place("Colosseum"));

        // The key is positional and scoped to a message, so the same key on a
        // different message resolves to that message's place — and the message
        // lookup is itself ownership-scoped.
        Assert.Equal("Real Alcázar", GlunoPlaceOptions.Resolve(mine, "place-0")?.Name);
        Assert.Equal("Colosseum", GlunoPlaceOptions.Resolve(theirs, "place-0")?.Name);
    }

    [Fact]
    public void A_message_with_no_payload_resolves_to_nothing()
    {
        var bare = new GlunoMessage { Role = GlunoMessageRoles.Assistant, Text = "hej" };

        Assert.Null(GlunoPlaceOptions.Resolve(bare, "place-0"));
    }

    [Fact]
    public void An_unreadable_payload_does_not_throw()
    {
        var broken = new GlunoMessage
        {
            Role = GlunoMessageRoles.Assistant,
            Text = "hej",
            PayloadJson = "{ not json",
        };

        // This runs on a tap. A payload that cannot be read is a reason to
        // return nothing, never to fail the request.
        Assert.Null(GlunoPlaceOptions.Resolve(broken, "place-0"));
    }

    [Fact]
    public void The_shortlist_is_capped()
    {
        // Past six, a recommendation stops being a shortlist and becomes a
        // directory.
        Assert.Equal(6, GlunoPlaceOptions.MaxPlaces);
    }

    // ── The endpoints ────────────────────────────────────────────────────

    [Fact]
    public void Both_endpoints_identify_the_place_by_route_rather_than_body()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        // No name, no provider id, no coordinates in the body — all three are
        // things the server already knows and a client could otherwise change.
        Assert.Contains("[HttpGet(\"messages/{messageId:guid}/places/{optionKey}\")]", controller);
        Assert.Contains("[HttpPost(\"messages/{messageId:guid}/places/{optionKey}/add\")]", controller);
    }

    [Fact]
    public void The_add_body_carries_no_place_data()
    {
        var dtos = Source("Dtos", "GlunoDtos.cs");

        var start = dtos.IndexOf("public class GlunoAddPlaceDto", StringComparison.Ordinal);
        var body = dtos[start..(start + 500)];

        Assert.True(start > 0);
        Assert.DoesNotContain("Name", body);
        Assert.DoesNotContain("Latitude", body);
        Assert.DoesNotContain("ExternalId", body);
    }

    [Fact]
    public void Ownership_is_the_lookup_on_both_endpoints()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 1400)];

        // A message from somebody else's conversation is simply not found.
        Assert.Contains("_conversations.GetMessageAsync(messageId, GetUserId(), ct)", body);
    }

    [Fact]
    public void The_detail_endpoint_reads_back_rather_than_searching_again()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        var start = controller.IndexOf("public async Task<ActionResult<GlunoPlaceDto>> GetRecommendedPlace", StringComparison.Ordinal);
        var body = controller[start..(start + 1400)];

        // A second lookup could return different data, and the card would then
        // show something the user was never recommended.
        Assert.Contains("GlunoPlaceOptions.Resolve(message, optionKey)", body);
        Assert.DoesNotContain("_places.Search", body);
    }

    [Fact]
    public void Tapping_a_place_needs_no_model_round()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var end = chat.IndexOf("/// The intent an \"add this place\" tap represents", StringComparison.Ordinal);
        var body = chat[start..end];

        Assert.True(start > 0 && end > start);
        // Which place the user meant is already settled by the key they
        // tapped, and the place's data came from a provider rather than from a
        // sentence.
        Assert.DoesNotContain("RunModelAsync", body);
        Assert.DoesNotContain("_provider", body);
    }

    // ── Adding is a proposal, never a write ──────────────────────────────

    [Fact]
    public void Adding_a_place_produces_a_proposal_and_nothing_else()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var end = chat.IndexOf("/// The intent an \"add this place\" tap represents", StringComparison.Ordinal);
        var body = chat[start..end];

        // The same proposal a chat turn would make, so it goes through the
        // same review, the same conflict checks and the same explicit Apply.
        Assert.Contains("CreateProposalsAsync(conversation, assistantMessage.Id, [proposal], ct)", body);
        Assert.DoesNotContain("_db.TripActivities.Add", body);
        Assert.DoesNotContain("_apply", body);
    }

    [Fact]
    public void There_is_still_exactly_one_place_a_proposal_is_created()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Adding from a recommendation goes through the same helper as a chat
        // turn. A second creation path would be a way past the draft flow.
        Assert.Equal(1, chat.Split("_proposals.CreateAsync(").Length - 1);
    }

    [Fact]
    public void Membership_is_rechecked_at_tap_time()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 3000)];

        // A recommendation can be tapped long after it was shown, and a stale
        // card must not be an access path.
        Assert.Contains("_db.TripMembers.AnyAsync", body);
    }

    [Fact]
    public void A_day_outside_the_trip_is_refused()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 5000)];

        Assert.Contains("TripDateRange.Contains(trip.StartDate, trip.EndDate, chosen.Value)", body);
    }

    [Fact]
    public void An_ambiguous_day_asks_rather_than_guessing()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 5000)];

        // Guessing a day on a two-week holiday is guessing at the shape of
        // somebody's itinerary, and the cost of being wrong is an Activity in
        // the wrong place.
        Assert.Contains("AskPlaceDayAsync(", body);
        Assert.Contains("GlunoClarificationBuilder.DayOptions(", body);
    }

    [Fact]
    public void An_ambiguous_Adventure_asks_with_the_usual_card()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 3000)];

        Assert.Contains("AskWhichAdventureAsync(", body);
    }

    [Fact]
    public void The_proposal_carries_only_provider_data()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("var livePayload = JsonSerializer.SerializeToElement(new", StringComparison.Ordinal);
        var body = chat[start..(start + 700)];

        Assert.True(start > 0);
        // Every field comes off the place the provider returned. Nothing is
        // written by a model and no number is invented.
        Assert.Contains("title = place.Name", body);
        Assert.Contains("latitude = place.Latitude", body);
        Assert.Contains("placeId = place.ExternalId", body);
    }

    // ── The mobile side ──────────────────────────────────────────────────

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }
            .Concat(parts).ToArray()));

    [Fact]
    public void Several_recommendations_render_as_a_shortlist_rather_than_cards()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // One place is the answer and deserves a full card. Six full cards is
        // a page nobody scrolls to the end of.
        Assert.Contains("places.length === 1 ?", row);
        Assert.Contains("<GlunoPlaceRecommendationList", row);
    }

    [Fact]
    public void The_shortlist_row_is_tappable_as_a_whole()
    {
        var list = Mobile("components", "gluno", "GlunoPlaceRecommendationList.tsx");

        Assert.Contains("<TouchableOpacity", list);
        Assert.Contains("accessibilityRole=\"button\"", list);
        Assert.Contains("onPress={() => onSelect(place)}", list);
    }

    [Fact]
    public void The_shortlist_sends_back_the_server_key()
    {
        var list = Mobile("components", "gluno", "GlunoPlaceRecommendationList.tsx");

        // Never a provider id, a name or a coordinate.
        Assert.Contains("place.optionKey", list);
        Assert.DoesNotContain("place.externalId", list);
    }

    [Fact]
    public void An_absent_rating_renders_as_absent()
    {
        var list = Mobile("components", "gluno", "GlunoPlaceRecommendationList.tsx");
        var detail = Mobile("components", "gluno", "GlunoPlaceDetailCard.tsx");

        // A placeholder reads as zero, which is worse than saying nothing.
        Assert.Contains("typeof place.rating === 'number'", list);
        Assert.Contains("typeof place.rating === 'number'", detail);
    }

    [Fact]
    public void A_broken_image_falls_back_rather_than_breaking_the_card()
    {
        var detail = Mobile("components", "gluno", "GlunoPlaceDetailCard.tsx");

        Assert.Contains("onError={() => setImageFailed(true)}", detail);
        // Deliberately a placeholder, not a stock photo of somewhere similar:
        // showing the wrong building is worse than showing none.
        Assert.Contains("imageFallback", detail);
    }

    [Fact]
    public void The_detail_card_has_one_primary_action()
    {
        var detail = Mobile("components", "gluno", "GlunoPlaceDetailCard.tsx");

        Assert.Contains("t('gluno.place.add')", detail);
        Assert.Contains("t('gluno.place.back')", detail);
        // A card with four buttons makes somebody choose between actions
        // before choosing about the place.
        Assert.DoesNotContain("openBrowserAsync", detail);
    }

    [Fact]
    public void A_double_tap_cannot_add_twice()
    {
        var detail = Mobile("components", "gluno", "GlunoPlaceDetailCard.tsx");
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        Assert.Contains("disabled={adding || added}", detail);
        // And the row-level guard: one add in flight at a time, and a place
        // already added is spent.
        Assert.Contains("if (!onAddPlace || addingKey !== null) return;", row);
        Assert.Contains("setAddedKeys((current) => [...current, place.optionKey])", row);
    }

    [Fact]
    public void The_add_request_is_idempotent_across_retries()
    {
        var screen = Mobile("app", "gluno.tsx");

        // Same key on a retry, so a dropped connection cannot produce two
        // proposals for one tap.
        Assert.Contains("place-${messageId}-${optionKey}", screen);
    }

    [Fact]
    public void Adding_appends_whatever_the_backend_decided_comes_next()
    {
        var screen = Mobile("app", "gluno.tsx");

        var start = screen.IndexOf("const runAddPlace = useCallback(", StringComparison.Ordinal);
        var body = screen[start..(start + 1900)];

        Assert.True(start > 0);
        // A proposal card, or a question about which Adventure or which day.
        // The app renders what came back rather than deciding.
        Assert.Contains("turn.clarification", body);
        Assert.Contains("mergeGlunoMessages(current, rows)", body);
    }

    [Fact]
    public void The_add_and_back_labels_exist_in_both_languages()
    {
        var translations = Mobile("components", "i18n-provider.tsx");

        foreach (var key in new[] { "gluno.place.add", "gluno.place.added", "gluno.place.back" })
        {
            Assert.Equal(2, translations.Split($"'{key}'").Length - 1);
        }

        Assert.Contains("'gluno.place.add': 'Lägg till'", translations);
        Assert.Contains("'gluno.place.add': 'Add'", translations);
    }

    [Fact]
    public void A_single_place_still_renders_the_original_card()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // Ordinary answers are untouched — one recommendation is the answer,
        // and it deserves the full card it always had.
        Assert.Contains("<GlunoPlaceCard key={`${message.id}-${places[0].externalId}`}", row);
    }
}
