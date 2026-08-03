using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the one invariant that survived three attempts to fix it.
///
/// THE PRODUCTION FAILURE, THIRD REPORT. "Lägg till Casas de Pilatos" came back
/// as a misspelled apology asking the user to type the same sentence again. The
/// sentence exists nowhere in either repository, so the model wrote it — which
/// means an add request reached the model.
///
/// THE PROVEN CAUSE, found by running the real router rather than reasoning
/// about it: the add branch also required GlunoIntentRouter to agree the intent
/// was PlaceRecommendation or AddActivity. The router scores on CATEGORY WORDS
/// — "restaurang", "museum", "sevärdhet" — and a place named outright contains
/// none of them. With no trip scope, "Lägg till Casas de Pilatos" classifies as
/// Unclear. So the gate failed and the turn fell through.
///
/// A third condition that can fail independently is a third route to the model.
/// The gate is gone; the two deterministic text signals decide.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class AddIntentInvariantEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static GlunoIntentResult Intent(string message, bool hasTrip) =>
        GlunoIntentRouter.Classify(new GlunoIntentInput { Message = message, HasTrip = hasTrip });

    // ── The cause, pinned ────────────────────────────────────────────────

    [Theory]
    [InlineData("Lägg till Casas de Pilatos")]
    [InlineData("Lägg till Real Alcázar")]
    [InlineData("Add Casas de Pilatos")]
    public void The_router_cannot_recognise_a_place_named_outright(string message)
    {
        // THE EVIDENCE. Not an assumption about the router — the router itself.
        // A proper noun carries no category word, so nothing scores and the
        // fallback is Unclear.
        Assert.Equal(GlunoIntent.Unclear, Intent(message, hasTrip: false).PrimaryIntent);

        // While both deterministic signals are certain about it. That gap is
        // exactly where the turn escaped to the model.
        Assert.True(GlunoPlaceOptions.IsAddRequest(message));
        Assert.True(GlunoPlaceOptions.PointsAtSomethingShown(message));
    }

    [Fact]
    public void The_add_branch_no_longer_asks_the_router()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // The gate that was the bug.
        Assert.DoesNotContain("LooksLikePlaceAdd(intent, text)", chat);
        Assert.Contains("private static bool LooksLikePlaceAdd(string text)", chat);
        Assert.Contains("if (LooksLikePlaceAdd(text))", chat);
    }

    [Fact]
    public void An_add_request_that_points_at_a_place_can_never_reach_the_model()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        var start = chat.IndexOf("if (GlunoPlaceOptions.IsAddRequest(text))", StringComparison.Ordinal);
        // The whole add branch, up to the block that follows it — a fixed
        // char count went stale the first time the branch grew.
        var end = chat.IndexOf("A pure place question", start, StringComparison.Ordinal);
        var branch = chat[start..end];

        Assert.True(start > 0 && end > start);

        // Every exit from the branch is a return. Nothing falls past it into
        // the model when both signals hold.
        Assert.Contains("if (added != null) return added;", branch);
        // A name with no cards behind it is recovered by SEARCHING the
        // provider — deterministically — before the ask-which fallback.
        Assert.Contains("RecoverNamedPlaceAsync(", branch);
        Assert.Contains("return await AskWhichPlaceToAddAsync(conversation, userId, text, ct);", branch);
        Assert.DoesNotContain("_ai.", branch);
        Assert.DoesNotContain("RunTurnAsync", branch);
    }

    // ── Precision: the fix must not hijack planning requests ─────────────

    [Theory]
    // Places, named or numbered.
    [InlineData("Lägg till Casas de Pilatos", true)]
    [InlineData("Lägg till Real Alcázar", true)]
    [InlineData("Lägg till den första", true)]
    [InlineData("add the second one", true)]
    // Itinerary edits. A false positive here answers a planning request with
    // "which place did you mean?", which is why the length test was replaced.
    [InlineData("Lägg till en vilodag", false)]
    [InlineData("Lägg till en anteckning om färjan", false)]
    [InlineData("Add a note about the ferry", false)]
    [InlineData("Boka in middag på torsdag", false)]
    [InlineData("lägg till en timme", false)]
    public void Only_a_named_or_numbered_thing_counts(string message, bool expected)
    {
        Assert.Equal(expected, GlunoPlaceOptions.PointsAtSomethingShown(message));
    }

    [Fact]
    public void The_discriminator_is_a_proper_noun_not_a_long_word()
    {
        var options = Source("Services", "Gluno", "GlunoPlaceOptions.cs");

        // The length test matched "about", "middag" and "anteckning" — every
        // planning request has a long word in it somewhere.
        Assert.DoesNotContain("word.Length >= 5", options);
        Assert.Contains("char.IsUpper(word[0])", options);
        Assert.Contains(".Skip(1)", options);
    }

    // ── The sentence itself ──────────────────────────────────────────────

    [Fact]
    public void The_production_sentence_exists_nowhere_in_the_backend()
    {
        // Proof it is model-generated rather than a stored fallback: if any of
        // these were in the source, the fix would be to delete them instead.
        var root = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Gluno");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var phrase in new[]
            {
                "Just den här stunden", "kan jag inte förbereda", "så gör jag ett förslag",
            })
            {
                Assert.DoesNotContain(phrase, text);
            }
        }
    }

    // ── responseOrigin ───────────────────────────────────────────────────

    [Fact]
    public void Every_turn_says_which_path_produced_it()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        foreach (var origin in new[]
        {
            "GlunoResponseOrigins.ModelTurn", "GlunoResponseOrigins.PlaceAdd",
            "GlunoResponseOrigins.PlaceRefresh", "GlunoResponseOrigins.Proposal",
            "GlunoResponseOrigins.IdempotencyReplay",
        })
        {
            Assert.Contains(origin, chat);
        }
    }

    [Fact]
    public void The_origin_is_a_fixed_vocabulary_and_nothing_else()
    {
        var source = Source("Services", "Gluno", "GlunoResponseOrigin.cs");

        // A branch name, not a result. No text, no ids, no provider data.
        Assert.DoesNotContain("GlunoPlaceCard", source);
        Assert.DoesNotContain("Guid", source);

        // Eight original branches plus the three the production debug export
        // forced into existence: the model-free list, the Adventure question,
        // and the resumed pending action.
        Assert.Equal(11, GlunoResponseOrigins.All.Count);
        Assert.Contains("idempotency_replay", GlunoResponseOrigins.All);
        Assert.Contains("model_turn", GlunoResponseOrigins.All);
        Assert.Contains("direct_place_search", GlunoResponseOrigins.All);
        Assert.Contains("adventure_clarification", GlunoResponseOrigins.All);
        Assert.Contains("pending_action_resume", GlunoResponseOrigins.All);
    }

    [Fact]
    public void The_origin_is_logged_but_never_rendered()
    {
        var screen = Mobile("app", "gluno.tsx");
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");

        // Development console only, and behind __DEV__.
        Assert.Contains("turn.responseOrigin ?? 'none'", screen);

        // Nothing that draws a message can see it — which is what keeps it out
        // of the chat the way "HTTP: 502" was not.
        Assert.DoesNotContain("responseOrigin", row);
    }

    // ── Idempotency namespaces ───────────────────────────────────────────

    [Fact]
    public void A_place_add_key_can_never_collide_with_a_chat_turn_key()
    {
        var screen = Mobile("app", "gluno.tsx");
        var client = Mobile("lib", "gluno.ts");

        // A place add is prefixed and derived from ids the server minted; a
        // chat turn is random. Neither can produce the other's key, so an old
        // model answer cannot be replayed as the answer to an add.
        Assert.Contains("`place-${messageId}-${optionKey}`", screen);
        Assert.Contains("export function createGlunoIdempotencyKey()", client);
    }

    [Fact]
    public void A_replayed_answer_says_so()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // So the next report can tell an old answer from a new one without
        // guessing — the exact ambiguity that made this take three rounds.
        Assert.Equal(3, chat.Split("ResponseOrigin = GlunoResponseOrigins.IdempotencyReplay,").Length - 1);
    }
}
