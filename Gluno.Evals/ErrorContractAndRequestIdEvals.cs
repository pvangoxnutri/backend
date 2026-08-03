using System.Text.Json;
using System.Text.RegularExpressions;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the error-contract boundary and the request-id trail, added after
/// the production test of 2026-08-03: a failed turn reached the app as the
/// GENERIC "Gluno kunde inte svara just nu" line instead of the provider
/// error's own sentence.
///
/// WHAT THE INVESTIGATION ESTABLISHED. The mobile app renders the generic line
/// only for a failure code it has no copy for — and every code the backend's
/// closed list can send IS mapped. So the symptom proves the response carried
/// no readable envelope at all: an exception that escaped the controller or
/// response serialization (Kestrel answers 500 with an EMPTY body — no
/// exception middleware existed), an ad-hoc "unknown" code from the
/// controller's last-resort catch, or an edge response from outside the
/// backend entirely.
///
/// WHAT IS PINNED HERE: every layer of the Gluno request now answers with the
/// one envelope (middleware, controller, service, provider), the mobile parser
/// reads the envelope before giving up, an id minted per request follows
/// controller → service → provider → response, and a failed turn is
/// distinguishable in the debug export from a request that never got an
/// answer.
///
/// Behavioural tests run real code. Source assertions cover wiring with no
/// test harness of its own — they prove the call exists, not that it runs,
/// and are labelled as such.
/// </summary>
public class ErrorContractAndRequestIdEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string ChatService() => Source("Services", "Gluno", "GlunoChatService.cs");

    /// The body of one method, sliced between its declaration and the next
    /// method declaration, so an assertion about one path cannot accidentally
    /// pass on code from another.
    private static string MethodOf(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {declaration}");

        var end = source.IndexOf("\n    private ", start + declaration.Length, StringComparison.Ordinal);
        var endPublic = source.IndexOf("\n    public ", start + declaration.Length, StringComparison.Ordinal);
        if (endPublic >= 0 && (end < 0 || endPublic < end)) end = endPublic;

        return end > start ? source[start..end] : source[start..];
    }

    private static JsonElement AsJson(object body)
        => JsonSerializer.SerializeToElement(body);

    // ── 1–2. Terra contract and JSON failures become the structured error ──

    [Fact]
    public void A_terra_contract_change_is_a_failed_status_never_an_exception()
    {
        // Valid JSON, wrong envelope — what a renamed Terra field looks like.
        using var document = JsonDocument.Parse("""{"results": []}""");

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "sv");

        Assert.False(parsed.EnvelopeFound);

        // SOURCE ASSERTION: the provider turns that into Failed — the status
        // the chat service maps to the structured tripadvisor_unavailable
        // error — rather than letting anything escape.
        var provider = Source("Services", "Gluno", "TerraTravelProvider.cs");
        var search = MethodOf(provider, "public async Task<TravelSearchResult> SearchPlacesWithStatusAsync(");
        Assert.Contains("if (!parsed.EnvelopeFound)", search);
        Assert.Contains("TerraFailure.ProviderContractChanged", search);
        Assert.Contains("return Empty(TravelSearchStatus.Failed);", search);
    }

    [Fact]
    public void A_terra_json_failure_is_caught_and_classified_never_thrown()
    {
        var provider = Source("Services", "Gluno", "TerraTravelProvider.cs");

        // SOURCE ASSERTIONS. The transport layer catches JsonException and
        // answers with a failure category; the parse call is wrapped so a
        // deserialisation defect becomes MappingFailed + Failed.
        var send = MethodOf(provider, "private async Task<(JsonDocument? Document, TerraFailure Failure)> SendAsync(");
        Assert.Contains("catch (JsonException)", send);
        Assert.Contains("TerraFailure.DeserializationFailed", send);

        var search = MethodOf(provider, "public async Task<TravelSearchResult> SearchPlacesWithStatusAsync(");
        Assert.Contains("catch (Exception ex) when (ex is JsonException or InvalidOperationException)", search);
        Assert.Contains("TerraFailure.MappingFailed", search);
    }

    // ── 3. Every-result-discarded does not throw ──────────────────────────

    [Fact]
    public void Raw_results_with_zero_mapped_parse_without_throwing()
    {
        // One search result that carries no location object at all — the
        // shape that must be DISCARDED, not thrown on.
        using var document = JsonDocument.Parse("""
            {"search_results": [ {"type": "experience", "experience": {"id": 1}} ]}
            """);

        var parsed = TerraTravelProvider.ParseSearchResponse(document.RootElement, "sv");

        Assert.True(parsed.EnvelopeFound);
        Assert.Equal(1, parsed.RawCount);
        Assert.Equal(0, parsed.MappedCount);
        Assert.Equal(1, parsed.DiscardedCount);
    }

    [Fact]
    public void The_direct_search_flow_maps_every_exception_to_the_place_error()
    {
        // SOURCE ASSERTION: the wrapper around the direct-search core catches
        // everything short of OOM and answers with the structured place error
        // AND the branch that failed — ranking, sanitisation, retention and
        // persistence defects can no longer escape to a bare 500.
        var wrapper = MethodOf(ChatService(), "private async Task<GlunoTurnResult> RunDirectPlaceSearchAsync(");

        Assert.Contains("catch (OperationCanceledException) when (ct.IsCancellationRequested)", wrapper);
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException)", wrapper);
        Assert.Contains("FailureCode = GlunoFailureCodes.TripadvisorUnavailable", wrapper);
        Assert.Contains("ResponseOrigin = origin", wrapper);
        // Type name only — never the exception message, which can carry URIs.
        Assert.Contains("ex.GetType().Name", wrapper);
        Assert.DoesNotContain("ex.Message", wrapper);
    }

    [Fact]
    public void A_provider_failure_return_names_its_branch()
    {
        // The failure body can only carry responseOrigin if the service sets
        // it on the failed result — pinned so the app's export can name the
        // branch for provider failures too.
        var core = MethodOf(ChatService(), "private async Task<GlunoTurnResult> RunDirectPlaceSearchCoreAsync(");
        var failure = core.IndexOf("Error = GlunoTurnError.ProviderFailed", StringComparison.Ordinal);

        Assert.True(failure >= 0);
        Assert.Contains("ResponseOrigin = origin", core[failure..(failure + 400)]);
    }

    // ── 4. The controller and the middleware answer with the one contract ──

    [Fact]
    public void The_error_body_carries_code_message_retryable_origin_and_request_id()
    {
        var body = AsJson(GlunoErrors.Body(
            "tripadvisor_unavailable", retryable: true,
            responseOrigin: "direct_place_search", requestId: "abc123def456"));

        Assert.Equal("tripadvisor_unavailable", body.GetProperty("code").GetString());
        Assert.Equal("tripadvisor_unavailable", body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.True(body.GetProperty("retryable").GetBoolean());
        Assert.Equal("direct_place_search", body.GetProperty("responseOrigin").GetString());
        Assert.Equal("abc123def456", body.GetProperty("requestId").GetString());
    }

    [Fact]
    public void The_controllers_last_resort_uses_the_closed_code_list()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var send = MethodOf(controller, "public async Task<ActionResult<GlunoTurnResponseDto>> SendMessage(");

        // THE PROVEN GAP. This catch used to answer with the ad-hoc code
        // "unknown", which no app build has copy for — so a boundary failure
        // rendered as the generic line even WITH a JSON body.
        Assert.DoesNotContain("\"unknown\"", send);
        Assert.Contains("GlunoErrors.Body(", send);
        Assert.Contains("GlunoFailureCodes.AiMalformedResponse", send);
    }

    [Fact]
    public void Turn_failures_carry_origin_and_request_id()
    {
        var controller = Source("Controllers", "GlunoController.cs");
        var failure = MethodOf(controller, "private ObjectResult TurnFailure(GlunoTurnResult result)");

        Assert.Contains("GlunoErrors.Body(code, retryable, result.ResponseOrigin, _diagnostics.RequestId)", failure);
        Assert.Contains("GlunoErrors.StatusFor(code)", failure);
    }

    [Fact]
    public void A_gluno_request_cannot_escape_without_the_envelope()
    {
        var program = Source("Program.cs");

        // SOURCE ASSERTIONS on the middleware: scoped to /api/gluno, stamps
        // the request id on every response, writes the envelope for any
        // exception that reaches it, and logs the type name only.
        Assert.Contains("ctx.Request.Path.StartsWithSegments(\"/api/gluno\")", program);
        Assert.Contains("ctx.Response.Headers[\"X-Gluno-Request-Id\"]", program);
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException)", program);
        Assert.Contains("GlunoErrors.Body(", program);
        Assert.Contains("GlunoFailureCodes.AiMalformedResponse", program);
        Assert.Contains("[GLUNO] request escaped type={Category} requestId={RequestId}", program);
        // The one summary line per request, whatever the outcome.
        Assert.Contains("glunoDiagnostics.WriteSummary(app.Logger, ctx.Response.StatusCode)", program);
    }

    // ── 5–6. The mobile parser reads the envelope before giving up ────────

    [Fact]
    public void The_app_reads_the_json_body_even_when_the_response_is_not_ok()
    {
        var gluno = Mobile("lib", "gluno.ts");

        var notOk = gluno.IndexOf("if (!response.ok) {", StringComparison.Ordinal);
        Assert.True(notOk >= 0);

        var block = gluno[notOk..];
        var readsBody = block.IndexOf("await response.json()", StringComparison.Ordinal);
        var throws = block.IndexOf("throw error;", StringComparison.Ordinal);

        // The order that decides everything: body first, throw after. A
        // `throw` before the read is exactly the anti-pattern that swallows a
        // structured error into the generic line.
        Assert.True(readsBody >= 0);
        Assert.True(throws >= 0);
        Assert.True(readsBody < throws, "the error body must be read BEFORE the error is thrown");

        // And the read extracts the contract, not just the status.
        Assert.Contains("if (typeof body?.code === 'string') error.code = body.code;", block);
        Assert.Contains("if (typeof body?.retryable === 'boolean') error.retryable = body.retryable;", block);
        Assert.Contains("error.responseOrigin = body.responseOrigin", block);
    }

    [Fact]
    public void Tripadvisor_unavailable_has_its_own_sentence_in_both_languages()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("tripadvisor_unavailable: 'gluno.error.placesUnavailable'", row);

        var i18n = Mobile("components", "i18n-provider.tsx");
        Assert.Contains("'gluno.error.placesUnavailable': 'Jag kunde inte hämta verifierade platsförslag just nu.'", i18n);
        Assert.Contains("'gluno.error.placesUnavailable': 'I couldn", i18n);
    }

    // ── 7–8. Named codes never render generically; the generic line is only
    //         for a response with no contract at all ───────────────────────

    [Fact]
    public void Every_backend_failure_code_has_mobile_copy()
    {
        // The closed list, read from the backend source so a code added there
        // cannot silently render as the generic line in the app.
        var backendCodes = Regex.Matches(
                Source("Services", "Gluno", "GlunoFailure.cs"),
                "public const string \\w+ = \"([a-z_]+)\";")
            .Select(match => match.Groups[1].Value)
            // Cancellation never renders as a failure — the app shows nothing.
            .Where(code => code != "cancelled")
            .ToList();

        Assert.NotEmpty(backendCodes);

        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        foreach (var code in backendCodes)
        {
            Assert.True(
                copy.Contains($"{code}:", StringComparison.Ordinal),
                $"backend failure code '{code}' has no FAILURE_COPY entry — it would render as the generic line");
        }
    }

    [Fact]
    public void The_turn_endpoints_own_codes_have_mobile_copy_too()
    {
        // The codes the send endpoint's switch sends outside GlunoFailureCodes.
        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        foreach (var code in new[]
        {
            "gluno_unavailable", "empty_message", "conversation_not_found",
            "conversation_archived", "duplicate_in_flight",
        })
        {
            Assert.Contains($"{code}:", copy);
        }
    }

    [Fact]
    public void The_generic_line_is_reserved_for_contractless_responses()
    {
        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // request_failed is the app's own stamp for "answered without the
        // envelope" — by DESIGN it has no specific copy and falls to the
        // generic line, and nothing else should.
        Assert.DoesNotContain("request_failed:", copy);

        // A dead radio has its own sentence — the generic line must not
        // absorb it.
        Assert.Contains("network_error: 'gluno.error.network'", copy);

        // And the stamp itself exists only where no code could be read.
        var gluno = Mobile("lib", "gluno.ts");
        Assert.Contains("if (error.code === undefined) error.code = 'request_failed';", gluno);
    }

    // ── 9–10. The request id follows the flow, and is logged safely ───────

    [Fact]
    public void The_request_id_follows_controller_service_and_provider()
    {
        // Controller: stamps the result facts the summary line reads.
        var controller = Source("Controllers", "GlunoController.cs");
        Assert.Contains("_diagnostics.ConversationId ??= result.Conversation?.Id;", controller);
        Assert.Contains("_diagnostics.ErrorCode ??= result.FailureCode;", controller);
        Assert.Contains("_diagnostics.Completed = result.Error == GlunoTurnError.None;", controller);

        // Service: the provider query carries the id, and the branch stamps
        // name which deterministic path claimed the turn.
        var service = ChatService();
        Assert.Contains("RequestId = _diagnostics.RequestId", service);
        Assert.Contains("_diagnostics.IntentBranch = \"direct_place_search\";", service);
        Assert.Contains("_diagnostics.IntentBranch = \"discovery_followup\";", service);
        Assert.Contains("_diagnostics.IntentBranch = \"destination_answer\";", service);
        Assert.Contains("_diagnostics.IntentBranch = \"model_turn\";", service);
        Assert.Contains("_diagnostics.ProviderStatus = result.Status.ToString();", service);

        // Provider: the id appears in all three structural lines.
        var provider = Source("Services", "Gluno", "TerraTravelProvider.cs");
        Assert.Contains("terra transport status={Status} contentType={ContentType} bodyLength={BodyLength} requestId={RequestId}", provider);
        Assert.Contains("discardReasons={Reasons} requestId={RequestId} in {Elapsed}ms", provider);
        Assert.Contains("status={Status} requestId={RequestId} in {Elapsed}ms", provider);

        // Response: the middleware stamps the header on every answer, and the
        // failure body carries the same id (pinned above in TurnFailure).
        Assert.Contains("X-Gluno-Request-Id", Source("Program.cs"));
    }

    [Fact]
    public void The_summary_line_is_fixed_metadata_and_nothing_else()
    {
        var diagnostics = Source("Services", "Gluno", "GlunoRequestDiagnostics.cs");

        foreach (var field in new[]
        {
            "requestId={RequestId}", "conversationId={ConversationId}",
            "scopeType={ScopeType}", "intentBranch={IntentBranch}",
            "responseOrigin={ResponseOrigin}", "httpStatus={HttpStatus}",
            "errorCode={ErrorCode}", "providerStatus={ProviderStatus}",
            "completed={Completed}", "in {Elapsed}ms",
        })
        {
            Assert.Contains(field, diagnostics);
        }

        // No free text: the type has no property that could carry the user's
        // message, a header or provider content.
        foreach (var forbidden in new[] { "Message", "Text", "Header", "Token", "Key", "Body", "Query" })
        {
            Assert.DoesNotContain($"public string {forbidden}", diagnostics);
        }
    }

    [Fact]
    public void The_dev_log_line_carries_the_seven_facts_and_no_body()
    {
        var gluno = Mobile("lib", "gluno.ts");

        // One line per request with exactly the diagnostic vocabulary asked
        // for — and built from typed fields, never from the response body.
        Assert.Contains("requestId=${error.requestId ?? '-'} endpoint=${path}", gluno);
        Assert.Contains("httpStatus=${error.status} parsed=${parsed} errorCode=${error.code}", gluno);
        Assert.Contains("responseOrigin=${error.responseOrigin ?? '-'} fallbackUsed=${fallbackUsed}", gluno);

        // The network arm says outright that nothing exists to correlate.
        Assert.Contains("requestId=- endpoint=${path} httpStatus=- parsed=false", gluno);

        var devBlocks = Regex.Matches(gluno, "console\\.log\\(\\s*(`\\[GLUNO\\] request[^;]*);");
        Assert.True(devBlocks.Count >= 3, "expected the three request log call sites");

        foreach (Match block in devBlocks)
        {
            Assert.DoesNotContain("body", block.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", block.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("message.text", block.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 11. A failed turn is present and distinguishable in the export ────

    [Fact]
    public void A_failed_live_turn_exports_its_code_origin_and_request_id()
    {
        var screen = Mobile("app", "gluno.tsx");

        // The failure handler keeps the join key, the failing branch and the
        // transport fact — a response DID arrive — on the failed row.
        Assert.Contains("requestId: failure.requestId,", screen);
        Assert.Contains("responseOrigin: failure.responseOrigin,", screen);
        Assert.Contains("live: failure.status !== undefined,", screen);

        var export = Mobile("lib", "gluno-debug-export.ts");

        // The export prints them — and prints the ABSENCE of an origin
        // explicitly for failed rows, which is what separates a provider
        // failure from a request that never produced a response.
        Assert.Contains("pairs.push(`requestId=${message.requestId}`)", export);
        Assert.Contains("responseOrigin=-", export);
        Assert.Contains("if (message.live) return 'live_response';", export);
    }

    // ── 12. Nothing secret in any of the new lines ────────────────────────

    [Fact]
    public void No_new_log_line_carries_secrets_or_user_text()
    {
        // Every log format string added for this round, verbatim.
        var newLines = new[]
        {
            "[GLUNO] request done requestId={RequestId} conversationId={ConversationId} ",
            "[GLUNO] request escaped type={Category} requestId={RequestId}",
            "[GLUNO] escaped type={Category} stage={Stage} requestId={RequestId}",
            "[GLUNO] direct place search escaped type={Category} requestId={RequestId}",
            "[GLUNO] direct place search failed status={Status} category={Category} requestId={RequestId}",
        };

        var sources = string.Join('\n',
            Source("Program.cs"),
            ChatService(),
            Source("Services", "Gluno", "GlunoRequestDiagnostics.cs"));

        foreach (var line in newLines)
        {
            Assert.Contains(line, sources);
        }

        // The placeholders across ALL new lines are drawn from this fixed
        // vocabulary — no {Message}, no {Query}, no {Header}, no {Url}.
        var allowed = new HashSet<string>
        {
            "RequestId", "ConversationId", "ScopeType", "IntentBranch",
            "ResponseOrigin", "HttpStatus", "ErrorCode", "ProviderStatus",
            "Completed", "Elapsed", "Category", "Stage", "Status",
        };

        foreach (var line in newLines)
        {
            foreach (Match placeholder in Regex.Matches(line, "\\{(\\w+)\\}"))
            {
                Assert.Contains(placeholder.Groups[1].Value, allowed);
            }
        }
    }

    // ── The status the failure travels under ──────────────────────────────

    [Fact]
    public void The_place_error_is_a_retryable_502()
    {
        Assert.Equal(502, GlunoErrors.StatusFor("tripadvisor_unavailable"));
        Assert.True(GlunoFailureCodes.IsRetryable("tripadvisor_unavailable"));

        // And the boundary code the middleware answers with is retryable too —
        // a serialization defect on one turn says nothing about the next.
        Assert.Equal(502, GlunoErrors.StatusFor(GlunoFailureCodes.AiMalformedResponse));
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiMalformedResponse));
    }
}
