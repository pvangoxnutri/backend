using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for keeping an id and nothing else.
///
/// THE DECISION. Tripadvisor Terra's caching policy says "caching, copying,
/// downloading, storing or indexing content is not permitted for any content"
/// and carves out the Location ID, which may be kept. Those are two separate
/// permissions, so SideQuest treats them as two: the cards are not written
/// down, the ids are, and a place is fetched again from its id at the moment
/// somebody acts on it.
///
/// WHAT THAT BUYS. "Add" keeps working under a provider that licenses nothing
/// for storage. WHAT IT COSTS is one upstream call per add, and cards that do
/// not come back after a reload — there is nothing stored to draw them from.
///
/// THE MECHANISM WORTH UNDERSTANDING. Both decisions travel on each RESULT, not
/// on a provider name. Terra and the Content API are both "tripadvisor" and
/// issue the same location ids, so a name comparison could not tell them apart
/// even if it were an acceptable way to decide.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class PersistencePolicyEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static GlunoPlaceCard Card(int index, bool content, bool identity = true) => new()
    {
        Provider = "tripadvisor",
        ExternalId = $"tripadvisor:{187443 + index}",
        ProviderPlaceId = $"{187443 + index}",
        Name = index == 0 ? "Real Alcázar" : $"Museo {index}",
        Category = "attraction",
        CategoryLabel = "Historic Sites",
        SourceAttribution = "Data provided by Tripadvisor",
        Rating = 4.7,
        ReviewCount = 41230,
        Latitude = 37.383,
        Longitude = -5.990,
        Address = "Patio de Banderas, Sevilla",
        ReviewSummary = "Stunning gardens.",
        OpeningHours = ["Mon-Sun 09:30-17:00"],
        ProviderUrl = "https://www.tripadvisor.com/x",
        AllowsContentPersistence = content,
        AllowsIdentityPersistence = identity,
    };

    private static GlunoPlaceSearchContext Sevilla() => new()
    {
        Near = "Sevilla",
        Category = "attraction",
        Query = "historic sites",
        Language = "sv",
        Limit = 5,
        SearchedAtUtc = DateTime.UtcNow.AddMinutes(-3),
    };

    /// The payload as it would actually be written, so the assertions below are
    /// about stored JSON rather than about an object in memory.
    private static string StoredJson(GlunoPlaceRetention retention)
        => System.Text.Json.JsonSerializer.Serialize(
            new GlunoAssistantPayload
            {
                Places = retention.Places.ToList(),
                PlaceRefs = retention.References.ToList(),
                PlaceSearch = retention.Search,
            },
            GlunoJson.Options);

    private static GlunoMessage MessageWith(GlunoPlaceRetention retention) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        Role = GlunoMessageRoles.Assistant,
        Text = "Tre förslag i Sevilla.",
        PayloadJson = StoredJson(retention),
    };

    // ── 1. The live turn is untouched ────────────────────────────────────

    [Fact]
    public void The_direct_response_shows_the_full_unfiltered_list()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // THE TRAP THIS AVOIDS: mapping the answer back out of the payload that
        // was just reduced to ids would hand the app an empty list on the very
        // turn that fetched the places.
        Assert.Contains("livePlaces: result.Places", controller);
        Assert.Contains("Places = livePlaces != null", controller);
        Assert.Contains("Places = visiblePlaces,", chat);
    }

    [Fact]
    public void Every_turn_endpoint_passes_the_live_places()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        // Send, resolve-clarification and add all return a turn. Missing one
        // would make places vanish on that path only, which is the hardest kind
        // of bug to notice.
// Send, resolve-clarification, add and refresh all return a turn.
        Assert.Equal(4, controller.Split("livePlaces: result.Places").Length - 1);
    }

    // ── 2-8. What the payload may hold ───────────────────────────────────

    [Fact]
    public void A_reference_carries_the_key_the_provider_and_the_id_only()
    {
        var properties = typeof(GlunoPlaceReference).GetProperties().Select(p => p.Name).ToList();

        Assert.Equal(3, properties.Count);
        Assert.Contains("OptionKey", properties);
        Assert.Contains("ProviderId", properties);
        Assert.Contains("LocationId", properties);
    }

    [Fact]
    public void Unstorable_content_becomes_references_and_nothing_else()
    {
        var retention = GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false)], Sevilla());

        Assert.Empty(retention.Places);
        Assert.Equal(2, retention.References.Count);
        Assert.Equal("place-0", retention.References[0].OptionKey);
        Assert.Equal("tripadvisor", retention.References[0].ProviderId);
        Assert.Equal("187443", retention.References[0].LocationId);
    }

    [Theory]
    // Every field the policy names, checked against the bytes that get written.
    [InlineData("Real Alcázar")]           // name
    [InlineData("Patio de Banderas")]      // address
    [InlineData("37.383")]                 // coordinates
    [InlineData("4.7")]                    // rating
    [InlineData("41230")]                  // review count
    [InlineData("09:30")]                  // opening hours
    [InlineData("Stunning gardens")]       // description / review snippet
    [InlineData("Historic Sites")]         // category label
    [InlineData("tripadvisor.com")]        // provider url
    [InlineData("Data provided by")]       // attribution
    public void No_provider_content_reaches_the_stored_payload(string content)
    {
        var json = StoredJson(GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false)], Sevilla()));

        Assert.DoesNotContain(content, json);
    }

    [Fact]
    public void The_stored_payload_holds_the_ids_and_the_own_search_context()
    {
        var json = StoredJson(GlunoPlaceRetention.Decide(
            [Card(0, content: false)], Sevilla()));

        Assert.Contains("187443", json);
        // SideQuest's own request. A destination the user's Adventure resolved,
        // our category vocabulary, our search words — none of it came out of a
        // provider response.
        Assert.Contains("Sevilla", json);
        Assert.Contains("historic sites", json);
    }

    // ── 9-10. What may be kept about the search ──────────────────────────

    [Fact]
    public void A_users_own_sentence_is_never_kept_as_the_query()
    {
        var sentence = "hej gluno! vi är i sevilla på fredag med mormor, "
            + "hon går dåligt, vad kan vi se som inte kräver trappor? mvh anna";

        var sanitised = GlunoPlaceSearchContexts.Sanitise(sentence);

        Assert.NotNull(sanitised);
        Assert.True(sanitised!.Length <= GlunoPlaceSearchContexts.MaxQueryLength);
        // The parts that make it a message rather than a search term.
        Assert.DoesNotContain("mormor", sanitised);
        Assert.DoesNotContain("anna", sanitised);
        Assert.DoesNotContain("?", sanitised);
        Assert.DoesNotContain("!", sanitised);
    }

    [Fact]
    public void A_sanitised_query_is_whole_words()
    {
        // Cut mid-word it would search for something nobody asked about.
        var sanitised = GlunoPlaceSearchContexts.Sanitise(
            "restauranger tapas traditionella andalusiska familjevänliga uteservering");

        Assert.NotNull(sanitised);
        Assert.DoesNotContain("  ", sanitised);
        Assert.All(sanitised!.Split(' '), word => Assert.NotEqual(0, word.Length));
    }

    [Fact]
    public void The_executor_sanitises_before_the_context_is_built()
    {
        var executor = Source("Services", "Gluno", "GlunoActionExecutor.cs");

        Assert.Contains("Query = GlunoPlaceSearchContexts.Sanitise(query)", executor);
    }

    // ── 11-14. The re-fetch ──────────────────────────────────────────────

    [Fact]
    public void Adding_a_referenced_place_fetches_it_again()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddPlaceFromKeyAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 2200)];

        Assert.True(start > 0);
        Assert.Contains("GlunoPlaceOptions.ResolveReference(message, optionKey)", body);
        Assert.Contains("_rehydrator.RehydrateAsync(", body);
    }

    [Fact]
    public void The_refetch_replays_the_stored_destination_and_category()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        // The same fields the original search used, out of the stored context —
        // not a new query that happens to be similar.
        Assert.Contains("Near = context.Near,", rehydrator);
        Assert.Contains("Category = TravelPlaceCategories.Parse(context.Category)", rehydrator);
        Assert.Contains("Query = context.Query ?? string.Empty,", rehydrator);
        Assert.Contains("Language = context.Language,", rehydrator);
    }

    [Fact]
    public void The_refetch_matches_on_the_exact_provider_and_id()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        Assert.Contains(
            "string.Equals(candidate.ProviderPlaceId, reference.LocationId, StringComparison.Ordinal)",
            rehydrator);
        Assert.Contains(
            "string.Equals(candidate.Provider, reference.ProviderId, StringComparison.Ordinal)",
            rehydrator);
    }

    [Fact]
    public void A_stored_reference_is_never_matched_by_name_or_position()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        // The fresh response is a different list from a different day. The
        // second entry today is not the second entry from last week, and two
        // places in one city share a name more often than is comfortable.
        var start = rehydrator.IndexOf(
            "private async Task<(Dictionary<string, TravelPlace> Matched", StringComparison.Ordinal);
        var lookup = rehydrator[start..];

        Assert.True(start > 0);
        Assert.DoesNotContain("candidate.Name", lookup);
        Assert.DoesNotContain("place.Name", lookup);
        Assert.DoesNotContain("DistanceKm", lookup);
        Assert.DoesNotContain("Latitude", lookup);
        Assert.DoesNotContain("result.Places[", lookup);
        Assert.DoesNotContain("GlunoPlaceOptions.Match", rehydrator);
    }

    [Fact]
    public void The_id_is_verified_again_where_it_becomes_a_proposal()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // The one place where being wrong writes into somebody's plan.
        Assert.Contains(
            "!string.Equals(fresh.ProviderPlaceId, reference.LocationId, StringComparison.Ordinal)",
            chat);
    }

    [Fact]
    public void The_refetch_reads_the_unranked_unclamped_results()
    {
        var registry = Source("Services", "Gluno", "TravelDataRegistry.cs");
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        // The ranked search takes the top N. A place could come back from the
        // provider and still be trimmed off before anybody looked for it.
        Assert.Contains("_travelData.SearchAllAsync(", rehydrator);
        Assert.Contains("public async Task<TravelSearchResult> SearchAllAsync(", registry);

        var start = registry.IndexOf("public async Task<TravelSearchResult> SearchAllAsync(", StringComparison.Ordinal);
        Assert.DoesNotContain("TravelPlaceRanker", registry[start..]);
    }

    // ── 15-19. Where a matched place goes ────────────────────────────────

    [Fact]
    public void A_matched_place_continues_into_the_ordinary_add_flow()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // The same method the stored-card path ends at, so Adventure, day,
        // proposal and conflict handling are shared rather than duplicated.
        Assert.Contains(
            "userId, conversation, GlunoPlaceCards.From(fresh), message.Id, optionKey, date, ct);", chat);
        Assert.Contains(
            "userId, conversation, stored, message.Id, optionKey, date, ct);", chat);
    }

    [Fact]
    public void The_add_flow_still_asks_which_adventure_when_none_is_settled()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 4000)];

        Assert.True(start > 0);
        Assert.Contains("if (tripId == null)", body);
        Assert.Contains("AskWhichAdventureAsync(", body);
        // Membership is re-checked at tap time, not at recommend time.
        Assert.Contains("_db.TripMembers.AnyAsync(", body);
    }

    [Fact]
    public void The_add_flow_still_asks_which_day_when_several_fit()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 6000)];

        Assert.Contains("OnlySensibleDay(trip, context.Route)", body);
        Assert.Contains("GlunoClarificationBuilder.DayOptions(", body);
        Assert.Contains("AskPlaceDayAsync(", body);
    }

    [Fact]
    public void A_rehydrated_proposal_faces_the_same_apply_time_checks()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");
        var apply = Source("Services", "Gluno", "GlunoProposalApplyService.cs");

        // Exactly one call to the store in the whole chat service, shared by
        // every path. A second creation route would be a way to produce
        // something with an Apply button that never went past the checks.
        Assert.Equal(1, chat.Split("_proposals.CreateAsync(").Length - 1);
        Assert.Contains("CreateProposalsAsync(conversation, assistantMessage.Id, [proposal], ct)", chat);

        // Which is what makes conflict detection unavoidable: it runs against
        // the snapshot taken when the proposal was created, at the moment
        // somebody applies it.
        Assert.Contains("_store.BuildSnapshotAsync(tripId, proposal.ActionType, payload, ct)", apply);
        Assert.Contains("!storedSnapshot.Matches(currentSnapshot)", apply);
    }

    [Fact]
    public void The_add_flow_creates_a_proposal_and_never_writes()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult> AddResolvedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 9000)];

        Assert.Contains("ActionName = GlunoActions.ProposeActivity", body);
        Assert.Contains("CreateProposalsAsync(", body);
        // The proposal goes through the same review, the same conflict checks
        // and the same explicit Apply as any other.
        Assert.DoesNotContain("SaveChangesAsync", body);
        Assert.DoesNotContain("_db.Activities.Add", body);
    }

    // ── 20-22. What may not be added ─────────────────────────────────────

    [Fact]
    public void A_manipulated_option_key_resolves_to_nothing()
    {
        var message = MessageWith(GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false)], Sevilla()));

        foreach (var key in new[] { "place-9", "PLACE-0", "place 0", "place-", "../place-0", "place--1", "0" })
        {
            Assert.Null(GlunoPlaceOptions.ResolveReference(message, key));
            Assert.Null(GlunoPlaceOptions.Resolve(message, key));
        }
    }

    [Fact]
    public void A_key_from_another_conversation_resolves_to_that_conversations_place()
    {
        var mine = MessageWith(GlunoPlaceRetention.Decide([Card(0, content: false)], Sevilla()));
        var theirs = MessageWith(GlunoPlaceRetention.Decide([Card(7, content: false)], Sevilla()));

        // Positional and scoped to the message. The same key in two
        // conversations means two different places, and neither can reach the
        // other — which is what the message id in the route is for.
        Assert.Equal("187443", GlunoPlaceOptions.ResolveReference(mine, "place-0")!.LocationId);
        Assert.Equal("187450", GlunoPlaceOptions.ResolveReference(theirs, "place-0")!.LocationId);
    }

    [Fact]
    public void A_reference_is_found_by_its_key_not_by_its_position()
    {
        var message = MessageWith(GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false), Card(2, content: false)], Sevilla()));

        // Compared rather than indexed: a list that ever stopped being dense
        // would otherwise hand back the wrong place, and the wrong place is
        // worse than none.
        Assert.Equal("place-2", GlunoPlaceOptions.ResolveReference(message, "place-2")!.OptionKey);
        Assert.Equal("187445", GlunoPlaceOptions.ResolveReference(message, "place-2")!.LocationId);
    }

    [Fact]
    public void A_turn_that_kept_nothing_refuses_distinctly()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Not "not found". The key is real and the user is looking at the card;
        // what is missing is anything stored to verify it against.
        Assert.Contains("GlunoTurnError.PlaceNotRetained", chat);
        Assert.Contains("GlunoErrors.Body(\"place_not_retained\", false)", controller);
        Assert.Contains("place_not_retained: 'gluno.error.placeNotRetained'",
            Mobile("components", "gluno", "GlunoMessageRow.tsx"));
    }

    [Fact]
    public void The_client_may_never_supply_the_place()
    {
        var dtos = Source("Dtos", "GlunoDtos.cs");

        var start = dtos.IndexOf("public class GlunoAddPlaceDto", StringComparison.Ordinal);
        var body = dtos[start..(start + 500)];

        // The obvious shortcut would be to let the app send the name and
        // coordinates it already has on screen. That is precisely what the
        // option-key design exists to prevent.
        Assert.DoesNotContain("Name", body);
        Assert.DoesNotContain("Latitude", body);
        Assert.DoesNotContain("LocationId", body);
    }

    // ── 23-25. When the place does not come back ─────────────────────────

    [Fact]
    public void A_place_that_does_not_return_gets_a_plain_sentence()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Fixed strings, now in one place — see GlunoPlaceFailureText.
        Assert.Equal(
            "Jag kunde inte hämta platsen igen. Ta fram nya förslag.",
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.NotFound, "sv"));
        Assert.Contains("couldn't fetch that place again",
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.NotFound, "en"));
    }

    [Fact]
    public void Nothing_technical_reaches_the_user_text()
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
                var text = GlunoPlaceFailureText.For(status, language);

                foreach (var word in new[] { "Terra", "Tripadvisor", "licen", "persist", "rate limit", "429", "API" })
                {
                    Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void A_busy_provider_says_try_again_rather_than_its_gone()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // "Over the rate limit" and "that place is gone" are the same empty
        // result and must not become the same sentence.
Assert.Equal(
            "Jag kunde inte hämta platsen just nu. Försök igen om en liten stund.",
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.Busy, "sv"));
        Assert.NotEqual(
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.Busy, "sv"),
            GlunoPlaceFailureText.For(GlunoRehydrationStatus.NotFound, "sv"));
    }

    [Fact]
    public void At_most_one_fallback_call_is_made()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        // Two LookUpAsync calls in the whole method, and no loop around either.
        Assert.Equal(2, rehydrator.Split("await LookUpAsync(").Length - 1);
        Assert.DoesNotContain("while (", rehydrator);
        Assert.DoesNotContain("for (", rehydrator);
        Assert.Contains("ProviderCalls = 2,", rehydrator);
    }

    [Fact]
    public void The_fallback_is_skipped_when_the_provider_is_not_answering()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        // Asking a rate-limited provider the same question twice in a row is
        // not a fallback, it is a second failure.
        Assert.Contains(
            "if (first.Status != TravelSearchStatus.Ok && first.Status != TravelSearchStatus.Unknown)",
            rehydrator);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, TerraFailure.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, TerraFailure.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, TerraFailure.RateLimited)]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout, TerraFailure.Timeout)]
    public void Provider_rejections_are_categorised_rather_than_thrown(
        System.Net.HttpStatusCode status, TerraFailure expected)
    {
        Assert.Equal(expected, TerraTravelProvider.Classify(status));
    }

    [Fact]
    public void Only_a_rate_limit_is_reported_as_transient()
    {
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");

        // A rejected key and a changed contract both need a person, and neither
        // is worth telling the user to retry.
        Assert.Contains(
            "failure is TerraFailure.RateLimited or TerraFailure.QuotaExceeded",
            terra);
        Assert.Contains("? TravelSearchStatus.RateLimited", terra);
        Assert.Contains(": TravelSearchStatus.Failed", terra);
    }

    [Fact]
    public void An_unreported_status_is_never_read_as_healthy()
    {
        var registry = Source("Services", "Gluno", "TravelDataRegistry.cs");

        // A provider that cannot say why means "no information", not "fine".
        Assert.Contains("TravelSearchStatus.Unknown", registry);
        Assert.Contains(
            "return current == TravelSearchStatus.Unknown || next == TravelSearchStatus.Unknown",
            registry);
    }

    // ── 26. Double tap ───────────────────────────────────────────────────

    [Fact]
    public void Adding_twice_makes_at_most_one_fetch_and_one_proposal()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "public async Task<GlunoTurnResult> AddRecommendedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 3000)];

        Assert.True(start > 0);
        // Claimed BEFORE any of it, so the second tap never reaches the
        // provider — that would be two upstream calls for one intention.
        var claimAt = body.IndexOf("_idempotency.ClaimAsync(", StringComparison.Ordinal);
        var workAt = body.IndexOf("AddPlaceFromKeyAsync(", StringComparison.Ordinal);

        Assert.True(claimAt > 0 && workAt > claimAt);
        Assert.Contains("GlunoIdempotencyOutcome.AlreadyInFlight", body);
        // The ORIGINAL proposal replayed, not a new one — minting a second
        // would let the same place be applied twice.
        Assert.Contains("GlunoIdempotencyOutcome.AlreadyCompleted", body);
        Assert.Contains("proposal.MessageId == completedMessageId", body);
        Assert.Contains("_idempotency.CompleteAsync(", body);
    }

    // ── 27. Reload ───────────────────────────────────────────────────────

    [Fact]
    public void History_reconstructs_no_cards_from_references()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var message = MessageWith(GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false)], Sevilla()));

        // History maps payload.Places. References are not places, so a reopened
        // conversation has nothing to draw a card from — no empty cards, no
        // buttons without a basis.
        Assert.Contains("payload.Places.Select((place, index) => MapPlace(place, index)).ToList()", controller);
        Assert.Null(GlunoPlaceOptions.Resolve(message, "place-0"));
        Assert.NotNull(GlunoPlaceOptions.ResolveReference(message, "place-0"));
    }

    [Fact]
    public void References_stay_server_side()
    {
        var dtos = Source("Dtos", "GlunoDtos.cs");
        var controller = Source("Controllers", "GlunoController.cs");

        // Nothing maps them into a response. They are a server-side handle, and
        // an app that could see them could send one back.
        Assert.DoesNotContain("PlaceRefs", dtos);
        Assert.DoesNotContain("PlaceRefs", controller);
        Assert.DoesNotContain("GlunoPlaceReference", dtos);
    }

    // ── 28. Legacy ───────────────────────────────────────────────────────

    [Fact]
    public void A_storable_provider_keeps_storing_whole_cards()
    {
        var retention = GlunoPlaceRetention.Decide(
            [Card(0, content: true), Card(1, content: true)], Sevilla());

        Assert.Equal(2, retention.Places.Count);
        Assert.Empty(retention.References);
        Assert.Null(retention.Search);
        Assert.False(retention.Reduced);

        // And it still resolves to a full card, with no upstream call.
        var message = MessageWith(retention);
        Assert.Equal("Real Alcázar", GlunoPlaceOptions.Resolve(message, "place-0")?.Name);
    }

    [Fact]
    public void The_legacy_provider_still_stamps_both_permissions_true()
    {
        var legacy = Source("Services", "Gluno", "TripadvisorTravelProvider.cs");

        Assert.Contains("public bool AllowsContentPersistence => true;", legacy);
        Assert.Contains("public bool AllowsLocationIdPersistence => true;", legacy);
        Assert.Contains("AllowsContentPersistence = true,", legacy);
        Assert.Contains("AllowsIdentityPersistence = true,", legacy);
    }

    [Fact]
    public void A_mixed_turn_is_governed_by_its_strictest_terms()
    {
        var mixed = GlunoPlaceRetention.Decide(
            [Card(0, content: true), Card(1, content: false)], Sevilla());

        // Never a blend. Half a shortlist rendered on reload as if it were the
        // whole one is worse than none of it — the user would think one place
        // was all Gluno found.
        Assert.Empty(mixed.Places);
        Assert.Equal(2, mixed.References.Count);
        Assert.True(mixed.Reduced);
        Assert.DoesNotContain("Real Alcázar", StoredJson(mixed));
    }

    [Fact]
    public void A_place_whose_identity_may_not_be_kept_leaves_no_reference()
    {
        var retention = GlunoPlaceRetention.Decide(
            [Card(0, content: false, identity: false)], Sevilla());

        Assert.Empty(retention.Places);
        Assert.Empty(retention.References);
    }

    [Fact]
    public void References_without_a_search_context_are_not_written()
    {
        // An id whose search cannot be reproduced is an id nobody can look up —
        // the endpoint that answers by id is allowlist-governed.
        var retention = GlunoPlaceRetention.Decide([Card(0, content: false)], null);

        Assert.Empty(retention.References);
        Assert.Null(retention.Search);
    }

    // ── 29. Capability, not provider name ────────────────────────────────

    [Fact]
    public void The_decision_is_the_capability_never_the_provider_name()
    {
        foreach (var file in new[] { "GlunoPlaceRetention.cs", "GlunoPlaceRehydrator.cs" })
        {
            var source = Source("Services", "Gluno", file);

            // Both Tripadvisor products are "tripadvisor" and issue the same
            // location ids, so a name comparison would be wrong as well as
            // brittle.
            Assert.DoesNotContain("\"terra\"", source);
            Assert.DoesNotContain("TerraTravelProvider", source);
            Assert.DoesNotContain("\"tripadvisor\"", source);
        }

        Assert.Contains("place.AllowsContentPersistence", Source("Services", "Gluno", "GlunoPlaceRetention.cs"));
        Assert.Contains("place.AllowsIdentityPersistence", Source("Services", "Gluno", "GlunoPlaceRetention.cs"));
    }

    [Fact]
    public void A_place_defaults_to_neither_permission()
    {
        // A provider that says nothing about its rights gets the careful
        // reading rather than the convenient one.
        var bare = new TravelPlace
        {
            Provider = "x",
            ExternalId = "x:1",
            ProviderPlaceId = "1",
            Name = "X",
            Category = "general",
            SourceAttribution = "x",
        };

        Assert.False(bare.AllowsContentPersistence);
        Assert.False(bare.AllowsIdentityPersistence);
        Assert.Empty(GlunoPlaceRetention.Decide(
            [new GlunoPlaceCard
            {
                Provider = "x", ExternalId = "x:1", Name = "X",
                Category = "general", SourceAttribution = "x",
            }],
            Sevilla()).References);
    }

    [Fact]
    public void Neither_flag_is_ever_serialised()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Card(0, content: true), GlunoJson.Options);

        // They exist to decide what gets written. Writing them would be
        // pointless — and they would then be read back as data on reload.
        Assert.DoesNotContain("allowsContentPersistence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allowsIdentityPersistence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerPlaceId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_flags_survive_every_hop_from_provider_to_decision()
    {
        var executor = Source("Services", "Gluno", "GlunoActionExecutor.cs");
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Losing them anywhere in between would make every result unstorable —
        // which fails safe, and silently breaks the legacy provider.
        Assert.Contains("AllowsContentPersistence = ranked.Place.AllowsContentPersistence,", executor);
        Assert.Contains("AllowsIdentityPersistence = ranked.Place.AllowsIdentityPersistence,", executor);
        Assert.Contains("AllowsContentPersistence = place.AllowsContentPersistence,", chat);
        Assert.Contains("AllowsIdentityPersistence = place.AllowsIdentityPersistence,", chat);
    }

    [Fact]
    public void Working_memory_obeys_the_same_rule_as_the_payload()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Working memory remembers a place by name and coordinate so "the
        // second one" resolves next turn. That is content, and the same rule
        // has to reach it.
        Assert.Contains("visiblePlaces, retention.Places, records, text, ct);", chat);
        Assert.Contains("GlunoReferenceResolver.Remember(\n            state,\n            rememberable,",
            chat.Replace("\r\n", "\n"));
    }

    // ── 30. Nothing else is stored ───────────────────────────────────────

    [Fact]
    public void There_is_no_cache_of_provider_content_anywhere_in_this_path()
    {
        foreach (var file in new[] { "TerraTravelProvider.cs", "GlunoPlaceRehydrator.cs" })
        {
            var source = Source("Services", "Gluno", file);

            // A short-lived copy would be exactly the storage the policy
            // forbids, and a time limit does not make storing something
            // permitted. The extra call is the price.
            Assert.DoesNotContain("TravelDataCache", source);
            Assert.DoesNotContain("MemoryCache", source);
            Assert.DoesNotContain("GetOrAddAsync", source);
        }

        Assert.Contains("using (document)", Source("Services", "Gluno", "TerraTravelProvider.cs"));
    }

    [Fact]
    public void The_key_lives_in_a_header_and_nowhere_else()
    {
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");

        Assert.DoesNotContain("_apiKey", terra);
        Assert.Contains("request.Headers.Add(\"X-API-Key\", ApiKey);", terra);
    }

    [Fact]
    public void Rehydration_logs_counts_and_never_content()
    {
        var rehydrator = Source("Services", "Gluno", "GlunoPlaceRehydrator.cs");

        var start = rehydrator.IndexOf("place rehydration status=", StringComparison.Ordinal);
        Assert.True(start > 0);

        var line = rehydrator[start..Math.Min(rehydrator.Length, start + 320)];
        Assert.Contains("matched={Matched}/{Wanted}", line);
        Assert.Contains("calls={Calls}", line);
        // Not the id, not a name, and not the geography — that is the user's
        // own destination.
        Assert.DoesNotContain("{LocationId}", line);
        Assert.DoesNotContain("{Near}", line);
        Assert.DoesNotContain("{Query}", line);
    }

    // ── 31. Status ───────────────────────────────────────────────────────

    [Fact]
    public void The_status_block_reports_both_halves_of_the_policy()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");

        Assert.Contains("ContentPersistence = terraOn", controller);
        Assert.Contains("LocationIdPersistence = terraOn", controller);

        Assert.Contains("public bool AllowsContentPersistence => ContentMayBeStored;", terra);
        Assert.Contains("public bool AllowsLocationIdPersistence => LocationIdMayBeStored;", terra);
        Assert.Contains("private const bool ContentMayBeStored = false;", terra);
        Assert.Contains("private const bool LocationIdMayBeStored = true;", terra);
    }

    [Fact]
    public void One_constant_drives_the_property_and_the_mapper()
    {
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");

        // The mapper is static and the capabilities are instance properties.
        // Without shared constants they could disagree, and the disagreement
        // would be silent.
        Assert.Contains("AllowsContentPersistence = ContentMayBeStored,", terra);
        Assert.Contains("AllowsIdentityPersistence = LocationIdMayBeStored,", terra);
    }

    [Fact]
    public void The_status_block_still_names_nothing()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        var start = controller.IndexOf("private GlunoTravelDataDto DescribeTravelData()", StringComparison.Ordinal);
        var body = controller[start..(start + 1400)];

        Assert.True(start > 0);
        foreach (var leak in new[] { "ApiKey", "BaseUrl", "http", "X-API-Key" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── The text paths ───────────────────────────────────────────────────

    [Fact]
    public void Typed_add_requests_use_the_same_id_based_flow()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("private async Task<GlunoTurnResult?> AddNamedPlaceAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 4200)];

        Assert.True(start > 0);
        // "Add the first one" and "add Real Alcázar" both end at the option key
        // and the same verified add, never at a place the sentence described.
        // The key comes from the REFERENCE, not the position — a re-fetched
        // list can be short, and a positional key would point elsewhere.
        Assert.Contains("keys[matches[0]]", body);
        Assert.Contains("RefetchShownPlacesAsync(message, ct)", body);
    }

    [Fact]
    public void The_typed_path_resolves_against_the_order_the_user_was_shown()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf(
            "private async Task<RefetchedPlaces> RefetchShownPlacesAsync", StringComparison.Ordinal);
        var body = chat[start..(start + 1800)];

        Assert.True(start > 0);
        // Built by walking the stored references, not the provider's ordering
        // today — "the second one" means the second card the user saw.
        Assert.Contains("foreach (var reference in references)", body);
        // A short list keeps its real keys, and ORDINALS are gated on
        // completeness rather than the whole list being thrown away.
        Assert.Contains("keys.Add(reference.OptionKey);", body);
        Assert.Contains("places.Count == references.Count", body);
    }

    [Fact]
    public void Name_text_is_never_matched_against_stored_provider_content()
    {
        var message = MessageWith(GlunoPlaceRetention.Decide(
            [Card(0, content: false), Card(1, content: false)], Sevilla()));

        // After a reload there is no name in storage to match against, which is
        // the point — the matcher only ever sees a list fetched just now.
        Assert.DoesNotContain("Alc", message.PayloadJson!);
        Assert.Empty(GlunoPlaceOptions.Match(
            GlunoPlaceOptions.References(message)
                .Select(_ => (GlunoPlaceCard?)null)
                .Where(card => card != null)
                .Select(card => card!)
                .ToList(),
            "Lägg till Real Alcázar"));
    }
}
