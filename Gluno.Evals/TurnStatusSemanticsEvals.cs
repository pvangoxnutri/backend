using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Controllers;
using sidequest.backend.Dtos;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the HTTP status a handled turn failure travels under.
///
/// THE PROVEN PRODUCTION FAILURE. A direct place search completed in 4.6s,
/// the backend built the full envelope — tripadvisor_unavailable, retryable,
/// responseOrigin=direct_place_search, requestId — and sent it as HTTP 502.
/// Cloudflare, in front of the origin, treated the gateway status as its own
/// and replaced the response with an HTML error page. The app received
/// text/html with no envelope and no X-Gluno-Request-Id, and could only say
/// the connection broke.
///
/// THE INVARIANT PINNED HERE. A failure this process has already turned into
/// a renderable turn result travels as HTTP 200 — transport status describes
/// the PROTOCOL, the envelope describes the TURN. Real request/auth failures
/// keep their real 4xx statuses, 503 keeps its availability semantics, and
/// only the truly contractless case (the middleware's own exception
/// envelope) may still use 5xx.
///
/// The controller is driven for real; a stub chat service stands in for the
/// model. Source assertions cover the mobile side and are labelled as such.
/// </summary>
public class TurnStatusSemanticsEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static GlunoController Controller(GlunoTurnResult result)
    {
        var controller = new GlunoController(
            availability: null!,
            chat: new StubChat(result),
            conversations: null!,
            proposals: null!,
            apply: null!,
            routing: null!,
            dayPlanner: null!,
            liveTravel: null!,
            clarifications: null!,
            contextBuilder: null!,
            travelProviders: Array.Empty<ITravelDataProvider>(),
            travelRegistry: new TravelDataRegistry([], Microsoft.Extensions.Logging.Abstractions.NullLogger<TravelDataRegistry>.Instance),
            rehydrator: new UnusedRehydrator(),
            diagnostics: new GlunoRequestDiagnostics(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<GlunoController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "test")),
            },
        };

        return controller;
    }

    private static async Task<(int Status, JsonElement Body)> SendAsync(GlunoTurnResult result)
    {
        var action = await Controller(result).SendMessage(new SendGlunoMessageDto { Message = "hej" });
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action.Result);

        var json = JsonSerializer.Serialize(
            objectResult.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return (objectResult.StatusCode ?? 0, JsonSerializer.Deserialize<JsonElement>(json));
    }

    // ── 1–5. The proven failure, replayed against the real controller ─────

    [Fact]
    public async Task A_provider_failure_travels_as_200_with_the_full_envelope()
    {
        var (status, body) = await SendAsync(new GlunoTurnResult
        {
            Error = GlunoTurnError.ProviderFailed,
            FailureCode = GlunoFailureCodes.TripadvisorUnavailable,
            ResponseOrigin = GlunoResponseOrigins.DirectPlaceSearch,
        });

        // HTTP 200: nothing between the backend and the app may replace this
        // response. The turn's outcome lives in the body.
        Assert.Equal(200, status);
        Assert.Equal("tripadvisor_unavailable", body.GetProperty("code").GetString());
        Assert.Equal("tripadvisor_unavailable", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("direct_place_search", body.GetProperty("responseOrigin").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("requestId").GetString()));
    }

    // ── 12. No renderable turn failure may use a gateway status ───────────

    [Fact]
    public async Task No_renderable_turn_failure_uses_a_gateway_status()
    {
        // The exact codes the production incidents travelled under, plus the
        // rest of the provider tier. StatusFor may still CLASSIFY them as the
        // provider tier — but the wire status for a handled turn result must
        // never be one the edge can replace with HTML.
        foreach (var code in new[]
        {
            GlunoFailureCodes.TripadvisorUnavailable,
            GlunoFailureCodes.AiMalformedResponse,
            GlunoFailureCodes.AiTimeout,
            GlunoFailureCodes.AiRateLimited,
            GlunoFailureCodes.RoutingUnavailable,
            GlunoFailureCodes.WeatherUnavailable,
            GlunoFailureCodes.GroundingFailed,
        })
        {
            Assert.True(GlunoErrors.DeliveredAsTurnResult(code), $"{code} must be a turn result");

            var (status, _) = await SendAsync(new GlunoTurnResult
            {
                Error = GlunoTurnError.ProviderFailed,
                FailureCode = code,
            });

            Assert.False(status is 502 or 503 or 504, $"{code} travelled as {status} — the edge may replace that");
            Assert.Equal(200, status);
        }
    }

    // ── 13. Real request and auth failures keep their real statuses ───────

    [Theory]
    [InlineData(GlunoTurnError.EmptyMessage, 400)]
    [InlineData(GlunoTurnError.ConversationNotFound, 404)]
    [InlineData(GlunoTurnError.ConversationArchived, 400)]
    [InlineData(GlunoTurnError.NotTripMember, 403)]
    [InlineData(GlunoTurnError.DuplicateInFlight, 409)]
    [InlineData(GlunoTurnError.Cancelled, 499)]
    [InlineData(GlunoTurnError.Unavailable, 503)]
    public async Task Real_http_failures_keep_their_statuses(GlunoTurnError error, int expected)
    {
        var (status, _) = await SendAsync(new GlunoTurnResult { Error = error });
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task The_users_own_rate_limit_is_still_a_429()
    {
        var (status, body) = await SendAsync(new GlunoTurnResult
        {
            Error = GlunoTurnError.UsageLimitReached,
            FailureCode = GlunoFailureCodes.UserUsageLimit,
        });

        Assert.Equal(429, status);
        Assert.Equal(GlunoFailureCodes.UserUsageLimit, body.GetProperty("code").GetString());
    }

    // ── 14. Only the contractless case may still answer 5xx ───────────────

    [Fact]
    public void The_middleware_keeps_5xx_for_the_truly_contractless_case()
    {
        var program = Source("Program.cs");

        // An exception that escapes the controller entirely — no valid turn
        // result was ever built. That envelope is written by the middleware
        // and stays on the gateway tier; everything the controller handled
        // is a 200 turn result.
        Assert.Contains(
            "ctx.Response.StatusCode = GlunoErrors.StatusFor(GlunoFailureCodes.AiMalformedResponse);",
            program);

        // And the controller's own last resort — which DOES build a valid
        // envelope — no longer hands it to the edge.
        var controller = Source("Controllers", "GlunoController.cs");
        Assert.Contains("return Ok(GlunoErrors.Body(", controller);
    }

    // ── 15–16. The diagnostics tell the truth about the new shape ─────────

    [Fact]
    public void The_summary_line_reports_the_wire_status_beside_the_error_code()
    {
        // The middleware logs ctx.Response.StatusCode in finally and the
        // controller stamps the code — so the post-fix summary for the same
        // provider outcome reads httpStatus=200 errorCode=tripadvisor_…
        var program = Source("Program.cs");
        Assert.Contains("glunoDiagnostics.WriteSummary(app.Logger, ctx.Response.StatusCode)", program);

        var controller = Source("Controllers", "GlunoController.cs");
        Assert.Contains("_diagnostics.ErrorCode ??= code;", controller);
    }

    [Fact]
    public void Provider_status_is_stamped_from_the_registrys_real_answer()
    {
        // WHY THE PRODUCTION LINE SAID Unknown. The stamp reads the registry's
        // real status. Terra overrides SearchPlacesWithStatusAsync and always
        // reports Ok/Failed/RateLimited; the LEGACY provider does not, so the
        // interface default answers Unknown — "this provider cannot say".
        // Unknown in the log therefore means the legacy provider ran, which
        // is a configuration fact, not a missed assignment.
        var service = Source("Services", "Gluno", "GlunoChatService.cs");
        Assert.Contains("_diagnostics.ProviderStatus = result.Status.ToString();", service);

        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");
        Assert.Contains("public async Task<TravelSearchResult> SearchPlacesWithStatusAsync(", terra);

        var contract = Source("Services", "Gluno", "ITravelDataProvider.cs");
        Assert.Contains("Status = TravelSearchStatus.Unknown,", contract);
    }

    // ── 7–11. The mobile side of the same contract ────────────────────────

    [Fact]
    public void The_app_recognises_a_turn_failure_delivered_as_200()
    {
        var gluno = Mobile("lib", "gluno.ts");

        // Recognised BY SHAPE on the success path — code + retryable exist on
        // no successful Gluno payload — and thrown as the same typed error
        // the non-2xx path throws, with origin and both ids preserved.
        Assert.Contains("if (typeof contract?.code === 'string' && typeof contract?.retryable === 'boolean')", gluno);
        Assert.Contains("error.requestId = typeof envelope.requestId === 'string' ? envelope.requestId : requestId;", gluno);
        Assert.Contains("error.clientRequestId = clientRequestId;", gluno);
    }

    [Fact]
    public void The_app_never_classifies_a_200_failure_as_an_edge_failure()
    {
        var gluno = Mobile("lib", "gluno.ts");

        // The edge_ classification lives INSIDE the !ok branch — a 200 with
        // an envelope can never reach it.
        var notOk = gluno.IndexOf("if (!response.ok) {", StringComparison.Ordinal);
        var edge = gluno.IndexOf("error.code = EDGE_STATUSES.has(response.status)", StringComparison.Ordinal);
        var okReturn = gluno.IndexOf("return payload;", StringComparison.Ordinal);

        Assert.True(notOk >= 0 && edge > notOk && okReturn > edge,
            "edge classification must sit inside the !ok branch, before the success return");
    }

    [Fact]
    public void The_failed_row_renders_the_provider_sentence_with_a_retry()
    {
        // The 200 failure surfaces through the SAME failed-row path: the code
        // has its own copy, the server's retry verdict wins, and the retry
        // resends the row's own text. All pinned elsewhere too — this is the
        // contract seam for the new status.
        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("tripadvisor_unavailable: 'gluno.error.placesUnavailable'", copy);

        var screen = Mobile("app", "gluno.tsx");
        Assert.Contains("retryable: failure.retryable,", screen);
        Assert.Contains("await deliver(message.text, message.id, key);", screen);

        // And the composer stays out of it entirely.
        Assert.DoesNotContain("? text : current", screen);
    }

    private sealed class StubChat : IGlunoChatService
    {
        private readonly GlunoTurnResult _result;

        public StubChat(GlunoTurnResult result) => _result = result;

        public Task<GlunoTurnResult> SendAsync(
            Guid userId, Guid? conversationId, Guid? tripId, string message,
            string? screen, string? idempotencyKey, CancellationToken ct)
            => Task.FromResult(_result);

        public Task<GlunoTurnResult> ContinueFromClarificationAsync(
            Guid userId, sidequest.backend.Models.GlunoClarification clarification,
            sidequest.backend.Models.GlunoClarificationOption option,
            string? idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<GlunoTurnResult> ContinueFromDraftAsync(
            Guid userId, sidequest.backend.Models.GlunoClarification clarification,
            sidequest.backend.Models.GlunoClarificationOption option, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<GlunoTurnResult> RefreshPlaceSuggestionsAsync(
            Guid userId, sidequest.backend.Models.GlunoMessage message,
            string? idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<GlunoTurnResult> AddRecommendedPlaceAsync(
            Guid userId, sidequest.backend.Models.GlunoMessage message, string optionKey,
            DateOnly? date, string? idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
