using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How a Terra call ended, for diagnostics only.
///
/// Never reaches a user. The point is that four different empty results —
/// switched off, rejected, timed out, genuinely nothing there — stop looking
/// identical in the logs, which is exactly what made the last outage take a
/// day to find.
/// </summary>
public enum TerraFailure
{
    None,
    NotConfigured,
    Unauthorized,
    Forbidden,
    RateLimited,
    QuotaExceeded,
    Timeout,
    InvalidRequest,
    EmptyResult,
    DeserializationFailed,
    ProviderContractChanged,
    MappingFailed,
    Network,
}

/// <summary>
/// Tripadvisor Terra — the platform that replaces the Content API.
///
/// WHY A SECOND PROVIDER RATHER THAN AN EDIT. Terra is a different product
/// with a different base URL, a different authentication scheme and a
/// different response shape, on a separate account with its own key. Editing
/// the Content API provider in place would create one class where a Terra key
/// could be sent to the legacy host as a query parameter — a live secret in a
/// URL, on a service that would reject it anyway. Two classes and two
/// configuration namespaces make that mistake unrepresentable.
///
/// THE ENDPOINT. `POST /recommendations/search` takes a free-text query and a
/// geography and returns whole location objects — coordinates, categories,
/// opening hours, ratings, rankings — plus the review snippets behind each
/// recommendation. Tripadvisor documents it as "optimized for AI agent
/// consumption", which is precisely the shape of "what should we see in
/// Sevilla?".
///
/// The alternatives are worse for this. `catalog/locations/search` is not
/// allowlist-limited but returns a lightweight projection with no hours and no
/// photos; `locations/search` returns everything but is "restricted to
/// Locations you are licensed to access", which cannot answer about a city
/// nobody registered in advance.
///
/// THE KEY IS A HEADER HERE. On the Content API it was a query parameter,
/// which is why that provider has its HTTP loggers removed — its URIs were
/// secrets. Terra's URIs are safe; the HEADER is the secret, and nothing below
/// logs headers.
/// </summary>
public sealed class TerraTravelProvider : ITravelDataProvider
{
    public const string ProviderId = "tripadvisor";
    public const string HttpClientName = "tripadvisor-terra";

    /// Shown wherever a result appears. Attribution travels with the
    /// individual place, never with the batch.
    private const string Attribution = "Data provided by Tripadvisor";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<TerraTravelProvider> _logger;

    public TerraTravelProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<TerraTravelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public string Provider => ProviderId;

    private string? ApiKey => _config["TripadvisorTerra:ApiKey"];

    private string BaseUrl =>
        (_config["TripadvisorTerra:BaseUrl"] ?? "https://terra.tripadvisor.com/api").TrimEnd('/');

    /// <summary>
    /// Off unless switched on AND given a key AND pointed at an https host.
    ///
    /// Enabled defaults to false for the same reason the legacy provider's
    /// does: deploying this code must not start calling a metered API by
    /// itself.
    /// </summary>
    public bool IsConfigured =>
        _config.GetValue("TripadvisorTerra:Enabled", false)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
        && baseUri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// FALSE, and this is a deliberate reading of Tripadvisor's terms.
    ///
    /// Terra's caching policy states that "caching, copying, downloading,
    /// storing or indexing content is not permitted for any content", and
    /// allows only the Location ID to be kept "solely to improve the speed of
    /// your application".
    ///
    /// THE CONSEQUENCE, STATED PLAINLY. Gluno persists place cards into a
    /// message payload so a recommendation survives a reload and stays
    /// tappable. Under this flag that persistence is reduced to what the policy
    /// allows, which means a reopened conversation shows the answer text but
    /// not the full cards. That is a real loss of behaviour and it is the
    /// honest reading — the alternative is storing licensed content because it
    /// is convenient.
    ///
    /// It does NOT mean "Add" stops working. The location id may be kept, and
    /// keeping it is enough to ask this provider for the same place again at
    /// the moment somebody acts on it — see GlunoPlaceRehydrator.
    ///
    /// If the SideQuest contract grants broader rights, flipping this to true
    /// restores the previous behaviour and nothing else changes.
    /// </summary>
    public bool AllowsContentPersistence => ContentMayBeStored;

    /// <summary>
    /// TRUE, and from the same paragraph as the flag above.
    ///
    /// The policy's one carve-out: the Location ID may be stored "solely to
    /// improve the speed of your application". An id carries no presentation
    /// text, so keeping it publishes nothing.
    /// </summary>
    public bool AllowsLocationIdPersistence => LocationIdMayBeStored;

    /// The single source for each decision, so the instance properties and the
    /// static mapper below cannot disagree.
    private const bool ContentMayBeStored = false;
    private const bool LocationIdMayBeStored = true;

    private TimeSpan Timeout => TimeSpan.FromSeconds(
        Math.Clamp(_config.GetValue("TripadvisorTerra:TimeoutSeconds", 8), 2, 20));

    private int MaxResults => Math.Clamp(_config.GetValue("TripadvisorTerra:MaxResults", 6), 1, 10);

    // ── Capabilities, for the status endpoint ─────────────────────────────

    /// What this integration actually calls. Photos and reviews are separate
    /// endpoints that this build does not use — recommendations already
    /// carries review snippets, and photos would be one extra call per place.
    public bool SupportsRecommendationsSearch => true;
    public bool SupportsPhotos => false;
    public bool SupportsReviews => true;
    public bool SupportsOpeningHours => true;

    // ── Search ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(
        TravelPlaceQuery query, CancellationToken ct)
        => (await SearchPlacesWithStatusAsync(query, ct)).Places;

    /// <summary>
    /// The search, with the failure category translated into the vocabulary a
    /// caller can act on.
    ///
    /// The mapping is deliberately blunt: only a rate limit is transient enough
    /// to be worth telling somebody to try again. A rejected key and a changed
    /// contract both need a person, and neither is the user's problem.
    /// </summary>
    public async Task<TravelSearchResult> SearchPlacesWithStatusAsync(
        TravelPlaceQuery query, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            Log(TerraFailure.NotConfigured, query, 0, 0, 0, null);
            return Empty(TravelSearchStatus.Failed);
        }

        var startedAt = DateTime.UtcNow;

        // ── Where ─────────────────────────────────────────────────────────
        //
        // The area comes from SideQuest's own route resolution — the same day
        // locations the weather screen reads. Terra takes a geography by name;
        // without one it would search the world.
        var geoName = query.Near?.Trim();

        if (string.IsNullOrWhiteSpace(geoName))
        {
            // Coordinates alone are not a geography here. Rather than guess a
            // city name from a latitude, this returns nothing and says why.
            Log(TerraFailure.InvalidRequest, query, 0, 0, Elapsed(startedAt), null);
            return Empty(TravelSearchStatus.Failed);
        }

        var body = JsonSerializer.Serialize(new
        {
            query = BuildQueryText(query),
            geo = new { name = geoName },
            limit = Math.Min(Math.Max(query.Limit, 1), MaxResults),
            // Full location objects rather than the fastest possible answer:
            // the cards need hours and ratings, and a second call to fill them
            // in would cost more than the extra latency here.
            response_preference = "quality",
            top_level_categories = ToTerraCategories(query.Category),
        }, GlunoJson.Options);

        JsonDocument? document;
        TerraFailure failure;

        (document, failure) = await SendAsync("/recommendations/search", body, ct);

        if (document == null)
        {
            Log(failure, query, 0, 0, Elapsed(startedAt), null);

            // A rate limit is the one failure that means "later", so it is the
            // one the caller is told apart from the rest.
            return Empty(failure is TerraFailure.RateLimited or TerraFailure.QuotaExceeded
                ? TravelSearchStatus.RateLimited
                : TravelSearchStatus.Failed);
        }

        using (document)
        {
            var raw = 0;
            var places = new List<TravelPlace>();

            try
            {
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    // The envelope is not what the contract says. Distinct from
                    // an empty result: one is a Tripadvisor change, the other
                    // is an answer about Sevilla.
                    Log(TerraFailure.ProviderContractChanged, query, 0, 0, Elapsed(startedAt), null);
                    return Empty(TravelSearchStatus.Failed);
                }

                foreach (var item in data.EnumerateArray())
                {
                    raw++;

                    var place = MapRecommendation(item, query.Language);
                    if (place != null) places.Add(place);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                Log(TerraFailure.MappingFailed, query, raw, 0, Elapsed(startedAt), null);
                return Empty(TravelSearchStatus.Failed);
            }

            Log(
                places.Count == 0 ? (raw == 0 ? TerraFailure.EmptyResult : TerraFailure.MappingFailed)
                    : TerraFailure.None,
                query, raw, places.Count, Elapsed(startedAt), null);

            // Terra answered. An empty list here is a real answer about the
            // place that was searched, not a failure.
            return new TravelSearchResult { Places = places, Status = TravelSearchStatus.Ok };
        }
    }

    private static TravelSearchResult Empty(TravelSearchStatus status)
        => new() { Places = Array.Empty<TravelPlace>(), Status = status };

    /// <summary>
    /// Details for one place.
    ///
    /// NOT IMPLEMENTED against `/locations/{id}`, deliberately. That endpoint
    /// is governed by the account allowlist — "the allowlist governs what the
    /// content endpoints (Location Details, Reviews, Photos, and data feeds)
    /// will return for you" — and calling it for a place discovered through
    /// recommendations would either 404 or quietly require registering every
    /// city somebody asks about.
    ///
    /// The recommendation response already carries what a detail card shows,
    /// so nothing is lost by not calling it.
    /// </summary>
    public Task<TravelPlace?> GetPlaceDetailsAsync(
        string providerPlaceId, string language, CancellationToken ct)
        => Task.FromResult<TravelPlace?>(null);

    // ── The request ───────────────────────────────────────────────────────

    /// <summary>
    /// The free-text query sent upstream.
    ///
    /// Only what to look for. The traveller's plan, their companions, their
    /// budget and everything else in the Gluno context stay on this side of
    /// the boundary — a place provider needs to know what kind of thing and
    /// roughly where, and nothing else.
    ///
    /// The user's own sentence is NOT forwarded. It can contain anything, and
    /// a category plus their search words is both safer and a better query.
    /// </summary>
    public static string BuildQueryText(TravelPlaceQuery query)
    {
        var words = query.Query?.Trim() ?? string.Empty;

        var intent = query.Category switch
        {
            TravelPlaceCategory.Restaurant => "restaurants and places to eat",
            TravelPlaceCategory.Attraction => "top attractions and things to see",
            TravelPlaceCategory.Hotel => "hotels and places to stay",
            _ => "things to do",
        };

        // The user's search words first when they gave any — "rooftop bar"
        // beats the generic phrase — with the intent as the fallback.
        return words.Length > 0 ? $"{words} {intent}" : intent;
    }

    /// Terra's own category vocabulary. Null means no filter, which is right
    /// for a general request.
    public static string[]? ToTerraCategories(TravelPlaceCategory category) => category switch
    {
        TravelPlaceCategory.Restaurant => ["RESTAURANT"],
        TravelPlaceCategory.Attraction => ["ATTRACTION"],
        TravelPlaceCategory.Hotel => ["HOTEL"],
        _ => null,
    };

    // ── The response ──────────────────────────────────────────────────────

    /// <summary>
    /// One recommendation into a place, or null when it is not usable.
    ///
    /// THE ONLY THINGS THAT DISQUALIFY A RESULT are a missing id and a missing
    /// name. Everything else is optional and a gap in it is recorded as null
    /// rather than as a zero — a caller must be able to tell "the provider did
    /// not return this" from "the provider says it is zero", because Gluno is
    /// forbidden from inventing ratings, prices and opening hours.
    /// </summary>
    public static TravelPlace? MapRecommendation(JsonElement item, string language)
    {
        // "experience" results are bookable tours, not places on a map. Only
        // locations become place cards.
        if (!item.TryGetProperty("location", out var location)
            || location.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadId(location, "id");
        var name = ReadLocalised(location, "names", language);

        if (id == null || string.IsNullOrWhiteSpace(name)) return null;

        var (rating, reviewCount) = ReadRatings(location);
        var (latitude, longitude) = ReadCoordinates(location);

        return new TravelPlace
        {
            Provider = ProviderId,
            ExternalId = TravelPlaceIds.Namespaced(ProviderId, id),
            ProviderPlaceId = id,
            Name = name!,
            Category = ReadCategory(location),
            CategoryLabel = ReadCategoryLabel(location, language),
            Address = ReadLocalised(location, "addresses", language),
            Latitude = latitude,
            Longitude = longitude,
            Rating = rating,
            // Terra reports on a 5-point scale, like the Content API did.
            RatingScaleMax = rating.HasValue ? 5 : null,
            ReviewCount = reviewCount,
            // Not in the recommendations payload. Absent rather than guessed —
            // a photo of the wrong building is worse than none.
            ImageUrl = null,
            ProviderUrl = ReadUrl(location),
            SourceAttribution = Attribution,
            // Stamped here because this provider is the only thing that knows
            // its own terms. Terra permits storing the location id and nothing
            // else — two permissions, two flags.
            AllowsContentPersistence = ContentMayBeStored,
            AllowsIdentityPersistence = LocationIdMayBeStored,
            OpeningHours = ReadOpeningHours(location),
            ReviewSummary = ReadReviewSnippet(item),
        };
    }

    private static string? ReadId(JsonElement location, string name)
    {
        if (!location.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    /// <summary>
    /// A localised array field — names, addresses, descriptions.
    ///
    /// Terra returns these as arrays of {language, value} rather than as
    /// strings. The requested language wins; otherwise the first entry, because
    /// a name in the wrong language is far better than no name.
    /// </summary>
    public static string? ReadLocalised(JsonElement parent, string property, string language)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            // Some fields are plain strings depending on the shape.
            return parent.TryGetProperty(property, out var direct) && direct.ValueKind == JsonValueKind.String
                ? direct.GetString()
                : null;
        }

        string? first = null;

        foreach (var entry in array.EnumerateArray())
        {
            var value = entry.ValueKind == JsonValueKind.String
                ? entry.GetString()
                : Text(entry, "value") ?? Text(entry, "address_string") ?? Text(entry, "name");

            if (string.IsNullOrWhiteSpace(value)) continue;

            first ??= value;

            var entryLanguage = Text(entry, "language");

            if (entryLanguage != null
                && entryLanguage.StartsWith(language, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return first;
    }

    private static (double? Rating, int? ReviewCount) ReadRatings(JsonElement location)
    {
        if (!location.TryGetProperty("traveler_ratings", out var ratings)
            || ratings.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        double? overall = ratings.TryGetProperty("overall", out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

        int? count = ratings.TryGetProperty("count", out var countValue)
            && countValue.ValueKind == JsonValueKind.Number
                ? countValue.GetInt32()
                : null;

        return (overall, count);
    }

    private static (double? Latitude, double? Longitude) ReadCoordinates(JsonElement location)
    {
        if (!location.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var latitude = Number(coordinates, "latitude") ?? Number(coordinates, "lat");
        var longitude = Number(coordinates, "longitude") ?? Number(coordinates, "lng");

        // A half-supplied pair is worse than none — it would reach a routing
        // provider as a real point.
        return latitude.HasValue && longitude.HasValue ? (latitude, longitude) : (null, null);
    }

    /// <summary>
    /// Terra's category hierarchy onto SideQuest's four-value vocabulary.
    ///
    /// Unknown maps to general rather than being dropped: the place is still
    /// real, and a category nobody recognises is not a reason to hide it.
    /// </summary>
    private static string ReadCategory(JsonElement location)
    {
        if (!location.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array)
        {
            return TravelPlaceCategories.ToWireValue(TravelPlaceCategory.General);
        }

        foreach (var category in categories.EnumerateArray())
        {
            var name = Text(category, "name")?.ToLowerInvariant()
                ?? ReadLocalised(category, "names", "en")?.ToLowerInvariant();

            var mapped = name switch
            {
                not null when name.Contains("restaurant") || name.Contains("food")
                    => TravelPlaceCategory.Restaurant,
                not null when name.Contains("hotel") || name.Contains("lodging")
                    => TravelPlaceCategory.Hotel,
                not null when name.Contains("attraction") || name.Contains("sight")
                    || name.Contains("museum") || name.Contains("landmark")
                    => TravelPlaceCategory.Attraction,
                _ => (TravelPlaceCategory?)null,
            };

            if (mapped.HasValue) return TravelPlaceCategories.ToWireValue(mapped.Value);
        }

        return TravelPlaceCategories.ToWireValue(TravelPlaceCategory.General);
    }

    private static string? ReadCategoryLabel(JsonElement location, string language)
    {
        if (!location.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var category in categories.EnumerateArray())
        {
            var label = ReadLocalised(category, "names", language) ?? Text(category, "name");
            if (!string.IsNullOrWhiteSpace(label)) return label;
        }

        return null;
    }

    /// The formatted opening-hours strings, when the location carries any.
    private static IReadOnlyList<string> ReadOpeningHours(JsonElement location)
    {
        if (!location.TryGetProperty("opening_hours", out var hours)
            || hours.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        if (!hours.TryGetProperty("formatted", out var formatted)
            || formatted.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return formatted.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(7)
            .ToList();
    }

    /// <summary>
    /// One review snippet, as the citation behind the recommendation.
    ///
    /// Capped hard. This is a card in a chat, and the full review belongs on
    /// Tripadvisor's own page under their review implementation policy.
    /// </summary>
    private static string? ReadReviewSnippet(JsonElement item)
    {
        if (!item.TryGetProperty("review_sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var source in sources.EnumerateArray())
        {
            var text = Text(source, "snippet") ?? Text(source, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;

            return text.Length > 300 ? text[..300] : text;
        }

        return null;
    }

    private static string? ReadUrl(JsonElement location)
    {
        if (!location.TryGetProperty("urls", out var urls)) return null;

        var raw = urls.ValueKind == JsonValueKind.Object
            ? Text(urls, "location") ?? Text(urls, "web")
            : urls.ValueKind == JsonValueKind.Array
                ? urls.EnumerateArray().Select(entry => Text(entry, "url")).FirstOrDefault(url => url != null)
                : null;

        // Only an https Tripadvisor link ever reaches the app.
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? raw
            : null;
    }

    // ── Transport ─────────────────────────────────────────────────────────

    /// <summary>
    /// One POST, with the key in a header and nothing sensitive logged.
    ///
    /// Returns the parsed document or a failure category. Never throws for a
    /// provider problem — a place lookup failing costs the answer its cards,
    /// not its usefulness.
    /// </summary>
    private async Task<(JsonDocument? Document, TerraFailure Failure)> SendAsync(
        string path, string body, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            request.Headers.Accept.ParseAdd("application/json");
            // THE SECRET. Set per request rather than on the client so it is
            // never part of shared client state, and never written anywhere.
            request.Headers.Add("X-API-Key", ApiKey);

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (!response.IsSuccessStatusCode) return (null, Classify(response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);

            return (await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token), TerraFailure.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away — not a provider failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, TerraFailure.Timeout);
        }
        catch (JsonException)
        {
            return (null, TerraFailure.DeserializationFailed);
        }
        catch (HttpRequestException)
        {
            return (null, TerraFailure.Network);
        }
    }

    /// <summary>
    /// HTTP status onto a category somebody can act on.
    ///
    /// 403 is called out separately because Terra returns it for an endpoint
    /// the subscription does not include — which is a portal problem, not an
    /// outage, and looks identical to a bad key without this.
    /// </summary>
    public static TerraFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => TerraFailure.Unauthorized,
        HttpStatusCode.Forbidden => TerraFailure.Forbidden,
        HttpStatusCode.TooManyRequests => TerraFailure.RateLimited,
        HttpStatusCode.PaymentRequired => TerraFailure.QuotaExceeded,
        HttpStatusCode.BadRequest => TerraFailure.InvalidRequest,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => TerraFailure.Timeout,
        _ => TerraFailure.Network,
    };

    // ── Diagnostics ───────────────────────────────────────────────────────

    /// <summary>
    /// Shape only.
    ///
    /// The category is a fixed vocabulary value, the counts are integers, and
    /// the failure is an enum. NEVER the API key, never a header, never the
    /// response body, and never the user's own sentence — the query text sent
    /// upstream is built from a category, but the message that produced it can
    /// contain anything.
    /// </summary>
    private void Log(
        TerraFailure failure, TravelPlaceQuery query, int raw, int mapped, int elapsedMs, int? status)
    {
        _logger.LogInformation(
            "[GLUNO] terra search result={Result} category={Category} raw={Raw} mapped={Mapped} "
            + "status={Status} in {Elapsed}ms",
            failure, query.Category, raw, mapped, status, elapsedMs);

        if (failure is TerraFailure.Unauthorized or TerraFailure.Forbidden
            or TerraFailure.QuotaExceeded or TerraFailure.ProviderContractChanged)
        {
            // The ones that need a person rather than a retry.
            _logger.LogWarning("[GLUNO] terra needs attention result={Result}", failure);
        }
    }

    private static int Elapsed(DateTime startedAt)
        => (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
