using System.Net;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the request Terra actually accepts, and for a failure the app can
/// always render.
///
/// THE TWO BUGS THESE CLOSE, both found by reading the published schema against
/// the implementation rather than by testing the implementation against itself:
///
///  • `top_level_categories: ["ATTRACTION"]`. The documented values are
///    "Attraction", "Accommodation", "Experience" and "Eat &amp; Drink". Every
///    categorised search was a 400.
///
///  • The response was read from `data`. Terra returns `search_results`. So a
///    perfectly good 200 mapped to nothing, which looks exactly like a city
///    with no attractions in it.
///
/// The first version of these fixtures encoded the SAME guesses as the code, so
/// they passed while production failed. A fixture is only evidence when it
/// comes from the contract rather than from the caller.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class TerraContractAndFailureEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Terra() => Source("Services", "Gluno", "TerraTravelProvider.cs");

    // ── 1-2. The request ─────────────────────────────────────────────────

    [Fact]
    public void The_default_request_is_the_smallest_one_that_can_answer()
    {
        var terra = Terra();

        var start = terra.IndexOf("var request = new Dictionary<string, object?>", StringComparison.Ordinal);
        var body = terra[start..(start + 400)];

        Assert.True(start > 0);
        // query + geo + limit, unconditionally. Everything else is behind a
        // switch, because an optional field the server rejects costs the whole
        // answer while it only sharpens it.
        Assert.Contains("[\"query\"] = BuildQueryText(query),", body);
        Assert.Contains("[\"geo\"] = new { name = geoName },", body);
        Assert.Contains("[\"limit\"] =", body);
    }

    [Fact]
    public void The_optional_fields_are_off_unless_switched_on()
    {
        var terra = Terra();

        Assert.Contains("if (SendResponsePreference) request[\"response_preference\"] = \"quality\";", terra);
        Assert.Contains("if (SendCategoryFilter && ToTerraCategories(query.Category) is { } categories)", terra);

        // Both default to false, so a deploy cannot start sending a field
        // nobody has seen accepted.
        Assert.Contains("_config.GetValue(\"TripadvisorTerra:SendCategoryFilter\", false)", terra);
        Assert.Contains("_config.GetValue(\"TripadvisorTerra:SendResponsePreference\", false)", terra);
    }

    [Fact]
    public void The_category_values_are_the_documented_ones()
    {
        Assert.Equal(["Attraction"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Attraction));
        Assert.Equal(["Eat & Drink"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Restaurant));
        Assert.Equal(["Accommodation"], TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.Hotel));
        Assert.Null(TerraTravelProvider.ToTerraCategories(TravelPlaceCategory.General));

        // The shape the first build guessed. Checked against the CODE rather
        // than the file, so the note explaining the mistake can stay.
        var mapper = Terra();
        var start = mapper.IndexOf("public static string[]? ToTerraCategories", StringComparison.Ordinal);
        var body = mapper[start..(start + 400)];

        Assert.DoesNotContain("ATTRACTION", body);
        Assert.DoesNotContain("RESTAURANT", body);
    }

    [Fact]
    public void The_response_is_read_from_the_documented_envelope()
    {
        Assert.Equal("search_results", TerraTravelProvider.ResultsProperty);
        Assert.Contains("TryGetProperty(ResultsProperty, out var data)", Terra());
        // The envelope the first build assumed.
        Assert.DoesNotContain("TryGetProperty(\"data\"", Terra());
    }

    // ── 3-8. Classification ──────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TerraFailure.InvalidRequest)]
    [InlineData(HttpStatusCode.Unauthorized, TerraFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TerraFailure.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, TerraFailure.RateLimited)]
    [InlineData(HttpStatusCode.PaymentRequired, TerraFailure.QuotaExceeded)]
    [InlineData(HttpStatusCode.RequestTimeout, TerraFailure.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, TerraFailure.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, TerraFailure.Network)]
    [InlineData(HttpStatusCode.BadGateway, TerraFailure.Network)]
    public void Every_rejection_gets_its_own_category(HttpStatusCode status, TerraFailure expected)
    {
        Assert.Equal(expected, TerraTravelProvider.Classify(status));
    }

    [Fact]
    public void A_rejection_is_logged_with_the_field_it_named()
    {
        var terra = Terra();

        // THE DIAGNOSTIC THAT WOULD HAVE FOUND THE ENUM BUG IN ONE LINE. A 400
        // says which field it disliked; without reading it, "invalid request"
        // is all anyone gets.
        Assert.Contains("field_errors", terra);
        Assert.Contains("terra rejected request status={Status} type={Type} fields={Fields}", terra);

        var start = terra.IndexOf("private async Task LogProblemAsync", StringComparison.Ordinal);
        var body = terra[start..(start + 2200)];

        // `type` and `status` are a fixed vocabulary and a number. `message`
        // can quote the request back, and the request contains the search text.
        Assert.DoesNotContain("Text(problem.RootElement, \"message\")", body);
        Assert.DoesNotContain("ReadAsStringAsync", body);
    }

    [Fact]
    public void Only_a_rate_limit_is_reported_as_worth_retrying()
    {
        var terra = Terra();

        Assert.Contains("failure is TerraFailure.RateLimited or TerraFailure.QuotaExceeded", terra);
        Assert.Contains("? TravelSearchStatus.RateLimited", terra);
        Assert.Contains(": TravelSearchStatus.Failed", terra);
    }

    // ── 9. The failure envelope ──────────────────────────────────────────

    [Fact]
    public void Every_failure_body_carries_a_code_a_message_and_a_flag()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            GlunoErrors.Body("place_lookup_failed", true), GlunoJson.Options);

        foreach (var field in new[] { "code", "error", "message", "retryable" })
        {
            Assert.Contains($"\"{field}\"", json);
        }

        Assert.Contains("place_lookup_failed", json);
    }

    [Fact]
    public void The_controller_builds_every_failure_the_same_way()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        // THE BUG THIS CLOSES. A failure reached the app with no code and no
        // retry flag, and the chat rendered "code: missing, retry: missing".
        // One builder, and it cannot omit a field.
        Assert.DoesNotContain("new { error =", controller);
        Assert.DoesNotContain("retryable = false,", controller);
        Assert.DoesNotContain("retryable = result.IsRetryable,", controller);
        Assert.Contains("GlunoErrors.Body(", controller);
        // The same builder, now carrying the failing branch and the request id
        // so the app's debug export can join the failure to the backend log.
        Assert.Contains(
            "GlunoErrors.Body(code, retryable, result.ResponseOrigin, _diagnostics.RequestId)",
            controller);
    }

    // ── 10-13. What the app does with it ─────────────────────────────────

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    [Fact]
    public void The_app_never_prints_a_status_a_code_or_a_missing_field()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // The exact strings from the production screenshot.
        // Checked against what RENDERS, so the comments explaining the bug can
        // stay in the file.
        var start = row.IndexOf("<View style={styles.errorBlock}>", StringComparison.Ordinal);
        var block = row[start..(start + 1800)];

        Assert.True(start > 0);
        Assert.DoesNotContain("HTTP", block);
        Assert.DoesNotContain("missing", block);
        Assert.DoesNotContain("errorStatus", block);
        Assert.DoesNotContain("failureCode", block);
        // The debug row itself is gone from the file entirely.
        Assert.DoesNotContain("devDetail", row);
        Assert.DoesNotContain("HTTP:", row);
    }

    [Fact]
    public void An_unrecognised_body_becomes_a_code_the_app_has_copy_for()
    {
        var client = Mobile("lib", "gluno.ts");

        // A 502 from a proxy carries no JSON at all — it knows nothing about
        // this envelope. That is a failure like any other, not a response to
        // render field by field. The proxy statuses get their own edge_ codes
        // (with their own copy); everything else contractless stays
        // request_failed.
        Assert.Contains("error.code = EDGE_STATUSES.has(response.status)", client);
        Assert.Contains(": 'request_failed';", client);
        Assert.Contains(
            "if (error.retryable === undefined) error.retryable = response.status >= 500;", client);
        // Reads the current field and the earlier one.
        Assert.Contains("if (typeof body?.code === 'string') error.code = body.code;", client);
        Assert.Contains("else if (typeof body?.error === 'string') error.code = body.error;", client);
    }

    [Fact]
    public void The_generic_line_covers_a_code_this_build_has_never_seen()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        var translations = Mobile("components", "i18n-provider.tsx");

        Assert.Contains("t('gluno.error.generic')", row);
        Assert.Contains("'gluno.error.generic'", translations);
    }

    // ── 14-15. Layout ────────────────────────────────────────────────────

    [Fact]
    public void The_error_text_wraps_inside_the_bubble()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // THE BUG. The column around it is alignItems: 'flex-end', which sizes a
        // child to its content and pins it right — so a line wider than the
        // column overflowed LEFTWARDS, off the screen, and the first words of
        // every error were unreadable.
        Assert.Contains("alignSelf: 'stretch',", row);
        Assert.Contains("flexShrink: 1,", row);

        // And nothing that could put it back outside.
        Assert.DoesNotContain("position: 'absolute'", row);
        Assert.DoesNotContain("marginLeft: -", row);
        Assert.DoesNotContain("marginRight: -", row);
        Assert.DoesNotContain("left: -", row);
    }

    [Fact]
    public void The_retry_button_cannot_squeeze_the_text()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // A column, not a row: the sentence gets the full width and the button
        // sits under it.
        Assert.Contains("errorBlock:", row);
        Assert.Contains("retryButton:", row);

        // The block itself stacks. Its first line is a row (icon beside text),
        // which is fine — what matters is that the button is not in it.
        var start = row.IndexOf("errorBlock: {", StringComparison.Ordinal);
        var block = row[start..row.IndexOf("errorLine: {", StringComparison.Ordinal)];

        Assert.DoesNotContain("flexDirection", block);
        Assert.Contains("alignSelf: .stretch.,".Replace(".", "'"), block);
    }

    [Fact]
    public void The_reason_is_read_before_the_retry_button()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        var textAt = row.IndexOf("<Text style={styles.errorText}", StringComparison.Ordinal);
        var buttonAt = row.IndexOf("style={styles.retryButton}", StringComparison.Ordinal);

        Assert.True(textAt > 0 && buttonAt > textAt,
            "a screen reader must hear what went wrong before it is offered an action");
    }

    // ── 16-19. Retry ─────────────────────────────────────────────────────

    [Fact]
    public void Retry_reuses_the_row_and_the_key()
    {
        var screen = Mobile("app", "gluno.tsx");

        var start = screen.IndexOf("async function handleRetry", StringComparison.Ordinal);
        var body = screen[start..(start + 1400)];

        Assert.True(start > 0);
        // The existing row is mutated rather than a new one pushed, so a failed
        // turn cannot leave two copies of the same question behind.
        Assert.Contains("entry.id === message.id", body);
        // The SAME key. A new one would make the backend treat this as a fresh
        // question and answer it twice.
        Assert.Contains("idempotencyKeysRef.current.get(message.id)", body);
        Assert.Contains("await deliver(message.text, message.id, key);", body);
    }

    [Fact]
    public void A_second_tap_while_retrying_does_nothing()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        var screen = Mobile("app", "gluno.tsx");

        // Two guards, and the local one is the same state that draws the
        // spinner — so they cannot disagree about whether one is running.
        Assert.Contains("if (retrying) return;", row);
        Assert.Contains("disabled={retrying}", row);
        Assert.Contains("if (sending) return;", screen);
    }

    [Fact]
    public void The_spinner_sits_on_the_button()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        var start = row.IndexOf("style={styles.retryButton}", StringComparison.Ordinal);
        var body = row[start..(start + 600)];

        Assert.Contains("<ActivityIndicator", body);
        Assert.Contains("accessibilityState={{ disabled: retrying, busy: retrying }}", body);
    }

    // ── 21-23. One provider call ─────────────────────────────────────────

    [Fact]
    public void A_list_costs_exactly_one_upstream_call()
    {
        var terra = Terra();

        // One POST per search, and no per-row hydration: the recommendations
        // response already carries what a card shows.
        Assert.Equal(1, terra.Split("await SendAsync(").Length - 1);
        Assert.Contains("public Task<TravelPlace?> GetPlaceDetailsAsync(", terra);
        Assert.Contains("=> Task.FromResult<TravelPlace?>(null);", terra);
    }

    [Fact]
    public void Legacy_does_not_run_alongside_terra()
    {
        var registry = Source("Services", "Gluno", "TravelDataRegistry.cs");

        // Both products answer to "tripadvisor" and issue the same ids, so ONE
        // implementation per family is what stops two upstream calls and two
        // bills. The rule lives in SelectProviders — enabled owner by fixed
        // priority, fail closed on a broken owner — and both search paths go
        // through it, so neither can quietly start fanning out.
        foreach (var method in new[] { "> SearchPlacesAsync(", "> SearchAllAsync(" })
        {
            var start = registry.IndexOf(method, StringComparison.Ordinal);
            var body = registry[start..(start + 1200)];

            Assert.True(start > 0, method);
            Assert.Contains("var configured = SelectProviders();", body);
        }

        var rule = registry.IndexOf("private List<ITravelDataProvider> SelectProviders()", StringComparison.Ordinal);
        Assert.True(rule > 0);
        var ruleBody = registry[rule..(rule + 1600)];
        Assert.Contains(".GroupBy(provider => provider.Provider, StringComparer.Ordinal)", ruleBody);
        Assert.Contains(".OrderBy(provider => provider.SelectionPriority)", ruleBody);
    }

    [Fact]
    public void The_http_client_is_pooled_rather_than_built_per_turn()
    {
        var terra = Terra();
        var program = Source("Program.cs");

        Assert.Contains("_httpClientFactory.CreateClient(HttpClientName)", terra);
        Assert.DoesNotContain("new HttpClient(", terra);
        Assert.Contains(".AddHttpClient(TerraTravelProvider.HttpClientName)", program);
    }

    // ── 25. Timing ───────────────────────────────────────────────────────

    [Fact]
    public void The_turn_records_every_stage_it_passes()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        foreach (var stage in new[]
        {
            "turn_planned", "context_built", "reference_resolved", "evidence_built",
            "prompt_assembled", "user_turn_persisted", "model_request_started",
            "model_finished", "answer_persisted", "proposals_persisted",
        })
        {
            Assert.Contains($"latency.Reached(\"{stage}\")", chat);
        }

        // Tool time separately from model time. They interleave, and one
        // combined figure hides which of the two a slow turn was spent on.
        Assert.Contains("latency.Stage($\"tool_{call.Name}\")", chat);
    }

    [Fact]
    public void The_timing_line_carries_durations_and_nothing_else()
    {
        var telemetry = Source("Services", "Gluno", "GlunoTurnTelemetry.cs");

        Assert.Contains("stages={Stages}", telemetry);
        Assert.Contains("$\"{pair.Key}={pair.Value}\"", telemetry);
    }

    // ── 26-27. The waiting line ──────────────────────────────────────────

    [Fact]
    public void The_waiting_line_is_never_stored()
    {
        var screen = Mobile("app", "gluno.tsx");

        // Rendered as the list's header, not appended to the messages — so it
        // has no id, no row, and nothing to persist.
        Assert.Contains("sending && waitingVisible ? (", screen);
        Assert.Contains("ListHeaderComponent={", screen);
    }

    [Fact]
    public void The_waiting_line_waits_before_showing_and_clears_afterwards()
    {
        var screen = Mobile("app", "gluno.tsx");

        // A status that appears and vanishes inside a couple of frames reads as
        // a flicker, not as progress.
        Assert.Contains("setTimeout(() => setWaitingVisible(true), WAITING_VISIBLE_AFTER_MS)", screen);
        Assert.Contains("clearTimeout(waitTimer);", screen);
        Assert.Contains("setWaitingVisible(false);", screen);
    }

    [Fact]
    public void The_waiting_line_says_one_thing_and_names_nothing_technical()
    {
        var screen = Mobile("app", "gluno.tsx");
        var translations = Mobile("components", "i18n-provider.tsx");

        // One sentence for the whole wait. Rotating through several reads as a
        // progress bar that knows something it does not.
        Assert.Contains("t('gluno.state.thinkingAbout', { trip: tripTitle })", screen);
        Assert.Contains("'gluno.state.thinkingAbout': 'Gluno tänker på {trip}…',", translations);

        foreach (var leak in new[] { "Terra", "Tripadvisor", "provider", "HTTP" })
        {
            Assert.DoesNotContain($"gluno.state.thinking': '{leak}", translations);
        }
    }
}
