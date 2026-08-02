using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the place provider actually producing places.
///
/// THE PRODUCTION SYMPTOM. "What should we see in Sevilla?" answered with prose
/// instead of tappable place cards. The rendering chain was proved correct, so
/// the question was why `outcome.Places` was empty.
///
/// THE THING THAT MADE IT HARD TO DIAGNOSE, and the reason these exist: four
/// completely different situations produced one indistinguishable outcome. A
/// provider with no API key returned an empty list. So did a provider that
/// answered with nothing. So did one that timed out, and one whose results were
/// all discarded by our own mapping. From outside, all four looked like
/// "Sevilla has no attractions".
///
/// So the provider is driven for real here — its own configuration, its own
/// HTTP client, a stubbed transport — and each failure is separated from the
/// others.
///
/// No network. No key. The stub answers every request.
/// </summary>
public class PlaceProviderEvals
{
    /// <summary>
    /// A transport that answers without a network.
    ///
    /// The API key rides in the query string on this provider, so the handler
    /// deliberately never records or asserts on the URI — a test that captured
    /// it would put a secret in a failure message.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        public int Calls { get; private set; }

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            // The PATH only. Never the query string.
            return Task.FromResult(_respond(request.RequestUri!.AbsolutePath));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static IConfiguration Config(bool enabled = true, string? key = "test-key")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tripadvisor:Enabled"] = enabled ? "true" : "false",
                ["Tripadvisor:ApiKey"] = key,
                ["Tripadvisor:BaseUrl"] = "https://api.example.test",
                ["Tripadvisor:MaxDetailHydrations"] = "6",
                ["Tripadvisor:IncludePhotos"] = "false",
            })
            .Build();

    private static TripadvisorTravelProvider Provider(
        StubHandler handler, IConfiguration? config = null)
        => new(
            new StubFactory(handler),
            config ?? Config(),
            new TravelDataCache(),
            NullLogger<TripadvisorTravelProvider>.Instance);

    private static TravelPlaceQuery SevillaQuery() => new()
    {
        Query = "things to do",
        Near = "Sevilla",
        Category = TravelPlaceCategory.Attraction,
        Limit = 5,
        Language = "sv",
    };

    /// A search response naming five attractions.
    private const string FiveResults = """
        { "data": [
          { "location_id": "1", "name": "Real Alcázar" },
          { "location_id": "2", "name": "Catedral de Sevilla" },
          { "location_id": "3", "name": "Plaza de España" },
          { "location_id": "4", "name": "Metropol Parasol" },
          { "location_id": "5", "name": "Barrio Santa Cruz" }
        ] }
        """;

    // ── The happy path ───────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_provider_response_produces_places()
    {
        // Details fail for every candidate, so each place falls back to its
        // search identity — which is exactly the "detail call failed but the
        // result is still usable" path.
        var handler = new StubHandler(path =>
            path.Contains("search", StringComparison.Ordinal)
                ? Json(FiveResults)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Equal(5, places.Count);
        Assert.Contains(places, place => place.Name == "Real Alcázar");
    }

    [Fact]
    public async Task Every_place_keeps_a_namespaced_identity_and_attribution()
    {
        var handler = new StubHandler(path =>
            path.Contains("search", StringComparison.Ordinal)
                ? Json(FiveResults)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.All(places, place =>
        {
            Assert.StartsWith("tripadvisor:", place.ExternalId);
            Assert.False(string.IsNullOrWhiteSpace(place.SourceAttribution));
        });
    }

    // ── Optional fields must not discard a place ─────────────────────────

    [Fact]
    public async Task A_place_with_no_rating_no_image_and_no_hours_survives()
    {
        // The minimum a place can be: an id and a name.
        var handler = new StubHandler(path =>
            path.Contains("/search", StringComparison.Ordinal)
                ? Json("""{ "data": [ { "location_id": "9", "name": "Bare Place" } ] }""")
                : Json("""{ "location_id": "9", "name": "Bare Place" }"""));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Single(places);
        // Missing means null, not zero and not a placeholder — a caller must be
        // able to tell "not returned" from "is zero".
        Assert.Null(places[0].Rating);
        Assert.Null(places[0].ImageUrl);
        Assert.Empty(places[0].OpeningHours);
    }

    [Fact]
    public async Task A_result_with_no_id_or_no_name_is_the_only_thing_discarded()
    {
        var handler = new StubHandler(path =>
            path.Contains("search", StringComparison.Ordinal)
                ? Json("""
                    { "data": [
                      { "location_id": "1", "name": "Keeps this" },
                      { "name": "No id" },
                      { "location_id": "3" },
                      { "location_id": "4", "name": "Keeps this too" }
                    ] }
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // Identity or name missing is unusable. Everything else is optional.
        Assert.Equal(2, places.Count);
    }

    [Fact]
    public async Task A_failed_detail_call_still_leaves_a_usable_place()
    {
        var handler = new StubHandler(path =>
            path.Contains("/search", StringComparison.Ordinal)
                ? Json("""{ "data": [ { "location_id": "1", "name": "Real Alcázar", "address_obj": { "address_string": "Sevilla" } } ] }""")
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // Name and address beat dropping the result entirely.
        Assert.Single(places);
        Assert.Equal("Real Alcázar", places[0].Name);
    }

    // ── The failures, kept apart ─────────────────────────────────────────

    [Fact]
    public async Task An_unconfigured_provider_makes_no_call_at_all()
    {
        var handler = new StubHandler(_ => Json(FiveResults));

        var places = await Provider(handler, Config(enabled: false))
            .SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Empty(places);
        // The distinguishing fact: no request was made. An empty list from a
        // provider that was never asked is not an answer about Sevilla.
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_missing_key_leaves_the_provider_unconfigured()
    {
        var handler = new StubHandler(_ => Json(FiveResults));

        var provider = Provider(handler, Config(key: null));

        Assert.False(provider.IsConfigured);
        Assert.Empty(await provider.SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_non_https_base_url_leaves_the_provider_unconfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tripadvisor:Enabled"] = "true",
                ["Tripadvisor:ApiKey"] = "test-key",
                // The key rides in the query string, so plaintext would put it
                // on the wire.
                ["Tripadvisor:BaseUrl"] = "http://api.example.test",
            })
            .Build();

        Assert.False(Provider(new StubHandler(_ => Json(FiveResults)), config).IsConfigured);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Every_upstream_failure_yields_no_places_rather_than_throwing(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        // A place lookup failing must never fail the turn — the answer loses
        // its cards, not its usefulness.
        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        Assert.Empty(places);
        // And it WAS attempted, which is what separates this from
        // not-configured.
        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task An_unreadable_body_yields_no_places_rather_than_throwing()
    {
        var handler = new StubHandler(_ => Json("{ not json"));

        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task A_response_with_no_data_array_yields_no_places()
    {
        var handler = new StubHandler(_ => Json("""{ "message": "no results" }"""));

        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task A_genuinely_empty_result_yields_no_places()
    {
        var handler = new StubHandler(_ => Json("""{ "data": [] }"""));

        // Different from every failure above, and the only one that is a real
        // answer about the place searched for.
        Assert.Empty(await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None));
        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task A_provider_failure_fabricates_nothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var places = await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // The one outcome that would be worse than no cards: invented ones.
        Assert.Empty(places);
    }

    // ── The query that gets sent ─────────────────────────────────────────

    [Fact]
    public void An_attraction_query_maps_to_the_attractions_category()
    {
        // "What should we see" is attractions — not hotels, not restaurants.
        Assert.Equal(TravelPlaceCategory.Attraction, TravelPlaceCategories.Parse("attraction"));
        Assert.Equal(TravelPlaceCategory.Attraction, TravelPlaceCategories.Parse("sights"));
        Assert.Equal(TravelPlaceCategory.Attraction, TravelPlaceCategories.Parse("museum"));
    }

    [Fact]
    public async Task The_search_endpoint_is_used_when_there_is_a_search_term()
    {
        var paths = new List<string>();

        var handler = new StubHandler(path =>
        {
            paths.Add(path);
            return Json("""{ "data": [] }""");
        });

        await Provider(handler).SearchPlacesAsync(SevillaQuery(), CancellationToken.None);

        // location/search, not nearby_search: we know WHAT but not exact
        // coordinates, and nearby_search needs the latter.
        Assert.Contains(paths, path => path.EndsWith("/location/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinates_without_a_term_use_the_nearby_endpoint()
    {
        var paths = new List<string>();

        var handler = new StubHandler(path =>
        {
            paths.Add(path);
            return Json("""{ "data": [] }""");
        });

        await Provider(handler).SearchPlacesAsync(
            new TravelPlaceQuery
            {
                Query = string.Empty,
                Latitude = 37.39,
                Longitude = -5.98,
                Category = TravelPlaceCategory.Attraction,
                Limit = 5,
                Language = "sv",
            },
            CancellationToken.None);

        Assert.Contains(paths, path => path.EndsWith("/location/nearby_search", StringComparison.Ordinal));
    }

    // ── The diagnostics that would have found this ───────────────────────

    private static string RegistrySource() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Services", "Gluno", "TravelDataRegistry.cs"));

    [Fact]
    public void Not_configured_is_logged_as_its_own_reason()
    {
        var source = RegistrySource();

        // The whole diagnostic gap: a Gluno that had never had a key looked
        // exactly like one whose search found nothing.
        Assert.Contains("reason=not_configured", source);
    }

    [Fact]
    public void A_total_provider_outage_is_told_apart_from_an_empty_answer()
    {
        var source = RegistrySource();

        Assert.Contains("all_providers_failed", source);
        Assert.Contains("provider_returned_zero", source);
    }

    [Fact]
    public void Results_discarded_by_our_own_code_are_logged_separately()
    {
        var source = RegistrySource();

        // "The provider found nothing" and "the provider found things and we
        // dropped them all" need different fixes — one is an API key, the
        // other is a mapping bug.
        Assert.Contains("place search dropped every result", source);
        Assert.Contains("reason=mapping_or_ranking", source);
    }

    [Fact]
    public void The_counts_before_and_after_mapping_are_both_logged()
    {
        var source = RegistrySource();

        Assert.Contains("raw={Raw} deduped={Deduped} ranked={Ranked}", source);
    }

    [Fact]
    public void No_diagnostic_carries_a_query_a_key_or_a_body()
    {
        var source = RegistrySource();

        var start = source.IndexOf("public async Task<IReadOnlyList<RankedTravelPlace>> SearchPlacesAsync", StringComparison.Ordinal);
        var body = source[start..(start + 3200)];

        Assert.True(start > 0);
        // Counts and a category. The category is a fixed vocabulary value, not
        // user text.
        Assert.DoesNotContain("query.Query", body);
        Assert.DoesNotContain("query.Near", body);
        Assert.DoesNotContain("ApiKey", body);
    }

    [Fact]
    public void The_provider_never_logs_a_request_uri()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "TripadvisorTravelProvider.cs"));

        // The API key is a QUERY PARAMETER on this provider, so a logged URI is
        // a logged secret. Status and endpoint type only.
        Assert.DoesNotContain("RequestUri}", source);
        Assert.DoesNotContain("ex.Message", source);
    }

    [Fact]
    public void The_http_client_has_its_default_loggers_removed()
    {
        var program = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Program.cs"));

        // HttpClientFactory logs request URIs at Information by default, and
        // this provider's URIs contain the key.
        Assert.Contains(".RemoveAllLoggers()", program);
    }
}
