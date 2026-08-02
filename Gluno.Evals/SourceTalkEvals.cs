using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for what Gluno says when a lookup fails.
///
/// THE ANSWER THAT SHIPPED: "Jag kunde inte hämta aktuella betyg just nu –
/// inga leverantörer svarar. Det här är min egen kunskap…"
///
/// Three sentences, of which one is useful. The user planning a holiday needs
/// to know how much to trust what follows. Which service was called, whether it
/// responded, and where the remainder came from are internal facts — and the
/// last one actively undermines the answer they are about to read.
///
/// The wording did not come from the model inventing it. The prompt asked for
/// it: "say plainly that you could not fetch current recommendations, then keep
/// helping from the plan and your general knowledge — and say that is what you
/// are doing." Gluno did exactly as instructed.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class SourceTalkEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    // ── The forbidden wording ────────────────────────────────────────────

    [Theory]
    [InlineData("Inga leverantörer svarar just nu.")]
    [InlineData("Det här är min egen kunskap.")]
    [InlineData("Jag kunde inte hämta live-data.")]
    [InlineData("Tripadvisor svarade inte.")]
    [InlineData("Tjänsten svarar inte just nu.")]
    [InlineData("Det beror på ett tekniskt fel.")]
    [InlineData("Integrationen fungerar inte.")]
    public void Swedish_source_talk_is_caught(string text)
    {
        Assert.True(GlunoUiPromise.ExplainsItsSources(text));
    }

    [Theory]
    [InlineData("No providers are responding.")]
    [InlineData("This is from my own knowledge.")]
    [InlineData("Provider unavailable.")]
    [InlineData("This comes from my training data.")]
    [InlineData("The API isn't responding.")]
    [InlineData("I couldn't fetch live data.")]
    [InlineData("Tripadvisor didn't respond.")]
    public void English_source_talk_is_caught(string text)
    {
        Assert.True(GlunoUiPromise.ExplainsItsSources(text));
    }

    [Theory]
    [InlineData("Jag kan inte bekräfta aktuella betyg just nu.")]
    [InlineData("Betyg och öppettider kan ha ändrats, så kontrollera dem innan ni åker.")]
    [InlineData("I can't confirm current ratings just now.")]
    [InlineData("Ratings and hours may have changed, so check before you go.")]
    [InlineData("Utifrån er rutt skulle jag prioritera de här tre.")]
    public void The_good_phrasings_are_left_alone(string text)
    {
        // These say what it means for the ANSWER. That is the half worth
        // keeping, and stripping it would leave a confident-looking reply with
        // no hint that anything was uncertain.
        Assert.False(GlunoUiPromise.ExplainsItsSources(text));
    }

    [Fact]
    public void The_exact_production_sentence_is_rewritten()
    {
        var actual =
            "Jag kunde inte hämta aktuella betyg just nu – inga leverantörer svarar. "
            + "Det här är min egen kunskap, men utifrån er rutt skulle jag börja med Ronda.";

        var safe = GlunoUiPromise.WithoutSourceTalk(actual, "sv");

        Assert.DoesNotContain("leverantör", safe);
        Assert.DoesNotContain("egen kunskap", safe);
        // And something about uncertainty survives, so the answer does not
        // read as more confident than it is.
        Assert.Contains("kan inte bekräfta", safe);
    }

    [Fact]
    public void The_caution_replaces_rather_than_merely_deletes()
    {
        var safe = GlunoUiPromise.WithoutSourceTalk("No providers are responding.", "en");

        // Deleting outright would leave nothing, and the user would never know
        // a lookup had failed at all.
        Assert.NotEqual(string.Empty, safe);
        Assert.Contains("can't confirm current details", safe);
    }

    [Fact]
    public void Useful_sentences_survive_alongside_the_caution()
    {
        var safe = GlunoUiPromise.WithoutSourceTalk(
            "Utifrån er rutt skulle jag prioritera de här tre. Inga leverantörer svarar just nu.",
            "sv");

        Assert.Contains("prioritera de här tre", safe);
        Assert.DoesNotContain("leverantör", safe);
    }

    [Fact]
    public void An_answer_with_no_source_talk_is_returned_untouched()
    {
        var clean = "Ni är i Ronda den 9 augusti. Det tar ungefär en timme att köra dit.";

        Assert.Equal(clean, GlunoUiPromise.WithoutSourceTalk(clean, "sv"));
    }

    [Fact]
    public void The_caution_exists_in_both_languages()
    {
        var swedish = GlunoUiPromise.WithoutSourceTalk("Inga leverantörer svarar.", "sv");
        var english = GlunoUiPromise.WithoutSourceTalk("No providers are responding.", "en");

        Assert.Contains("kontrollera dem innan ni åker", swedish);
        Assert.Contains("check them before you go", english);
        Assert.NotEqual(swedish, english);
    }

    // ── It runs on every answer ──────────────────────────────────────────

    [Fact]
    public void The_guard_runs_before_the_answer_is_stored()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        Assert.Contains("GlunoUiPromise.ExplainsItsSources(assistantText)", chat);
        Assert.Contains("GlunoUiPromise.WithoutSourceTalk(assistantText, context.User.Language)", chat);
    }

    [Fact]
    public void The_real_reason_stays_in_the_log()
    {
        var chat = Source("Services", "Gluno", "GlunoChatService.cs");

        // Diagnostics keep the detail; the chat does not. Losing the ability
        // to debug a provider outage would be a worse trade than the wording.
        Assert.Contains("answer explained its sources; caution substituted", chat);
    }

    [Fact]
    public void The_internal_failure_codes_still_exist()
    {
        // The user-facing text changes; the machine-readable state does not.
        foreach (var reason in Enum.GetValues<GlunoFallbackReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(reason.ToString()));
        }

        Assert.Contains(GlunoFallbackReason.TripadvisorUnavailable, Enum.GetValues<GlunoFallbackReason>());
        Assert.Contains(GlunoFallbackReason.RoutingUnavailable, Enum.GetValues<GlunoFallbackReason>());
    }

    // ── The backend's own notes were already safe ────────────────────────

    [Fact]
    public void No_fallback_note_names_a_provider_or_explains_a_failure()
    {
        foreach (var reason in Enum.GetValues<GlunoFallbackReason>())
        {
            foreach (var language in new[] { "sv", "en" })
            {
                var note = GlunoFallbacks.Note(reason, language);

                Assert.False(string.IsNullOrWhiteSpace(note));
                // Naming a provider in a failure attaches its brand to a bad
                // moment it had no part in.
                Assert.False(
                    GlunoUiPromise.ExplainsItsSources(note),
                    $"{reason}/{language} explains its sources: {note}");
            }
        }
    }

    [Fact]
    public void Every_fallback_note_is_one_short_sentence()
    {
        foreach (var reason in Enum.GetValues<GlunoFallbackReason>())
        {
            foreach (var language in new[] { "sv", "en" })
            {
                var note = GlunoFallbacks.Note(reason, language);

                // A caution, not a paragraph. Past this it stops being a note
                // beside the answer and starts competing with it.
                Assert.True(note.Length < 120, note);
                Assert.Equal(1, note.Count(character => character is '.' or '!' or '?'));
            }
        }
    }

    // ── The prompt no longer asks for it ─────────────────────────────────

    [Fact]
    public void The_prompt_no_longer_tells_Gluno_to_cite_its_own_knowledge()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        // This instruction is where the reported sentence came from. Gluno was
        // doing as it was told.
        Assert.DoesNotContain(
            "keep helping from the plan and your general knowledge — and say that is what you are doing",
            flat);
    }

    [Fact]
    public void The_prompt_gives_the_right_and_wrong_wording()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        Assert.Contains("say what it means for the ANSWER and nothing about why", flat);
        Assert.Contains("Wrong: \"No providers are responding.\"", flat);
        Assert.Contains("Wrong: \"This is from my own knowledge.\"", flat);
        Assert.Contains("Right: \"I can't confirm current ratings just now.\"", flat);
    }

    [Fact]
    public void The_prompt_forbids_substituting_a_remembered_number()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");
        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        // The one thing worse than admitting a lookup failed is filling the
        // gap with a rating nobody measured.
        Assert.Contains("Never substitute an old or remembered number", flat);
        Assert.Contains("suggest trying again shortly", flat);
    }

    [Fact]
    public void The_ledger_rules_on_numbers_are_untouched()
    {
        var prompt = Source("Services", "Gluno", "GlunoSystemPrompt.cs");

        // The caution wording changed; what Gluno is entitled to state did
        // not. A rating still needs a provider behind it.
        Assert.Contains("Name the provider when you state a rating", prompt);
        Assert.Contains("Never attach a provider's name to something it did not give you", prompt);
    }
}
