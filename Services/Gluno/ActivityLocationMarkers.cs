using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One activity's location, as the app stores it.
/// </summary>
public sealed record ActivityLocation(
    string? Label,
    string? PlaceId,
    double? Latitude,
    double? Longitude);

/// <summary>
/// Reads the location an Activity carries in its description.
///
/// TripActivity has no location column. The app stores it as marker lines in
/// the description and parses them back out when rendering
/// (mobile/lib/sidequest-location.ts) — so this is not a hack around the
/// schema, it IS the schema, and the server has to speak the same format to
/// see what the user sees.
///
/// Why the backend needs it now: without coordinates, Gluno cannot tell that
/// a restaurant is on the wrong side of town from the rest of the day. Every
/// geographic finding in <see cref="TripAnalyzer"/> depends on this parse.
///
/// Deliberately forgiving. A malformed marker yields null rather than an
/// exception: a description is user-editable free text, and one bad line must
/// never break the context build.
/// </summary>
public static class ActivityLocationMarkers
{
    private const string LocationMarker = "[map-location]:";
    private const string PlaceMarker = "[map-place]:";
    // Legacy: activities briefly had a sub-location field. Nothing writes it
    // any more; it is stripped so the raw marker never reaches Gluno as prose.
    private const string SubLocationMarker = "[map-sublocation]:";

    public static ActivityLocation Read(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return new ActivityLocation(null, null, null, null);

        string? label = null;
        string? placeId = null;
        double? latitude = null;
        double? longitude = null;

        foreach (var rawLine in description.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith(LocationMarker, StringComparison.Ordinal))
            {
                label = Decode(line[LocationMarker.Length..].Trim());
                continue;
            }

            if (!line.StartsWith(PlaceMarker, StringComparison.Ordinal)) continue;

            var payload = Decode(line[PlaceMarker.Length..].Trim());
            if (string.IsNullOrWhiteSpace(payload)) continue;

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                placeId = ReadString(root, "placeId");
                latitude = ReadDouble(root, "latitude");
                longitude = ReadDouble(root, "longitude");
                label ??= ReadString(root, "name");
            }
            catch (JsonException)
            {
                // A half-written place marker is not worth failing over.
            }
        }

        // Only a complete, in-range pair counts. A half-set coordinate would
        // put an activity in the Gulf of Guinea and skew every distance.
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
        {
            latitude = null;
            longitude = null;
        }

        return new ActivityLocation(label, placeId, latitude, longitude);
    }

    /// <summary>
    /// The description with every marker line removed — what the user actually
    /// wrote. Gluno must never see (or repeat) the raw marker text.
    /// </summary>
    public static string? StripMarkers(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var lines = description
            .Split('\n')
            .Where(line =>
            {
                var trimmed = line.Trim();
                return !trimmed.StartsWith(LocationMarker, StringComparison.Ordinal)
                    && !trimmed.StartsWith(PlaceMarker, StringComparison.Ordinal)
                    && !trimmed.StartsWith(SubLocationMarker, StringComparison.Ordinal);
            })
            .Select(line => line.TrimEnd());

        var cleaned = string.Join('\n', lines).Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    private static string? Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }
}
