using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Dtos;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/maps")]
public class MapsController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public MapsController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [HttpGet("autocomplete")]
    [Authorize]
    public async Task<ActionResult<List<PlaceAutocompleteSuggestionDto>>> Autocomplete([FromQuery] string input)
    {
        var apiKey = _configuration["GoogleMaps:ApiKey"];

        var query = input?.Trim() ?? string.Empty;
        if (query.Length < 2)
            return Ok(new List<PlaceAutocompleteSuggestionDto>());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(await AutocompleteFromOpenStreetMap(query));
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:autocomplete");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", "suggestions.placePrediction.placeId,suggestions.placePrediction.text.text,suggestions.placePrediction.structuredFormat");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                input = query,
                languageCode = "en"
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return Ok(await AutocompleteFromOpenStreetMap(query));

        await using var stream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: HttpContext.RequestAborted);

        var result = new List<PlaceAutocompleteSuggestionDto>();
        if (!doc.RootElement.TryGetProperty("suggestions", out var suggestions) || suggestions.ValueKind != JsonValueKind.Array)
            return Ok(result);

        foreach (var item in suggestions.EnumerateArray())
        {
            if (!item.TryGetProperty("placePrediction", out var pred))
                continue;

            var placeId = pred.TryGetProperty("placeId", out var placeIdEl) ? placeIdEl.GetString() : null;
            var primary = pred.TryGetProperty("text", out var textEl) && textEl.TryGetProperty("text", out var primaryEl)
                ? primaryEl.GetString()
                : null;

            string? secondary = null;
            if (pred.TryGetProperty("structuredFormat", out var sf) &&
                sf.TryGetProperty("secondaryText", out var st) &&
                st.TryGetProperty("text", out var secondaryEl))
            {
                secondary = secondaryEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(placeId) || string.IsNullOrWhiteSpace(primary))
                continue;

            result.Add(new PlaceAutocompleteSuggestionDto
            {
                PlaceId = placeId!,
                PrimaryText = primary!,
                SecondaryText = secondary,
                GoogleMapsUri = $"https://www.google.com/maps/search/?api=1&query_place_id={Uri.EscapeDataString(placeId!)}"
            });
        }

        return Ok(result);
    }

    [HttpGet("place/{placeId}")]
    [Authorize]
    public async Task<ActionResult<PlaceDetailsDto>> GetPlace(string placeId)
    {
        var apiKey = _configuration["GoogleMaps:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return BadRequest("Google Maps API key is not configured.");

        var id = placeId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing placeId.");

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(id)}?languageCode=en&regionCode=SE");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", "id,displayName.text,formattedAddress,location,googleMapsUri");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return NotFound("Place not found.");

        await using var stream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: HttpContext.RequestAborted);
        var root = doc.RootElement;

        var name = root.TryGetProperty("displayName", out var dn) && dn.TryGetProperty("text", out var dnText)
            ? dnText.GetString()
            : null;
        var address = root.TryGetProperty("formattedAddress", out var fa) ? fa.GetString() : null;
        var googleMapsUri = root.TryGetProperty("googleMapsUri", out var gm) ? gm.GetString() : null;
        double? lat = null;
        double? lng = null;
        if (root.TryGetProperty("location", out var loc))
        {
            if (loc.TryGetProperty("latitude", out var latEl)) lat = latEl.GetDouble();
            if (loc.TryGetProperty("longitude", out var lngEl)) lng = lngEl.GetDouble();
        }

        return Ok(new PlaceDetailsDto
        {
            PlaceId = id!,
            Name = name ?? address ?? id!,
            Address = address,
            Latitude = lat,
            Longitude = lng,
            GoogleMapsUri = googleMapsUri
        });
    }

    private async Task<List<PlaceAutocompleteSuggestionDto>> AutocompleteFromOpenStreetMap(string query)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&addressdetails=1&q={Uri.EscapeDataString(query)}");
        request.Headers.UserAgent.ParseAdd("SideQuest/1.0 (local-dev)");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return new List<PlaceAutocompleteSuggestionDto>();

        await using var stream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: HttpContext.RequestAborted);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<PlaceAutocompleteSuggestionDto>();

        var items = new List<PlaceAutocompleteSuggestionDto>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var displayName = row.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            var parts = displayName.Split(',', 2, StringSplitOptions.TrimEntries);
            var primary = parts.Length > 0 ? parts[0] : displayName;
            var secondary = parts.Length > 1 ? parts[1] : null;

            double? lat = null;
            double? lon = null;
            if (row.TryGetProperty("lat", out var latEl) && double.TryParse(latEl.GetString(), out var latVal))
                lat = latVal;
            if (row.TryGetProperty("lon", out var lonEl) && double.TryParse(lonEl.GetString(), out var lonVal))
                lon = lonVal;

            var osmType = row.TryGetProperty("osm_type", out var ot) ? ot.GetString() : "n";
            var osmId = row.TryGetProperty("osm_id", out var oi) ? oi.GetRawText() : Guid.NewGuid().ToString("N");
            var placeId = $"osm:{osmType}:{osmId}";

            string? gm = null;
            if (lat.HasValue && lon.HasValue)
            {
                gm = $"https://www.google.com/maps/search/?api=1&query={lat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lon.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }
            else
            {
                gm = $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(displayName)}";
            }

            items.Add(new PlaceAutocompleteSuggestionDto
            {
                PlaceId = placeId,
                PrimaryText = primary ?? displayName,
                SecondaryText = secondary,
                Latitude = lat,
                Longitude = lon,
                GoogleMapsUri = gm
            });
        }

        return items;
    }
}
