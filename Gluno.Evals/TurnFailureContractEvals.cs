using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Controllers;
using sidequest.backend.Dtos;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the JSON a failed turn actually puts on the wire.
///
/// Every other failure eval checks the CODE. These check the ENVELOPE — that
/// the code survives the trip from service to controller to serialised body,
/// under the field name the app reads, together with the retry flag.
///
/// This is where the generic "Gluno could not answer right now" came from
/// twice over: a branch that returned a bare status with no body at all, and
/// branches that returned a code the app had no copy for. Both look completely
/// fine from the backend's side — the endpoint returned, the log line was
/// clean, and the user got a shrug.
///
/// The controller is driven for real here. A stub chat service stands in for
/// the model, and nothing touches a network or a database.
/// </summary>
public class TurnFailureContractEvals
{
    /// The field name the mobile client reads the code from. Changing it is a
    /// breaking API change and this constant is what makes that visible.
    private const string CodeField = "error";
    private const string RetryField = "retryable";

    private static GlunoController Controller(GlunoTurnResult result)
    {
        var controller = new GlunoController(
            availability: StubAvailability(),
            chat: new StubChatService(result),
            conversations: null!,
            proposals: null!,
            apply: null!,
            routing: null!,
            dayPlanner: null!,
            liveTravel: null!,
            clarifications: null!,
            contextBuilder: null!,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<GlunoController>.Instance);

        // The endpoint reads the caller from the principal, never from the
        // body — so the test has to supply one the same way auth would.
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

        // IsAssignableFrom, not IsType: BadRequest/NotFound/Conflict all
        // return ObjectResult SUBCLASSES.
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action.Result);

        // Serialised with the app's own camelCase settings rather than
        // inspected as a CLR object: the property NAME is half the contract,
        // and an anonymous type reads the same whatever it serialises to.
        var json = JsonSerializer.Serialize(
            objectResult.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return (objectResult.StatusCode ?? 0, JsonSerializer.Deserialize<JsonElement>(json));
    }

    private static GlunoTurnResult Failure(GlunoTurnError error, string? code = null) => new()
    {
        Error = error,
        FailureCode = code,
    };

    // ── The provider path, end to end ────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GlunoFailureCodes.AiNotConfigured, false)]
    [InlineData(HttpStatusCode.NotFound, GlunoFailureCodes.ModelNotConfigured, false)]
    [InlineData(HttpStatusCode.BadRequest, GlunoFailureCodes.ModelNotConfigured, false)]
    [InlineData(HttpStatusCode.TooManyRequests, GlunoFailureCodes.AiRateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GlunoFailureCodes.AiTimeout, true)]
    public async Task An_Anthropic_failure_reaches_the_wire_intact(
        HttpStatusCode providerStatus, string expectedCode, bool expectedRetryable)
    {
        // The real mapping, not a hand-written code: this is the exact path a
        // rejected key or a retired model id takes.
        var code = GlunoFailureCodes.FromException(
            new AnthropicApiException("x", null) { StatusCode = providerStatus, ResponseBody = "" });

        var (status, body) = await SendAsync(Failure(GlunoTurnError.ProviderFailed, code));

        Assert.Equal(502, status);
        Assert.Equal(expectedCode, body.GetProperty(CodeField).GetString());
        Assert.Equal(expectedRetryable, body.GetProperty(RetryField).GetBoolean());
    }

    [Fact]
    public async Task A_provider_failure_never_arrives_without_a_code()
    {
        // Even when the service somehow reports none. An empty envelope is
        // what the app renders as the generic line.
        var (status, body) = await SendAsync(Failure(GlunoTurnError.ProviderFailed));

        Assert.Equal(502, status);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty(CodeField).GetString()));
    }

    // ── Every other failure branch ───────────────────────────────────────

    [Theory]
    [InlineData(GlunoTurnError.Unavailable, 503, "gluno_unavailable")]
    [InlineData(GlunoTurnError.EmptyMessage, 400, "empty_message")]
    [InlineData(GlunoTurnError.ConversationNotFound, 404, "conversation_not_found")]
    [InlineData(GlunoTurnError.ConversationArchived, 400, "conversation_archived")]
    [InlineData(GlunoTurnError.NotTripMember, 403, GlunoFailureCodes.AuthorizationChanged)]
    [InlineData(GlunoTurnError.DuplicateInFlight, 409, "duplicate_in_flight")]
    [InlineData(GlunoTurnError.Cancelled, 499, GlunoFailureCodes.Cancelled)]
    public async Task Every_turn_failure_carries_a_code_and_a_retry_flag(
        GlunoTurnError error, int expectedStatus, string expectedCode)
    {
        var (status, body) = await SendAsync(Failure(error));

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body.GetProperty(CodeField).GetString());

        // Both fields on every branch. "Not tripped member" used to be a bare
        // Forbid() — 403 with no body at all, which left the app with nothing
        // but a status code to guess from.
        Assert.True(
            body.TryGetProperty(RetryField, out var retryable),
            $"{error} returns no '{RetryField}' field");
        Assert.False(retryable.GetBoolean());
    }

    [Fact]
    public async Task A_usage_ceiling_is_refused_without_a_retry_offer()
    {
        var (status, body) = await SendAsync(
            Failure(GlunoTurnError.UsageLimitReached, GlunoFailureCodes.UserUsageLimit));

        Assert.Equal(429, status);
        Assert.Equal(GlunoFailureCodes.UserUsageLimit, body.GetProperty(CodeField).GetString());
        Assert.False(body.GetProperty(RetryField).GetBoolean());
    }

    [Fact]
    public async Task No_failure_body_carries_anything_but_the_envelope()
    {
        // A failure body is read by a client that shows it to somebody. It may
        // name the problem and nothing else — no message text, no provider
        // detail, no configuration.
        foreach (var error in Enum.GetValues<GlunoTurnError>().Where(e => e != GlunoTurnError.None))
        {
            var (_, body) = await SendAsync(Failure(error, GlunoFailureCodes.AiMalformedResponse));

            foreach (var property in body.EnumerateObject())
            {
                Assert.Contains(property.Name, new[] { CodeField, RetryField, "reason" });
            }
        }
    }

    /// Configured enough to answer; the turn fails for its own reasons.
    private static GlunoAvailability StubAvailability()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new GlunoAvailability(
            config,
            new StubEnvironment(),
            new AnthropicGlunoAiProvider(
                config, new GlunoModelPolicy(config),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnthropicGlunoAiProvider>.Instance),
            new TravelDataRegistry(
                [], Microsoft.Extensions.Logging.Abstractions.NullLogger<TravelDataRegistry>.Instance));
    }

    private sealed class StubEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubChatService : IGlunoChatService
    {
        private readonly GlunoTurnResult _result;

        public StubChatService(GlunoTurnResult result) => _result = result;

        public Task<GlunoTurnResult> ContinueFromClarificationAsync(
            Guid userId, sidequest.backend.Models.GlunoClarification clarification,
            sidequest.backend.Models.GlunoClarificationOption option,
            string? idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<GlunoTurnResult> ContinueFromDraftAsync(
            Guid userId, sidequest.backend.Models.GlunoClarification clarification,
            sidequest.backend.Models.GlunoClarificationOption option, CancellationToken ct)
            => throw new NotSupportedException();


        public Task<GlunoTurnResult> SendAsync(
            Guid userId, Guid? conversationId, Guid? tripId, string message,
            string? screen, string? idempotencyKey, CancellationToken ct)
            => Task.FromResult(_result);
    }
}
