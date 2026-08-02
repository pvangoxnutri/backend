using System.Globalization;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Gluno's first external travel-data provider, on Tripadvisor's official
/// Content API.
///
/// SCOPE. Official REST endpoints only — location search, nearby search,
/// details, photos, reviews. No scraping, no HTML parsing, no undocumented
/// endpoint. Every request is built from the CONFIGURED base URL plus a fixed
/// relative path, so there is no code path where a URL from the model, the
/// user, or a provider response becomes a request target.
///
/// THE KEY. Tripadvisor's Content API takes the key as a query parameter,
/// which means every request URI contains a secret. Two consequences that must
/// not be undone:
///   1. the typed HttpClient is registered with .RemoveAllLoggers() in
///      Program.cs — the default HttpClientFactory logging prints request URIs
///   2. nothing in this file ever logs a URI, a query, or a response body;
///      only endpoint type, status class and duration
///
/// SHAPE OF A SEARCH. Tripadvisor's search endpoint returns identity only —
/// id, name, address. Ratings, review counts, price bands, hours and photos
/// all live behind per-location calls. So a search here is one search request
/// plus a bounded fan-out of detail (and optionally photo/review) requests,
/// all cached. The fan-out is capped by MaxDetailHydrations rather than by
/// the caller's limit, so a large limit cannot turn into a burst of upstream
/// calls.
///
/// MISSING DATA STAYS MISSING. Every parse below returns null rather than a
/// default when a field is absent. Gluno is explicitly not allowed to state a
/// rating, price, review or opening hour it was not given, and the cheapest
/// way to guarantee that is to never manufacture one here.
/// </summary>
public sealed class TripadvisorTravelProvider : ITravelDataProvider
{
    public const string ProviderId = "tripadvisor";

    /// Shown wherever a Tripadvisor result appears. Text rather than a logo:
    /// the project has no licensed Tripadvisor brand asset, and shipping an
    /// unlicensed one would be worse than plain attribution.
    public const string Attribution = "Data provided by Tripadvisor";

    /// Hosts a Tripadvisor URL is allowed to point at. Anything else is
    /// dropped rather than forwarded — a link the app opens must not be able
    /// to come from somewhere unexpected in a provider payload.
    private static readonly string[] AllowedLinkHostSuffixes =
    [
        "tripadvisor.com", "tripadvisor.co.uk", "tripadvisor.se", "tripadvisor.de",
        "tripadvisor.fr", "tripadvisor.es", "tripadvisor.it", "tripadvisor.nl",
        "tripadvisor.dk", "tripadvisor.no", "tripadvisor.fi", "tripadvisor.ca",
        "tripadvisor.com.au", "tripadvisor.in", "tripadvisor.jp",
        // Tripadvisor's media CDNs.
        "tacdn.com", "tripcdn.com",
    ];

    /// Named client, registered in Program.cs with .RemoveAllLoggers().
    public const string HttpClientName = "tripadvisor";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly TravelDataCache _cache;
    private readonly ILogger<TripadvisorTravelProvider> _logger;

    // A factory rather than an injected HttpClient: this provider is a
    // singleton (the registry that holds it is), and capturing a typed
    // HttpClient in a singleton pins one message handler for the process
    // lifetime, which is how DNS changes stop being picked up.
    public TripadvisorTravelProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        TravelDataCache cache,
        ILogger<TripadvisorTravelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public string Provider => ProviderId;

    private string? ApiKey => _config["Tripadvisor:ApiKey"];

    private string BaseUrl => (_config["Tripadvisor:BaseUrl"] ?? "https://api.content.tripadvisor.com").TrimEnd('/');

    /// <summary>
    /// Off unless explicitly switched on AND given a key.
    ///
    /// Enabled defaults to FALSE on purpose: shipping this code must not turn
    /// the integration on by itself. Production enables it by configuration,
    /// deliberately, or not at all.
    /// </summary>
    public bool IsConfigured =>
        _config.GetValue("Tripadvisor:Enabled", false)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// True — this integration was built around the Content API's own cache
    /// and detail-hydration model, and its search and detail caches predate
    /// the Terra terms.
    ///
    /// Left as it was rather than tightened speculatively: this provider is
    /// being retired (Content API keys stop working 2026-08-31), and changing
    /// its storage behaviour now would alter a working integration on a
    /// reading of terms that apply to the other product.
    /// </summary>
    public bool AllowsContentPersistence => true;

    /// True, like every provider that has an id worth keeping. Stated
    /// separately from content so the two permissions stay two questions.
    public bool AllowsLocationIdPersistence => true;

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("Tripadvisor:TimeoutSeconds", 6), 2, 20));
    private int MaxDetailHydrations => Math.Clamp(_config.GetValue("Tripadvisor:MaxDetailHydrations", 6), 1, 10);
    private bool IncludePhotos => _config.GetValue("Tripadvisor:IncludePhotos", true);
    /// Off by default: review text is a third call per place, and the summary
    /// is a nice-to-have next to rating and review count.
    private bool IncludeReviewSnippets => _config.GetValue("Tripadvisor:IncludeReviewSnippets", false);
    private TimeSpan SearchTtl => TimeSpan.FromMinutes(Math.Clamp(_config.GetValue("Tripadvisor:SearchCacheMinutes", 10), 1, 120));
    /// Details change far more slowly than what is currently popular, so they
    /// are cached an order of magnitude longer than searches.
    private TimeSpan DetailsTtl => TimeSpan.FromHours(Math.Clamp(_config.GetValue("Tripadvisor:DetailsCacheHours", 24), 1, 168));

    // ── Search ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
    {
        if (!IsConfigured) return Array.Empty<TravelPlace>();

        var cacheKey = TravelDataCache.BuildKey(ProviderId, "search", query);
        // The shared fetch runs on CancellationToken.None deliberately: it is
        // bounded by this provider's own timeout, and one caller walking away
        // must not cancel the call every other caller is waiting on. The
        // caller's token still governs how long THIS caller waits.
        var identities = await _cache.GetOrAddAsync(
            cacheKey, SearchTtl, () => FetchSearchIdentitiesAsync(query, CancellationToken.None), ct);

        if (identities == null || identities.Count == 0) return Array.Empty<TravelPlace>();

        // Cap the fan-out here rather than trusting the caller's limit: the
        // ranker needs a few more candidates than it returns, but not an
        // unbounded number of upstream calls.
        var candidates = identities.Take(Math.Min(MaxDetailHydrations, Math.Max(query.Limit, 3) + 2)).ToList();

        var hydrated = await Task.WhenAll(candidates.Select(async identity =>
        {
            var place = await GetPlaceDetailsAsync(identity.LocationId, query.Language, ct);
            // A detail call that failed still leaves a usable identity — name
            // and address are better than dropping the result entirely.
            return place ?? FromIdentity(identity);
        }));

        return hydrated
            .Where(place => place != null)
            .Select(place => WithDistance(place!, query))
            .ToList();
    }

    private sealed record SearchIdentity(string LocationId, string Name, string? Address);

    private async Task<List<SearchIdentity>?> FetchSearchIdentitiesAsync(TravelPlaceQuery query, CancellationToken ct)
    {
        var hasCoordinates = query.Latitude.HasValue && query.Longitude.HasValue;
        var hasText = !string.IsNullOrWhiteSpace(query.Query);

        // nearby_search is the right endpoint when we know WHERE but not
        // WHAT; location/search needs a search term.
        var path = hasCoordinates && !hasText ? "/api/v1/location/nearby_search" : "/api/v1/location/search";

        var parameters = new List<string> { $"key={Uri.EscapeDataString(ApiKey!)}" };

        if (hasText) parameters.Add($"searchQuery={Uri.EscapeDataString(BuildSearchTerm(query))}");
        if (hasCoordinates)
        {
            var latLong = string.Create(CultureInfo.InvariantCulture, $"{query.Latitude!.Value},{query.Longitude!.Value}");
            parameters.Add($"latLong={Uri.EscapeDataString(latLong)}");

            if (query.RadiusKm is { } radius)
            {
                parameters.Add($"radius={radius.ToString("0.##", CultureInfo.InvariantCulture)}");
                parameters.Add("radiusUnit=km");
            }
        }
        else if (!string.IsNullOrWhiteSpace(query.Near))
        {
            // No coordinates: the area becomes part of the search term, which
            // is the only place-scoping the text endpoint offers.
            parameters.Add($"address={Uri.EscapeDataString(query.Near)}");
        }

        var category = ToTripadvisorCategory(query.Category);
        if (category != null) parameters.Add($"category={category}");
        parameters.Add($"language={Uri.EscapeDataString(ToTripadvisorLanguage(query.Language))}");

        using var document = await SendAsync(path, parameters, hasCoordinates && !hasText ? "nearby_search" : "search", ct);
        if (document == null) return null;

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new List<SearchIdentity>();

        var results = new List<SearchIdentity>();
        foreach (var item in data.EnumerateArray())
        {
            var locationId = ReadString(item, "location_id");
            var name = ReadString(item, "name");
            if (locationId == null || name == null) continue;

            results.Add(new SearchIdentity(locationId, name, ReadAddress(item)));
        }

        return results;
    }

    /// The free-text term sent upstream.
    ///
    /// Only the search words and, when we have no coordinates, the area. The
    /// traveller's plan, their trip, their companions and everything else in
    /// the Gluno context stay on this side of the boundary — a place provider
    /// needs to know what to look for and roughly where, and nothing else.
    private static string BuildSearchTerm(TravelPlaceQuery query)
    {
        var hasCoordinates = query.Latitude.HasValue && query.Longitude.HasValue;
        var parts = new List<string> { query.Query.Trim() };

        if (!hasCoordinates && !string.IsNullOrWhiteSpace(query.Near)) parts.Add(query.Near.Trim());

        return string.Join(' ', parts.Where(part => part.Length > 0));
    }

    // ── Details ───────────────────────────────────────────────────────────

    public async Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        if (!IsSafeLocationId(providerPlaceId)) return null;

        var cacheKey = TravelDataCache.BuildDetailsKey(ProviderId, providerPlaceId, language);
        return await _cache.GetOrAddAsync(
            cacheKey, DetailsTtl, () => FetchDetailsAsync(providerPlaceId, language, CancellationToken.None), ct);
    }

    private async Task<TravelPlace?> FetchDetailsAsync(string locationId, string language, CancellationToken ct)
    {
        var parameters = new List<string>
        {
            $"key={Uri.EscapeDataString(ApiKey!)}",
            $"language={Uri.EscapeDataString(ToTripadvisorLanguage(language))}",
        };

        using var document = await SendAsync($"/api/v1/location/{locationId}/details", parameters, "details", ct);
        if (document == null) return null;

        var root = document.RootElement;
        var name = ReadString(root, "name");
        if (name == null) return null;

        // Ratings and review counts arrive as strings in this API; a failed
        // parse yields null rather than 0, which would read as "rated zero".
        var rating = ReadDoubleFromStringOrNumber(root, "rating");
        var reviewCount = ReadIntFromStringOrNumber(root, "num_reviews");

        var openingHours = ReadOpeningHours(root);
        var imageUrl = IncludePhotos ? await FetchPrimaryPhotoAsync(locationId, language, ct) : null;
        var reviewSummary = IncludeReviewSnippets ? await FetchReviewSnippetAsync(locationId, language, ct) : null;

        return new TravelPlace
        {
            Provider = ProviderId,
            ExternalId = TravelPlaceIds.Namespaced(ProviderId, locationId),
            ProviderPlaceId = locationId,
            Name = name,
            Category = NormaliseCategory(root),
            CategoryLabel = ReadNestedString(root, "category", "localized_name") ?? ReadNestedString(root, "category", "name"),
            Address = ReadAddress(root),
            Latitude = ReadDoubleFromStringOrNumber(root, "latitude"),
            Longitude = ReadDoubleFromStringOrNumber(root, "longitude"),
            Rating = rating,
            // Only meaningful when there is a rating to scale.
            RatingScaleMax = rating.HasValue ? 5 : null,
            ReviewCount = reviewCount,
            PriceLevel = ReadString(root, "price_level"),
            ImageUrl = imageUrl,
            ProviderUrl = SafeLink(ReadString(root, "web_url")),
            SourceAttribution = Attribution,
            // The Content API integration was built around its own cache; its
            // results stay storable. See AllowsContentPersistence.
            AllowsContentPersistence = true,
            AllowsIdentityPersistence = true,
            OpeningHours = openingHours,
            // Stamped with when we fetched it, so the schedule engine can
            // refuse to quote hours that have gone stale.
            Hours = Services.Gluno.OpeningHours.FromTripadvisor(root, DateTime.UtcNow),
            ReviewSummary = reviewSummary,
        };
    }

    private async Task<string?> FetchPrimaryPhotoAsync(string locationId, string language, CancellationToken ct)
    {
        var parameters = new List<string>
        {
            $"key={Uri.EscapeDataString(ApiKey!)}",
            $"language={Uri.EscapeDataString(ToTripadvisorLanguage(language))}",
            "limit=1",
        };

        using var document = await SendAsync($"/api/v1/location/{locationId}/photos", parameters, "photos", ct);
        if (document == null) return null;
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        foreach (var photo in data.EnumerateArray())
        {
            if (!photo.TryGetProperty("images", out var images)) continue;

            // Prefer a size that looks good on a phone card without pulling a
            // full-resolution asset over a mobile connection.
            foreach (var size in new[] { "medium", "large", "original", "small", "thumbnail" })
            {
                var url = SafeLink(ReadNestedString(images, size, "url"));
                if (url != null) return url;
            }
        }

        return null;
    }

    /// <summary>
    /// One short review snippet, when the integration is configured to include
    /// them. Truncated, and never attributed to a named reviewer — the card
    /// shows a flavour of what people say, not somebody's post.
    /// </summary>
    private async Task<string?> FetchReviewSnippetAsync(string locationId, string language, CancellationToken ct)
    {
        var parameters = new List<string>
        {
            $"key={Uri.EscapeDataString(ApiKey!)}",
            $"language={Uri.EscapeDataString(ToTripadvisorLanguage(language))}",
            "limit=1",
        };

        using var document = await SendAsync($"/api/v1/location/{locationId}/reviews", parameters, "reviews", ct);
        if (document == null) return null;
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        foreach (var review in data.EnumerateArray())
        {
            var text = ReadString(review, "text") ?? ReadString(review, "title");
            if (string.IsNullOrWhiteSpace(text)) continue;

            var trimmed = text.Trim();
            return trimmed.Length <= 220 ? trimmed : trimmed[..217].TrimEnd() + "…";
        }

        return null;
    }

    // ── HTTP ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The single place a request leaves for Tripadvisor.
    ///
    /// Every caller passes a FIXED relative path and a parameter list; the
    /// host always comes from configuration. No caller can supply an absolute
    /// URL, which is what makes SSRF impossible here by construction rather
    /// than by validation.
    ///
    /// No retries, by design: a provider hiccup should degrade Gluno's answer,
    /// not multiply load on a rate-limited API while the user waits.
    /// </summary>
    private async Task<JsonDocument?> SendAsync(
        string path, IReadOnlyList<string> parameters, string endpointType, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}?{string.Join('&', parameters)}");
            request.Headers.Accept.ParseAdd("application/json");

            // Some Content API keys are locked to a referrer; harmless when
            // unset.
            var referer = _config["Tripadvisor:Referer"];
            if (!string.IsNullOrWhiteSpace(referer)) request.Headers.Referrer = new Uri(referer);

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                // Status and endpoint type only. Never the URI (it carries the
                // key), never the response body (it can echo the query).
                _logger.LogWarning(
                    "[GLUNO] tripadvisor {Endpoint} failed status={Status} in {Elapsed}ms",
                    endpointType, (int)response.StatusCode, elapsed);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);

            _logger.LogInformation(
                "[GLUNO] tripadvisor {Endpoint} ok in {Elapsed}ms", endpointType, elapsed);
            return document;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away — not a provider failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[GLUNO] tripadvisor {Endpoint} timed out after {Elapsed}ms",
                endpointType, (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            // Category only — the exception message can contain the request
            // URI, and the URI contains the API key.
            _logger.LogWarning(
                "[GLUNO] tripadvisor {Endpoint} failed: {Category}", endpointType, ex.GetType().Name);
            return null;
        }
    }

    // ── Parsing helpers ───────────────────────────────────────────────────

    private static TravelPlace FromIdentity(SearchIdentity identity) => new()
    {
        Provider = ProviderId,
        ExternalId = TravelPlaceIds.Namespaced(ProviderId, identity.LocationId),
        ProviderPlaceId = identity.LocationId,
        Name = identity.Name,
        Category = TravelPlaceCategories.ToWireValue(TravelPlaceCategory.General),
        Address = identity.Address,
        AllowsContentPersistence = true,
        AllowsIdentityPersistence = true,
        SourceAttribution = Attribution,
    };

    private static TravelPlace WithDistance(TravelPlace place, TravelPlaceQuery query)
    {
        var distance = GeoDistance.KilometresBetween(
            query.Latitude, query.Longitude, place.Latitude, place.Longitude);

        return distance == null ? place : new TravelPlace
        {
            Provider = place.Provider,
            ExternalId = place.ExternalId,
            ProviderPlaceId = place.ProviderPlaceId,
            Name = place.Name,
            Category = place.Category,
            CategoryLabel = place.CategoryLabel,
            Address = place.Address,
            Latitude = place.Latitude,
            Longitude = place.Longitude,
            Rating = place.Rating,
            RatingScaleMax = place.RatingScaleMax,
            ReviewCount = place.ReviewCount,
            PriceLevel = place.PriceLevel,
            ImageUrl = place.ImageUrl,
            ProviderUrl = place.ProviderUrl,
            SourceAttribution = place.SourceAttribution,
            AllowsContentPersistence = place.AllowsContentPersistence,
            AllowsIdentityPersistence = place.AllowsIdentityPersistence,
            OpeningHours = place.OpeningHours,
            Hours = place.Hours,
            ReviewSummary = place.ReviewSummary,
            DistanceKm = distance,
        };
    }

    private static string? ToTripadvisorCategory(TravelPlaceCategory category) => category switch
    {
        TravelPlaceCategory.Restaurant => "restaurants",
        TravelPlaceCategory.Attraction => "attractions",
        TravelPlaceCategory.Hotel => "hotels",
        _ => null,
    };

    private static string NormaliseCategory(JsonElement root)
    {
        var raw = ReadNestedString(root, "category", "name")?.ToLowerInvariant();
        return raw switch
        {
            "restaurant" => "restaurant",
            "attraction" => "attraction",
            "hotel" or "accommodation" or "lodging" => "hotel",
            _ => "general",
        };
    }

    /// Tripadvisor only returns hours for some listings, and returns them as
    /// pre-formatted weekday lines. Absent means absent — nothing is derived.
    private static IReadOnlyList<string> ReadOpeningHours(JsonElement root)
    {
        if (!root.TryGetProperty("hours", out var hours)) return Array.Empty<string>();
        if (!hours.TryGetProperty("weekday_text", out var weekdays) || weekdays.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var lines = new List<string>();
        foreach (var line in weekdays.EnumerateArray())
        {
            var value = line.ValueKind == JsonValueKind.String ? line.GetString() : null;
            if (!string.IsNullOrWhiteSpace(value)) lines.Add(value.Trim());
        }

        return lines;
    }

    private static string? ReadAddress(JsonElement element)
    {
        if (!element.TryGetProperty("address_obj", out var address)) return null;
        return ReadString(address, "address_string")
            ?? string.Join(", ", new[] { ReadString(address, "street1"), ReadString(address, "city") }
                .Where(part => !string.IsNullOrWhiteSpace(part)))
                .NullIfEmpty();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? ReadNestedString(JsonElement element, string outer, string inner)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(outer, out var nested)) return null;
        return ReadString(nested, inner);
    }

    private static double? ReadDoubleFromStringOrNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? ReadIntFromStringOrNumber(JsonElement element, string name)
    {
        var value = ReadDoubleFromStringOrNumber(element, name);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    /// <summary>
    /// Only https URLs on a Tripadvisor-owned host survive.
    ///
    /// The app opens these links and renders these images, so a URL from a
    /// provider payload is treated as untrusted input like any other — an
    /// unexpected host is dropped, not forwarded.
    /// </summary>
    private static string? SafeLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        var allowed = AllowedLinkHostSuffixes.Any(suffix =>
            host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal));

        return allowed ? uri.ToString() : null;
    }

    /// Location ids are numeric in this API. Validating the shape keeps a
    /// malformed id out of the request path entirely.
    private static bool IsSafeLocationId(string? locationId)
        => !string.IsNullOrWhiteSpace(locationId)
           && locationId.Length <= 24
           && locationId.All(char.IsAsciiDigit);

    private static string ToTripadvisorLanguage(string language)
        => language.StartsWith("sv", StringComparison.OrdinalIgnoreCase) ? "sv" : "en";
}

internal static class TripadvisorStringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
