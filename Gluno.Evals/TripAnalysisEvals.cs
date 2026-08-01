using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// The deterministic half of Gluno's evals: does SideQuest itself see what is
/// wrong with a plan, before any model is involved?
///
/// These matter more than they look. Every planning behaviour downstream —
/// which day Gluno offers to fill, whether it notices a restaurant is
/// stranded, whether it warns about the weather — is driven by these findings.
/// If the analyzer is wrong, no amount of prompting fixes it, and the failure
/// is invisible in a chat transcript.
///
/// Nothing here calls a model, a database or a provider.
/// </summary>
public class TripAnalysisEvals
{
    private static IReadOnlyList<TripFinding> Analyze(
        GlunoTripContext trip,
        TripPace pace = TripPace.Balanced,
        IReadOnlyList<GlunoWeatherContext>? weather = null)
        => TripAnalyzer.Analyze(trip, pace, weather ?? Array.Empty<GlunoWeatherContext>());

    private static bool Has(IReadOnlyList<TripFinding> findings, string type, DateOnly? date = null)
        => findings.Any(f => f.Type == type && (date == null || f.Date == date.Value.ToString("yyyy-MM-dd")));

    // ── 1. Plan an empty day ──────────────────────────────────────────────

    [Fact]
    public void Empty_day_is_found_so_Gluno_can_offer_to_fill_it()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Castle Hill", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
        ]);

        var findings = Analyze(trip);

        Assert.True(Has(findings, "empty_day", GlunoScenarios.Day2));
        Assert.True(Has(findings, "empty_day", GlunoScenarios.Day3));
        // The day that HAS something must not be reported as empty.
        Assert.False(Has(findings, "empty_day", GlunoScenarios.Day1));
    }

    // ── 2. Improve an overfull day ────────────────────────────────────────

    [Fact]
    public void Overfull_day_is_found_and_the_pace_is_stated_as_a_fact()
    {
        var stops = Enumerable.Range(0, 7)
            .Select(i => GlunoScenarios.Activity($"Stop {i}", GlunoScenarios.Day1, i, $"{9 + i:00}:00",
                latitude: GlunoScenarios.MonacoLat, longitude: GlunoScenarios.MonacoLon))
            .ToList();

        var findings = Analyze(GlunoScenarios.Trip(stops));
        var overpacked = findings.Single(f => f.Type == "overpacked_day");

        Assert.Equal("suggestion", overpacked.Severity);
        // Facts are what Gluno is allowed to state outright, so they have to
        // actually be there.
        Assert.Equal("7", overpacked.Facts["plannedStops"]);
        Assert.Equal("balanced", overpacked.Facts["pace"]);
    }

    // ── 3. Move an activity that sits geographically wrong ────────────────

    [Fact]
    public void Activity_far_from_the_rest_of_the_day_is_found_with_a_measured_distance()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Old town walk", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
            GlunoScenarios.Activity("Monaco casino", GlunoScenarios.Day1, 1, "13:00",
                latitude: GlunoScenarios.MonacoLat, longitude: GlunoScenarios.MonacoLon),
        ]);

        var findings = Analyze(trip);
        var hop = findings.Single(f => f.Type is "long_hop" or "very_long_hop");

        // A real, straight-line kilometre value — never a travel time.
        var km = double.Parse(hop.Facts["straightLineKm"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(km, 10, 20);
        Assert.DoesNotContain("minute", hop.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hour", hop.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. Restaurant near the hotel ──────────────────────────────────────

    [Fact]
    public void Meal_marooned_away_from_the_day_is_found()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Museum", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
            GlunoScenarios.Activity("Promenade", GlunoScenarios.Day1, 1, "15:00",
                latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
            GlunoScenarios.Activity("Dinner in Cannes", GlunoScenarios.Day1, 2, "19:00", category: "food",
                latitude: GlunoScenarios.CannesLat, longitude: GlunoScenarios.CannesLon),
        ]);

        var findings = Analyze(trip);
        Assert.True(Has(findings, "meal_far_from_day"));
    }

    // ── 5. A rainy day ────────────────────────────────────────────────────

    [Fact]
    public void Rain_over_an_outdoor_day_is_found_and_attributed_to_SideQuest_weather()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Coastal walk", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
        ]);

        var findings = Analyze(trip, weather: [GlunoScenarios.Weather(GlunoScenarios.Day1, "heavy_rain", 80)]);
        var weatherFinding = findings.Single(f => f.Type == "weather_risk");

        Assert.Equal("heavy_rain", weatherFinding.Facts["condition"]);
        // The provenance has to travel with the finding, or Gluno cannot say
        // where the forecast came from.
        Assert.Equal("sidequest_weather", weatherFinding.Facts["source"]);
    }

    [Fact]
    public void No_forecast_means_no_weather_finding_rather_than_an_assumption()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Coastal walk", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
        ]);

        Assert.False(Has(Analyze(trip), "weather_risk"));
    }

    // ── 6 & 7. Relaxed and packed pace ────────────────────────────────────

    [Fact]
    public void Same_day_reads_differently_at_each_pace()
    {
        var stops = Enumerable.Range(0, 4)
            .Select(i => GlunoScenarios.Activity($"Stop {i}", GlunoScenarios.Day1, i, $"{10 + i:00}:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon))
            .ToList();
        var trip = GlunoScenarios.Trip(stops);

        // Four stops is too many for a relaxed day, fine for balanced, and
        // unremarkable for packed.
        Assert.True(Has(Analyze(trip, TripPace.Relaxed), "overpacked_day"));
        Assert.False(Has(Analyze(trip, TripPace.Balanced), "overpacked_day"));
        Assert.False(Has(Analyze(trip, TripPace.Packed), "overpacked_day"));
    }

    [Fact]
    public void A_packed_traveller_is_told_when_a_day_is_thin()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("One museum", GlunoScenarios.Day1, 0, "11:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
        ]);

        Assert.True(Has(Analyze(trip, TripPace.Packed), "sparse_day", GlunoScenarios.Day1));
    }

    // ── 8. Several places on one day ──────────────────────────────────────

    [Fact]
    public void Multiple_locations_on_one_day_are_flagged_as_context_not_as_a_problem()
    {
        var trip = GlunoScenarios.Trip(
            [GlunoScenarios.Activity("Lunch", GlunoScenarios.Day1, 0, "12:00", category: "food",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon)],
            dayLocations:
            [
                GlunoScenarios.DayLocation(GlunoScenarios.Day1, "Nice", GlunoScenarios.NiceOldTownLat, GlunoScenarios.NiceOldTownLon),
                GlunoScenarios.DayLocation(GlunoScenarios.Day1, "Monaco", GlunoScenarios.MonacoLat, GlunoScenarios.MonacoLon, sortIndex: 1),
            ]);

        var finding = Analyze(trip).Single(f => f.Type == "multiple_locations_same_day");
        Assert.Equal("info", finding.Severity);
        Assert.Contains("Monaco", finding.Facts["locations"], StringComparison.Ordinal);
    }

    // ── 9. Ongoing Adventure with no end date ─────────────────────────────

    [Fact]
    public void An_open_ended_Adventure_analyses_without_inventing_an_end()
    {
        var trip = GlunoScenarios.Trip(
            [GlunoScenarios.Activity("Arrive", GlunoScenarios.Day1, 0, "09:00",
                latitude: GlunoScenarios.NiceAirportLat, longitude: GlunoScenarios.NiceAirportLon)],
            start: GlunoScenarios.Day1,
            end: null);

        var findings = Analyze(trip);

        // No end date must never make an activity look out of range.
        Assert.False(Has(findings, "activity_outside_trip_dates"));
        Assert.All(findings, f => Assert.NotEqual("error", f.Severity));
    }

    // ── 10. Hotel with check-in / check-out ───────────────────────────────

    [Fact]
    public void Something_planned_before_check_in_is_flagged_for_the_luggage_problem()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Hotel Negresco", GlunoScenarios.Day1, 0, "15:00", category: "hotel",
                endDate: GlunoScenarios.Day3, endTime: "11:00",
                latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
            GlunoScenarios.Activity("Old town walk", GlunoScenarios.Day1, 1, "10:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
        ]);

        var finding = Analyze(trip).Single(f => f.Type == "activity_before_checkin");
        Assert.Equal("15:00", finding.Facts["checkInTime"]);
    }

    [Fact]
    public void A_stay_that_ends_before_it_starts_is_a_warning()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Hotel", GlunoScenarios.Day2, 0, "15:00", category: "hotel",
                endDate: GlunoScenarios.Day1, endTime: "11:00"),
        ]);

        Assert.True(Has(Analyze(trip), "invalid_stay"));
    }

    // ── 16. Outside the Adventure's dates ─────────────────────────────────

    [Fact]
    public void An_activity_outside_the_dates_is_a_warning_with_both_bounds_as_facts()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Too early", GlunoScenarios.Day1.AddDays(-3), 0, "10:00"),
        ]);

        var finding = Analyze(trip).Single(f => f.Type == "activity_outside_trip_dates");
        Assert.Equal("warning", finding.Severity);
        Assert.Equal(GlunoScenarios.Day1.ToString("yyyy-MM-dd"), finding.Facts["tripStart"]);
    }

    // ── 18. Activity with no coordinates ──────────────────────────────────

    [Fact]
    public void Activities_without_coordinates_are_skipped_by_geography_not_placed_at_a_guess()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Somewhere unspecified", GlunoScenarios.Day1, 0, "10:00"),
            GlunoScenarios.Activity("Also unspecified", GlunoScenarios.Day1, 1, "14:00"),
        ]);

        var findings = Analyze(trip);

        Assert.False(Has(findings, "long_hop"));
        Assert.False(Has(findings, "zigzag_order"));
        Assert.False(Has(findings, "meal_far_from_day"));
    }

    // ── 19. Same name, different places ───────────────────────────────────

    [Fact]
    public void Two_places_with_the_same_name_far_apart_are_not_treated_as_duplicates()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Marché", GlunoScenarios.Day1, 0, "10:00",
                latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
            GlunoScenarios.Activity("Marché", GlunoScenarios.Day1, 1, "16:00",
                latitude: GlunoScenarios.MonacoLat, longitude: GlunoScenarios.MonacoLon),
        ]);

        var findings = Analyze(trip);

        // Identical titles ARE reported — the user should be asked — but the
        // distance between them must still be surfaced as its own finding.
        Assert.True(Has(findings, "near_duplicate"));
        Assert.True(Has(findings, "long_hop") || Has(findings, "very_long_hop"));
    }

    // ── 15. A plan that is already good ───────────────────────────────────

    [Fact]
    public void A_sensible_day_produces_nothing_to_complain_about()
    {
        var trip = GlunoScenarios.Trip(
            [
                GlunoScenarios.Activity("Old town walk", GlunoScenarios.Day1, 0, "10:00",
                    latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
                GlunoScenarios.Activity("Lunch nearby", GlunoScenarios.Day1, 1, "13:00", category: "food",
                    latitude: GlunoScenarios.NiceOldTownLat + 0.002, longitude: GlunoScenarios.NiceOldTownLon + 0.002),
                GlunoScenarios.Activity("Promenade", GlunoScenarios.Day1, 2, "16:00",
                    latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
                GlunoScenarios.Activity("Dinner nearby", GlunoScenarios.Day1, 3, "20:00", category: "food",
                    latitude: GlunoScenarios.NicePromenadeLat + 0.002, longitude: GlunoScenarios.NicePromenadeLon),
            ],
            start: GlunoScenarios.Day1,
            end: GlunoScenarios.Day1);

        var findings = Analyze(trip);

        // Nothing structural should be wrong with this day.
        Assert.DoesNotContain(findings, f =>
            f.Type is "overpacked_day" or "time_overlap" or "long_hop" or "very_long_hop"
                or "meal_far_from_day" or "missing_meal" or "zigzag_order");
    }

    // ── Time handling ─────────────────────────────────────────────────────

    [Fact]
    public void Two_things_at_the_same_clock_time_are_a_warning_not_a_suggestion()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Museum", GlunoScenarios.Day1, 0, "14:00"),
            GlunoScenarios.Activity("Boat trip", GlunoScenarios.Day1, 1, "14:00"),
        ]);

        var finding = Analyze(trip).Single(f => f.Type == "time_overlap");
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("14:00", finding.Facts["time"]);
    }

    [Fact]
    public void A_busy_day_with_nowhere_to_eat_is_flagged()
    {
        var trip = GlunoScenarios.Trip(
            Enumerable.Range(0, 4).Select(i =>
                GlunoScenarios.Activity($"Sight {i}", GlunoScenarios.Day1, i, $"{10 + (i * 2):00}:00",
                    latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon)));

        var finding = Analyze(trip, TripPace.Packed).Single(f => f.Type == "missing_meal");
        Assert.Equal("0", finding.Facts["plannedMeals"]);
    }

    // ── Geographic ordering ───────────────────────────────────────────────

    [Fact]
    public void A_zigzag_order_is_found_and_compared_against_a_grouped_one()
    {
        // Old town → Cannes → old town → Cannes: the same four places, ordered
        // as badly as possible.
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("A", GlunoScenarios.Day1, 0, latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
            GlunoScenarios.Activity("B", GlunoScenarios.Day1, 1, latitude: GlunoScenarios.CannesLat, longitude: GlunoScenarios.CannesLon),
            GlunoScenarios.Activity("C", GlunoScenarios.Day1, 2, latitude: GlunoScenarios.NiceOldTownLat, longitude: GlunoScenarios.NiceOldTownLon),
            GlunoScenarios.Activity("D", GlunoScenarios.Day1, 3, latitude: GlunoScenarios.CannesLat, longitude: GlunoScenarios.CannesLon),
        ]);

        var finding = Analyze(trip).Single(f => f.Type == "zigzag_order");

        var planned = double.Parse(finding.Facts["plannedStraightLineKm"], System.Globalization.CultureInfo.InvariantCulture);
        var grouped = double.Parse(finding.Facts["groupedStraightLineKm"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(planned > grouped);
    }

    [Fact]
    public void Moving_between_cities_with_no_transport_planned_is_a_warning()
    {
        var trip = GlunoScenarios.Trip(
            [
                GlunoScenarios.Activity("Beach", GlunoScenarios.Day1, 0, "11:00",
                    latitude: GlunoScenarios.NicePromenadeLat, longitude: GlunoScenarios.NicePromenadeLon),
                GlunoScenarios.Activity("Casino", GlunoScenarios.Day2, 0, "11:00",
                    latitude: GlunoScenarios.MonacoLat, longitude: GlunoScenarios.MonacoLon),
            ],
            dayLocations:
            [
                GlunoScenarios.DayLocation(GlunoScenarios.Day1, "Nice", GlunoScenarios.NiceOldTownLat, GlunoScenarios.NiceOldTownLon),
                GlunoScenarios.DayLocation(GlunoScenarios.Day2, "Cannes", GlunoScenarios.CannesLat, GlunoScenarios.CannesLon),
            ]);

        Assert.True(Has(Analyze(trip), "missing_intercity_transport"));
    }

    [Fact]
    public void A_planned_transfer_removes_the_missing_transport_warning()
    {
        var trip = GlunoScenarios.Trip(
            [
                GlunoScenarios.Activity("Train to Cannes", GlunoScenarios.Day2, 0, "09:00", category: "train",
                    latitude: GlunoScenarios.CannesLat, longitude: GlunoScenarios.CannesLon),
            ],
            dayLocations:
            [
                GlunoScenarios.DayLocation(GlunoScenarios.Day1, "Nice", GlunoScenarios.NiceOldTownLat, GlunoScenarios.NiceOldTownLon),
                GlunoScenarios.DayLocation(GlunoScenarios.Day2, "Cannes", GlunoScenarios.CannesLat, GlunoScenarios.CannesLon),
            ]);

        Assert.False(Has(Analyze(trip), "missing_intercity_transport"));
    }

    // ── Findings are structured, never prose ──────────────────────────────

    [Fact]
    public void Every_finding_carries_a_machine_type_and_a_known_severity()
    {
        var trip = GlunoScenarios.Trip([
            GlunoScenarios.Activity("Museum", GlunoScenarios.Day1, 0, "14:00"),
            GlunoScenarios.Activity("Boat", GlunoScenarios.Day1, 1, "14:00"),
        ]);

        foreach (var finding in Analyze(trip))
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Type));
            Assert.Contains(finding.Severity, new[] { "info", "suggestion", "warning" });
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation));
        }
    }
}
