using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Deterministic contract tests that capture the REAL HttpRequestMessage the
/// Terra provider builds — not string-searches over the source.
///
/// WHY. Production sends a valid-looking request and gets 401. Every layer of
/// wiring on our side has been string-verified before; these evals verify it
/// EXECUTABLY instead: the exact URL, method, auth header (exactly once,
/// non-empty, no prefix), Content-Type, Accept, and the exact request JSON
/// against Terra's documented schema (docs.terra.tripadvisor.com/reference/
/// recommendationssearch-1: POST https://terra.tripadvisor.com/api/
/// recommendations/search, X-API-Key raw value, Content-Type and Accept
/// application/json, body fields query/geo/limit/top_level_categories/
/// exclude_location_ids/response_preference).
///
/// A keyless probe against the live endpoint answers 401 with zero redirects
/// on the same host, so nothing between us and Terra strips the header — if
/// the captured request is right, the 401 is about the KEY'S standing on
/// Terra's side, not about this code.
///
/// The fake key below is a test constant, never a secret.
/// </summary>
public class TerraRequestCaptureEvals
{
    private const string FakeKey = "eval-fake-key-not-a-secret";

    private static TerraTravelProvider Provider(
        HttpMessageHandler handler, ILogger<TerraTravelProvider>? logger = null,
        params (string Key, string? Value)[] extraSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TripadvisorTerra:Enabled"] = "true",
            ["TripadvisorTerra:ApiKey"] = FakeKey,
        };
        foreach (var (key, value) in extraSettings) settings[key] = value;

        return new TerraTravelProvider(
            new SingleClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            logger ?? NullLogger<TerraTravelProvider>.Instance);
    }

    private static TravelPlaceQuery Query() => new()
    {
        Near = "Linz",
        Category = TravelPlaceCategory.Attraction,
        Limit = 3,
        Language = "sv",
        ExcludedLocationIds = new[] { "123456" },
        RequestId = "evalreq000001",
    };

    // ── The captured request, field by field ──────────────────────────────

    [Fact]
    public async Task The_request_matches_the_documented_contract_exactly()
    {
        var handler = new CapturingHandler();
        var provider = Provider(handler);

        var result = await ((ITravelDataProvider)provider).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Ok, result.Status);
        Assert.Equal(1, handler.Calls);

        var request = handler.Captured!;

        // URL and method, exactly as the reference documents them.
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://terra.tripadvisor.com/api/recommendations/search",
            request.RequestUri!.ToString());
        // No query string — nothing for a proxy log to capture.
        Assert.True(string.IsNullOrEmpty(request.RequestUri.Query));

        // The auth header: present, exactly once, the raw value, no prefix.
        Assert.True(request.Headers.TryGetValues("X-API-Key", out var keys));
        var keyValues = keys!.ToList();
        Assert.Single(keyValues);
        Assert.Equal(FakeKey, keyValues[0]);
        Assert.False(string.IsNullOrWhiteSpace(keyValues[0]));
        Assert.Null(request.Headers.Authorization);

        // Accept and Content-Type, as the reference requires.
        Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "application/json");
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_body_carries_only_documented_fields_with_documented_casing()
    {
        var handler = new CapturingHandler();
        await ((ITravelDataProvider)Provider(handler)).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.CapturedBody!);
        var root = body.RootElement;

        // Required fields.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("query").GetString()));
        Assert.Equal("Linz", root.GetProperty("geo").GetProperty("name").GetString());

        // Optional fields we send.
        Assert.Equal(3, root.GetProperty("limit").GetInt32());
        var excluded = root.GetProperty("exclude_location_ids");
        Assert.Equal(JsonValueKind.Array, excluded.ValueKind);
        Assert.Equal(123456, excluded[0].GetInt64());

        // NOTHING undocumented — an unknown field is a 400 that costs the
        // whole answer, and the whitelist is the documented schema.
        var documented = new HashSet<string>
        {
            "query", "geo", "limit", "top_level_categories",
            "exclude_location_ids", "response_preference",
        };
        foreach (var property in root.EnumerateObject())
        {
            Assert.Contains(property.Name, documented);
        }
    }

    [Fact]
    public async Task A_fresh_request_message_is_built_per_call()
    {
        // An HttpRequestMessage cannot be reused; a second search must build
        // its own, with its own header.
        var handler = new CapturingHandler();
        var provider = Provider(handler);

        await ((ITravelDataProvider)provider).SearchPlacesWithStatusAsync(Query(), CancellationToken.None);
        var first = handler.Captured;
        await ((ITravelDataProvider)provider).SearchPlacesWithStatusAsync(Query(), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.NotSame(first, handler.Captured);
        Assert.True(handler.Captured!.Headers.TryGetValues("X-API-Key", out var keys));
        Assert.Equal(FakeKey, Assert.Single(keys!));
    }

    [Fact]
    public async Task The_legacy_section_never_feeds_the_terra_request()
    {
        // A legacy key present beside an EMPTY Terra key must not leak in —
        // the provider reports unconfigured and never even builds a request.
        var handler = new CapturingHandler();
        var provider = Provider(handler,
            logger: null,
            ("TripadvisorTerra:ApiKey", ""),
            ("Tripadvisor:ApiKey", "legacy-key-value"),
            ("Tripadvisor:Enabled", "true"));

        Assert.False(provider.IsConfigured);

        var result = await ((ITravelDataProvider)provider).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    // ── The 401 diagnostic path, executed ─────────────────────────────────

    [Fact]
    public async Task A_401_problem_logs_status_and_type_but_never_title_or_body()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"type":"https://terra.tripadvisor.com/problems/unauthorized","title":"SECRET-TITLE-MARKER","detail":"SECRET-DETAIL-MARKER","status":401}""",
                    Encoding.UTF8, "application/problem+json"),
            },
        };

        var logger = new CollectingLogger();
        var result = await ((ITravelDataProvider)Provider(handler, logger)).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Failed, result.Status);

        var lines = string.Join('\n', logger.Lines);

        // The safe line carries the status and Terra's own problem TYPE —
        // enough to name the 401 class — and the needs-attention flag fires.
        Assert.Contains("terra rejected request", lines);
        Assert.Contains("401", lines);
        Assert.Contains("https://terra.tripadvisor.com/problems/unauthorized", lines);
        Assert.Contains("terra needs attention", lines);

        // Never the title, the detail, or the key.
        Assert.DoesNotContain("SECRET-TITLE-MARKER", lines);
        Assert.DoesNotContain("SECRET-DETAIL-MARKER", lines);
        Assert.DoesNotContain(FakeKey, lines);
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured;
        public string? CapturedBody;
        public int Calls;

        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Captured = request;
            CapturedBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"search_results": []}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// Collects FORMATTED log lines so assertions can check what a reader of
    /// the production log would actually see.
    private sealed class CollectingLogger : ILogger<TerraTravelProvider>
    {
        public readonly List<string> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
