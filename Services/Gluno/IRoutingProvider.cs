using System.Globalization;

namespace sidequest.backend.Services.Gluno;

public enum TravelMode
{
    Walking,
    Driving,
    Transit,
    Cycling,
}

public static class TravelModes
{
    public static TravelMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "driving" or "drive" or "car" or "bil" => TravelMode.Driving,
        "transit" or "public_transport" or "kollektivtrafik" or "buss" or "tåg" or "tag" => TravelMode.Transit,
        "cycling" or "bike" or "bicycle" or "cykel" => TravelMode.Cycling,
        _ => TravelMode.Walking,
    };

    public static string ToWireValue(TravelMode mode) => mode switch
    {
        TravelMode.Driving => "driving",
        TravelMode.Transit => "transit",
        TravelMode.Cycling => "cycling",
        _ => "walking",
    };

    /// Display names live here rather than in the model's answer, so "Gång"
    /// and "Walking" can never drift apart from the wire value.
    public static string Label(TravelMode mode, string language)
    {
        var swedish = language == "sv";
        return mode switch
        {
            TravelMode.Driving => swedish ? "Bil" : "Driving",
            TravelMode.Transit => swedish ? "Kollektivtrafik" : "Transit",
            TravelMode.Cycling => swedish ? "Cykel" : "Cycling",
            _ => swedish ? "Gång" : "Walking",
        };
    }
}

/// <summary>
/// One end of a journey. A label is carried for cache keys and explanations
/// only — the provider is given coordinates.
/// </summary>
public sealed record RoutePoint(double Latitude, double Longitude, string? Label = null)
{
    /// Rounded to ~100 m. Two stops in the same square share a cache entry
    /// instead of each paying for its own provider call.
    public string CacheKey()
        => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Latitude, 3)},{Math.Round(Longitude, 3)}");

    public bool IsValid()
        => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>
/// The result of asking "how do I get from here to there?".
///
/// <see cref="Verified"/> is the field everything else hangs off. True means a
/// routing provider actually computed this and <see cref="DurationMinutes"/>
/// may be stated as a travel time. False means SideQuest measured a
/// straight line and nothing more — there is NO duration, and the prompt
/// forbids inventing one.
///
/// That distinction is the whole point of this layer. "About 12 minutes' walk"
/// with no routing data behind it is the most convincing kind of wrong answer
/// an assistant can give, because the user has no way to tell.
/// </summary>
public sealed class RouteLeg
{
    public required RoutePoint Origin { get; init; }
    public required RoutePoint Destination { get; init; }
    public required TravelMode Mode { get; init; }

    /// Straight-line when unverified; the provider's road/route distance when
    /// verified.
    public double? DistanceKm { get; init; }

    /// ONLY ever set when <see cref="Verified"/> is true.
    public int? DurationMinutes { get; init; }

    /// Walking at either end of a transit leg, when the provider reports it.
    public int? AccessWalkMinutes { get; init; }

    /// "google_routes" | "straight_line". Never a URL.
    public required string Source { get; init; }

    /// True only when a routing provider computed it.
    public required bool Verified { get; init; }

    public DateTime ComputedAt { get; init; } = DateTime.UtcNow;

    /// Machine reason a verified result is missing: "no_provider",
    /// "provider_failed", "no_route", "budget_exhausted", "invalid_point".
    public string? UnavailableReason { get; init; }

    /// <summary>
    /// The straight-line fallback. Distance only — deliberately no duration,
    /// because there is no honest way to derive one without routing data.
    /// </summary>
    public static RouteLeg StraightLine(RoutePoint from, RoutePoint to, TravelMode mode, string reason)
        => new()
        {
            Origin = from,
            Destination = to,
            Mode = mode,
            DistanceKm = GeoDistance.KilometresBetween(from.Latitude, from.Longitude, to.Latitude, to.Longitude),
            DurationMinutes = null,
            Source = "straight_line",
            Verified = false,
            UnavailableReason = reason,
        };
}

/// <summary>
/// One source of route and travel-time data.
///
/// Deliberately an interface with a matrix method rather than a single-leg
/// one: comparing four candidate orders for a day is sixteen pairs, and any
/// design that asks per pair will either be slow or expensive. Providers that
/// only do single legs can implement the matrix by looping — that choice
/// belongs to them, not to the planner.
///
/// Credentials are server-side only. The mobile app never calls a routing
/// provider, and no caller here can supply a URL or an endpoint.
/// </summary>
public interface IRoutingProvider
{
    string Provider { get; }

    /// False when there is no key or the integration is switched off. Gluno
    /// then keeps working on straight-line distances and says so.
    bool IsConfigured { get; }

    /// Largest number of origin × destination pairs one call may cover.
    int MaxMatrixElements { get; }

    /// <param name="departureUtc">
    /// When the journey starts. Matters for traffic and for transit
    /// timetables; a provider that ignores it still has to accept it.
    /// </param>
    Task<IReadOnlyList<RouteLeg>> ComputeMatrixAsync(
        IReadOnlyList<RoutePoint> origins,
        IReadOnlyList<RoutePoint> destinations,
        TravelMode mode,
        DateTime departureUtc,
        CancellationToken ct);
}
