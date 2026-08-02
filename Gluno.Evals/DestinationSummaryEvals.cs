using System.Text.Json;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for whether Gluno knows where the trip actually goes.
///
/// THE BUG THESE EXIST FOR. The context carried raw TripDayLocation rows: one
/// per stored row, no carry-forward, no ordering. The Feed shows Málaga across
/// three days because it runs those rows through
/// TripDayLocationService.ResolveTimeline; Gluno saw a single row on a single
/// date. Asked which cities a Spanish roadtrip visited, it could only offer the
/// trip's title — and then asked the user where they were going, about an
/// Adventure they had already filled in.
///
/// The fix is not a second interpretation of what a day's location means. It is
/// the SAME resolver, so the two surfaces cannot drift apart and disagree about
/// somebody's holiday.
///
/// Nothing here calls a model, a network, or a database.
/// </summary>
public class DestinationSummaryEvals
{
    private static Trip Trip(string destination = "", DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Roadtrip",
        Destination = destination,
        StartDate = new DateOnly(2026, 8, 10),
        EndDate = end ?? new DateOnly(2026, 8, 16),
    };

    private static TripDayLocation Day(string label, int day, int sortIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        StartDate = new DateOnly(2026, 8, day),
        SortIndex = sortIndex,
        LocationLabel = label,
    };

    private static TripDestinationSummary Build(
        Trip trip, IReadOnlyList<TripDayLocation> days, params GlunoActivityContext[] activities)
        => TripDestinationSummary.Build(trip, days, activities, Array.Empty<string>());

    // ── The roadtrip that started this ───────────────────────────────────

    [Fact]
    public void A_multi_stop_roadtrip_keeps_every_stop_in_order()
    {
        var summary = Build(Trip(), [Day("Málaga", 10), Day("Ronda", 12), Day("Sevilla", 14)]);

        // Not "Spain". Three places, in the order the trip reaches them.
        Assert.Equal(
            ["Málaga", "Ronda", "Sevilla"],
            summary.Stops.Select(stop => stop.Label).ToList());
    }

    [Fact]
    public void A_main_location_carries_forward_exactly_as_the_Feed_shows_it()
    {
        var summary = Build(Trip(), [Day("Málaga", 10), Day("Ronda", 13)]);

        var malaga = summary.Stops.First(stop => stop.Label == "Málaga");

        // Set once on the 10th, still the location on the 11th and 12th —
        // collapsed into one run rather than repeated per day.
        Assert.Equal("2026-08-10", malaga.From);
        Assert.Equal("2026-08-12", malaga.To);
    }

    [Fact]
    public void An_extra_stop_applies_to_its_own_day_and_no_other()
    {
        var summary = Build(Trip(), [Day("Málaga", 10), Day("Gibraltar", 11, sortIndex: 1)]);

        var extra = summary.Stops.First(stop => stop.Label == "Gibraltar");

        Assert.True(extra.AppliesToOneDayOnly);
        Assert.Equal(extra.From, extra.To);
        // And it must not have interrupted the main run.
        Assert.Contains(summary.Stops, stop => stop.Label == "Málaga" && stop.To != stop.From);
    }

    [Fact]
    public void A_country_is_never_inferred_from_a_place_name()
    {
        // Tangier and Faro are not Spanish destinations just because most of
        // the trip is in Spain. Countries come from the trip's own data, and
        // an empty list stays empty rather than being guessed.
        var summary = Build(Trip(), [Day("Málaga", 10), Day("Tanger", 12), Day("Faro", 14)]);

        Assert.Empty(summary.Countries);
        Assert.Equal(3, summary.Stops.Count);
    }

    // ── The weaker sources, kept weak ────────────────────────────────────

    [Fact]
    public void An_activity_location_fills_a_gap_but_is_labelled_as_weaker()
    {
        var summary = Build(
            Trip(),
            [],
            new GlunoActivityContext
            {
                Date = new DateOnly(2026, 8, 11),
                Title = "Lunch",
                LocationLabel = "Ronda",
            });

        var stop = Assert.Single(summary.Stops, entry => entry.Label == "Ronda");

        // Present, but never presented as something the user chose as the
        // day's location.
        Assert.Equal("activity", stop.Source);
        Assert.False(stop.IsExplicit);
    }

    [Fact]
    public void An_activity_with_no_location_marker_contributes_nothing()
    {
        // Prose is not a destination. "Dinner near the old town in Ronda" with
        // no marker is how a plan acquires a city nobody chose.
        var summary = Build(
            Trip(),
            [],
            new GlunoActivityContext
            {
                Date = new DateOnly(2026, 8, 11),
                Title = "Dinner",
                Description = "Somewhere near the old town in Ronda",
            });

        Assert.Empty(summary.Stops);
    }

    [Fact]
    public void A_day_with_no_location_anywhere_is_reported_as_missing()
    {
        // The first anchor lands on the 13th, and the trip has no destination
        // fallback — so the 10th to the 12th have no place at all.
        var summary = Build(Trip(), [Day("Málaga", 13)]);

        Assert.Contains("2026-08-10", summary.DaysWithoutLocation);
        Assert.Contains("2026-08-12", summary.DaysWithoutLocation);
        // From the anchor onward it carries forward, so those are not missing.
        Assert.DoesNotContain("2026-08-14", summary.DaysWithoutLocation);
    }

    [Fact]
    public void The_trip_destination_is_the_fallback_and_is_marked_as_such()
    {
        var trip = Trip(destination: "Spain");
        // The resolver only falls back to the trip destination when it has
        // coordinates — that is deliberate, and the test has to honour it.
        trip.DestinationLatitude = 40.4;
        trip.DestinationLongitude = -3.7;

        var summary = Build(trip, []);

        var stop = Assert.Single(summary.Stops);

        Assert.Equal("Spain", stop.Label);
        Assert.Equal("trip_destination", stop.Source);
        Assert.False(stop.IsExplicit);
    }

    [Fact]
    public void An_open_ended_trip_is_reported_as_ongoing_rather_than_given_an_end()
    {
        var trip = Trip();
        trip.EndDate = null;

        var summary = Build(trip, [Day("Málaga", 10)]);

        Assert.True(summary.IsOngoing);
        Assert.Null(summary.EndDate);
    }

    [Fact]
    public void A_removed_day_location_disappears_from_the_next_summary()
    {
        var trip = Trip();

        var before = Build(trip, [Day("Málaga", 10), Day("Ronda", 12)]);
        var after = Build(trip, [Day("Málaga", 10)]);

        Assert.Contains(before.Stops, stop => stop.Label == "Ronda");
        Assert.DoesNotContain(after.Stops, stop => stop.Label == "Ronda");
    }

    // ── It survives the context budget ───────────────────────────────────

    [Fact]
    public void Destinations_survive_a_context_far_over_budget()
    {
        var summary = Build(Trip(), [Day("Málaga", 10), Day("Ronda", 12), Day("Sevilla", 14)]);
        var destinations = JsonSerializer.Serialize(summary, GlunoJson.Options);

        var budget = new GlunoContextBudget(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gluno:Context:MaxTokens"] = "2000" })
            .Build());

        var fitted = budget.Fit(
        [
            new GlunoContextSection(GlunoContextPriority.RelevantTrip, "destinations", destinations)
            { IsCritical = true },
            new GlunoContextSection(GlunoContextPriority.OlderHistory, "history",
                $"\"{new string('x', 500_000)}\""),
            new GlunoContextSection(GlunoContextPriority.Evidence, "evidence",
                $"\"{new string('y', 500_000)}\""),
        ]);

        // The trimming took the history and the evidence. Every stop is still
        // there — a trip whose destinations were trimmed away is a trip Gluno
        // has to ask about.
        // ASCII only: the serialiser escapes "á" as \u00E1, which the model
        // decodes fine but a substring check does not.
        foreach (var place in new[] { "Ronda", "Sevilla" })
        {
            Assert.Contains(place, fitted.Json);
        }

        Assert.Contains("history", fitted.DroppedSections);
    }

    // ── Scope ────────────────────────────────────────────────────────────

    [Fact]
    public void A_global_conversation_gets_no_trip_context_at_all()
    {
        // And therefore no destinations. The absence is correct — global Gluno
        // genuinely has no Adventure selected.
        var workflow = GlunoPlanningStrategy.For(
            new GlunoIntentResult
            {
                PrimaryIntent = GlunoIntent.GeneralTravelQuestion,
                Confidence = 0.9,
                Scope = GlunoIntentScope.Global,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = false,
            },
            hasTrip: false,
            canEdit: false);

        Assert.False(workflow.NeedsTripContext);
    }

    [Fact]
    public void An_Adventure_conversation_loads_the_trip()
    {
        var workflow = GlunoPlanningStrategy.For(
            new GlunoIntentResult
            {
                PrimaryIntent = GlunoIntent.GeneralTravelQuestion,
                Confidence = 0.9,
                Scope = GlunoIntentScope.Trip,
                RequiresCurrentData = false,
                RequiresExternalSearch = false,
                ExpectsProposal = false,
                RequiresClarification = false,
            },
            hasTrip: true,
            canEdit: true);

        Assert.True(workflow.NeedsTripContext);
    }

    // ── The prompt's own rules ───────────────────────────────────────────

    [Fact]
    public void The_prompt_forbids_asking_where_the_trip_goes_when_it_is_known()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("NEVER ask where the trip goes", prompt);
        Assert.Contains("extra_stop", prompt);
        Assert.Contains("daysWithoutLocation", prompt);
    }

    [Fact]
    public void The_prompt_version_moved_with_the_behaviour()
        => Assert.True(GlunoSystemPrompt.Version >= 12);
}
