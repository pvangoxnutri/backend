using System.Net;
using System.Text.Json;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the two production failures proven by the 2026-08-03 debug
/// exports: "Ge mig tips på saker att göra i Linz" went to the model because
/// the discovery resolver only knew a handful of exact phrases, and "Platser i
/// Sevilla" ran the direct path but showed "inga nya förslag" because zero
/// results — whatever their cause — were reported as a valid empty answer.
///
/// TWO KINDS OF PROOF HERE. The response contract is pinned by a FIXTURE built
/// from Terra's documented schema (docs.terra.tripadvisor.com, recommendations
/// search reference) and run through the REAL parser — a mock returning
/// ready-made TravelPlace objects proves nothing about the two bugs that
/// lived in the parsing. The language resolver is exercised with the REAL
/// production messages and their variants.
///
/// The fixture is sanitised by construction: no keys, no headers, no personal
/// data, no signed URLs — documented property names with invented values.
/// </summary>
public class TerraFixtureAndDiscoveryEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string ChatService() => Source("Services", "Gluno", "GlunoChatService.cs");
    private static string Provider() => Source("Services", "Gluno", "TerraTravelProvider.cs");

    private static string ServiceMethod(string declaration)
    {
        var source = ChatService();
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {declaration}");

        var end = source.IndexOf("\n    private ", start + declaration.Length, StringComparison.Ordinal);
        var endPublic = source.IndexOf("\n    public ", start + declaration.Length, StringComparison.Ordinal);
        if (endPublic >= 0 && (end < 0 || endPublic < end)) end = endPublic;

        return end > start ? source[start..end] : source[start..];
    }

    // ── The documented response, as a sanitised fixture ──────────────────
    //
    // Property names transcribed from the recommendations-search reference;
    // values invented. One location, one experience (which must be discarded
    // as not-a-place, not as an error).
    private const string DocumentedResponse = """
    {
      "search_results": [
        {
          "type": "location",
          "location": {
            "id": 195232,
            "names": [
              { "language": "en", "value": "Real Alcazar", "primary": true },
              { "language": "sv", "value": "Kungliga Alcazar" }
            ],
            "addresses": [
              {
                "street_address": "Patio de Banderas",
                "city": "Seville",
                "country_code": "ES",
                "formatted": "Patio de Banderas, 41004 Seville Spain"
              }
            ],
            "coordinates": { "latitude": 37.3826, "longitude": -5.9904 },
            "categories": [
              {
                "id": 47,
                "display_name": "Points of Interest & Landmarks",
                "top_level_category": "Attraction"
              }
            ],
            "traveler_ratings": { "overall": { "rating": 4.6, "count": 41210 } },
            "opening_hours": { "formatted": ["Mon-Sun 09:30-19:00"] },
            "urls": {
              "tripadvisor": "https://www.tripadvisor.com/Attraction_Review-g187443",
              "official": "https://www.example.org"
            },
            "status": { "value": "OPEN" }
          },
          "review_sources": [
            { "id": 1, "snippet": "A stunning palace with beautiful gardens." }
          ]
        },
        {
          "type": "experience",
          "experience": { "id": 999 }
        }
      ]
    }
    """;

    // ── 13, 21, 22: the documented fixture maps ──────────────────────────

    [Fact]
    public void The_documented_fixture_parses_to_one_place_and_one_counted_discard()
    {
        using var document = JsonDocument.Parse(DocumentedResponse);

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "sv");

        Assert.True(parsed.EnvelopeFound);
        Assert.Equal(2, parsed.RawCount);
        Assert.Equal(1, parsed.MappedCount);
        Assert.Equal(1, parsed.DiscardedCount);
        // The experience is not a mapping failure — it is a documented result
        // kind that is not a place on a map.
        Assert.Equal(1, parsed.Discards[TerraDiscard.MissingLocation]);
    }

    [Fact]
    public void The_location_id_and_the_name_survive_mapping()
    {
        using var document = JsonDocument.Parse(DocumentedResponse);

        var place = TerraTravelProvider
            .ParseSearchResponse(document.RootElement, "sv").Places.Single();

        Assert.Equal("195232", place.ProviderPlaceId);
        Assert.Equal("tripadvisor:195232", place.ExternalId);
        // The requested language wins among the documented names entries.
        Assert.Equal("Kungliga Alcazar", place.Name);
    }

    [Fact]
    public void The_documented_field_shapes_map_ratings_address_category_and_url()
    {
        using var document = JsonDocument.Parse(DocumentedResponse);

        var place = TerraTravelProvider
            .ParseSearchResponse(document.RootElement, "en").Places.Single();

        // overall is an OBJECT — { rating, count } — per the reference. The
        // first reading treated it as a number and every rating came back
        // null.
        Assert.Equal(4.6, place.Rating);
        Assert.Equal(41210, place.ReviewCount);

        // addresses carry "formatted"; categories carry "top_level_category"
        // and "display_name"; urls carry "tripadvisor". None of those were in
        // the first reading either.
        Assert.Equal("Patio de Banderas, 41004 Seville Spain", place.Address);
        Assert.Equal("attraction", place.Category);
        Assert.Equal("Points of Interest & Landmarks", place.CategoryLabel);
        Assert.StartsWith("https://www.tripadvisor.com/", place.ProviderUrl);

        Assert.Single(place.OpeningHours);
        Assert.NotNull(place.ReviewSummary);
        Assert.Equal(37.3826, place.Latitude);
    }

    // ── 23: Terra content still is not persisted ─────────────────────────

    [Fact]
    public void The_fixture_place_reaches_live_cards_but_only_its_id_is_stored()
    {
        using var document = JsonDocument.Parse(DocumentedResponse);

        var place = TerraTravelProvider
            .ParseSearchResponse(document.RootElement, "en").Places.Single();

        Assert.False(place.AllowsContentPersistence);
        Assert.True(place.AllowsIdentityPersistence);

        // Structured live card — name and all — for THIS turn.
        var card = GlunoPlaceCards.From(place);
        Assert.Equal("Real Alcazar", card.Name);

        // And the stored form is the identity, nothing else.
        var retention = GlunoPlaceRetention.Decide([card], new GlunoPlaceSearchContext
        {
            Near = "Seville",
            Category = "attraction",
            Language = "en",
            Limit = 5,
        });

        Assert.Empty(retention.Places);
        var reference = Assert.Single(retention.References);
        Assert.Equal("195232", reference.LocationId);
        Assert.True(retention.Reduced);
    }

    // ── 14–15, 20: empty, contract change, and the counts between ────────

    [Fact]
    public void A_valid_200_with_zero_results_is_a_genuine_empty()
    {
        using var document = JsonDocument.Parse("""{ "search_results": [] }""");

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "en");

        Assert.True(parsed.EnvelopeFound);
        Assert.Equal(0, parsed.RawCount);

        // And the provider reports it as Ok — the one case allowed to say
        // "nothing there".
        var provider = Provider();
        Assert.Contains("parsed.RawCount == 0 ? TerraFailure.EmptyResult : TerraFailure.None", provider);
        Assert.Contains("return new TravelSearchResult { Places = parsed.Places, Status = TravelSearchStatus.Ok };", provider);
    }

    [Fact]
    public void A_wrong_envelope_is_a_contract_failure_not_an_empty_city()
    {
        // The usual envelope for this kind of API — and not Terra's.
        using var document = JsonDocument.Parse("""{ "data": [ { "id": 1 } ] }""");

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "en");

        Assert.False(parsed.EnvelopeFound);
        // The property names are structure, not content — they are what the
        // shape log carries so a contract change is a reading, not a hunt.
        Assert.Contains("data", parsed.TopLevelProperties);

        var provider = Provider();
        Assert.Contains("if (!parsed.EnvelopeFound)", provider);
        Assert.Contains("Log(TerraFailure.ProviderContractChanged", provider);
    }

    [Fact]
    public void Results_that_all_fail_mapping_are_a_failure_not_an_empty_answer()
    {
        // THE PRODUCTION SEMANTICS BUG. raw > 0 with mapped == 0 used to
        // return Ok, so a contract mismatch wore an empty city's clothes and
        // the user was told "inga nya förslag" about Sevilla.
        var provider = Provider();

        Assert.Contains("if (parsed.RawCount > 0 && parsed.MappedCount == 0)", provider);

        var start = provider.IndexOf(
            "if (parsed.RawCount > 0 && parsed.MappedCount == 0)", StringComparison.Ordinal);
        var block = provider[start..(start + 900)];

        Assert.Contains("TerraFailure.MappingFailed", block);
        Assert.Contains("return Empty(TravelSearchStatus.Failed);", block);
    }

    [Fact]
    public void Raw_mapped_and_discarded_are_separate_numbers()
    {
        using var document = JsonDocument.Parse(DocumentedResponse);

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "en");

        Assert.NotEqual(parsed.RawCount, parsed.MappedCount);
        Assert.Equal(parsed.RawCount, parsed.MappedCount + parsed.DiscardedCount);

        // And the shape log names all of them, plus the reasons.
        var provider = Provider();
        Assert.Contains("searchResultCount={Raw} mappedCount={Mapped} discardedCount={Discarded}", provider);
        Assert.Contains("discardReasons={Reasons}", provider);
        Assert.Contains("topLevel={TopLevel}", provider);
        // Transport structure: status, media type, length — never a body.
        Assert.Contains("terra transport status={Status} contentType={ContentType} bodyLength={BodyLength}", provider);
    }

    // ── 16–19: the failure taxonomy ──────────────────────────────────────

    [Fact]
    public void Each_http_failure_keeps_its_own_meaning()
    {
        Assert.Equal(TerraFailure.Unauthorized, TerraTravelProvider.Classify(HttpStatusCode.Unauthorized));
        Assert.Equal(TerraFailure.Forbidden, TerraTravelProvider.Classify(HttpStatusCode.Forbidden));
        Assert.Equal(TerraFailure.RateLimited, TerraTravelProvider.Classify(HttpStatusCode.TooManyRequests));
        Assert.Equal(TerraFailure.InvalidRequest, TerraTravelProvider.Classify(HttpStatusCode.BadRequest));
        // Documented for recommendations/search: geo not found.
        Assert.Equal(TerraFailure.InvalidRequest, TerraTravelProvider.Classify(HttpStatusCode.NotFound));
        Assert.Equal(TerraFailure.Network, TerraTravelProvider.Classify(HttpStatusCode.InternalServerError));
        Assert.Equal(TerraFailure.Network, TerraTravelProvider.Classify(HttpStatusCode.BadGateway));

        var provider = Provider();

        // 429/quota → RateLimited; everything else that failed → Failed. A
        // deserialisation error goes the same road: never an empty answer.
        Assert.Contains("failure is TerraFailure.RateLimited or TerraFailure.QuotaExceeded", provider);
        Assert.Contains("TerraFailure.DeserializationFailed", provider);
        // 401/403/quota/contract are flagged for a person, not a retry loop.
        Assert.Contains("terra needs attention", provider);
    }

    [Fact]
    public void The_chat_layer_shows_a_retryable_provider_error_for_every_non_ok()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchAsync(");

        // The strict gate: only a genuine Ok may answer with an empty state.
        Assert.Contains("if (result.Status != TravelSearchStatus.Ok)", method);
        Assert.Contains("Error = GlunoTurnError.ProviderFailed", method);
        Assert.Contains("FailureCode = GlunoFailureCodes.TripadvisorUnavailable", method);
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.TripadvisorUnavailable));

        // And the thread survives the failure, so the retry knows where.
        Assert.Contains("workingState.Discovery = GlunoDiscoveryContexts.WithLifetime", method);

        // The app's copy for that code names what could not be fetched.
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("tripadvisor_unavailable: 'gluno.error.placesUnavailable'", row);

        var translations = Mobile("components", "i18n-provider.tsx");
        Assert.Contains(
            "'gluno.error.placesUnavailable': 'Jag kunde inte hämta verifierade platsförslag just nu.',",
            translations);
    }

    // ── 1–6: the language resolver ───────────────────────────────────────

    [Theory]
    // The production message, verbatim, and the stated variants.
    [InlineData("Ge mig tips på saker att göra i Linz", "Linz")]
    [InlineData("Vad kan man göra i Linz?", "Linz")]
    [InlineData("Vad finns det att se i Linz?", "Linz")]
    [InlineData("Tips i Linz", "Linz")]
    [InlineData("Saker att göra i Sevilla", "Sevilla")]
    [InlineData("Vad rekommenderar du i Ronda?", "Ronda")]
    [InlineData("Sevärdheter i Tanger", "Tanger")]
    [InlineData("Något kul att göra i Málaga", "Málaga")]
    [InlineData("Vad borde vi hitta på i Sevilla?", "Sevilla")]
    [InlineData("Ge mig fem tips i Linz", "Linz")]
    [InlineData("Vilka platser är värda att besöka i Linz?", "Linz")]
    [InlineData("Ge mig förslag på aktiviteter i Sevilla", "Sevilla")]
    public void Natural_Swedish_discovery_questions_go_direct(string message, string destination)
    {
        var query = GlunoDirectPlaceSearch.Parse(message);

        Assert.NotNull(query);
        Assert.Equal(destination, query!.Destination);
    }

    [Theory]
    [InlineData("Things to do in Linz", "Linz")]
    [InlineData("What should we see in Seville?", "Seville")]
    [InlineData("Give me five places in Ronda", "Ronda")]
    [InlineData("What do you recommend in Tangier?", "Tangier")]
    [InlineData("Places worth visiting in Linz", "Linz")]
    public void English_discovery_questions_go_direct(string message, string destination)
    {
        var query = GlunoDirectPlaceSearch.Parse(message);

        Assert.NotNull(query);
        Assert.Equal(destination, query!.Destination);
    }

    [Fact]
    public void Reasonable_typos_and_casual_spelling_still_read_as_discovery()
    {
        // "föfslag" — one edit from "förslag".
        Assert.NotNull(GlunoDirectPlaceSearch.Parse("Ge mig föfslag"));
        // "aktivitetr" — one edit from "aktiviteter".
        Assert.NotNull(GlunoDirectPlaceSearch.Parse("aktivitetr i Sevilla"));
        // "va" for "vad", lowercase destination.
        var casual = GlunoDirectPlaceSearch.Parse("va kan man göra i linz");
        Assert.NotNull(casual);
        Assert.Equal("linz", casual!.Destination);
        // A doubled letter on a bare noun.
        Assert.NotNull(GlunoDirectPlaceSearch.Parse("sevärdheterr"));
    }

    [Fact]
    public void A_requested_count_is_read_and_bounded()
    {
        Assert.Equal(5, GlunoDirectPlaceSearch.Parse("Ge mig fem tips i Linz")!.RequestedCount);
        Assert.Equal(5, GlunoDirectPlaceSearch.Parse("Give me five places in Ronda")!.RequestedCount);
        Assert.Equal(3, GlunoDirectPlaceSearch.Parse("Ge mig 3 platser i Sevilla")!.RequestedCount);
    }

    [Fact]
    public void Statements_and_time_words_do_not_become_searches
        ()
    {
        // A sentence about yesterday carries a noun and is not a request.
        Assert.Null(GlunoDirectPlaceSearch.Parse("Vi såg ett museum igår"));
        // A month is not a geography.
        var seasonal = GlunoDirectPlaceSearch.Parse("Vad kan man göra i juli?");
        Assert.True(seasonal == null || seasonal.Destination == null);
        var summer = GlunoDirectPlaceSearch.Parse("Vad kan man göra i sommar?");
        Assert.True(summer == null || summer.Destination == null);
    }

    // ── 12: complex questions keep their model turn ──────────────────────

    [Theory]
    [InlineData("Vilket av dessa passar bäst med barn och regn?")]
    [InlineData("Jämför Alcázar och Casa de Pilatos")]
    [InlineData("Planera en lugn dag med lunch och pauser")]
    [InlineData("Vad passar bäst utifrån allt du vet om oss?")]
    public void Comparison_planning_and_judgement_stay_with_the_model(string message)
    {
        Assert.Null(GlunoDirectPlaceSearch.Parse(message));
    }

    // ── 7–9: follow-ups use the discovery thread ─────────────────────────

    [Fact]
    public void Ge_mig_5_platser_is_a_follow_up_with_a_count()
    {
        // The production message, verbatim. Inside an active thread it means
        // "5 from the SAME destination".
        var followUp = GlunoDiscoveryFollowUps.Parse("Ge mig 5 platser");

        Assert.NotNull(followUp);
        Assert.Equal(5, followUp!.RequestedCount);
    }

    [Fact]
    public void Fler_and_its_variants_ask_for_more()
    {
        foreach (var message in new[] { "Fler", "Har du andra?", "Något lugnare?", "Mer", "More" })
        {
            var followUp = GlunoDiscoveryFollowUps.Parse(message);
            Assert.True(followUp is { More: true }, $"should be a more-follow-up: {message}");
        }
    }

    [Fact]
    public void Andra_sevardheter_switches_to_attractions_in_the_same_thread()
    {
        var followUp = GlunoDiscoveryFollowUps.Parse("Andra sevärdheter");

        Assert.NotNull(followUp);
        Assert.Equal(TravelPlaceCategory.Attraction, followUp!.SwitchCategory);
    }

    [Fact]
    public void Visa_restauranger_istallet_switches_category_and_keeps_the_destination()
    {
        var followUp = GlunoDiscoveryFollowUps.Parse("Visa restauranger istället");

        Assert.NotNull(followUp);
        Assert.Equal(TravelPlaceCategory.Restaurant, followUp!.SwitchCategory);

        // The handler reuses the thread's own destination and never re-derives
        // one from the sentence.
        var handler = ServiceMethod("private async Task<GlunoTurnResult> DirectPlaceFollowUpAsync(");
        Assert.Contains("discovery.Destination", handler);
        // A switched category starts fresh; "fler" excludes what was shown.
        Assert.Contains("discovery.ShownLocationIds", handler);
    }

    [Fact]
    public void A_message_naming_a_new_destination_is_not_a_follow_up()
    {
        // "i Ronda" changes the subject — that is a fresh search.
        Assert.Null(GlunoDiscoveryFollowUps.Parse("platser i Ronda"));
        // And a plain yes has nothing to do with the thread.
        Assert.Null(GlunoDiscoveryFollowUps.Parse("Ja det blir bra"));
    }

    [Fact]
    public void The_follow_up_runs_before_ordinary_routing_and_excludes_shown_ids()
    {
        var source = ChatService();

        var followUpBlock = source.IndexOf("GlunoDiscoveryFollowUps.Parse(text)", StringComparison.Ordinal);
        var parseBlock = source.IndexOf("GlunoDirectPlaceSearch.Parse(text)", StringComparison.Ordinal);
        var detector = source.IndexOf("GlunoClarificationDetector.Detect(new GlunoDetectionInput", StringComparison.Ordinal);
        var model = source.IndexOf("_ai.RunTurnAsync(", StringComparison.Ordinal);

        Assert.True(followUpBlock > 0);
        Assert.True(followUpBlock < parseBlock, "the thread wins over a fresh parse");
        Assert.True(followUpBlock < detector, "the thread wins over the detector");
        Assert.True(followUpBlock < model, "the thread wins over the model");

        // The provider is ASKED to exclude, and the results are re-filtered
        // locally either way.
        var provider = Provider();
        Assert.Contains("exclude_location_ids", provider);

        var run = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchAsync(");
        Assert.Contains("!excludeLocationIds.Contains(place.ProviderPlaceId", run);
    }

    [Fact]
    public void A_retry_after_a_provider_failure_reuses_the_discovery_context()
    {
        var run = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchAsync(");

        // The failure branch SAVES the thread — destination, category and
        // count survive the error — so the retry (a re-send, or a follow-up
        // like "ge mig 5") never has to re-resolve where to look.
        var failureBranch = run[run.IndexOf("if (result.Status != TravelSearchStatus.Ok)", StringComparison.Ordinal)..];
        var savedInFailure = failureBranch.IndexOf(
            "workingState.Discovery = GlunoDiscoveryContexts.WithLifetime", StringComparison.Ordinal);
        var failureReturn = failureBranch.IndexOf("Error = GlunoTurnError.ProviderFailed", StringComparison.Ordinal);

        Assert.True(savedInFailure >= 0 && savedInFailure < failureReturn,
            "the discovery context must be saved BEFORE the failure returns");

        // And the follow-up handler reads that stored destination verbatim.
        var followUp = ServiceMethod("private async Task<GlunoTurnResult> DirectPlaceFollowUpAsync(");
        Assert.Contains("discovery.Destination", followUp);
    }

    [Fact]
    public void The_thread_remembers_only_what_terra_permits_keeping()
    {
        var context = Source("Services", "Gluno", "GlunoDiscoveryContext.cs");

        // Location ids and SideQuest's own facts. No names, no ratings, no
        // content of any kind.
        Assert.Contains("ShownLocationIds", context);
        foreach (var forbidden in new[] { "Name", "Rating", "Address", "Snippet", "Review" })
        {
            Assert.DoesNotContain($"public string {forbidden}", context);
        }

        // Bounded and expiring.
        Assert.Contains("MaxShownIds", context);
        Assert.Contains("ExpiresAtUtc", context);
    }

    // ── 10–11: zero model calls, one provider call ───────────────────────

    [Fact]
    public void Discovery_makes_exactly_one_provider_call_and_zero_model_calls()
    {
        var run = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchAsync(");

        Assert.Equal(1, run.Split("SearchAllAsync").Length - 1);
        Assert.DoesNotContain("_ai.", run);
        Assert.Contains("telemetry.ModelSkipped = true;", run);
    }

    // ── 24: the model cannot fabricate a discovery list ──────────────────

    [Fact]
    public void A_matched_discovery_question_always_ends_structured()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult?> DirectPlaceSearchAsync(");

        // Every branch returns: places, a destination question, an Adventure
        // question, a route-stop question, a provider error, or a verified
        // empty. No branch falls through to the model.
        Assert.DoesNotContain("return null", method);
        Assert.Contains("AskDestinationAsync(", method);
        Assert.Contains("AskWhichAdventureAsync(", method);
        Assert.Contains("AskRouteStopAsync(", method);
    }

    [Fact]
    public void The_destination_question_remembers_what_was_asked_for()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult> AskDestinationAsync(");

        Assert.Contains("AwaitingDestination = true", method);
        Assert.Contains("GlunoResponseOrigins.DestinationClarification", method);

        // A short place name completes the search; a "ja" or a "tack" does
        // not get sent to a geo lookup.
        Assert.Equal("Linz", GlunoDiscoveryFollowUps.ParseDestinationAnswer("Linz"));
        Assert.Equal("New York", GlunoDiscoveryFollowUps.ParseDestinationAnswer("New York"));
        Assert.Null(GlunoDiscoveryFollowUps.ParseDestinationAnswer("ja"));
        Assert.Null(GlunoDiscoveryFollowUps.ParseDestinationAnswer("vet inte"));
        Assert.Null(GlunoDiscoveryFollowUps.ParseDestinationAnswer(
            "en lugn stad med bra mat och lite folk"));
    }

    // ── 25: responseOrigin ───────────────────────────────────────────────

    [Fact]
    public void The_new_origins_are_fixed_vocabulary_and_set_where_they_belong()
    {
        Assert.Equal("direct_place_followup", GlunoResponseOrigins.DirectPlaceFollowup);
        Assert.Equal("destination_clarification", GlunoResponseOrigins.DestinationClarification);
        Assert.Contains(GlunoResponseOrigins.DirectPlaceFollowup, GlunoResponseOrigins.All);
        Assert.Contains(GlunoResponseOrigins.DestinationClarification, GlunoResponseOrigins.All);

        var source = ChatService();
        Assert.Contains("origin: GlunoResponseOrigins.DirectPlaceFollowup", source);
        Assert.Contains("origin: GlunoResponseOrigins.DirectPlaceSearch", source);
    }
}
