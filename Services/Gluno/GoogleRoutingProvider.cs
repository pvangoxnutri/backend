using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Verified travel times from Google's official Routes API
/// (<c>:computeRouteMatrix</c>).
///
/// WHY GOOGLE. SideQuest's map integration is already Google (see
/// MapsController), so place coordinates and route coordinates come from the
/// same reference frame. A second vendor would mean two geocoders quietly
/// disagreeing about where a restaurant is.
///
/// WHY THE MATRIX ENDPOINT. Planning a day is not one question. Six stops in
/// an unknown order is thirty pairs; asking one at a time is thirty round
/// trips inside a chat turn. The matrix answers them in one call.
///
/// THE KEY. Sent as a header, never a query parameter, and the client is
/// registered with .RemoveAllLoggers() anyway. Nothing here logs a URI, a
/// request body, a response body, or a coordinate — only endpoint type,
/// element count, status class and duration.
///
/// SSRF. There is exactly one request site, and it builds its URL from the
/// CONFIGURED base plus a constant path. No caller — and certainly no model —
/// can supply a host, a path or a query.
/// </summary>
public sealed class GoogleRoutingProvider : IRoutingProvider
{
    public const string ProviderId = "google_routes";

    /// Named client, registered in Program.cs with .RemoveAllLoggers().
    public const string HttpClientName = "google-routes";

    private const string MatrixPath = "/distanceMatrix/v2:computeRouteMatrix";

    /// Only what we actually read. A narrow field mask is both cheaper on
    /// Google's billing tiers and less data crossing the boundary.
    private const string FieldMask =
        "originIndex,destinationIndex,duration,distanceMeters,condition,status";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<GoogleRoutingProvider> _logger;

    public GoogleRoutingProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<GoogleRoutingProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public string Provider => ProviderId;

    /// Falls back to the existing Maps key so routing can be switched on
    /// without provisioning a second credential — but only when Routing is
    /// explicitly enabled.
    private string? ApiKey => _config["Routing:ApiKey"] ?? _config["GoogleMaps:ApiKey"];

    private string BaseUrl => (_config["Routing:BaseUrl"] ?? "https://routes.googleapis.com").TrimEnd('/');

    /// <summary>
    /// Off unless explicitly switched on AND given a key over https.
    ///
    /// Defaults to FALSE deliberately, exactly like the Tripadvisor
    /// integration: deploying this code must never start spending money or
    /// calling a third party by itself. When this is false Gluno keeps
    /// planning on straight-line distances and is required to say that its
    /// travel times are unverified.
    /// </summary>
    public bool IsConfigured =>
        _config.GetValue("Routing:Enabled", false)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps;

    private TimeSpan Timeout
        => TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Routing:TimeoutSeconds", 8), 2, 20));

    /// <summary>
    /// Google caps a matrix at 625 elements, and at 100 for transit. We stay
    /// well under: a day plan never needs more, and a smaller ceiling bounds
    /// both cost and the blast radius of a bug that builds a matrix in a loop.
    /// </summary>
    public int MaxMatrixElements
        => Math.Clamp(_config.GetValue("Routing:MaxMatrixElements", 64), 4, 100);

    public async Task<IReadOnlyList<RouteLeg>> ComputeMatrixAsync(
        IReadOnlyList<RoutePoint> origins,
        IReadOnlyList<RoutePoint> destinations,
        TravelMode mode,
        DateTime departureUtc,
        CancellationToken ct)
    {
        if (!IsConfigured || origins.Count == 0 || destinations.Count == 0)
            return Array.Empty<RouteLeg>();

        // Coordinate validation before anything leaves the process. A NaN or a
        // latitude of 900 is a bug upstream, and sending it produces a 400 we
        // pay for.
        if (origins.Any(point => !point.IsValid()) || destinations.Any(point => !point.IsValid()))
        {
            _logger.LogWarning("[GLUNO] routing matrix rejected: invalid coordinates");
            return Array.Empty<RouteLeg>();
        }

        var elements = origins.Count * destinations.Count;
        if (elements > MaxMatrixElements)
        {
            _logger.LogWarning(
                "[GLUNO] routing matrix rejected: {Elements} elements exceeds {Max}", elements, MaxMatrixElements);
            return Array.Empty<RouteLeg>();
        }

        var startedAt = DateTime.UtcNow;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + MatrixPath)
            {
                Content = JsonContent.Create(BuildBody(origins, destinations, mode, departureUtc)),
            };
            request.Headers.Add("X-Goog-Api-Key", ApiKey!);
            request.Headers.Add("X-Goog-FieldMask", FieldMask);

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                // Status only. The body can echo the coordinates we sent, and
                // the request URI is not logged anywhere.
                _logger.LogWarning(
                    "[GLUNO] routing matrix failed status={Status} elements={Elements} in {Elapsed}ms",
                    (int)response.StatusCode, elements, elapsed);
                return Array.Empty<RouteLeg>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);

            var legs = ParseMatrix(document.RootElement, origins, destinations, mode);

            _logger.LogInformation(
                "[GLUNO] routing matrix ok mode={Mode} elements={Elements} resolved={Resolved} in {Elapsed}ms",
                TravelModes.ToWireValue(mode), elements, legs.Count, elapsed);

            return legs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[GLUNO] routing matrix timed out elements={Elements} after {Elapsed}ms",
                elements, (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);
            return Array.Empty<RouteLeg>();
        }
        catch (Exception ex)
        {
            // Category only: an exception message can carry the request URI.
            _logger.LogWarning("[GLUNO] routing matrix failed: {Category}", ex.GetType().Name);
            return Array.Empty<RouteLeg>();
        }
    }

    private object BuildBody(
        IReadOnlyList<RoutePoint> origins,
        IReadOnlyList<RoutePoint> destinations,
        TravelMode mode,
        DateTime departureUtc)
    {
        // Google rejects a departure time in the past. Planning a day that has
        // already started is legitimate, so clamp forward rather than fail —
        // for walking and cycling the time changes nothing anyway.
        var departure = departureUtc <= DateTime.UtcNow.AddMinutes(1)
            ? (DateTime?)null
            : DateTime.SpecifyKind(departureUtc, DateTimeKind.Utc);

        var body = new Dictionary<string, object?>
        {
            ["origins"] = origins.Select(ToWaypoint).ToArray(),
            ["destinations"] = destinations.Select(ToWaypoint).ToArray(),
            ["travelMode"] = ToGoogleMode(mode),
        };

        // TRAFFIC_AWARE is driving-only in the Routes API; sending it with
        // WALK is a 400.
        if (mode == TravelMode.Driving) body["routingPreference"] = "TRAFFIC_AWARE";

        if (departure != null)
        {
            body["departureTime"] = departure.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return body;
    }

    private static object ToWaypoint(RoutePoint point) => new
    {
        waypoint = new
        {
            location = new
            {
                latLng = new { latitude = point.Latitude, longitude = point.Longitude },
            },
        },
    };

    private static string ToGoogleMode(TravelMode mode) => mode switch
    {
        TravelMode.Driving => "DRIVE",
        TravelMode.Transit => "TRANSIT",
        TravelMode.Cycling => "BICYCLE",
        _ => "WALK",
    };

    /// <summary>
    /// Turns the flat element list into legs.
    ///
    /// Partial failures are normal and expected: an island with no ferry in
    /// the graph, or a transit query outside a covered region, comes back as a
    /// single element with condition ROUTE_NOT_FOUND while every other element
    /// is fine. Those elements are simply omitted — the caller fills the gap
    /// with a straight line and marks the leg unverified, rather than the
    /// whole day losing its verified travel times because one pair failed.
    /// </summary>
    private static List<RouteLeg> ParseMatrix(
        JsonElement root,
        IReadOnlyList<RoutePoint> origins,
        IReadOnlyList<RoutePoint> destinations,
        TravelMode mode)
    {
        var legs = new List<RouteLeg>();
        if (root.ValueKind != JsonValueKind.Array) return legs;

        foreach (var element in root.EnumerateArray())
        {
            // Missing indices default to 0 in this API — that is the documented
            // shape, not a guess on our part.
            var originIndex = ReadInt(element, "originIndex") ?? 0;
            var destinationIndex = ReadInt(element, "destinationIndex") ?? 0;

            if (originIndex < 0 || originIndex >= origins.Count) continue;
            if (destinationIndex < 0 || destinationIndex >= destinations.Count) continue;

            // A per-element `status` object means that element failed, even
            // though the HTTP call succeeded.
            if (element.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() != 0)
            {
                continue;
            }

            var condition = element.TryGetProperty("condition", out var conditionValue)
                && conditionValue.ValueKind == JsonValueKind.String
                    ? conditionValue.GetString()
                    : null;

            if (condition != null && !string.Equals(condition, "ROUTE_EXISTS", StringComparison.Ordinal)) continue;

            var seconds = ReadDurationSeconds(element);
            if (seconds == null) continue;

            var metres = ReadInt(element, "distanceMeters");

            legs.Add(new RouteLeg
            {
                Origin = origins[originIndex],
                Destination = destinations[destinationIndex],
                Mode = mode,
                // Round up: a 90-second walk is "2 minutes" to a traveller, and
                // rounding down systematically builds schedules that are late.
                DurationMinutes = Math.Max(1, (int)Math.Ceiling(seconds.Value / 60.0)),
                DistanceKm = metres.HasValue ? Math.Round(metres.Value / 1000.0, 2) : null,
                Source = ProviderId,
                Verified = true,
            });
        }

        return legs;
    }

    /// Routes API durations are protobuf strings: "832s".
    private static double? ReadDurationSeconds(JsonElement element)
    {
        if (!element.TryGetProperty("duration", out var duration)) return null;

        if (duration.ValueKind == JsonValueKind.Number) return duration.GetDouble();
        if (duration.ValueKind != JsonValueKind.String) return null;

        var text = duration.GetString()?.TrimEnd('s');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }
}
