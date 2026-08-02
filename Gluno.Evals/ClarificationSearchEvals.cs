using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for provider references and for "something else".
///
/// TWO PROPERTIES, AND THE SECOND IS THE SECURITY ONE.
///
/// A reference like "the second one" must resolve against the list the user
/// ACTUALLY SAW. Resolving it by searching again would let a provider's
/// ordering decide what "second" means, and the user would get something they
/// never looked at.
///
/// And "something else" is a free-text box wired to a search. Every query below
/// runs over data already in front of the caller — their own Adventures, this
/// trip's days and Activities and stops, the results already shown in this
/// conversation. A query that could reach anything else would make this a
/// lookup endpoint with a text field on it.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ClarificationSearchEvals
{
    private static readonly DateOnly Today = new(2026, 8, 12);

    private static GlunoDiscussedPlaceContext Place(
        string name, string? category = null, string? address = null) => new()
        {
            Provider = "tripadvisor",
            ExternalId = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Category = category,
            Address = address,
        };

    private static GlunoDetectionInput Input(
        string message, params GlunoDiscussedPlaceContext[] places) => new()
        {
            Message = message,
            Intent = new GlunoIntentResult
            {
                PrimaryIntent = GlunoIntent.AddActivity,
                Confidence = 0.9,
                Scope = GlunoIntentScope.Trip,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = true,
                RequiresClarification = false,
            },
            Context = new GlunoContext
            {
                Today = Today,
                User = new GlunoUserContext { Language = "sv" },
                DiscussedPlaces = places,
            },
            Workflow = GlunoPlanningStrategy.For(
                new GlunoIntentResult
                {
                    PrimaryIntent = GlunoIntent.AddActivity,
                    Confidence = 0.9,
                    Scope = GlunoIntentScope.Trip,
                    RequiresCurrentData = false,
                    RequiresExternalSearch = false,
                    ExpectsProposal = true,
                    RequiresClarification = false,
                },
                hasTrip: true, canEdit: true),
            Today = Today,
            Language = "sv",
        };

    private static TripDestinationSummary Destinations(params string[] stops) => new()
    {
        Title = "Andalusien",
        StartDate = "2026-08-10",
        EndDate = "2026-08-22",
        Stops = stops.Select((stop, index) => new TripStop
        {
            Label = stop,
            From = new DateOnly(2026, 8, 10).AddDays(index * 3).ToString("yyyy-MM-dd"),
            To = new DateOnly(2026, 8, 12).AddDays(index * 3).ToString("yyyy-MM-dd"),
            Source = "day_location",
        }).ToList(),
    };

    // ── References to what was just shown ────────────────────────────────

    [Fact]
    public void The_second_one_resolves_to_the_second_result()
    {
        var second = Place("Casa Lopez");

        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta den andra", Place("El Pimpi"), second, Place("Los Patios")));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Equal($"tripadvisor:{second.ExternalId}", detection.ResolvedValue);
    }

    [Fact]
    public void An_ordinal_past_the_end_of_the_list_is_not_invented()
    {
        // "The fourth" with three results. Silently taking the last would
        // hand the user something they did not ask for.
        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta den fjärde", Place("A"), Place("B"), Place("C")));

        Assert.NotEqual(GlunoDetectionOutcome.Resolved, detection.Outcome);
    }

    [Fact]
    public void A_uniquely_named_result_resolves_directly()
    {
        var italian = Place("Trattoria Milano", "italiensk");

        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Lägg till den italienska", Place("El Pimpi", "spansk"), italian));

        Assert.Equal(GlunoDetectionOutcome.Resolved, detection.Outcome);
        Assert.Contains(italian.ExternalId, detection.ResolvedValue);
    }

    [Fact]
    public void A_vague_reference_offers_the_results_as_choices()
    {
        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta en av dem", Place("A"), Place("B"), Place("C")));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
        Assert.Equal(3, detection.Options.Count);
    }

    [Fact]
    public void The_cheapest_asks_because_the_context_carries_no_price()
    {
        // Ranking on data we do not have would be a guess dressed as a
        // decision. The context has no price level, so this asks.
        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta den billigaste av dem", Place("A"), Place("B")));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
    }

    [Fact]
    public void A_name_matching_two_results_asks_rather_than_picking()
    {
        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta restaurang", Place("Restaurang Nord"), Place("Restaurang Syd")));

        Assert.Equal(GlunoDetectionOutcome.NeedsClarification, detection.Outcome);
    }

    [Fact]
    public void A_message_with_no_reference_is_left_alone()
    {
        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectDiscussedPlace(
                Input("Vilket väder blir det?", Place("A"), Place("B"))).Outcome);
    }

    [Fact]
    public void Nothing_shown_means_nothing_to_reference()
    {
        Assert.Equal(
            GlunoDetectionOutcome.NotApplicable,
            GlunoClarificationDetector.DetectDiscussedPlace(Input("Ta den andra")).Outcome);
    }

    [Fact]
    public void Every_place_option_carries_a_provider_namespaced_id()
    {
        var options = GlunoClarificationBuilder.PlaceOptions([Place("A"), Place("B")]);

        // An id is only meaningful with the provider that issued it, and two
        // providers can use the same number.
        foreach (var option in options)
        {
            Assert.StartsWith("tripadvisor:", option.Value);
            Assert.Equal(GlunoClarificationEntityTypes.ExternalPlace, option.EntityType);
        }
    }

    [Fact]
    public void More_results_than_fit_offer_a_search_instead_of_a_longer_list()
    {
        var places = Enumerable.Range(0, 9).Select(index => Place($"Place {index}")).ToArray();

        var detection = GlunoClarificationDetector.DetectDiscussedPlace(
            Input("Ta en av dem", places));

        Assert.True(detection.Options.Count <= GlunoClarificationBuilder.MaxOptions);
        Assert.True(detection.AllowFreeText);
    }

    // ── Something else: query handling ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void A_query_too_short_to_mean_anything_is_refused(string? query)
        => Assert.False(GlunoClarificationSearch.IsUsable(query));

    [Fact]
    public void An_exact_date_is_usable_even_though_it_is_short()
        => Assert.True(GlunoClarificationSearch.IsUsable("2026-08-14"));

    [Fact]
    public void A_normal_query_is_usable()
        => Assert.True(GlunoClarificationSearch.IsUsable("ron"));

    // ── Something else: scope ────────────────────────────────────────────

    [Fact]
    public void Searching_Adventures_only_ever_narrows_the_caller_s_own_list()
    {
        var mine = new List<TripChoice>
        {
            new(Guid.NewGuid(), "Andalusien & Marocko", Today, Today.AddDays(6)),
            new(Guid.NewGuid(), "Nice & Monaco", Today.AddDays(30), Today.AddDays(36)),
        };

        var found = GlunoClarificationSearch.Adventures(mine, "monaco", Today, "sv");

        // Only from the list handed in — the search cannot reach a trip the
        // caller is not a member of, because it never sees one.
        Assert.Single(found);
        Assert.Equal("Nice & Monaco", found[0].Label);
    }

    [Fact]
    public void Searching_Adventures_matches_accents_typed_without_them()
    {
        var mine = new List<TripChoice>
        {
            new(Guid.NewGuid(), "Málaga", Today, Today.AddDays(4)),
        };

        Assert.Single(GlunoClarificationSearch.Adventures(mine, "malaga", Today, "sv"));
    }

    [Fact]
    public void Searching_days_matches_a_date_a_weekday_or_a_place()
    {
        var destinations = Destinations("Málaga", "Ronda");
        var start = new DateOnly(2026, 8, 10);
        var end = new DateOnly(2026, 8, 16);

        Assert.NotEmpty(GlunoClarificationSearch.Days(destinations, start, end, "2026-08-14", "sv"));
        Assert.NotEmpty(GlunoClarificationSearch.Days(destinations, start, end, "fredag", "sv"));
        Assert.NotEmpty(GlunoClarificationSearch.Days(destinations, start, end, "ronda", "sv"));
    }

    [Fact]
    public void Searching_days_never_leaves_the_Adventure()
    {
        var destinations = Destinations("Málaga");

        var found = GlunoClarificationSearch.Days(
            destinations, new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 12), "2026-12-24", "sv");

        // Christmas is not on this trip. The search cannot invent a day.
        Assert.Empty(found);
    }

    [Fact]
    public void Searching_Activities_matches_title_and_location()
    {
        var activities = new[]
        {
            new GlunoActivityContext
            {
                Id = Guid.NewGuid(), Title = "Picasso Museum",
                Date = new DateOnly(2026, 8, 11), LocationLabel = "Málaga",
            },
            new GlunoActivityContext
            {
                Id = Guid.NewGuid(), Title = "Flamenco",
                Date = new DateOnly(2026, 8, 13), LocationLabel = "Sevilla",
            },
        };

        Assert.Single(GlunoClarificationSearch.Activities(activities, "picasso", "sv"));
        Assert.Single(GlunoClarificationSearch.Activities(activities, "sevilla", "sv"));
    }

    [Fact]
    public void Searching_destinations_only_returns_stops_on_the_trip()
    {
        var destinations = Destinations("Málaga", "Ronda", "Sevilla");

        Assert.Single(GlunoClarificationSearch.Destinations(destinations, "ronda"));
        // Barcelona is not on this Adventure.
        Assert.Empty(GlunoClarificationSearch.Destinations(destinations, "barcelona"));
    }

    [Fact]
    public void Searching_places_stays_inside_the_conversation_s_own_snapshot()
    {
        var shown = new[]
        {
            Place("El Pimpi", "spansk", "Málaga"),
            Place("Trattoria Milano", "italiensk", "Ronda"),
        };

        Assert.Single(GlunoClarificationSearch.DiscussedPlaces(shown, "italiensk"));
        // Nothing outside what was already shown is reachable — this never
        // starts a provider search.
        Assert.Empty(GlunoClarificationSearch.DiscussedPlaces(shown, "sushi"));
    }

    [Fact]
    public void No_search_ever_returns_more_than_fits()
    {
        var many = Enumerable.Range(0, 30)
            .Select(index => new TripChoice(Guid.NewGuid(), $"Resa {index}", Today, Today.AddDays(3)))
            .ToList();

        Assert.True(
            GlunoClarificationSearch.Adventures(many, "resa", Today, "sv").Count
                <= GlunoClarificationBuilder.MaxOptions);

        var places = Enumerable.Range(0, 30).Select(index => Place($"Bar {index}")).ToArray();
        Assert.True(
            GlunoClarificationSearch.DiscussedPlaces(places, "bar").Count
                <= GlunoClarificationBuilder.MaxOptions);
    }

    [Fact]
    public void An_empty_result_is_an_answer_rather_than_an_error()
    {
        // The card says "nothing matched" and keeps the original options. A
        // search that finds nothing has not failed.
        Assert.Empty(GlunoClarificationSearch.Destinations(Destinations("Málaga"), "zzz"));
    }

    [Fact]
    public void Search_results_carry_backend_built_keys()
    {
        var found = GlunoClarificationSearch.Destinations(Destinations("Málaga", "Ronda"), "ronda");

        // New keys, produced here — a result the user just searched for is as
        // verifiable at resolve time as one offered originally.
        foreach (var option in found)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Key));
            Assert.True(option.Key.Length <= 64);
        }
    }
}
