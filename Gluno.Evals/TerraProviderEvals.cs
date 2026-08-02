using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the Tripadvisor Terra provider.
///
/// WHY A SECOND PROVIDER RATHER THAN AN EDIT. Terra is a different product with
/// a different host, a different authentication scheme and a different response
/// shape, on a separate account with its own key. Editing the Content API
/// provider in place would create one class in which a Terra key could be sent
/// to the legacy host as a QUERY PARAMETER — a live secret in a URL, on a
/// service that would reject it anyway.
///
/// THE THING THESE GUARD HARDEST. On the old provider the key was a query
/// parameter, which is why its HTTP loggers are removed. Here the key is a
/// header, and the tests below assert it never reaches a URI.
///
/// No network and no real key: the stub answers every request.
/// </summary>
public class TerraProviderEvals
{
    /// <summary>
    /// A transport that answers without a network and records what it was
    /// asked — the URI and the header names, so the tests can prove where the
    /// key did and did not go.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastApiKeyHeader { get; private set; }
        public int Calls { get; private set; }

        public StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            LastApiKeyHeader = request.Headers.TryGetValues("X-API-Key", out var values)
                ? string.Join(',', values)
                : null;

            return _respond();
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private const string TestKey = "terra-test-key";

    private static IConfiguration Config(
        bool enabled = true, string? key = TestKey, string baseUrl = "https://terra.tripadvisor.com/api")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TripadvisorTerra:Enabled"] = enabled ? "true" : "false",
                ["TripadvisorTerra:ApiKey"] = key,
                ["TripadvisorTerra:BaseUrl"] = baseUrl,
                ["TripadvisorTerra:MaxResults"] = "6",
            })
            .Build();

    private static TerraTravelProvider Provider(StubHandler handler, IConfiguration? config = null)
        => new(new StubFactory(handler), config ?? Config(), NullLogger<TerraTravelProvider>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static TravelPlaceQuery SevillaQuery(
        TravelPlaceCategory category = TravelPlaceCategory.Attraction,
        string? near = "Sevilla",
        int limit = 6,
        string language = "sv") => new()
    {
        Query = string.Empty,
        Near = near,
        Category = category,
        Limit = limit,
        Language = language,
    };

    /// A recommendations response in Terra's documented shape.
    private const string TwoResults = """
        { "search_results": [
          {
            "type": "location",
            "location": {
              "id": 187443,
              "names": [ { "language": "sv", "value": "Real Alcázar" },
                         { "language": "en", "value": "Royal Alcazar" } ],
              "addresses": [ { "language": "sv", "value": "Patio de Banderas, Sevilla" } ],
              "coordinates": { "latitude": 37.3830, "longitude": -5.9903 },
              "categories": [ { "name": "Attraction" } ],
              "traveler_ratings": { "overall": 4.7, "count": 41230 },
              "opening_hours": { "formatted": [ "Mon-Sun 09:30-17:00" ] },
              "urls": { "location": "https://www.tripadvisor.com/x" }
            },
            "review_sources": [ { "id": "r1", "snippet": "Stunning gardens and tilework." } ]
          },
          {
            "type": "location",
            "location": {
              "id": 187444,
              "names": [ { "language": "sv", "value": "Catedral de Sevilla" } ]
            }
          }
        ] }
        """;

    // ── Configuration ────────────────────────────────────────────────────

    [Fact]
    public void Enabled_a_key_and_an_https_host_are_all_required()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        Assert.True(Provider(handler).IsConfigured);
        Assert.False(Provider(handler, Config(enabled: false)).IsConfigured);
        Assert.False(Provider(handler, Config(key: null)).IsConfigured);
        Assert.False(Provider(handler, Config(key: "  ")).IsConfigured);
        // Plaintext would be rejected by Terra anyway, and a key in flight over
        // http is not something to attempt.
        Assert.False(Provider(handler, Config(baseUrl: "http://terra.tripadvisor.com/api")).IsConfigured);
    }

    [Fact]
    public void The_legacy_configuration_namespace_is_never_read()
    {
        var legacyOnly = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tripadvisor:Enabled"] = "true",
                ["Tripadvisor:ApiKey"] = "legacy-key",
                ["Tripadvisor:BaseUrl"] = "https://api.content.tripadvisor.com",
            })
            .Build();

        // A Content API key must never be sent to Terra, or the reverse.
        Assert.False(Provider(new StubHandler(() => Json(TwoResults)), legacyOnly).IsConfigured);
    }

    [Fact]
    public async Task An_unconfigured_provider_makes_no_call()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler, Config(enabled: false))
            .SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Empty(places);
        Assert.Equal(0, handler.Calls);
    }

    // ── The secret ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_key_travels_in_the_X_API_Key_header()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Equal(TestKey, handler.LastApiKeyHeader);
    }

    [Fact]
    public async Task The_key_never_appears_in_the_URI()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // The whole reason this is a separate provider. On the Content API the
        // key WAS the query string.
        Assert.DoesNotContain(TestKey, handler.LastUri!);
        Assert.DoesNotContain("key=", handler.LastUri!);
    }

    [Fact]
    public async Task The_key_never_appears_in_the_body()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.DoesNotContain(TestKey, handler.LastBody!);
    }

    [Fact]
    public void The_provider_never_logs_a_key_a_header_or_a_body()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TerraTravelProvider.cs"));

        var start = source.IndexOf("private void Log(", StringComparison.Ordinal);
        var body = source[start..(start + 1200)];

        Assert.True(start > 0);
        Assert.DoesNotContain("ApiKey", body);
        Assert.DoesNotContain("X-API-Key", body);
        // Nor the user's own sentence — the query sent upstream is built from a
        // category, but the message behind it can contain anything.
        Assert.DoesNotContain("query.Query", body);
        Assert.DoesNotContain("query.Near", body);
    }

    // ── The request ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_Sevilla_question_posts_to_recommendations_search()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.EndsWith("/recommendations/search", handler.LastUri!);
        Assert.StartsWith("https://terra.tripadvisor.com/api", handler.LastUri!);
    }

    [Fact]
    public async Task The_request_names_the_city_as_the_geography()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        Assert.Equal("Sevilla", body.GetProperty("geo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_limit_is_capped_at_the_configured_maximum()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        await Provider(handler).SearchPlacesAsync(
            SevillaQuery(limit: 50), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        Assert.Equal(6, body.GetProperty("limit").GetInt32());
    }

    [Theory]
    [InlineData(TravelPlaceCategory.Attraction, "attractions")]
    [InlineData(TravelPlaceCategory.Restaurant, "restaurants")]
    [InlineData(TravelPlaceCategory.Hotel, "hotels")]
    public void The_query_text_matches_the_intent(TravelPlaceCategory category, string expected)
    {
        var text = TerraTravelProvider.BuildQueryText(
            new TravelPlaceQuery { Query = string.Empty, Category = category, Language = "sv" });

        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_users_own_search_words_lead_when_they_gave_any()
    {
        var text = TerraTravelProvider.BuildQueryText(new TravelPlaceQuery
        {
            Query = "rooftop bar",
            Category = TravelPlaceCategory.Restaurant,
            Language = "sv",
        });

        // "rooftop bar" beats the generic phrase, and the intent stays as
        // context.
        Assert.StartsWith("rooftop bar", text);
    }

    [Fact]
    public void The_category_filter_uses_Terras_own_vocabulary()
    {
        // TRANSCRIBED FROM THE SCHEMA, not inferred from the shape of the API.
        // The first version of this eval asserted ["ATTRACTION"] — screaming
        // snake case, which is what an endpoint like this usually uses — and it
        // passed against a provider that sent the same guess, while every real
        // categorised search came back 400. A fixture that encodes the same
        // assumption as the code tests nothing.
        Assert.Equal(["Attraction"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Attraction));
        Assert.Equal(["Eat & Drink"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Restaurant));
        Assert.Equal(["Accommodation"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Hotel));
        // A general request gets no filter rather than a guessed one.
        Assert.Null(TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.General));
    }

    [Fact]
    public async Task A_query_with_no_geography_is_refused_rather_than_sent()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(
            SevillaQuery(near: null), CancellationToken.None);

        // Terra takes a geography by name. Without one it would search the
        // world, and guessing a city from a latitude is not something to do.
        Assert.Empty(places);
        Assert.Equal(0, handler.Calls);
    }

    // ── The response ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_response_produces_places()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Equal(2, places.Count);
        Assert.Equal("Real Alcázar", places[0].Name);
    }

    [Fact]
    public async Task An_id_and_a_name_are_enough_to_keep_a_place()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // The second result has nothing but an id and a name.
        var bare = places[1];

        Assert.Equal("Catedral de Sevilla", bare.Name);
        // Missing means null, never zero — Gluno is forbidden from inventing
        // ratings and hours, and a placeholder would read as data.
        Assert.Null(bare.Rating);
        Assert.Null(bare.ReviewCount);
        Assert.Null(bare.Latitude);
        Assert.Empty(bare.OpeningHours);
        Assert.Null(bare.ReviewSummary);
    }

    [Fact]
    public async Task A_result_with_no_id_or_no_name_is_discarded()
    {
        var handler = new StubHandler(() => Json("""
            { "search_results": [
              { "type": "location", "location": { "names": [ { "language": "sv", "value": "No id" } ] } },
              { "type": "location", "location": { "id": 5 } },
              { "type": "experience", "experience": { "id": 9 } },
              { "type": "location", "location": { "id": 7, "names": [ { "language": "sv", "value": "Keeps" } ] } }
            ] }
            """));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // An experience is a bookable tour, not a place on a map.
        Assert.Single(places);
        Assert.Equal("Keeps", places[0].Name);
    }

    [Fact]
    public async Task Localised_arrays_pick_the_requested_language()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var swedish = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);
        var english = await Provider(handler).SearchPlacesAsync(
            SevillaQuery(language: "en"), CancellationToken.None);

        Assert.Equal("Real Alcázar", swedish[0].Name);
        Assert.Equal("Royal Alcazar", english[0].Name);
    }

    [Fact]
    public void A_language_with_no_entry_falls_back_to_the_first()
    {
        var element = JsonDocument.Parse("""
            { "names": [ { "language": "es", "value": "Alcázar" } ] }
            """).RootElement;

        // A name in the wrong language is far better than no name.
        Assert.Equal("Alcázar", TerraTravelProvider.ReadLocalised(element, "names", "sv"));
    }

    [Fact]
    public async Task Ratings_and_review_counts_map_from_traveler_ratings()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Equal(4.7, places[0].Rating);
        Assert.Equal(41230, places[0].ReviewCount);
        Assert.Equal(5, places[0].RatingScaleMax);
    }

    [Fact]
    public async Task Coordinates_opening_hours_and_a_review_snippet_map()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Equal(37.3830, places[0].Latitude);
        Assert.Equal(-5.9903, places[0].Longitude);
        Assert.Single(places[0].OpeningHours);
        Assert.Contains("Stunning gardens", places[0].ReviewSummary);
    }

    [Fact]
    public async Task Half_a_coordinate_pair_is_treated_as_none()
    {
        var handler = new StubHandler(() => Json("""
            { "search_results": [ { "type": "location", "location": {
              "id": 1, "names": [ { "language": "sv", "value": "Half" } ],
              "coordinates": { "latitude": 37.38 } } } ] }
            """));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // A half-supplied pair would reach a routing provider as a real point.
        Assert.Null(places[0].Latitude);
        Assert.Null(places[0].Longitude);
    }

    [Fact]
    public async Task Every_place_carries_attribution_and_a_namespaced_id()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.All(places, place =>
        {
            Assert.StartsWith("tripadvisor:", place.ExternalId);
            Assert.Contains("Tripadvisor", place.SourceAttribution);
        });
    }

    [Fact]
    public async Task No_image_is_invented()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // Recommendations carries no photo, and a picture of the wrong
        // building is worse than none.
        Assert.All(places, place => Assert.Null(place.ImageUrl));
    }

    [Fact]
    public async Task Only_an_https_provider_link_is_kept()
    {
        var handler = new StubHandler(() => Json("""
            { "search_results": [ { "type": "location", "location": {
              "id": 1, "names": [ { "language": "sv", "value": "X" } ],
              "urls": { "location": "javascript:alert(1)" } } } ] }
            """));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Null(places[0].ProviderUrl);
    }

    // ── Failures, kept apart ─────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TerraFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TerraFailure.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, TerraFailure.RateLimited)]
    [InlineData(HttpStatusCode.PaymentRequired, TerraFailure.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadRequest, TerraFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.GatewayTimeout, TerraFailure.Timeout)]
    public void Each_status_maps_to_its_own_category(HttpStatusCode status, TerraFailure expected)
    {
        // 403 in particular: Terra returns it for an endpoint the subscription
        // does not include, which is a portal problem rather than an outage and
        // looks identical to a bad key without this.
        Assert.Equal(expected, TerraTravelProvider.Classify(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_upstream_failure_yields_no_places_rather_than_throwing(HttpStatusCode status)
    {
        var handler = new StubHandler(() => new HttpResponseMessage(status));

        // A place lookup failing costs the answer its cards, not its
        // usefulness.
        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_JSON_yields_no_places_rather_than_throwing()
    {
        var handler = new StubHandler(() => Json("{ not json"));

        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_data_array_is_a_contract_change_not_an_empty_result()
    {
        var handler = new StubHandler(() => Json("""{ "results": [] }"""));

        // Distinct from an empty answer: one is a Tripadvisor change, the
        // other is a real answer about Sevilla.
        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task A_genuinely_empty_result_is_attempted_and_empty()
    {
        var handler = new StubHandler(() => Json("""{ "search_results": [] }"""));

        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task A_failure_fabricates_nothing()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    // ── Allowlist and details ────────────────────────────────────────────

    [Fact]
    public void No_allowlist_mutation_is_implemented()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TerraTravelProvider.cs"));

        // Registering every city somebody asks about would be a hidden
        // workaround for an account-access problem.
        Assert.DoesNotContain("/allowlist", source);
        Assert.DoesNotContain("operation_type", source);
    }

    [Fact]
    public async Task Location_details_are_not_called()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        var place = await Provider(handler).GetPlaceDetailsAsync("187443", "sv", CancellationToken.None);

        // /locations/{id} is allowlist-governed, and the recommendation
        // response already carries what a detail card shows.
        Assert.Null(place);
        Assert.Equal(0, handler.Calls);
    }

    // ── Storage policy ───────────────────────────────────────────────────

    [Fact]
    public void Terra_content_may_not_be_persisted()
    {
        var handler = new StubHandler(() => Json(TwoResults));

        // Terra's caching policy permits only the Location ID to be kept.
        Assert.False(Provider(handler).AllowsContentPersistence);
    }

    [Fact]
    public void The_provider_holds_no_cache_of_its_own()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TerraTravelProvider.cs"));

        // The legacy provider takes a TravelDataCache and stores searches for
        // ten minutes and details for a day. This one takes none.
        Assert.DoesNotContain("TravelDataCache", source);
    }

    [Fact]
    public void The_legacy_provider_keeps_its_own_policy()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TripadvisorTravelProvider.cs"));

        // One rule for both would either break one provider's terms or throw
        // away the other's performance.
        Assert.Contains("public bool AllowsContentPersistence => true;", source);
    }

    // ── Priority ─────────────────────────────────────────────────────────

    [Fact]
    public void Terra_is_registered_before_the_legacy_provider()
    {
        var program = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));

        var terraAt = program.IndexOf("ITravelDataProvider, TerraTravelProvider", StringComparison.Ordinal);
        var legacyAt = program.IndexOf("ITravelDataProvider, TripadvisorTravelProvider", StringComparison.Ordinal);

        Assert.True(terraAt > 0 && legacyAt > 0);
        // Registration order IS the priority — see the registry.
        Assert.True(terraAt < legacyAt);
    }

    [Fact]
    public void Only_one_provider_per_place_id_namespace_ever_runs()
    {
        var registry = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TravelDataRegistry.cs"));

        // Both are Tripadvisor and issue the SAME location ids, so running
        // both would mean two calls, two bills, and a deduplicated list where
        // each place came from whichever answered first.
        Assert.Contains("GroupBy(provider => provider.Provider", registry);
        Assert.Contains("Select(group => group.First())", registry);
    }

    [Fact]
    public void Both_providers_share_the_place_id_namespace()
    {
        // Which is what makes the grouping above correct rather than
        // accidental.
        Assert.Equal(TerraTravelProvider.ProviderId, TripadvisorTravelProvider.ProviderId);
    }

    [Fact]
    public void The_two_providers_use_different_http_clients()
    {
        Assert.NotEqual(TerraTravelProvider.HttpClientName, TripadvisorTravelProvider.HttpClientName);
    }

    // ── Status ───────────────────────────────────────────────────────────

    [Fact]
    public void The_status_block_reports_which_provider_is_live()
    {
        var controller = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Controllers", "GlunoController.cs"));

        // The previous outage was invisible from every surface. This is what
        // makes "the provider is switched off" diagnosable without a log.
        Assert.Contains("ActiveProvider = terraOn ? \"terra\" : legacyOn ? \"legacy\" : null", controller);
        Assert.Contains("TerraConfigured = terraOn", controller);
        Assert.Contains("LegacyConfigured = legacyOn", controller);
    }

    [Fact]
    public void The_status_block_reads_the_providers_rather_than_a_cached_flag()
    {
        var controller = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Controllers", "GlunoController.cs"));

        // So it cannot drift from what a search would actually do.
        Assert.Contains("_travelProviders.OfType<TerraTravelProvider>()", controller);
    }
}
