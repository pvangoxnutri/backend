using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the copyable debug transcript.
///
/// WHY IT EXISTS. The same production failure survived several rounds because
/// every investigation began by guessing which path produced a line. A
/// transcript the user can paste turns that into a reading.
///
/// WHAT IT IS ALLOWED TO CONTAIN is the user's own conversation — already on
/// their screen, and copied by their own deliberate action — plus the
/// structural facts about how each turn was produced: ids, codes, enums,
/// counts.
///
/// WHAT IT MUST NEVER CONTAIN is anything they cannot see and did not choose
/// to share. That is enforced by construction rather than by filtering: the
/// builder names every field it reads, so a field added to the message type
/// later cannot arrive in an export by default. The sanitiser is a second line
/// of defence for the one field that carries free text.
///
/// These read the mobile source directly — there is no test runner in that
/// project, and the builder is a pure function whose shape can be asserted.
/// </summary>
public class DebugExportEvals
{
    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string Builder() => Mobile("lib", "gluno-debug-export.ts");
    private static string Screen() => Mobile("app", "gluno.tsx");

    // ── 1. Development only ──────────────────────────────────────────────

    [Fact]
    public void The_button_exists_only_in_a_development_build()
    {
        var screen = Screen();

        var start = screen.IndexOf("t('gluno.debug.copy')", StringComparison.Ordinal);
        var before = screen[Math.Max(0, start - 900)..start];

        Assert.True(start > 0, "the copy button is missing");
        // Gated on __DEV__, so no store build renders it.
        Assert.Contains("{__DEV__ ? (", before);
    }

    [Fact]
    public void The_button_sits_after_the_scope_picker()
    {
        var screen = Screen();

        var pickerAt = screen.IndexOf("<GlunoScopePicker", StringComparison.Ordinal);
        var buttonAt = screen.IndexOf("t('gluno.debug.copy')", StringComparison.Ordinal);

        // So it cannot come between the pill and its sheet.
        Assert.True(pickerAt > 0 && buttonAt > pickerAt);
    }

    // ── 2-4. The transcript ──────────────────────────────────────────────

    [Fact]
    public void Messages_are_exported_in_chronological_order()
    {
        var builder = Builder();

        // Sorted by timestamp with a stable tiebreak — the same ordering the
        // list itself uses, so a pasted transcript matches the screen.
        Assert.Contains("a.createdAt.localeCompare(b.createdAt)", builder);
        Assert.Contains("a.id.localeCompare(b.id)", builder);
    }

    [Fact]
    public void Each_turn_is_labelled_with_who_said_it()
    {
        var builder = Builder();

        Assert.Contains("message.role === 'user' ? 'USER' : 'GLUNO'", builder);
        // With a clock time, so a report can be lined up against a log.
        Assert.Contains("function clockTime(", builder);
    }

    [Fact]
    public void The_visible_text_is_included()
    {
        var builder = Builder();

        // The user's own conversation, copied by their own action.
        Assert.Contains("sanitiseForExport(message.text ?? '')", builder);
    }

    // ── 5-8. The metadata ────────────────────────────────────────────────

    [Fact]
    public void Every_turn_carries_its_message_id()
    {
        Assert.Contains("`messageId=${message.id}`", Builder());
    }

    [Fact]
    public void The_response_origin_is_included_when_the_server_sent_one()
    {
        var builder = Builder();
        var screen = Screen();

        Assert.Contains("responseOrigin=${message.responseOrigin}", builder);
        // And it is kept on the row when a live turn arrives, so there is
        // something to export.
        Assert.Contains("last.responseOrigin = turn.responseOrigin;", screen);
    }

    [Fact]
    public void The_header_names_the_scope_the_conversation_and_the_trip()
    {
        var builder = Builder();

        foreach (var field in new[]
        {
            "GLUNO DEBUG EXPORT", "Scope: ", "ScopeKey: ",
            "ConversationId: ", "TripId: ", "ExportedAt: ",
        })
        {
            Assert.Contains(field, builder);
        }
    }

    [Fact]
    public void Actions_failures_and_card_counts_are_included()
    {
        var builder = Builder();

        foreach (var field in new[]
        {
            "errorCode=", "httpStatus=", "retryable=", "action=",
            "clarification=", "proposals=", "places=", "optionKeys=",
        })
        {
            Assert.Contains(field, builder);
        }

        // Where the row came from, as far as the app can tell.
        Assert.Contains("local_optimistic", builder);
        Assert.Contains("live_response", builder);
        Assert.Contains("history_or_cache", builder);
    }

    // ── 9-10. What must never be exported ────────────────────────────────

    [Fact]
    public void Nothing_secret_is_ever_read_in_the_first_place()
    {
        var builder = Builder();

        // Enforced by construction: every field is named, so a token, a header
        // or a coordinate cannot arrive by being added to the type later.
        foreach (var forbidden in new[]
        {
            "token", "Authorization", "headers", "cookie", "email",
            "latitude", "longitude", "imageUrl", "providerUrl",
        })
        {
            Assert.DoesNotContain($"message.{forbidden}", builder);
            Assert.DoesNotContain($"place.{forbidden}", builder);
        }

        // A proposal's payload is the plan itself — kind and status only.
        Assert.DoesNotContain("p.payload", builder);
        Assert.Contains("`${p.kind}:${p.status}`", builder);
    }

    [Fact]
    public void The_sanitiser_removes_signatures_tokens_and_long_blobs()
    {
        var builder = Builder();

        // Query strings first, so a signed URL loses its signature before the
        // URL itself is considered.
        Assert.Contains("[?#][^\\s]*", builder);
        Assert.Contains("bearer|authorization", builder);
        Assert.Contains("eyJ", builder);
        Assert.Contains("api[_-]?key|secret|token|password", builder);
        // Replaced with a marker rather than deleted, so a reader can see that
        // something was removed instead of reading a truncated sentence.
        Assert.Contains("[removed]", builder);
    }

    [Fact]
    public void A_runaway_body_is_capped()
    {
        var builder = Builder();

        Assert.Contains("const MAX_TEXT = 4000;", builder);
        Assert.Contains("[truncated]", builder);
    }

    // ── 11. Failure and double press ─────────────────────────────────────

    [Fact]
    public void A_clipboard_failure_says_something_neutral()
    {
        var screen = Screen();
        var translations = Mobile("components", "i18n-provider.tsx");

        Assert.Contains("t('gluno.debug.copyFailed')", screen);
        Assert.Contains("'gluno.debug.copyFailed': 'Kunde inte kopiera chatten',", translations);
        Assert.Contains("'gluno.debug.copied': 'Chatten kopierad',", translations);

        var start = screen.IndexOf("const handleCopyTranscript", StringComparison.Ordinal);
        var body = screen[start..(start + 1400)];

        // Never the clipboard's own error — it says nothing a person can act
        // on, and it is the kind of raw text this chat has had removed before.
        Assert.DoesNotContain("error.message", body);
        Assert.DoesNotContain("String(error)", body);
    }

    [Fact]
    public void A_second_press_while_copying_does_nothing()
    {
        var screen = Screen();

        Assert.Contains("if (copying) return;", screen);
        Assert.Contains("disabled={copying}", screen);
        // Cleared whatever happened, so a failure cannot leave the button dead.
        Assert.Contains("setCopying(false);", screen);
    }

    [Fact]
    public void The_builder_is_pure_and_testable_without_a_screen()
    {
        var builder = Builder();

        // No clipboard, no navigation, no state — which is what keeps the
        // decision about WHAT to include separate from when to offer it.
        Assert.DoesNotContain("Clipboard", builder);
        Assert.DoesNotContain("useState", builder);
        Assert.DoesNotContain("react-native", builder);
        // And the timestamp is injected, so the output is deterministic.
        Assert.Contains("now?: Date;", builder);
        Assert.Contains("(input.now ?? new Date())", builder);
    }
}
