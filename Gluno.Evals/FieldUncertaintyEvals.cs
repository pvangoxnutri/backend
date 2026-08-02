using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for saying which field is uncertain, and only that field.
///
/// THE SENTENCE THAT SHIPPED: "Betygen kan jag inte kontrollera just nu, så
/// kolla öppettiderna innan ni går." Ratings could not be fetched — which says
/// nothing whatever about opening hours. It sends somebody to verify something
/// that was never in doubt while leaving the real gap unmentioned.
///
/// IT WAS NOT ONE SENTENCE. The model wrote about ratings, and the backend
/// separately appended a note about opening hours, chosen by a first-match
/// chain that fired whenever any hours entry was stale regardless of what the
/// answer had been about. Two correct halves concatenated into something
/// false.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class FieldUncertaintyEvals
{
    private static string Note(
        string language, params (GlunoDataField Field, GlunoFieldStatus Status)[] statuses)
        => GlunoFieldUncertainty.Note(
            statuses.ToDictionary(entry => entry.Field, entry => entry.Status), language)
           ?? string.Empty;

    // ── One field at a time ──────────────────────────────────────────────

    [Fact]
    public void Only_ratings_missing_mentions_only_ratings()
    {
        var swedish = Note("sv", (GlunoDataField.Rating, GlunoFieldStatus.Unavailable));

        Assert.Equal("Jag kan inte bekräfta aktuella betyg just nu.", swedish);
        // The failure that started this: no advice about a field that was
        // never in doubt.
        Assert.DoesNotContain("öppettid", swedish);
        Assert.DoesNotContain("innan ni går", swedish);
    }

    [Fact]
    public void Only_opening_hours_missing_mentions_only_hours_and_advises()
    {
        var swedish = Note("sv", (GlunoDataField.OpeningHours, GlunoFieldStatus.Unavailable));

        Assert.Contains("dagens öppettider", swedish);
        // "Check before you go" belongs to hours alone — on a rating it would
        // be advice about the wrong thing.
        Assert.Contains("kontrollera innan ni går", swedish);
        Assert.DoesNotContain("betyg", swedish);
    }

    [Fact]
    public void Only_price_missing_mentions_only_price_and_does_not_advise()
    {
        var swedish = Note("sv", (GlunoDataField.Price, GlunoFieldStatus.Unavailable));

        Assert.Equal("Jag kan inte bekräfta aktuellt pris just nu.", swedish);
        // Advising a check on a price would imply the place might be shut.
        Assert.DoesNotContain("innan ni går", swedish);
    }

    [Fact]
    public void Two_missing_fields_name_exactly_those_two()
    {
        var swedish = Note(
            "sv",
            (GlunoDataField.Rating, GlunoFieldStatus.Unavailable),
            (GlunoDataField.OpeningHours, GlunoFieldStatus.Unavailable));

        Assert.Contains("aktuella betyg", swedish);
        Assert.Contains("dagens öppettider", swedish);
        Assert.DoesNotContain("pris", swedish);
    }

    [Fact]
    public void A_verified_field_produces_no_caution()
    {
        Assert.Equal(string.Empty, Note("sv", (GlunoDataField.Rating, GlunoFieldStatus.Verified)));

        // And a verified field is not dragged into somebody else's caution.
        var mixed = Note(
            "sv",
            (GlunoDataField.Rating, GlunoFieldStatus.Unavailable),
            (GlunoDataField.OpeningHours, GlunoFieldStatus.Verified));

        Assert.Contains("betyg", mixed);
        Assert.DoesNotContain("öppettid", mixed);
    }

    [Fact]
    public void A_field_nobody_asked_about_is_never_mentioned()
    {
        // "I didn't look up the price" is noise on an answer that was not
        // about price.
        Assert.Equal(
            string.Empty,
            Note("sv", (GlunoDataField.Price, GlunoFieldStatus.NotRequested)));

        var withRating = Note(
            "sv",
            (GlunoDataField.Rating, GlunoFieldStatus.Unavailable),
            (GlunoDataField.Price, GlunoFieldStatus.NotRequested));

        Assert.DoesNotContain("pris", withRating);
    }

    [Fact]
    public void Stale_counts_as_uncertain()
    {
        Assert.NotEqual(
            string.Empty,
            Note("sv", (GlunoDataField.OpeningHours, GlunoFieldStatus.Stale)));
    }

    [Fact]
    public void Nothing_uncertain_produces_no_line_at_all()
    {
        Assert.Null(GlunoFieldUncertainty.Note(
            new Dictionary<GlunoDataField, GlunoFieldStatus>(), "sv"));
    }

    [Fact]
    public void The_caution_is_one_short_sentence()
    {
        foreach (var language in new[] { "sv", "en" })
        {
            var note = Note(
                language,
                (GlunoDataField.Rating, GlunoFieldStatus.Unavailable),
                (GlunoDataField.OpeningHours, GlunoFieldStatus.Unavailable),
                (GlunoDataField.Price, GlunoFieldStatus.Unavailable));

            Assert.Equal(1, note.Count(character => character is '.' or '!' or '?'));
            Assert.True(note.Length < 160, note);
        }
    }

    [Fact]
    public void Both_languages_are_written()
    {
        var swedish = Note("sv", (GlunoDataField.Rating, GlunoFieldStatus.Unavailable));
        var english = Note("en", (GlunoDataField.Rating, GlunoFieldStatus.Unavailable));

        Assert.Equal("I can't confirm current ratings just now.", english);
        Assert.NotEqual(swedish, english);
    }

    [Fact]
    public void The_field_order_is_stable()
    {
        // Same two fields always read the same way, rather than depending on
        // dictionary iteration order.
        var first = Note(
            "en",
            (GlunoDataField.OpeningHours, GlunoFieldStatus.Unavailable),
            (GlunoDataField.Rating, GlunoFieldStatus.Unavailable));

        var second = Note(
            "en",
            (GlunoDataField.Rating, GlunoFieldStatus.Unavailable),
            (GlunoDataField.OpeningHours, GlunoFieldStatus.Unavailable));

        Assert.Equal(first, second);
    }

    // ── The guard on the model's own version ─────────────────────────────

    [Fact]
    public void The_exact_production_sentence_is_caught()
    {
        Assert.True(GlunoUiPromise.MixesUncertainFields(
            "Betygen kan jag inte kontrollera just nu, så kolla öppettiderna innan ni går."));
    }

    [Theory]
    [InlineData("I can't check the ratings, so check the opening hours before you go.")]
    [InlineData("Jag kan inte bekräfta priset, men kolla öppettiderna.")]
    public void Other_cross_field_cautions_are_caught(string text)
    {
        Assert.True(GlunoUiPromise.MixesUncertainFields(text));
    }

    [Theory]
    [InlineData("Jag kan inte bekräfta aktuella betyg just nu.")]
    [InlineData("Jag kan inte bekräfta dagens öppettider, så kontrollera innan ni går.")]
    [InlineData("I can't confirm current ratings just now.")]
    [InlineData("Betyget är 4,5 och de har öppet till 18.")]
    public void A_single_field_caution_is_left_alone(string text)
    {
        // One field per sentence is exactly what the structural note produces,
        // and a statement of fact about two fields is not a caution at all.
        Assert.False(GlunoUiPromise.MixesUncertainFields(text));
    }

    [Fact]
    public void Fields_mentioned_in_separate_sentences_are_fine()
    {
        // An answer may legitimately talk about ratings in one sentence and
        // hours in another.
        Assert.False(GlunoUiPromise.MixesUncertainFields(
            "Betyget är 4,5. Öppettiderna kan jag inte bekräfta."));
    }

    [Fact]
    public void The_mixing_sentence_is_dropped_whole()
    {
        var trimmed = GlunoUiPromise.WithoutMixedFields(
            "Real Alcázar är värt ett besök. Betygen kan jag inte kontrollera just nu, "
            + "så kolla öppettiderna innan ni går.");

        Assert.Contains("Real Alcázar är värt ett besök.", trimmed);
        Assert.DoesNotContain("kolla öppettiderna", trimmed);
    }

    [Fact]
    public void The_guard_runs_on_every_answer()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        Assert.Contains("GlunoUiPromise.MixesUncertainFields(assistantText)", chat);
        Assert.Contains("answer mixed uncertain fields; clause dropped", chat);
    }

    // ── The backend no longer cross-wires ────────────────────────────────

    [Fact]
    public void The_note_is_built_from_per_field_statuses()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var start = chat.IndexOf("private string WithFreshnessNote", StringComparison.Ordinal);
        var body = chat[start..(start + 2800)];

        Assert.True(start > 0);
        // The old shape picked ONE reason by first match, and the hours branch
        // fired whatever the answer was about.
        Assert.Contains("var statuses = new Dictionary<GlunoDataField, GlunoFieldStatus>()", body);
        Assert.Contains("GlunoFieldUncertainty.Note(statuses, language)", body);
        Assert.DoesNotContain("GlunoFallbackReason? reason = null;", body);
    }

    [Fact]
    public void A_field_with_no_ledger_entries_reports_nothing()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var start = chat.IndexOf("private string WithFreshnessNote", StringComparison.Ordinal);
        var body = chat[start..(start + 2800)];

        // Absent entirely means nobody looked, which is not a gap worth
        // mentioning — that is the NotRequested case, and it never gets added
        // to the dictionary at all.
        Assert.Contains("if (hours.Count > 0)", body);
        Assert.Contains("if (ratings.Count > 0)", body);
    }

    [Fact]
    public void The_prompt_forbids_cross_field_advice()
    {
        var prompt = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoSystemPrompt.cs"));

        var flat = System.Text.RegularExpressions.Regex.Replace(prompt, @"\s+", " ");

        Assert.Contains("A CAUTION BELONGS TO ONE FIELD", flat);
        Assert.Contains("Wrong: \"I can't check the ratings, so check the opening hours before you go.\"", flat);
        Assert.Contains("never mention a field nobody asked about", flat);
    }

    // ── Adding a place by name ───────────────────────────────────────────

    private static GlunoPlaceCard Place(string name) => new()
    {
        Provider = "tripadvisor",
        ExternalId = $"tripadvisor:{name.Length}",
        Name = name,
        Category = "attraction",
        SourceAttribution = "Tripadvisor",
    };

    private static readonly GlunoPlaceCard[] Shown =
    [
        Place("Real Alcázar"),
        Place("Catedral de Sevilla"),
        Place("Metropol Parasol"),
    ];

    [Theory]
    [InlineData("Lägg till Real Alcázar")]
    [InlineData("lagg till alcazar")]
    [InlineData("Add Real Alcázar please")]
    public void A_named_place_resolves_to_exactly_one(string message)
    {
        var matches = GlunoPlaceOptions.Match(Shown, message);

        Assert.Single(matches);
        Assert.Equal(0, matches[0]);
    }

    [Fact]
    public void A_positional_reference_resolves()
    {
        Assert.Equal(0, GlunoPlaceOptions.Match(Shown, "Lägg till den första")[0]);
        Assert.Equal(1, GlunoPlaceOptions.Match(Shown, "add the second one")[0]);
    }

    [Fact]
    public void A_position_past_the_list_resolves_to_nothing()
    {
        Assert.Empty(GlunoPlaceOptions.Match(Shown, "Lägg till den femte"));
    }

    [Fact]
    public void A_name_matching_several_places_asks()
    {
        var places = new[] { Place("Museo Picasso"), Place("Museo Naval") };

        // "Museo" fits both. Adding the wrong one puts somewhere they did not
        // choose into their plan.
        Assert.Equal(2, GlunoPlaceOptions.Match(places, "Lägg till Museo").Count);
    }

    [Fact]
    public void A_name_nobody_showed_resolves_to_nothing()
    {
        Assert.Empty(GlunoPlaceOptions.Match(Shown, "Lägg till Colosseum"));
    }

    [Fact]
    public void Short_words_in_a_name_do_not_match_everything()
    {
        // "de" and "la" appear in half of Spanish place names.
        Assert.Empty(GlunoPlaceOptions.Match(Shown, "Lägg till de"));
    }

    [Theory]
    [InlineData("Lägg till Real Alcázar", true)]
    [InlineData("Add the first one", true)]
    [InlineData("Vad borde vi se?", false)]
    [InlineData("Hur långt är det dit?", false)]
    public void An_add_request_is_recognised_by_its_verb(string message, bool expected)
    {
        Assert.Equal(expected, GlunoPlaceOptions.IsAddRequest(message));
    }

    [Fact]
    public void The_text_path_runs_before_the_model()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var addAt = chat.IndexOf("GlunoPlaceOptions.IsAddRequest(text)", StringComparison.Ordinal);
        var modelAt = chat.IndexOf("RunModelAsync", StringComparison.Ordinal);

        Assert.True(addAt > 0);
        // A model asked to "add Real Alcázar" has to reconstruct which place
        // that was, and has no way to produce a provider reference at all.
        if (modelAt > 0) Assert.True(addAt < modelAt);
    }

    [Fact]
    public void The_text_path_searches_no_provider()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var start = chat.IndexOf("private async Task<GlunoTurnResult?> AddNamedPlaceAsync", StringComparison.Ordinal);
        var end = chat.IndexOf("private static IReadOnlyList<GlunoPlaceCard> ReadPlaces", StringComparison.Ordinal);
        var body = chat[start..end];

        Assert.True(start > 0 && end > start);
        // The user is pointing at something on their screen. A fresh lookup
        // could return a different place with a similar name.
        Assert.DoesNotContain("_actions", body);
        Assert.DoesNotContain("search_places", body);
    }

    [Fact]
    public void An_unrelated_add_request_falls_through_to_the_ordinary_turn()
    {
        var chat = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        // "Add a rest day" is an add request and is nothing to do with
        // recommended places — the handler returns null and the turn carries
        // on.
        Assert.Contains("if (added != null) return added;", chat);
    }
}
