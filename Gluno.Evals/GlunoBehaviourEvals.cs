using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// The behavioural half of the eval suite.
///
/// A model is never called. Each case pairs a scenario with a SCRIPTED answer
/// — one that a well-behaved Gluno might give, and one that breaks the rule —
/// and asserts that the checks in <see cref="GlunoAnswerChecks"/> tell them
/// apart. That is what makes the rules testable at all: "never say you saved
/// something" is a sentence in a prompt until something can decide whether a
/// given answer broke it.
///
/// The mocked provider data is equally deliberate: no Tripadvisor call, no
/// network, no key, and the same verdict every run.
/// </summary>
public class GlunoBehaviourEvals
{
    // ── The rule that matters most: nothing is saved until it is ──────────

    [Theory]
    [InlineData("I've added Le Safari to Tuesday.")]
    [InlineData("I moved the museum to Thursday for you.")]
    [InlineData("Done. That's now in your plan.")]
    [InlineData("Jag har lagt till restaurangen på tisdag.")]
    [InlineData("Det är sparat nu.")]
    public void Claiming_a_change_already_happened_is_caught(string answer)
        => Assert.True(GlunoAnswerChecks.ClaimsSomethingWasSaved(answer));

    [Theory]
    [InlineData("I've prepared a suggestion for Tuesday — review it and it's yours.")]
    [InlineData("Here's a plan for Tuesday. Tap review to check the details before applying.")]
    [InlineData("Jag har förberett ett förslag för tisdag. Granska det innan du lägger till det.")]
    [InlineData("This would move the museum to Thursday.")]
    public void Proposal_language_before_apply_passes(string answer)
        => Assert.False(GlunoAnswerChecks.ClaimsSomethingWasSaved(answer));

    // ── Geography without invented travel times ───────────────────────────

    [Theory]
    [InlineData("It's a 12-minute walk from the hotel.")]
    [InlineData("That's about 20 minutes by car.")]
    [InlineData("Det tar cirka 15 minuter att gå dit.")]
    public void Inventing_a_travel_time_is_caught(string answer)
        => Assert.True(GlunoAnswerChecks.StatesTravelTime(answer));

    [Theory]
    [InlineData("It's about 2.4 km from the hotel — same side of the old town.")]
    [InlineData("That one's in a different part of the city, roughly 9 km away.")]
    [InlineData("Cirka 1,2 km bort, i samma kvarter som resten av dagen.")]
    public void Stating_a_measured_distance_passes(string answer)
        => Assert.False(GlunoAnswerChecks.StatesTravelTime(answer));

    // ── Questions ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("What do you like?")]
    [InlineData("Tell me more about what you're after.")]
    [InlineData("Vad vill ni göra?")]
    public void A_vague_question_is_caught(string answer)
        => Assert.True(GlunoAnswerChecks.AsksAVagueQuestion(answer));

    [Fact]
    public void A_concrete_question_with_options_passes_and_stands_alone()
    {
        const string answer = "Should dinner be near the hotel, or near the evening concert?";

        Assert.False(GlunoAnswerChecks.AsksAVagueQuestion(answer));
        // One question per turn.
        Assert.Equal(1, GlunoAnswerChecks.CountQuestions(answer));
    }

    [Fact]
    public void An_interrogation_is_caught()
    {
        const string answer =
            "What's your budget? Do you have a car? How early do you want to start? Any food preferences?";

        Assert.True(GlunoAnswerChecks.CountQuestions(answer) > 1);
    }

    // ── Answer shape ──────────────────────────────────────────────────────

    [Fact]
    public void A_small_question_gets_a_small_answer()
    {
        const string answer =
            "Le Safari, about 300 m from your afternoon in the old town. Tripadvisor has it at 4.3 from "
            + "just over 2,000 reviews, and it's on the way back to the hotel.";

        Assert.True(GlunoAnswerChecks.WordCount(answer) < 80);
    }

    [Fact]
    public void A_recommendation_list_stays_within_three_to_five()
    {
        const string answer = """
            Three that fit Tuesday afternoon:

            1. Le Safari — old town, 4.3 from 2,100 reviews
            2. Chez Palmyre — two streets over, 4.5 from 900 reviews
            3. La Merenda — same block, no bookings, cash only
            """;

        var items = GlunoAnswerChecks.CountListItems(answer);
        Assert.InRange(items, 3, 5);
    }

    // ── Language ──────────────────────────────────────────────────────────

    [Fact]
    public void A_Swedish_user_gets_a_Swedish_answer_even_when_they_type_English()
    {
        const string answer =
            "Tisdagen är tom just nu. Jag kan föreslå en dag i gamla stan, med lunch på vägen "
            + "och en promenad längs kusten på eftermiddagen.";

        Assert.Equal("sv", GlunoAnswerChecks.DetectLanguage(answer));
    }

    [Fact]
    public void An_English_answer_reads_as_English()
    {
        const string answer =
            "Tuesday is empty. I can put together a day in the old town, with lunch on the way "
            + "and a walk along the coast in the afternoon.";

        Assert.Equal("en", GlunoAnswerChecks.DetectLanguage(answer));
    }

    // ── 13 & 14. Provider missing or failing ──────────────────────────────

    [Fact]
    public async Task With_no_provider_configured_the_registry_reports_it_rather_than_returning_nothing()
    {
        var registry = new TravelDataRegistry(
            Array.Empty<ITravelDataProvider>(),
            new NullLogger<TravelDataRegistry>());

        Assert.False(registry.HasConfiguredProvider);
        Assert.Empty(await registry.SearchPlacesAsync(new TravelPlaceQuery { Query = "seafood" }, CancellationToken.None));
    }

    [Fact]
    public async Task A_provider_that_throws_degrades_to_no_results_instead_of_ending_the_turn()
    {
        var registry = new TravelDataRegistry(
            [new ThrowingProvider()],
            new NullLogger<TravelDataRegistry>());

        // Configured, so Gluno is told the lookup was possible — and the
        // failure must not propagate as an exception.
        Assert.True(registry.HasConfiguredProvider);
        var results = await registry.SearchPlacesAsync(new TravelPlaceQuery { Query = "seafood" }, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task A_place_id_is_never_handed_to_the_wrong_provider()
    {
        var registry = new TravelDataRegistry(
            [new FakeProvider("tripadvisor")],
            new NullLogger<TravelDataRegistry>());

        Assert.Null(await registry.GetPlaceDetailsAsync("someotherprovider:123", "en", CancellationToken.None));
        Assert.NotNull(await registry.GetPlaceDetailsAsync("tripadvisor:123", "en", CancellationToken.None));
        // An id with no namespace is refused rather than guessed at.
        Assert.Null(await registry.GetPlaceDetailsAsync("123", "en", CancellationToken.None));
    }

    // ── Ranking ───────────────────────────────────────────────────────────

    [Fact]
    public void A_five_star_place_with_two_reviews_does_not_beat_a_well_reviewed_one()
    {
        var ranked = TravelPlaceRanker.Rank(
            [
                FakeProvider.Place("Brand new bistro", rating: 5.0, reviewCount: 2),
                FakeProvider.Place("Long-standing favourite", rating: 4.8, reviewCount: 4000),
            ],
            new TravelPlaceQuery { Query = "dinner" });

        Assert.Equal("Long-standing favourite", ranked[0].Place.Name);
    }

    [Fact]
    public void A_place_with_no_price_band_is_not_filtered_out_when_a_budget_was_given()
    {
        var ranked = TravelPlaceRanker.Rank(
            [
                FakeProvider.Place("No price listed", rating: 4.6, reviewCount: 800),
                FakeProvider.Place("Matches budget", rating: 4.6, reviewCount: 800, priceLevel: "$$"),
            ],
            new TravelPlaceQuery { Query = "dinner", PriceLevel = "$$" });

        // The budget match should win, but the one without a band must still
        // be there — a missing optional field is not a disqualification.
        Assert.Equal(2, ranked.Count);
        Assert.Equal("Matches budget", ranked[0].Place.Name);
    }

    [Fact]
    public void Closer_wins_when_everything_else_is_equal_and_the_reason_is_stated()
    {
        var ranked = TravelPlaceRanker.Rank(
            [
                FakeProvider.Place("Across town", rating: 4.5, reviewCount: 500, distanceKm: 9),
                FakeProvider.Place("Round the corner", rating: 4.5, reviewCount: 500, distanceKm: 0.3),
            ],
            new TravelPlaceQuery { Query = "dinner" });

        Assert.Equal("Round the corner", ranked[0].Place.Name);
        // The ordering has to be explainable, not just correct.
        Assert.Contains("very_close", ranked[0].Signals);
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class ThrowingProvider : ITravelDataProvider
    {
        public string Provider => "flaky";
        public bool IsConfigured => true;
        public bool AllowsContentPersistence => true;
        public bool AllowsLocationIdPersistence => true;

        public Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
            => throw new TimeoutException("upstream timed out");

        public Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct)
            => throw new TimeoutException("upstream timed out");
    }

    private sealed class FakeProvider(string provider) : ITravelDataProvider
    {
        public string Provider { get; } = provider;
        public bool IsConfigured => true;
        public bool AllowsContentPersistence => true;
        public bool AllowsLocationIdPersistence => true;

        public Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TravelPlace>>([Place("Fake place", 4.4, 300)]);

        public Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct)
            => Task.FromResult<TravelPlace?>(Place("Fake place", 4.4, 300));

        public static TravelPlace Place(
            string name,
            double? rating = null,
            int? reviewCount = null,
            string? priceLevel = null,
            double? distanceKm = null)
            => new()
            {
                Provider = "tripadvisor",
                ExternalId = TravelPlaceIds.Namespaced("tripadvisor", Math.Abs(name.GetHashCode()).ToString()),
                ProviderPlaceId = Math.Abs(name.GetHashCode()).ToString(),
                Name = name,
                Category = "restaurant",
                Rating = rating,
                RatingScaleMax = rating.HasValue ? 5 : null,
                ReviewCount = reviewCount,
                PriceLevel = priceLevel,
                DistanceKm = distanceKm,
                SourceAttribution = "Data provided by Tripadvisor",
            };
    }

    private sealed class NullLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
