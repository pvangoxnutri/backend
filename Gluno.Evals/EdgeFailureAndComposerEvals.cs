using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the two failures proven by the debug export of 2026-08-03,
/// second round.
///
/// FAILURE ONE. "Vad ska vi se i Linz" and "Ge mig nya förslag" (twice) died
/// as httpStatus=502, errorCode=request_failed, responseOrigin=-, no
/// requestId. The backend middleware stamps X-Gluno-Request-Id before
/// anything can fail, so a 502 WITHOUT the header did not come from our
/// process — it is the edge answering instead of the backend. What is pinned
/// here: those responses are named edge_502/503/504 rather than blending
/// into request_failed, a device-minted clientRequestId exists for every
/// request so even an edge failure can be correlated, the caller's abort
/// signal actually reaches the network layer, and a timeout, a network
/// failure, a user cancellation and an edge failure are four different facts
/// with four different names.
///
/// FAILURE TWO. After a failed send the composer was refilled with the same
/// text the user had just sent — while the failed row already carried it and
/// the retry button already resent from that row. The refill is gone; the
/// composer stays empty whatever the request does.
///
/// Source assertions prove the wiring exists, not that it runs, and are
/// labelled as such.
/// </summary>
public class EdgeFailureAndComposerEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    /// One function of a mobile source file, sliced from its declaration to
    /// the next top-level declaration, so an assertion about one path cannot
    /// pass on code from another.
    private static string MobileFunction(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration not found: {declaration}");

        var next = source.IndexOf("\n  async function ", start + declaration.Length, StringComparison.Ordinal);
        var nextConst = source.IndexOf("\n  const ", start + declaration.Length, StringComparison.Ordinal);
        if (nextConst >= 0 && (next < 0 || nextConst < next)) next = nextConst;

        return next > start ? source[start..next] : source[start..];
    }

    // ── 1–3. Contractless proxy answers get their own names ───────────────

    [Fact]
    public void Contractless_502_503_and_504_are_named_edge_failures()
    {
        var gluno = Mobile("lib", "gluno.ts");

        // The classification only runs when NO code could be read from the
        // body — a backend envelope always wins over the status number.
        Assert.Contains("const EDGE_STATUSES = new Set([502, 503, 504]);", gluno);
        Assert.Contains("error.code = EDGE_STATUSES.has(response.status)", gluno);
        Assert.Contains("? `edge_${response.status}`", gluno);
        Assert.Contains(": 'request_failed';", gluno);

        // And each name has its own sentence — the connection line, not a
        // sentence that blames Gluno for an answer it never gave.
        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("edge_502: 'gluno.error.connectionLost'", copy);
        Assert.Contains("edge_503: 'gluno.error.connectionLost'", copy);
        Assert.Contains("edge_504: 'gluno.error.connectionLost'", copy);

        var i18n = Mobile("components", "i18n-provider.tsx");
        Assert.Contains("'gluno.error.connectionLost': 'Anslutningen till Gluno bröts. Försök igen.'", i18n);
        Assert.Contains("'gluno.error.connectionLost': 'The connection to Gluno was interrupted. Try again.'", i18n);
    }

    // ── 4–6. The device's own id exists before anything can fail ──────────

    [Fact]
    public void The_client_request_id_is_minted_before_the_fetch()
    {
        var gluno = Mobile("lib", "gluno.ts");

        var minted = gluno.IndexOf("const clientRequestId = createGlunoClientRequestId();", StringComparison.Ordinal);
        var fetched = gluno.IndexOf("await apiFetch(path, { ...options, headers }", StringComparison.Ordinal);

        Assert.True(minted >= 0);
        Assert.True(fetched >= 0);
        Assert.True(minted < fetched, "the client id must exist before the request leaves the device");
    }

    [Fact]
    public void The_client_request_id_travels_as_a_header()
    {
        var gluno = Mobile("lib", "gluno.ts");
        Assert.Contains("headers.set('X-Gluno-Client-Request-Id', clientRequestId);", gluno);
    }

    [Fact]
    public void The_backend_validates_echoes_and_logs_the_client_id()
    {
        var program = Source("Program.cs");

        // Validated before it is used for ANYTHING — an id that fails the
        // shape check is ignored, never sanitised, never echoed.
        Assert.Contains("GlunoRequestDiagnostics.IsValidClientRequestId(clientRequestId)", program);
        Assert.Contains("ctx.Response.Headers[\"X-Gluno-Client-Request-Id\"] = clientRequestId;", program);

        var diagnostics = Source("Services", "Gluno", "GlunoRequestDiagnostics.cs");

        // The validator is a closed shape: bounded length, id characters only.
        Assert.Contains("candidate.Length is >= 4 and <= 64", diagnostics);
        Assert.Contains("char.IsAsciiLetterOrDigit(c) || c is '-' or '_'", diagnostics);

        // And it lands in the one summary line, beside the server's own id.
        Assert.Contains("clientRequestId={ClientRequestId}", diagnostics);
    }

    // ── 7–9. The export tells the failure modes apart ─────────────────────

    [Fact]
    public void The_export_carries_the_client_id_and_prints_a_missing_server_id()
    {
        var export = Mobile("lib", "gluno-debug-export.ts");

        Assert.Contains("pairs.push(`clientRequestId=${message.clientRequestId}`)", export);
        // requestId=- on a failed row beside a clientRequestId IS the edge
        // signature — an answer that never came from our backend.
        Assert.Contains("else if (message.failed) pairs.push('requestId=-');", export);
    }

    [Fact]
    public void The_export_never_reads_a_response_body()
    {
        var export = Mobile("lib", "gluno-debug-export.ts");

        // Structure of the failure only: media type and declared length from
        // headers. No field on the message type carries a body, and the
        // export enumerates fields explicitly.
        Assert.Contains("contentType=${message.errorContentType}", export);
        Assert.Contains("bodyLength=${message.errorBodyLength}", export);
        Assert.DoesNotContain("responseBody", export);
        Assert.DoesNotContain("rawBody", export);

        var cache = Mobile("lib", "gluno-cache.ts");
        Assert.Contains("errorBodyLength?: number;", cache);
        Assert.DoesNotContain("errorBody?:", cache);
        Assert.DoesNotContain("responseBody", cache);
    }

    // ── 10–13. Signals: own controller, honoured aborts, named timeouts ───

    [Fact]
    public void Every_request_gets_its_own_abort_controller_and_the_callers_signal_is_honoured()
    {
        var api = Mobile("lib", "api.ts");

        // One controller per apiFetch call…
        Assert.Contains("const controller = new AbortController();", api);
        // …and the caller's signal is LINKED to it rather than silently
        // replaced — the bug that made Stop and scope switches no-ops at the
        // network layer.
        Assert.Contains("if (callerSignal?.aborted) {", api);
        Assert.Contains("callerSignal?.addEventListener('abort', () => controller.abort(), { once: true });", api);
    }

    [Fact]
    public void A_retry_never_reuses_an_aborted_signal()
    {
        var screen = Mobile("app", "gluno.tsx");
        var deliver = MobileFunction(screen, "async function deliver(");

        // deliver() mints a fresh controller per attempt, and retry goes
        // through deliver — so a retried turn can never inherit the aborted
        // signal of the attempt it replaces.
        Assert.Contains("const controller = new AbortController();", deliver);
        Assert.Contains("abortRef.current = controller;", deliver);

        var retry = MobileFunction(screen, "async function handleRetry(");
        Assert.Contains("await deliver(message.text, message.id, key);", retry);
        Assert.DoesNotContain("new AbortController", retry);
    }

    [Fact]
    public void A_deliberate_abort_is_a_cancellation_never_a_server_failure()
    {
        var screen = Mobile("app", "gluno.tsx");

        // Both deliberate aborts — Stop and a scope switch — go through the
        // same signal…
        Assert.Contains("abortRef.current?.abort();", screen);

        // …and the failure handler treats an abort as a cancellation: the
        // row is unmarked, no red bubble, no failure code.
        var deliver = MobileFunction(screen, "async function deliver(");
        Assert.Contains("if (isGlunoCancellation(error))", deliver);
        Assert.Contains("pending: false, failed: false", deliver);

        // The transport layer rethrows the abort untouched instead of
        // stamping a failure code on it.
        var gluno = Mobile("lib", "gluno.ts");
        Assert.Contains("throw transport;   // The user pressed Stop, or the screen left. Not a failure.", gluno);
    }

    [Fact]
    public void A_timeout_is_named_apart_from_a_dead_radio_and_an_edge_502()
    {
        var api = Mobile("lib", "api.ts");

        // The API layer marks its own deadline firing…
        Assert.Contains("timeoutError.timedOut = true;", api);
        // …and never reports a caller's abort as a timeout.
        Assert.Contains("if (callerSignal?.aborted) {", api);

        // The Gluno layer turns the mark into its own code, distinct from
        // network_error — and both are transport facts, distinct from the
        // edge statuses which are HTTP responses.
        var gluno = Mobile("lib", "gluno.ts");
        Assert.Contains("? 'request_timeout' : 'network_error'", gluno);

        var copy = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("request_timeout: 'gluno.error.timeout'", copy);
    }

    // ── 14–16. The composer stays empty ───────────────────────────────────

    [Fact]
    public void Sending_clears_the_composer()
    {
        var send = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleSend(");
        Assert.Contains("setDraft('');", send);
    }

    [Fact]
    public void The_composer_stays_empty_when_the_request_fails()
    {
        var screen = Mobile("app", "gluno.tsx");
        var send = MobileFunction(screen, "async function handleSend(");

        // THE PROVEN BUG. handleSend restored the draft when deliver()
        // returned false, handing the user their own sentence back beside a
        // retry button that never needed it. One setDraft — the clear — and
        // no path that writes the text back.
        var clears = send.Split("setDraft(").Length - 1;
        Assert.Equal(1, clears);
        Assert.DoesNotContain("setDraft((current)", send);
        Assert.DoesNotContain("? text : current", send);
    }

    [Fact]
    public void No_failure_path_writes_the_original_text_into_the_composer()
    {
        var screen = Mobile("app", "gluno.tsx");

        var deliver = MobileFunction(screen, "async function deliver(");
        Assert.DoesNotContain("setDraft", deliver);

        var retry = MobileFunction(screen, "async function handleRetry(");
        Assert.DoesNotContain("setDraft", retry);
    }

    // ── 17–21. Retry runs from the failed row's own state ─────────────────

    [Fact]
    public void Retry_resends_the_failed_rows_own_text_with_the_original_idempotency_key()
    {
        var retry = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleRetry(");

        // The row's text, the row's id, the SAME idempotency key as the
        // original attempt — never a second answer for one question.
        Assert.Contains("idempotencyKeysRef.current.get(message.id) ?? createGlunoIdempotencyKey();", retry);
        Assert.Contains("await deliver(message.text, message.id, key);", retry);
    }

    [Fact]
    public void Retry_does_not_read_the_composer()
    {
        var retry = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleRetry(");
        Assert.DoesNotContain("draft", retry);
    }

    [Fact]
    public void Retry_mutates_the_existing_row_instead_of_appending_a_second_one()
    {
        var retry = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleRetry(");

        Assert.Contains("current.map((entry) =>", retry);
        Assert.DoesNotContain("appendAndFollow", retry);
        Assert.DoesNotContain("...current,", retry);
    }

    [Fact]
    public void Retry_does_not_focus_the_composer_or_open_the_keyboard()
    {
        var retry = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleRetry(");
        Assert.DoesNotContain("inputRef", retry);
        Assert.DoesNotContain(".focus(", retry);
    }

    [Fact]
    public void Double_tapping_retry_is_blocked_on_both_sides()
    {
        var retry = MobileFunction(Mobile("app", "gluno.tsx"), "async function handleRetry(");
        Assert.Contains("if (sending) return;", retry);

        // The button itself latches while its own retry is in flight.
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        Assert.Contains("if (retrying) return;", row);
        Assert.Contains("disabled={retrying}", row);
    }

    // ── The failed row carries what the retry and the export need ─────────

    [Fact]
    public void The_failed_row_keeps_its_diagnostics()
    {
        var screen = Mobile("app", "gluno.tsx");

        foreach (var kept in new[]
        {
            "requestId: failure.requestId,",
            "responseOrigin: failure.responseOrigin,",
            "clientRequestId: failure.clientRequestId,",
            "errorContentType: failure.contentType,",
            "errorBodyLength: failure.bodyLength,",
            "live: failure.status !== undefined,",
        })
        {
            Assert.Contains(kept, screen);
        }
    }

    [Fact]
    public void The_client_id_shape_is_what_the_backend_accepts()
    {
        // The generator's output must pass the backend's validator — pinned
        // from both sides so neither can drift alone.
        var gluno = Mobile("lib", "gluno.ts");
        Assert.Contains("return `c${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;", gluno);

        Assert.True(sidequest.backend.Services.Gluno.GlunoRequestDiagnostics
            .IsValidClientRequestId("clkj2x8f9a1b2c3d"));
        Assert.False(sidequest.backend.Services.Gluno.GlunoRequestDiagnostics
            .IsValidClientRequestId("has space"));
        Assert.False(sidequest.backend.Services.Gluno.GlunoRequestDiagnostics
            .IsValidClientRequestId("x"));
        Assert.False(sidequest.backend.Services.Gluno.GlunoRequestDiagnostics
            .IsValidClientRequestId(new string('a', 65)));
        Assert.False(sidequest.backend.Services.Gluno.GlunoRequestDiagnostics
            .IsValidClientRequestId("semi;colon"));
    }
}
