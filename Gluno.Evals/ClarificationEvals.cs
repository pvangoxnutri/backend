using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for clickable follow-up questions.
///
/// TWO FAILURE MODES, PULLING IN OPPOSITE DIRECTIONS.
///
/// Ask too little and Gluno guesses which Adventure somebody meant, and plans
/// the wrong holiday. Ask too much and every question grows a chooser in front
/// of it, including the ones whose answer was already knowable — which is
/// slower than the sentence it replaced and trains people to tap without
/// reading.
///
/// Underneath both sits the security property: the model may ask for a KIND of
/// choice, never for the options themselves. Every option below is built from
/// rows fetched under the user's own membership, with ids the backend
/// produced, and re-verified when the user actually taps.
///
/// Nothing here calls a model, a network, or a database.
/// </summary>
public class ClarificationEvals
{
    private static readonly DateOnly Today = new(2026, 8, 12);

    private static TripChoice Trip(
        string title, int startOffset, int endOffset, string? places = null) => new(
            Guid.NewGuid(), title,
            Today.AddDays(startOffset), Today.AddDays(endOffset))
    {
        DestinationSummary = places,
    };

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none").Options;

        using var db = new AppDbContext(options);
        return db.Model;
    }

    // ── Resolving without asking ─────────────────────────────────────────

    [Fact]
    public void One_Adventure_is_chosen_without_a_question()
    {
        var trips = new[] { Trip("Andalusien", 0, 6) };

        Assert.NotNull(GlunoClarificationBuilder.ResolveSingle(trips, "Vart ska vi?", Today));
    }

    [Fact]
    public void An_Adventure_the_question_names_is_chosen_without_a_question()
    {
        var trips = new[]
        {
            Trip("Andalusien & Marocko", 30, 40),
            Trip("Nice & Monaco", 60, 66),
        };

        var resolved = GlunoClarificationBuilder.ResolveSingle(
            trips, "Vad ska vi göra i Andalusien?", Today);

        Assert.Equal("Andalusien & Marocko", resolved?.Title);
    }

    [Fact]
    public void The_trip_happening_right_now_is_chosen_when_nothing_else_is_named()
    {
        var trips = new[]
        {
            Trip("Andalusien", -2, 4),
            Trip("Nice", 60, 66),
            Trip("Stockholm", -90, -85),
        };

        var resolved = GlunoClarificationBuilder.ResolveSingle(
            trips, "Vad har vi på fredag?", Today);

        Assert.Equal("Andalusien", resolved?.Title);
    }

    [Fact]
    public void Two_equally_plausible_Adventures_are_not_guessed_between()
    {
        var trips = new[] { Trip("Nice", 30, 36), Trip("Stockholm", 40, 44) };

        // Neither named, neither running. Guessing here plans somebody's other
        // holiday.
        Assert.Null(GlunoClarificationBuilder.ResolveSingle(trips, "Vad gör vi?", Today));
    }

    [Fact]
    public void A_short_word_does_not_count_as_naming_a_trip()
    {
        // Substring matching would make "Nice" match "Venice" and a
        // three-letter token match almost anything.
        var trips = new[] { Trip("Rom", 30, 36), Trip("Nice", 40, 44) };

        Assert.Null(GlunoClarificationBuilder.ResolveSingle(trips, "Vad gör vi i Venice?", Today));
    }

    // ── Ranking ──────────────────────────────────────────────────────────

    [Fact]
    public void The_named_Adventure_ranks_first()
    {
        var trips = new[]
        {
            Trip("Stockholm weekend", -1, 2),
            Trip("Andalusien & Marocko", 30, 40),
        };

        var ranked = GlunoClarificationBuilder.RankTrips(trips, "vart i Andalusien?", Today);

        // Ahead of the trip that is actually running — being asked about is a
        // stronger signal than being current.
        Assert.Equal("Andalusien & Marocko", ranked[0].Title);
    }

    [Fact]
    public void The_running_Adventure_ranks_above_a_future_one()
    {
        var trips = new[] { Trip("Nice", 40, 46), Trip("Andalusien", -1, 5) };

        Assert.Equal("Andalusien", GlunoClarificationBuilder.RankTrips(trips, "hej", Today)[0].Title);
    }

    [Fact]
    public void Ranking_is_stable_for_the_same_inputs()
    {
        // A list that reorders itself between the question and the answer
        // means the user taps the second row and gets the third thing.
        var trips = new[] { Trip("A", 10, 12), Trip("B", 10, 12), Trip("C", 10, 12) };

        Assert.Equal(
            GlunoClarificationBuilder.RankTrips(trips, "hej", Today).Select(trip => trip.Id),
            GlunoClarificationBuilder.RankTrips(trips, "hej", Today).Select(trip => trip.Id));
    }

    [Fact]
    public void More_Adventures_than_fit_are_capped()
    {
        var trips = Enumerable.Range(0, 12)
            .Select(index => Trip($"Trip {index}", index, index + 2))
            .ToList();

        var options = GlunoClarificationBuilder.TripOptions(
            GlunoClarificationBuilder.RankTrips(trips, "hej", Today), Today, "sv");

        Assert.Equal(GlunoClarificationBuilder.MaxOptions, options.Count);
    }

    // ── What the options carry ───────────────────────────────────────────

    [Fact]
    public void An_Adventure_option_shows_status_dates_and_places()
    {
        var options = GlunoClarificationBuilder.TripOptions(
            [Trip("Andalusien", -1, 5, "Málaga · Ronda · Sevilla")], Today, "sv");

        var option = Assert.Single(options);

        Assert.Equal("Andalusien", option.Label);
        Assert.Contains("Málaga", option.Description);
        Assert.Contains("Pågår", option.Description);
    }

    [Fact]
    public void An_open_ended_Adventure_is_described_as_ongoing()
    {
        var trip = new TripChoice(Guid.NewGuid(), "Världen", Today.AddDays(-3), null);

        var option = Assert.Single(GlunoClarificationBuilder.TripOptions([trip], Today, "sv"));

        Assert.Contains("Pågående", option.Description);
    }

    [Fact]
    public void No_option_carries_a_route_or_a_url()
    {
        var options = GlunoClarificationBuilder.TripOptions([Trip("Andalusien", 0, 5)], Today, "en")
            .Concat(GlunoClarificationBuilder.TransportOptions("en"))
            .Concat(GlunoClarificationBuilder.PaceOptions("en"));

        foreach (var option in options)
        {
            Assert.DoesNotContain("http", option.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/", option.Value);
            // An icon is a name from the app's own set, never a URL.
            Assert.DoesNotContain("http", option.Icon ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Fixed vocabularies ───────────────────────────────────────────────

    [Fact]
    public void Pace_and_budget_offer_exactly_three_choices()
    {
        Assert.Equal(3, GlunoClarificationBuilder.PaceOptions("sv").Count);
        Assert.Equal(3, GlunoClarificationBuilder.BudgetOptions("sv").Count);
    }

    [Fact]
    public void Transport_options_are_all_allow_listed_values()
    {
        var allowed = new[] { "walking", "public_transport", "car", "taxi", "bike" };

        foreach (var option in GlunoClarificationBuilder.TransportOptions("en"))
        {
            Assert.Contains(option.Value, allowed);
            Assert.Equal(GlunoClarificationEntityTypes.Enum, option.EntityType);
        }
    }

    [Fact]
    public void Preference_scope_omits_the_Adventure_when_there_is_none()
    {
        var global = GlunoClarificationBuilder.PreferenceScopeOptions(hasTrip: false, "sv");
        var scoped = GlunoClarificationBuilder.PreferenceScopeOptions(hasTrip: true, "sv");

        Assert.DoesNotContain(global, option => option.Value == GlunoPreferenceScopes.Trip);
        Assert.Contains(scoped, option => option.Value == GlunoPreferenceScopes.Trip);

        // Every value is a real scope — nothing invented.
        foreach (var option in scoped) Assert.True(GlunoPreferenceScopes.IsKnown(option.Value));
    }

    // ── Localisation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("sv", "Vilket av dina Adventures menar du?")]
    [InlineData("en", "Which of your Adventures do you mean?")]
    public void The_question_is_short_and_localised(string language, string expected)
    {
        var question = GlunoClarificationBuilder.QuestionFor(
            GlunoClarificationTypes.Adventure, language);

        Assert.Equal(expected, question);
        // The response contract: a question, not a preamble.
        Assert.True(question.Length < 60);
    }

    [Fact]
    public void Every_clarification_type_has_a_question_in_both_languages()
    {
        foreach (var type in GlunoClarificationTypes.All)
        {
            foreach (var language in new[] { "sv", "en" })
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    GlunoClarificationBuilder.QuestionFor(type, language)));
            }
        }
    }

    [Fact]
    public void An_unknown_type_still_produces_a_usable_question()
    {
        // A future type must not render an empty card.
        Assert.False(string.IsNullOrWhiteSpace(
            GlunoClarificationBuilder.QuestionFor("something_new", "sv")));
    }

    // ── The contract with the client ─────────────────────────────────────

    [Fact]
    public void No_entity_id_crosses_the_wire()
    {
        // The client sends back a KEY. Every id stays server-side, so a
        // tampered request cannot point the choice at something else.
        var names = typeof(GlunoClarificationOptionDto).GetProperties()
            .Select(property => property.Name).ToList();

        Assert.DoesNotContain("EntityId", names);
        Assert.DoesNotContain("Value", names);
        Assert.Contains("Key", names);
    }

    [Fact]
    public void The_resolve_request_carries_nothing_but_a_choice()
    {
        var names = typeof(GlunoClarificationResolveDto).GetProperties()
            .Select(property => property.Name).ToList();

        Assert.DoesNotContain("UserId", names);
        Assert.DoesNotContain("TripId", names);
        Assert.DoesNotContain("EntityId", names);
    }

    [Fact]
    public void A_clarification_is_optional_on_an_ordinary_turn()
    {
        // Nullable, so a turn that needed no choice carries none — and an
        // older client ignores the field entirely.
        var property = typeof(GlunoTurnResponseDto).GetProperty("Clarification");

        Assert.NotNull(property);
        Assert.Null(new GlunoTurnResponseDto().Clarification);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public void A_new_clarification_is_pending_and_expires()
    {
        var clarification = new GlunoClarification();

        Assert.Equal(GlunoClarificationStatuses.Pending, clarification.Status);
        Assert.True(clarification.IsAnswerable);
        Assert.True(clarification.ExpiresAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData(GlunoClarificationStatuses.Resolved)]
    [InlineData(GlunoClarificationStatuses.Cancelled)]
    [InlineData(GlunoClarificationStatuses.Expired)]
    [InlineData(GlunoClarificationStatuses.Stale)]
    public void Only_a_pending_clarification_is_answerable(string status)
    {
        Assert.False(GlunoClarificationStatuses.IsOpen(status));
        Assert.False(new GlunoClarification { Status = status }.IsAnswerable);
    }

    [Fact]
    public void An_expired_clarification_is_not_answerable_even_while_pending()
    {
        var clarification = new GlunoClarification
        {
            Status = GlunoClarificationStatuses.Pending,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        };

        Assert.False(clarification.IsAnswerable);
    }

    [Fact]
    public void The_original_question_is_referenced_rather_than_copied()
    {
        // Storing the text again would be a second copy of what somebody
        // typed, and a second thing to leak.
        var properties = typeof(GlunoClarification).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("OriginalUserMessageId", properties);
        Assert.DoesNotContain("OriginalMessageText", properties);
        Assert.DoesNotContain("OriginalPrompt", properties);
    }

    [Fact]
    public void The_continuation_is_remembered_so_a_repeat_tap_replays_it()
        => Assert.NotNull(typeof(GlunoClarification).GetProperty("ContinuationMessageId"));

    // ── Storage ──────────────────────────────────────────────────────────

    [Fact]
    public void Clarifications_die_with_their_conversation_and_their_owner()
    {
        var entity = Model().FindEntityType(typeof(GlunoClarification))!;

        foreach (var principal in new[] { typeof(GlunoConversation), typeof(User) })
        {
            var fk = entity.GetForeignKeys()
                .FirstOrDefault(key => key.PrincipalEntityType.ClrType == principal);

            Assert.NotNull(fk);
            Assert.Equal(DeleteBehavior.Cascade, fk!.DeleteBehavior);
        }
    }

    [Fact]
    public void Deleting_an_Adventure_leaves_the_question_readable_but_unanswerable()
    {
        var tripFk = Model().FindEntityType(typeof(GlunoClarification))!
            .GetForeignKeys()
            .First(key => key.PrincipalEntityType.ClrType == typeof(Trip));

        // SetNull: the exchange is still the user's own history. The
        // membership re-check at resolve time is what stops it being acted on.
        Assert.Equal(DeleteBehavior.SetNull, tripFk.DeleteBehavior);
    }

    [Fact]
    public void An_option_key_is_unique_within_its_clarification()
    {
        var index = Model().FindEntityType(typeof(GlunoClarificationOption))!
            .GetIndexes()
            .FirstOrDefault(entry => entry.IsUnique);

        // The key is what the client sends back. Two options sharing one would
        // make the choice ambiguous.
        Assert.NotNull(index);
    }

    [Fact]
    public void Options_die_with_their_question()
    {
        var fk = Model().FindEntityType(typeof(GlunoClarificationOption))!
            .GetForeignKeys()
            .First(key => key.PrincipalEntityType.ClrType == typeof(GlunoClarification));

        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    // ── Nothing expensive runs before the choice ─────────────────────────

    [Fact]
    public void Resolving_is_cancellable_and_returns_a_typed_error()
    {
        foreach (var method in typeof(IGlunoClarificationService).GetMethods()
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType)))
        {
            Assert.True(
                method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)),
                $"{method.Name} cannot be cancelled");
        }

        Assert.True(typeof(GlunoClarificationError).IsEnum);
    }

    [Fact]
    public void The_chat_service_can_continue_from_a_clarification()
    {
        // Without this the user would have to retype the question — which is
        // the entire thing the feature exists to avoid.
        var method = typeof(IGlunoChatService).GetMethod(
            nameof(IGlunoChatService.ContinueFromClarificationAsync));

        Assert.NotNull(method);
    }

    [Fact]
    public void Every_option_key_is_short_enough_to_round_trip()
    {
        var options = GlunoClarificationBuilder.TripOptions(
            [Trip("A very long Adventure name that goes on and on", 0, 5)], Today, "en");

        foreach (var option in options) Assert.True(option.Key.Length <= 64);
    }
}
