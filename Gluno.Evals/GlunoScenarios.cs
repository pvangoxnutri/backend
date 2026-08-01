using sidequest.backend.Services.Gluno;

namespace Gluno.Evals;

/// <summary>
/// The Adventures the eval suite plans against.
///
/// Hand-built in memory rather than seeded into a database: the whole point is
/// that a scenario is a fixed, readable input, so a failing eval says exactly
/// which shape of trip broke — not "something in the fixture".
///
/// Coordinates are real, because the geographic checks are the ones worth
/// testing. Nice, Monaco and Vieux Nice sit where they actually sit, so
/// "9 km apart" in an assertion is a genuine 9 km.
/// </summary>
public static class GlunoScenarios
{
    // Real coordinates — the distances between these are what the geographic
    // findings are measured against.
    public const double NiceOldTownLat = 43.6961;
    public const double NiceOldTownLon = 7.2758;
    public const double NicePromenadeLat = 43.6947;
    public const double NicePromenadeLon = 7.2650;
    public const double NiceAirportLat = 43.6653;
    public const double NiceAirportLon = 7.2150;
    public const double MonacoLat = 43.7384;
    public const double MonacoLon = 7.4246;
    public const double CannesLat = 43.5528;
    public const double CannesLon = 7.0174;

    public static readonly DateOnly Day1 = new(2026, 8, 10);
    public static readonly DateOnly Day2 = new(2026, 8, 11);
    public static readonly DateOnly Day3 = new(2026, 8, 12);

    /// <summary>
    /// A trip with the given days, wired up so <see cref="TripAnalyzer"/> sees
    /// a realistic Adventure rather than a bag of activities.
    /// </summary>
    public static GlunoTripContext Trip(
        IEnumerable<GlunoActivityContext> activities,
        IEnumerable<GlunoDayLocationContext>? dayLocations = null,
        DateOnly? start = null,
        DateOnly? end = null)
    {
        var activityList = activities.ToList();
        var startDate = start ?? Day1;
        // `end` is nullable on purpose: passing null models an OPEN-ENDED
        // Adventure, so it must not silently fall back to a default date.
        var endDate = end;
        if (end == null && start == null) endDate = Day3;

        return new GlunoTripContext
        {
            Id = Guid.NewGuid(),
            Title = "Riviera",
            Destination = "Nice",
            DestinationLatitude = NiceOldTownLat,
            DestinationLongitude = NiceOldTownLon,
            StartDate = startDate,
            EndDate = endDate,
            // For an open-ended trip the analyzer needs a finite bound to walk
            // to; the app derives the same thing via TripDateRange.
            EffectiveEndDate = endDate ?? startDate.AddDays(2),
            IsOpenEnded = endDate == null,
            IsOwner = true,
            MembersCanEdit = true,
            CanEdit = true,
            MemberCount = 2,
            Activities = activityList,
            DayLocations = (dayLocations ?? DefaultDayLocations(startDate)).ToList(),
        };
    }

    private static IEnumerable<GlunoDayLocationContext> DefaultDayLocations(DateOnly start)
    {
        yield return new GlunoDayLocationContext
        {
            Date = start,
            SortIndex = 0,
            Label = "Nice",
            Latitude = NiceOldTownLat,
            Longitude = NiceOldTownLon,
        };
    }

    public static GlunoActivityContext Activity(
        string title,
        DateOnly date,
        int sortIndex,
        string? time = null,
        string? category = null,
        double? latitude = null,
        double? longitude = null,
        DateOnly? endDate = null,
        string? endTime = null,
        string? placeId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Date = date,
            SortIndex = sortIndex,
            Title = title,
            Time = time,
            EndDate = endDate,
            EndTime = endTime,
            Category = category,
            Latitude = latitude,
            Longitude = longitude,
            PlaceId = placeId,
            Role = ActivityRoles.FromCategory(category, endDate),
        };

    public static GlunoWeatherContext Weather(
        DateOnly date, string condition, int precipitationProbability = 0, string label = "Nice")
        => new()
        {
            Date = date,
            Condition = condition,
            PrecipitationProbability = precipitationProbability,
            TempMaxC = 24,
            TempMinC = 18,
            LocationLabel = label,
        };

    public static GlunoDayLocationContext DayLocation(
        DateOnly date, string label, double latitude, double longitude, int sortIndex = 0)
        => new()
        {
            Date = date,
            SortIndex = sortIndex,
            Label = label,
            Latitude = latitude,
            Longitude = longitude,
        };
}
