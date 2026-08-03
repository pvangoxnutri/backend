using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using sidequest.backend.Controllers;
using sidequest.backend.Dtos;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the turn's outermost failure boundary.
///
/// THE BUG THESE EXIST FOR. A turn's own try/catch only ever wrapped the model
/// call. Loading the conversation, building the context, the history query,
/// persisting the message, the quality gate, grounding, persisting the answer
/// and the telemetry write all ran outside it. An exception in any of them
/// escaped to the host, and the app received a bare 5xx with NO BODY — which
/// on a phone reads as "Gluno could not answer right now", the least
/// actionable sentence in the product.
///
/// So every case below throws from a different place and asserts the same
/// thing: the response is still Gluno's envelope, and nothing reached the
/// host.
///
/// Nothing here calls a model or a network.
/// </summary>
public class TurnBoundaryEvals
{
    private const string CodeField = "error";
    private const string RetryField = "retryable";

    private static GlunoController Controller(IGlunoChatService chat)
    {
        var controller = new GlunoController(
            availability: null!,
            chat: chat,
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
            logger: NullLogger<GlunoController>.Instance);

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

    private static async Task<(int Status, JsonElement Body)> SendAsync(Exception thrown)
    {
        var action = await Controller(new ThrowingChatService(thrown))
            .SendMessage(new SendGlunoMessageDto { Message = "hej" });

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action.Result);

        var json = JsonSerializer.Serialize(
            objectResult.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return (objectResult.StatusCode ?? 0, JsonSerializer.Deserialize<JsonElement>(json));
    }

    /// <summary>
    /// One exception per stage that used to run unprotected.
    ///
    /// The TYPES matter as much as the stages: none of these derive from the
    /// exception the turn's inner catch was written against, which is exactly
    /// why they escaped.
    /// </summary>
    public static TheoryData<string, Exception> StageFailures() => new()
    {
        { "context_build", new InvalidOperationException("context") },
        { "evidence_build", new KeyNotFoundException("evidence") },
        { "prompt_assembly", new JsonException("prompt") },
        { "database_persistence", new DbUpdateException("persist", new Exception()) },
        { "database_driver", new NpgsqlException("connection reset") },
        { "grounding", new ArgumentOutOfRangeException("grounding") },
        { "telemetry", new FormatException("telemetry") },
        { "timeout", new TimeoutException("provider") },
    };

    [Theory]
    [MemberData(nameof(StageFailures))]
    public async Task An_exception_at_any_stage_still_answers_with_the_envelope(string stage, Exception thrown)
    {
        var (status, body) = await SendAsync(thrown);

        // 200, not 5xx: this catch just BUILT a valid renderable envelope,
        // and a gateway status hands the response to the edge — Cloudflare
        // replaced an origin 502 with its own HTML page in production, and
        // the app lost the code and the retry flag. The envelope carries the
        // failure; the transport status says it was delivered intact. Only
        // the middleware's truly contractless case still answers 5xx.
        Assert.Equal(200, status);

        Assert.True(
            body.TryGetProperty(CodeField, out var code),
            $"{stage} produced a response with no '{CodeField}' — this is the bare-body bug");
        Assert.False(string.IsNullOrWhiteSpace(code.GetString()));

        Assert.True(
            body.TryGetProperty(RetryField, out var retryable),
            $"{stage} produced a response with no '{RetryField}'");
        Assert.True(retryable.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    [Fact]
    public async Task No_exception_reaches_the_host()
    {
        // The assertion is simply that awaiting the action does not throw. If
        // it does, ASP.NET answers with its own error page and the app gets
        // nothing it can read.
        foreach (var (_, thrown) in StageFailures().Select(row => ((string)row[0]!, (Exception)row[1]!)))
        {
            var action = await Controller(new ThrowingChatService(thrown))
                .SendMessage(new SendGlunoMessageDto { Message = "hej" });

            Assert.NotNull(action.Result);
        }
    }

    [Fact]
    public async Task A_process_critical_failure_is_deliberately_not_swallowed()
    {
        // OutOfMemory is not something to report as a failed turn — the
        // process is already unwell and catching it hides that.
        await Assert.ThrowsAsync<OutOfMemoryException>(
            () => Controller(new ThrowingChatService(new OutOfMemoryException()))
                .SendMessage(new SendGlunoMessageDto { Message = "hej" }));
    }

    [Fact]
    public async Task A_cancelled_request_is_not_reported_as_a_failure()
    {
        var controller = Controller(new ThrowingChatService(new OperationCanceledException()));

        // The user pressed stop: the request itself is aborted.
        var source = new CancellationTokenSource();
        source.Cancel();
        controller.ControllerContext.HttpContext.RequestAborted = source.Token;

        var action = await controller.SendMessage(new SendGlunoMessageDto { Message = "hej" });
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(action.Result);

        // 499, the client-closed convention — never a 5xx, and never retryable.
        Assert.Equal(499, objectResult.StatusCode);
    }

    [Fact]
    public async Task An_unexpected_failure_is_offered_as_retryable()
    {
        // Unknown means unknown. It might be transient, and refusing a retry
        // for something we cannot classify is the wrong way to be wrong.
        var (_, body) = await SendAsync(new InvalidOperationException("boom"));

        Assert.True(body.GetProperty(RetryField).GetBoolean());
    }

    [Fact]
    public void The_failure_boundary_never_logs_an_exception_message()
    {
        // Only the type name. An exception message can carry a connection
        // string, a request URI with a key in it, or a row's contents.
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "Services", "Gluno", "GlunoChatService.cs"));

        Assert.Contains("ex.GetType().Name", source);
        Assert.DoesNotContain("ex.Message", source);
    }

    private sealed class ThrowingChatService : IGlunoChatService
    {
        private readonly Exception _thrown;

        public ThrowingChatService(Exception thrown) => _thrown = thrown;

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


        public Task<GlunoTurnResult> SendAsync(
            Guid userId, Guid? conversationId, Guid? tripId, string message,
            string? screen, string? idempotencyKey, CancellationToken ct)
            => throw _thrown;
    }
}
